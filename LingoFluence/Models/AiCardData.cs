using System.Text.Json.Serialization;

namespace LingoFluence.Models;

/// <summary>
/// One AI-generated flashcard returned from the claude CLI.
/// Maps to the existing note columns: answer_text / context_text / word_en / sentence_de / sentence_en.
/// </summary>
public record AiCardData(
    string German,
    string English,
    string Grammar,
    [property: JsonPropertyName("example_de")] string ExampleDe,
    [property: JsonPropertyName("example_en")] string ExampleEn
);
