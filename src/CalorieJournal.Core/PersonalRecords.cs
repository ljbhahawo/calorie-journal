using System.Text.Json.Serialization;

namespace CalorieJournal.Core;

public sealed record IntakeGoal
{
    public required Guid Id { get; init; }
    public required DateOnly EffectiveDate { get; init; }
    public long? TargetKcalTenths { get; init; }
    public string Note { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string TargetText => TargetKcalTenths is { } value ? $"{value / 10m:0.0} 大卡/日" : "暂停目标";
}

public sealed record GoalPeriod(IntakeGoal Goal, DateOnly? Until)
{
    public string UntilText => Until?.ToString("yyyy-MM-dd") ?? "持续生效";
}

public sealed record GoalComparison(DateOnly Date, IntakeGoal? Goal, long? IntakeKcalTenths)
{
    public long? DifferenceTenths => Goal?.TargetKcalTenths is { } target && IntakeKcalTenths is { } intake ? target - intake : null;
    public string Description => $"{Date:yyyy-MM-dd} · " + (Goal is null ? "未设置目标" : Goal.TargetKcalTenths is null ? "目标已暂停" :
        $"自设目标 {Goal.TargetText} · " + (IntakeKcalTenths is null ? "当天未记录摄入，未比较" :
        $"已记录 {IntakeKcalTenths / 10m:0.0} 大卡 · " + (DifferenceTenths > 0 ? $"距目标尚差 {DifferenceTenths / 10m:0.0} 大卡" : DifferenceTenths < 0 ? $"已超过目标 {-DifferenceTenths / 10m:0.0} 大卡" : "与目标相同")));
}

public sealed record ExerciseRecord
{
    public required Guid Id { get; init; }
    public required DateOnly Date { get; init; }
    public required string Activity { get; init; }
    public required long DurationMinutesTenths { get; init; }
    public long? EnergyKcalTenths { get; init; }
    public string Note { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public decimal DurationMinutes => DurationMinutesTenths / 10m;
    [JsonIgnore] public string EnergyText => EnergyKcalTenths is { } value ? $"{value / 10m:0.0}" : "未填写";
}

public static class PersonalRecordValidation
{
    public static void Goal(IntakeGoal value)
    {
        Validation.Require(value.Id != Guid.Empty && value.EffectiveDate != default, "目标ID或生效日期无效。");
        Validation.Require(value.TargetKcalTenths is null or >= 1 and <= 999999, "摄入目标须为0.1–99999.9大卡，或选择暂停目标。");
        Validation.Text(value.Note, "目标备注", 2000);
        Validation.Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "目标记录时间无效。");
    }
    public static void Exercise(ExerciseRecord value, bool checkToday = true)
    {
        Validation.Require(value.Id != Guid.Empty && value.Date != default && (!checkToday || value.Date <= DateOnly.FromDateTime(DateTime.Today)), "锻炼ID或日期无效，日期不能晚于今天。");
        Validation.Text(value.Activity, "活动名称", 100, true);
        Validation.Require(value.DurationMinutesTenths is >= 1 and <= 14400, "时长须为0.1–1440分钟，最多一位小数。");
        Validation.Require(value.EnergyKcalTenths is null or >= 0 and <= 999999, "手动消耗须为0–99999.9大卡，或留空。");
        Validation.Text(value.Note, "锻炼备注", 2000);
        Validation.Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "锻炼记录时间无效。");
    }
    public static void Snapshot(JournalSnapshot value)
    {
        if (value.FormatVersion < 6)
        { Validation.Require(value.IntakeGoals is null && value.Exercises is null, "旧格式不能包含目标或锻炼记录。"); return; }
        Validation.Require(value.IntakeGoals is not null && value.IntakeGoals.Length <= 100000 && value.IntakeGoals.All(x => x is not null), "目标记录缺失或条目无效。");
        Validation.Require(value.Exercises is not null && value.Exercises.Length <= 100000 && value.Exercises.All(x => x is not null), "锻炼记录缺失或条目无效。");
        foreach (var item in value.IntakeGoals) Goal(item);
        foreach (var item in value.Exercises) Exercise(item, false);
        Validation.Require(value.IntakeGoals.Select(x => x.Id).Distinct().Count() == value.IntakeGoals.Length && value.IntakeGoals.Select(x => x.EffectiveDate).Distinct().Count() == value.IntakeGoals.Length, "目标ID或生效日期重复。");
        Validation.Require(value.Exercises.Select(x => x.Id).Distinct().Count() == value.Exercises.Length, "锻炼记录ID重复。");
    }
}

public sealed partial class JournalService
{
    public GoalPeriod[] Goals()
    {
        var goals = store.ReadIntakeGoals().OrderBy(x => x.EffectiveDate).ToArray();
        return goals.Select((goal, index) => new GoalPeriod(goal, index + 1 < goals.Length ? goals[index + 1].EffectiveDate.AddDays(-1) : null)).Reverse().ToArray();
    }
    public GoalComparison CompareGoal(DateOnly date)
    {
        Validation.Require(date != default && date <= DateOnly.FromDateTime(DateTime.Today), "目标比较日期无效或晚于今天。");
        var goal = store.ReadIntakeGoals().Where(x => x.EffectiveDate <= date).MaxBy(x => x.EffectiveDate);
        var entries = store.ReadEntries(date, date);
        return new(date, goal, entries.Length == 0 ? null : entries.Sum(x => x.EnergyKcalTenths));
    }
    public void SaveIntakeGoal(IntakeGoal value)
    {
        value = value with { Note = Validation.Text(value.Note, "目标备注", 2000), UpdatedAtUtc = DateTimeOffset.UtcNow };
        PersonalRecordValidation.Goal(value); store.SaveIntakeGoal(value);
    }
    public void DeleteIntakeGoal(Guid id) => store.DeleteIntakeGoal(id);
    public ExerciseRecord[] Exercises(DateOnly from, DateOnly to)
    {
        Validation.Require(from != default && to >= from && to.DayNumber - from.DayNumber < 366 && to <= DateOnly.FromDateTime(DateTime.Today), "锻炼查询区间须有效、不晚于今天且不超过366天。");
        return store.ReadExercises(from, to).OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArray();
    }
    public void SaveExercise(ExerciseRecord value)
    {
        value = value with { Activity = Validation.Text(value.Activity, "活动名称", 100, true), Note = Validation.Text(value.Note, "锻炼备注", 2000), UpdatedAtUtc = DateTimeOffset.UtcNow };
        PersonalRecordValidation.Exercise(value); store.SaveExercise(value);
    }
    public void DeleteExercise(Guid id) => store.DeleteExercise(id);
}
