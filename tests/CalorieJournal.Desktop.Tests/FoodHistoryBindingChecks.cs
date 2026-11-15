using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class FoodHistoryBindingChecks
{
    public static void Verify()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "wpf_food_history_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json")));
        var journal = new JournalService(store);
        var meal = journal.Meals()[0];
        var recent = new IntakeEntry
        {
            Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), LocalTime = "12:30",
            MealCategoryId = meal.Id, MealNameSnapshot = meal.Name, FoodNameSnapshot = "番茄", EnergyKcalTenths = 285,
            WeightGramsTenths = 1500, KcalPer100gHundredthsSnapshot = 1900, InputMode = "weight",
            PreparationSnapshot = "生", Note = "和午餐一起吃", ProteinGramsHundredths = 120, ProteinPer100gHundredthsSnapshot = 80
        };
        var earlier = recent with { EntryDate = recent.EntryDate.AddDays(-120), FoodNameSnapshot = "西红柿" };
        var zero = earlier with { Id = Guid.NewGuid(), LocalTime = "", InputMode = "direct", WeightGramsTenths = null,
            KcalPer100gHundredthsSnapshot = null, EnergyKcalTenths = 0, Note = "当次未称重", ProteinGramsHundredths = null, ProteinPer100gHundredthsSnapshot = null };
        store.AppendEntries([recent, zero, .. Enumerable.Range(0, 100).Select(i => earlier with
        { Id = Guid.NewGuid(), EntryDate = earlier.EntryDate.AddDays(-i), Note = "" })]);

        var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(root, "recovery")), root);
        var window = new MainWindow(vm) { Width = 920, Height = 600 };
        window.Show();
        ((TabControl)window.FindName("MainTabs")).SelectedIndex = 2;
        Pump();
        Assert.IsTrue(Control<DataGrid>("HistoryGrid").IsVisible);
        Control<TabControl>("HistoryViews").SelectedIndex = 1;
        Pump();

        var grid = Control<DataGrid>("FoodHistoryGrid");
        var query = Control<TextBox>("FoodHistoryQueryBox");
        Assert.IsTrue(grid.IsVisible && grid.ActualHeight > 100,
            $"按食物结果可见={grid.IsVisible}，高度={grid.ActualHeight}，窗口={window.ActualHeight}，主页面={vm.TabIndex}，历史页面={vm.HistoryViewIndex}。");
        Assert.AreEqual(100, grid.Items.Count);
        query.Text = "西红柿";
        Click("FoodHistoryQueryButton");
        Assert.IsTrue(Control<TextBlock>("FoodHistorySummaryLabel").Text.Contains("「西红柿」"));
        grid.SelectedIndex = 0;
        Pump();
        var detail = (FoodHistoryRow)Control<ContentControl>("FoodHistoryDetail").Content;
        Assert.AreEqual(recent.Id, detail.Entry.Id);
        Assert.AreEqual("150 g", detail.WeightText);
        Assert.AreEqual("和午餐一起吃", detail.NoteText);
        Assert.AreEqual("生", detail.PreparationText);
        Assert.AreEqual(28.5m, detail.Entry.EnergyKcal);
        Assert.IsTrue(grid.ActualHeight > 100);
        Click("NextFoodHistoryButton");
        Assert.AreEqual(2, grid.Items.Count);
        Assert.IsFalse(Control<Button>("NextFoodHistoryButton").IsEnabled);
        Click("PreviousFoodHistoryButton");
        var unweighed = vm.FoodHistoryRows.Single(row => row.Entry.Id == zero.Id);
        Assert.AreEqual("—", unweighed.TimeText);
        Assert.AreEqual("—", unweighed.WeightText);
        Assert.AreEqual(0m, unweighed.Entry.EnergyKcal);
        query.Text = "完全没有匹配的名称";
        Click("FoodHistoryQueryButton");
        Assert.AreEqual(0, grid.Items.Count);
        Assert.IsNull(Control<ContentControl>("FoodHistoryDetail").Content);
        query.Text = "";
        Pump();
        Assert.AreEqual(100, grid.Items.Count);
        Assert.IsTrue(Control<TextBlock>("FoodHistorySummaryLabel").Text.Contains("全部摄入"));

        Control<TabControl>("HistoryViews").SelectedIndex = 0;
        Pump();
        Assert.IsTrue(Control<DataGrid>("HistoryGrid").IsVisible);
        Assert.HasCount(102, store.ReadSnapshot().Entries);
        window.Close();

        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            var peer = new ButtonAutomationPeer(Control<Button>(name));
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Pump();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
