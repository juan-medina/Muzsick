using Avalonia.Controls;
using Muzsick.ViewModels;

namespace Muzsick.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Pass the window reference to the ViewModel for file dialogs
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SetMainWindow(this);
        }

        // Also handle DataContext changes in case it's set later
        DataContextChanged += (sender, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SetMainWindow(this);
            }
        };
    }
}
