using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using LingoFluence.Models;
using LingoFluence.Services;

namespace LingoFluence.ViewModels;

/// <summary>
/// ViewModel for the AI card generator window.
/// Calls claude CLI via AiService, shows progress in a chat log,
/// and saves the result as an is_ai deck.
/// </summary>
public class AiViewModel : BaseViewModel
{
    private readonly DatabaseService _db = new();
    private readonly AiService       _ai = new();

    private string _userRequest = "";
    private string _deckName    = "";
    private bool   _isGenerating;
    private bool   _hasCards;
    private bool   _isAvailable;
    private List<AiCardData> _generatedCards = [];

    public string UserRequest
    {
        get => _userRequest;
        set { Set(ref _userRequest, value); InvalidateCommands(); }
    }

    public string DeckName
    {
        get => _deckName;
        set { Set(ref _deckName, value); InvalidateCommands(); }
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        private set { Set(ref _isGenerating, value); InvalidateCommands(); }
    }

    public bool HasCards
    {
        get => _hasCards;
        private set { Set(ref _hasCards, value); InvalidateCommands(); }
    }

    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }

    public ObservableCollection<string>       ChatMessages { get; } = [];
    public ObservableCollection<AiCardPreview> CardPreviews { get; } = [];

    /// <summary>Set after a successful Import so the caller can refresh its deck list.</summary>
    public int ImportedDeckId { get; private set; } = -1;

    public ICommand GenerateCommand { get; }
    public ICommand ImportCommand   { get; }

    public AiViewModel()
    {
        GenerateCommand = new RelayCommand(
            async _ => await GenerateAsync(),
            _ => !IsGenerating && !string.IsNullOrWhiteSpace(UserRequest));
        ImportCommand = new RelayCommand(
            async _ => await ImportAsync(),
            _ => HasCards && !IsGenerating && !string.IsNullOrWhiteSpace(DeckName));

        _ = CheckAvailabilityAsync();
    }

    // ── Async operations ──────────────────────────────────────────────────────

    private async Task CheckAvailabilityAsync()
    {
        var path = await AiService.FindClaudeAsync();
        IsAvailable = path != null;
        AddMsg(IsAvailable
            ? "✅  claude CLI ready. Describe the flashcards you want to generate, then press Generate."
            : "⚠️  claude CLI not found in PATH. Install it (npm i -g @anthropic-ai/claude-code) and restart.");
    }

    private async Task GenerateAsync()
    {
        HasCards = false;
        _generatedCards = [];
        Application.Current.Dispatcher.Invoke(() => CardPreviews.Clear());
        IsGenerating = true;
        AddMsg($"🧑  {UserRequest}");

        var progress = new Progress<string>(msg => AddMsg($"⏳  {msg}"));
        try
        {
            _generatedCards = await _ai.GenerateCardsAsync(UserRequest, progress);
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var c in _generatedCards)
                    CardPreviews.Add(new AiCardPreview(c.German, c.English, c.Grammar));
            });
            AddMsg($"✅  {_generatedCards.Count} cards ready. Give the deck a name and click Import.");
            HasCards = true;
            if (string.IsNullOrWhiteSpace(DeckName))
                DeckName = $"AI: {UserRequest.Trim()[..Math.Min(45, UserRequest.Trim().Length)]}";
        }
        catch (Exception ex) { AddMsg($"❌  {ex.Message}"); }
        finally { IsGenerating = false; }
    }

    private async Task ImportAsync()
    {
        IsGenerating = true;
        try
        {
            var name  = DeckName;
            var cards = _generatedCards;
            var req   = UserRequest;
            ImportedDeckId = await Task.Run(() => _db.SaveAiDeck(name, req, cards));
            AddMsg($"✅  Deck \"{name}\" imported ({cards.Count} cards). Close this window to see it.");
            HasCards = false;
        }
        catch (Exception ex) { AddMsg($"❌  Import failed: {ex.Message}"); }
        finally { IsGenerating = false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AddMsg(string msg) =>
        Application.Current.Dispatcher.Invoke(() => ChatMessages.Add(msg));

    private static void InvalidateCommands() =>
        Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal,
            (Action)System.Windows.Input.CommandManager.InvalidateRequerySuggested);
}

public record AiCardPreview(string German, string English, string Grammar);
