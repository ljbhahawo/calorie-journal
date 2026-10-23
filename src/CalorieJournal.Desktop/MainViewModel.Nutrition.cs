using System.Globalization;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel
{
    public string[] EnergyUnits { get; } = ["大卡", "kJ"];
    [ObservableProperty] private string entryEnergyUnit = "大卡";
    [ObservableProperty] private string directEnergyUnit = "大卡";
    [ObservableProperty] private string foodEnergyUnit = "大卡";
    [ObservableProperty] private string unitProtein = "";
    [ObservableProperty] private string directProtein = "";
    [ObservableProperty] private string foodProtein = "";
    [ObservableProperty] private string dayProteinText = "蛋白质未记录";
    public bool DirectProteinEnabled => !WeightMode;
    public string ProteinPreview
    {
        get { if (string.IsNullOrWhiteSpace(WeightMode ? UnitProtein : DirectProtein)) return "蛋白质未填写（可选）";
            try { return "本项蛋白质 " + Nutrition.ProteinText(ReadProtein()); } catch (ValidationException ex) { return "蛋白质：" + ex.Message; } }
    }
    public string UnitEnergyPreview
    {
        get { try { return $"每100克 {ReadUnitKcal() / 100m:0.00} 大卡"; } catch (ValidationException) { return "填写有效的每100克能量后换算"; } }
    }
    public string FoodEnergyPreview
    {
        get { try { return $"折合每100克 {ReadFoodUnitKcal() / 100m:0.00} 大卡"; } catch (ValidationException) { return "填写有效能量后显示大卡"; } }
    }
    partial void OnEntryEnergyUnitChanged(string? oldValue, string newValue) { if (!loading) UnitKcal = ConvertUnitText(UnitKcal, oldValue, newValue, false); }
    partial void OnDirectEnergyUnitChanged(string? oldValue, string newValue) { if (!loading) DirectKcal = ConvertUnitText(DirectKcal, oldValue, newValue, true); }
    partial void OnFoodEnergyUnitChanged(string? oldValue, string newValue) { if (!loading) FoodUnitKcal = ConvertUnitText(FoodUnitKcal, oldValue, newValue, false); }
    private string ConvertUnitText(string text, string? from, string to, bool direct)
    {
        if (string.IsNullOrWhiteSpace(text) || from == to) return text;
        if (!EnergyUnits.Contains(from) || !EnergyUnits.Contains(to)) { Status = "能量单位无效，请重新选择。"; return text; }
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 999999)
        { Status = "单位已切换，原输入无效；请重新填写并核对数值。"; return text; }
        var converted = to == "kJ" ? value * Nutrition.KilojoulesPerKcal : value / Nutrition.KilojoulesPerKcal;
        Status = "能量单位已换算，显示值按输入精度舍入，请核对营养成分表。";
        return decimal.Round(converted, to == "大卡" && direct ? 1 : 2, MidpointRounding.AwayFromZero).ToString(to == "大卡" && direct ? "0.0" : "0.00", CultureInfo.InvariantCulture);
    }
    private static long? ReadOptionalProtein(string text, decimal maximum, string label) => string.IsNullOrWhiteSpace(text) ? null : Validation.Scaled(Validation.Number(text, label), 100, 0, maximum, label);
    private static bool IsKilojoules(string unit) { Validation.Require(unit is "大卡" or "kJ", "请选择有效能量单位。"); return unit == "kJ"; }
    private long? ReadEntryKilojoules() => IsKilojoules(WeightMode ? EntryEnergyUnit : DirectEnergyUnit) ? Nutrition.Kilojoules(Validation.Number(WeightMode ? UnitKcal : DirectKcal, "千焦kJ"), WeightMode ? 10000m : 99999.9m) : null;
    private long ReadFoodUnitKcal() => IsKilojoules(FoodEnergyUnit) ? Nutrition.UnitKcal(Nutrition.Kilojoules(Validation.Number(FoodUnitKcal, "食物每100克千焦"), 10000m)) : Validation.Scaled(Validation.Number(FoodUnitKcal, "每100克大卡"), 100, 0, 10000m, "每100克大卡");
    private long? ReadProtein() => WeightMode ? Nutrition.Protein(ReadWeight(), ReadOptionalProtein(UnitProtein, 100m, "每100克蛋白质")) : ReadOptionalProtein(DirectProtein, 100000m, "本项蛋白质");
    public void EnterDirectEnergy()
    {
        if (!loading && !IsBusy && WeightMode) { WeightMode = false; Status = "已切换直接录入，按本项热量及可选蛋白质保存。"; }
    }
}
