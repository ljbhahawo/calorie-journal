using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Desktop;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace CalorieJournal.Desktop.Tests;

[TestClass]
public sealed class BindingTests
{
    // 在测试进程中运行真实WPF控件和绑定，不对用户桌面注入输入，也不替换业务或数据库实现。
    [STATestMethod]
    public void RealControlsBindThroughViewModelIntoSqliteAndRefreshViews()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("未配置隔离测试目录。");
        var directory = Path.Combine(root, "wpf_bindings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var listener = new System.Diagnostics.TextWriterTraceListener(Path.Combine(directory, "binding-diagnostics.txt"));
        System.Diagnostics.Trace.Listeners.Add(listener); System.Diagnostics.Trace.AutoFlush = true;
        void Trace(string stage) => File.AppendAllText(Path.Combine(directory, "stages.txt"), stage + Environment.NewLine);
        Trace("创建测试数据库");
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json")));
        var service = new JournalService(store);
        service.SaveSettings(new JournalSettings { DefaultWeightMode = false, NewFoodMode = "never" });
        Trace("创建WPF应用资源");
        _ = new TestApplication();
        Trace("创建真实ViewModel");
        var vm = new MainViewModel(service, new BackupService(store, Path.Combine(directory, "recovery")), directory);
        Trace("ViewModel已创建");
        var window = new MainWindow(vm);
        Trace("Window已创建");
        window.Show(); Pump(); Trace("真实窗口显示与绑定初始化完成");

        var entryName = (TextBox)window.FindName("EntryNameBox");
        var directKcal = (TextBox)window.FindName("DirectKcalBox");
        entryName.Text = "WPF绑定验证输入"; directKcal.Text = "123.4"; Pump();
        Trace("输入绑定完成");
        Assert.AreEqual("WPF绑定验证输入", vm.EntryName);
        vm.SaveEntryCommand.Execute(null); Pump();
        Trace("保存命令完成：" + vm.Status);
        Assert.HasCount(1, store.ReadSnapshot().Entries);
        Assert.AreEqual(123.4m, store.ReadSnapshot().Entries[0].EnergyKcal);
        Assert.AreEqual(1, ((DataGrid)window.FindName("EntriesGrid")).Items.Count);
        vm.SaveEntryCommand.Execute(null);
        Assert.HasCount(1, store.ReadSnapshot().Entries);
        Assert.IsTrue(vm.Status.Contains("请填写") || vm.Status.Contains("有效数字"));

        var choice = (ComboBox)window.FindName("FoodChoiceBox");
        choice.SelectedItem = vm.FoodChoices.Single(x => x.Name == "长粒白米饭（熟）"); Pump();
        Assert.IsTrue(vm.WeightMode);
        ((TextBox)window.FindName("WeightBox")).Text = "150"; Pump();
        Assert.IsTrue(vm.EnergyPreview.Contains("195.0"));
        vm.WeightUnit = "lb"; vm.SaveEntryCommand.Execute(null);
        Assert.HasCount(1, store.ReadSnapshot().Entries);
        Assert.IsTrue(vm.Status.Contains("单位只支持"));
        vm.WeightUnit = "g";
        vm.SaveEntryCommand.Execute(null); Pump();
        Assert.HasCount(2, store.ReadSnapshot().Entries);
        Assert.AreEqual(318.4m, service.History(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today)).Single().EnergyKcal);

        var tabs = (TabControl)window.FindName("MainTabs"); tabs.SelectedIndex = 1; Pump();
        var foodGrid = (DataGrid)window.FindName("FoodsGrid");
        foodGrid.SelectedItem = vm.LibraryFoods.Single(x => x.Name == "长粒白米饭（熟）"); Pump();
        Assert.AreEqual(foodGrid.SelectedItem, vm.SelectedLibraryFood);
        ((TextBox)window.FindName("FoodUnitKcalBox")).Text = "200"; Pump();
        vm.SaveFoodCommand.Execute(null); Pump();
        Assert.AreEqual(200m, service.Foods("长粒白米饭").Single().KcalPer100g, vm.Status);
        Assert.AreEqual(195m, store.ReadSnapshot().Entries.Single(x => x.InputMode == "weight").EnergyKcal);

        ((TextBox)window.FindName("FoodNameBox")).Text = "WPF自建食物";
        ((TextBox)window.FindName("FoodUnitKcalBox")).Text = "80"; Pump();
        vm.SaveFoodCommand.Execute(null); Pump();
        Assert.HasCount(61, service.Foods());
        Assert.IsTrue(vm.Status.StartsWith("食物已保存"));

        tabs.SelectedIndex = 3; Pump();
        vm.NewMealCommand.Execute(null);
        ((TextBox)window.FindName("MealNameBox")).Text = "WPF下午茶"; Pump();
        vm.SaveMealCommand.Execute(null); Pump();
        Assert.IsTrue(service.Meals().Any(x => x.Name == "WPF下午茶"));

        tabs.SelectedIndex = 2; Pump();
        vm.LoadHistoryCommand.Execute(null); Pump();
        Assert.AreEqual(30, ((DataGrid)window.FindName("HistoryGrid")).Items.Count);
        vm.HistoryFrom = DateTime.Today.AddDays(1);
        vm.LoadHistoryCommand.Execute(null); Pump();
        Assert.IsTrue(vm.Status.Contains("日期区间"));
        Assert.AreEqual(30, vm.HistoryRows.Count);
        tabs.SelectedIndex = 0; Pump();
        entryName.Text = "无效筛选期间的明确验收输入"; directKcal.Text = "1.0"; Pump();
        vm.SaveEntryCommand.Execute(null); Pump();
        Assert.IsTrue(vm.Status.StartsWith("摄入已保存"));
        Assert.AreEqual(3, vm.HistoryRows.Single(x => x.Date == DateOnly.FromDateTime(DateTime.Today)).Count);
        var extra = store.ReadSnapshot().Entries.Single(x => x.FoodNameSnapshot == "无效筛选期间的明确验收输入");
        service.DeleteEntry(extra.Id);
        tabs.SelectedIndex = 2; Pump();
        Assert.AreEqual(2, vm.HistoryRows.Single(x => x.Date == DateOnly.FromDateTime(DateTime.Today)).Count);

