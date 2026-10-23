using System.Windows;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public partial class PortionEditorWindow : Window
{
    private readonly PortionEditorViewModel viewModel;
    public PortionEditorWindow(PortionEditorViewModel viewModel)
    {
        InitializeComponent(); this.viewModel = viewModel; DataContext = viewModel;
        Closing += (_, args) => args.Cancel = !viewModel.CanClose();
    }
    private void Save(object sender, RoutedEventArgs args) => Run(viewModel.Save);
    private void New(object sender, RoutedEventArgs args) => Run(viewModel.New);
    private void Delete(object sender, RoutedEventArgs args) => Run(viewModel.Delete);
    private void CloseWindow(object sender, RoutedEventArgs args) => Close();
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is ValidationException or System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { viewModel.Status = "操作未完成：" + exception.Message; }
    }
}
