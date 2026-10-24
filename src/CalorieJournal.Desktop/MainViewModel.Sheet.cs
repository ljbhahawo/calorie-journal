using System.Collections.ObjectModel;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class SheetMealViewModel : ObservableObject
{
    public DateOnly Date { get; }
    public MealCategory Meal { get; private set; }
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string summary = "未记录";
    public ObservableCollection<SheetEntryViewModel> Rows { get; } = [];
    public IRelayCommand AddCommand { get; }
    public SheetMealViewModel(DateOnly date, MealCategory meal, Action<SheetMealViewModel> add) { Date = date; Meal = meal; AddCommand = new RelayCommand(() => add(this)); }
    public void Update(MealCategory meal, IntakeEntry[] entries, bool proteinEnabled)
    {
        Meal = meal; Title = string.Join(" / ", new[] { meal.Name }.Concat(entries.Select(x => x.MealNameSnapshot)).Distinct()) + (meal.IsActive ? "" : "（已停用）");
        Summary = entries.Length == 0 ? "未记录" : $"{entries.Sum(x => x.EnergyKcalTenths) / 10m:0.0} 大卡 · {entries.Length}项" + (proteinEnabled ? " · " + Nutrition.ProteinSummary(entries) : "");
    }
}
public sealed partial class SheetDayViewModel(DateOnly date) : ObservableObject
{
    public DateOnly Date { get; } = date;
    public string Title => Date.ToString("yyyy-MM-dd dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    [ObservableProperty] private string summary = "未记录";
    public ObservableCollection<SheetMealViewModel> Meals { get; } = [];
}

public sealed partial class MainViewModel
{
    private DateOnly appliedSheetFrom = DateOnly.FromDateTime(DateTime.Today.AddDays(-29));
    private DateOnly appliedSheetTo = DateOnly.FromDateTime(DateTime.Today);
    private readonly Dictionary<Guid, SheetEntryViewModel> sheetRows = [];
    [ObservableProperty] private bool sheetMode;
    [ObservableProperty] private DateTime sheetFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime sheetTo = DateTime.Today;
    public ObservableCollection<SheetDayViewModel> SheetDays { get; } = [];
    public bool HasSheetDrafts => sheetRows.Values.Any(x => x.HasDraft);
    internal bool ContainsSheetRow(SheetEntryViewModel row) => sheetRows.TryGetValue(row.Id, out var current) && ReferenceEquals(current, row);
    public string SheetRangeText => $"{appliedSheetFrom:yyyy-MM-dd} 至 {appliedSheetTo:yyyy-MM-dd} · {SheetDays.Count}天 · 空行及草稿不计入合计";
    public IRelayCommand QuerySheetCommand { get; private set; } = null!;
    public IRelayCommand EarlierSheetCommand { get; private set; } = null!;
    public IRelayCommand TodaySheetCommand { get; private set; } = null!;
    private void InitializeSheet()
    {
        QuerySheetCommand = new RelayCommand(() => Run(() => ApplySheetRange(DateOnly.FromDateTime(SheetFrom), DateOnly.FromDateTime(SheetTo))));
        TodaySheetCommand = new RelayCommand(() => Run(() => OpenSheetDate(DateOnly.FromDateTime(DateTime.Today))));
        EarlierSheetCommand = new RelayCommand(() => Run(() =>
        {
            var count = Math.Min(30, Math.Min(appliedSheetFrom.DayNumber, 365 - (appliedSheetTo.DayNumber - appliedSheetFrom.DayNumber)));
            Validation.Require(count > 0, "已经显示366天或到达最早日期，请调整查询区间。");
            ApplySheetRange(appliedSheetFrom.AddDays(-count), appliedSheetTo);
        }));
    }
    partial void OnSheetModeChanged(bool value) { if (value) RefreshSheet(); }
    private void ApplySheetRange(DateOnly from, DateOnly to)
    {
        _ = journal.BrowseDays(from, to);
        Validation.Require(!HasSheetDrafts, "请先保存或取消表格中未保存的行，再调整日期区间。");
        appliedSheetFrom = from; appliedSheetTo = to; SheetFrom = from.ToDateTime(TimeOnly.MinValue); SheetTo = to.ToDateTime(TimeOnly.MinValue);
        RefreshSheet(); Status = "表格日期区间已更新。";
    }
    private void OpenSheetDate(DateOnly date) => ApplySheetRange(date, date);
    private void RefreshSheet()
    {
        var days = journal.BrowseDays(appliedSheetFrom, appliedSheetTo); var meals = journal.Meals();
        var retained = new HashSet<Guid>(); var nextDays = new List<SheetDayViewModel>();
        foreach (var day in days)
        {
            var model = SheetDays.FirstOrDefault(x => x.Date == day.Date) ?? new SheetDayViewModel(day.Date);
            model.Summary = day.Summary + (ProteinEnabled ? " · " + day.ProteinSummary : "");
            var groups = new List<SheetMealViewModel>();
            foreach (var meal in meals.Where(x => x.IsActive || day.Entries.Any(e => e.MealCategoryId == x.Id) || sheetRows.Values.Any(r => r.HasDraft && r.Original is null && DateOnly.FromDateTime(r.Date) == day.Date && r.Meal?.Id == x.Id)))
            {
                var group = model.Meals.FirstOrDefault(x => x.Meal.Id == meal.Id) ?? new SheetMealViewModel(day.Date, meal, g => Run(() => AddSheetRow(g)));
                var entries = day.Entries.Where(x => x.MealCategoryId == meal.Id).ToArray(); group.Update(meal, entries, ProteinEnabled);
                var rows = new List<SheetEntryViewModel>();
                foreach (var entry in entries)
                {
                    if (!sheetRows.TryGetValue(entry.Id, out var row)) sheetRows[entry.Id] = row = CreateSheetRow(day.Date, meal, entry, meals);
                    else if (!row.HasDraft && row.Original != entry) row.Accept(entry);
                    rows.Add(row); retained.Add(row.Id);
                }
                foreach (var row in group.Rows.Where(x => x.Original is null)) { rows.Add(row); retained.Add(row.Id); }
                Sync(group.Rows, rows); groups.Add(group);
            }
            Sync(model.Meals, groups); nextDays.Add(model);
        }
        Sync(SheetDays, nextDays);
        foreach (var id in sheetRows.Keys.Where(x => !retained.Contains(x) && !sheetRows[x].HasDraft).ToArray()) sheetRows.Remove(id);
        OnPropertyChanged(nameof(SheetRangeText));
    }
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        var desired = values.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--) if (!desired.Contains(target[index])) target.RemoveAt(index);
        var present = target.ToHashSet();
        for (var index = 0; index < values.Count; index++)
        {
            if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], values[index])) continue;
            if (present.Add(values[index])) target.Insert(index, values[index]); else target.Move(target.IndexOf(values[index]), index);
        }
    }
    private SheetEntryViewModel CreateSheetRow(DateOnly date, MealCategory meal, IntakeEntry? entry, MealCategory[]? knownMeals = null) => new(journal, date, meal, entry,
        row => Run(() =>
        {
            try
            {
                if (!row.HasDraft) { row.Message = "本行已保存。"; return; }
                var value = row.CreateEntry();
                Validation.Require(!entryDirty || editingEntry?.Id != row.Id, "同一记录在现有表单仍有未保存修改。");
                if (!SaveWithFoodPolicy(value, out var message, out var added)) { row.Message = "已取消保存，行内草稿保留。"; return; }
                row.Accept(journal.Entries(value.EntryDate).Single(x => x.Id == row.Id));
                if (added) RefreshFoods(); RefreshDay(); RefreshHistory(); row.Message = "本行已保存。" + message; Status = row.Message;
            }
            catch (Exception ex) when (Expected(ex)) { row.Message = "保存未完成：" + ex.Message; Status = row.Message; }
        }), row => Run(() =>
        {
            if (row.Dirty && !Confirm("放弃这行尚未保存的修改？")) return;
            if (row.Original is not null) row.Revert(); else RemoveSheetRow(row);
            RefreshSheet(); Status = "已取消本行修改。";
        }), row => Run(() =>
        {
            if (row.Original is null) { row.Message = "空行尚未写入，请使用移除空行。"; return; }
            DeleteEntry(row.Original);
            if (!journal.Entries(row.Original.EntryDate).Any(x => x.Id == row.Id)) { RemoveSheetRow(row); RefreshSheet(); }
        }), knownMeals);
    private void RemoveSheetRow(SheetEntryViewModel row)
    {
        foreach (var group in SheetDays.SelectMany(x => x.Meals)) group.Rows.Remove(row);
        sheetRows.Remove(row.Id);
    }
    private void AddSheetRow(SheetMealViewModel group)
    {
        Validation.Require(journal.Meals().Any(x => x.Id == group.Meal.Id && x.IsActive), "此餐次已停用，请先在设置中启用。");
        Validation.Require(sheetRows.Values.Count(x => x.Original is null) < 100, "最多同时保留100个新增草稿，请先保存或取消。");
        var row = CreateSheetRow(group.Date, group.Meal, null); sheetRows.Add(row.Id, row); group.Rows.Add(row);
        Status = $"已为{group.Date:yyyy-MM-dd} {group.Meal.Name}新增空行，填写并保存后才计入。";
    }
    private void AddSheetFood(FoodItem food)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (!SheetDays.Any(x => x.Date == today)) OpenSheetDate(today);
        var group = SheetDays.Single(x => x.Date == today).Meals.First(x => x.Meal.IsActive);
        AddSheetRow(group); group.Rows.Last().UseFood(food); TabIndex = 0;
    }
}
