using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly JournalService journal;
    private readonly BackupService backups;
    private bool loading;
    private bool entryDirty;
    private bool foodDirty;
    private bool mealDirty;
    private IntakeEntry? editingEntry;
    private FoodItem? editingFood;
    private FoodItem? librarySelection;
    private DateTime selectedDate = DateTime.Today;
    private DateTime lastToday = DateTime.Today;
    private DateOnly appliedHistoryFrom = DateOnly.FromDateTime(DateTime.Today.AddDays(-29));
    private DateOnly appliedHistoryTo = DateOnly.FromDateTime(DateTime.Today);
    private string savedSource = "用户录入";
    private Guid? savedFoodId;
    private long? sourceUnitKcal;
    public string DataDirectory { get; }
    public string VersionText => "版本 " + typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion + " · 数据库 1 · 备份格式 1";
    public string FoodAttribution => "内置数据来源：USDA FoodData Central / SR Legacy 2018-04 · CC0\n中文名称为应用整理，数值按每100克可食部分。请区分生熟、品牌和实际份量。";
    public ObservableCollection<FoodItem> FoodChoices { get; } = [];
    public ObservableCollection<FoodItem> LibraryFoods { get; } = [];
    public ObservableCollection<MealCategory> AllMeals { get; } = [];
    public ObservableCollection<MealCategory> EntryMeals { get; } = [];
    public ObservableCollection<IntakeEntry> DayEntries { get; } = [];
    public ObservableCollection<DailySummary> HistoryRows { get; } = [];
    public string[] WeightUnits { get; } = ["g", "kg"];

    [ObservableProperty] private string status = "数据只保存在本机。";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string dayTotal = "未记录";
    [ObservableProperty] private string mealTotals = "";
    [ObservableProperty] private IntakeEntry? selectedEntry;
    [ObservableProperty] private MealCategory? entryMeal;
    [ObservableProperty] private DateTime entryDate = DateTime.Today;
    [ObservableProperty] private string entryTime = "";
    [ObservableProperty] private string entryName = "";
    [ObservableProperty] private string entryPreparation = "";
    [ObservableProperty] private string entryNote = "";
    [ObservableProperty] private string directKcal = "";
    [ObservableProperty] private bool weightMode;
    [ObservableProperty] private string weight = "";
    [ObservableProperty] private string weightUnit = "g";
    [ObservableProperty] private string unitKcal = "";
    [ObservableProperty] private string entryFoodQuery = "";
    [ObservableProperty] private FoodItem? chosenFood;
    [ObservableProperty] private string libraryQuery = "";
    [ObservableProperty] private string foodName = "";
    [ObservableProperty] private string foodOriginalName = "";
    [ObservableProperty] private string foodAliases = "";
    [ObservableProperty] private string foodPreparation = "";
    [ObservableProperty] private string foodCategory = "";
    [ObservableProperty] private string foodUnitKcal = "";
    [ObservableProperty] private string foodSourceNote = "";
    [ObservableProperty] private string foodSource = "用户录入";
    private MealCategory? selectedMeal;
    [ObservableProperty] private string mealName = "";
    [ObservableProperty] private string mealOrder = "0";
    [ObservableProperty] private bool mealActive = true;
    [ObservableProperty] private DateTime historyFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime historyTo = DateTime.Today;
    [ObservableProperty] private DailySummary? selectedHistory;
    [ObservableProperty] private int tabIndex;
    public bool NotBusy => !IsBusy;
    public string EntryHeading => editingEntry is null ? "添加摄入" : "修改摄入";
    public string FoodHeading => editingFood is null ? "新增食物" : "编辑食物";
    public string EntrySource => savedSource;
    public string EnergyPreview
    {
        get
        {
            try
            {
                var value = ReadEnergy();
                return $"本项 {value / 10m:0.0} 大卡";
            }
            catch (ValidationException) { return "填写有效数值后显示计算结果"; }
        }
    }

    public DateTime SelectedDate
    {
        get => selectedDate;
        set
        {
            if (value.Date == selectedDate.Date) return;
            if (!ConfirmEntryDiscard()) { OnPropertyChanged(); return; }
            if (value.Date > DateTime.Today) { Status = "记录日期不能晚于今天。"; OnPropertyChanged(); return; }
            SetProperty(ref selectedDate, value.Date);
            ResetEntry();
            RefreshDay();
        }
    }

    public FoodItem? SelectedLibraryFood
    {
        get => librarySelection;
        set
        {
            if (value?.Id == librarySelection?.Id) return;
            if (foodDirty && !Confirm("食物修改尚未保存，是否放弃修改？")) { OnPropertyChanged(); return; }
            SetProperty(ref librarySelection, value);
            LoadFood(value);
        }
    }

    public MealCategory? SelectedMeal
    {
        get => selectedMeal;
        set
        {
            if (value?.Id == selectedMeal?.Id) return;
            if (!loading && mealDirty && !Confirm("放弃未保存的餐次修改？")) { OnPropertyChanged(); return; }
            SetProperty(ref selectedMeal, value); LoadMeal(value);
        }
    }

    public IRelayCommand PreviousDayCommand { get; }
    public IRelayCommand NextDayCommand { get; }
    public IRelayCommand TodayCommand { get; }
    public IRelayCommand NewEntryCommand { get; }
    public IRelayCommand EditEntryCommand { get; }
    public IRelayCommand CopyEntryCommand { get; }
    public IRelayCommand DeleteEntryCommand { get; }
    public IRelayCommand SaveEntryCommand { get; }
    public IRelayCommand ClearFoodCommand { get; }
    public IRelayCommand NewFoodCommand { get; }
    public IRelayCommand SaveFoodCommand { get; }
    public IRelayCommand DeleteFoodCommand { get; }
    public IRelayCommand UseFoodCommand { get; }
    public IRelayCommand NewMealCommand { get; }
    public IRelayCommand SaveMealCommand { get; }
    public IRelayCommand LoadHistoryCommand { get; }
    public IRelayCommand OpenHistoryDayCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IRelayCommand OpenImportCommand { get; }

    public MainViewModel(JournalService journal, BackupService backups, string dataDirectory)
    {
        this.journal = journal; this.backups = backups; DataDirectory = dataDirectory;
        PreviousDayCommand = new RelayCommand(() => Run(() => SelectedDate = SelectedDate.AddDays(-1)));
        NextDayCommand = new RelayCommand(() => Run(() => SelectedDate = SelectedDate.AddDays(1)));
        TodayCommand = new RelayCommand(() => Run(() => SelectedDate = DateTime.Today));
        NewEntryCommand = new RelayCommand(() => Run(() => { if (ConfirmEntryDiscard()) { entryDirty = false; RefreshClockDate(); ResetEntry(); } }));
        EditEntryCommand = new RelayCommand(() => Run(EditEntry));
        CopyEntryCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedEntry is not null, "请先选择一条摄入记录。");
            if (!ConfirmEntryDiscard()) return;
            LoadEntry(SelectedEntry, true);
            Status = "已复制到今天的新增表单。请核对日期、餐次和数值，再点击保存摄入；原记录保持不变。";
        }));
        SaveEntryCommand = new RelayCommand(() => Run(SaveEntry));
        DeleteEntryCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedEntry is not null, "请先选择一条摄入记录。");
            if (!Confirm($"删除“{SelectedEntry.FoodNameSnapshot}”这条记录？")) return;
            journal.DeleteEntry(SelectedEntry.Id);
            if (editingEntry?.Id == SelectedEntry.Id) ResetEntry();
            RefreshDay(); RefreshHistory(); Status = "已删除指定摄入记录。";
        }));
        ClearFoodCommand = new RelayCommand(() =>
        {
            ChosenFood = null; savedFoodId = null; sourceUnitKcal = null; savedSource = "用户录入"; entryDirty = true; OnPropertyChanged(nameof(EntrySource));
        });
        NewFoodCommand = new RelayCommand(() => Run(() =>
        {
            if (foodDirty && !Confirm("放弃未保存的食物修改？")) return;
            librarySelection = null; OnPropertyChanged(nameof(SelectedLibraryFood)); LoadFood(null);
        }));
        SaveFoodCommand = new RelayCommand(() => Run(SaveFood));
        DeleteFoodCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(editingFood is not null, "请先选择一个食物。");
            if (!Confirm($"删除“{editingFood.Name}”？历史摄入会保留。")) return;
            journal.DeleteFood(editingFood); foodDirty = false; librarySelection = null;
            LoadFood(null); RefreshFoods(); Status = "食物已删除，历史记录保持不变。";
        }));
        UseFoodCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedLibraryFood is not null, "请先选择一个食物。");
            Validation.Require(!foodDirty, "请先保存或取消食物修改，再引用食物库条目。");
            if (!ConfirmEntryDiscard()) return;
            ResetEntry(); EntryFoodQuery = ""; ChosenFood = SelectedLibraryFood; TabIndex = 0;
        }));
        NewMealCommand = new RelayCommand(() =>
        {
            if (mealDirty && !Confirm("放弃未保存的餐次修改？")) return;
            loading = true; SelectedMeal = null; MealName = ""; MealOrder = (AllMeals.Count + 1).ToString(CultureInfo.InvariantCulture); MealActive = true; loading = false; mealDirty = false;
        });
        SaveMealCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(int.TryParse(MealOrder, out var order), "顺序须为整数。");
            journal.SaveMeal(new MealCategory { Id = SelectedMeal?.Id ?? Guid.NewGuid(), Name = MealName, SortOrder = order, IsActive = MealActive });
            mealDirty = false;
            RefreshMeals(); Status = "餐次已保存；历史记录保留原餐次名称。";
        }));
        LoadHistoryCommand = new RelayCommand(() => Run(LoadHistory));
        OpenHistoryDayCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedHistory is not null, "请先选择一天。");
            SelectedDate = SelectedHistory.Date.ToDateTime(TimeOnly.MinValue); TabIndex = 0;
        }));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
        OpenImportCommand = new RelayCommand(() => Run(() =>
        {
            if (entryDirty || foodDirty || mealDirty) { Status = "请先保存或取消正在编辑的摄入、食物或餐次，再导入历史记录。"; return; }
            var viewModel = new ImportViewModel(journal, backups);
            new ImportWindow(viewModel) { Owner = Application.Current.MainWindow }.ShowDialog();
            if (!viewModel.Imported) return;
            RefreshDay(); RefreshHistory();
            Status = "历史记录已导入；导入前备份：" + viewModel.RecoveryPath;
        }));
        RefreshMeals(); RefreshFoods(); ResetEntry(); RefreshDay(); RefreshHistory(); LoadFood(null);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(NotBusy));
        if (e.PropertyName is nameof(Weight) or nameof(WeightUnit) or nameof(UnitKcal) or nameof(DirectKcal) or nameof(WeightMode))
            OnPropertyChanged(nameof(EnergyPreview));
        if (loading) return;
        if (e.PropertyName is nameof(EntryDate) or nameof(EntryMeal) or nameof(EntryTime) or nameof(EntryName) or nameof(EntryPreparation) or nameof(EntryNote) or nameof(DirectKcal) or nameof(Weight) or nameof(WeightUnit) or nameof(UnitKcal) or nameof(WeightMode)) entryDirty = true;
        if (e.PropertyName is nameof(FoodName) or nameof(FoodAliases) or nameof(FoodPreparation) or nameof(FoodCategory) or nameof(FoodUnitKcal) or nameof(FoodSourceNote)) foodDirty = true;
        if (e.PropertyName is nameof(MealName) or nameof(MealOrder) or nameof(MealActive)) mealDirty = true;
    }

    partial void OnEntryFoodQueryChanged(string value) => Run(() => Fill(FoodChoices, journal.Foods(value)));
    partial void OnLibraryQueryChanged(string value) => Run(() => Fill(LibraryFoods, journal.Foods(value)));
    partial void OnTabIndexChanged(int value)
    {
        if (value == 2) Run(RefreshHistory);
    }
    partial void OnChosenFoodChanged(FoodItem? value)
    {
        if (value is null || loading) return;
        EntryName = value.Name; EntryPreparation = value.Preparation;
        UnitKcal = value.KcalPer100g.ToString("0.00", CultureInfo.InvariantCulture); WeightMode = true;
        savedFoodId = value.Id; savedSource = value.SourceSummary + " " + value.SourceUrl;
        sourceUnitKcal = value.KcalPer100gHundredths;
        OnPropertyChanged(nameof(EntrySource));
    }
    private void LoadMeal(MealCategory? value)
    {
        var wasLoading = loading; loading = true;
        MealName = value?.Name ?? ""; MealOrder = (value?.SortOrder ?? 0).ToString(CultureInfo.InvariantCulture); MealActive = value?.IsActive ?? true;
        loading = wasLoading; mealDirty = false;
    }

    private static bool Confirm(string text)
    {
        return MessageBox.Show(text, "请确认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    private bool ConfirmEntryDiscard() => !entryDirty || Confirm("摄入内容尚未保存，是否放弃修改？");
    public bool CanClose() => (!entryDirty && !foodDirty && !mealDirty) || Confirm("有未保存的内容，是否放弃并关闭？");

    public void RefreshClockDate()
    {
        if (DateTime.Today == lastToday) return;
        if (!entryDirty && editingEntry is null && SelectedDate.Date == lastToday)
        {
            selectedDate = DateTime.Today; OnPropertyChanged(nameof(SelectedDate)); ResetEntry(); RefreshDay();
        }
        lastToday = DateTime.Today;
    }

    private static bool Expected(Exception ex) => ex is ValidationException or IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException;
    private void Run(Action work)
    {
        if (IsBusy) return;
        try { work(); }
        catch (Exception ex) when (Expected(ex)) { Status = "操作未完成：" + ex.Message; }
    }

    private async Task WorkAsync(Action work, Action after)
    {
        if (IsBusy) return;
        IsBusy = true; Status = "正在处理，请稍候…";
        try { await Task.Run(work); after(); }
        catch (Exception ex) when (Expected(ex)) { Status = "操作未完成：" + ex.Message; }
        finally { IsBusy = false; }
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private void RefreshFoods() { Fill(FoodChoices, journal.Foods(EntryFoodQuery)); Fill(LibraryFoods, journal.Foods(LibraryQuery)); }
    private void RefreshMeals()
    {
        var wasLoading = loading; loading = true;
        try
        {
            var id = EntryMeal?.Id;
            var latest = journal.Meals();
            // 重置摄入表单时不重建未变化的餐次列表，以保留另一页面正在编辑的内容。
            if (!AllMeals.SequenceEqual(latest)) Fill(AllMeals, latest);
            var choices = latest.Where(x => x.IsActive || x.Id == editingEntry?.MealCategoryId).ToArray();
            if (!EntryMeals.SequenceEqual(choices)) Fill(EntryMeals, choices);
            EntryMeal = EntryMeals.FirstOrDefault(x => x.Id == id) ?? EntryMeals.FirstOrDefault();
        }
        finally { loading = wasLoading; }
    }

    private void RefreshDay()
    {
        var rows = journal.Entries(DateOnly.FromDateTime(SelectedDate));
        Fill(DayEntries, rows);
        DayTotal = rows.Length == 0 ? "未记录" : $"{rows.Sum(x => x.EnergyKcalTenths) / 10m:0.0} 大卡 · {rows.Length} 项";
        MealTotals = string.Join("    ", rows.GroupBy(x => x.MealNameSnapshot).Select(x => $"{x.Key} {x.Sum(e => e.EnergyKcalTenths) / 10m:0.0}"));
    }

    private void RefreshHistory() => Fill(HistoryRows, journal.History(appliedHistoryFrom, appliedHistoryTo));

    private void LoadHistory()
    {
        var from = DateOnly.FromDateTime(HistoryFrom);
        var to = DateOnly.FromDateTime(HistoryTo);
        var rows = journal.History(from, to);
        // 只有查询成功才采用新区间；尚未提交或无效的筛选不影响摄入保存后的刷新。
        appliedHistoryFrom = from; appliedHistoryTo = to;
        Fill(HistoryRows, rows);
        Status = $"已查询 {from:yyyy-MM-dd} 至 {to:yyyy-MM-dd}。";
    }

    private void ResetEntry()
    {
        loading = true;
        var mealId = EntryMeal?.Id;
        editingEntry = null; ChosenFood = null; savedFoodId = null; sourceUnitKcal = null; savedSource = "用户录入";
        EntryDate = SelectedDate; EntryTime = ""; EntryName = ""; EntryPreparation = ""; EntryNote = "";
        DirectKcal = ""; Weight = ""; UnitKcal = ""; WeightUnit = "g"; WeightMode = false;
        RefreshMeals(); EntryMeal = EntryMeals.FirstOrDefault(x => x.Id == mealId) ?? EntryMeals.FirstOrDefault();
        loading = false; entryDirty = false;
        OnPropertyChanged(nameof(EntryHeading)); OnPropertyChanged(nameof(EntrySource));
    }

    private void EditEntry()
    {
        Validation.Require(SelectedEntry is not null, "请先选择一条摄入记录。");
        if (!ConfirmEntryDiscard()) return;
        LoadEntry(SelectedEntry, false);
    }

    private void LoadEntry(IntakeEntry row, bool asNew)
    {
        var missingFood = asNew && row.FoodId is not null && !journal.Foods().Any(x => x.Id == row.FoodId);
        loading = true;
        try
        {
            editingEntry = asNew ? null : row; ChosenFood = null;
            savedFoodId = missingFood ? null : row.FoodId;
            sourceUnitKcal = row.KcalPer100gHundredthsSnapshot;
            savedSource = row.SourceSnapshot + (missingFood ? " · 复制历史快照（原食物已删除，未关联食物库）" : "");
            RefreshMeals();
            EntryMeal = asNew
                ? EntryMeals.FirstOrDefault(x => x.Id == row.MealCategoryId) ?? EntryMeals.First()
                : EntryMeals.Single(x => x.Id == row.MealCategoryId);
            EntryDate = asNew ? DateTime.Today : row.EntryDate.ToDateTime(TimeOnly.MinValue);
            EntryTime = asNew ? "" : row.LocalTime;
            EntryName = row.FoodNameSnapshot; EntryPreparation = row.PreparationSnapshot; EntryNote = row.Note;
            WeightMode = row.InputMode == "weight"; DirectKcal = row.EnergyKcal.ToString("0.0", CultureInfo.InvariantCulture);
            Weight = (row.WeightGramsTenths / 10m)?.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
            UnitKcal = (row.KcalPer100gHundredthsSnapshot / 100m)?.ToString("0.00", CultureInfo.InvariantCulture) ?? ""; WeightUnit = "g";
            entryDirty = asNew;
        }
        finally { loading = false; }
        OnPropertyChanged(nameof(EntryHeading)); OnPropertyChanged(nameof(EntrySource));
    }

    private long ReadWeight()
    {
        Validation.Require(WeightUnit is "g" or "kg", "重量单位只支持g和kg。");
        return Validation.Scaled(Validation.Number(Weight, "重量") * (WeightUnit == "kg" ? 1000 : 1), 10, 0.1m, 100000, "重量（克）");
    }
    private long ReadUnitKcal() => Validation.Scaled(Validation.Number(UnitKcal, "每100克大卡"), 100, 0, 10000, "每100克大卡");
    private long ReadEnergy() => WeightMode ? Validation.CalculateEnergy(ReadWeight(), ReadUnitKcal())
        : Validation.Scaled(Validation.Number(DirectKcal, "本项大卡"), 10, 0, 99999.9m, "本项大卡");

    private void SaveEntry()
    {
        Validation.Require(EntryMeal is not null, "请选择餐次。");
        var source = savedSource;
        if (sourceUnitKcal is not null && WeightMode && ReadUnitKcal() != sourceUnitKcal && !source.Contains("本次单位热量已调整", StringComparison.Ordinal)) source += " · 本次单位热量已调整";
        var entry = new IntakeEntry
        {
            Id = editingEntry?.Id ?? Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(EntryDate), LocalTime = EntryTime.Trim(),
            MealCategoryId = EntryMeal.Id,
            MealNameSnapshot = editingEntry?.MealCategoryId == EntryMeal.Id ? editingEntry.MealNameSnapshot : EntryMeal.Name,
            FoodId = savedFoodId, FoodNameSnapshot = EntryName, PreparationSnapshot = EntryPreparation,
            InputMode = WeightMode ? "weight" : "direct", WeightGramsTenths = WeightMode ? ReadWeight() : null,
            KcalPer100gHundredthsSnapshot = WeightMode ? ReadUnitKcal() : null, EnergyKcalTenths = ReadEnergy(),
            Note = EntryNote, SourceSnapshot = source, CreatedAtUtc = editingEntry?.CreatedAtUtc ?? DateTimeOffset.UtcNow
        };
        journal.SaveEntry(entry);
        entryDirty = false;
        if (SelectedDate.Date != EntryDate.Date) { selectedDate = EntryDate.Date; OnPropertyChanged(nameof(SelectedDate)); }
        ResetEntry(); RefreshDay(); RefreshHistory(); Status = "摄入已保存，可以继续添加下一项。";
    }

    private void LoadFood(FoodItem? value)
    {
        loading = true; editingFood = value;
        FoodName = value?.Name ?? ""; FoodOriginalName = value?.OriginalName ?? ""; FoodAliases = value?.Aliases ?? "";
        FoodPreparation = value?.Preparation ?? ""; FoodCategory = value?.Category ?? "";
        FoodUnitKcal = value?.KcalPer100g.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        FoodSourceNote = value?.SourceNote ?? ""; FoodSource = value?.SourceSummary ?? "用户录入";
        loading = false; foodDirty = false; OnPropertyChanged(nameof(FoodHeading));
    }

    private void SaveFood()
    {
        if (editingFood is null && journal.Foods().Any(x => x.Name.Equals(FoodName.Trim(), StringComparison.OrdinalIgnoreCase)) && !Confirm("已有同名食物，是否仍新增为独立条目？")) return;
        var food = (editingFood ?? new FoodItem { Id = Guid.NewGuid(), Name = "", KcalPer100gHundredths = 0 }) with
        {
            Name = FoodName, Aliases = FoodAliases, Preparation = FoodPreparation, Category = FoodCategory,
            KcalPer100gHundredths = Validation.Scaled(Validation.Number(FoodUnitKcal, "每100克大卡"), 100, 0, 10000, "每100克大卡"),
            SourceNote = FoodSourceNote, UserModified = true
        };
        journal.SaveFood(food); foodDirty = false;
        librarySelection = null; LoadFood(null); RefreshFoods(); Status = "食物已保存，历史摄入数值保持不变。";
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog { Title = "导出完整备份（请选择新文件名）", Filter = "完整备份|*.calorie-backup.json", FileName = $"热量备份_{DateTime.Now:yyyyMMdd_HHmmss}.calorie-backup.json", InitialDirectory = DataDirectory };
        if (dialog.ShowDialog() != true) return;
        await WorkAsync(() => backups.Export(dialog.FileName), () => Status = "完整备份已导出：" + dialog.FileName);
    }

    private async Task RestoreAsync()
    {
        if (entryDirty || foodDirty || mealDirty) { Status = "请先保存或取消正在编辑的摄入、食物或餐次内容，再恢复备份。"; return; }
        var dialog = new OpenFileDialog { Title = "选择完整备份", Filter = "完整备份|*.calorie-backup.json", InitialDirectory = DataDirectory };
        if (dialog.ShowDialog() != true) return;
        JournalSnapshot? snapshot = null;
        await WorkAsync(() => snapshot = backups.Inspect(dialog.FileName), () => Status = "备份校验完成。");
        if (snapshot is null) return;
        if (!Confirm($"将完整替换当前数据。\n备份时间：{snapshot.ExportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n食物 {snapshot.Foods.Length} 项，餐次 {snapshot.Meals.Length} 项，摄入 {snapshot.Entries.Length} 项。\n现有数据会先自动备份，是否恢复？")) return;
        var recoveryPath = "";
        await WorkAsync(() => recoveryPath = backups.Restore(snapshot), () =>
        {
            RefreshMeals(); RefreshFoods(); ResetEntry(); RefreshDay(); RefreshHistory();
            Status = "已恢复。恢复前数据保存在：" + recoveryPath;
        });
    }
}
