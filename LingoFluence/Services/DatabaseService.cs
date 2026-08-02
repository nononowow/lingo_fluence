using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using LingoFluence.Models;

namespace LingoFluence.Services;

/// <summary>
/// Manages the local SQLite database storing all imported decks and review state.
/// </summary>
public class DatabaseService
{
    public static readonly string AppDataPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LingoFluence");
    public static readonly string DbPath = Path.Combine(AppDataPath, "data.db");
    private string ConnStr => $"Data Source={DbPath}";

    public DatabaseService()
    {
        Directory.CreateDirectory(AppDataPath);
        InitDb();
    }

    private void InitDb()
    {
        using var conn = Open();
        Exec(conn, @"
            CREATE TABLE IF NOT EXISTS decks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                import_path TEXT,
                imported_at TEXT,
                media_folder TEXT
            );
            CREATE TABLE IF NOT EXISTS notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                deck_id INTEGER NOT NULL,
                anki_note_id INTEGER,
                answer_text TEXT NOT NULL,
                context_text TEXT,
                audio_file TEXT,
                FOREIGN KEY (deck_id) REFERENCES decks(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS cards (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                note_id INTEGER NOT NULL,
                deck_id INTEGER NOT NULL,
                anki_card_id INTEGER,
                due_date TEXT NOT NULL,
                interval INTEGER NOT NULL DEFAULT 0,
                ease_factor REAL NOT NULL DEFAULT 2.5,
                rep_count INTEGER NOT NULL DEFAULT 0,
                lapse_count INTEGER NOT NULL DEFAULT 0,
                card_state INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (note_id) REFERENCES notes(id) ON DELETE CASCADE
            );
        ");

        // Migrate existing databases: add richer note fields if missing.
        EnsureColumn(conn, "notes", "sentence_de", "TEXT");
        EnsureColumn(conn, "notes", "word_en",     "TEXT");
        EnsureColumn(conn, "notes", "sentence_en", "TEXT");
        // Chinese meaning of the word (AI decks). Empty for imported Anki decks.
        EnsureColumn(conn, "notes", "chinese",     "TEXT");
        EnsureColumn(conn, "decks", "is_ai",       "INTEGER NOT NULL DEFAULT 0");
        // Stores the AI generation transcript (JSON array of AiConversationTurn)
        // so an AI deck can be reopened and refined.
        EnsureColumn(conn, "decks", "conversation", "TEXT");
    }

    // Idempotently add a column to a table if it doesn't already exist.
    private static void EnsureColumn(SqliteConnection conn, string table, string col, string decl)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var r = check.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), col, StringComparison.OrdinalIgnoreCase))
                return; // column already present
        r.Close();
        Exec(conn, $"ALTER TABLE {table} ADD COLUMN {col} {decl}");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnStr);
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ─── Deck operations ────────────────────────────────────────────────────

    public List<Deck> LoadDecks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT d.id, d.name, d.import_path, d.imported_at,
                   COUNT(c.id)                                                   AS total,
                   SUM(CASE WHEN c.card_state = 0 THEN 1 ELSE 0 END)           AS new_cnt,
                   SUM(CASE WHEN c.due_date <= date('now')
                             AND c.card_state > 0 THEN 1 ELSE 0 END)           AS due_cnt,
                   d.is_ai
            FROM decks d
            LEFT JOIN cards c ON c.deck_id = d.id
            GROUP BY d.id ORDER BY d.imported_at DESC";

        var result = new List<Deck>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Deck
            {
                Id        = r.GetInt32(0),
                Name      = r.GetString(1),
                ImportPath = r.IsDBNull(2) ? "" : r.GetString(2),
                ImportedAt = DateTime.TryParse(r.IsDBNull(3) ? null : r.GetString(3), out var dt) ? dt : DateTime.Now,
                TotalCards = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                NewCards   = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                DueCards   = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                IsAi       = !r.IsDBNull(7) && r.GetInt32(7) != 0
            });
        }
        return result;
    }

    public int SaveDeck(string name, string importPath, string mediaFolder)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO decks (name, import_path, imported_at, media_folder)
            VALUES ($n, $p, $t, $m); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$p", importPath);
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$m", mediaFolder);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void DeleteDeck(int deckId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM decks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", deckId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns ids of any decks previously imported from the same source file,
    /// so a re-import can replace stale data instead of duplicating it.
    /// </summary>
    public List<int> FindDecksByImportPath(string importPath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM decks WHERE import_path=$p";
        cmd.Parameters.AddWithValue("$p", importPath);
        var ids = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt32(0));
        return ids;
    }

    // ─── AI deck ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a new AI-generated deck along with its generation transcript.
    /// The user request is stored as import_path for reference; the full
    /// conversation is stored as JSON so the deck can be reopened and refined.
    /// </summary>
    public int SaveAiDeck(string name, string userRequest,
        IEnumerable<AiCardData> cards, IReadOnlyList<AiConversationTurn>? conversation = null)
    {
        int deckId;
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO decks (name, import_path, imported_at, media_folder, is_ai, conversation)
                VALUES ($n, $p, $t, '', 1, $cv); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$p", userRequest);
            cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$cv", SerializeConversation(conversation));
            deckId = Convert.ToInt32(cmd.ExecuteScalar());
        }

        SaveNotesAndCards(deckId, BuildAiRows(cards));
        return deckId;
    }

    /// <summary>
    /// Replaces an existing AI deck's cards and transcript in place (same deck id,
    /// so continuing a conversation never spawns a duplicate deck). Review history
    /// is reset because the card set is regenerated.
    /// </summary>
    public void UpdateAiDeck(int deckId, string name,
        IEnumerable<AiCardData> cards, IReadOnlyList<AiConversationTurn> conversation)
    {
        using (var conn = Open())
        {
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                // Notes cascade-delete their cards via the FK.
                del.CommandText = "DELETE FROM notes WHERE deck_id=$d";
                del.Parameters.AddWithValue("$d", deckId);
                del.ExecuteNonQuery();
            }
            using (var upd = conn.CreateCommand())
            {
                upd.CommandText = "UPDATE decks SET name=$n, conversation=$cv WHERE id=$d";
                upd.Parameters.AddWithValue("$n", name);
                upd.Parameters.AddWithValue("$cv", SerializeConversation(conversation));
                upd.Parameters.AddWithValue("$d", deckId);
                upd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        SaveNotesAndCards(deckId, BuildAiRows(cards));
    }

    /// <summary>Loads the stored generation transcript for an AI deck (empty if none).</summary>
    public List<AiConversationTurn> GetConversation(int deckId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT conversation FROM decks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", deckId);
        var v = cmd.ExecuteScalar();
        var json = v as string;
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AiConversationTurn>>(json) ?? [];
        }
        catch { return []; }
    }

    /// <summary>Reads an AI deck's cards back as AiCardData for editing/preview.</summary>
    public List<AiCardData> GetAiCards(int deckId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT n.answer_text, n.context_text, n.word_en, n.sentence_de, n.sentence_en, n.chinese
            FROM notes n WHERE n.deck_id=$d ORDER BY n.id ASC";
        cmd.Parameters.AddWithValue("$d", deckId);

        var list = new List<AiCardData>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AiCardData(
                German:    r.GetString(0),
                English:   r.IsDBNull(1) ? "" : r.GetString(1),
                Grammar:   r.IsDBNull(2) ? "" : r.GetString(2),
                ExampleDe: r.IsDBNull(3) ? "" : r.GetString(3),
                ExampleEn: r.IsDBNull(4) ? "" : r.GetString(4),
                Chinese:   r.IsDBNull(5) ? "" : r.GetString(5)));
        }
        return list;
    }

    private static string SerializeConversation(IReadOnlyList<AiConversationTurn>? conversation)
        => conversation == null || conversation.Count == 0
            ? (string)"" : JsonSerializer.Serialize(conversation);

    private static IEnumerable<(long ankiNoteId, string answer, string context, string? audio,
                     long ankiCardId, DateTime dueDate, int interval, double ease,
                     int reps, int lapses, CardState state,
                     string sentenceDe, string wordEn, string sentenceEn, string chinese)>
        BuildAiRows(IEnumerable<AiCardData> cards)
    {
        var baseId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return cards.Select((c, i) =>
        {
            long id = -(baseId + i);   // negative → never collides with real Anki IDs (timestamps)
            return (ankiNoteId: id,
                    answer:     c.German,
                    context:    c.English,
                    audio:      (string?)null,
                    ankiCardId: id,
                    dueDate:    DateTime.Today,
                    interval:   0,
                    ease:       2.5,
                    reps:       0,
                    lapses:     0,
                    state:      CardState.New,
                    sentenceDe: c.ExampleDe,
                    wordEn:     c.Grammar,
                    sentenceEn: c.ExampleEn,
                    chinese:    c.Chinese);
        });
    }

    // ─── Note / Card import ──────────────────────────────────────────────────

    public void SaveNotesAndCards(int deckId,
        IEnumerable<(long ankiNoteId, string answer, string context, string? audio,
                     long ankiCardId, DateTime dueDate, int interval, double ease,
                     int reps, int lapses, CardState state,
                     string sentenceDe, string wordEn, string sentenceEn, string chinese)> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var noteCmd = conn.CreateCommand();
        noteCmd.CommandText = @"
            INSERT INTO notes (deck_id, anki_note_id, answer_text, context_text, audio_file,
                               sentence_de, word_en, sentence_en, chinese)
            VALUES ($d,$an,$a,$c,$au,$sd,$we,$se,$zh); SELECT last_insert_rowid();";

        using var cardCmd = conn.CreateCommand();
        cardCmd.CommandText = @"
            INSERT INTO cards (note_id, deck_id, anki_card_id, due_date, interval,
                               ease_factor, rep_count, lapse_count, card_state)
            VALUES ($ni,$di,$ac,$dd,$iv,$ef,$rc,$lc,$cs)";

        foreach (var row in rows)
        {
            noteCmd.Parameters.Clear();
            noteCmd.Parameters.AddWithValue("$d",  deckId);
            noteCmd.Parameters.AddWithValue("$an", row.ankiNoteId);
            noteCmd.Parameters.AddWithValue("$a",  row.answer);
            noteCmd.Parameters.AddWithValue("$c",  row.context ?? (object)DBNull.Value);
            noteCmd.Parameters.AddWithValue("$au", row.audio ?? (object)DBNull.Value);
            noteCmd.Parameters.AddWithValue("$sd", string.IsNullOrEmpty(row.sentenceDe) ? (object)DBNull.Value : row.sentenceDe);
            noteCmd.Parameters.AddWithValue("$we", string.IsNullOrEmpty(row.wordEn)     ? (object)DBNull.Value : row.wordEn);
            noteCmd.Parameters.AddWithValue("$se", string.IsNullOrEmpty(row.sentenceEn) ? (object)DBNull.Value : row.sentenceEn);
            noteCmd.Parameters.AddWithValue("$zh", string.IsNullOrEmpty(row.chinese)    ? (object)DBNull.Value : row.chinese);
            var noteId = Convert.ToInt32(noteCmd.ExecuteScalar());

            cardCmd.Parameters.Clear();
            cardCmd.Parameters.AddWithValue("$ni", noteId);
            cardCmd.Parameters.AddWithValue("$di", deckId);
            cardCmd.Parameters.AddWithValue("$ac", row.ankiCardId);
            cardCmd.Parameters.AddWithValue("$dd", row.dueDate.ToString("o"));
            cardCmd.Parameters.AddWithValue("$iv", row.interval);
            cardCmd.Parameters.AddWithValue("$ef", row.ease);
            cardCmd.Parameters.AddWithValue("$rc", row.reps);
            cardCmd.Parameters.AddWithValue("$lc", row.lapses);
            cardCmd.Parameters.AddWithValue("$cs", (int)row.state);
            cardCmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ─── Study session queries ───────────────────────────────────────────────

    public List<Card> GetDueCards(int deckId, int maxNew = 20)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.id, c.note_id, c.deck_id,
                   n.answer_text, n.context_text, n.audio_file,
                   c.due_date, c.interval, c.ease_factor,
                   c.rep_count, c.lapse_count, c.card_state,
                   n.sentence_de, n.word_en, n.sentence_en, n.chinese
            FROM cards c
            JOIN notes n ON n.id = c.note_id
            WHERE c.deck_id = $did
              AND (c.card_state = 0 OR c.due_date <= datetime('now'))
            ORDER BY c.card_state ASC, c.due_date ASC
            LIMIT 500";
        cmd.Parameters.AddWithValue("$did", deckId);

        var result = new List<Card>();
        int newSeen = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var state = (CardState)r.GetInt32(11);
            if (state == CardState.New && newSeen >= maxNew) continue;
            if (state == CardState.New) newSeen++;
            result.Add(new Card
            {
                Id         = r.GetInt32(0),
                NoteId     = r.GetInt32(1),
                DeckId     = r.GetInt32(2),
                FrontText  = r.GetString(3),
                BackText   = r.IsDBNull(4) ? "" : r.GetString(4),
                AudioFile  = r.IsDBNull(5) ? null : r.GetString(5),
                DueDate    = DateTime.TryParse(r.GetString(6), out var dt) ? dt : DateTime.Now,
                Interval   = r.GetInt32(7),
                EaseFactor = r.GetDouble(8),
                RepCount   = r.GetInt32(9),
                LapseCount = r.GetInt32(10),
                State      = state,
                SentenceDe = r.IsDBNull(12) ? "" : r.GetString(12),
                WordEn     = r.IsDBNull(13) ? "" : r.GetString(13),
                SentenceEn = r.IsDBNull(14) ? "" : r.GetString(14),
                Chinese    = r.IsDBNull(15) ? "" : r.GetString(15)
            });
        }
        return result;
    }

    public void UpdateCard(Card card)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE cards SET
                due_date    = $dd,
                interval    = $iv,
                ease_factor = $ef,
                rep_count   = $rc,
                lapse_count = $lc,
                card_state  = $cs
            WHERE id = $id";
        cmd.Parameters.AddWithValue("$dd", card.DueDate.ToString("o"));
        cmd.Parameters.AddWithValue("$iv", card.Interval);
        cmd.Parameters.AddWithValue("$ef", card.EaseFactor);
        cmd.Parameters.AddWithValue("$rc", card.RepCount);
        cmd.Parameters.AddWithValue("$lc", card.LapseCount);
        cmd.Parameters.AddWithValue("$cs", (int)card.State);
        cmd.Parameters.AddWithValue("$id", card.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persists a fetched Chinese meaning onto a note so it's cached permanently.</summary>
    public void UpdateNoteChinese(int noteId, string chinese)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notes SET chinese=$c WHERE id=$id";
        cmd.Parameters.AddWithValue("$c", chinese);
        cmd.Parameters.AddWithValue("$id", noteId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateDeckMediaFolder(int deckId, string mediaFolder)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE decks SET media_folder=$m WHERE id=$id";
        cmd.Parameters.AddWithValue("$m", mediaFolder);
        cmd.Parameters.AddWithValue("$id", deckId);
        cmd.ExecuteNonQuery();
    }

    public string? GetMediaFolder(int deckId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT media_folder FROM decks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", deckId);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }
}
