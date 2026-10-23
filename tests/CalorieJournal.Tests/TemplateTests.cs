using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class TemplateTests
{
    private static (string Directory, SqliteJournalStore Store, JournalService Journal) Open(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("未配置测试目录。"), "templates_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        return (directory, store, new JournalService(store));
    }

    private static MealTemplate Create(JournalService journal)
    {
        var foods = journal.Foods().Take(2).ToArray();
        var template = new MealTemplate { Id = Guid.NewGuid(), Name = "日常午餐", PreferredMealId = journal.Meals()[0].Id, Note = "核对实际吃下的重量",
            Items = foods.Select(x => journal.CreateTemplateItem(Guid.NewGuid(), x.Id, 1000)).ToArray() };
        journal.SaveTemplate(template); return template;
    }

    private static IntakeEntry[] Entries(JournalService journal, MealTemplate template)
    {
        var meal = journal.Meals().First(x => x.IsActive);
        return journal.RefreshTemplateItems(template).Select(x => new IntakeEntry { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today),
            MealCategoryId = meal.Id, MealNameSnapshot = meal.Name, FoodId = x.FoodId, FoodNameSnapshot = x.FoodNameSnapshot, PreparationSnapshot = x.PreparationSnapshot,
            InputMode = "weight", WeightGramsTenths = x.WeightGramsTenths, KcalPer100gHundredthsSnapshot = x.KcalPer100gHundredthsSnapshot,
            EnergyKcalTenths = Validation.CalculateEnergy(x.WeightGramsTenths, x.KcalPer100gHundredthsSnapshot), WeightCalculation = x.WeightCalculation, SourceSnapshot = x.SourceSnapshot }).ToArray();
    }

    [TestMethod]
    public void TemplateCrudUsesCurrentFoodAndPortionsWithoutChangingHistory()
    {
        var (directory, store, journal) = Open(); var template = Create(journal);
        Assert.IsEmpty(store.ReadSnapshot().Entries); Assert.HasCount(1, Open(directory).Journal.Templates("午餐"));
        var food = journal.Foods().Single(x => x.Id == template.Items[0].FoodId);
        var portion = new FoodPortion { Id = Guid.NewGuid(), FoodId = food.Id, Name = "一碗", GramsTenths = 1500 }; journal.SavePortion(portion);
        var calculation = new WeightCalculationSnapshot { Kind = "portion", PortionId = portion.Id, PortionName = portion.Name, PortionGramsTenths = portion.GramsTenths, PortionCountThousandths = 1500 };
        template = template with { Items = [journal.CreateTemplateItem(template.Items[0].Id, food.Id, 2250, calculation), template.Items[1]] };
        journal.SaveTemplate(template); var entries = Entries(journal, template); journal.SaveTemplateBatch(Guid.NewGuid(), template.Id, entries);
        journal.SaveFood(food with { KcalPer100gHundredths = 20000, UserModified = true }); journal.SavePortion(portion with { GramsTenths = 2000 });
        var fresh = journal.RefreshTemplateItems(template); Assert.AreEqual(3000L, fresh[0].WeightGramsTenths); Assert.AreEqual(20000L, fresh[0].KcalPer100gHundredthsSnapshot);
        Assert.AreEqual(JsonSerializer.Serialize(entries[0]), JsonSerializer.Serialize(store.FindEntry(entries[0].Id)));
        journal.SavePortion(portion with { IsDeleted = true }); Assert.Throws<ValidationException>(() => journal.RefreshTemplateItems(template));
        journal.DeleteFood(food); Assert.Throws<ValidationException>(() => journal.SaveTemplate(template));
        journal.SaveTemplate(template with { IsDeleted = true }); Assert.IsEmpty(journal.Templates());
        Assert.HasCount(1, Open(directory).Store.ReadTemplateBatches());
    }

    [TestMethod]
    public void BatchIsAtomicAndPersistentlyIdempotentEvenAfterDeletingIntake()
    {
        var (directory, store, journal) = Open(); var template = Create(journal); var entries = Entries(journal, template); var batchId = Guid.NewGuid();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER fail_second BEFORE INSERT ON entries WHEN NEW.id='{entries[1].Id}' BEGIN SELECT RAISE(ABORT,'明确第二项写入失败检查'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => journal.SaveTemplateBatch(batchId, template.Id, entries));
        Assert.IsEmpty(store.ReadSnapshot().Entries); Assert.IsEmpty(store.ReadTemplateBatches());
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TRIGGER fail_second"; command.ExecuteNonQuery(); }
        journal.SaveTemplateBatch(batchId, template.Id, entries); Assert.HasCount(2, store.ReadSnapshot().Entries);
        Assert.Throws<ValidationException>(() => Open(directory).Journal.SaveTemplateBatch(batchId, template.Id, Entries(journal, template)));
        journal.DeleteEntry(entries[0].Id); journal.DeleteEntry(entries[1].Id);
        Assert.Throws<ValidationException>(() => journal.SaveTemplateBatch(batchId, template.Id, Entries(journal, template)));
        journal.SaveTemplateBatch(Guid.NewGuid(), template.Id, Entries(journal, template)); Assert.HasCount(2, store.ReadSnapshot().Entries); Assert.HasCount(2, store.ReadTemplateBatches());
    }

    [TestMethod]
    public void FullBackupAndLegacyRestorePreserveNewDataInRecoveryArchive()
    {
        var (directory, store, journal) = Open(); var template = Create(journal); journal.SaveTemplateBatch(Guid.NewGuid(), template.Id, Entries(journal, template));
        var backups = new BackupService(store, Path.Combine(directory, "recovery")); var snapshot = store.ReadSnapshot();
        var path = Path.Combine(directory, "完整.calorie-backup.json"); backups.Export(path); var inspected = backups.Inspect(path);
        Assert.AreEqual(3, inspected.FormatVersion); Assert.HasCount(1, inspected.Templates!); Assert.HasCount(1, inspected.TemplateBatches!);
        foreach (var bad in new[] { snapshot with { Templates = null }, snapshot with { TemplateBatches = null }, snapshot with { Templates = [template, template] },
            snapshot with { TemplateBatches = [snapshot.TemplateBatches![0], snapshot.TemplateBatches[0]] },
            snapshot with { Templates = [template with { Items = [template.Items[0] with { FoodId = Guid.NewGuid() }] }] },
            snapshot with { TemplateBatches = [snapshot.TemplateBatches![0] with { TemplateId = Guid.NewGuid() }] }, snapshot with { FormatVersion = 2, SchemaVersion = 2 } })
        {
            Assert.Throws<ValidationException>(() => backups.Restore(bad));
            Assert.AreEqual(JsonSerializer.Serialize(snapshot.Templates), JsonSerializer.Serialize(store.ReadSnapshot().Templates));
        }
        var legacy = snapshot with { FormatVersion = 2, SchemaVersion = 2, Templates = null, TemplateBatches = null };
        var archive = backups.Restore(legacy); Assert.IsEmpty(store.ReadTemplates()); Assert.IsEmpty(store.ReadTemplateBatches());
        var saved = backups.Inspect(archive); Assert.HasCount(1, saved.Templates!); Assert.HasCount(1, saved.TemplateBatches!);
        backups.Restore(saved); var actual = store.ReadSnapshot();
        Assert.AreEqual(JsonSerializer.Serialize(snapshot.Templates), JsonSerializer.Serialize(actual.Templates));
        Assert.AreEqual(JsonSerializer.Serialize(snapshot.TemplateBatches), JsonSerializer.Serialize(actual.TemplateBatches));
        Assert.AreEqual(JsonSerializer.Serialize(snapshot.Entries), JsonSerializer.Serialize(actual.Entries));
    }

    [TestMethod]
    public void InvalidTemplatesAndInactiveReferencesAreRejectedBeforeAnyBatchWrite()
    {
        var (_, store, journal) = Open(); var template = Create(journal);
        foreach (var bad in new[] { template with { Items = [] }, template with { Name = " " }, template with { Items = [template.Items[0], template.Items[0]] },
            template with { PreferredMealId = Guid.Empty }, template with { Items = [template.Items[0] with { WeightGramsTenths = 0 }] } })
            Assert.Throws<ValidationException>(() => journal.SaveTemplate(bad));
        var entries = Entries(journal, template); var meal = journal.Meals().Single(x => x.Id == entries[0].MealCategoryId); journal.SaveMeal(meal with { IsActive = false });
        Assert.Throws<ValidationException>(() => journal.SaveTemplate(template));
        Assert.Throws<ValidationException>(() => journal.SaveTemplateBatch(Guid.NewGuid(), template.Id, entries));
        journal.SaveMeal(meal); journal.DeleteFood(journal.Foods().Single(x => x.Id == entries[0].FoodId));
        Assert.Throws<ValidationException>(() => journal.SaveTemplateBatch(Guid.NewGuid(), template.Id, entries));
        Assert.IsEmpty(store.ReadSnapshot().Entries); Assert.IsEmpty(store.ReadTemplateBatches());
    }
}
