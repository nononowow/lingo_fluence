using System.Text.Json.Serialization;

namespace LingoFluence.Models;

/// <summary>
/// One turn in an AI card-generation conversation. The full ordered list is
/// stored with the deck so the user can reopen it and keep refining the cards.
/// </summary>
public record AiConversationTurn(
    [property: JsonPropertyName("role")] string Role,   // "user" or "assistant"
    [property: JsonPropertyName("text")] string Text
);
