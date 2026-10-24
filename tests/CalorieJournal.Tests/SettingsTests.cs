using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class SettingsTests
{
    private static (string Root, SqliteJournalStore Store, JournalService Journal) Open(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "settings_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        return (directory, store, new JournalService(store));
    }
    private static IntakeEntry Entry(JournalService journal, string name = "手动验收食物")
    {
        var meal = journal.Meals().First();
        return new IntakeEntry { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), MealCategoryId = meal.Id, MealNameSnapshot = meal.Name,
            FoodNameSnapshot = name, PreparationSnapshot = "熟", InputMode = "weight", WeightGramsTenths = 1500, KcalPer100gHundredthsSnapshot = 8000, EnergyKcalTenths = 1200 };
    }
    [TestMethod]
    public void SettingsRestartBackupAndLegacyRestoreAreExplicit()
    {
        var (root, store, journal) = Open(); Assert.AreEqual(JournalSettings.Default, journal.Settings());
        var settings = new JournalSettings { DefaultWeightMode = false, NewFoodMode = "auto" }; journal.SaveSettings(settings);
        Assert.AreEqual(settings, Open(root).Journal.Settings());
        Assert.Throws<ValidationException>(() => journal.SaveSettings(settings with { NewFoodMode = "invalid" }));
        var snapshot = store.ReadSnapshot(); var backups = new BackupService(store, Path.Combine(root, "recovery"));
        Assert.Throws<ValidationException>(() => backups.Restore(snapshot with { Settings = null }));
        var archive = backups.Restore(snapshot with { FormatVersion = 3, SchemaVersion = 3, Settings = null, BodyMeasurements = null, IntakeGoals = null, Exercises = null });
        Assert.AreEqual(JournalSettings.Default, journal.Settings()); backups.Restore(backups.Inspect(archive)); Assert.AreEqual(settings, journal.Settings());
    }
    [TestMethod]
    public void SheetSettingPersistsAndRejectsLegacyClaimWithoutLosingData()
    {
        var (root, store, journal) = Open(); journal.SaveSettings(JournalSettings.Default with { DailyRecordPage = "sheet" });
        Assert.AreEqual("sheet", Open(root).Journal.Settings().DailyRecordPage);
        Assert.Throws<ValidationException>(() => journal.SaveSettings(JournalSettings.Default with { DailyRecordPage = "invalid" }));
        var backups = new BackupService(store, Path.Combine(root, "recovery")); var full = store.ReadSnapshot();
        Assert.AreEqual(9, full.FormatVersion); Assert.Throws<ValidationException>(() => backups.Restore(full with { FormatVersion = 7, SchemaVersion = 7 }));
        var previous = full with { FormatVersion = 7, SchemaVersion = 7, Settings = full.Settings! with { DailyRecordPage = null } };
        var recovery = backups.Restore(previous); Assert.IsNull(journal.Settings().DailyRecordPage);
        backups.Restore(backups.Inspect(recovery)); Assert.AreEqual("sheet", journal.Settings().DailyRecordPage);
        Assert.AreEqual(JsonSerializer.Serialize(full.Foods), JsonSerializer.Serialize(store.ReadSnapshot().Foods));
        Assert.IsFalse(JsonSerializer.Serialize(JournalSettings.Default, JsonFiles.Options).Contains("DailyRecordPage"));
    }
    [TestMethod]
    public void ProteinPreferencePreservesAllDataAndRequiresVersionNine()
    {
        var (root, store, journal) = Open(); var before = store.ReadSnapshot();
        Assert.IsTrue(journal.Settings().RecordsProtein);
        journal.SaveSettings(journal.Settings() with { ProteinEnabled = false });
        Assert.IsFalse(Open(root).Journal.Settings().RecordsProtein);
        var saved = store.ReadSnapshot(); var backups = new BackupService(store, Path.Combine(root, "recovery"));
        Assert.Throws<ValidationException>(() => backups.Restore(saved with { FormatVersion = 8, SchemaVersion = 8 }));
        var archive = backups.Restore(saved with { FormatVersion = 8, SchemaVersion = 8, Settings = saved.Settings! with { ProteinEnabled = null } });
        Assert.IsTrue(journal.Settings().RecordsProtein); backups.Restore(backups.Inspect(archive)); Assert.IsFalse(journal.Settings().RecordsProtein);
        CollectionAssert.AreEqual(before.Foods, store.ReadSnapshot().Foods);
        Assert.IsFalse(JsonSerializer.Serialize(JournalSettings.Default, JsonFiles.Options).Contains("ProteinEnabled"));
    }
    [TestMethod]
    public void ManualDataDeduplicatesWithoutOverwritingAndCanSaveWithoutAdding()
    {
        var (_, store, journal) = Open(); var first = Entry(journal);
        Assert.IsNotNull(journal.UnlistedFood(first)); Assert.IsTrue(journal.SaveManualEntry(first, true));
        Assert.HasCount(61, journal.Foods()); Assert.IsNotNull(store.FindEntry(first.Id)!.FoodId);
        var repeat = first with { Id = Guid.NewGuid() }; Assert.IsNull(journal.UnlistedFood(repeat)); Assert.IsFalse(journal.SaveManualEntry(repeat, true)); Assert.HasCount(61, journal.Foods());
        var different = first with { Id = Guid.NewGuid(), KcalPer100gHundredthsSnapshot = 9000, EnergyKcalTenths = 1350 };
        Assert.IsTrue(journal.SaveManualEntry(different, true)); Assert.HasCount(62, journal.Foods());
        journal.SaveManualEntry(Entry(journal, "只存摄入"), false); Assert.HasCount(62, journal.Foods());
        var direct = Entry(journal, "直接大卡") with { InputMode = "direct", WeightGramsTenths = null, KcalPer100gHundredthsSnapshot = null };
        Assert.IsNull(journal.UnlistedFood(direct)); journal.SaveManualEntry(direct, true); Assert.HasCount(62, journal.Foods());
        var original = store.FindEntry(first.Id)!; var food = journal.Foods().Single(x => x.Id == original.FoodId); journal.DeleteFood(food);
        Assert.IsTrue(journal.SaveManualEntry(first with { Id = Guid.NewGuid() }, true)); Assert.IsTrue(store.ReadFoods(true).Single(x => x.Id == food.Id).IsDeleted);
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(store.FindEntry(first.Id)));
    }
    [TestMethod]
    public void RealEntryWriteFailureRollsBackNewFoodAndIntakeTogether()
    {
        var (_, store, journal) = Open(); var entry = Entry(journal); var before = store.ReadSnapshot();
        using(var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER fail_manual_entry BEFORE INSERT ON entries BEGIN SELECT RAISE(ABORT,'明确的摄入写入故障'); END;"; command.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => journal.SaveManualEntry(entry, true));
        CollectionAssert.AreEqual(before.Foods, store.ReadSnapshot().Foods); Assert.IsEmpty(store.ReadSnapshot().Entries);
    }
    [TestMethod]
    public void MealGroupsRetainMissingZeroCustomAndHistoricalNames()
    {
        var (_, _, journal) = Open(); var meals = journal.Meals(); var first = Entry(journal) with { InputMode = "direct", WeightGramsTenths = null, KcalPer100gHundredthsSnapshot = null, EnergyKcalTenths = 0 };
        journal.SaveEntry(first); journal.SaveMeal(meals[0] with { Name = "新早餐", IsActive = false });
        var custom = new MealCategory { Id = Guid.NewGuid(), Name = "训练前", SortOrder = 10 }; journal.SaveMeal(custom);
        var second = first with { Id = Guid.NewGuid(), MealCategoryId = custom.Id, MealNameSnapshot = custom.Name, EnergyKcalTenths = 500 }; journal.SaveEntry(second);
        var groups = journal.MealsForDay(first.EntryDate); Assert.HasCount(5, groups);
        Assert.IsTrue(groups.Single(x => x.MealId == first.MealCategoryId).Name.Contains("早餐")); Assert.IsTrue(groups.Single(x => x.MealId == first.MealCategoryId).Summary.Contains("0.0"));
        Assert.AreEqual("未记录", groups.Single(x => x.MealId == meals[1].Id).Summary);
        Assert.AreEqual(500L, groups.SelectMany(x => x.Entries).Sum(x => x.EnergyKcalTenths));
        journal.DeleteEntry(second.Id); Assert.AreEqual("未记录", journal.MealsForDay(first.EntryDate).Single(x => x.MealId == custom.Id).Summary);
    }
}
