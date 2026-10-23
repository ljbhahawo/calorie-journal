using System.Diagnostics;
using System.IO;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class PerformanceChecks
{
    public static void Verify(int count)
    {
        Assert.IsTrue(count is 1000 or 10000 or 100000);
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "performance_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var seed = store.ReadSnapshot(); var today = DateOnly.FromDateTime(DateTime.Today);
        var entries = Enumerable.Range(0, count).Select(index => new IntakeEntry { Id = Guid.NewGuid(), EntryDate = today.AddDays(-(index % 30)),
            MealCategoryId = seed.Meals[0].Id, MealNameSnapshot = seed.Meals[0].Name, FoodNameSnapshot = "明确性能测试记录", EnergyKcalTenths = index % 10 }).ToArray();
        store.ReplaceSnapshot(seed with { Entries = entries });
        var journal = new JournalService(store); var watch = Stopwatch.StartNew();
        var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(root, "recovery")), root);
        var classic = watch.Elapsed.TotalMilliseconds; watch.Restart(); vm.SettingsDailyPage = 1; vm.SaveSettingsCommand.Execute(null);
        var sheet = watch.Elapsed.TotalMilliseconds; Assert.IsTrue(vm.SheetMode); Assert.AreEqual(count, vm.SheetDays.Sum(x => x.Meals.Sum(m => m.Rows.Count)));
        watch.Restart(); vm.SheetFrom = DateTime.Today.AddDays(-365); vm.QuerySheetCommand.Execute(null); var range = watch.Elapsed.TotalMilliseconds;
        var row = vm.SheetDays[0].Meals[0].Rows.First(); row.DirectEnergy = "1.0"; watch.Restart(); row.SaveCommand.Execute(null); var save = watch.Elapsed.TotalMilliseconds;
        Assert.IsFalse(row.HasDraft); Assert.AreEqual(10L, store.FindEntry(row.Id)!.EnergyKcalTenths);
        var catalog = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data/lookup"), Path.Combine(root, "cache")).Load(true)[0];
        var foods = catalog.Foods.Select(food => new FoodReferenceHit(catalog, food).CreateFood()).ToArray();
        store.ReplaceSnapshot(store.ReadSnapshot() with { Foods = foods });
        var searches = new List<double>();
        for (var index = 0; index < 7; index++) { watch.Restart(); var results = journal.Foods("鸡胸"); searches.Add(watch.Elapsed.TotalMilliseconds); Assert.IsNotEmpty(results); }
        JsonFiles.WriteNew(Path.Combine(root, "performance-check.json"), new { Success = true, Count = count, PersonalFoodCount = foods.Length,
            ClassicInitializationMilliseconds = classic, SheetInitializationMilliseconds = sheet, Range366DaysMilliseconds = range,
            SaveAndRefreshMilliseconds = save, SearchesMilliseconds = searches, WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64,
            AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainViewModel).Assembly.Location))),
            Source = "真实随包USDA目录及明确的SQLite规模验收输入；不表示真实饮食" });
    }
}
