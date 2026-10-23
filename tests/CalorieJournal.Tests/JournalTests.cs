using System.Diagnostics;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace CalorieJournal.Tests;

[TestClass]
public sealed class JournalTests
{
    public TestContext TestContext { get; set; } = null!;
    private string directory = "";
    private SqliteJournalStore store = null!;
    private JournalService service = null!;
    private FoodPackage package = null!;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [TestInitialize]
    public void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")
            ?? throw new InvalidOperationException("请将CALORIE_TEST_ROOT设置为项目内测试目录。");
        directory = Path.Combine(root, TestContext.TestName + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        package = JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json"));
        store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(package);
        service = new JournalService(store);
    }

    private IntakeEntry Direct(long energy = 1000, DateOnly? date = null) => new()
    {
        Id = Guid.NewGuid(), EntryDate = date ?? Today, MealCategoryId = service.Meals()[0].Id,
        MealNameSnapshot = service.Meals()[0].Name, FoodNameSnapshot = "明确的测试输入", EnergyKcalTenths = energy
    };

    private IntakeEntry Rice()
    {
        var food = service.Foods("长粒白米饭").Single();
        return Direct() with { FoodId = food.Id, FoodNameSnapshot = food.Name, PreparationSnapshot = food.Preparation,
            SourceSnapshot = food.SourceSummary, InputMode = "weight", WeightGramsTenths = 1500,
            KcalPer100gHundredthsSnapshot = food.KcalPer100gHundredths, EnergyKcalTenths = Validation.CalculateEnergy(1500, food.KcalPer100gHundredths) };
    }

    [TestMethod]
    public void DecimalCalculationAndPrecisionAreExact()
    {
        Assert.AreEqual(1852L, Validation.CalculateEnergy(1500, 12345));
        Assert.AreEqual(1500L, Validation.Scaled(0.15m * 1000, 10, 0.1m, 100000, "克"));
        Assert.AreEqual(1L, Validation.CalculateEnergy(1, 5000));
        Assert.Throws<ValidationException>(() => Validation.CalculateEnergy(0, 100));
        Assert.Throws<ValidationException>(() => Validation.CalculateEnergy(1, -1));
        Assert.Throws<ValidationException>(() => Validation.Scaled(1.001m, 10, 0, 100, "数值"));
        foreach (var text in new[] { "", "NaN", "Infinity", "1,000", "1e2", "中文" })
            Assert.Throws<ValidationException>(() => Validation.Number(text, "数值"));
    }

    [TestMethod]
    public void RealBuiltinSourceDataAndSearchAreAvailable()
    {
        Assert.AreEqual(60, service.Foods().Length);
        Assert.IsEmpty(store.ReadSnapshot().Entries);
        Assert.AreEqual(130m, service.Foods("长粒白米饭").Single().KcalPer100g);
        Assert.IsNotEmpty(service.Foods("米饭 熟"));
        Assert.IsNotEmpty(service.Foods("chicken breast raw"));
        Assert.IsTrue(package.Foods.All(x => x.SourceKind == "USDA SR Legacy" && x.SourceVersion == "2018-04" && x.SourceUrl.Contains(x.SourceId)));
        Assert.AreEqual(0m, service.Foods("食盐").Single().KcalPer100g);
    }

    [TestMethod]
    public void CrudPersistsAcrossConnectionsAndSummariesStayConsistent()
    {
        var first = Direct(1255); var second = Rice();
        service.SaveEntry(first); service.SaveEntry(second);
        var reopened = new JournalService(new SqliteJournalStore(store.DatabasePath));
        Assert.HasCount(2, reopened.Entries(Today));
        Assert.AreEqual(320.5m, reopened.History(Today, Today).Single().EnergyKcal);
        service.SaveEntry(first with { EntryDate = Today.AddDays(-1), EnergyKcalTenths = 1500 });
        Assert.HasCount(1, service.Entries(Today));
        service.DeleteEntry(first.Id);
        Assert.IsEmpty(service.Entries(Today.AddDays(-1)));
        Assert.Throws<ValidationException>(() => service.DeleteEntry(first.Id));
    }

