using System.Diagnostics;

namespace CozyHarness.Storage;

/// <summary>
/// One commit per tick; commit message is the episode summary. This gives the
/// operator `git log --oneline` as the timeline and `git log -p self/model.md`
/// as the self-model diff viewer, with no tooling to build.
///
/// Shells out to git rather than using a library: the agent shares this
/// environment and should be able to run the same commands itself.
/// </summary>
public sealed class GitStore {
    private readonly string _root;
    private readonly string? _mirror;
    private readonly bool _enabled;

    public GitStore(string root, string? mirrorRemote, bool enabled = true) {
        _root = root;
        _mirror = mirrorRemote;
        _enabled = enabled;
    }

    public void EnsureRepo() {
        if (!_enabled) return;
        if (Directory.Exists(Path.Combine(_root, ".git"))) return;
        Run("init");
        Run("config user.name agent");
        Run("config user.email agent@localhost");
        Run("add -A");
        Run("commit -m \"tree: initial layout\" --allow-empty");
    }

    /// <summary>Commit everything changed by a tick. Returns the sha, or null if nothing changed (including when git is disabled).</summary>
    public string? CommitTick(string tickType, string summary) {
        if (!_enabled) return null;
        Run("add -A");
        // `git status` has no --cached flag (that's a `git diff` option) — this
        // used to always fail and read as "nothing changed", so nothing was ever
        // committed. Plain --porcelain reflects the staged state fine.
        var status = Run("status --porcelain");
        if (string.IsNullOrWhiteSpace(status)) return null;

        var msg = $"{tickType}: {Sanitize(summary)}";
        Run($"commit -m {Quote(msg)}");
        return Run("rev-parse HEAD").Trim();
    }

    /// <summary>
    /// Push to a bare repo the agent cannot write to. `rm` is recoverable from git;
    /// a force-push or aggressive gc is not. With the mirror, it can be completely
    /// free with its own tree — which is the point.
    /// </summary>
    public void PushMirror() {
        if (!_enabled || _mirror is null) return;
        try { Run($"push --quiet {_mirror} HEAD"); }
        catch (Exception) { /* mirror unreachable: never let this stop a tick */ }
    }

    public string Log(string args) => _enabled ? Run($"log {args}") : "";

    private static string Sanitize(string s) {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 120 ? s[..117] + "..." : s;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private string Run(string args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git failed to start");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !args.StartsWith("status"))
            throw new InvalidOperationException($"git {args} failed: {stderr}");
        return stdout;
    }
}
