using System.Collections.ObjectModel;
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
    private readonly PhoneticService         _phon   = new();

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
    private string _exampleZh   = "";
    private string _ipaText     = "";

    public string GermanText  { get => _germanText;  private set { Set(ref _germanText, value); OnPropertyChanged(nameof(StoryTitleDe)); OnPropertyChanged(nameof(TitleCopyText)); OnPropertyChanged(nameof(StoryCopyText)); } }
    public string EnglishText { get => _englishText; private set => Set(ref _englishText, value); }
    public string ChineseText { get => _chineseText; private set { Set(ref _chineseText, value); OnPropertyChanged(nameof(HasChinese)); OnPropertyChanged(nameof(CanFetchChinese)); } }
    public string GrammarText { get => _grammarText; private set => Set(ref _grammarText, value); }
    public string ExampleDe   { get => _exampleDe;   private set => Set(ref _exampleDe,   value); }
    public string ExampleEn   { get => _exampleEn;   private set => Set(ref _exampleEn,   value); }
    public string ExampleZh   { get => _exampleZh;   private set { Set(ref _exampleZh, value); OnPropertyChanged(nameof(HasExampleZh)); OnPropertyChanged(nameof(CanFetchExampleZh)); } }
    public string IpaText     { get => _ipaText;     private set { Set(ref _ipaText, value); OnPropertyChanged(nameof(HasIpa)); OnPropertyChanged(nameof(CanFetchIpa)); } }

    private bool _isFetchingChinese;
    public bool IsFetchingChinese
    {
        get => _isFetchingChinese;
        private set { Set(ref _isFetchingChinese, value); OnPropertyChanged(nameof(CanFetchChinese)); }
    }

    private bool _isFetchingExampleZh;
    public bool IsFetchingExampleZh
    {
        get => _isFetchingExampleZh;
        private set { Set(ref _isFetchingExampleZh, value); OnPropertyChanged(nameof(CanFetchExampleZh)); }
    }

    private bool _isFetchingIpa;
    public bool IsFetchingIpa
    {
        get => _isFetchingIpa;
        private set { Set(ref _isFetchingIpa, value); OnPropertyChanged(nameof(CanFetchIpa)); }
    }

    public bool HasChinese   => !string.IsNullOrWhiteSpace(ChineseText);
    // Offer the fetch button only when flipped, this card lacks Chinese, and none is in flight.
    public bool CanFetchChinese => IsFlipped && !HasChinese && !IsFetchingChinese;
    public bool HasGrammar   => !string.IsNullOrWhiteSpace(GrammarText);
    public bool HasExampleDe => !string.IsNullOrWhiteSpace(ExampleDe);
    public bool HasExampleEn => !string.IsNullOrWhiteSpace(ExampleEn);
    public bool HasExampleZh => !string.IsNullOrWhiteSpace(ExampleZh);
    // Offer the sentence-fetch button only when flipped, there is a German sentence,
    // it lacks a Chinese translation, and none is in flight.
    public bool CanFetchExampleZh => IsFlipped && HasExampleDe && !HasExampleZh && !IsFetchingExampleZh;

    public bool HasIpa => !string.IsNullOrWhiteSpace(IpaText);
    // Offer the phonetic button (beside Speak, both faces) when this word lacks an
    // IPA transcription and none is in flight. Never for a story card — its face is a
    // title, not a single word, so Wiktionary has nothing to return.
    public bool CanFetchIpa => !IsStoryCard && !HasIpa && !IsFetchingIpa;

    // ── Story topic (title) ───────────────────────────────────────────────────
    // The topic line needs the same affordances as every other German text in the
    // app: speak it, copy it, translate it. The stored English field is metadata
    // ("Story · A1 · 15 sentences"), not a translation, so translations are fetched
    // on demand and cached on the note.

    private string _titleEn = "";
    private string _titleZh = "";

    /// <summary>
    /// The topic without the 📖 marker — what gets spoken, copied and translated.
    /// Strips any leading non-letter run, so a marker stored without its trailing
    /// space (or with a stray variation selector) never reaches the TTS engine.
    /// </summary>
    public string StoryTitleDe
    {
        get
        {
            if (!IsStoryCard) return "";
            var s = GermanText.Replace(AiService.StoryPrefix, "").Trim();
            var i = 0;
            while (i < s.Length && !char.IsLetterOrDigit(s[i]) && !char.IsPunctuation(s[i])) i++;
            return s[i..].Trim();
        }
    }

    public string TitleEn
    {
        get => _titleEn;
        private set { Set(ref _titleEn, value); OnPropertyChanged(nameof(HasTitleTranslation)); OnPropertyChanged(nameof(TitleTranslation)); OnPropertyChanged(nameof(CanFetchTitle)); OnPropertyChanged(nameof(TitleCopyText)); OnPropertyChanged(nameof(StoryCopyText)); }
    }

    public string TitleZh
    {
        get => _titleZh;
        private set { Set(ref _titleZh, value); OnPropertyChanged(nameof(HasTitleTranslation)); OnPropertyChanged(nameof(TitleTranslation)); OnPropertyChanged(nameof(CanFetchTitle)); OnPropertyChanged(nameof(TitleCopyText)); OnPropertyChanged(nameof(StoryCopyText)); }
    }

    private bool _isFetchingTitle;
    public bool IsFetchingTitle
    {
        get => _isFetchingTitle;
        private set { Set(ref _isFetchingTitle, value); OnPropertyChanged(nameof(CanFetchTitle)); }
    }

    /// <summary>Both translations on one line, e.g. "My Morning · 我的早晨".</summary>
    public string TitleTranslation => Join(" · ", TitleEn, TitleZh);

    public bool HasTitleTranslation => !string.IsNullOrWhiteSpace(TitleTranslation);

    /// <summary>Offered on a story card that still lacks a topic translation.</summary>
    public bool CanFetchTitle => IsStoryCard && !HasTitleTranslation && !IsFetchingTitle;

    /// <summary>Topic copy: the German line plus whatever translations exist.</summary>
    public string TitleCopyText => Join("\n", StoryTitleDe, TitleEn, TitleZh);

    /// <summary>Joins non-empty parts with a separator.</summary>
    private static string Join(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    // ── Story cards ───────────────────────────────────────────────────────────

    private bool _isStoryCard;
    /// <summary>
    /// True for the full-text card of a generated reading story (German field is
    /// "📖 Title", ExampleDe holds the whole paragraph). Drives a reading-oriented
    /// layout: bigger body text, no IPA lookup, a stop-playback control.
    /// </summary>
    public bool IsStoryCard
    {
        get => _isStoryCard;
        private set
        {
            Set(ref _isStoryCard, value);
            OnPropertyChanged(nameof(IsWordCard));
            OnPropertyChanged(nameof(IsFrontWord));
            OnPropertyChanged(nameof(IsFrontStory));
            OnPropertyChanged(nameof(IsBackWord));
            OnPropertyChanged(nameof(IsBackStory));
            OnPropertyChanged(nameof(RevealText));
            OnPropertyChanged(nameof(CanFetchIpa));
            OnPropertyChanged(nameof(StoryTitleDe));
            OnPropertyChanged(nameof(TitleCopyText));
            OnPropertyChanged(nameof(CanFetchTitle));
        }
    }
    public bool IsWordCard => !IsStoryCard;

    /// <summary>
    /// The story's sentences, one row each. Empty for word cards.
    /// </summary>
    public ObservableCollection<StoryLine> StoryLines { get; } = new();

    /// <summary>
    /// Splits the three newline-separated blocks a story card stores (see
    /// AiService.FlattenStory) back into aligned per-sentence rows. Translation
    /// blocks may be shorter than the German one, so missing entries fall back
    /// to empty rather than throwing.
    /// </summary>
    private void BuildStoryLines(Card c)
    {
        StoryLines.Clear();
        if (!IsStoryCard)
        {
            StoryParagraph = "";
            OnPropertyChanged(nameof(HasStoryLines));
            OnPropertyChanged(nameof(StoryCopyText));
            return;
        }

        var sections = ParseSections(c.BackText);
        StoryParagraph = sections.TryGetValue(AiService.TextMarker, out var t)
            ? string.Join(" ", t)
            : "";

        // Blank lines are KEPT here: a sentence the model left untranslated writes an
        // empty line, and dropping it would shift every later translation up by one.
        static string[] Split(string s) => string.IsNullOrEmpty(s)
            ? []
            : s.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).ToArray();

        // The German column is the row count, so its blanks are the only ones removed.
        var de = Split(c.SentenceDe).Where(x => x.Length > 0).ToArray();
        var en = Split(c.SentenceEn);
        var zh = Split(c.Chinese);
        var gr = Split(c.WordEn);   // structure notes, index-aligned with `de`

        for (var i = 0; i < de.Length; i++)
        {
            // Per-sentence rows are tagged W (word) or P (phrase) in one ⟦SENT:n⟧ section.
            List<StoryEntry> words = [], phr = [];
            if (sections.TryGetValue($"{AiService.SentenceMarkerPrefix}{i + 1}⟧", out var rows))
                foreach (var row in rows)
                {
                    var p = row.Split('|');
                    if (p.Length < 2 || p[1].Trim().Length == 0) continue;
                    var entry = new StoryEntry(
                        p[1].Trim(),
                        p.Length > 2 ? p[2].Trim() : "",
                        p.Length > 3 ? p[3].Trim() : "",
                        p.Length > 4 ? p[4].Trim() : "");
                    (p[0].Trim() == "P" ? phr : words).Add(entry);
                }

            StoryLines.Add(new StoryLine(
                i + 1,
                de[i],
                i < en.Length ? en[i] : "",
                i < zh.Length ? zh[i] : "",
                i < gr.Length ? gr[i] : "",
                words,
                phr));
        }

        OnPropertyChanged(nameof(HasStoryLines));
        OnPropertyChanged(nameof(StoryCopyText));
    }

    /// <summary>
    /// The human-readable part of a story card's English field: everything before the
    /// first packed section marker (e.g. "Story · A1 · 8 sentences").
    /// </summary>
    private static string SummaryOf(string backText)
    {
        if (string.IsNullOrEmpty(backText)) return "";
        // Any marker ends the summary, so a section added later can't leak into the UI.
        var i = backText.IndexOf('⟦');
        return (i < 0 ? backText : backText[..i]).Trim();
    }

    /// <summary>Vocabulary rows parsed from the card's ⟦VOCAB⟧ section.</summary>
    public ObservableCollection<StoryEntry> StoryVocab { get; } = new();

    /// <summary>Phrase rows parsed from the card's ⟦PHRASES⟧ section.</summary>
    public ObservableCollection<StoryEntry> StoryPhrases { get; } = new();

    public bool HasStoryVocab   => StoryVocab.Count > 0;
    public bool HasStoryPhrases => StoryPhrases.Count > 0;

    /// <summary>
    /// Unpacks the "de | pos | en | zh" lines that AiService.StoryToCards appended to
    /// the English field under its section markers. Everything before the first marker
    /// is the human-readable summary and is ignored here.
    /// </summary>
    private void BuildStoryEntries(Card c)
    {
        StoryVocab.Clear();
        StoryPhrases.Clear();

        if (IsStoryCard && !string.IsNullOrEmpty(c.BackText))
        {
            var sections = ParseSections(c.BackText);
            Fill(StoryVocab,   AiService.VocabMarker);
            Fill(StoryPhrases, AiService.PhraseMarker);

            void Fill(ObservableCollection<StoryEntry> target, string marker)
            {
                if (!sections.TryGetValue(marker, out var rows)) return;
                foreach (var line in rows)
                {
                    var p = line.Split('|');
                    var word = p[0].Trim();
                    if (word.Length == 0) continue;
                    target.Add(new StoryEntry(
                        word,
                        p.Length > 1 ? p[1].Trim() : "",
                        p.Length > 2 ? p[2].Trim() : "",
                        p.Length > 3 ? p[3].Trim() : ""));
                }
            }
        }

        OnPropertyChanged(nameof(HasStoryVocab));
        OnPropertyChanged(nameof(HasStoryPhrases));
    }

    /// <summary>
    /// Splits a story card's packed English field into its marked sections, keyed by the
    /// marker line itself. Everything before the first marker is the human-readable
    /// summary and is dropped. Unknown markers are tolerated, so a card written by a
    /// newer build degrades to missing sections rather than mangled rows.
    /// </summary>
    private static Dictionary<string, List<string>> ParseSections(string backText)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(backText)) return result;

        List<string>? current = null;
        foreach (var raw in backText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('⟦') && line.EndsWith('⟧'))
            {
                current = result.TryGetValue(line, out var existing) ? existing : (result[line] = []);
                continue;
            }
            if (current is null || line.Length == 0) continue;
            current.Add(line);
        }
        return result;
    }

    /// <summary>
    /// The whole German story as one block, for full-paragraph reading and TTS. Empty on
    /// word cards and on story cards written before the ⟦TEXT⟧ section existed.
    /// </summary>
    public string StoryParagraph
    {
        get => _storyParagraph;
        private set
        {
            if (_storyParagraph == value) return;
            _storyParagraph = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStoryParagraph));
            OnPropertyChanged(nameof(StoryCopyText));
        }
    }
    private string _storyParagraph = "";

    public bool HasStoryParagraph => !string.IsNullOrWhiteSpace(StoryParagraph);

    private static string FirstNonEmpty(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a;

    private static string PackedTitle(Dictionary<string, List<string>> sections, string marker) =>
        sections.TryGetValue(marker, out var rows) && rows.Count > 0 ? rows[0] : "";

    public bool HasStoryLines => StoryLines.Count > 0;

    /// <summary>
    /// Whole story for the "Copy story" button, in the order it is displayed: the
    /// trilingual heading, the full German paragraph, then each sentence with its
    /// translations, grammar note and own word/phrase lists.
    /// </summary>
    public string StoryCopyText
    {
        get
        {
            var parts = new List<string>();
            var heading = Join(" / ", StoryTitleDe, TitleEn, TitleZh);
            if (heading.Length > 0) parts.Add(heading);
            if (HasStoryParagraph) parts.Add(StoryParagraph);
            parts.AddRange(StoryLines.Select(l => l.CopyText));
            return string.Join("\n\n", parts);
        }
    }


    /// <summary>
    /// Reveal-button label. A story card's back holds translations rather than a
    /// hidden answer, so the prompt says so instead of "Reveal Answer".
    /// </summary>
    public string RevealText => IsStoryCard ? "🔄  Show translation" : "🔄  Reveal Answer";

    // ── Flip + progress ───────────────────────────────────────────────────────

    private bool _isFlipped;
    private bool _isFinished;
    private int  _done;
    private int  _total;

    public bool IsFlipped
    {
        get => _isFlipped;
        private set { Set(ref _isFlipped, value); OnPropertyChanged(nameof(IsFront)); OnPropertyChanged(nameof(IsFrontWord)); OnPropertyChanged(nameof(IsFrontStory)); OnPropertyChanged(nameof(IsBackWord)); OnPropertyChanged(nameof(IsBackStory)); OnPropertyChanged(nameof(CanGrade)); OnPropertyChanged(nameof(CanFetchChinese)); OnPropertyChanged(nameof(CanFetchExampleZh)); }
    }
    public bool IsFront  => !IsFlipped;
    // Each face has two layouts: a single word, or a per-sentence story list.
    public bool IsFrontWord  => IsFront   && IsWordCard;
    public bool IsFrontStory => IsFront   && IsStoryCard;
    public bool IsBackWord   => IsFlipped && IsWordCard;
    public bool IsBackStory  => IsFlipped && IsStoryCard;
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
    public ICommand FetchExampleZhCommand { get; }
    public ICommand FetchIpaCommand { get; }
    public ICommand FetchTitleCommand { get; }

    public string DeckTitle { get; private set; } = "";

    public AiStudyViewModel(DatabaseService db)
    {
        _db = db;
        FlipCommand       = new RelayCommand(_ => Flip(),                        _ => IsFront && !IsFinished);
        GradeAgainCommand = new RelayCommand(_ => Grade(ReviewGrade.Again), _ => CanGrade);
        GradeGoodCommand  = new RelayCommand(_ => Grade(ReviewGrade.Good),  _ => CanGrade);
        FetchChineseCommand = new RelayCommand(async _ => await FetchChineseAsync(), _ => CanFetchChinese);
        FetchExampleZhCommand = new RelayCommand(async _ => await FetchExampleZhAsync(), _ => CanFetchExampleZh);
        FetchIpaCommand = new RelayCommand(async _ => await FetchIpaAsync(), _ => CanFetchIpa);
        FetchTitleCommand = new RelayCommand(async _ => await FetchTitleAsync(), _ => CanFetchTitle);
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

    /// <summary>
    /// Speak arbitrary text; lang = "de" or "en". Text longer than one TTS request
    /// (a story paragraph) is split at sentence boundaries and played back to back,
    /// so a whole story reads continuously instead of cutting off at ~200 chars.
    /// </summary>
    public async void Speak(string? text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var paths = await _tts.GetAudioPathsAsync(text, lang);
        if (paths.Count == 1) _audio.Play(paths[0]);
        else if (paths.Count > 1) _audio.PlaySequence(paths);
    }

    /// <summary>Stops any in-progress playback — useful mid-story.</summary>
    public void StopSpeaking() => _audio.Stop();

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

    /// <summary>
    /// Fetches a Chinese translation of the German example sentence on demand,
    /// shows it, and writes it back to the note so it's cached permanently.
    /// </summary>
    private async Task FetchExampleZhAsync()
    {
        if (_index >= _queue.Count) return;
        var card = _queue[_index];
        if (string.IsNullOrWhiteSpace(card.SentenceDe)) return;
        IsFetchingExampleZh = true;
        try
        {
            var zh = await _trans.TranslateAsync(card.SentenceDe, target: "zh-CN", source: "de");
            if (string.IsNullOrWhiteSpace(zh))
            {
                // Leave ExampleZh empty so the button stays and the user can retry.
                System.Windows.MessageBox.Show(
                    "翻译失败，请检查网络或代理后重试。",
                    "获取中文", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            card.SentenceZh = zh;
            ExampleZh       = zh;
            _db.UpdateNoteSentenceZh(card.NoteId, zh);
        }
        finally
        {
            IsFetchingExampleZh = false;
        }
    }

    /// <summary>
    /// Fetches English and Chinese translations of a story's topic on demand, shows
    /// them, and caches both on the note. One button covers both languages because a
    /// title is short and the user wants the pair, not one at a time. A partial result
    /// is kept — whichever language came back is better than nothing, and the button
    /// stays available while either is still missing so the rest can be retried.
    /// </summary>
    private async Task FetchTitleAsync()
    {
        if (_index >= _queue.Count) return;
        var card = _queue[_index];
        var de = StoryTitleDe;
        if (string.IsNullOrWhiteSpace(de)) return;
        IsFetchingTitle = true;
        try
        {
            var en = string.IsNullOrWhiteSpace(TitleEn)
                ? await _trans.TranslateAsync(de, target: "en", source: "de")
                : TitleEn;
            var zh = string.IsNullOrWhiteSpace(TitleZh)
                ? await _trans.TranslateAsync(de, target: "zh-CN", source: "de")
                : TitleZh;

            if (string.IsNullOrWhiteSpace(en) && string.IsNullOrWhiteSpace(zh))
            {
                System.Windows.MessageBox.Show(
                    "翻译失败，请检查网络或代理后重试。",
                    "获取翻译", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            TitleEn = en?.Trim() ?? "";
            TitleZh = zh?.Trim() ?? "";
            card.TitleEn = TitleEn;
            card.TitleZh = TitleZh;
            _db.UpdateNoteTitleTranslations(card.NoteId, TitleEn, TitleZh);
        }
        finally
        {
            IsFetchingTitle = false;
        }
    }

    /// <summary>
    /// Fetches the IPA phonetic transcription of the current German word on demand,
    /// shows it, and writes it back to the note so it's cached permanently.
    /// </summary>
    private async Task FetchIpaAsync()
    {
        if (_index >= _queue.Count) return;
        var card = _queue[_index];
        if (string.IsNullOrWhiteSpace(card.FrontText)) return;
        IsFetchingIpa = true;
        try
        {
            var ipa = await _phon.GetIpaAsync(card.FrontText);
            if (string.IsNullOrWhiteSpace(ipa))
            {
                // Leave IpaText empty so the button stays and the user can retry.
                System.Windows.MessageBox.Show(
                    "未找到音标，请检查网络或代理后重试。",
                    "音标", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            card.Ipa = ipa;
            IpaText  = ipa;
            _db.UpdateNoteIpa(card.NoteId, ipa);
        }
        finally
        {
            IsFetchingIpa = false;
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
        _audio.Stop();   // don't let the previous card's audio bleed into this one
        var c = _queue[_index];
        var isStory = c.FrontText.StartsWith(AiService.StoryPrefix, StringComparison.Ordinal);
        GermanText  = c.FrontText;
        // A story card packs its vocab/phrase tables into BackText and its per-sentence
        // structure notes into WordEn; both render as dedicated sections, so the plain
        // text properties keep only the parts meant to be shown verbatim.
        EnglishText = isStory ? SummaryOf(c.BackText) : c.BackText;
        ChineseText = isStory ? "" : c.Chinese;
        GrammarText = isStory ? "" : c.WordEn;
        ExampleDe   = isStory ? "" : c.SentenceDe;
        ExampleEn   = isStory ? "" : c.SentenceEn;
        ExampleZh   = isStory ? "" : c.SentenceZh;
        IpaText     = c.Ipa;
        IsStoryCard = isStory;
        // A stored translation (from the 🌐 fetch) wins; otherwise use the one the
        // generator supplied with the story, so titles start out trilingual.
        var packed = isStory ? ParseSections(c.BackText) : [];
        TitleEn = FirstNonEmpty(c.TitleEn, PackedTitle(packed, AiService.TitleEnMarker));
        TitleZh = FirstNonEmpty(c.TitleZh, PackedTitle(packed, AiService.TitleZhMarker));
        BuildStoryLines(c);
        BuildStoryEntries(c);
        IsFlipped   = false;
        IsFinished  = false;
        OnPropertyChanged(nameof(HasChinese));
        OnPropertyChanged(nameof(HasGrammar));
        OnPropertyChanged(nameof(HasExampleDe));
        OnPropertyChanged(nameof(HasExampleEn));
        OnPropertyChanged(nameof(HasExampleZh));
        OnPropertyChanged(nameof(HasIpa));
        // Auto-play the word, but not a whole story — the user starts a story read
        // themselves so it isn't triggered just by scrolling past the card.
        if (!IsStoryCard) Speak(GermanText, "de");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _audio.Dispose();
        base.Dispose(disposing);
    }
}
