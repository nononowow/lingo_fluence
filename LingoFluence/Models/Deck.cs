namespace LingoFluence.Models;

public class Deck
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ImportPath { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public int TotalCards { get; set; }
    public int DueCards { get; set; }
    public int NewCards { get; set; }

    public bool IsAi { get; set; }

    public string StatsDisplay => IsAi
        ? $"Total: {TotalCards}"
        : $"Due: {DueCards}  New: {NewCards}  Total: {TotalCards}";
}
