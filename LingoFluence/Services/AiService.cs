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
                ? output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim()
                : "";
        }
        catch { _claudePath = ""; }

        return string.IsNullOrEmpty(_claudePath) ? null : _claudePath;
    }

    // ── Card generation ───────────────────────────────────────────────────────

    public async Task<List<AiCardData>> GenerateCardsAsync(
        string userRequest,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var cacheKey  = CacheKey(userRequest);
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
        var json = await RunClaudeAsync(claudePath, BuildPrompt(userRequest), ct);

        progress?.Report("Parsing response…");
        var cards = ParseCards(json)
                    ?? throw new InvalidOperationException(
                        $"Claude's output could not be parsed as JSON.\n\nRaw:\n{Trim(json, 600)}");

        if (cards.Count == 0)
            throw new InvalidOperationException("Claude returned an empty card list.");

        await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(cards, JsonOpts), Encoding.UTF8, ct);
        progress?.Report($"✓ {cards.Count} cards generated.");
        return cards;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static async Task<string> RunClaudeAsync(
        string claudePath, string prompt, CancellationToken ct)
    {
        ProcessStartInfo psi;

        // npm-installed claude is a .cmd wrapper — needs cmd.exe on Windows
        if (claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            claudePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
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
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
        }
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);

        using var proc   = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start claude.");
        var stdoutTask   = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask   = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"claude exited {proc.ExitCode}: {Trim(stderr, 500)}");
        return stdout.Trim();
    }

    private static string BuildPrompt(string userRequest) => $"""
        You are a German language flashcard generator.
        Return ONLY a JSON array — no markdown fences, no explanation, just raw JSON.

        Each object must have exactly these string fields:
        - "german"     : the German word/phrase (nouns with article, e.g. "der Hund")
        - "english"    : concise English meaning, include key usage notes
        - "grammar"    : grammar note, e.g. "noun: der Hund, Hunde · masculine" or "verb: kaufen, kaufte, hat gekauft"
        - "example_de" : a natural German example sentence
        - "example_en" : English translation of that sentence

        User request: {userRequest}
        """;

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

    private static string CacheKey(string req)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(req.Trim().ToLowerInvariant())));

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
