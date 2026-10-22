using System.Windows;

namespace CalorieJournal.Desktop;

public partial class ImportWindow : Window
{
    public ImportWindow(ImportViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Closing += (_, args) =>
        {
            if (viewModel.IsBusy) { args.Cancel = true; return; }
            if (!viewModel.Imported && (viewModel.Rows.Count > 0 || viewModel.RawText.Length > 0))
                args.Cancel = MessageBox.Show(this, "尚未导入的草稿将关闭，原数据库保持不变。是否关闭？", "关闭导入", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
        };
    }
    private void CloseWindow(object sender, RoutedEventArgs args) => Close();
}
