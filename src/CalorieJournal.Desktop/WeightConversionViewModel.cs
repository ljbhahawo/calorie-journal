using System.Collections.ObjectModel;
using System.ComponentModel;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalorieJournal.Desktop;

public sealed partial class WeightDeductionInput : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string weight = "";
    [ObservableProperty] private string unit = "g";
}

public sealed record PortionOption(Guid Id, string Name, long GramsTenths, bool Historical = false)
{
    public string Display => $"{Name} · {GramsTenths / 10m:0.0}g" + (Historical ? "（保存时的历史值）" : "");
}

public sealed partial class WeightConversionViewModel : ObservableObject
{
    private readonly JournalService journal;
    private readonly long unitKcal;
    private readonly long? unitKilojoules;
    public Guid? FoodId { get; }
    public string[] Units { get; } = ["g", "kg"];
    public ObservableCollection<WeightDeductionInput> Deductions { get; } = [];
    public ObservableCollection<PortionOption> Portions { get; } = [];
    [ObservableProperty] private bool usePortion;
    [ObservableProperty] private string gross = "";
    [ObservableProperty] private string grossUnit = "g";
    [ObservableProperty] private string portionCount = "1";
    [ObservableProperty] private PortionOption? selectedPortion;
    [ObservableProperty] private WeightDeductionInput? selectedDeduction;
    [ObservableProperty] private string status = "换算只更新草稿；返回主窗后仍需保存摄入。";
    public bool CanManagePortions => FoodId is not null && journal.Foods().Any(x => x.Id == FoodId);
    public string Preview
    {
        get
        {
            try { var value = CreateSnapshot(); return $"可食重量 {value.NetGramsTenths / 10m:0.0}g · {Nutrition.Energy(value.NetGramsTenths, unitKcal, unitKilojoules) / 10m:0.0}大卡"; }
            catch (ValidationException exception) { return exception.Message; }
        }
    }

    public WeightConversionViewModel(JournalService journal, Guid? foodId, long unitKcal, WeightCalculationSnapshot? snapshot, long? unitKilojoules = null)
    {
        this.journal = journal; this.unitKcal = unitKcal; this.unitKilojoules = unitKilojoules; FoodId = foodId;
        if (snapshot?.Kind == "gross")
        {
            Gross = (snapshot.GrossGramsTenths / 10m)!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var deduction in snapshot.Deductions) AddDeduction(new WeightDeductionInput { Name = deduction.Name, Weight = (deduction.GramsTenths / 10m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) });
        }
        else AddDeduction(new WeightDeductionInput());
        ReloadPortions(snapshot?.Kind == "portion" ? new PortionOption(snapshot.PortionId!.Value, snapshot.PortionName, snapshot.PortionGramsTenths!.Value, true) : null);
        if (snapshot?.Kind == "portion")
        {
            UsePortion = true;
            PortionCount = (snapshot.PortionCountThousandths / 1000m)!.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(UsePortion) or nameof(Gross) or nameof(GrossUnit) or nameof(PortionCount) or nameof(SelectedPortion)) OnPropertyChanged(nameof(Preview));
    }

    public void AddDeduction(WeightDeductionInput? input = null)
    {
        Validation.Require(Deductions.Count < 30, "扣除项最多30项。");
        input ??= new WeightDeductionInput();
        input.PropertyChanged += DeductionChanged;
        Deductions.Add(input); OnPropertyChanged(nameof(Preview));
    }

    private void DeductionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Preview));

    public void RemoveSelectedDeduction()
    {
        Validation.Require(SelectedDeduction is not null, "请先选择一个草稿扣除项。");
        var row = SelectedDeduction; row.PropertyChanged -= DeductionChanged;
        Deductions.Remove(row); OnPropertyChanged(nameof(Preview));
    }

    public void ReloadPortions(PortionOption? preserve = null)
    {
        preserve ??= SelectedPortion;
        Portions.Clear();
        if (FoodId is not null && CanManagePortions)
            foreach (var value in journal.Portions(FoodId.Value)) Portions.Add(new PortionOption(value.Id, value.Name, value.GramsTenths));
        var same = Portions.FirstOrDefault(x => x.Id == preserve?.Id && x.Name == preserve.Name && x.GramsTenths == preserve.GramsTenths);
        if (preserve is not null && same is null) { same = preserve with { Historical = true }; Portions.Add(same); }
        SelectedPortion = same ?? Portions.FirstOrDefault();
    }

    public WeightCalculationSnapshot CreateSnapshot()
    {
        WeightCalculationSnapshot snapshot;
        if (UsePortion)
        {
            Validation.Require(SelectedPortion is not null, "请先选择或维护当前食物的常用份量。");
            snapshot = new WeightCalculationSnapshot { Kind = "portion", PortionId = SelectedPortion.Id, PortionName = SelectedPortion.Name,
                PortionGramsTenths = SelectedPortion.GramsTenths, PortionCountThousandths = Validation.Scaled(Validation.Number(PortionCount, "份数"), 1000, 0.001m, 100000, "份数") };
        }
        else snapshot = new WeightCalculationSnapshot { Kind = "gross", GrossGramsTenths = WeightCalculator.Grams(Validation.Number(Gross, "总重"), GrossUnit, "总重"),
            Deductions = Deductions.Select(x => new WeightDeduction(Validation.Text(x.Name, "扣除名称", 30, true), WeightCalculator.Grams(Validation.Number(x.Weight, "扣重"), x.Unit, "扣重", true))).ToArray() };
        _ = snapshot.NetGramsTenths;
        return snapshot;
    }
}
