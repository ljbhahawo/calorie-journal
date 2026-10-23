using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class PersonalRecordTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private static (string Root, SqliteJournalStore Store, JournalService Journal) Open(string? root = null)
    {
        root ??= Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "personal_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        return (root, store, new JournalService(store));
    }
    private static IntakeGoal Goal(DateOnly date, long? target = 18000) => new() { Id = Guid.NewGuid(), EffectiveDate = date, TargetKcalTenths = target, Note = "明确的目标验收输入" };
    private static ExerciseRecord Exercise() => new() { Id = Guid.NewGuid(), Date = Today, Activity = "明确的步行验收输入", DurationMinutesTenths = 305, Note = "验收输入，与个人资料无关" };
    private static void Intake(JournalService journal, DateOnly date, long energy)
    {
        var meal = journal.Meals()[0]; journal.SaveEntry(new IntakeEntry { Id = Guid.NewGuid(), EntryDate = date, MealCategoryId = meal.Id, MealNameSnapshot = meal.Name, FoodNameSnapshot = "目标比较明确输入", EnergyKcalTenths = energy });
    }
    [TestMethod]
    public void EffectivePeriodsPauseFutureAndHistoricalCorrectionStayExplicit()
    {
        var (_, _, journal) = Open(); var first = Goal(Today.AddDays(-10)); var second = Goal(Today.AddDays(-5), 20000);
        journal.SaveIntakeGoal(first); journal.SaveIntakeGoal(second); journal.SaveIntakeGoal(Goal(Today.AddDays(2), 22000));
        Assert.IsNull(journal.CompareGoal(Today.AddDays(-11)).Goal); Assert.AreEqual(first.Id, journal.CompareGoal(Today.AddDays(-6)).Goal!.Id);
        Assert.AreEqual(second.Id, journal.CompareGoal(Today).Goal!.Id); Assert.AreEqual(Today.AddDays(-6), journal.Goals().Single(x => x.Goal.Id == first.Id).Until);
        var pause = Goal(Today.AddDays(-2), null); journal.SaveIntakeGoal(pause); Assert.IsNull(journal.CompareGoal(Today).Goal!.TargetKcalTenths);
        Assert.AreEqual(second.Id, journal.CompareGoal(Today.AddDays(-3)).Goal!.Id);
        journal.SaveIntakeGoal(second with { TargetKcalTenths = 21000 }); Assert.AreEqual(18000L, journal.CompareGoal(Today.AddDays(-6)).Goal!.TargetKcalTenths);
        Assert.AreEqual(21000L, journal.CompareGoal(Today.AddDays(-3)).Goal!.TargetKcalTenths);
        journal.DeleteIntakeGoal(pause.Id); Assert.AreEqual(second.Id, journal.CompareGoal(Today).Goal!.Id);
        Assert.Throws<ValidationException>(() => journal.DeleteIntakeGoal(pause.Id));
    }
    [TestMethod]
    public void MissingIntakeZeroAndIndependentExerciseNeverChangeGoalDifference()
    {
        var (_, store, journal) = Open(); journal.SaveIntakeGoal(Goal(Today));
        Assert.IsNull(journal.CompareGoal(Today).IntakeKcalTenths); Assert.IsNull(journal.CompareGoal(Today).DifferenceTenths);
        Intake(journal, Today, 0); Assert.AreEqual(18000L, journal.CompareGoal(Today).DifferenceTenths);
        Intake(journal, Today, 19000); var before = JsonSerializer.Serialize(store.ReadSnapshot().Entries); var comparison = journal.CompareGoal(Today); Assert.AreEqual(-1000L, comparison.DifferenceTenths);
        var exercise = Exercise(); journal.SaveExercise(exercise); journal.SaveExercise(exercise with { EnergyKcalTenths = 90000 });
        Assert.AreEqual(comparison, journal.CompareGoal(Today)); Assert.AreEqual(19000L, journal.IntakeStatistics(Today, Today).Days[0].EnergyKcalTenths);
        journal.DeleteExercise(exercise.Id); Assert.AreEqual(comparison, journal.CompareGoal(Today)); Assert.AreEqual(before, JsonSerializer.Serialize(store.ReadSnapshot().Entries));
    }
    [TestMethod]
    public void GoalDuplicatesMovementLimitsAndRestartAreValidated()
    {
        var (root, store, journal) = Open(); var goal = Goal(Today, 1); journal.SaveIntakeGoal(goal);
        Assert.Throws<ValidationException>(() => journal.SaveIntakeGoal(goal with { Id = Guid.NewGuid() }));
        var other = Goal(Today.AddDays(1), 999999); journal.SaveIntakeGoal(other);
        Assert.Throws<ValidationException>(() => journal.SaveIntakeGoal(other with { EffectiveDate = Today }));
        foreach (var invalid in new[] { goal with { Id = Guid.Empty }, goal with { EffectiveDate = default }, goal with { TargetKcalTenths = 0 }, goal with { TargetKcalTenths = -1 }, goal with { TargetKcalTenths = 1000000 }, goal with { Note = new string('a', 2001) }, goal with { CreatedAtUtc = default } }) Assert.Throws<ValidationException>(() => journal.SaveIntakeGoal(invalid));
        Assert.HasCount(2, Open(root).Store.ReadIntakeGoals()); Assert.HasCount(2, store.ReadSnapshot().IntakeGoals!);
        Assert.Throws<ValidationException>(() => journal.CompareGoal(Today.AddDays(1)));
    }
    [TestMethod]
    public void ExerciseCrudRestartMissingEnergyAndRangeBoundaries()
    {
        var (root, store, journal) = Open(); var first = Exercise(); journal.SaveExercise(first);
        journal.SaveExercise(first with { Id = Guid.NewGuid(), EnergyKcalTenths = 0 });
        Assert.HasCount(2, journal.Exercises(Today, Today)); Assert.IsTrue(journal.Exercises(Today, Today).Any(x => x.EnergyText == "未填写")); Assert.IsTrue(journal.Exercises(Today, Today).Any(x => x.EnergyText == "0.0"));
        journal.SaveExercise(first with { Date = Today.AddDays(-365), EnergyKcalTenths = 1234, DurationMinutesTenths = 14400 });
        var restarted = Open(root); Assert.HasCount(2, restarted.Journal.Exercises(Today.AddDays(-365), Today)); Assert.HasCount(1, restarted.Journal.Exercises(Today, Today));
        Assert.AreEqual(14400L, restarted.Journal.Exercises(Today.AddDays(-365), Today).Single(x => x.Id == first.Id).DurationMinutesTenths);
        Assert.Throws<ValidationException>(() => journal.Exercises(Today.AddDays(-366), Today)); Assert.Throws<ValidationException>(() => journal.Exercises(Today, Today.AddDays(1))); Assert.Throws<ValidationException>(() => journal.Exercises(Today, Today.AddDays(-1)));
        journal.DeleteExercise(first.Id); Assert.HasCount(1, store.ReadExercises(DateOnly.MinValue, DateOnly.MaxValue)); Assert.Throws<ValidationException>(() => journal.DeleteExercise(first.Id));
    }
    [TestMethod]
    public void InvalidExerciseAndFixedPrecisionFailBeforeWriting()
    {
        var (_, store, journal) = Open(); var value = Exercise();
        foreach (var invalid in new[] { value with { Id = Guid.Empty }, value with { Date = Today.AddDays(1) }, value with { Activity = " " }, value with { Activity = new string('a', 101) }, value with { DurationMinutesTenths = 0 }, value with { DurationMinutesTenths = -1 }, value with { DurationMinutesTenths = 14401 }, value with { EnergyKcalTenths = -1 }, value with { EnergyKcalTenths = 1000000 }, value with { Note = new string('a', 2001) } }) Assert.Throws<ValidationException>(() => journal.SaveExercise(invalid));
        Assert.Throws<ValidationException>(() => Validation.Scaled(30.01m, 10, 0.1m, 1440m, "时长"));
        Assert.Throws<ValidationException>(() => Validation.Scaled(1800.01m, 10, 0.1m, 99999.9m, "目标")); Assert.IsEmpty(store.ReadSnapshot().Exercises!);
        journal.SaveExercise(value with { DurationMinutesTenths = 1, EnergyKcalTenths = 999999 }); Assert.HasCount(1, store.ReadSnapshot().Exercises!);
    }
    [TestMethod]
    public void FullBackupLegacyFiveRestoreAndNewTypesReturnIntact()
    {
        var (root, store, journal) = Open(); journal.SaveIntakeGoal(Goal(Today)); journal.SaveExercise(Exercise()); journal.SaveExercise(Exercise() with { EnergyKcalTenths = 0 });
        var backups = new BackupService(store, Path.Combine(root, "recovery")); var snapshot = store.ReadSnapshot(); var file = Path.Combine(root, "personal.calorie-backup.json"); backups.Export(file);
        foreach (var invalid in new[] { snapshot with { IntakeGoals = null }, snapshot with { Exercises = null }, snapshot with { IntakeGoals = [snapshot.IntakeGoals![0], snapshot.IntakeGoals[0]] }, snapshot with { Exercises = [snapshot.Exercises![0], snapshot.Exercises[0]] }, snapshot with { FormatVersion = 5, SchemaVersion = 5 }, snapshot with { IntakeGoals = [snapshot.IntakeGoals![0] with { TargetKcalTenths = 0 }] } }) Assert.Throws<ValidationException>(() => backups.Restore(invalid));
        var recovery = backups.Restore(snapshot with { FormatVersion = 5, SchemaVersion = 5, IntakeGoals = null, Exercises = null }); Assert.IsEmpty(store.ReadIntakeGoals()); Assert.IsEmpty(store.ReadExercises(DateOnly.MinValue, DateOnly.MaxValue));
        backups.Restore(backups.Inspect(recovery)); var actual = store.ReadSnapshot(); Assert.AreEqual(JsonSerializer.Serialize(snapshot.IntakeGoals), JsonSerializer.Serialize(actual.IntakeGoals)); Assert.AreEqual(JsonSerializer.Serialize(snapshot.Exercises), JsonSerializer.Serialize(actual.Exercises));
        journal.DeleteIntakeGoal(snapshot.IntakeGoals![0].Id); backups.Restore(backups.Inspect(file)); Assert.HasCount(1, store.ReadIntakeGoals());
    }
    [TestMethod]
    public void RealTransactionFailurePreservesAllCollectionsAndIndexMismatchIsVisible()
    {
        var (root, store, journal) = Open(); journal.SaveIntakeGoal(Goal(Today)); journal.SaveExercise(Exercise()); Intake(journal, Today, 15000); var before = store.ReadSnapshot();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER deny_exercise BEFORE INSERT ON exercise_records BEGIN SELECT RAISE(ABORT,'明确锻炼事务故障'); END;"; command.ExecuteNonQuery(); }
        var backups = new BackupService(store, Path.Combine(root, "recovery")); Assert.Throws<SqliteException>(() => backups.Restore(before));
        var after = store.ReadSnapshot(); CollectionAssert.AreEqual(before.IntakeGoals, after.IntakeGoals); CollectionAssert.AreEqual(before.Exercises, after.Exercises); CollectionAssert.AreEqual(before.Entries, after.Entries); CollectionAssert.AreEqual(before.Foods, after.Foods);
        Assert.Throws<SqliteException>(() => journal.SaveExercise(Exercise()));
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE intake_goals SET effective_date='2000-01-01'"; command.ExecuteNonQuery(); }
        Assert.Throws<ValidationException>(() => store.ReadSnapshot());
    }
}
