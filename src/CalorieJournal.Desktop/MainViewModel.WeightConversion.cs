using System.Globalization;
using CalorieJournal.Core;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel
{
    private WeightCalculationSnapshot? weightCalculation;
    public string WeightCalculationDescription => weightCalculation is null ? "可输入已称量的可食重量，或使用下方换算。" : weightCalculation.Description + "；手动修改重量或单位会取消此依据。";

    private void SetWeightCalculation(WeightCalculationSnapshot? value)
    {
        weightCalculation = value; OnPropertyChanged(nameof(WeightCalculationDescription));
    }

    private void TrackWeightChange(string? propertyName)
    {
        if (weightCalculation is not null && (propertyName is nameof(Weight) or nameof(WeightUnit) || propertyName == nameof(WeightMode) && !WeightMode))
        {
            SetWeightCalculation(null);
            Status = "重量或录入方式已手动修改，此前换算依据已取消，保存时采用当前表单数值。";
        }
    }

    public void ApplyWeightCalculation(WeightCalculationSnapshot value)
    {
        var grams = WeightCalculator.Calculate(value);
        var wasLoading = loading; loading = true;
        try { WeightMode = true; WeightUnit = "g"; Weight = (grams / 10m).ToString("0.0", CultureInfo.InvariantCulture); SetWeightCalculation(value); }
        finally { loading = wasLoading; }
        entryDirty = true;
        Status = "可食重量已带入草稿，尚未保存；请核对大卡并点击保存摄入。";
    }
}
