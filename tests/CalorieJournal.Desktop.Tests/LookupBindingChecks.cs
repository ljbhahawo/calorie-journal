using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Validation = CalorieJournal.Core.Validation;

namespace CalorieJournal.Desktop.Tests;

internal static class LookupBindingChecks
{
    public static void Verify()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var directory = Path.Combine(root, "wpf_lookup_" + Guid.NewGuid().ToString("N"));
        var lookup = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data", "lookup"), Path.Combine(directory, "lookup"));
        var vm = new FoodLookupViewModel(lookup);
        var window = new FoodLookupWindow(vm); window.Show(); Pump();
        var query = (TextBox)window.FindName("LookupQueryBox"); query.Text = "香蕉"; Pump();
        ((Button)window.FindName("LookupSearchButton")).Command.Execute(null); Pump();
        Assert.AreEqual(2, vm.Results.Select(x => x.Provider).Distinct().Count(), vm.Status);
        ((DataGrid)window.FindName("LookupResultsGrid")).SelectedItem = vm.Results.First(x => x.Provider == "TFDA"); Pump();
        Assert.IsTrue(((TextBox)window.FindName("LookupDetailsBox")).Text.Contains("https://data.gov.tw/dataset/8543"));
        Assert.IsTrue(((Button)window.FindName("LookupSourceButton")).IsEnabled);
        var checks = new List<object>(); var grid = (Grid)window.Content;
        foreach (var factor in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = 920 * factor; window.Height = 600 * factor; grid.LayoutTransform = new ScaleTransform(factor, factor); Pump();
            foreach (var name in new[] { "LookupSearchButton", "LookupUpdateButton", "LookupCancelUpdateButton", "LookupBundledButton", "LookupChooseButton", "LookupSourceButton", "LookupCloseButton" })
            {
                var control = (Button)window.FindName(name); control.BringIntoView(); Pump();
                var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                Assert.IsTrue(control.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight + 1, $"查询窗口缩放{factor}下{name}不可访问：{bounds}");
                checks.Add(new { Scale = factor, Control = name });
            }
            Assert.IsTrue(((DataGrid)window.FindName("LookupResultsGrid")).ActualHeight >= 80);
        }
        grid.LayoutTransform = Transform.Identity; window.Width = 1100; window.Height = 780; Pump();
        ((Button)window.FindName("LookupChooseButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
        var hit = window.Chosen!; Assert.IsNotNull(hit);
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json")));
        var journal = new JournalService(store);
        var main = new MainViewModel(journal, new BackupService(store, Path.Combine(directory, "recovery")), directory) { TabIndex = 1 };
        var mainWindow = new MainWindow(main); mainWindow.Show(); Pump();
        main.FoodName = "尚未保存的明确测试草稿";
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => main.ApplyFoodReference(hit), false));
        Assert.AreEqual("尚未保存的明确测试草稿", main.FoodName); Assert.HasCount(60, journal.Foods());
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => main.ApplyFoodReference(hit), true)); Pump();
        Assert.HasCount(60, journal.Foods()); Assert.AreEqual("新增食物", main.FoodHeading);
        Assert.AreEqual(hit.Name, ((TextBox)mainWindow.FindName("FoodNameBox")).Text);
        main.FoodName = "TFDA界面保存验证 " + hit.Name;
        main.SaveFoodCommand.Execute(null); Pump();
        var saved = journal.Foods().Single(x => x.Name.StartsWith("TFDA界面保存验证"));
        Assert.AreEqual(hit.SourceUrl, saved.SourceUrl); Assert.AreEqual(hit.Food.SourceId, saved.SourceId);
        Assert.AreEqual(hit.Food.OriginalName, saved.OriginalName); Assert.AreEqual(hit.Catalog.Version, saved.SourceVersion);
        main.SelectedLibraryFood = main.LibraryFoods.Single(x => x.Id == saved.Id); main.UseFoodCommand.Execute(null);
        main.Weight = "150"; main.SaveEntryCommand.Execute(null); Pump();
        var entry = store.ReadSnapshot().Entries.Single(); Assert.IsTrue(entry.SourceSnapshot.Contains(saved.SourceUrl));
        Assert.AreEqual(Validation.CalculateEnergy(1500, saved.KcalPer100gHundredths), entry.EnergyKcalTenths);
        mainWindow.Close();
        File.WriteAllText(Path.Combine(directory, "lookup-checks.json"), JsonSerializer.Serialize(new
        {
            TwoSourceSearch = true, DetailsAndOriginalLink = true, ChooseDoesNotWrite = true, DirtyDraftProtected = true,
            SavePreservesSource = true, IntakeSnapshotPreserved = true, LayoutChecks = checks
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task VerifyOnline()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var directory = Path.Combine(root, "lookup_online_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _ = new TestApplication();
        var service = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data", "lookup"), Path.Combine(directory, "lookup"));
        var vm = new FoodLookupViewModel(service) { Query = "雞蛋" };
        var window = new FoodLookupWindow(vm); window.Show(); Pump();
        await vm.UpdateCommand.ExecuteAsync(null); Pump();
        Assert.IsFalse(vm.IsBusy); Assert.IsFalse(vm.Status.Contains("失败") || vm.Status.Contains("超时"), vm.Status);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "lookup", "tfda.json")), vm.Status);
        var downloaded = service.Load().Single(x => x.Provider == "TFDA");
        Assert.IsTrue(downloaded.Foods.Length >= 2000); Assert.IsNotEmpty(vm.Results);
        Assert.IsTrue(downloaded.Foods.Any(x => x.Name.Contains("鸡蛋")));
        var before = File.ReadAllBytes(Path.Combine(directory, "lookup", "tfda.json"));
        var cancelTask = vm.UpdateCommand.ExecuteAsync(null); vm.CancelUpdate(); await cancelTask;
        Assert.IsTrue(vm.Status.Contains("取消"), vm.Status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory, "lookup", "tfda.json")));
        vm.SearchCommand.Execute(null); Assert.IsNotEmpty(vm.Results);
        // 使用真实不可写目录触发缓存失败，不替换HTTP响应或文件写入。
        using (var lockedCache = new FileStream(Path.Combine(directory, "lookup", "tfda.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.UpdateCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.Status.Contains("失败"), vm.Status);
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory, "lookup", "tfda.json")));
        vm.SearchCommand.Execute(null); Assert.IsNotEmpty(vm.Results);
        window.Close();
        File.WriteAllText(Path.Combine(directory, "online-checks.json"), JsonSerializer.Serialize(new
        {
            Success = true, OfficialUrl = FoodLookupService.TfdaDownloadUrl, downloaded.RetrievedAtUtc,
            downloaded.ArchiveSha256, FoodCount = downloaded.Foods.Length, ActualHttpDownload = true,
            CacheReread = true, CanceledUpdateRetainsCache = true, ActualCacheWriteFailureVisible = true, OfflineSearchAfterUpdate = true,
            PersonalDatabaseTouched = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
}
