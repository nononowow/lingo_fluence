using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LingoFluence.Models;

namespace LingoFluence.Services;

/// <summary>
/// Wraps the claude CLI to generate German vocabulary flashcards.
/// Results are cached by a MD5 hash of the user request so repeated
/// requests for the same topic load instantly.
/// </summary>
public class AiService
{
    private static readonly string CacheDir =
        Path.Combine(DatabaseService.AppDataPath, "ai_cache");

    // null = not yet checked, "" = checked and not found, else = path
    private static string? _claudePath;
    private static bool    _checked;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    static AiService() => Directory.CreateDirectory(CacheDir);

    // ── CLI detection ─────────────────────────────────────────────────────────

    public static async Task<string?> FindClaudeAsync()
    {
        if (_checked) return string.IsNullOrEmpty(_claudePath) ? null : _claudePath;
        _checked = true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("claude");

            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            _claudePath = proc.ExitCode == 0
                ? PickExecutable(output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                : "";
        }
        catch { _claudePath = ""; }

        return string.IsNullOrEmpty(_claudePath) ? null : _claudePath;
    }

    /// <summary>
    /// where.exe can return several matches for "claude". npm installs both an
    /// extensionless Unix shell script and a Windows wrapper (claude.cmd); only
    /// the wrapper (or a real .exe) can be launched on Windows. Prefer an
    /// executable variant, and if only the extensionless script is found, probe
    /// for a sibling .cmd/.exe/.bat on disk.
    /// </summary>
    private static string PickExecutable(string[] candidates)
    {
        var paths = candidates.Select(c => c.Trim())
                              .Where(c => c.Length > 0)
                              .ToArray();
        if (paths.Length == 0) return "";

        // Priority order of extensions Windows can actually start.
        string[] preferred = { ".exe", ".cmd", ".bat" };
        foreach (var ext in preferred)
        {
            var hit = paths.FirstOrDefault(
                p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        // Only an extensionless match (e.g. the npm shell script) — look for a
        // launchable sibling next to it before giving up.
        var first = paths[0];
        foreach (var ext in preferred)
        {
            var sibling = first + ext;
            if (File.Exists(sibling)) return sibling;
        }

        return first;
    }

    // ── Card generation ───────────────────────────────────────────────────────

    /// <summary>
    /// Generates (or refines) a flashcard deck from a full conversation transcript.
    /// The entire conversation is sent on every turn and Claude is asked to return
    /// the COMPLETE updated deck, so one conversation always maps to exactly one
    /// card set — continuing to chat refines the same deck rather than producing a
    /// disjoint second one. Cached by a hash of the whole conversation.
    /// </summary>
    public async Task<List<AiCardData>> GenerateFromConversationAsync(
        IReadOnlyList<AiConversationTurn> conversation,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var cacheKey  = ConversationCacheKey(conversation);
        var cacheFile = Path.Combine(CacheDir, cacheKey + ".json");

        if (File.Exists(cacheFile))
        {
            progress?.Report("Loading from cache…");
            var cached = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8, ct);
            return JsonSerializer.Deserialize<List<AiCardData>>(cached, JsonOpts)
                   ?? throw new InvalidOperationException("Cache file is corrupt.");
        }

        var claudePath = await FindClaudeAsync()
                         ?? throw new InvalidOperationException(
                             "claude CLI not found. Install it (npm i -g @anthropic-ai/claude-code) and restart.");

        progress?.Report("Asking Claude to generate flashcards…");
        var json = await RunClaudeAsync(claudePath, BuildConversationPrompt(conversation), ct);

        progress?.Report("Parsing response…");
        var cards = ParseCards(json)
                    ?? throw new InvalidOperationException(
                        $"Claude's output could not be parsed as JSON.\n\nRaw:\n{Trim(json, 600)}");

        if (cards.Count == 0)
            throw new InvalidOperationException("Claude returned an empty card list.");

        await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(cards, JsonOpts), Encoding.UTF8, ct);
        progress?.Report($"✓ {cards.Count} cards ready.");
        return cards;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static async Task<string> RunClaudeAsync(
        string claudePath, string prompt, CancellationToken ct)
    {
        // The prompt is multi-line. Passing it as a command-line argument breaks
        // under cmd.exe (newlines terminate the command), so we write it to the
        // process's stdin instead and invoke `claude -p` with no inline value —
        // claude reads the prompt from stdin. This also avoids all shell quoting.
        ProcessStartInfo psi;

        // npm-installed claude is a .cmd wrapper — needs cmd.exe on Windows
        if (claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            claudePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(claudePath);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = claudePath,
                UseShellExecute = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
        }
        psi.ArgumentList.Add("-p");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start claude.");

        // Feed the prompt via stdin, then close it so claude knows input is done.
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();

        var stdoutTask   = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask   = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"claude exited {proc.ExitCode}: {Trim(stderr, 500)}");
        return stdout.Trim();
    }

    private static string BuildConversationPrompt(IReadOnlyList<AiConversationTurn> conversation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a German language flashcard generator.");
        sb.AppendLine("Return ONLY a JSON array — no markdown fences, no explanation, just raw JSON.");
        sb.AppendLine();
        sb.AppendLine("Each object must have exactly these string fields:");
        sb.AppendLine("- \"german\"     : the German word/phrase (nouns with article, e.g. \"der Hund\")");
        sb.AppendLine("- \"english\"    : concise English meaning, include key usage notes");
        sb.AppendLine("- \"grammar\"    : grammar note, e.g. \"noun: der Hund, Hunde · masculine\" or \"verb: kaufen, kaufte, hat gekauft\"");
        sb.AppendLine("- \"example_de\" : a natural German example sentence");
        sb.AppendLine("- \"example_en\" : English translation of that sentence");
        sb.AppendLine();
        sb.AppendLine("Below is the conversation so far between the user and you. Treat it as ongoing:");
        sb.AppendLine("apply every instruction in order and return the COMPLETE updated deck that");
        sb.AppendLine("reflects all requests — not just the newest one. If the latest message asks to");
        sb.AppendLine("add, remove, or change cards, produce the full resulting list.");
        sb.AppendLine();
        sb.AppendLine("=== CONVERSATION ===");
        foreach (var turn in conversation)
        {
            var who = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "ASSISTANT" : "USER";
            sb.AppendLine($"[{who}] {turn.Text}");
        }
        sb.AppendLine("=== END CONVERSATION ===");
        sb.AppendLine();
        sb.AppendLine("Now output the complete JSON array for the current deck.");
        return sb.ToString();
    }

    private static List<AiCardData>? ParseCards(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var nl  = raw.IndexOf('\n');
            var end = raw.LastIndexOf("```");
            if (nl >= 0 && end > nl) raw = raw[(nl + 1)..end].Trim();
        }
        try { return JsonSerializer.Deserialize<List<AiCardData>>(raw, JsonOpts); }
        catch { return null; }
    }

    private static string ConversationCacheKey(IReadOnlyList<AiConversationTurn> conversation)
    {
        var joined = string.Join("\n", conversation.Select(t => $"{t.Role}:{t.Text.Trim()}"))
                           .ToLowerInvariant();
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
