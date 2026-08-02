using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using LingoFluence.Models;
using LingoFluence.Services;
using LingoFluence.ViewModels;

namespace LingoFluence.Views;

public partial class StudyWindow : Window
{
    private readonly StudyViewModel _vm;

    public StudyWindow(int deckId, string deckTitle)
    {
        InitializeComponent();
        Title = $"Study — {deckTitle}";
        _vm = new StudyViewModel(new DatabaseService());
        DataContext = _vm;

        // Bind progress bar manually (Progress is 0..1 double)
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StudyViewModel.Progress))
                ProgressBar.Value = _vm.Progress;
        };

        Loaded += (_, _) =>
        {
            _vm.LoadDeck(deckId);
            InputBox.Focus();
        };
    }

    // ── Button handlers ─────────────────────────────────────────────────────

    private void AudioBtn_Click(object sender, RoutedEventArgs e) => _vm.PlayAudio();

    private void SentenceAudioBtn_Click(object sender, RoutedEventArgs e) => _vm.PlaySentenceAudio();

    private void SpeakDe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string t) _vm.Speak(t, "de");
    }

    private void SpeakEn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string t) _vm.Speak(t, "en");
    }

    private void HintBtn_Click(object sender, RoutedEventArgs e)
    {
        _vm.ShowHint();
        InputBox.Focus();
    }

    private void ShowAnswerBtn_Click(object sender, RoutedEventArgs e) => _vm.ShowAnswer();

    private void CheckBtn_Click(object sender, RoutedEventArgs e)
    {
        _vm.CheckAnswer();
    }

    private void Again_Click(object sender, RoutedEventArgs e) => Grade(ReviewGrade.Again);
    private void Hard_Click(object sender, RoutedEventArgs e)  => Grade(ReviewGrade.Hard);
    private void Good_Click(object sender, RoutedEventArgs e)  => Grade(ReviewGrade.Good);
    private void Easy_Click(object sender, RoutedEventArgs e)  => Grade(ReviewGrade.Easy);

    private void Grade(ReviewGrade grade)
    {
        _vm.Grade(grade);
        if (!_vm.IsFinished)
            InputBox.Focus();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // Copy a detail row's text (passed via the button's Tag) to the clipboard,
    // then confirm with a brief toast: 'Copied "…"'.
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string text && !string.IsNullOrEmpty(text))
        {
            try
            {
                Clipboard.SetText(text);
                var shown = text.Length > 40 ? text[..40] + "…" : text;
                ShowToast($"Copied \"{shown}\"");
            }
            catch { /* clipboard may be locked by another app */ }
        }
    }

    // Fade a small confirmation toast in, hold, then fade out.
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;

        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1450))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1750))));
        fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    // ── Keyboard shortcuts ───────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Space/F5 = replay audio
        if (e.Key == Key.F5 || (e.Key == Key.Space && InputBox.IsVisible && InputBox.Text.Length == 0))
        {
            _vm.PlayAudio();
            e.Handled = true;
            return;
        }
        // F1 = hint
        if (e.Key == Key.F1 && !_vm.IsAnswerShown)
        {
            _vm.ShowHint();
            InputBox.Focus();
            e.Handled = true;
            return;
        }
        // F2 / Escape = show answer
        if ((e.Key == Key.F2 || e.Key == Key.Escape) && !_vm.IsAnswerShown)
        {
            _vm.ShowAnswer();
            e.Handled = true;
            return;
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter = check (if typing) or Good grade (if answer shown)
        if (e.Key != Key.Enter) return;
        if (!_vm.IsAnswerShown && _vm.CanCheck)
        {
            _vm.CheckAnswer();
            e.Handled = true;
        }
        else if (_vm.IsAnswerShown && !_vm.IsFinished)
        {
            Grade(ReviewGrade.Good); // Enter = "Good" as a quick shortcut
            e.Handled = true;
        }
    }
}