    [TestMethod]
    public void BrowseIncludesEmptyDaysAndExactZeroAndUsesMealTimeOrder()
    {
        var first = Direct(0, Today.AddDays(-2)) with { LocalTime = "10:30" };
        var second = Direct(101, Today.AddDays(-2)) with { LocalTime = "08:00" };
        var third = Direct(205, Today.AddDays(-2)) with { MealCategoryId = service.Meals()[1].Id, MealNameSnapshot = service.Meals()[1].Name };
        service.SaveEntry(first); service.SaveEntry(second); service.SaveEntry(third);
        service.SaveEntry(Direct(0));
        var days = service.BrowseDays(Today.AddDays(-2), Today);
        CollectionAssert.AreEqual(new[] { Today, Today.AddDays(-1), Today.AddDays(-2) }, days.Select(x => x.Date).ToArray());
        Assert.AreEqual("0.0 大卡 · 1 项", days[0].Summary);
        Assert.AreEqual("未记录", days[1].Summary);
        Assert.AreEqual(306L, days[2].EnergyKcalTenths);
        CollectionAssert.AreEqual(new[] { second.Id, first.Id, third.Id }, days[2].Entries.Select(x => x.Id).ToArray());
        service.SaveEntry(second with { EntryDate = Today, EnergyKcalTenths = 404 });
        service.DeleteEntry(third.Id);
        var reopened = new JournalService(new SqliteJournalStore(store.DatabasePath));
        var after = reopened.BrowseDays(Today.AddDays(-2), Today);
        Assert.AreEqual(404L, after[0].EnergyKcalTenths);
        Assert.HasCount(1, after[2].Entries);
        Assert.AreEqual(first.Id, after[2].Entries[0].Id);
    }

