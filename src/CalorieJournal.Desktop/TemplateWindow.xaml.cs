using System.Windows;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public partial class TemplateWindow : Window
{
    private readonly TemplateViewModel viewModel;
    public TemplateWindow(TemplateViewModel viewModel)
    {
        InitializeComponent(); this.viewModel = viewModel; DataContext = viewModel;
        Closing += (_, e) => { if (!viewModel.CanClose()) e.Cancel = true; };
    }
    private void NewTemplate(object sender, RoutedEventArgs e) => Run(viewModel.New);
    private void SaveTemplate(object sender, RoutedEventArgs e) => Run(viewModel.Save);
    private void DeleteTemplate(object sender, RoutedEventArgs e) => Run(viewModel.Delete);
    private void BeginReview(object sender, RoutedEventArgs e) => Run(viewModel.BeginReview);
    private void SaveBatch(object sender, RoutedEventArgs e) => Run(viewModel.SaveBatch);
    private void CancelReview(object sender, RoutedEventArgs e) => Run(viewModel.CancelReview);
    private void ClearMeal(object sender, RoutedEventArgs e) => viewModel.ClearPreferredMeal();
    private void AddFood(object sender, RoutedEventArgs e) => Run(() => viewModel.AddFood(false));
    private void ReplaceFood(object sender, RoutedEventArgs e) => Run(() => viewModel.AddFood(true));
    private void RemoveLine(object sender, RoutedEventArgs e) => Run(viewModel.RemoveLine);
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is ValidationException or System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { viewModel.Status = "操作未完成：" + exception.Message; }
    }
}
