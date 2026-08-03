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

    /// <summary>
    /// Returns local mp3 paths covering the whole of <paramref name="text"/>, in order.
    /// The tw-ob endpoint caps one request near 200 chars, which truncates a story
    /// paragraph, so long text is split at sentence boundaries and each chunk is
    /// fetched and cached separately. Chunks that fail to download are skipped, so a
    /// partial read is still possible; an empty list means nothing could be fetched.
    /// </summary>
    public async Task<List<string>> GetAudioPathsAsync(string text, string lang)
    {
        var paths = new List<string>();
        foreach (var chunk in SplitForTts(text ?? ""))
        {
            var path = await GetAudioPathAsync(chunk, lang).ConfigureAwait(false);
            if (path != null) paths.Add(path);
        }
        return paths;
    }

    // Max characters per TTS request. Kept under the endpoint's ~200 char limit so a
    // chunk is never silently truncated mid-word.
    private const int ChunkLimit = 180;

    /// <summary>
    /// Splits text into TTS-sized pieces. Line breaks are hard boundaries — a story
    /// stores one sentence per line, so each chunk stays a whole sentence and its
    /// cached audio is reused when that sentence is played on its own. Lines longer
    /// than the endpoint's limit fall back to splitting at sentence ends, then spaces,
    /// so playback never cuts a word in half.
    /// </summary>
    public static List<string> SplitForTts(string text)
    {
        var chunks = new List<string>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) SplitLine(trimmed, chunks);
        }
        return chunks;
    }

    private static void SplitLine(string text, List<string> chunks)
    {
        while (text.Length > ChunkLimit)
        {
            var window = text[..ChunkLimit];

            // Prefer the last sentence terminator in the window.
            var cut = window.LastIndexOfAny(['.', '!', '?', '…', ';', ':']);
            // Otherwise break at the last space; if there is none, hard-cut.
            if (cut < ChunkLimit / 3) cut = window.LastIndexOf(' ');
            if (cut <= 0) cut = ChunkLimit - 1;

            var piece = text[..(cut + 1)].Trim();
            if (piece.Length > 0) chunks.Add(piece);
            text = text[(cut + 1)..].TrimStart();
        }

        if (text.Length > 0) chunks.Add(text);
    }

    private static string Key(string text, string lang)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(lang + "|" + text)));
}
