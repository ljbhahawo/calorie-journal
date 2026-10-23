using System.Windows;
using System.Windows.Input;

namespace CalorieJournal.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        MainTabs.Items.Remove(SettingsTab);
        MainTabs.Items.Add(SettingsTab);
        DataContext = viewModel;
        Activated += (_, _) => viewModel.RefreshClockDate();
        Closing += (_, e) => { if (viewModel.IsBusy || !viewModel.CanClose()) e.Cancel = true; };
    }
    private void DirectEnergyClicked(object sender, MouseButtonEventArgs e) => ((MainViewModel)DataContext).EnterDirectEnergy();
    private void DirectEnergyFocused(object sender, KeyboardFocusChangedEventArgs e) => ((MainViewModel)DataContext).EnterDirectEnergy();
}
