using System.Text.Json.Serialization;

namespace CalorieJournal.Core;

public sealed record FoodPortion
{
    public required Guid Id { get; init; }
    public required Guid FoodId { get; init; }
    public required string Name { get; init; }
    public required long GramsTenths { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string Display => $"{Name} · {GramsTenths / 10m:0.0}g";
}

public sealed record WeightDeduction(string Name, long GramsTenths);

public sealed record WeightCalculationSnapshot
{
    public required string Kind { get; init; }
    public long? GrossGramsTenths { get; init; }
    public WeightDeduction[] Deductions { get; init; } = [];
    public Guid? PortionId { get; init; }
    public string PortionName { get; init; } = "";
    public long? PortionGramsTenths { get; init; }
    public long? PortionCountThousandths { get; init; }
    [JsonIgnore] public long NetGramsTenths => WeightCalculator.Calculate(this);
    [JsonIgnore] public string Description => Kind == "gross"
        ? $"总重 {GrossGramsTenths / 10m:0.0}g − " + string.Join(" − ", Deductions.Select(x => $"{x.Name} {x.GramsTenths / 10m:0.0}g"))
        : $"{PortionName} {PortionCountThousandths / 1000m:0.###}份 × {PortionGramsTenths / 10m:0.0}g/份";
}

public static class WeightCalculator
{
    public static long Grams(decimal value, string unit, string label, bool allowZero = false)
    {
        Validation.Require(unit is "g" or "kg", "重量单位只支持g和kg。");
        Validation.Require(value >= 0 && value <= (unit == "kg" ? 100 : 100000), $"{label}超出重量范围。");
        return Validation.Scaled(value * (unit == "kg" ? 1000 : 1), 10, allowZero ? 0 : 0.1m, 100000, label);
    }

    public static void Portion(FoodPortion value)
    {
        Validation.Require(value.Id != Guid.Empty && value.FoodId != Guid.Empty, "份量或食物ID无效。");
        Validation.Text(value.Name, "份量名称", 30, true);
        Validation.Require(value.GramsTenths is > 0 and <= 1_000_000, "每份重量须为0.1到100000克。");
        Validation.Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "份量时间无效。");
    }

    public static long Calculate(WeightCalculationSnapshot value)
    {
        Validation.Require(value.Deductions is not null && value.Deductions.Length <= 30, "扣除项最多30项，不能为null。");
        long net;
        if (value.Kind == "gross")
        {
            Validation.Require(value.GrossGramsTenths is > 0 and <= 1_000_000, "总重须为0.1到100000克。");
            Validation.Require(value.Deductions.Length > 0, "请至少填写一个扣除项。");
            Validation.Require(value.PortionId is null && value.PortionName == "" && value.PortionGramsTenths is null && value.PortionCountThousandths is null, "总重计算不能混入份量字段。");
            foreach (var deduction in value.Deductions)
            {
                Validation.Require(deduction is not null, "扣除项不能为空。");
                Validation.Text(deduction.Name, "扣除名称", 30, true);
                Validation.Require(deduction.GramsTenths is >= 0 and <= 1_000_000, "扣除重量须为0到100000克。");
            }
            net = value.GrossGramsTenths.Value - value.Deductions.Sum(x => x.GramsTenths);
            Validation.Require(net >= 0, "扣除重量不能大于总重。");
        }
        else
        {
            Validation.Require(value.Kind == "portion", "未知重量换算方式。");
            Validation.Require(value.GrossGramsTenths is null && value.Deductions.Length == 0, "份量计算不能混入总重字段。");
            Validation.Require(value.PortionId is not null && value.PortionId != Guid.Empty, "份量来源ID无效。");
            Validation.Text(value.PortionName, "份量名称", 30, true);
            Validation.Require(value.PortionGramsTenths is > 0 and <= 1_000_000, "每份克数无效。");
            Validation.Require(value.PortionCountThousandths is > 0 and <= 100_000_000, "份数须大于0且不超过100000，最多3位小数。");
            net = (long)decimal.Round(value.PortionGramsTenths.Value * (decimal)value.PortionCountThousandths.Value / 1000m, 0, MidpointRounding.AwayFromZero);
        }
        Validation.Require(net > 0, "没有可记录的食用重量；请核对输入，不能保存为正热量。");
        Validation.Require(net <= 1_000_000, "最终可食重量不能超过100000克。");
        return net;
    }
}
