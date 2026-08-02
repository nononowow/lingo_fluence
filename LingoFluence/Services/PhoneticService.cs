using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoFluence.Services;

/// <summary>
/// Looks up the IPA phonetic transcription of a German word from the German
/// Wiktionary (the wikitext carries it in a {{Lautschrift|…}} template) and
/// caches each result on disk, so a given word is fetched only once. Uses the
/// system (WinINET) proxy so local proxy tools (Clash/Verge) are honoured,
/// mirroring <see cref="TranslationService"/>. Returns null on failure so callers
/// degrade gracefully (offline, blocked, no entry, etc.).
/// </summary>
public partial class PhoneticService
{
    private static readonly string CacheDir = Path.Combine(DatabaseService.AppDataPath, "phonetic");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = WebRequest.GetSystemWebProxy(),
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // Matches {{Lautschrift|ˈanzaːɡə}} — captures the IPA between the pipe and the closing braces.
    [GeneratedRegex(@"\{\{Lautschrift\|([^}|]+)\}\}")]
    private static partial Regex LautschriftRegex();

    static PhoneticService()
    {
        Directory.CreateDirectory(CacheDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
    }

    /// <summary>
    /// Returns the IPA transcription of <paramref name="word"/> wrapped in brackets
    /// (e.g. "[ˈanzaːɡə]"), cached on first success. Returns null on failure.
    /// </summary>
    public async Task<string?> GetIpaAsync(string word)
    {
        word = (word ?? "").Trim();
        if (string.IsNullOrEmpty(word)) return null;

        // The headword for lookup: strip a trailing plural marker ("die Ansage, -n" → "Ansage")
        // and any leading article, so multi-part answer strings still resolve.
        var lookup = Headword(word);
        if (string.IsNullOrEmpty(lookup)) return null;

        var cache = Path.Combine(CacheDir, Key(lookup) + ".txt");
        if (File.Exists(cache) && new FileInfo(cache).Length > 0)
            return await File.ReadAllTextAsync(cache).ConfigureAwait(false);

        var url = "https://de.wiktionary.org/w/api.php?action=parse&prop=wikitext&format=json"
                + $"&formatversion=2&page={Uri.EscapeDataString(lookup)}";
        try
        {
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var ipa = ParseIpa(json);
            if (string.IsNullOrWhiteSpace(ipa)) return null;
            await File.WriteAllTextAsync(cache, ipa).ConfigureAwait(false);
            return ipa;
        }
        catch
        {
            return null; // network unavailable / endpoint blocked / no entry — caller decides fallback
        }
    }

    private static string? ParseIpa(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("parse", out var parse)) return null;
        if (!parse.TryGetProperty("wikitext", out var wt) || wt.ValueKind != JsonValueKind.String) return null;

        var m = LautschriftRegex().Match(wt.GetString() ?? "");
        if (!m.Success) return null;
        var ipa = m.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(ipa) ? null : $"[{ipa}]";
    }

    /// <summary>Extracts the bare German headword from an answer string like
    /// "die Ansage, -n" or "das Haus" → "Ansage" / "Haus".</summary>
    private static string Headword(string word)
    {
        // Drop anything after a comma (plural/genitive hints).
        var head = word.Split(',')[0].Trim();
        // Drop a leading article if present.
        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] is "der" or "die" or "das")
            return parts[1];
        return head;
    }

    private static string Key(string word)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(word)));
}
