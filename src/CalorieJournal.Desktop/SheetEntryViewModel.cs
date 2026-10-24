using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class SheetEntryViewModel : ObservableObject
{
    private readonly JournalService journal;
    private bool loading;
    private Guid? foodId;
    private string referencedName = "";
    private long? referencedEnergy, referencedProtein, referencedKj;
    private WeightCalculationSnapshot? calculation;
    public Guid Id { get; }
    public IntakeEntry? Original { get; private set; }
    public MealCategory[] Meals { get; private set; } = [];
    public string[] WeightUnits { get; } = ["g", "kg"];
    public string[] EnergyUnits { get; } = ["大卡", "kJ"];
    [ObservableProperty] private DateTime date;
    private bool dateInputValid = true;
    public DateTime? DateInput
    {
        get => dateInputValid ? Date : null;
        set
        {
            var changed = dateInputValid != (value is not null) || value is { } next && next != Date;
            dateInputValid = value is not null; if (value is { } dateValue) Date = dateValue;
            if (changed && !loading) Dirty = true; OnPropertyChanged();
        }
    }
    partial void OnDateChanged(DateTime value) { dateInputValid = true; OnPropertyChanged(nameof(DateInput)); }
    [ObservableProperty] private MealCategory? meal;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string preparation = "";
    [ObservableProperty] private string time = "";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private bool weightMode;
    [ObservableProperty] private string weight = "";
    [ObservableProperty] private string weightUnit = "g";
    [ObservableProperty] private string unitEnergy = "";
    [ObservableProperty] private string unitEnergyType = "大卡";
    [ObservableProperty] private string unitProtein = "";
    [ObservableProperty] private string directEnergy = "";
    [ObservableProperty] private string directEnergyType = "大卡";
    [ObservableProperty] private string directProtein = "";
    [ObservableProperty] private string source = "用户录入";
    [ObservableProperty] private bool dirty;
    [ObservableProperty] private string message = "";
    public bool MessageIsError => Message.StartsWith("保存未完成：", StringComparison.Ordinal);
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(MessageIsError));
    public bool ProteinEnabled { get; private set; }
    public void RefreshProteinSetting() { ProteinEnabled = journal.Settings().RecordsProtein; OnPropertyChanged(nameof(ProteinEnabled)); OnPropertyChanged(nameof(ProteinPreview)); }
    public bool HasDraft => Original is null || Dirty;
    public string DiscardText => Original is null ? Dirty ? "放弃新增" : "移除空行" : "放弃修改";
    public bool DirectMode => !WeightMode;
    public string StateText => Original is null ? "新增 · 未保存" : Dirty ? "已修改 · 未保存" : "已保存";
    public string EnergyPreview { get { try { return $"{ReadEnergy() / 10m:0.0} 大卡"; } catch (ValidationException) { return "待填写热量"; } } }
    public string ProteinPreview { get { try { return "蛋白质 " + Nutrition.ProteinText(ReadProtein()); } catch (ValidationException ex) { return ex.Message; } } }
    public string WeightDescription => calculation?.Description ?? "按实际可食重量计算";
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand ConvertWeightCommand { get; }
    public IRelayCommand ManualSourceCommand { get; }

    public SheetEntryViewModel(JournalService journal, DateOnly date, MealCategory meal, IntakeEntry? original,
        Action<SheetEntryViewModel> save, Action<SheetEntryViewModel> cancel, Action<SheetEntryViewModel> delete, MealCategory[]? knownMeals = null)
    {
        this.journal = journal; Id = original?.Id ?? Guid.NewGuid(); RefreshProteinSetting();
        SaveCommand = new RelayCommand(() => save(this)); CancelCommand = new RelayCommand(() => cancel(this)); DeleteCommand = new RelayCommand(() => delete(this));
        ManualSourceCommand = new RelayCommand(DetachFood);
        ConvertWeightCommand = new RelayCommand(() =>
        {
            try
            {
                Validation.Require(WeightMode, "请先选择按重量计算。");
                var model = new WeightConversionViewModel(journal, foodId, ReadUnitEnergy(), calculation, ReadKj());
                var window = new WeightConversionWindow(model, journal) { Owner = Application.Current.MainWindow };
                window.ShowDialog(); if (window.Result is not { } result) return;
                Weight = (result.NetGramsTenths / 10m).ToString("0.0", CultureInfo.InvariantCulture); WeightUnit = "g"; calculation = result;
                OnPropertyChanged(nameof(WeightDescription)); Message = "换算已带入，请保存本行。";
            }
            catch (Exception ex) when (ex is ValidationException or System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { Message = ex.Message; }
        });
        Load(original, date, meal, knownMeals);
    }
    public void Accept(IntakeEntry value) { var latest = journal.Meals(); Load(value, value.EntryDate, latest.Single(x => x.Id == value.MealCategoryId), latest); }
    public void Revert() { if (Original is { } original) Accept(original); }
    private void Load(IntakeEntry? value, DateOnly targetDate, MealCategory targetMeal, MealCategory[]? knownMeals = null)
    {
        loading = true;
        try
        {
            Original = value; dateInputValid = true; Meals = (knownMeals ?? journal.Meals()).Where(x => x.IsActive || x.Id == value?.MealCategoryId).ToArray();
            Date = targetDate.ToDateTime(TimeOnly.MinValue); Meal = Meals.FirstOrDefault(x => x.Id == targetMeal.Id);
            Name = value?.FoodNameSnapshot ?? ""; Preparation = value?.PreparationSnapshot ?? ""; Time = value?.LocalTime ?? ""; Note = value?.Note ?? "";
            WeightMode = value is null ? journal.Settings().DefaultWeightMode : value.InputMode == "weight";
            Weight = value?.WeightGramsTenths is { } grams ? Format(grams, 10, "0.0") : ""; WeightUnit = "g";
            UnitEnergyType = value?.InputMode == "weight" && value.KilojoulesHundredthsSnapshot is not null ? "kJ" : "大卡";
            UnitEnergy = value?.KcalPer100gHundredthsSnapshot is { } kcal ? Format(UnitEnergyType == "kJ" ? value.KilojoulesHundredthsSnapshot!.Value : kcal, 100, "0.00") : "";
            UnitProtein = value?.ProteinPer100gHundredthsSnapshot is { } unitProteinValue ? Format(unitProteinValue, 100, "0.00") : "";
            DirectEnergyType = value?.InputMode == "direct" && value.KilojoulesHundredthsSnapshot is not null ? "kJ" : "大卡";
            DirectEnergy = value is null ? "" : DirectEnergyType == "kJ" ? Format(value.KilojoulesHundredthsSnapshot!.Value, 100, "0.00") : Format(value.EnergyKcalTenths, 10, "0.0");
            DirectProtein = value?.ProteinGramsHundredths is { } protein ? Format(protein, 100, "0.00") : "";
            foodId = value?.FoodId; referencedName = Name; referencedEnergy = value?.KcalPer100gHundredthsSnapshot; referencedProtein = value?.ProteinPer100gHundredthsSnapshot; referencedKj = value?.KilojoulesHundredthsSnapshot;
            Source = value?.SourceSnapshot ?? "用户录入"; calculation = value?.WeightCalculation; Dirty = false; Message = "";
        }
        finally { loading = false; }
        OnPropertyChanged(nameof(Meals)); OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(DiscardText)); OnPropertyChanged(nameof(WeightDescription));
    }
    public FoodItem[] Suggest(string query) => string.IsNullOrWhiteSpace(query) ? [] : journal.Foods(query).Take(12).ToArray();
    public void UseFood(FoodItem food)
    {
        Validation.Require(journal.Foods().Any(x => x.Id == food.Id), "食物已删除，请重新选择。");
        loading = true;
        try
        {
            Name = food.Name; Preparation = food.Preparation; foodId = food.Id; referencedName = food.Name;
            referencedEnergy = food.KcalPer100gHundredths; referencedProtein = food.ProteinPer100gHundredths; referencedKj = food.KilojoulesPer100gHundredths;
            UnitEnergyType = referencedKj is null ? "大卡" : "kJ"; UnitEnergy = Format(referencedKj ?? food.KcalPer100gHundredths, 100, "0.00");
            UnitProtein = referencedProtein is { } protein ? Format(protein, 100, "0.00") : ""; WeightMode = true;
            Source = food.SourceSummary + " " + food.SourceUrl; calculation = null;
        }
        finally { loading = false; }
        Dirty = true; OnPropertyChanged(nameof(WeightDescription)); Message = "已引用食物，请填写重量并保存本行。";
    }
    public void EnterDirect() { if (!loading) WeightMode = false; }
    private void DetachFood() { foodId = null; referencedName = ""; referencedEnergy = referencedProtein = referencedKj = null; Source = "用户录入 · 手动填写本行资料"; Dirty = true; }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(WeightMode)) OnPropertyChanged(nameof(DirectMode));
        if (e.PropertyName is nameof(Dirty)) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(HasDraft)); OnPropertyChanged(nameof(DiscardText)); return; }
        if (e.PropertyName is nameof(Date) or nameof(Meal) or nameof(Name) or nameof(Preparation) or nameof(Time) or nameof(Note) or nameof(WeightMode) or nameof(Weight) or nameof(WeightUnit) or nameof(UnitEnergy) or nameof(UnitEnergyType) or nameof(UnitProtein) or nameof(DirectEnergy) or nameof(DirectEnergyType) or nameof(DirectProtein))
        {
            OnPropertyChanged(nameof(EnergyPreview)); OnPropertyChanged(nameof(ProteinPreview));
            if (loading) return;
            if (MessageIsError) Message = "";
            Dirty = true;
            if (e.PropertyName == nameof(Name) && foodId is not null && Name != referencedName) DetachFood();
            if (e.PropertyName is nameof(Weight) or nameof(WeightUnit)) { calculation = null; OnPropertyChanged(nameof(WeightDescription)); }
        }
    }
    partial void OnUnitEnergyTypeChanged(string? oldValue, string newValue) { if (!loading) UnitEnergy = ConvertEnergy(UnitEnergy, oldValue, newValue, false); }
    partial void OnDirectEnergyTypeChanged(string? oldValue, string newValue) { if (!loading) DirectEnergy = ConvertEnergy(DirectEnergy, oldValue, newValue, true); }
    private static string ConvertEnergy(string text, string? from, string to, bool direct)
    {
        if (string.IsNullOrWhiteSpace(text) || from == to || from is not ("大卡" or "kJ") || to is not ("大卡" or "kJ")) return text;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 999999) return text;
        return decimal.Round(to == "kJ" ? value * Nutrition.KilojoulesPerKcal : value / Nutrition.KilojoulesPerKcal, direct && to == "大卡" ? 1 : 2, MidpointRounding.AwayFromZero).ToString(direct && to == "大卡" ? "0.0" : "0.00", CultureInfo.InvariantCulture);
    }
    private static string Format(long value, decimal scale, string format) => (value / scale).ToString(format, CultureInfo.InvariantCulture);
    private static long? OptionalProtein(string text, decimal maximum) => string.IsNullOrWhiteSpace(text) ? null : Validation.Scaled(Validation.Number(text, "蛋白质g"), 100, 0, maximum, "蛋白质g");
    private static bool IsKj(string value) { Validation.Require(value is "大卡" or "kJ", "请选择有效能量单位。"); return value == "kJ"; }
    private long ReadWeight() => WeightCalculator.Grams(Validation.Number(Weight, "重量"), WeightUnit, "可食重量");
    private long ReadUnitEnergy() => IsKj(UnitEnergyType) ? Nutrition.UnitKcal(Nutrition.Kilojoules(Validation.Number(UnitEnergy, "每100克kJ"), 10000)) : Validation.Scaled(Validation.Number(UnitEnergy, "每100克大卡"), 100, 0, 10000, "每100克大卡");
    private long? ReadKj() => IsKj(WeightMode ? UnitEnergyType : DirectEnergyType) ? Nutrition.Kilojoules(Validation.Number(WeightMode ? UnitEnergy : DirectEnergy, "kJ"), WeightMode ? 10000 : 99999.9m) : null;
    private long ReadEnergy() => WeightMode ? Nutrition.Energy(ReadWeight(), ReadUnitEnergy(), ReadKj()) : IsKj(DirectEnergyType) ? Nutrition.DirectKcal(ReadKj()!.Value) : Validation.Scaled(Validation.Number(DirectEnergy, "本项大卡"), 10, 0, 99999.9m, "本项大卡");
    private long? ReadUnitProtein() => ProteinEnabled ? OptionalProtein(UnitProtein, 100) : Original?.ProteinPer100gHundredthsSnapshot;
    private long? ReadProtein() => WeightMode ? Nutrition.Protein(ReadWeight(), ReadUnitProtein()) : ProteinEnabled ? OptionalProtein(DirectProtein, 100000) : Original?.ProteinGramsHundredths;
    public IntakeEntry CreateEntry()
    {
        Validation.Require(Meal is not null, "请选择有效餐次。");
        Validation.Require(dateInputValid, "请填写有效日期。");
        Validation.Text(Name, "食物名称", 100, true);
        Validation.Require(ProteinEnabled || Original?.ProteinGramsHundredths is null || WeightMode == (Original.InputMode == "weight"), "此记录已有蛋白质，切换录入方式前请在设置启用蛋白质。");
        Validation.Require(ProteinEnabled || Original?.ProteinGramsHundredths is null || (Name == Original.FoodNameSnapshot && Preparation == Original.PreparationSnapshot && foodId == Original.FoodId), "此记录已有蛋白质，修改食物名称或来源前请在设置启用蛋白质，以核对营养资料。");
        var source = Source;
        if (foodId is not null && WeightMode && (ReadUnitEnergy() != referencedEnergy || ReadUnitProtein() != referencedProtein || ReadKj() != referencedKj) && !source.Contains("本次单位营养资料已调整", StringComparison.Ordinal)) source += " · 本次单位营养资料已调整";
        if (foodId is not null && !WeightMode && !source.Contains("本项直接录入", StringComparison.Ordinal)) source += " · 本项直接录入，热量及蛋白质请核对";
        var entry = new IntakeEntry { Id = Id, EntryDate = DateOnly.FromDateTime(Date), MealCategoryId = Meal.Id,
            MealNameSnapshot = Original is { } original && original.MealCategoryId == Meal.Id ? original.MealNameSnapshot : Meal.Name,
            LocalTime = Time, FoodId = foodId, FoodNameSnapshot = Name, PreparationSnapshot = Preparation, Note = Note,
            InputMode = WeightMode ? "weight" : "direct", WeightGramsTenths = WeightMode ? ReadWeight() : null, KcalPer100gHundredthsSnapshot = WeightMode ? ReadUnitEnergy() : null,
            EnergyKcalTenths = ReadEnergy(), ProteinPer100gHundredthsSnapshot = WeightMode ? ReadUnitProtein() : null, ProteinGramsHundredths = ReadProtein(), KilojoulesHundredthsSnapshot = ReadKj(),
            WeightCalculation = WeightMode ? calculation : null, SourceSnapshot = source, CreatedAtUtc = Original?.CreatedAtUtc ?? DateTimeOffset.UtcNow };
        Validation.Entry(entry); return entry;
    }
}
