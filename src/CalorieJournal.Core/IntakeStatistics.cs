namespace CalorieJournal.Core;

public sealed record IntakeStatisticsDay(DateOnly Date, int Count, long? EnergyKcalTenths)
{
    public bool IsRecorded => Count > 0;
    public decimal? EnergyKcal => EnergyKcalTenths / 10m;
    public string EnergyText => EnergyKcal is { } value ? value.ToString("0.0") : "—";
    public string State => IsRecorded ? $"已记录 {Count} 项" : "未记录";
}

public sealed record IntakeStatisticsReport(DateOnly From, DateOnly To, IntakeStatisticsDay[] Days)
{
    public int RecordedDays => Days.Count(x => x.IsRecorded);
    public int MissingDays => Days.Length - RecordedDays;
    public decimal? TotalKcal => RecordedDays == 0 ? null : Days.Sum(x => x.EnergyKcalTenths ?? 0) / 10m;
    public decimal? AverageKcal => RecordedDays == 0 ? null : TotalKcal / RecordedDays;
    public string TotalText => TotalKcal is { } value ? $"{value:0.0} 大卡" : "未记录";
    public string AverageText => AverageKcal is { } value ? $"{decimal.Round(value, 1, MidpointRounding.AwayFromZero):0.0} 大卡/已记录日" : "未记录";
}

public static class StatisticsCalendar
{
    public static (DateOnly From, DateOnly To) Period(DateOnly anchor, string mode, DateOnly today)
    {
        Validation.Require(anchor <= today, "统计日期不能晚于今天。");
        DateOnly from; DateOnly to;
        if (mode == "week")
        {
            var offset = ((int)anchor.DayOfWeek + 6) % 7;
            from = anchor.AddDays(-Math.Min(offset, anchor.DayNumber));
            to = from.AddDays(Math.Min(6, DateOnly.MaxValue.DayNumber - from.DayNumber));
        }
        else if (mode == "month")
        {
            from = new DateOnly(anchor.Year, anchor.Month, 1);
            to = new DateOnly(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month));
        }
        else throw new ValidationException("请选择周或月统计。");
        return (from, to > today ? today : to);
    }
    public static DateOnly? Move(DateOnly anchor, string mode, int direction, DateOnly today)
    {
        Validation.Require(direction is -1 or 1, "统计导航方向无效。");
        var range = Period(anchor, mode, today); DateOnly target;
        if (direction < 0 && range.From == DateOnly.MinValue) return null;
        if (mode == "week")
        {
            var delta = direction * 7;
            if (range.From.DayNumber + delta < 0 || range.From.DayNumber + delta > DateOnly.MaxValue.DayNumber) return null;
            target = range.From.AddDays(delta);
        }
        else
        {
            if (direction > 0 && range.From.Year == 9999 && range.From.Month == 12) return null;
            target = range.From.AddMonths(direction);
        }
        return target > today ? null : target;
    }
}

public sealed partial class JournalService
{
    public IntakeStatisticsReport IntakeStatistics(DateOnly from, DateOnly to)
    {
        Validation.Require(to >= from && to.DayNumber - from.DayNumber <= 365, "统计区间须有效且不超过366天。");
        Validation.Require(to <= DateOnly.FromDateTime(DateTime.Today), "统计区间不能包含未来日期。");
        var groups = store.ReadEntries(from, to).GroupBy(x => x.EntryDate).ToDictionary(x => x.Key, x => x.ToArray());
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(index =>
        {
            var day = from.AddDays(index); var rows = groups.GetValueOrDefault(day);
            return new IntakeStatisticsDay(day, rows?.Length ?? 0, rows is null ? null : rows.Sum(x => x.EnergyKcalTenths));
        }).ToArray();
        return new IntakeStatisticsReport(from, to, days);
    }
}
