using System.Windows.Input;
using LingoFluence.Models;
using LingoFluence.Services;

namespace LingoFluence.ViewModels;

/// <summary>
/// ViewModel for the AI flip-card study window.
/// No spelling required — user flips to reveal meaning, then grades.
/// </summary>
public class AiStudyViewModel : BaseViewModel
{
    private readonly DatabaseService         _db;
    private readonly SpacedRepetitionService _srs    = new();
    private readonly AudioService            _audio  = new();
    private readonly TtsService              _tts    = new();
    private readonly TranslationService      _trans  = new();

    private List<Card>    _queue     = new();
    private int           _index;
    private readonly HashSet<int> _completed = new();
    private const int AgainOffset = 3;

    // ── Card face ─────────────────────────────────────────────────────────────

    private string _germanText  = "";
    private string _englishText = "";
    private string _chineseText = "";
    private string _grammarText = "";
    private string _exampleDe   = "";
    private string _exampleEn   = "";

    public string GermanText  { get => _germanText;  private set => Set(ref _germanText,  value); }
    public string EnglishText { get => _englishText; private set => Set(ref _englishText, value); }
    public string ChineseText { get => _chineseText; private set { Set(ref _chineseText, value); OnPropertyChanged(nameof(HasChinese)); OnPropertyChanged(nameof(CanFetchChinese)); } }
    public string GrammarText { get => _grammarText; private set => Set(ref _grammarText, value); }
    public string ExampleDe   { get => _exampleDe;   private set => Set(ref _exampleDe,   value); }
    public string ExampleEn   { get => _exampleEn;   private set => Set(ref _exampleEn,   value); }

    private bool _isFetchingChinese;
    public bool IsFetchingChinese
    {
        get => _isFetchingChinese;
        private set { Set(ref _isFetchingChinese, value); OnPropertyChanged(nameof(CanFetchChinese)); }
    }

    public bool HasChinese   => !string.IsNullOrWhiteSpace(ChineseText);
    // Offer the fetch button only when flipped, this card lacks Chinese, and none is in flight.
    public bool CanFetchChinese => IsFlipped && !HasChinese && !IsFetchingChinese;
    public bool HasGrammar   => !string.IsNullOrWhiteSpace(GrammarText);
    public bool HasExampleDe => !string.IsNullOrWhiteSpace(ExampleDe);
    public bool HasExampleEn => !string.IsNullOrWhiteSpace(ExampleEn);

    // ── Flip + progress ───────────────────────────────────────────────────────

    private bool _isFlipped;
    private bool _isFinished;
    private int  _done;
    private int  _total;

    public bool IsFlipped
    {
        get => _isFlipped;
        private set { Set(ref _isFlipped, value); OnPropertyChanged(nameof(IsFront)); OnPropertyChanged(nameof(CanGrade)); OnPropertyChanged(nameof(CanFetchChinese)); }
    }
    public bool IsFront  => !IsFlipped;
    public bool CanGrade => IsFlipped && !IsFinished;

    public bool IsFinished
    {
        get => _isFinished;
        private set { Set(ref _isFinished, value); OnPropertyChanged(nameof(CanGrade)); }
    }
    public int  Done  { get => _done;  private set { Set(ref _done,  value); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); } }
    public int  Total { get => _total; private set { Set(ref _total, value); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); } }

    public double Progress     => Total == 0 ? 0 : (double)Done / Total;
    public string ProgressText => $"{Done} / {Total}";

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand FlipCommand         { get; }
    public ICommand GradeAgainCommand   { get; }
    public ICommand GradeGoodCommand    { get; }
    public ICommand FetchChineseCommand { get; }

    public string DeckTitle { get; private set; } = "";

    public AiStudyViewModel(DatabaseService db)
    {
        _db = db;
        FlipCommand       = new RelayCommand(_ => Flip(),                        _ => IsFront && !IsFinished);
        GradeAgainCommand = new RelayCommand(_ => Grade(ReviewGrade.Again), _ => CanGrade);
        GradeGoodCommand  = new RelayCommand(_ => Grade(ReviewGrade.Good),  _ => CanGrade);
        FetchChineseCommand = new RelayCommand(async _ => await FetchChineseAsync(), _ => CanFetchChinese);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void LoadDeck(int deckId, string deckName)
    {
        DeckTitle = $"{deckName} · AI Study";
        OnPropertyChanged(nameof(DeckTitle));
        _queue = _db.GetDueCards(deckId, maxNew: 50);
        Total  = _queue.Count;
        Done   = 0;
        _index = 0;
        _completed.Clear();
        ShowCard();
    }

    /// <summary>Speak arbitrary text; lang = "de" or "en".</summary>
    public async void Speak(string? text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var path = await _tts.GetAudioPathAsync(text, lang);
        if (path != null) _audio.Play(path);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void Flip() => IsFlipped = true;

    /// <summary>
    /// Fetches a Chinese meaning for the current card on demand (for older AI cards
    /// generated before the Chinese field existed), shows it, and writes it back to
    /// the note so it's cached permanently and never fetched again.
    /// </summary>
    private async Task FetchChineseAsync()
    {
        if (_index >= _queue.Count) return;
        var card = _queue[_index];
        IsFetchingChinese = true;
        try
        {
            // Translate the German word; fall back to the sentence if the word is blank.
            var source = string.IsNullOrWhiteSpace(card.FrontText) ? card.SentenceDe : card.FrontText;
            var zh = await _trans.TranslateAsync(source, target: "zh-CN", source: "de");
            if (string.IsNullOrWhiteSpace(zh))
            {
                // Leave ChineseText empty so the button stays and the user can retry.
                System.Windows.MessageBox.Show(
                    "翻译失败，请检查网络或代理后重试。",
                    "获取中文", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            card.Chinese = zh;
            ChineseText  = zh;
            _db.UpdateNoteChinese(card.NoteId, zh);
        }
        finally
        {
            IsFetchingChinese = false;
        }
    }

    private void Grade(ReviewGrade grade)
    {
        if (_index >= _queue.Count) return;
        var card = _queue[_index];
        _srs.ApplyGrade(card, grade);
        _db.UpdateCard(card);

        if (grade == ReviewGrade.Again)
        {
            _queue.RemoveAt(_index);
            _queue.Insert(Math.Min(_index + AgainOffset, _queue.Count), card);
        }
        else
        {
            _completed.Add(card.Id);
            _index++;
        }
        Done = _completed.Count;
        ShowCard();
    }

    private void ShowCard()
    {
        if (_index >= _queue.Count) { IsFinished = true; return; }
        var c = _queue[_index];
        GermanText  = c.FrontText;
        EnglishText = c.BackText;
        ChineseText = c.Chinese;
        GrammarText = c.WordEn;
        ExampleDe   = c.SentenceDe;
        ExampleEn   = c.SentenceEn;
        IsFlipped   = false;
        IsFinished  = false;
        OnPropertyChanged(nameof(HasChinese));
        OnPropertyChanged(nameof(HasGrammar));
        OnPropertyChanged(nameof(HasExampleDe));
        OnPropertyChanged(nameof(HasExampleEn));
        Speak(GermanText, "de");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _audio.Dispose();
        base.Dispose(disposing);
    }
}
