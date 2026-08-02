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
}
