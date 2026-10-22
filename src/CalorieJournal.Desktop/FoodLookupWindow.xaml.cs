using System.Windows;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public partial class FoodLookupWindow : Window
{
    private readonly FoodLookupViewModel viewModel;
    public FoodReferenceHit? Chosen { get; private set; }
    public FoodLookupWindow(FoodLookupViewModel viewModel)
    {
        InitializeComponent(); this.viewModel = viewModel; DataContext = viewModel;
        Closed += (_, _) => viewModel.CancelUpdate();
    }
    private void ChooseFood(object sender, RoutedEventArgs args)
    {
        if (viewModel.IsBusy) return;
        if (viewModel.Selected is null) { viewModel.Status = "请先选择一条资料。"; return; }
        Chosen = viewModel.Selected; Close();
    }
    private void CloseWindow(object sender, RoutedEventArgs args) => Close();
}
