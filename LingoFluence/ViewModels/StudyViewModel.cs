using System.IO;
using System.Text;
using LingoFluence.Models;
using LingoFluence.Services;

namespace LingoFluence.ViewModels;

public class StudyViewModel : BaseViewModel
{
    private readonly DatabaseService          _db;
    private readonly SpacedRepetitionService  _srs  = new();
    private readonly AudioService             _audio = new();
    private readonly TtsService               _tts   = new();
    private readonly Random                   _rng   = new();

    private List<Card>   _queue       = new();
    private int          _index       = 0;
    private bool         _hintUsed = false;
    private HashSet<int> _revealed  = new();

    // Cards graduated this session (rated Good/Easy). Keyed by card id so a card
    // that recurs after Again/Hard is only counted once toward progress.
    private readonly HashSet<int> _completed = new();

    // How many cards ahead a struggled card is re-queued within the session.
    private const int AgainOffset = 3;   // wrong: comes back soon
    private const int HardOffset  = 7;   // shaky: comes back a bit later

    // ── Bindable properties ──────────────────────────────────────────────────

    private Card?  _current;
    public  Card?  CurrentCard { get => _current; private set => Set(ref _current, value); }

    private string _contextText = "";
    public  string  ContextText { get => _contextText; set => Set(ref _contextText, value); }

    private string _sentenceDe = "";
    public  string  SentenceDe { get => _sentenceDe; private set { Set(ref _sentenceDe, value); OnPropertyChanged(nameof(HasSentenceDe)); OnPropertyChanged(nameof(HasDetails)); } }

    private string _wordEn = "";
    public  string  WordEn { get => _wordEn; private set { Set(ref _wordEn, value); OnPropertyChanged(nameof(HasWordEn)); OnPropertyChanged(nameof(HasDetails)); } }

    private string _sentenceEn = "";
    public  string  SentenceEn { get => _sentenceEn; private set { Set(ref _sentenceEn, value); OnPropertyChanged(nameof(HasSentenceEn)); OnPropertyChanged(nameof(HasDetails)); } }

    public bool HasSentenceDe => !string.IsNullOrWhiteSpace(SentenceDe);
    public bool HasWordEn     => !string.IsNullOrWhiteSpace(WordEn);
    public bool HasSentenceEn => !string.IsNullOrWhiteSpace(SentenceEn);
    public bool HasDetails    => HasSentenceDe || HasWordEn || HasSentenceEn;

    private string _hintDisplay = "";
    public  string  HintDisplay { get => _hintDisplay; set => Set(ref _hintDisplay, value); }

    private string _answerText = "";
    public  string  AnswerText { get => _answerText; set => Set(ref _answerText, value); }

    private string _userInput = "";
    public  string  UserInput  { get => _userInput;  set { Set(ref _userInput, value); OnPropertyChanged(nameof(CanCheck)); } }

    private bool _isAnswerShown;
    public  bool  IsAnswerShown { get => _isAnswerShown; private set { Set(ref _isAnswerShown, value); OnPropertyChanged(nameof(IsTypingMode)); } }

    private bool _isCorrect;
    public  bool  IsCorrect { get => _isCorrect; set => Set(ref _isCorrect, value); }

    private bool _resultShown;
    public  bool  ResultShown { get => _resultShown; set => Set(ref _resultShown, value); }

    private int _score;
    public  int  Score { get => _score; private set => Set(ref _score, value); }

    private int _done;
    public  int  Done { get => _done; private set => Set(ref _done, value); }

    private int _total;
    public  int  Total { get => _total; private set => Set(ref _total, value); }

    private bool _isFinished;
    public  bool  IsFinished { get => _isFinished; private set => Set(ref _isFinished, value); }

    public bool IsTypingMode => !IsAnswerShown;
    public bool CanCheck     => !string.IsNullOrWhiteSpace(UserInput) && !IsAnswerShown;
    public bool HasAudio     => CurrentCard?.AudioFile != null && File.Exists(CurrentCard.AudioFile);

    // Progress 0..1 for progress bar
    public double Progress => Total == 0 ? 0 : (double)Done / Total;

    public StudyViewModel(DatabaseService db) => _db = db;

    // ── Public actions ───────────────────────────────────────────────────────

    public void LoadDeck(int deckId)
    {
        _queue = _db.GetDueCards(deckId, maxNew: 20);
        Total  = _queue.Count;      // distinct cards to master this session
        Done   = 0;
        Score  = 0;
        _index = 0;
        _completed.Clear();
        ShowCard();
    }