        tabs.SelectedIndex = 3; Pump();
        vm.SelectedMeal = vm.AllMeals.Single(x => x.Name == "早餐");
        ((TextBox)window.FindName("MealNameBox")).Text = "验收早餐"; Pump();
        vm.RestoreCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.IsTrue(vm.Status.Contains("请先保存或取消") && vm.Status.Contains("餐次"));
        tabs.SelectedIndex = 0; Pump();
        entryName.Text = "跨页面编辑保护的明确输入"; directKcal.Text = "1.0"; Pump();
        vm.SaveEntryCommand.Execute(null); Pump();
        Assert.AreEqual("验收早餐", vm.MealName);
        vm.SaveMealCommand.Execute(null); Pump();
        Assert.IsTrue(service.Meals().Any(x => x.Name == "验收早餐"));

        var riceEntry = store.ReadSnapshot().Entries.Single(x => x.InputMode == "weight");
        var pastDate = DateTime.Today.AddDays(-1);
        var past = riceEntry with { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(pastDate), LocalTime = "12:34" };
        service.SaveEntry(past);
        var savedPast = store.FindEntry(past.Id)!;
        vm.SelectedDate = pastDate; Pump();
        vm.SelectedEntry = vm.DayEntries.Single();
        var countBeforeCopy = store.ReadSnapshot().Entries.Length;
        vm.CopyEntryCommand.Execute(null); Pump();
        Assert.AreEqual(countBeforeCopy, store.ReadSnapshot().Entries.Length);
        Assert.AreEqual(DateTime.Today, vm.EntryDate);
        Assert.AreEqual("", vm.EntryTime);
        Assert.AreEqual("添加摄入", vm.EntryHeading);
        Assert.AreEqual("130.00", vm.UnitKcal);
        Assert.IsTrue(vm.EnergyPreview.Contains("195.0"));
        vm.SaveEntryCommand.Execute(null); Pump();
        Assert.AreEqual(countBeforeCopy + 1, store.ReadSnapshot().Entries.Length);
        Assert.AreEqual(savedPast, store.FindEntry(past.Id));
        var copied = store.ReadSnapshot().Entries.OrderByDescending(x => x.CreatedAtUtc).First();
        Assert.AreNotEqual(past.Id, copied.Id);
        Assert.AreEqual(DateOnly.FromDateTime(DateTime.Today), copied.EntryDate);
        Assert.AreEqual(195m, copied.EnergyKcal);
        Assert.AreEqual("", copied.LocalTime);
        Assert.AreEqual("验收早餐", copied.MealNameSnapshot);
        vm.SelectedEntry = copied;
        vm.EditEntryCommand.Execute(null); Pump();
        vm.UnitKcal = "140.00";
        vm.SaveEntryCommand.Execute(null); Pump();
        Assert.AreEqual(210m, store.FindEntry(copied.Id)!.EnergyKcal);
        Assert.IsTrue(store.FindEntry(copied.Id)!.SourceSnapshot.Contains("本次单位热量已调整"));
        Assert.AreEqual(savedPast, store.FindEntry(past.Id));

        service.DeleteFood(service.Foods("长粒白米饭").Single());
        var oldMeal = service.Meals().Single(x => x.Id == past.MealCategoryId);
        service.SaveMeal(oldMeal with { IsActive = false });
        vm.SelectedDate = pastDate; Pump(); vm.SelectedEntry = vm.DayEntries.Single();
        vm.CopyEntryCommand.Execute(null); Pump();
        Assert.IsNotNull(vm.EntryMeal);
        Assert.IsTrue(vm.EntryMeal.IsActive);
        Assert.AreNotEqual(oldMeal.Id, vm.EntryMeal.Id);
        Assert.IsTrue(vm.EntrySource.Contains("原食物已删除"));
        vm.SaveEntryCommand.Execute(null); Pump();
        var unlinkedCopy = store.ReadSnapshot().Entries.OrderByDescending(x => x.CreatedAtUtc).First();
        Assert.IsNull(unlinkedCopy.FoodId);
        Assert.AreEqual(195m, unlinkedCopy.EnergyKcal);
        Assert.AreEqual(savedPast, store.FindEntry(past.Id));
        Assert.IsTrue(vm.Status.StartsWith("摄入已保存"));

        tabs.SelectedIndex = 0;
        Assert.IsNotNull(window.FindName("SaveEntryButton"));
        Assert.IsFalse(string.IsNullOrEmpty(((TextBlock)window.FindName("StatusText")).Text));
        vm.SelectedDate = DateTime.Today.AddDays(-7); Pump();
        entryName.Text = "零热量显示的明确验收输入"; directKcal.Text = "0"; Pump();
        vm.SaveEntryCommand.Execute(null); Pump();
        Assert.AreEqual(0m, vm.DayEntries.Single().EnergyKcal);
        WindowLayoutChecks.Verify(window, vm, Path.Combine(directory, "layout-checks.json"));
        Trace("全部断言通过");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
