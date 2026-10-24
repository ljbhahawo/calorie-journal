using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CalorieJournal.Desktop;

public partial class SheetJournalView : UserControl
{
    private Window? owner;
    private MainViewModel? model;
    private SheetEntryViewModel? activeRow;
    private bool saving;
    public SheetJournalView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }
    private void Attach()
    {
        Detach(); owner = Window.GetWindow(this); model = DataContext as MainViewModel;
        if (owner is null) return;
        owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OutsideClicked), true);
        owner.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused), true);
        if (model is not null) model.PropertyChanged += SettingChanged;
        UpdateProteinColumns();
    }
    private void Detach()
    {
        if (owner is not null)
        {
            owner.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OutsideClicked));
            owner.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Focused));
        }
        if (model is not null) model.PropertyChanged -= SettingChanged;
        owner = null; model = null;
    }
    private void SettingChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.ProteinEnabled)) UpdateProteinColumns(); }
    private void GridLoaded(object sender, RoutedEventArgs e) => UpdateProteinColumn((DataGrid)sender);
    private void UpdateProteinColumns() { foreach (var grid in Descendants<DataGrid>(this)) UpdateProteinColumn(grid); }
    private void UpdateProteinColumn(DataGrid grid)
    {
        var visible = (DataContext as MainViewModel)?.ProteinVisibility ?? Visibility.Visible;
        grid.Columns[6].Visibility = visible;
        // 每列保留单个输入区域，营养计算结果在选中行的详情中显示。
        grid.RowHeight = 48;
    }
    private void Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsVisible && !saving && FindRow(e.NewFocus as DependencyObject) is { } row) activeRow = row;
    }
    private static SheetEntryViewModel? FindRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: SheetEntryViewModel row }) return row;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : source is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
    private void OutsideClicked(object sender, MouseButtonEventArgs e)
    {
        if (!IsVisible || saving || activeRow is null || ReferenceEquals(FindRow(e.OriginalSource as DependencyObject), activeRow)) return;
        // 横向滚动属于行内编辑，尚未填写完整时也必须能访问后续列。
        for (var source = e.OriginalSource as DependencyObject; source is Visual; source = VisualTreeHelper.GetParent(source)) if (source is ScrollBar) return;
        if (!SaveRow(activeRow)) e.Handled = true;
    }
    public void SaveActiveBeforeClose() { if (IsVisible && activeRow is { Dirty: true }) SaveRow(activeRow); }
    private bool SaveRow(SheetEntryViewModel row)
    {
        if (model is not null && !model.ContainsSheetRow(row)) { activeRow = null; return true; }
        if (saving || !row.Dirty) return true;
        saving = true;
        try
        {
            var grid = Descendants<DataGrid>(this).FirstOrDefault(x => x.Items.Contains(row));
            // 日期控件可能保留未提交的文字；不能把上次的有效日期误写回数据库。
            var picker = grid is null ? null : Descendants<DatePicker>(grid).FirstOrDefault(x => x.DataContext == row);
            if (picker is not null && (picker.SelectedDate is not { } selected || !DateTime.TryParse(picker.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var typed) || typed.Date != selected.Date))
            { row.Message = "保存未完成：请填写有效日期。"; picker.BringIntoView(); picker.Focus(); return false; }
            row.SaveCommand.Execute(null);
            if (!row.HasDraft) return true;
            if (grid is not null)
            {
                grid.SelectedItem = row;
                var name = string.IsNullOrWhiteSpace(row.Name) ? "SheetFoodInput" : row.WeightMode && string.IsNullOrWhiteSpace(row.Weight) ? "SheetWeightBox" : row.WeightMode && string.IsNullOrWhiteSpace(row.UnitEnergy) ? "SheetUnitEnergyBox" : !row.WeightMode && string.IsNullOrWhiteSpace(row.DirectEnergy) ? "SheetDirectEnergyBox" : "SheetDetailsExpander";
                var column = name == "SheetFoodInput" ? 0 : name == "SheetWeightBox" ? 3 : name == "SheetUnitEnergyBox" ? 4 : name == "SheetDirectEnergyBox" ? 5 : 0;
                grid.ScrollIntoView(row, grid.Columns[column]); grid.UpdateLayout();
                var control = Descendants<FrameworkElement>(grid).FirstOrDefault(x => x.DataContext == row && x.Name == name);
                control?.BringIntoView(); if (control is FoodNameInput input) input.FocusName(); else control?.Focus();
            }
            return false;
        }
        finally { saving = false; }
    }
    private void RowAdded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SheetMealViewModel group) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (group.Rows.LastOrDefault() is not { Original: null } row) return;
            var grid = Descendants<DataGrid>(this).FirstOrDefault(x => x.DataContext == group);
            if (grid is null) return;
            grid.SelectedItem = row; grid.ScrollIntoView(row, grid.Columns[0]); grid.UpdateLayout();
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
        if (e.Key != Key.Enter || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Control) || FindRow(e.OriginalSource as DependencyObject) is not { } row) return;
        for (var source = e.OriginalSource as DependencyObject; source is Visual; source = VisualTreeHelper.GetParent(source)) if (source is FrameworkElement { Name: "SuggestionsList" }) return;
        if (e.OriginalSource is TextBox { AcceptsReturn: true } && Keyboard.Modifiers == ModifierKeys.None) return;
        if (e.OriginalSource is ButtonBase || Descendants<ComboBox>((DataGrid)sender).Any(x => x.DataContext == row && x.IsDropDownOpen)) return;
        var input = Descendants<FoodNameInput>((DataGrid)sender).FirstOrDefault(x => x.DataContext == row && x.IsKeyboardFocusWithin);
        if (input?.ChooseOnEnter() == true) { e.Handled = true; return; }
        activeRow = row; SaveRow(row); e.Handled = true;
    }
}
