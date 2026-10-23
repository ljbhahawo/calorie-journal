using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CalorieJournal.Desktop;

public static class TextInputCommands
{
    public static RoutedUICommand Clear { get; } = new("清空内容", nameof(Clear), typeof(TextInputCommands));
    static TextInputCommands()
    {
        CommandManager.RegisterClassCommandBinding(typeof(TextBox), new CommandBinding(Clear,
            (sender, e) => { var box = (TextBox)sender; box.BeginChange(); try { box.SelectAll(); box.SelectedText = ""; } finally { box.EndChange(); } e.Handled = true; },
            // 空内容由模板隐藏按钮；可执行性只取决于编辑权限，避免撤销后复用过期的禁用状态。
            (sender, e) => { e.CanExecute = sender is TextBox { IsReadOnly: false, IsEnabled: true }; e.Handled = true; }));
    }
}
