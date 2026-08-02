using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LingoFluence.Services;

/// <summary>
/// Translates short text via Google Translate's free endpoint and caches each
/// result on disk, so a given (text, target-language) pair is fetched only once.
/// Uses the system (WinINET) proxy so local proxy tools (Clash/Verge) are honoured,
/// mirroring <see cref="TtsService"/>. Returns null on failure so callers degrade
/// gracefully (offline, blocked, etc.).
/// </summary>
public class TranslationService
{
    private static readonly string CacheDir = Path.Combine(DatabaseService.AppDataPath, "translate");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = WebRequest.GetSystemWebProxy(),
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    static TranslationService()
    {
        Directory.CreateDirectory(CacheDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
    }

    /// <summary>
    /// Translates <paramref name="text"/> into <paramref name="target"/> (e.g. "zh-CN"),
    /// from <paramref name="source"/> (default "de"). Cached on first success.
    /// Returns null on failure.
    /// </summary>
    public async Task<string?> TranslateAsync(string text, string target = "zh-CN", string source = "de")
    {
        text = (text ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > 500) text = text[..500];

        var cache = Path.Combine(CacheDir, Key(text, source, target) + ".txt");
        if (File.Exists(cache) && new FileInfo(cache).Length > 0)
            return await File.ReadAllTextAsync(cache).ConfigureAwait(false);

        // Public "single request" endpoint returns nested JSON arrays; sentences sit
        // in result[0], each as [translatedChunk, sourceChunk, ...].
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t"
                + $"&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}"
                + $"&q={Uri.EscapeDataString(text)}";
        try
        {
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var translated = ParseTranslation(json);
            if (string.IsNullOrWhiteSpace(translated)) return null;
            await File.WriteAllTextAsync(cache, translated).ConfigureAwait(false);
            return translated;
        }
        catch
        {
            return null; // network unavailable / endpoint blocked — caller decides fallback
        }
    }

    private static string? ParseTranslation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var sentences = root[0];
        if (sentences.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var chunk in sentences.EnumerateArray())
            if (chunk.ValueKind == JsonValueKind.Array && chunk.GetArrayLength() > 0
                && chunk[0].ValueKind == JsonValueKind.String)
                sb.Append(chunk[0].GetString());
        return sb.ToString().Trim();
    }

    private static string Key(string text, string source, string target)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(source + "|" + target + "|" + text)));
}
