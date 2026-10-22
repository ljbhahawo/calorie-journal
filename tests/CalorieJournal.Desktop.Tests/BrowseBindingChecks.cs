using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Desktop;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class BrowseBindingChecks
{
    public static void Verify()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var directory = Path.Combine(root, "wpf_browse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json")));
        var journal = new JournalService(store);
        var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(directory, "recovery")), directory);
        var window = new MainWindow(vm); window.Show(); Pump();
        var datePicker = (DatePicker)window.FindName("SelectedDatePicker");
        var name = (TextBox)window.FindName("EntryNameBox");
        var energy = (TextBox)window.FindName("DirectKcalBox");
        name.Text = "连续列表明确测试输入"; energy.Text = "80"; Pump();
        Assert.IsFalse(((Button)window.FindName("NextDayButton")).IsEnabled);
        Assert.AreEqual(0, ConfirmationChecks.Respond(() =>
        {
            vm.NextDayCommand.Execute(null); vm.NextDayCommand.Execute(null);
            vm.SelectedDate = DateTime.Today.AddDays(1); Pump();
        }, false));
        Assert.AreEqual(DateTime.Today, vm.SelectedDate);
        Assert.AreEqual("连续列表明确测试输入", vm.EntryName);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => { datePicker.SelectedDate = DateTime.Today.AddDays(-1); Pump(); }, false));
        Assert.AreEqual(DateTime.Today, vm.SelectedDate);
        Assert.AreEqual(DateTime.Today, datePicker.SelectedDate);
        Assert.AreEqual("连续列表明确测试输入", name.Text);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => { datePicker.SelectedDate = DateTime.Today.AddDays(-1); Pump(); }, true));
        Assert.AreEqual(DateTime.Today.AddDays(-1), vm.SelectedDate);
        Assert.AreEqual("", name.Text);
        Assert.IsTrue(((Button)window.FindName("NextDayButton")).IsEnabled);
        vm.TodayCommand.Execute(null); Pump();
        name.Text = "视图切换保留草稿"; energy.Text = "80"; Pump();
        Assert.AreEqual(0, ConfirmationChecks.Respond(() => { Click(window, "ContinuousViewButton"); Click(window, "SingleViewButton"); Click(window, "ContinuousViewButton"); }, false));
        Assert.AreEqual("视图切换保留草稿", vm.EntryName);
        Click(window, "SaveEntryButton");
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.IsTrue(vm.BrowseDays[0].Summary.StartsWith("80.0"));
        Assert.AreEqual(30, vm.BrowseDays.Count);
        var list = (ListBox)window.FindName("ContinuousDaysList");
        var add = ListedButton(list, vm.BrowseDays[1], "AddListedEntryButton"); Click(add);
        Assert.AreEqual(DateTime.Today.AddDays(-1), vm.EntryDate);
        var rice = journal.Foods("长粒白米饭").Single();
        ((ComboBox)window.FindName("FoodChoiceBox")).SelectedItem = vm.FoodChoices.Single(x => x.Id == rice.Id);
        ((TextBox)window.FindName("WeightBox")).Text = "150"; Pump(); Click(window, "SaveEntryButton");
        var entry = journal.Entries(today.AddDays(-1)).Single();
        Assert.AreEqual(195m, entry.EnergyKcal);
        journal.SaveFood(rice with { KcalPer100gHundredths = 20000 });
        Click(ListedButton(list, vm.BrowseDays[1], "EditListedEntryButton"));
        Assert.AreEqual("130.00", vm.UnitKcal);
        Assert.AreEqual(entry.SourceSnapshot, vm.EntrySource);
        vm.EntryDate = DateTime.Today; ((TextBox)window.FindName("WeightBox")).Text = "100"; Pump(); Click(window, "SaveEntryButton");
        Assert.AreEqual(210m, vm.BrowseDays[0].EnergyKcalTenths / 10m);
        Assert.AreEqual("未记录", vm.BrowseDays[1].Summary);
        Assert.IsTrue(vm.DayTotal.StartsWith("210.0"));
        Assert.AreEqual(210m, vm.HistoryRows.Single(x => x.Date == today).EnergyKcal);
        var moved = journal.Entries(today).Single(x => x.Id == entry.Id);
        Assert.AreEqual(entry.SourceSnapshot, moved.SourceSnapshot);
        var delete = ListedButton(list, vm.BrowseDays[0], "DeleteListedEntryButton");
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click(delete), false));
        Assert.HasCount(2, store.ReadSnapshot().Entries);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click(delete), true));
        Assert.HasCount(1, store.ReadSnapshot().Entries);
        Assert.AreEqual(1, vm.BrowseDays[0].Entries.Length);
        Assert.AreEqual(vm.BrowseDays[0].EnergyKcalTenths / 10m, vm.HistoryRows.Single(x => x.Date == today).EnergyKcal);
        vm.BrowseFrom = DateTime.Today; vm.BrowseTo = DateTime.Today.AddDays(-1); vm.LoadBrowseCommand.Execute(null);
        Assert.IsTrue(vm.Status.Contains("区间")); Assert.AreEqual(30, vm.BrowseDays.Count);
        vm.AddListedEntryCommand.Execute(vm.BrowseDays[2]); name.Text = "零热量明确测试输入"; energy.Text = "0"; Pump(); Click(window, "SaveEntryButton");
        Assert.AreEqual("0.0 大卡 · 1 项", vm.BrowseDays[2].Summary);
        Assert.AreEqual(30, vm.BrowseDays.Count);
        Click(window, "LoadEarlierButton"); Assert.AreEqual(60, vm.BrowseDays.Count);
        Assert.AreEqual(DateTime.Today.AddDays(-59), vm.BrowseFrom);
        list.ScrollIntoView(vm.BrowseDays.Last()); Pump();
        var scroll = Descendants<ScrollViewer>(list).First();
        Assert.IsTrue(scroll.VerticalOffset > 0 && scroll.ScrollableHeight > 0);
        Assert.IsTrue(list.ItemContainerGenerator.ContainerFromItem(vm.BrowseDays.Last()) is ListBoxItem);
        Assert.IsTrue(list.ItemContainerGenerator.ContainerFromItem(vm.BrowseDays[0]) is null, "日期容器应按可视范围虚拟化。");
        var earlier = vm.BrowseDays.Last();
        Click(ListedButton(list, earlier, "AddListedEntryButton")); name.Text = "较早日期的明确输入"; energy.Text = "10"; Pump(); Click(window, "SaveEntryButton");
        Assert.IsTrue(scroll.VerticalOffset > 0);
        Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromItem(vm.BrowseDays.Last()), "较早日期保存后仍可见，不能跳回顶部。");
        Assert.AreEqual(10m, journal.Entries(earlier.Date).Single().EnergyKcal);
        vm.BrowseFrom = DateTime.Today.AddDays(-365); vm.BrowseTo = DateTime.Today; Click(window, "BrowseQueryButton");
        Click(window, "LoadEarlierButton"); Assert.AreEqual(366, vm.BrowseDays.Count); Assert.IsTrue(vm.Status.Contains("366"));
        vm.BrowseFrom = DateTime.Today.AddDays(-2); Click(window, "BrowseQueryButton");
        var layoutChecks = new List<object>();
        var rootGrid = (Grid)window.Content;
        foreach (var factor in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = 920 * factor; window.Height = 600 * factor;
            rootGrid.LayoutTransform = new ScaleTransform(factor, factor); Pump();
            list.ScrollIntoView(vm.BrowseDays[0]); Pump();
            foreach (var button in new[] { (Button)window.FindName("SaveEntryButton"), (Button)window.FindName("ContinuousViewButton"), (Button)window.FindName("BrowseQueryButton"), (Button)window.FindName("LoadEarlierButton"), ListedButton(list, vm.BrowseDays[0], "AddListedEntryButton"), ListedButton(list, vm.BrowseDays[0], "EditListedEntryButton"), ListedButton(list, vm.BrowseDays[0], "DeleteListedEntryButton") })
            {
                button.BringIntoView(); Pump();
                var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                Assert.IsTrue(button.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight + 1, $"连续列表缩放{factor}按钮{button.Name}不可访问：{bounds}");
                layoutChecks.Add(new { Scale = factor, Control = button.Name, Bounds = bounds.ToString() });
            }
        }
        rootGrid.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820; Pump();
        File.WriteAllText(Path.Combine(directory, "browse-checks.json"), JsonSerializer.Serialize(new
        {
            InvalidNavigationRetainsDraftWithoutPrompt = true, CancelNavigationRetainsBoundDate = true,
            AcceptedNavigationPromptsOnce = true, ViewSwitchRetainsDraft = true,
            ListedCrudAndCrossDateMove = true, SnapshotPreserved = true, EmptyAndZero = true,
            InvalidFilterPreservesAppliedRange = true, EarlierAndRangeLimit = true, VirtualizedScroll = true, SaveRetainsScrollPosition = true,
            LayoutScaleChecks = layoutChecks, Entries = store.ReadSnapshot().Entries.Length
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.IsTrue(vm.CanClose()); window.Hide();
    }

    private static Button ListedButton(ListBox list, DailyIntake day, string name)
    {
        list.ScrollIntoView(day); Pump();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(day);
        return Descendants<Button>(container).First(x => x.Name == name);
    }
    private static void Click(MainWindow window, string name) => Click((Button)window.FindName(name));
    private static void Click(Button button)
    {
        Assert.IsNotNull(button.Command); Assert.IsTrue(button.Command.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter); Pump();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
