using System.Text.Json.Serialization;

namespace LingoFluence.Models;

/// <summary>
/// One AI-generated reading story with a full didactic breakdown: the German
/// text, a sentence-by-sentence analysis, and exhaustive vocabulary / phrase
/// lists. Flattened into <see cref="AiCardData"/> rows for storage and study
/// (see AiService.StoryToCards), so stories reuse the whole existing card
/// pipeline — TTS, copy buttons, IPA, spaced repetition.
/// </summary>
public record AiStoryData(
    string Title,
    string Text,
    [property: JsonPropertyName("level")]      string Level = "",
    [property: JsonPropertyName("title_en")]   string TitleEn = "",
    [property: JsonPropertyName("title_zh")]   string TitleZh = "",
    [property: JsonPropertyName("sentences")]  List<AiStorySentence>? Sentences = null,
    [property: JsonPropertyName("vocabulary")] List<AiStoryVocab>?    Vocabulary = null,
    [property: JsonPropertyName("phrases")]    List<AiStoryPhrase>?   Phrases = null
);

/// <summary>
/// One sentence of a story with translations, a structural analysis, and the words
/// and phrases that occur in THIS sentence. The per-sentence lists are shown inline
/// under the sentence; the story-level lists remain the exhaustive index.
/// </summary>
public record AiStorySentence(
    [property: JsonPropertyName("de")] string De,
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("zh")] string Zh,
    [property: JsonPropertyName("structure_en")] string StructureEn = "",
    [property: JsonPropertyName("structure_zh")] string StructureZh = "",
    [property: JsonPropertyName("words")]   List<AiStoryVocab>?  Words   = null,
    [property: JsonPropertyName("phrases")] List<AiStoryPhrase>? Phrases = null
);

/// <summary>One vocabulary entry: headword (nouns with article + plural), part of speech, meanings.</summary>
public record AiStoryVocab(
    [property: JsonPropertyName("de")]  string De,
    [property: JsonPropertyName("en")]  string En,
    [property: JsonPropertyName("zh")]  string Zh,
    [property: JsonPropertyName("pos")] string Pos = ""
);

/// <summary>One fixed phrase, prepositional phrase, or collocation used in the story.</summary>
public record AiStoryPhrase(
    [property: JsonPropertyName("de")] string De,
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("zh")] string Zh
);
