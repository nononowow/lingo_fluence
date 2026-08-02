using System;
using System.Windows;
using System.Windows.Media.Animation;
using LingoFluence.Services;
using LingoFluence.ViewModels;

namespace LingoFluence.Views;

public partial class AiStudyWindow : System.Windows.Window
{
    private readonly AiStudyViewModel _vm;

    public AiStudyWindow(int deckId, string deckName)
    {
        InitializeComponent();
        _vm = new AiStudyViewModel(new DatabaseService());
        DataContext = _vm;
        _vm.LoadDeck(deckId, deckName);
    }

    private void SpeakGerman_Click(object sender, System.Windows.RoutedEventArgs e)
        => _vm.Speak(_vm.GermanText, "de");

    private void SpeakDE_Click(object sender, System.Windows.RoutedEventArgs e)
        => _vm.Speak((sender as System.Windows.Controls.Button)?.Tag?.ToString(), "de");

    private void SpeakEN_Click(object sender, System.Windows.RoutedEventArgs e)
        => _vm.Speak((sender as System.Windows.Controls.Button)?.Tag?.ToString(), "en");

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

    // Copy a field's text (passed via the button's Tag) to the clipboard,
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
}
