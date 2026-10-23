using System.Diagnostics;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class BackupProtectionTests
{
    private string root = null!;
    private SqliteJournalStore store = null!;
    private JournalService journal = null!;
    private BackupService backups = null!;
    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "backup_protection_" + Guid.NewGuid().ToString("N"));
        store = new SqliteJournalStore(Path.Combine(root, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        journal = new JournalService(store); backups = new BackupService(store, Path.Combine(root, "recovery"));
    }
    private IntakeEntry Entry() => new() { Id = Guid.NewGuid(), EntryDate = new DateOnly(2024, 1, 1), MealCategoryId = journal.Meals()[0].Id,
        MealNameSnapshot = "早餐", FoodNameSnapshot = "明确的容量测试输入", EnergyKcalTenths = 0 };

    [TestMethod]
    public void BackupLargerThanFiftyMebibytesReallyReadsAndRestores()
    {
        var watch = Stopwatch.StartNew();
        var seed = Entry() with { SourceSnapshot = new string('S', 3000), Note = new string('N', 1000) };
        var entries = Enumerable.Range(0, 14000).Select(_ => seed with { Id = Guid.NewGuid() }).ToArray();
        store.ReplaceSnapshot(store.ReadSnapshot() with { Entries = entries });
        var path = Path.Combine(root, "large.calorie-backup.json"); backups.Export(path);
        Assert.IsTrue(new FileInfo(path).Length > JsonFiles.MaximumDocumentBytes);
        Assert.Throws<ValidationException>(() => JsonFiles.Read<FoodPackage>(path));
        var snapshot = backups.Inspect(path); Assert.HasCount(14000, snapshot.Entries);
        var restored = new SqliteJournalStore(Path.Combine(root, "restored", "journal.db"));
        restored.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        new BackupService(restored, Path.Combine(root, "restored-recovery")).Restore(snapshot);
        Assert.AreEqual(JsonSerializer.Serialize(snapshot.Entries), JsonSerializer.Serialize(restored.ReadSnapshot().Entries));
        JsonFiles.WriteNew(Path.Combine(root, "large-backup-check.json"), new { Success = true, Entries = entries.Length, Bytes = new FileInfo(path).Length,
            ElapsedMilliseconds = watch.ElapsedMilliseconds, ActualSqliteAndJson = true, ReadBackBeforePublish = true });
    }

    [TestMethod]
    public void EntryLimitProtectsEveryWriteTransactionAndOldOversizeDataCanBeArchived()
    {
        var seed = Entry(); journal.SaveEntry(seed); FillEntries(100000);
        journal.SaveEntry(seed with { Note = "上限仍允许编辑" });
        var count = store.ReadSnapshot().Entries.Length; Assert.AreEqual(100000, count);
        Assert.Throws<ValidationException>(() => journal.SaveEntry(Entry()));
        Assert.Throws<ValidationException>(() => store.AppendEntries([Entry(), Entry()]));
        var food = journal.Foods()[0]; var foodsBefore = journal.Foods().Length;
        var weighted = Entry() with { FoodNameSnapshot = "明确新的联写食物", InputMode = "weight", WeightGramsTenths = 1000,
            KcalPer100gHundredthsSnapshot = 12300, EnergyKcalTenths = 1230 };
        Assert.Throws<ValidationException>(() => journal.SaveManualEntry(weighted, true));
        Assert.AreEqual(foodsBefore, journal.Foods().Length);
        var template = new MealTemplate { Id = Guid.NewGuid(), Name = "容量检查模板", Items = [journal.CreateTemplateItem(Guid.NewGuid(), food.Id, 1000)] };
        journal.SaveTemplate(template);
        var batchEntry = weighted with { Id = Guid.NewGuid(), FoodId = food.Id, KcalPer100gHundredthsSnapshot = food.KcalPer100gHundredths,
            EnergyKcalTenths = Nutrition.Energy(1000, food.KcalPer100gHundredths) };
        var receipt = new TemplateBatchReceipt { Id = Guid.NewGuid(), TemplateId = template.Id, EntryIds = [batchEntry.Id] };
        Assert.Throws<ValidationException>(() => store.AppendTemplateBatch(receipt, [batchEntry]));
        Assert.IsEmpty(store.ReadTemplateBatches()); Assert.HasCount(count, store.ReadSnapshot().Entries);
        FillEntries(100001); // 明确构造旧版本可能留下的超限库，不通过产品保存入口绕过限制。
        var archive = Path.Combine(root, "oversize.db"); backups.ExportDatabase(archive);
        Assert.HasCount(100001, new SqliteJournalStore(archive).ReadSnapshot().Entries);
        var invalidExport = Path.Combine(root, "invalid.calorie-backup.json");
        Assert.Throws<ValidationException>(() => backups.Export(invalidExport)); Assert.IsFalse(File.Exists(invalidExport));
        journal.SaveEntry(seed with { Note = "旧超限仍可编辑" }); journal.DeleteEntry(seed.Id);
        Assert.HasCount(100000, store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void MealCapacityAndUnreadableRecoveryProtectTheOriginalDatabase()
    {
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 5 UNION ALL SELECT i+1 FROM n WHERE i<1000) INSERT INTO meals SELECT printf('10000000-0000-0000-0000-%012d',i),'容量餐次'||i,i,1 FROM n";
            command.ExecuteNonQuery();
        }
        var meal = journal.Meals()[0]; journal.SaveMeal(meal with { Name = "修改已有餐次" });
        Assert.Throws<ValidationException>(() => journal.SaveMeal(new MealCategory { Id = Guid.NewGuid(), Name = "第1001餐" }));
        var current = store.ReadSnapshot();
        using var held = new FileStream(Path.Combine(root, "recovery"), FileMode.CreateNew);
        Assert.Throws<IOException>(() => backups.Restore(current));
        Assert.AreEqual(JsonSerializer.Serialize(current.Meals), JsonSerializer.Serialize(store.ReadSnapshot().Meals));
        Assert.Throws<JsonException>(() => { var malformed = Path.Combine(root, "malformed.json"); File.WriteAllText(malformed, "{\"FormatVersion\":8,\"FormatVersion\":7}"); backups.Inspect(malformed); });
    }

    [TestMethod]
    public void SafeByteBudgetRejectsGrowthAndKeepsDatabaseArchiveAvailable()
    {
        var seed = Entry() with { SourceSnapshot = new string('S', 3000), Note = new string('N', 1000) };
        journal.SaveEntry(seed); FillEntries(40000);
        Assert.Throws<ValidationException>(() => journal.SaveEntry(Entry()));
        journal.SaveEntry(seed with { Note = "缩短旧资料" });
        var archive = Path.Combine(root, "over-budget.db"); backups.ExportDatabase(archive);
        Assert.HasCount(40000, new SqliteJournalStore(archive).ReadSnapshot().Entries);
        Assert.Throws<ValidationException>(() => backups.ExportDatabase(archive));
        Assert.IsEmpty(Directory.GetFiles(root, "*.writing"));
    }

    private void FillEntries(int total)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$total)
            INSERT OR IGNORE INTO entries SELECT printf('20000000-0000-0000-0000-%012d',i), entry_date,local_time,meal_id,meal_name,food_id,food_name,
            preparation,input_mode,grams_tenths,kcal_hundredths,source_snapshot,energy_tenths,note,created_at,updated_at,weight_calculation,
            protein_unit_hundredths,protein_hundredths,kj_hundredths FROM n CROSS JOIN (SELECT * FROM entries LIMIT 1)
            """;
        command.Parameters.AddWithValue("$total", total - 1); command.ExecuteNonQuery();
    }
}
