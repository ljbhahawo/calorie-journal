using System.Text.Json;
using System.Security.Cryptography;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class PortionTests
{
    private static string NewDirectory() => Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")
        ?? throw new InvalidOperationException("未配置测试目录。"), "portions_" + Guid.NewGuid().ToString("N"));

    private static (SqliteJournalStore Store, JournalService Journal) Open(string directory)
    {
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        return (store, new JournalService(store));
    }

    [TestMethod]
    public void ReinitializingUnchangedPackageDoesNotWriteDatabaseBytes()
    {
        var directory = NewDirectory(); var (store, _) = Open(directory);
        var before = SHA256.HashData(File.ReadAllBytes(store.DatabasePath));
        _ = Open(directory);
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(store.DatabasePath)));
    }

    [TestMethod]
    public void GrossDeductionsAndPortionRoundingRejectInvalidInputs()
    {
        var gross = new WeightCalculationSnapshot { Kind = "gross", GrossGramsTenths = WeightCalculator.Grams(0.536m, "kg", "总重"), Deductions = [new("梗", 2210), new("外皮", 100)] };
        Assert.AreEqual(3050L, gross.NetGramsTenths);
        Assert.AreEqual(2745L, Validation.CalculateEnergy(gross.NetGramsTenths, 9000));
        var portion = new WeightCalculationSnapshot { Kind = "portion", PortionId = Guid.NewGuid(), PortionName = "一勺", PortionGramsTenths = 250, PortionCountThousandths = 333 };
        Assert.AreEqual(83L, portion.NetGramsTenths);
        Assert.AreEqual(1L, (portion with { PortionGramsTenths = 1, PortionCountThousandths = 500 }).NetGramsTenths);
        foreach (var value in new[] { gross with { GrossGramsTenths = 100 }, gross with { GrossGramsTenths = 2310 }, gross with { Deductions = [] }, gross with { Deductions = [new("", 0)] },
            gross with { Deductions = [new("骨", -1)] }, portion with { PortionCountThousandths = 0 }, portion with { PortionCountThousandths = 100_000_001 }, portion with { PortionGramsTenths = 0 },
            portion with { GrossGramsTenths = 1 }, portion with { PortionName = " " }, portion with { PortionCountThousandths = 1, PortionGramsTenths = 1 }, portion with { Kind = "unknown" } })
            Assert.Throws<ValidationException>(() => WeightCalculator.Calculate(value));
        Assert.Throws<ValidationException>(() => WeightCalculator.Grams(0.11m, "g", "重量"));
        Assert.Throws<ValidationException>(() => WeightCalculator.Grams(1, "ml", "重量"));
        Assert.Throws<ValidationException>(() => WeightCalculator.Grams(-1, "g", "重量"));
        Assert.Throws<ValidationException>(() => WeightCalculator.Grams(decimal.MaxValue, "kg", "重量"));
    }

    [TestMethod]
    public void RealPortionCrudRestartAndHistoricalCalculationSurviveDefinitionChanges()
    {
        var directory = NewDirectory(); var (store, journal) = Open(directory);
        var food = journal.Foods("长粒白米饭").Single(); var meal = journal.Meals()[0];
        var definition = new FoodPortion { Id = Guid.NewGuid(), FoodId = food.Id, Name = "一碗", GramsTenths = 1500 };
        journal.SavePortion(definition);
        Assert.HasCount(1, Open(directory).Journal.Portions(food.Id));
        Assert.Throws<ValidationException>(() => journal.SavePortion(definition with { Id = Guid.NewGuid() }));
        Assert.Throws<ValidationException>(() => journal.SavePortion(definition with { FoodId = Guid.NewGuid() }));
        var calculation = new WeightCalculationSnapshot { Kind = "portion", PortionId = definition.Id, PortionName = definition.Name, PortionGramsTenths = definition.GramsTenths, PortionCountThousandths = 1500 };
        var entry = new IntakeEntry
        {
            Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), MealCategoryId = meal.Id, MealNameSnapshot = meal.Name,
            FoodId = food.Id, FoodNameSnapshot = food.Name, InputMode = "weight", WeightGramsTenths = calculation.NetGramsTenths,
            KcalPer100gHundredthsSnapshot = food.KcalPer100gHundredths, EnergyKcalTenths = Validation.CalculateEnergy(calculation.NetGramsTenths, food.KcalPer100gHundredths), WeightCalculation = calculation
        };
        journal.SaveEntry(entry);
        journal.SavePortion(definition with { GramsTenths = 2000 });
        journal.SavePortion(definition with { IsDeleted = true });
        Assert.IsEmpty(journal.Portions(food.Id));
        var actual = Open(directory).Store.FindEntry(entry.Id)!;
        Assert.AreEqual(2925L, actual.EnergyKcalTenths);
        Assert.AreEqual(1500L, actual.WeightCalculation!.PortionGramsTenths);
        Assert.AreEqual(2250L, actual.WeightGramsTenths);
        Assert.AreEqual("一碗 1.5份 × 150.0g/份", actual.WeightCalculation.Description);
        journal.SaveEntry(actual with { Note = "保留原份量依据" });
        Assert.Throws<ValidationException>(() => journal.SaveEntry(actual with { WeightGramsTenths = 1000, EnergyKcalTenths = Validation.CalculateEnergy(1000, food.KcalPer100gHundredths) }));
        Assert.Throws<ValidationException>(() => journal.SaveEntry(actual with { InputMode = "direct", WeightGramsTenths = null, KcalPer100gHundredthsSnapshot = null }));
        journal.DeleteFood(food);
        Assert.Throws<ValidationException>(() => journal.SavePortion(definition with { Name = "另一个份量" }));
    }

    [TestMethod]
    public void FullBackupIncludesPortionsAndGrossSnapshotWithLegacyRestoreValidation()
    {
        var directory = NewDirectory(); var (store, journal) = Open(directory);
        var food = journal.Foods()[0]; var meal = journal.Meals()[0];
        var portion = new FoodPortion { Id = Guid.NewGuid(), FoodId = food.Id, Name = "一份", GramsTenths = 500 };
        journal.SavePortion(portion);
        var calculation = new WeightCalculationSnapshot { Kind = "gross", GrossGramsTenths = 1230, Deductions = [new("壳", 170)] };
        journal.SaveEntry(new IntakeEntry { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), MealCategoryId = meal.Id, MealNameSnapshot = meal.Name,
            FoodNameSnapshot = "明确扣重测试输入", InputMode = "weight", WeightGramsTenths = 1060, KcalPer100gHundredthsSnapshot = 10000, EnergyKcalTenths = 1060, WeightCalculation = calculation });
        var expected = store.ReadSnapshot(); var backups = new BackupService(store, Path.Combine(directory, "recovery"));
        var path = Path.Combine(directory, "完整.calorie-backup.json"); backups.Export(path);
        Assert.AreEqual(2, backups.Inspect(path).FormatVersion);
        journal.SavePortion(portion with { GramsTenths = 600 });
        backups.Restore(backups.Inspect(path));
        Assert.AreEqual(JsonSerializer.Serialize(expected.Portions), JsonSerializer.Serialize(store.ReadSnapshot().Portions));
        Assert.AreEqual(JsonSerializer.Serialize(expected.Entries), JsonSerializer.Serialize(store.ReadSnapshot().Entries));
        foreach (var bad in new[] { expected with { Portions = null }, expected with { Portions = [portion, portion] }, expected with { Portions = [portion with { FoodId = Guid.NewGuid() }] },
            expected with { FormatVersion = 1, SchemaVersion = 1 }, expected with { Portions = [portion with { GramsTenths = 0 }] } })
            Assert.Throws<ValidationException>(() => backups.Restore(bad));
        var legacy = expected with { FormatVersion = 1, SchemaVersion = 1, Portions = null, Entries = expected.Entries.Select(x => x with { WeightCalculation = null }).ToArray() };
        backups.Restore(legacy);
        Assert.IsEmpty(store.ReadSnapshot().Portions!);
        Assert.IsNull(store.ReadSnapshot().Entries[0].WeightCalculation);
        var archived = backups.Inspect(Directory.GetFiles(Path.Combine(directory, "recovery"), "*.calorie-backup.json").OrderBy(File.GetLastWriteTimeUtc).Last());
        Assert.HasCount(1, archived.Portions!);
    }

    [TestMethod]
    public void ActualSqliteFailureDoesNotPartiallyReplacePortionsOrEntries()
    {
        var directory = NewDirectory(); var (store, journal) = Open(directory);
        var portion = new FoodPortion { Id = Guid.NewGuid(), FoodId = journal.Foods()[0].Id, Name = "一份", GramsTenths = 500 };
        journal.SavePortion(portion); var before = store.ReadSnapshot();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER deny_portion_insert BEFORE INSERT ON portions BEGIN SELECT RAISE(ABORT,'明确SQLite份量写入失败检查'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => store.ReplaceSnapshot(before));
        CollectionAssert.AreEqual(before.Portions, store.ReadSnapshot().Portions);
        CollectionAssert.AreEqual(before.Foods, store.ReadSnapshot().Foods);
    }
}
