using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class StatisticsTests
{
    private static (SqliteJournalStore Store, JournalService Journal) Open()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "statistics_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        return (store, new JournalService(store));
    }
    private static IntakeEntry Entry(JournalService journal, DateOnly date, long tenths) => new() { Id = Guid.NewGuid(), EntryDate = date, MealCategoryId = journal.Meals()[0].Id, MealNameSnapshot = "早餐", FoodNameSnapshot = "统计明确输入", EnergyKcalTenths = tenths };
    [TestMethod]
    public void WeekMonthLeapAndCalendarBoundariesAreExplicit()
    {
        var today = new DateOnly(2024, 3, 1);
        Assert.AreEqual((new DateOnly(2024, 2, 26), today), StatisticsCalendar.Period(today, "week", today));
        Assert.AreEqual((new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29)), StatisticsCalendar.Period(new DateOnly(2024, 2, 20), "month", today));
        Assert.AreEqual((new DateOnly(2023, 12, 25), new DateOnly(2023, 12, 31)), StatisticsCalendar.Period(new DateOnly(2023, 12, 31), "week", today));
        Assert.AreEqual(new DateOnly(2024, 1, 1), StatisticsCalendar.Move(new DateOnly(2024, 2, 29), "month", -1, today));
        Assert.IsNull(StatisticsCalendar.Move(today, "week", 1, today)); Assert.IsNull(StatisticsCalendar.Move(DateOnly.MinValue, "month", -1, today));
        Assert.AreEqual(DateOnly.MaxValue, StatisticsCalendar.Period(DateOnly.MaxValue, "month", DateOnly.MaxValue).To);
        Assert.IsNull(StatisticsCalendar.Move(DateOnly.MaxValue, "month", 1, DateOnly.MaxValue));
        Assert.Throws<ValidationException>(() => StatisticsCalendar.Period(today.AddDays(1), "week", today));
        Assert.Throws<ValidationException>(() => StatisticsCalendar.Period(today, "unknown", today));
    }
    [TestMethod]
    public void RealSqliteSeparatesMissingZeroAndRecordedDayAverage()
    {
        var (_, journal) = Open(); var from = new DateOnly(2024, 2, 26); var to = new DateOnly(2024, 3, 3);
        journal.SaveEntry(Entry(journal, from, 0)); journal.SaveEntry(Entry(journal, new DateOnly(2024, 2, 29), 1234)); journal.SaveEntry(Entry(journal, new DateOnly(2024, 2, 29), 1000));
        var value = journal.IntakeStatistics(from, to); Assert.HasCount(7, value.Days); Assert.AreEqual(2, value.RecordedDays); Assert.AreEqual(5, value.MissingDays);
        Assert.AreEqual(223.4m, value.TotalKcal); Assert.AreEqual(111.7m, value.AverageKcal);
        Assert.AreEqual(0m, value.Days[0].EnergyKcal); Assert.AreEqual("0.0", value.Days[0].EnergyText); Assert.IsTrue(value.Days[0].IsRecorded);
        Assert.IsNull(value.Days[1].EnergyKcal); Assert.AreEqual("—", value.Days[1].EnergyText); Assert.IsFalse(value.Days[1].IsRecorded);
        CollectionAssert.AreEqual(Enumerable.Range(0, 7).Select(from.AddDays).ToArray(), value.Days.Select(x => x.Date).ToArray());
    }
    [TestMethod]
    public void EmptyAndAllZeroIntervalsAreDifferent()
    {
        var (_, journal) = Open(); var date = new DateOnly(2024, 1, 1); var empty = journal.IntakeStatistics(date, date);
        Assert.IsNull(empty.TotalKcal); Assert.IsNull(empty.AverageKcal); Assert.AreEqual("未记录", empty.TotalText);
        journal.SaveEntry(Entry(journal, date, 0)); var zero = journal.IntakeStatistics(date, date); Assert.AreEqual(0m, zero.TotalKcal); Assert.AreEqual(0m, zero.AverageKcal); Assert.AreEqual(1, zero.RecordedDays);
    }
    [TestMethod]
    public void EditingMovingDeletingAndRoundingUseCurrentRecords()
    {
        var (store, journal) = Open(); var date = new DateOnly(2024, 1, 1); var first = Entry(journal, date, 1); var second = Entry(journal, date.AddDays(1), 0);
        journal.SaveEntry(first); journal.SaveEntry(second); var initial = journal.IntakeStatistics(date, date.AddDays(2)); Assert.AreEqual(0.05m, initial.AverageKcal); Assert.AreEqual("0.1 大卡/已记录日", initial.AverageText);
        journal.SaveEntry(first with { EntryDate = date.AddDays(2), EnergyKcalTenths = 3333 }); var moved = journal.IntakeStatistics(date, date.AddDays(2)); Assert.IsNull(moved.Days[0].EnergyKcal); Assert.AreEqual(333.3m, moved.Days[2].EnergyKcal);
        journal.DeleteEntry(second.Id); Assert.AreEqual(1, journal.IntakeStatistics(date, date.AddDays(2)).RecordedDays);
        Assert.HasCount(1, store.ReadSnapshot().Entries); journal.DeleteEntry(first.Id); Assert.IsNull(journal.IntakeStatistics(date, date.AddDays(2)).AverageKcal);
    }
    [TestMethod]
    public void LimitsFutureAndReadOnlyBehaviorAreVerified()
    {
        var (store, journal) = Open(); var today = DateOnly.FromDateTime(DateTime.Today); var before = store.ReadSnapshot();
        Assert.HasCount(366, journal.IntakeStatistics(today.AddDays(-365), today).Days);
        Assert.Throws<ValidationException>(() => journal.IntakeStatistics(today.AddDays(-366), today));
        Assert.Throws<ValidationException>(() => journal.IntakeStatistics(today, today.AddDays(-1)));
        Assert.Throws<ValidationException>(() => journal.IntakeStatistics(today, today.AddDays(1)));
        var after = store.ReadSnapshot(); CollectionAssert.AreEqual(before.Entries, after.Entries); CollectionAssert.AreEqual(before.BodyMeasurements, after.BodyMeasurements); Assert.AreEqual(before.Settings, after.Settings);
    }
}
