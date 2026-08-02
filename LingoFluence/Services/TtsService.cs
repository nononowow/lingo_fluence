using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LingoFluence.Services;

/// <summary>
/// Fetches word/phrase-level speech audio from Google Translate's public TTS
/// endpoint and caches the resulting mp3 locally, so each phrase is downloaded
/// only once. Uses the system (WinINET) proxy by default, so local proxy tools
/// like Clash/Verge are honoured automatically.
/// </summary>
public class TtsService
{
    private static readonly string CacheDir = Path.Combine(DatabaseService.AppDataPath, "tts");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = true,                          // honour the system proxy (Clash/Verge etc.)
        Proxy = WebRequest.GetSystemWebProxy(),
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    static TtsService()
    {
        Directory.CreateDirectory(CacheDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
    }

    /// <summary>
    /// Returns a local mp3 path for <paramref name="text"/> spoken in <paramref name="lang"/>
    /// (e.g. "de", "en"), downloading and caching on first use. Returns null on failure
    /// (offline, blocked, etc.) so callers can degrade gracefully.
    /// </summary>
    public async Task<string?> GetAudioPathAsync(string text, string lang)
    {
        text = (text ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return null;
        // Google's tw-ob endpoint caps a single request near 200 chars.
        if (text.Length > 200) text = text[..200];

        var cache = Path.Combine(CacheDir, Key(text, lang) + ".mp3");
        if (File.Exists(cache) && new FileInfo(cache).Length > 0) return cache;

        var url = "https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob"
                + $"&tl={Uri.EscapeDataString(lang)}&q={Uri.EscapeDataString(text)}";
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(cache, bytes).ConfigureAwait(false);
            return cache;
        }
        catch
        {
            return null; // network unavailable / endpoint blocked — caller decides fallback
        }
    }

    private static string Key(string text, string lang)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(lang + "|" + text)));
}
