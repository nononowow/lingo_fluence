using System.Windows.Input;
using LingoFluence.ViewModels;

namespace LingoFluence.Views;

public partial class AiWindow : System.Windows.Window
{
    private readonly AiViewModel _vm;

    public AiWindow() : this(-1, null) { }

    /// <summary>
    /// Opens the generator. When deckId &gt; 0 the window loads that AI deck's
    /// conversation and cards for continued editing (Import saves in place).
    /// </summary>
    public AiWindow(int deckId, string? deckName)
    {
        InitializeComponent();
        _vm = (AiViewModel)DataContext;

        if (deckId > 0 && deckName != null)
            _vm.LoadForEdit(deckId, deckName);

        // Auto-scroll chat log whenever messages are added
        _vm.ChatMessages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() =>
                ChatScroller.ScrollToBottom(),
                System.Windows.Threading.DispatcherPriority.Background);
    }

    // Enter key in the request box triggers Generate
    private void RequestBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.GenerateCommand.CanExecute(null))
            _vm.GenerateCommand.Execute(null);
    }
}
