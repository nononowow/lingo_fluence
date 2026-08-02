using System.Windows.Input;
using LingoFluence.ViewModels;

namespace LingoFluence.Views;

public partial class AiWindow : System.Windows.Window
{
    private readonly AiViewModel _vm;

    public AiWindow()
    {
        InitializeComponent();
        _vm = (AiViewModel)DataContext;

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
