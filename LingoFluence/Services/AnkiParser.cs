using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using LingoFluence.Models;

namespace LingoFluence.Services;

/// <summary>
/// Parses Anki .apkg packages and extracts card data and media files.
/// </summary>
public partial class AnkiParser
{
    [GeneratedRegex(@"\[sound:([^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex SoundRx();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlRx();

    public class ImportResult
    {
        public string DeckName { get; init; } = "Imported Deck";
        public List<NoteCardRow> Rows { get; init; } = new();
    }

    public record NoteCardRow(
        long      AnkiNoteId,
        string    Answer,
        string    Context,
        string?   AudioFile,
        long      AnkiCardId,
        DateTime  DueDate,
        int       Interval,
        double    Ease,
        int       Reps,
        int       Lapses,
        CardState State,
        string    SentenceDe,
        string    WordEn,
        string    SentenceEn);

    // ─── Public entry point ──────────────────────────────────────────────────

    public ImportResult Parse(string apkgPath, string mediaOutputFolder)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ba_" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(apkgPath, tempDir);
            return ParseExtracted(tempDir, apkgPath, mediaOutputFolder);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    // ─── Internal pipeline ───────────────────────────────────────────────────

    private ImportResult ParseExtracted(string tempDir, string apkgPath, string mediaOutputFolder)
    {
        var dbPath = Path.Combine(tempDir, "collection.anki21");
        if (!File.Exists(dbPath))
            dbPath = Path.Combine(tempDir, "collection.anki2");
        if (!File.Exists(dbPath))
            throw new InvalidOperationException("No Anki collection database found in the package.");

        Directory.CreateDirectory(mediaOutputFolder);
        var mediaMap = ExtractMedia(tempDir, mediaOutputFolder);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        var crt         = GetLong(conn, "SELECT crt FROM col LIMIT 1");
        var deckName    = ReadDeckName(conn, apkgPath);
        var modelFields = ReadModelFields(conn);
        var rows        = ReadRows(conn, crt, modelFields, mediaMap, mediaOutputFolder);

        return new ImportResult { DeckName = deckName, Rows = rows };
    }

    // Copy numbered media files to output folder; return realName→fullPath map
    private static Dictionary<string, string> ExtractMedia(string tempDir, string outDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mediaFile = Path.Combine(tempDir, "media");
        if (!File.Exists(mediaFile)) return map;

        using var doc = JsonDocument.Parse(File.ReadAllText(mediaFile));
        foreach (var kv in doc.RootElement.EnumerateObject())
        {
            var realName = kv.Value.GetString();
            if (string.IsNullOrEmpty(realName)) continue;
            var src  = Path.Combine(tempDir, kv.Name);
            var dest = Path.Combine(outDir, realName);
            if (File.Exists(src))
                File.Copy(src, dest, overwrite: true);
            map[realName] = dest;
        }
        return map;
    }

    private static long GetLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try { return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); } catch { return 0L; }
    }

