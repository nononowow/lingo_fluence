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
    /// Generates or grows a flashcard deck from the conversation transcript plus the
    /// deck built so far. Claude is asked for a BATCH of NEW cards not already in the
    /// deck; the result is merged with <paramref name="existingCards"/> (dedup by the
    /// German term). This lets a deck scale to hundreds of cards over several turns —
    /// each response stays within the model's output limit, and continuing the chat
    /// accumulates instead of re-generating a small deck from scratch.
    /// </summary>
    public async Task<List<AiCardData>> GenerateFromConversationAsync(
        IReadOnlyList<AiConversationTurn> conversation,
        IReadOnlyList<AiCardData> existingCards,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // Reading-story requests ("10 German short stories at A1 with sentence-by-sentence
        // breakdown") need a different response shape than plain vocabulary cards, so we
        // ask for structured stories and flatten them into the same card rows.
        var storyMode = IsStoryRequest(conversation);

        var cacheKey  = ConversationCacheKey(conversation, existingCards, storyMode);
        var cacheFile = Path.Combine(CacheDir, cacheKey + ".json");

        List<AiCardData>? batch = null;
        if (File.Exists(cacheFile))
        {
            progress?.Report("Loading from cache…");
            var cached = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8, ct);
            batch = JsonSerializer.Deserialize<List<AiCardData>>(cached, JsonOpts);
        }

        if (batch == null)
        {
            var claudePath = await FindClaudeAsync()
                             ?? throw new InvalidOperationException(
                                 "claude CLI not found. Install it (npm i -g @anthropic-ai/claude-code) and restart.");

            progress?.Report(storyMode
                ? $"Asking Claude for up to {MaxStoryBatch} stories with full breakdowns…"
                : "Asking Claude to generate flashcards…");
            var prompt = storyMode
                ? BuildStoryPrompt(conversation, existingCards)
                : BuildConversationPrompt(conversation, existingCards);
            var json = await RunClaudeAsync(claudePath, prompt, ct);

            progress?.Report("Parsing response…");
            if (storyMode)
            {
                var stories = ParseStories(json)
                              ?? throw new InvalidOperationException(
                                  $"Claude's story output could not be parsed as JSON.\n\nRaw:\n{Trim(json, 600)}");
                // One card per story: grading it moves to the next story, and the full
                // breakdown travels inside that card (see StoryToCards).
                batch = stories.SelectMany(StoryToCards).ToList();
                progress?.Report($"Built {batch.Count} story cards.");
            }
            else
            {
                batch = ParseCards(json)
                        ?? throw new InvalidOperationException(
                            $"Claude's output could not be parsed as JSON.\n\nRaw:\n{Trim(json, 600)}");
            }

            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(batch, JsonOpts), Encoding.UTF8, ct);
        }

        var merged = MergeDecks(existingCards, batch);
        if (merged.Count == 0)
            throw new InvalidOperationException("Claude returned an empty card list.");

        var added = merged.Count - existingCards.Count;
        progress?.Report(added > 0
            ? $"✓ Added {added} new cards ({merged.Count} total)."
            : $"✓ No new cards this turn ({merged.Count} total). Try a more specific instruction.");
        return merged;
    }

    /// <summary>
    /// Merges a freshly generated batch into the existing deck, keeping order and
    /// dropping cards whose German term already exists (case/whitespace-insensitive).
    /// </summary>
    private static List<AiCardData> MergeDecks(
        IReadOnlyList<AiCardData> existing, IReadOnlyList<AiCardData> batch)
    {
        var result = new List<AiCardData>(existing);
        var seen   = new HashSet<string>(
            existing.Select(c => NormalizeTerm(c.German)), StringComparer.Ordinal);

        foreach (var card in batch)
        {
            if (string.IsNullOrWhiteSpace(card.German)) continue;
            if (seen.Add(NormalizeTerm(card.German)))
                result.Add(card);
        }
        return result;
    }

    private static string NormalizeTerm(string german) =>
        string.Join(' ', (german ?? "").Trim().ToLowerInvariant()
                                        .Split(' ', StringSplitOptions.RemoveEmptyEntries));

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

    // Cap on how many cards we ask for in a single response. Large "complete deck"
    // requests exceed the model's output limit and silently truncate, so we grow the
    // deck in batches instead — the user continues the chat to reach large targets.
    private const int MaxBatch = 60;

    private static string BuildConversationPrompt(
        IReadOnlyList<AiConversationTurn> conversation,
        IReadOnlyList<AiCardData> existingCards)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a German language flashcard generator.");
        sb.AppendLine("Return ONLY a JSON array — no markdown fences, no explanation, just raw JSON.");
        sb.AppendLine();
        sb.AppendLine("Each object must have exactly these string fields:");
        sb.AppendLine("- \"german\"     : the German word/phrase (nouns with article, e.g. \"der Hund\")");
        sb.AppendLine("- \"english\"    : concise English meaning, include key usage notes");
        sb.AppendLine("- \"chinese\"    : concise Chinese (简体中文) meaning of the word");
        sb.AppendLine("- \"grammar\"    : grammar note, e.g. \"noun: der Hund, Hunde · masculine\" or \"verb: kaufen, kaufte, hat gekauft\"");
        sb.AppendLine("- \"example_de\" : a natural German example sentence");
        sb.AppendLine("- \"example_en\" : English translation of that sentence");
        sb.AppendLine();
        sb.AppendLine($"Return a batch of AT MOST {MaxBatch} NEW cards that are NOT already in the deck below.");
        sb.AppendLine("Do NOT repeat any German term that already exists in the current deck.");
        sb.AppendLine("If the user asked for a large deck (e.g. \"top 300\", \"500 words\"), just return the");
        sb.AppendLine("next batch toward that goal — the user will continue the chat to request more.");
        sb.AppendLine("Follow every instruction in the conversation, focusing on the latest message.");
        sb.AppendLine();

        sb.AppendLine("=== CONVERSATION ===");
        foreach (var turn in conversation)
        {
            var who = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "ASSISTANT" : "USER";
            sb.AppendLine($"[{who}] {turn.Text}");
        }
        sb.AppendLine("=== END CONVERSATION ===");
        sb.AppendLine();

        sb.AppendLine($"=== CURRENT DECK ({existingCards.Count} cards already made — do NOT repeat these German terms) ===");
        if (existingCards.Count == 0)
        {
            sb.AppendLine("(empty — this is the first batch)");
        }
        else
        {
            // Send only the German terms: enough to dedup, cheap on tokens so the deck
            // can grow large without the prompt ballooning.
            foreach (var c in existingCards)
                sb.AppendLine($"- {c.German}");
        }
        sb.AppendLine("=== END CURRENT DECK ===");
        sb.AppendLine();
        sb.AppendLine($"Now output a JSON array of up to {MaxBatch} NEW cards to add to this deck.");
        return sb.ToString();
    }

    // ── Story mode ────────────────────────────────────────────────────────────

    // A story carries a whole breakdown (every sentence, every word, every phrase),
    // so far fewer fit in one response than plain vocabulary cards. The user keeps
    // pressing More/continue to reach a larger target.
    private const int MaxStoryBatch = 3;

    private static readonly string[] StoryKeywords =
    {
        "story", "stories", "short story", "text", "texts", "passage", "passages",
        "reading", "dialogue", "dialog", "article", "narrative",
        "故事", "短文", "阅读", "文章", "对话",
        "geschichte", "geschichten", "kurzgeschichte", "lesetext"
    };

    /// <summary>
    /// True when the latest user instruction asks for reading texts rather than
    /// vocabulary cards. Only the most recent user turn is examined so a user can
    /// switch between the two modes mid-conversation.
    /// </summary>
    private static bool IsStoryRequest(IReadOnlyList<AiConversationTurn> conversation)
    {
        var latest = conversation.LastOrDefault(
            t => t.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        if (latest == null) return false;

        var text = latest.Text.ToLowerInvariant();

        // "more"/"continue" carries no topic of its own — inherit the mode of the
        // previous real instruction so continuing a story deck keeps making stories.
        if (IsContinuation(text))
        {
            var prior = conversation
                .Where(t => t.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                .Reverse().Skip(1)
                .FirstOrDefault(t => !IsContinuation(t.Text.ToLowerInvariant()));
            if (prior == null) return false;
            text = prior.Text.ToLowerInvariant();
        }

        return StoryKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));
    }

    private static bool IsContinuation(string lowerText) =>
        lowerText.Contains("add more cards toward the goal", StringComparison.Ordinal) ||
        lowerText.Trim() is "more" or "continue" or "继续" or "更多";

    private static string BuildStoryPrompt(
        IReadOnlyList<AiConversationTurn> conversation,
        IReadOnlyList<AiCardData> existingCards)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a German reading-comprehension material generator.");
        sb.AppendLine("Return ONLY a JSON array of story objects — no markdown fences, no prose, just raw JSON.");
        sb.AppendLine();
        sb.AppendLine("Each story object has these fields:");
        sb.AppendLine("- \"title\"   : short German title of the story");
        sb.AppendLine("- \"title_en\": the title in English");
        sb.AppendLine("- \"title_zh\": the title in Chinese (中文标题)");
        sb.AppendLine("- \"level\"  : CEFR level, e.g. \"A1\"");
        sb.AppendLine("- \"text\"   : the FULL German story text. Use standard punctuation and normal");
        sb.AppendLine("             spacing only (no markdown, no line breaks inside the text, no");
        sb.AppendLine("             bullet characters) so a text-to-speech engine reads it smoothly.");
        sb.AppendLine("- \"sentences\" : array covering EVERY sentence of the text, in order, each with:");
        sb.AppendLine("    \"de\"           : the German sentence, exactly as it appears in \"text\".");
        sb.AppendLine("                     ONE sentence per entry, on a single line, no line breaks.");
        sb.AppendLine("    \"en\"           : English translation — one line, no line breaks.");
        sb.AppendLine("    \"zh\"           : Chinese translation (中文翻译) — one line, no line breaks.");
        sb.AppendLine("    \"structure_en\" : structure/grammar explanation in English — clause order,");
        sb.AppendLine("                     verb position, case usage, tense");
        sb.AppendLine("    \"structure_zh\" : the same explanation in Chinese");
        sb.AppendLine("    \"words\"        : the words occurring in THIS sentence, each with");
        sb.AppendLine("                     \"de\" (nouns WITH article and plural), \"pos\", \"en\", \"zh\"");
        sb.AppendLine("    \"phrases\"      : the phrases/collocations in THIS sentence, each with");
        sb.AppendLine("                     \"de\", \"en\", \"zh\". Omit or leave empty when it has none.");
        sb.AppendLine("- \"vocabulary\" : the story's COMPLETE word index — every distinct word used");
        sb.AppendLine("             anywhere in the text. Still required in full even though each");
        sb.AppendLine("             sentence also lists its own words. Each entry has:");
        sb.AppendLine("    \"de\"  : the headword — nouns WITH article and plural (e.g. \"der Hund, -e\")");
        sb.AppendLine("    \"pos\" : part of speech plus gender/plural or verb forms");
        sb.AppendLine("    \"en\"  : English meaning");
        sb.AppendLine("    \"zh\"  : Chinese meaning (中文释义)");
        sb.AppendLine("- \"phrases\" : array of EVERY fixed combination, prepositional phrase and common");
        sb.AppendLine("             collocation used in the story, each with \"de\", \"en\", \"zh\".");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Do not skip any sentence, word, or phrase. Completeness matters more than brevity.");
        sb.AppendLine("- \"en\" and \"zh\" must be present for EVERY sentence and must each stay on one");
        sb.AppendLine("  line: they are displayed line-by-line alongside the German sentence.");
        sb.AppendLine("- Respect the requested CEFR level strictly (for A1: present tense, simple main");
        sb.AppendLine("  clauses, everyday vocabulary) and the requested length per story.");
        sb.AppendLine($"- Return AT MOST {MaxStoryBatch} stories in this response, even if more were requested.");
        sb.AppendLine("  Each story must be complete with its full breakdown; the user will ask for the");
        sb.AppendLine("  next batch to reach the total.");
        sb.AppendLine("- Write NEW stories on topics not covered by the existing entries listed below.");
        sb.AppendLine("- Follow every instruction in the conversation, focusing on the latest message.");
        sb.AppendLine();

        sb.AppendLine("=== CONVERSATION ===");
        foreach (var turn in conversation)
        {
            var who = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "ASSISTANT" : "USER";
            sb.AppendLine($"[{who}] {turn.Text}");
        }
        sb.AppendLine("=== END CONVERSATION ===");
        sb.AppendLine();

        // Only story-level entries are listed: sending every vocabulary row of every
        // story would balloon the prompt, and titles are enough to avoid repeats.
        var titles = existingCards
            .Where(c => c.German.StartsWith(StoryPrefix, StringComparison.Ordinal))
            .Select(c => c.German)
            .ToList();
        sb.AppendLine($"=== STORIES ALREADY WRITTEN ({titles.Count}) ===");
        if (titles.Count == 0) sb.AppendLine("(none — this is the first batch)");
        else foreach (var t in titles) sb.AppendLine($"- {t}");
        sb.AppendLine("=== END STORIES ALREADY WRITTEN ===");
        sb.AppendLine();
        sb.AppendLine($"Now output a JSON array of at most {MaxStoryBatch} new story objects.");
        return sb.ToString();
    }

    private static List<AiStoryData>? ParseStories(string raw)
    {
        raw = StripFences(raw);
        try
        {
            var stories = JsonSerializer.Deserialize<List<AiStoryData>>(raw, JsonOpts);
            // A parse that yields no usable German text means the model answered in
            // some other shape; treat that as a failure so the caller shows the raw text.
            if (stories == null || stories.Count == 0) return null;
            if (stories.All(s => string.IsNullOrWhiteSpace(s.Text))) return null;
            return stories;
        }
        catch { return null; }
    }

    // Marks the one card per story that holds the whole text, so story cards can be
    // told apart from sentence/word/phrase cards in a mixed deck.
    public const string StoryPrefix = "📖 ";

    /// <summary>
    /// Section markers that pack the vocabulary and phrase lists into the story card's
    /// single English field. Chosen from a private-use bracket pair so they can never
    /// collide with German, English or Chinese content (see AiStudyViewModel.BuildStory*).
    /// </summary>
    public const string VocabMarker  = "⟦VOCAB⟧";
    public const string PhraseMarker = "⟦PHRASES⟧";

    /// <summary>Holds the story's full German paragraph as one unbroken block for whole-text TTS.</summary>
    public const string TextMarker = "⟦TEXT⟧";

    /// <summary>
    /// Opens the per-sentence word/phrase list for sentence N, written as "⟦SENT:3⟧".
    /// Rows inside are "W|de|pos|en|zh" for a word and "P|de||en|zh" for a phrase.
    /// </summary>
    public const string SentenceMarkerPrefix = "⟦SENT:";

    /// <summary>Title translations, one section each, so the card front can show all three languages.</summary>
    public const string TitleEnMarker = "⟦TITLE_EN⟧";
    public const string TitleZhMarker = "⟦TITLE_ZH⟧";

    /// <summary>
    /// Flattens one story into EXACTLY ONE study card, so grading it advances to the
    /// next story rather than stepping through its own sentences. The full breakdown
    /// rides along in the six existing note columns — no schema change:
    ///   German    → 📖 topic
    ///   English   → summary label, then marked sections: ⟦TITLE_EN⟧ / ⟦TITLE_ZH⟧,
    ///               ⟦TEXT⟧ (the whole paragraph), ⟦SENT:n⟧ (that sentence's words and
    ///               phrases), ⟦VOCAB⟧ and ⟦PHRASES⟧ (the story-wide indexes)
    ///   Grammar   → one structure note per sentence ('\n'-separated, index-aligned)
    ///   ExampleDe → one German sentence per line
    ///   ExampleEn → one English translation per line
    ///   Chinese   → one Chinese translation per line
    /// The four '\n'-separated blocks stay index-aligned so the study view can rebuild
    /// a row per sentence with its own play and copy buttons.
    /// </summary>
    public static IEnumerable<AiCardData> StoryToCards(AiStoryData story)
    {
        var title = string.IsNullOrWhiteSpace(story.Title) ? "Geschichte" : story.Title.Trim();
        var level = string.IsNullOrWhiteSpace(story.Level) ? "" : $" · {story.Level.Trim()}";

        var lines = (story.Sentences ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.De))
            .ToList();

        if (lines.Count > 0 || !string.IsNullOrWhiteSpace(story.Text))
        {
            // The story card holds one sentence PER LINE in three parallel blocks —
            // German / English / Chinese — so the study view can render a row per
            // sentence with its own play and copy buttons (see AiStudyViewModel.
            // BuildStoryLines). Sentences are single-line by instruction, so '\n' is
            // an unambiguous separator and the columns stay index-aligned.
            // Falls back to the unsplit text when a story arrived without a sentence list.
            // OneLine guards the alignment: '\n' is the row separator, so an embedded
            // break in any single value would silently shift every later translation.
            var de = lines.Count > 0
                ? string.Join("\n", lines.Select(s => OneLine(s.De)))
                : OneLine(story.Text);
            var en = string.Join("\n", lines.Select(s => OneLine(s.En)));
            var zh = string.Join("\n", lines.Select(s => OneLine(s.Zh)));
            // Structure notes ride in the Grammar column, one line per sentence and
            // index-aligned with the blocks above. An empty line keeps that alignment
            // when the model skipped the analysis for one sentence.
            var gr = string.Join("\n", lines.Select(s => OneLine(Join(" / ", s.StructureEn, s.StructureZh))));

            var summary = $"Story{level} · {(lines.Count > 0 ? $"{lines.Count} sentences" : "full text")}";

            // Vocabulary and phrases append as marked sections of the English field:
            // "de | pos | en | zh" per line, so the study view can rebuild the tables.
            var vocab = (story.Vocabulary ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v.De))
                .Select(v => string.Join(" | ", Cell(v.De), Cell(v.Pos), Cell(v.En), Cell(v.Zh)))
                .ToList();
            var phrases = (story.Phrases ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.De))
                .Select(p => string.Join(" | ", Cell(p.De), "", Cell(p.En), Cell(p.Zh)))
                .ToList();

            var sb = new StringBuilder(summary);

            // Title translations, so the front can head the page with all three languages.
            if (!string.IsNullOrWhiteSpace(story.TitleEn))
                sb.Append('\n').Append(TitleEnMarker).Append('\n').Append(OneLine(story.TitleEn));
            if (!string.IsNullOrWhiteSpace(story.TitleZh))
                sb.Append('\n').Append(TitleZhMarker).Append('\n').Append(OneLine(story.TitleZh));

            // The paragraph as one block. Sentence rows are joined with spaces when the
            // model gave no separate "text", so the reader always has a full-text unit.
            var para = OneLine(string.IsNullOrWhiteSpace(story.Text)
                ? string.Join(" ", lines.Select(s => s.De.Trim()))
                : story.Text);
            if (para.Length > 0)
                sb.Append('\n').Append(TextMarker).Append('\n').Append(para);

            // Per-sentence words and phrases, one section per sentence, tagged W/P so a
            // single section carries both kinds while staying one row per line.
            for (var i = 0; i < lines.Count; i++)
            {
                var rows = new List<string>();
                foreach (var w in lines[i].Words ?? [])
                    if (!string.IsNullOrWhiteSpace(w.De))
                        rows.Add(string.Join("|", "W", Cell(w.De), Cell(w.Pos), Cell(w.En), Cell(w.Zh)));
                foreach (var p in lines[i].Phrases ?? [])
                    if (!string.IsNullOrWhiteSpace(p.De))
                        rows.Add(string.Join("|", "P", Cell(p.De), "", Cell(p.En), Cell(p.Zh)));

                if (rows.Count > 0)
                    sb.Append('\n').Append(SentenceMarkerPrefix).Append(i + 1).Append('⟧')
                      .Append('\n').Append(string.Join("\n", rows));
            }

            if (vocab.Count > 0)   sb.Append('\n').Append(VocabMarker).Append('\n').Append(string.Join("\n", vocab));
            if (phrases.Count > 0) sb.Append('\n').Append(PhraseMarker).Append('\n').Append(string.Join("\n", phrases));

            yield return new AiCardData(
                German:    StoryPrefix + title,
                English:   sb.ToString(),
                Grammar:   gr,
                ExampleDe: de,
                ExampleEn: en,
                Chinese:   zh);
        }
    }

    /// <summary>Collapses any internal line breaks so one value stays one line.</summary>
    /// <summary>
    /// One field of a "|"-delimited packed row. Collapses newlines like <see cref="OneLine"/>
    /// and additionally strips "|", which would otherwise shift every following field of
    /// that row when the study view splits it back apart.
    /// </summary>
    private static string Cell(string? s) => OneLine(s).Replace('|', '/');

    private static string OneLine(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? ""
            : string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim())
                                .Where(x => x.Length > 0));

    private static string Join(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static List<AiCardData>? ParseCards(string raw)
    {
        try { return JsonSerializer.Deserialize<List<AiCardData>>(StripFences(raw), JsonOpts); }
        catch { return null; }
    }

    /// <summary>Removes a ```json … ``` wrapper if the model added one despite instructions.</summary>
    private static string StripFences(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var nl  = raw.IndexOf('\n');
            var end = raw.LastIndexOf("```");
            if (nl >= 0 && end > nl) raw = raw[(nl + 1)..end].Trim();
        }
        return raw;
    }

    private static string ConversationCacheKey(
        IReadOnlyList<AiConversationTurn> conversation,
        IReadOnlyList<AiCardData> existingCards,
        bool storyMode)
    {
        // Include the current deck size + terms: the same conversation applied to a
        // different deck state must produce a different (fresh) batch, not a stale hit.
        // storyMode is part of the key so the two prompt shapes never share an entry.
        // "story2" retires entries written when one story fanned out into many cards
        // (one per sentence/word/phrase); replaying those would restore the old queue.
        var convo = string.Join("\n", conversation.Select(t => $"{t.Role}:{t.Text.Trim()}"));
        var deck  = string.Join("\n", existingCards.Select(c => c.German.Trim()));
        var joined = ($"{(storyMode ? "story2" : "cards")}\n{convo}\n##DECK##\n{deck}").ToLowerInvariant();
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
