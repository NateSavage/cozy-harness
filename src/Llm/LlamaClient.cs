using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CozyHarness.Llm;

/// <summary>
/// Talks to a persistent llama-server over its Unix domain socket rather than TCP
/// loopback — the traffic never leaves the box, so there's no point paying for a
/// TCP handshake, Nagle/delayed-ACK, or a port-table entry on every request.
///
/// The important part is `id_slot` + `cache_prompt`: each tick type owns a slot
/// whose KV cache stays warm between ticks, so only the delta is prefilled.
///
/// This makes context ORDER load-bearing. One edited token invalidates the cache
/// from that point forward, so stable material must come first. See PromptParts.
/// </summary>
public sealed class LlamaClient : IDisposable {
    private readonly HttpClient _http;
    private readonly double _topP;
    private readonly int _topK;
    private readonly IReadOnlyList<string> _stop;

    /// <param name="socketPath">
    /// Filesystem path to the llama-server's unix socket (its `--host *.sock`
    /// flag). The request URI's host is never actually dialed — every connection
    /// goes straight to this path — so it's just a fixed placeholder.
    /// </param>
    /// <param name="topP">
    /// Nucleus sampling threshold sent on every request — a client-level sampling
    /// policy (Google's published Gemma 4 default), not a per-call choice the way
    /// temperature is. See AgentConfig.LlmConfig.TopP; this is where it lands.
    /// </param>
    /// <param name="topK">
    /// Top-k cutoff sent on every request. llama-server's own built-in default
    /// (40) predates Gemma 4 and doesn't match what the model was tuned/evaluated
    /// against, so this is sent explicitly rather than relying on the server.
    /// </param>
    /// <param name="stop">
    /// Default stop strings, overridable per call. We talk to llama.cpp's raw
    /// /completion endpoint, never /v1/chat/completions, so nothing ever renders
    /// Gemma's <c>&lt;start_of_turn&gt;</c>/<c>&lt;end_of_turn&gt;</c> template —
    /// this is document completion, not chat. <c>&lt;end_of_turn&gt;</c> (Gemma's
    /// real turn-end token, id 106) is still worth catching since an IT-tuned
    /// model can emit it unprompted when it "feels" done even without being
    /// given the template. Proper turn wrapping is deferred until conversation
    /// boundaries are tracked — see DESIGN.md §9.
    /// </param>
    public LlamaClient(string socketPath, double topP = 0.95, int topK = 64, IReadOnlyList<string>? stop = null) {
        _topP = topP;
        _topK = topK;
        _stop = stop ?? new[] { "\n\n---", "<end_of_turn>" };

        var handler = new SocketsHttpHandler {
            ConnectCallback = async (_, ct) => {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                } catch {
                    socket.Dispose();
                    throw;
                }
            },
        };

        _http = new HttpClient(handler) {
            BaseAddress = new Uri("http://llama.sock"),
            Timeout = TimeSpan.FromMinutes(30),   // CPU inference is slow; that is fine
        };
    }

    public async Task<LlmResult> CompleteAsync(
        string prompt, int slot, int maxTokens, double temperature = 0.7,
        IReadOnlyList<string>? stop = null, CancellationToken ct = default)
    {
        var body = new
        {
            prompt,
            n_predict = maxTokens,
            temperature,
            top_p = _topP,
            top_k = _topK,
            id_slot = slot,
            cache_prompt = true,          // the whole point
            stop = stop ?? _stop,
        };

        using var resp = await _http.PostAsJsonAsync("/completion", body, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<CompletionResponse>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("empty completion response");

        return new LlmResult(json.Content ?? "", json.TokensEvaluated + json.TokensPredicted);
    }

    /// <summary>
    /// Ask for JSON and parse it. Small models fence their output constantly, so
    /// strip fences before parsing and return null rather than throwing — a tick
    /// that gets garbage should degrade, not crash the loop.
    /// </summary>
    public async Task<T?> CompleteJsonAsync<T>(
        string prompt, int slot, int maxTokens, CancellationToken ct = default) where T : class
    {
        var r = await CompleteAsync(prompt, slot, maxTokens, temperature: 0.3, ct: ct);
        var text = r.Text.Trim();

        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) text = text[(start + 1)..end].Trim();
        }

        var brace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (brace >= 0 && lastBrace > brace) text = text[brace..(lastBrace + 1)];

        try { return JsonSerializer.Deserialize<T>(text, JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Polls /health until llama-server answers, or ct is cancelled. Meant to
    /// run once at startup, AFTER the process has already been reported
    /// "started" to systemd (see Program.cs) — CPU model load under --mlock
    /// can take minutes, and that has no business blocking systemd's own
    /// startup timeout. TickRunner already treats a failed tick as just
    /// another recorded outcome, never a crash; this exists purely so the
    /// first few ticks after a cold boot don't spend themselves on a
    /// foregone-conclusion connection failure.
    /// </summary>
    public async Task WaitForHealthyAsync(string label, CancellationToken ct) {
        var attempt = 0;
        while (true) {
            try {
                using var resp = await _http.GetAsync("/health", ct);
                if (resp.IsSuccessStatusCode) return;
            } catch { /* socket not there yet, or server still loading — keep polling */ }

            if (attempt > 0 && attempt % 15 == 0)   // ~every 30s at a 2s poll interval
                Console.WriteLine($"[llm] still waiting for {label} to become healthy...");
            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public void Dispose() => _http.Dispose();

    private sealed class CompletionResponse
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tokens_evaluated")] public int TokensEvaluated { get; set; }
        [JsonPropertyName("tokens_predicted")] public int TokensPredicted { get; set; }
    }
}

public readonly record struct LlmResult(string Text, int Tokens);
