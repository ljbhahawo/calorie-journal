using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class BodyTests
{
    private static (string Root, SqliteJournalStore Store, JournalService Journal) Open(string? root = null)
    {
        root ??= Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "body_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        return (root, store, new JournalService(store));
    }
    private static BodyMeasurement Value() => new() { Id = Guid.NewGuid(), Date = DateOnly.FromDateTime(DateTime.Today), WeightKgHundredths = 7000, HeightCmTenths = 1750, AgeYears = 30, FormulaSex = "male", WaistCmTenths = 800, Note = "明确的测量验收输入" };
    [TestMethod]
    public void PublishedFormulaCasesAndMissingInputsStayDistinct()
    {
        var value = Value(); Assert.AreEqual(70m / (1.75m * 1.75m), value.Bmi); Assert.AreEqual(1648.75m, value.RestingKcal);
        Assert.AreEqual(1482.75m, (value with { FormulaSex = "female" }).RestingKcal);
        Assert.AreEqual("17.51", (value with { WeightKgHundredths = 7002, HeightCmTenths = 2000 }).BmiText);
        Assert.AreEqual("1649.7 大卡/日", (value with { WeightKgHundredths = 7009 }).RestingText);
        foreach (var incomplete in new[] { value with { HeightCmTenths = null }, value with { AgeYears = null }, value with { FormulaSex = "unspecified" }, value with { AgeYears = 18 }, value with { AgeYears = 79 }, value with { WeightKgHundredths = 100, HeightCmTenths = 500, AgeYears = 78, FormulaSex = "female" } })
        { BodyCalculator.Validate(incomplete); Assert.IsNull(incomplete.RestingKcal); Assert.IsFalse(string.IsNullOrWhiteSpace(incomplete.RestingReason)); }
        Assert.IsNull((value with { HeightCmTenths = null }).Bmi);
    }
    [TestMethod]
    public void InvalidBodyInputsAndUnknownVersionsFailBeforeWriting()
    {
        var (_, store, journal) = Open(); var value = Value();
        foreach (var invalid in new[] { value with { Id = Guid.Empty }, value with { Date = value.Date.AddDays(1) }, value with { WeightKgHundredths = 0 }, value with { WeightKgHundredths = 100001 }, value with { HeightCmTenths = 0 }, value with { WaistCmTenths = 0 }, value with { AgeYears = 0 }, value with { FormulaSex = "guess" }, value with { FormulaVersion = "unknown" }, value with { Note = new string('a', 2001) } })
            Assert.Throws<ValidationException>(() => journal.SaveBodyMeasurement(invalid));
        Assert.IsEmpty(store.ReadBodyMeasurements()); Assert.IsEmpty(store.ReadSnapshot().Entries);
    }
    [TestMethod]
    public void RealCrudDuplicateDatesRestartAndHistoricalInputsArePreserved()
    {
        var (root, store, journal) = Open(); var value = Value(); journal.SaveBodyMeasurement(value);
        Assert.Throws<ValidationException>(() => journal.SaveBodyMeasurement(value with { Id = Guid.NewGuid() }));
        journal.SaveBodyMeasurement(value with { WeightKgHundredths = 7123 }); Assert.AreEqual(7123L, Open(root).Store.ReadBodyMeasurements()[0].WeightKgHundredths);
        var past = value with { Id = Guid.NewGuid(), Date = value.Date.AddDays(-2), HeightCmTenths = 1700, AgeYears = 29 }; journal.SaveBodyMeasurement(past);
        Assert.Throws<ValidationException>(() => journal.SaveBodyMeasurement(value with { Date = past.Date }));
        var rows = journal.BodyMeasurements(past.Date, value.Date); Assert.HasCount(2, rows); Assert.AreEqual(1700L, rows[1].HeightCmTenths); Assert.AreEqual(29, rows[1].AgeYears);
        Assert.Throws<ValidationException>(() => journal.BodyMeasurements(value.Date, past.Date));
        journal.DeleteBodyMeasurement(value.Id); Assert.HasCount(1, store.ReadBodyMeasurements()); Assert.Throws<ValidationException>(() => journal.DeleteBodyMeasurement(value.Id));
    }
    [TestMethod]
    public void FullBackupAndLegacyRestoreRetainEveryBodySnapshot()
    {
        var (root, store, journal) = Open(); journal.SaveBodyMeasurement(Value()); journal.SaveSettings(new JournalSettings { DefaultWeightMode = false, NewFoodMode = "auto" });
        var before = store.ReadSnapshot(); var backups = new BackupService(store, Path.Combine(root, "recovery"));
        foreach (var invalid in new[] { before with { BodyMeasurements = null }, before with { BodyMeasurements = [before.BodyMeasurements![0], before.BodyMeasurements[0]] }, before with { BodyMeasurements = [before.BodyMeasurements![0] with { FormulaVersion = "unknown" }] } }) Assert.Throws<ValidationException>(() => backups.Restore(invalid));
        var file = Path.Combine(root, "body.calorie-backup.json"); backups.Export(file);
        var old = before with { FormatVersion = 4, SchemaVersion = 4, BodyMeasurements = null }; var archive = backups.Restore(old);
        Assert.IsEmpty(store.ReadBodyMeasurements()); Assert.AreEqual(before.Settings, journal.Settings()); backups.Restore(backups.Inspect(archive));
        Assert.AreEqual(JsonSerializer.Serialize(before.BodyMeasurements), JsonSerializer.Serialize(store.ReadSnapshot().BodyMeasurements));
        journal.DeleteBodyMeasurement(before.BodyMeasurements![0].Id); backups.Restore(backups.Inspect(file)); Assert.HasCount(1, store.ReadBodyMeasurements());
    }
    [TestMethod]
    public void RealRestoreFailureLeavesBodyAndOtherCollectionsUntouched()
    {
        var (root, store, journal) = Open(); journal.SaveBodyMeasurement(Value()); var before = store.ReadSnapshot();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER deny_body BEFORE INSERT ON body_measurements BEGIN SELECT RAISE(ABORT,'明确身体记录故障'); END;"; command.ExecuteNonQuery(); }
        var backups = new BackupService(store, Path.Combine(root, "recovery")); Assert.Throws<SqliteException>(() => backups.Restore(before));
        var after = store.ReadSnapshot(); CollectionAssert.AreEqual(before.BodyMeasurements, after.BodyMeasurements); CollectionAssert.AreEqual(before.Foods, after.Foods); Assert.AreEqual(before.Settings, after.Settings);
        Assert.Throws<SqliteException>(() => journal.SaveBodyMeasurement(Value() with { Date = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)) })); Assert.HasCount(1, store.ReadBodyMeasurements());
    }
    [TestMethod]
    public void HierarchyUsesMealIdsAndNeverChangesEnergyTotals()
    {
        var (_, store, journal) = Open(); var meals = journal.Meals(); var custom = new MealCategory { Id = Guid.NewGuid(), Name = "早餐前", SortOrder = 5 }; journal.SaveMeal(custom);
        foreach (var meal in new[] { meals[0], meals[0], custom }) journal.SaveEntry(new IntakeEntry { Id = Guid.NewGuid(), EntryDate = Value().Date, MealCategoryId = meal.Id, MealNameSnapshot = meal.Name, FoodNameSnapshot = "层级验收输入", EnergyKcalTenths = 100 });
        var day = journal.BrowseDays(Value().Date, Value().Date).Single(); Assert.HasCount(2, day.Meals); Assert.HasCount(2, day.Meals[0].Entries); Assert.AreEqual(300L, day.Meals.SelectMany(x => x.Entries).Sum(x => x.EnergyKcalTenths));
        var before = JsonSerializer.Serialize(store.ReadSnapshot().Entries); journal.SaveBodyMeasurement(Value()); Assert.AreEqual(before, JsonSerializer.Serialize(store.ReadSnapshot().Entries));
    }
}
