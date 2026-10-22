using System.Collections.ObjectModel;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel
{
    private DateOnly appliedBrowseFrom = DateOnly.FromDateTime(DateTime.Today.AddDays(-29));
    private DateOnly appliedBrowseTo = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool continuousMode;
    [ObservableProperty] private DateTime browseFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime browseTo = DateTime.Today;
    public DateTime TodayDate => DateTime.Today;
    public ObservableCollection<DailyIntake> BrowseDays { get; } = [];
    public string BrowseRangeText => $"{appliedBrowseFrom:yyyy-MM-dd} 至 {appliedBrowseTo:yyyy-MM-dd} · 共 {BrowseDays.Count} 天";
    public IRelayCommand ShowSingleCommand { get; private set; } = null!;
    public IRelayCommand ShowContinuousCommand { get; private set; } = null!;
    public IRelayCommand LoadBrowseCommand { get; private set; } = null!;
    public IRelayCommand LoadEarlierCommand { get; private set; } = null!;
    public IRelayCommand<DailyIntake> AddListedEntryCommand { get; private set; } = null!;
    public IRelayCommand<IntakeEntry> EditListedEntryCommand { get; private set; } = null!;
    public IRelayCommand<IntakeEntry> DeleteListedEntryCommand { get; private set; } = null!;

    private void InitializeBrowse()
    {
        ShowSingleCommand = new RelayCommand(() => ContinuousMode = false);
        ShowContinuousCommand = new RelayCommand(() => ContinuousMode = true);
        LoadBrowseCommand = new RelayCommand(() => Run(() => ApplyBrowse(DateOnly.FromDateTime(BrowseFrom), DateOnly.FromDateTime(BrowseTo))));
        LoadEarlierCommand = new RelayCommand(() => Run(() =>
        {
            var available = Math.Min(30, Math.Min(appliedBrowseFrom.DayNumber, 365 - (appliedBrowseTo.DayNumber - appliedBrowseFrom.DayNumber)));
            Validation.Require(available > 0, "已经显示366天或到达最早日期，请调整查询区间。");
            ApplyBrowse(appliedBrowseFrom.AddDays(-available), appliedBrowseTo);
        }));
        AddListedEntryCommand = new RelayCommand<DailyIntake>(day => Run(() =>
        {
            Validation.Require(day is not null, "请选择要添加记录的日期。");
            if (!ConfirmEntryDiscard()) return;
            selectedDate = day.Date.ToDateTime(TimeOnly.MinValue); OnPropertyChanged(nameof(SelectedDate));
            ResetEntry(); RefreshDay();
            Status = $"正在为{day.Date:yyyy-MM-dd}添加摄入，请填写左侧表单并保存。";
        }));
        EditListedEntryCommand = new RelayCommand<IntakeEntry>(row => Run(() =>
        {
            Validation.Require(row is not null, "请选择要修改的摄入。");
            if (!ConfirmEntryDiscard()) return;
            selectedDate = row.EntryDate.ToDateTime(TimeOnly.MinValue); OnPropertyChanged(nameof(SelectedDate));
            LoadEntry(row, false); RefreshDay();
            Status = $"正在修改{row.EntryDate:yyyy-MM-dd}的摄入，保存后更新列表。";
        }));
        DeleteListedEntryCommand = new RelayCommand<IntakeEntry>(row => Run(() => DeleteEntry(row)));
    }

    partial void OnContinuousModeChanged(bool value)
    {
        if (value) Run(RefreshBrowse);
    }

    private void ApplyBrowse(DateOnly from, DateOnly to)
    {
        var days = journal.BrowseDays(from, to);
        // 只有读取成功才更新已应用区间；无效筛选不影响后续写入后的刷新。
        appliedBrowseFrom = from; appliedBrowseTo = to;
        BrowseFrom = from.ToDateTime(TimeOnly.MinValue); BrowseTo = to.ToDateTime(TimeOnly.MinValue);
        Fill(BrowseDays, days); OnPropertyChanged(nameof(BrowseRangeText));
        Status = "连续列表已更新。";
    }

    private void RefreshBrowse()
    {
        var days = journal.BrowseDays(appliedBrowseFrom, appliedBrowseTo);
        if (BrowseDays.Count != days.Length || !BrowseDays.Select(x => x.Date).SequenceEqual(days.Select(x => x.Date))) Fill(BrowseDays, days);
        else
        {
            // 保留未变化的日期容器及滚动位置，避免在较早日期保存后跳回列表开头。
            for (var index = 0; index < days.Length; index++)
                if (!BrowseDays[index].Entries.SequenceEqual(days[index].Entries)) BrowseDays[index] = days[index];
        }
        OnPropertyChanged(nameof(BrowseRangeText));
    }
}