    // In spell mode we speak just the WORD (the deck's own AudioFile is a whole
    // sentence, which mismatches a single-word answer). Word audio is fetched via
    // TTS and cached; if TTS is unavailable we fall back to the recorded sentence.
    public async void PlayAudio()
    {
        var word = CurrentCard?.FrontText;
        if (string.IsNullOrWhiteSpace(word))
        {
            if (HasAudio) _audio.Play(CurrentCard!.AudioFile!);
            return;
        }
        var path = await _tts.GetAudioPathAsync(word, "de");
        if (path != null) _audio.Play(path);
        else if (HasAudio) _audio.Play(CurrentCard!.AudioFile!); // offline fallback
    }

    // Play the recorded example-sentence audio bundled with the deck.
    public void PlaySentenceAudio()
    {
        if (HasAudio) _audio.Play(CurrentCard!.AudioFile!);
    }

    // Speak arbitrary text (used by the per-row 🔊 buttons). lang: "de" or "en".
    public async void Speak(string? text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var path = await _tts.GetAudioPathAsync(text, lang);
        if (path != null) _audio.Play(path);
    }

    public void ShowHint()
    {
        if (CurrentCard == null || IsAnswerShown) return;
        _hintUsed = true;
        var word = CurrentCard.FrontText;
        // Reveal ~1/3 of unrevealed letter positions each press
        var unrevealed = Enumerable.Range(0, word.Length)
            .Where(i => !char.IsWhiteSpace(word[i]) && !_revealed.Contains(i))
            .OrderBy(_ => _rng.Next())
            .ToList();
        int reveal = Math.Max(1, (int)Math.Ceiling(word.Length / 3.0));
        foreach (var i in unrevealed.Take(reveal))
            _revealed.Add(i);
        HintDisplay = BuildHint();
    }

    public void ShowAnswer()
    {
        if (CurrentCard == null) return;
        IsAnswerShown = true;
        AnswerText    = CurrentCard.FrontText;
        HintDisplay   = "";
        ResultShown   = false;
    }

    public bool CheckAnswer()
    {
        if (CurrentCard == null) return false;
        var correct = string.Equals(
            UserInput.Trim(), CurrentCard.FrontText.Trim(),
            StringComparison.OrdinalIgnoreCase);

        IsAnswerShown = true;
        AnswerText    = CurrentCard.FrontText;
        HintDisplay   = "";
        IsCorrect     = correct;
        ResultShown   = true;

        if (correct) Score += _hintUsed ? 1 : 3;
        return correct;
    }

    public void Grade(ReviewGrade grade)
    {
        if (CurrentCard == null) return;
        var card = CurrentCard;

        // Cross-session scheduling (SM-2): due date / interval / ease persist.
        _srs.ApplyGrade(card, grade);
        _db.UpdateCard(card);

        // Intra-session repetition, Anki-style: struggled cards recur within this
        // sitting so hard words get seen more often. Again comes back sooner than
        // Hard; Good/Easy graduate the card out of the session.
        if (grade == ReviewGrade.Again || grade == ReviewGrade.Hard)
        {
            RequeueCurrent(grade == ReviewGrade.Again ? AgainOffset : HardOffset);
        }
        else
        {
            _completed.Add(card.Id);
            _index++;
        }

        Done = _completed.Count;
        OnPropertyChanged(nameof(Progress));
        ShowCard();
    }

    // Move the current card forward `offset` positions (clamped to the queue end)
    // so it reappears later in this session instead of being skipped until reopen.
    private void RequeueCurrent(int offset)
    {
        var card = _queue[_index];
        _queue.RemoveAt(_index);
        int target = Math.Min(_index + offset, _queue.Count);
        _queue.Insert(target, card);
        // _index stays put: it now points at the next distinct card.
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void ShowCard()
    {
        if (_index >= _queue.Count)
        {
            IsFinished = true;
            CurrentCard = null;
            return;
        }
        CurrentCard   = _queue[_index];
        ContextText   = CurrentCard.BackText;
        SentenceDe    = CurrentCard.SentenceDe;
        WordEn        = CurrentCard.WordEn;
        SentenceEn    = CurrentCard.SentenceEn;
        UserInput     = "";
        IsAnswerShown = false;
        AnswerText    = "";
        ResultShown   = false;
        _hintUsed     = false;
        _revealed     = new HashSet<int>();
        HintDisplay   = BuildHint();
        OnPropertyChanged(nameof(HasAudio));

        // Spell mode: auto-speak the WORD only (not the sentence audio).
        PlayAudio();
    }

    private string BuildHint()
    {
        if (CurrentCard == null) return "";
        var word = CurrentCard.FrontText;
        var sb   = new StringBuilder();
        for (int i = 0; i < word.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(char.IsWhiteSpace(word[i]) ? ' '
                    : _revealed.Contains(i)      ? word[i]
                    : '_');
        }
        return sb.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _audio.Dispose();
        base.Dispose(disposing);
    }
}