    [TestMethod]
    public void BrowseRejectsInvalidIntervalsAndKeepsSnapshotsAfterFoodChanges()
    {
        var rice = Rice(); service.SaveEntry(rice);
        var before = store.FindEntry(rice.Id);
        service.DeleteFood(service.Foods("长粒白米饭").Single());
        Assert.AreEqual(before, service.BrowseDays(Today, Today).Single().Entries.Single());
        Assert.AreEqual(366, service.BrowseDays(Today.AddDays(-365), Today).Length);
        Assert.Throws<ValidationException>(() => service.BrowseDays(Today.AddDays(-366), Today));
        Assert.Throws<ValidationException>(() => service.BrowseDays(Today, Today.AddDays(-1)));
        Assert.Throws<ValidationException>(() => service.BrowseDays(Today, Today.AddDays(1)));
        Assert.HasCount(1, store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void FoodChangesDeletionAndReinitializationKeepHistoricalSnapshots()
    {
        var entry = Rice(); service.SaveEntry(entry);
        var persistedBeforeFoodEdit = store.FindEntry(entry.Id);
        var food = service.Foods("长粒白米饭").Single();
        service.SaveFood(food with { Name = "用户修改的米饭", KcalPer100gHundredths = 20000, UserModified = true });
        service.DeleteFood(store.ReadFoods().Single(x => x.Id == food.Id));
        store.Initialize(package with { Version = "same-source-new-package" });
        Assert.IsFalse(service.Foods().Any(x => x.Id == food.Id));
        Assert.AreEqual(persistedBeforeFoodEdit, store.FindEntry(entry.Id));
        Assert.Throws<ValidationException>(() => service.SaveEntry(RiceReference(entry)));
        service.SaveEntry(entry with { Note = "历史条目仍可编辑" });
        Assert.AreEqual(195m, store.FindEntry(entry.Id)!.EnergyKcal);
    }

    private static IntakeEntry RiceReference(IntakeEntry entry) => entry with { Id = Guid.NewGuid() };

    [TestMethod]
    public void UserFoodAndMealMaintenancePreserveReferences()
    {
        var food = new FoodItem { Id = Guid.NewGuid(), Name = "测试自建食物", Aliases = "测试别名", KcalPer100gHundredths = 12345 };
        service.SaveFood(food);
        Assert.AreEqual(food.Id, service.Foods("测试别名").Single().Id);
        var meal = new MealCategory { Id = Guid.NewGuid(), Name = "下午加餐", SortOrder = 6 };
        service.SaveMeal(meal);
        var entry = Direct() with { MealCategoryId = meal.Id, MealNameSnapshot = meal.Name };
        service.SaveEntry(entry);
        service.SaveMeal(meal with { Name = "下午茶", IsActive = false });
        Assert.AreEqual("下午加餐", store.FindEntry(entry.Id)!.MealNameSnapshot);
        Assert.Throws<ValidationException>(() => service.SaveEntry(entry with { Id = Guid.NewGuid() }));
        service.SaveEntry(entry with { EntryDate = Today.AddDays(-1) });
        Assert.AreEqual(Today.AddDays(-1), store.FindEntry(entry.Id)!.EntryDate);
        Assert.Throws<ValidationException>(() => service.SaveMeal(meal with { Id = Guid.NewGuid(), Name = "早餐" }));
    }

    [TestMethod]
    public void AtLeastOneMealMustRemainActive()
    {
        foreach (var meal in service.Meals().Skip(1)) service.SaveMeal(meal with { IsActive = false });
        Assert.Throws<ValidationException>(() => service.SaveMeal(service.Meals()[0] with { IsActive = false }));
        Assert.AreEqual(1, service.Meals().Count(x => x.IsActive));
    }

    [TestMethod]
    public void EmptyDaysZeroEntriesAndDateBoundariesAreDistinct()
    {
        var from = new DateOnly(2024, 12, 31); var to = new DateOnly(2025, 1, 2);
        service.SaveEntry(Direct(0, from)); service.SaveEntry(Direct(100, to));
        var rows = service.History(from, to);
        Assert.HasCount(3, rows);
        Assert.AreEqual("未记录", rows.Single(x => x.Date == from.AddDays(1)).State);
        Assert.AreEqual("已记录 1 项", rows.Single(x => x.Date == from).State);
        Assert.AreEqual(10m, rows.Single(x => x.Date == to).EnergyKcal);
        Assert.Throws<ValidationException>(() => service.SaveEntry(Direct(10, Today.AddDays(1))));
        Assert.Throws<ValidationException>(() => service.History(to, from));
        Assert.Throws<ValidationException>(() => service.History(from, from.AddDays(3661)));
    }

    [TestMethod]
    public void InvalidEntriesAndFoodAreRejectedBeforeWriting()
    {
        foreach (var entry in new[] { Direct(-1), Direct() with { FoodNameSnapshot = " " }, Direct() with { LocalTime = "25:00" },
            Direct() with { MealCategoryId = Guid.NewGuid() }, Rice() with { EnergyKcalTenths = 1 }, Direct() with { InputMode = "unknown" } })
            Assert.Throws<ValidationException>(() => service.SaveEntry(entry));
        Assert.IsEmpty(store.ReadSnapshot().Entries);
        Assert.Throws<ValidationException>(() => service.SaveFood(package.Foods[0] with { KcalPer100gHundredths = -1 }));
        Assert.Throws<ValidationException>(() => service.SaveFood(package.Foods[0] with { Name = "" }));
    }

    [TestMethod]
    public void BackupRestoreRoundTripIncludesDeletedFoodsAndPreRestoreBackup()
    {
        service.SaveEntry(Rice());
        service.DeleteFood(service.Foods("黑咖啡").Single());
        var backups = new BackupService(store, Path.Combine(directory, "recovery"));
        var path = Path.Combine(directory, "full.calorie-backup.json"); backups.Export(path);
        var expected = backups.Inspect(path);
        service.SaveEntry(Direct(25));
        var beforePath = backups.Restore(expected);
        var before = backups.Inspect(beforePath);
        var actual = store.ReadSnapshot();
        Assert.HasCount(2, before.Entries);
        CollectionAssert.AreEqual(expected.Foods, actual.Foods);
        CollectionAssert.AreEqual(expected.Meals, actual.Meals);
        CollectionAssert.AreEqual(expected.Entries, actual.Entries);
        Assert.AreEqual(expected.FoodPackageVersion, actual.FoodPackageVersion);
        Assert.Throws<ValidationException>(() => backups.Export(path));
    }

    [TestMethod]
    public void InvalidBackupsDoNotChangeCurrentData()
    {
        service.SaveEntry(Direct());
        var snapshot = store.ReadSnapshot();
        var invalid = new[] { snapshot with { FormatVersion = 99 }, snapshot with { Entries = [snapshot.Entries[0], snapshot.Entries[0]] },
            snapshot with { Meals = [] }, snapshot with { Entries = [Direct() with { FoodId = Guid.NewGuid() }] },
            snapshot with { Foods = [snapshot.Foods[0], snapshot.Foods[0]] } };
        foreach (var value in invalid) Assert.Throws<ValidationException>(() => store.ReplaceSnapshot(value));
        Assert.AreEqual(snapshot.Entries[0], store.ReadSnapshot().Entries[0]);
        var path = Path.Combine(directory, "invalid.json");
        File.WriteAllText(path, "{\"FormatVersion\":1,\"FormatVersion\":2}");
        Assert.Throws<JsonException>(() => new BackupService(store, directory).Inspect(path));
    }

    [TestMethod]
    public void RealSqliteFailureRollsBackEntireReplacement()
    {
        service.SaveEntry(Rice());
        var original = store.ReadSnapshot();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER deny_test_insert BEFORE INSERT ON entries BEGIN SELECT RAISE(ABORT,'实际SQLite事务故障检查'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => store.ReplaceSnapshot(original));
        CollectionAssert.AreEqual(original.Foods, store.ReadSnapshot().Foods);
        CollectionAssert.AreEqual(original.Entries, store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void ActualDatabaseLockAndBackupDirectoryFailureAreVisible()
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        connection.Open(); using var transaction = connection.BeginTransaction();
        Assert.Throws<SqliteException>(() => service.SaveEntry(Direct()));
        transaction.Rollback();
        var file = Path.Combine(directory, "not-a-directory"); File.WriteAllText(file, "测试文件");
        Assert.Throws<IOException>(() => new BackupService(store, file).Restore(store.ReadSnapshot()));
        Assert.IsEmpty(store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void NewerOrUnrelatedDatabaseIsNeverReinitialized()
    {
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA user_version={SqliteJournalStore.SchemaVersion + 1}"; command.ExecuteNonQuery();
        }
        Assert.Throws<ValidationException>(() => store.Initialize(package));
        var otherPath = Path.Combine(directory, "other.db");
        using (var connection = new SqliteConnection($"Data Source={otherPath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE user_work(value TEXT); INSERT INTO user_work VALUES('保留');"; command.ExecuteNonQuery();
        }
        Assert.Throws<ValidationException>(() => new SqliteJournalStore(otherPath).Initialize(package));
    }

    [TestMethod]
    public void OnlineDatabaseBackupIsAnIndependentReadableDatabase()
    {
        service.SaveEntry(Rice());
        var path = Path.Combine(directory, "snapshot.db"); store.BackupDatabase(path);
        service.SaveEntry(Direct());
        var archived = new SqliteJournalStore(path).ReadSnapshot();
        Assert.HasCount(1, archived.Entries);
        Assert.HasCount(2, store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void RealCatalogQueriesAndWritesMeetLocalLatencyTargets()
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) Assert.HasCount(1, service.Foods("长粒白米饭"));
        var searchMs = watch.Elapsed.TotalMilliseconds / 10;
        watch.Restart(); service.SaveEntry(Rice()); var saveMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); _ = service.Entries(Today); var readMs = watch.Elapsed.TotalMilliseconds;
        TestContext.WriteLine($"真实60项库：搜索均值 {searchMs:F2}ms；保存 {saveMs:F2}ms；当日查询 {readMs:F2}ms");
        Assert.IsTrue(searchMs < 300 && saveMs < 300 && readMs < 300);
    }
}
