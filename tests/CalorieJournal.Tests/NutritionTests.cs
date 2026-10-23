using System.IO.Compression;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class NutritionTests
{
    private static (string Root, SqliteJournalStore Store, JournalService Journal) Open()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "nutrition_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(Package()); return (root, store, new JournalService(store));
    }
    private static FoodPackage Package() => JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json"));
    private static FoodItem Food() => new() { Id = Guid.NewGuid(), Name = "明确的营养标签验收食物", KcalPer100gHundredths = 10000, KilojoulesPer100gHundredths = 41840, ProteinPer100gHundredths = 2000, UserModified = true };
    private static IntakeEntry Entry(JournalService journal, FoodItem food) => new() { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), MealCategoryId = journal.Meals()[0].Id, MealNameSnapshot = journal.Meals()[0].Name,
        FoodNameSnapshot = food.Name, FoodId = food.Id, InputMode = "weight", WeightGramsTenths = 1500, KcalPer100gHundredthsSnapshot = food.KcalPer100gHundredths,
        KilojoulesHundredthsSnapshot = food.KilojoulesPer100gHundredths, EnergyKcalTenths = 1500, ProteinPer100gHundredthsSnapshot = 2000, ProteinGramsHundredths = 3000 };
    [TestMethod]
    public void KilojouleFormulaSingleRoundingAndInputBoundaries()
    {
        Assert.AreEqual(41840L, Nutrition.Kilojoules(418.4m, 10000)); Assert.AreEqual(10000L, Nutrition.UnitKcal(41840)); Assert.AreEqual(1000L, Nutrition.DirectKcal(41840));
        Assert.AreEqual(239L, Nutrition.Energy(100000, 24, 100)); Assert.AreEqual(240L, Validation.CalculateEnergy(100000, 24));
        Assert.AreEqual(1L, Nutrition.Protein(500, 1)); Assert.AreEqual(0L, Nutrition.Protein(1, 0)); Assert.IsNull(Nutrition.Protein(1000, null));
        foreach (var invalid in new[] { -0.01m, 41840.01m, 418.401m }) Assert.Throws<ValidationException>(() => Nutrition.Kilojoules(invalid, 10000));
        Assert.Throws<ValidationException>(() => Nutrition.Energy(1000, 10001, 41840)); Assert.Throws<ValidationException>(() => Nutrition.UnitProtein(10001));
        Assert.Throws<ValidationException>(() => Nutrition.DirectKcal(-1)); Assert.Throws<ValidationException>(() => Nutrition.DirectKcal(41840000));
    }
    [TestMethod]
    public void RealFoodIntakeSnapshotsRestartAndLaterFoodEdits()
    {
        var (_, store, journal) = Open(); var food = Food(); journal.SaveFood(food); var entry = Entry(journal, food); journal.SaveEntry(entry);
        var first = store.FindEntry(entry.Id)!; Assert.AreEqual(3000L, first.ProteinGramsHundredths); Assert.IsTrue(first.Calculation.Contains("418.40 kJ/100g"));
        journal.SaveFood(food with { ProteinPer100gHundredths = 4000, KilojoulesPer100gHundredths = 83680, KcalPer100gHundredths = 20000 });
        journal.DeleteFood(journal.Foods().Single(x => x.Id == food.Id));
        var restart = new SqliteJournalStore(store.DatabasePath); Assert.AreEqual(first, restart.FindEntry(entry.Id)); Assert.AreEqual(4000L, restart.ReadFoods(true).Single(x => x.Id == food.Id).ProteinPer100gHundredths);
        foreach (var bad in new[] { first with { ProteinGramsHundredths = 3001 }, first with { ProteinPer100gHundredthsSnapshot = null }, first with { KilojoulesHundredthsSnapshot = 1 } }) Assert.Throws<ValidationException>(() => journal.SaveEntry(bad));
    }
    [TestMethod]
    public void DirectProteinMissingZeroAndSummaryNeverInventTotals()
    {
        var (_, store, journal) = Open(); var food = Food(); var entry = Entry(journal, food) with { FoodId = null, InputMode = "direct", WeightGramsTenths = null, KcalPer100gHundredthsSnapshot = null, KilojoulesHundredthsSnapshot = 41840, EnergyKcalTenths = 1000, ProteinPer100gHundredthsSnapshot = null, ProteinGramsHundredths = null };
        journal.SaveEntry(entry); Assert.IsTrue(Nutrition.ProteinSummary(journal.Entries(entry.EntryDate)).Contains("未填写"));
        journal.SaveEntry(entry with { Id = Guid.NewGuid(), ProteinGramsHundredths = 0 }); var rows = journal.Entries(entry.EntryDate); Assert.IsTrue(Nutrition.ProteinSummary(rows).Contains("0.00 g")); Assert.IsTrue(Nutrition.ProteinSummary(rows).Contains("1项未填写"));
        Assert.IsNull(rows.Single(x => x.Id == entry.Id).ProteinGramsHundredths); Assert.HasCount(2, store.ReadSnapshot().Entries);
        foreach (var bad in new[] { entry with { ProteinGramsHundredths = -1 }, entry with { ProteinGramsHundredths = 10000001 }, entry with { ProteinPer100gHundredthsSnapshot = 0 }, entry with { EnergyKcalTenths = 999 } }) Assert.Throws<ValidationException>(() => journal.SaveEntry(bad));
    }
    [TestMethod]
    public void NewFoodPolicyIncludesProteinAndOriginalKilojoules()
    {
        var (_, store, journal) = Open(); var food = Food(); var entry = Entry(journal, food) with { FoodId = null };
        Assert.IsTrue(journal.SaveManualEntry(entry, true)); var added = journal.Foods().Single(x => x.Name == food.Name);
        Assert.AreEqual(food.ProteinPer100gHundredths, added.ProteinPer100gHundredths); Assert.AreEqual(food.KilojoulesPer100gHundredths, added.KilojoulesPer100gHundredths);
        Assert.IsFalse(journal.SaveManualEntry(entry with { Id = Guid.NewGuid() }, true));
        Assert.IsTrue(journal.SaveManualEntry(entry with { Id = Guid.NewGuid(), ProteinPer100gHundredthsSnapshot = 3000, ProteinGramsHundredths = 4500 }, true));
        Assert.HasCount(2, journal.Foods().Where(x => x.Name == food.Name).ToArray()); Assert.HasCount(3, store.ReadSnapshot().Entries);
    }
    [TestMethod]
    public void TemplatesPreserveNutritionWhileFutureReviewUsesCurrentFood()
    {
        var (_, store, journal) = Open(); var food = Food(); journal.SaveFood(food); var item = journal.CreateTemplateItem(Guid.NewGuid(), food.Id, 1500);
        var template = new MealTemplate { Id = Guid.NewGuid(), Name = "营养模板明确输入", Items = [item] }; journal.SaveTemplate(template);
        Assert.AreEqual(2000L, store.ReadTemplates().Single().Items[0].ProteinPer100gHundredthsSnapshot);
        var entry = Entry(journal, food); journal.SaveTemplateBatch(Guid.NewGuid(), template.Id, [entry]); var original = store.FindEntry(entry.Id);
        journal.SaveFood(food with { ProteinPer100gHundredths = 2500 }); Assert.AreEqual(2500L, journal.RefreshTemplateItems(template)[0].ProteinPer100gHundredthsSnapshot); Assert.AreEqual(original, store.FindEntry(entry.Id));
    }
    [TestMethod]
    public void CompleteBackupRejectsInconsistentFieldsAndLegacySixCanReturn()
    {
        var (root, store, journal) = Open(); var food = Food(); journal.SaveFood(food); journal.SaveEntry(Entry(journal, food)); var before = store.ReadSnapshot();
        var backups = new BackupService(store, Path.Combine(root, "recovery")); var file = Path.Combine(root, "nutrition.calorie-backup.json"); backups.Export(file);
        Assert.Throws<ValidationException>(() => backups.Restore(before with { FormatVersion = 6, SchemaVersion = 6 }));
        Assert.Throws<ValidationException>(() => backups.Restore(before with { Entries = [before.Entries[0] with { ProteinGramsHundredths = 1 }] }));
        var legacy = before with { FormatVersion = 6, SchemaVersion = 6, Foods = before.Foods.Select(x => x with { ProteinPer100gHundredths = null, KilojoulesPer100gHundredths = null }).ToArray(), Entries = before.Entries.Select(x => x with { ProteinPer100gHundredthsSnapshot = null, ProteinGramsHundredths = null, KilojoulesHundredthsSnapshot = null }).ToArray() };
        var archive = backups.Restore(legacy); Assert.IsNull(store.ReadSnapshot().Entries[0].ProteinGramsHundredths); backups.Restore(backups.Inspect(archive));
        Assert.AreEqual(JsonSerializer.Serialize(before.Entries), JsonSerializer.Serialize(store.ReadSnapshot().Entries)); Assert.AreEqual(JsonSerializer.Serialize(before.Foods), JsonSerializer.Serialize(store.ReadSnapshot().Foods));
    }
    [TestMethod]
    public async Task OfficialCatalogSupplementAndRealTfdaSampleRetainMissingValues()
    {
        var (root, _, _) = Open(); var directory = Path.Combine(AppContext.BaseDirectory, "data/lookup"); var catalogs = new FoodLookupService(directory, Path.Combine(root, "lookup")).Load();
        var usda = catalogs.Single(x => x.Provider == "USDA SR Legacy"); var tfda = catalogs.Single(x => x.Provider == "TFDA");
        Assert.HasCount(7793, usda.Foods); Assert.AreEqual(7793, usda.Foods.Count(x => x.ProteinPer100gHundredths is not null)); Assert.AreEqual(30, tfda.Foods.Count(x => x.ProteinPer100gHundredths is null));
        Assert.AreEqual(109L, usda.Foods.Single(x => x.SourceId == "173944").ProteinPer100gHundredths); var hit = new FoodReferenceHit(usda, usda.Foods.Single(x => x.SourceId == "173944")); Assert.AreEqual(109L, hit.CreateFood().ProteinPer100gHundredths);
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures/tfda-sample.zip")); var parsed = await FoodLookupService.ReadTfdaAsync(stream, DateTimeOffset.UtcNow, CancellationToken.None);
        foreach (var item in parsed.Foods) Assert.AreEqual(tfda.Foods.Single(x => x.SourceId == item.SourceId).ProteinPer100gHundredths, item.ProteinPer100gHundredths);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true); using var json = JsonDocument.Parse(zip.Entries[0].Open());
        foreach (var row in json.RootElement.EnumerateArray().Where(x => x.GetProperty("分析項").GetString() == "粗蛋白")) Assert.IsTrue(parsed.Foods.Any(x => x.SourceId == row.GetProperty("整合編號").GetString()));
        Assert.IsTrue(JsonFiles.Read<FoodReferenceCatalog>(Path.Combine(directory, "usda.json")).Foods.All(x => x.ProteinPer100gHundredths is null));
    }
    [TestMethod]
    public void EnrichmentPreservesModifiedDeletedFoodsAndExistingIntake()
    {
        var (_, store, journal) = Open(); var foods = store.ReadFoods(); journal.SaveFood(foods[0] with { ProteinPer100gHundredths = 1234, UserModified = true }); journal.DeleteFood(foods[1]);
        var meal = journal.Meals()[0]; journal.SaveEntry(new IntakeEntry { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), MealCategoryId = meal.Id, MealNameSnapshot = meal.Name, FoodNameSnapshot = foods[2].Name, FoodId = foods[2].Id, EnergyKcalTenths = 1000 });
        var before = store.ReadSnapshot().Entries; var enriched = ProteinSupplement.EnrichBuiltin(Package(), Path.Combine(AppContext.BaseDirectory, "data/lookup")); store.Initialize(enriched); store.Initialize(enriched);
        Assert.AreEqual(1234L, store.ReadFoods(true).Single(x => x.Id == foods[0].Id).ProteinPer100gHundredths); Assert.IsNull(store.ReadFoods(true).Single(x => x.Id == foods[1].Id).ProteinPer100gHundredths);
        Assert.IsNotNull(store.ReadFoods().Single(x => x.Id == foods[2].Id).ProteinPer100gHundredths); CollectionAssert.AreEqual(before, store.ReadSnapshot().Entries);
    }
    [TestMethod]
    public void RealSqliteRestoreFailureKeepsNutritionAndOtherCollections()
    {
        var (root, store, journal) = Open(); var food = Food(); journal.SaveFood(food); journal.SaveEntry(Entry(journal, food)); var before = store.ReadSnapshot();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False")) { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER deny_nutrition BEFORE INSERT ON entries BEGIN SELECT RAISE(ABORT,'明确营养记录事务故障'); END;"; command.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => new BackupService(store, Path.Combine(root, "recovery")).Restore(before)); var after = store.ReadSnapshot(); CollectionAssert.AreEqual(before.Entries, after.Entries); CollectionAssert.AreEqual(before.Foods, after.Foods);
    }
}
