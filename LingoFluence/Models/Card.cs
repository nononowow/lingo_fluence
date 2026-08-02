namespace LingoFluence.Models;

public class Card
{
    public int Id { get; set; }
    public int NoteId { get; set; }
    public int DeckId { get; set; }
    // The German word (answer to spell)
    public string FrontText { get; set; } = "";
    // The translation/meaning shown as context
    public string BackText { get; set; } = "";
    // Optional audio file path (matches the German example sentence, not the word)
    public string? AudioFile { get; set; }
    // Richer copyable details (empty for Basic decks without these fields)
    public string SentenceDe { get; set; } = ""; // German example sentence (matches audio)
    public string WordEn { get; set; } = "";     // English translation of the word
    public string SentenceEn { get; set; } = ""; // English example sentence
    public string Chinese { get; set; } = "";    // Chinese meaning of the word (AI decks)
    public string SentenceZh { get; set; } = ""; // Chinese translation of the German example sentence (on-demand)
    public string Ipa { get; set; } = "";        // IPA phonetic transcription of the German word (on-demand)
    public DateTime DueDate { get; set; } = DateTime.Today;
    public int Interval { get; set; } = 0; // days; 0 = new/learning
    public double EaseFactor { get; set; } = 2.5;
    public int RepCount { get; set; } = 0;
    public int LapseCount { get; set; } = 0;
    public CardState State { get; set; } = CardState.New;
    public long AnkiCardId { get; set; }
    public long AnkiNoteId { get; set; }
}

public enum CardState { New = 0, Learning = 1, Review = 2 }
public enum ReviewGrade { Again = 0, Hard = 1, Good = 2, Easy = 3 }
