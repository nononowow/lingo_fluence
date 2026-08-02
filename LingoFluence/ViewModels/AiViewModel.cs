using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using LingoFluence.Models;
using LingoFluence.Services;

namespace LingoFluence.ViewModels;

/// <summary>
/// ViewModel for the AI card generator window.
/// Holds the full generation conversation and sends the whole transcript to
/// AiService on every turn, so continuing to chat refines the SAME deck instead
/// of producing a disjoint second card set. Can open in edit mode to reload an
/// existing AI deck's transcript and update it in place.
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

    // The running transcript. Every Generate appends a user turn, sends the whole
    // list, then appends an assistant turn. Persisted with the deck on import.
    private readonly List<AiConversationTurn> _conversation = [];

    // When >0 we are editing an existing AI deck; Import updates it in place.
    private int _editingDeckId = -1;

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

    /// <summary>Import button label reflects whether we create or update a deck.</summary>
    public string ImportButtonText => _editingDeckId > 0 ? "💾  Save Changes" : "📥  Import Deck";

    public ObservableCollection<string>       ChatMessages { get; } = [];
    public ObservableCollection<AiCardPreview> CardPreviews { get; } = [];

    /// <summary>Set after a successful Import/Save so the caller can refresh its deck list.</summary>
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

    /// <summary>
    /// Loads an existing AI deck for continued editing: restores its transcript,
    /// current cards, and name, so the next Generate refines it and Import saves
    /// back to the same deck id.
    /// </summary>
    public void LoadForEdit(int deckId, string deckName)
    {
        _editingDeckId = deckId;
        DeckName = deckName;
        OnPropertyChanged(nameof(ImportButtonText));

        var turns = _db.GetConversation(deckId);
        var cards = _db.GetAiCards(deckId);
        _conversation.AddRange(turns);
        _generatedCards = cards;

        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var t in turns)
            {
                var icon = t.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "🤖" : "🧑";
                ChatMessages.Add($"{icon}  {t.Text}");
            }
            foreach (var c in cards)
                CardPreviews.Add(new AiCardPreview(c.German, c.English, c.Grammar));
        });

        AddMsg($"✏️  Editing \"{deckName}\" ({cards.Count} cards). Type more instructions to refine, then Save Changes.");
        HasCards = cards.Count > 0;
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
        var request = UserRequest.Trim();
        _conversation.Add(new AiConversationTurn("user", request));
        AddMsg($"🧑  {request}");
        UserRequest = "";

        IsGenerating = true;
        var progress = new Progress<string>(msg => AddMsg($"⏳  {msg}"));
        try
        {
            var cards = await _ai.GenerateFromConversationAsync(_conversation, progress);
            _generatedCards = cards;

            // Record the assistant's contribution as a compact summary turn so the
            // next round has context without re-sending the full card JSON.
            _conversation.Add(new AiConversationTurn(
                "assistant", $"(produced a deck of {cards.Count} cards)"));

            Application.Current.Dispatcher.Invoke(() =>
            {
                CardPreviews.Clear();
                foreach (var c in cards)
                    CardPreviews.Add(new AiCardPreview(c.German, c.English, c.Grammar));
            });
            AddMsg($"🤖  {cards.Count} cards ready. Refine with another message, or name the deck and {(_editingDeckId > 0 ? "Save Changes" : "Import")}.");
            HasCards = true;
            if (string.IsNullOrWhiteSpace(DeckName))
                DeckName = $"AI: {request[..Math.Min(45, request.Length)]}";
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
            var convo = new List<AiConversationTurn>(_conversation);

            if (_editingDeckId > 0)
            {
                var id = _editingDeckId;
                await Task.Run(() => _db.UpdateAiDeck(id, name, cards, convo));
                ImportedDeckId = id;
                AddMsg($"✅  Saved \"{name}\" ({cards.Count} cards). Close this window to see it.");
            }
            else
            {
                var req = convo.Count > 0 ? convo[0].Text : name;
                ImportedDeckId = await Task.Run(() => _db.SaveAiDeck(name, req, cards, convo));
                _editingDeckId = ImportedDeckId;   // further edits update this deck
                OnPropertyChanged(nameof(ImportButtonText));
                AddMsg($"✅  Deck \"{name}\" imported ({cards.Count} cards). Close this window to see it.");
            }
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
