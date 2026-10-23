using System.Collections.ObjectModel;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed record StatisticsTrendDay(IntakeStatisticsDay Day, decimal Maximum)
{
    public string Label => Day.Date.ToString("MM-dd");
    public string Value => Day.EnergyText;
    public string Description => $"{Day.Date:yyyy-MM-dd} · {Day.State}" + (Day.IsRecorded ? $" · {Day.EnergyText} 大卡" : "");
    public double BarHeight => Day.EnergyKcal is > 0 && Maximum > 0 ? (double)(Day.EnergyKcal.Value / Maximum * 120m) : 0;
    public bool IsZero => Day.IsRecorded && Day.EnergyKcal == 0;
    public bool IsMissing => !Day.IsRecorded;
}

public sealed partial class MainViewModel
{
    private IntakeStatisticsReport? appliedStatistics;
    private string appliedStatisticsMode = "week";
    private DateOnly appliedStatisticsAnchor = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private int statisticsMode;
    [ObservableProperty] private DateTime statisticsAnchor = DateTime.Today;
    [ObservableProperty] private DateTime statisticsFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime statisticsTo = DateTime.Today;
    [ObservableProperty] private IntakeStatisticsDay? selectedStatisticsDay;
    public string[] StatisticsModes { get; } = ["按周", "按月", "自定义区间"];
    public bool StatisticsCalendarMode => StatisticsMode != 2;
    public bool StatisticsCustomMode => StatisticsMode == 2;
    public ObservableCollection<IntakeStatisticsDay> StatisticsDays { get; } = [];
    public ObservableCollection<StatisticsTrendDay> StatisticsTrend { get; } = [];
    public string StatisticsRange => appliedStatistics is null ? "未查询" : $"{appliedStatistics.From:yyyy-MM-dd} 至 {appliedStatistics.To:yyyy-MM-dd} · 未来日期不参与";
    public string StatisticsTotal => appliedStatistics?.TotalText ?? "未记录";
    public string StatisticsAverage => appliedStatistics?.AverageText ?? "未记录";
    public string StatisticsCoverage => appliedStatistics is null ? "未查询" : $"{appliedStatistics.RecordedDays} 天已记录 · {appliedStatistics.MissingDays} 天未记录";
    public string StatisticsScale => StatisticsTrend.All(x => x.IsMissing) ? "本区间未记录摄入 · —表示空缺" : $"每日摄入 · 大卡 · 纵轴从0起，最高刻度 {StatisticsTrend.Select(x => x.Maximum).DefaultIfEmpty().Max():0.0}";
    public double StatisticsChartWidth => StatisticsTrend.Count * 46d;
    public IRelayCommand QueryStatisticsCommand { get; private set; } = null!;
    public IRelayCommand PreviousStatisticsCommand { get; private set; } = null!;
    public IRelayCommand NextStatisticsCommand { get; private set; } = null!;
    public IRelayCommand CurrentStatisticsCommand { get; private set; } = null!;
    public IRelayCommand OpenStatisticsDayCommand { get; private set; } = null!;
    private void InitializeStatistics()
    {
        QueryStatisticsCommand = new RelayCommand(() => Run(ApplyStatistics));
        PreviousStatisticsCommand = new RelayCommand(() => Run(() => MoveStatistics(-1)), () => CanMoveStatistics(-1));
        NextStatisticsCommand = new RelayCommand(() => Run(() => MoveStatistics(1)), () => CanMoveStatistics(1));
        CurrentStatisticsCommand = new RelayCommand(() => Run(() => { StatisticsAnchor = DateTime.Today; if (StatisticsMode == 2) { StatisticsFrom = DateTime.Today.AddDays(-29); StatisticsTo = DateTime.Today; } ApplyStatistics(); }));
        OpenStatisticsDayCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedStatisticsDay is not null, "请选择统计日表中的日期。");
            if (SheetMode) { OpenSheetDate(SelectedStatisticsDay.Date); TabIndex = 0; return; }
            if (!ConfirmEntryDiscard()) return;
            selectedDate = SelectedStatisticsDay.Date.ToDateTime(TimeOnly.MinValue); OnPropertyChanged(nameof(SelectedDate));
            ResetEntry(); RefreshDay(); ShowSingleCommand.Execute(null); TabIndex = 0;
        }));
        ApplyStatistics();
    }
    partial void OnStatisticsModeChanged(int value)
    { OnPropertyChanged(nameof(StatisticsCalendarMode)); OnPropertyChanged(nameof(StatisticsCustomMode)); }
    private void ApplyStatistics()
    {
        Validation.Require(StatisticsMode is >= 0 and <= 2, "请选择有效统计方式。");
        var today = DateOnly.FromDateTime(DateTime.Today); var anchor = DateOnly.FromDateTime(StatisticsAnchor);
        var mode = new[] { "week", "month", "custom" }[StatisticsMode];
        var range = mode == "custom" ? (From: DateOnly.FromDateTime(StatisticsFrom), To: DateOnly.FromDateTime(StatisticsTo)) : StatisticsCalendar.Period(anchor, mode, today);
        var value = journal.IntakeStatistics(range.From, range.To);
        appliedStatisticsMode = mode; appliedStatisticsAnchor = anchor; ShowStatistics(value);
        Status = "统计已更新，平均只计算有记录日期。";
    }
    private void ShowStatistics(IntakeStatisticsReport value)
    {
        appliedStatistics = value; Fill(StatisticsDays, value.Days);
        var maximum = value.Days.Select(x => x.EnergyKcal ?? 0).DefaultIfEmpty().Max();
        Fill(StatisticsTrend, value.Days.Select(x => new StatisticsTrendDay(x, maximum)));
        foreach (var name in new[] { nameof(StatisticsRange), nameof(StatisticsTotal), nameof(StatisticsAverage), nameof(StatisticsCoverage), nameof(StatisticsScale), nameof(StatisticsChartWidth) }) OnPropertyChanged(name);
        PreviousStatisticsCommand.NotifyCanExecuteChanged(); NextStatisticsCommand.NotifyCanExecuteChanged();
    }
    private bool CanMoveStatistics(int direction) => appliedStatisticsMode != "custom" && StatisticsCalendar.Move(appliedStatisticsAnchor, appliedStatisticsMode, direction, DateOnly.FromDateTime(DateTime.Today)) is not null;
    private void MoveStatistics(int direction)
    {
        var anchor = StatisticsCalendar.Move(appliedStatisticsAnchor, appliedStatisticsMode, direction, DateOnly.FromDateTime(DateTime.Today));
        Validation.Require(appliedStatisticsMode != "custom" && anchor is not null, "当前区间不能继续向该方向切换。");
        StatisticsMode = appliedStatisticsMode == "week" ? 0 : 1; StatisticsAnchor = anchor.Value.ToDateTime(TimeOnly.MinValue); ApplyStatistics();
    }
    private void RefreshStatistics()
    {
        if (appliedStatistics is null) return;
        var range = appliedStatisticsMode == "custom" ? (appliedStatistics.From, appliedStatistics.To) : StatisticsCalendar.Period(appliedStatisticsAnchor, appliedStatisticsMode, DateOnly.FromDateTime(DateTime.Today));
        ShowStatistics(journal.IntakeStatistics(range.Item1, range.Item2));
    }
}
