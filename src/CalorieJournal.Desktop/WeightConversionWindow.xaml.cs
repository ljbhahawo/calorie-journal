using System.Windows;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public partial class WeightConversionWindow : Window
{
    private readonly WeightConversionViewModel viewModel;
    private readonly JournalService journal;
    public WeightCalculationSnapshot? Result { get; private set; }
    public WeightConversionWindow(WeightConversionViewModel viewModel, JournalService journal)
    {
        InitializeComponent(); this.viewModel = viewModel; this.journal = journal; DataContext = viewModel;
    }
    private void AddDeduction(object sender, RoutedEventArgs args) => Run(() => viewModel.AddDeduction());
    private void RemoveDeduction(object sender, RoutedEventArgs args) => Run(viewModel.RemoveSelectedDeduction);
    private void ApplyWeight(object sender, RoutedEventArgs args) => Run(() => { DeductionsGrid.CommitEdit(); DeductionsGrid.CommitEdit(); Result = viewModel.CreateSnapshot(); Close(); });
    private void Cancel(object sender, RoutedEventArgs args) => Close();
    private void ManagePortions(object sender, RoutedEventArgs args) => Run(() =>
    {
        Validation.Require(viewModel.CanManagePortions, "请先在主窗选择有效食物。");
        new PortionEditorWindow(new PortionEditorViewModel(journal, viewModel.FoodId!.Value)) { Owner = this }.ShowDialog();
        viewModel.ReloadPortions();
    });
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is ValidationException or System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { viewModel.Status = "操作未完成：" + exception.Message; }
    }
}
