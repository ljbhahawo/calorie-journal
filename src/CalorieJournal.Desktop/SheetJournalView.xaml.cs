using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CalorieJournal.Desktop;

public partial class SheetJournalView : UserControl
{
    public SheetJournalView() => InitializeComponent();
    private void RowAdded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SheetMealViewModel group) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (group.Rows.LastOrDefault() is not { Original: null } row) return;
            var grid = Descendants<DataGrid>(this).FirstOrDefault(x => x.DataContext == group);
            if (grid is null) return;
            grid.SelectedItem = row; grid.ScrollIntoView(row, grid.Columns[1]); grid.UpdateLayout();
            Descendants<FoodNameInput>(grid).FirstOrDefault(x => x.DataContext == row)?.FocusName();
        }));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private void DirectClicked(object sender, MouseButtonEventArgs e) { if (((FrameworkElement)sender).DataContext is SheetEntryViewModel row) row.EnterDirect(); }
    private void DirectFocused(object sender, KeyboardFocusChangedEventArgs e) { if (((FrameworkElement)sender).DataContext is SheetEntryViewModel row) row.EnterDirect(); }
    private void RowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var inner = Descendants<ScrollViewer>((DataGrid)sender).FirstOrDefault();
        if (inner is not null && (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight || e.Delta > 0 && inner.VerticalOffset > 0)) return;
        // 表格到达边界后继续浏览日期和餐次，防止内部ScrollViewer吞掉滚轮事件。
        var outer = Descendants<ScrollViewer>(SheetDaysList).FirstOrDefault();
        if (outer is null) return;
        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }
    private void RowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is FrameworkElement { DataContext: SheetEntryViewModel row }) { row.SaveCommand.Execute(null); e.Handled = true; }
    }
}
