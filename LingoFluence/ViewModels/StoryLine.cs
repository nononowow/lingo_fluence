namespace LingoFluence.ViewModels;

/// <summary>
/// One sentence of a story card, shown as its own row so it can be spoken and
/// copied independently of the rest of the text.
/// </summary>
/// <param name="Number">1-based position, displayed as the row's gutter label.</param>
/// <param name="De">The German sentence — the unit passed to TTS.</param>
/// <param name="En">English translation, empty when the model omitted one.</param>
/// <param name="Zh">Chinese translation, empty when the model omitted one.</param>
public sealed record StoryLine(
    int Number,
    string De,
    string En,
    string Zh,
    string Structure = "",
    IReadOnlyList<StoryEntry>? Words = null,
    IReadOnlyList<StoryEntry>? Phrases = null)
{
    public bool HasEn => !string.IsNullOrWhiteSpace(En);
    public bool HasZh => !string.IsNullOrWhiteSpace(Zh);
    public bool HasStructure => !string.IsNullOrWhiteSpace(Structure);

    /// <summary>The words of this sentence, shown inline beneath it.</summary>
    public IReadOnlyList<StoryEntry> WordList   => Words   ?? [];
    public IReadOnlyList<StoryEntry> PhraseList => Phrases ?? [];

    public bool HasWords   => WordList.Count   > 0;
    public bool HasPhrases => PhraseList.Count > 0;

    /// <summary>Row heading, e.g. "Sentence 3:".</summary>
    public string Label => $"Sentence {Number}:";

    /// <summary>
    /// What the back face's 📋 button copies: the sentence plus every translation it
    /// has. The front face copies <see cref="De"/> alone, since it keeps translations
    /// hidden until the card is flipped.
    /// </summary>
    public string CopyText
    {
        get
        {
            var parts = new List<string>(
                new[] { De, En, Zh, Structure }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (HasWords)
                parts.Add("Words: " + string.Join("; ", WordList.Select(w => $"{w.Headword} — {w.Meaning}")));
            if (HasPhrases)
                parts.Add("Phrases: " + string.Join("; ", PhraseList.Select(p => $"{p.De} — {p.Meaning}")));
            return string.Join("\n", parts);
        }
    }
}

/// <summary>
/// One vocabulary or phrase row of a story card: headword, part of speech (blank for
/// phrases), and both meanings. Parsed from the packed sections of the English field
/// by AiStudyViewModel.BuildStoryEntries.
/// </summary>
public sealed record StoryEntry(string De, string Pos, string En, string Zh)
{
    public bool HasPos => !string.IsNullOrWhiteSpace(Pos);

    /// <summary>"der Hund, -e · noun" — headword with its grammatical label.</summary>
    public string Headword => HasPos ? $"{De} · {Pos}" : De;

    /// <summary>Both meanings on one line, e.g. "dog · 狗".</summary>
    public string Meaning =>
        string.Join(" · ", new[] { En, Zh }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string CopyText =>
        string.Join("\n", new[] { De, Pos, En, Zh }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