    private static string ReadDeckName(SqliteConnection conn, string apkgPath)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT decks FROM col LIMIT 1";
            var json = cmd.ExecuteScalar()?.ToString();
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var kv in doc.RootElement.EnumerateObject())
                {
                    if (kv.Value.TryGetProperty("name", out var n))
                    {
                        var name = n.GetString() ?? "";
                        if (!string.IsNullOrEmpty(name) && name != "Default")
                            return name;
                    }
                }
            }
        }
        catch { /* fallback */ }
        return Path.GetFileNameWithoutExtension(apkgPath);
    }

    // Returns modelId → ordered list of field names
    private static Dictionary<long, string[]> ReadModelFields(SqliteConnection conn)
    {
        var result = new Dictionary<long, string[]>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT models FROM col LIMIT 1";
            var json = cmd.ExecuteScalar()?.ToString();
            if (json == null) return result;
            using var doc = JsonDocument.Parse(json);
            foreach (var model in doc.RootElement.EnumerateObject())
            {
                if (!long.TryParse(model.Name, out var mid)) continue;
                if (!model.Value.TryGetProperty("flds", out var fldsEl)) continue;
                var names = fldsEl.EnumerateArray()
                    .Select(f => f.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "")
                    .ToArray();
                result[mid] = names;
            }
        }
        catch { /* ignore parse errors */ }
        return result;
    }

    private List<NoteCardRow> ReadRows(
        SqliteConnection conn, long crt,
        Dictionary<long, string[]> modelFields,
        Dictionary<string, string> mediaMap,
        string mediaFolder)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT n.id, n.mid, n.flds,
                   c.id, c.type, c.due, c.ivl, c.factor, c.reps, c.lapses
            FROM notes n
            JOIN cards c ON c.nid = n.id
            WHERE c.queue >= 0
            ORDER BY c.type ASC, c.due ASC";

        var rows = new List<NoteCardRow>();
        var seenNotes = new HashSet<long>();

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long noteId = r.GetInt64(0);
            if (!seenNotes.Add(noteId)) continue; // one card per note

            long   mid    = r.GetInt64(1);
            var    flds   = r.GetString(2);
            long   cardId = r.GetInt64(3);
            int    ctype  = r.GetInt32(4);
            long   due    = r.GetInt64(5);
            int    ivl    = r.GetInt32(6);
            int    factor = r.GetInt32(7);
            int    reps   = r.GetInt32(8);
            int    lapses = r.GetInt32(9);

            var parts = flds.Split('\x1f');

            // Extract audio from any field
            string? audioFile = null;
            foreach (var p in parts)
            {
                var m = SoundRx().Match(p);
                if (!m.Success) continue;
                var fn = m.Groups[1].Value.Trim();
                audioFile = mediaMap.TryGetValue(fn, out var fp) ? fp
                          : Path.Combine(mediaFolder, fn);
                break;
            }

            var names = modelFields.TryGetValue(mid, out var fnames) ? fnames : Array.Empty<string>();
            int answerIdx  = PickGermanField(names, parts);
            int contextIdx = PickContextField(names, parts, answerIdx);

            var answer  = Strip(answerIdx  >= 0 && answerIdx  < parts.Length ? parts[answerIdx]  : "");
            var context = Strip(contextIdx >= 0 && contextIdx < parts.Length ? parts[contextIdx] : "");
            if (string.IsNullOrWhiteSpace(answer)) continue;

            // Richer fields for the copyable details panel. The audio matches the
            // German sentence, so surfacing it explains the long single-word audio.
            var sentenceDe = PickFieldByNames(names, parts,
                new[] { "de_sentence", "sample sentence", "example", "beispiel", "satz" }, answerIdx, contextIdx);
            var wordEn = PickFieldByNames(names, parts,
                new[] { "en_word", "english", "translation", "übersetzung", "meaning", "back" }, answerIdx, -1);
            var sentenceEn = PickFieldByNames(names, parts,
                new[] { "en_sentence", "en example", "english sentence" }, answerIdx, contextIdx);

            var dueDate  = CalcDue(crt, ctype, due);
            var ease     = factor > 0 ? factor / 1000.0 : 2.5;
            var interval = ivl < 0 ? 0 : ivl;
            var state    = ctype == 0 ? CardState.New
                         : ctype == 2 ? CardState.Review
                         : CardState.Learning;

            rows.Add(new NoteCardRow(
                noteId, answer, context, audioFile,
                cardId, dueDate, interval, ease, reps, lapses, state,
                sentenceDe, wordEn, sentenceEn));
        }
        return rows;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static DateTime CalcDue(long crt, int cardType, long due)
    {
        return cardType switch
        {
            0     => DateTime.Today,
            1or 3 => due > 0
                       ? DateTimeOffset.FromUnixTimeSeconds(due).LocalDateTime
                       : DateTime.Now,
            _     => DateTimeOffset.FromUnixTimeSeconds(crt)
                       .LocalDateTime.Date.AddDays(due)
        };
    }

    private static string Strip(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = SoundRx().Replace(html, "");
        html = HtmlRx().Replace(html, "");
        html = html
            .Replace("&amp;",  "&") .Replace("&lt;",   "<") .Replace("&gt;",   ">")
            .Replace("&nbsp;", " ") .Replace("&uuml;", "ü") .Replace("&ouml;", "ö")
            .Replace("&auml;", "ä") .Replace("&Uuml;", "Ü") .Replace("&Ouml;", "Ö")
            .Replace("&Auml;", "Ä") .Replace("&szlig;", "ß").Replace("&#39;", "'")
            .Replace("&quot;", "\"");
        return html.Trim();
    }

    // Field names that hold the German word, in priority order.
    private static readonly string[] GermanNames =
        { "german", "de_word", "wort", "word", "front", "vokabel", "deutsch", "target", "expression" };

    // Field names that never hold the answer (ids, media, meta).
    private static bool IsMetaField(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("id") || n.Contains("audio") || n.Contains("sound")
            || n.Contains("picture") || n.Contains("image") || n.Contains("note")
            || n.Contains("level") || n.Contains("thing") || n.Contains("attribution")
            || n.Contains("comment") || n.Contains("part of speech") || n.Contains("tag");
    }

    // Choose the field holding the German word by name, then by content heuristics.
    private static int PickGermanField(string[] names, string[] parts)
    {
        // 1. Exact-ish match on a known German field name (must have real text).
        for (int pass = 0; pass < GermanNames.Length; pass++)
        {
            for (int i = 0; i < names.Length && i < parts.Length; i++)
            {
                if (names[i].Trim().ToLowerInvariant() == GermanNames[pass]
                    && LooksLikeWord(parts[i]))
                    return i;
            }
        }
        // 2. First non-meta field that looks like a word (not a bare number / media).
        for (int i = 0; i < parts.Length; i++)
        {
            var nm = i < names.Length ? names[i] : "";
            if (!IsMetaField(nm) && LooksLikeWord(parts[i]))
                return i;
        }
        // 3. First field that looks like a word at all.
        for (int i = 0; i < parts.Length; i++)
            if (LooksLikeWord(parts[i])) return i;
        return 0;
    }

    // Choose a context/definition field (English/sentence), avoiding the answer and meta.
    private static int PickContextField(string[] names, string[] parts, int answerIdx)
    {
        string[] pref = { "english", "en_word", "übersetzung", "translation",
                          "meaning", "back", "de_sentence", "sample sentence", "en_sentence" };
        foreach (var want in pref)
            for (int i = 0; i < names.Length && i < parts.Length; i++)
                if (i != answerIdx && names[i].Trim().ToLowerInvariant() == want
                    && !string.IsNullOrWhiteSpace(Strip(parts[i])))
                    return i;

        for (int i = 0; i < parts.Length; i++)
        {
            if (i == answerIdx) continue;
            var nm = i < names.Length ? names[i] : "";
            if (!IsMetaField(nm) && !string.IsNullOrWhiteSpace(Strip(parts[i])))
                return i;
        }
        return -1;
    }

    // Return the stripped value of the first field whose name matches one of the
    // wanted names (in priority order) and has real text, skipping excluded indices.
    // Returns "" when no such field exists (Basic decks lack these fields).
    private static string PickFieldByNames(string[] names, string[] parts,
        string[] wanted, int exclude1, int exclude2)
    {
        foreach (var want in wanted)
            for (int i = 0; i < names.Length && i < parts.Length; i++)
            {
                if (i == exclude1 || i == exclude2) continue;
                if (names[i].Trim().ToLowerInvariant() == want)
                {
                    var v = Strip(parts[i]);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
        return "";
    }

    // A word/phrase: has at least one letter and isn't purely digits/punctuation/media.
    private static bool LooksLikeWord(string raw)
    {
        var s = Strip(raw);
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Any(char.IsLetter);
    }
}
