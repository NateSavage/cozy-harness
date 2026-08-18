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

    /// <param name="socketPath">
    /// Filesystem path to the llama-server's unix socket (its `--host *.sock`
    /// flag). The request URI's host is never actually dialed — every connection
    /// goes straight to this path — so it's just a fixed placeholder.
    /// </param>
    public LlamaClient(string socketPath) {
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
            id_slot = slot,
            cache_prompt = true,          // the whole point
            stop = stop ?? new[] { "\n\n---", "<|im_end|>" },
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
