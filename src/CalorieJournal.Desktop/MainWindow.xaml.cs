using System.Windows;

namespace CalorieJournal.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Activated += (_, _) => viewModel.RefreshClockDate();
        Closing += (_, e) => { if (viewModel.IsBusy || !viewModel.CanClose()) e.Cancel = true; };
    }
}
