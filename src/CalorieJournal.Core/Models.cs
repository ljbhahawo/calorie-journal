using System.Text.Json.Serialization;

namespace CalorieJournal.Core;

public sealed record FoodItem
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string OriginalName { get; init; } = "";
    public string Aliases { get; init; } = "";
    public string Preparation { get; init; } = "";
    public string Category { get; init; } = "";
    public required long KcalPer100gHundredths { get; init; }
    public string SourceKind { get; init; } = "用户录入";
    public string SourceId { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string SourceNote { get; init; } = "";
    public string BuiltinKey { get; init; } = "";
    public bool UserModified { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public decimal KcalPer100g => KcalPer100gHundredths / 100m;
    [JsonIgnore] public string SourceSummary => string.Join(" · ", new[] { SourceKind, SourceId, SourceVersion, UserModified ? "用户修改" : "", SourceNote }.Where(x => x.Length > 0));
}

public sealed record MealCategory
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record IntakeEntry
{
    public required Guid Id { get; init; }
    public required DateOnly EntryDate { get; init; }
    public string LocalTime { get; init; } = "";
    public required Guid MealCategoryId { get; init; }
    public required string MealNameSnapshot { get; init; }
    public Guid? FoodId { get; init; }
    public required string FoodNameSnapshot { get; init; }
    public string PreparationSnapshot { get; init; } = "";
    public string InputMode { get; init; } = "direct";
    public long? WeightGramsTenths { get; init; }
    public long? KcalPer100gHundredthsSnapshot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WeightCalculationSnapshot? WeightCalculation { get; init; }
    public string SourceSnapshot { get; init; } = "用户录入";
    public required long EnergyKcalTenths { get; init; }
    public string Note { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public decimal EnergyKcal => EnergyKcalTenths / 10m;
    [JsonIgnore] public string Calculation => InputMode == "weight"
        ? (WeightCalculation is null ? "" : WeightCalculation.Description + "；") + $"{WeightGramsTenths / 10m:0.0} g × {KcalPer100gHundredthsSnapshot / 100m:0.00} kcal/100g" : "直接输入大卡";
}

public sealed record JournalSnapshot
{
    public required int FormatVersion { get; init; }
    public required int SchemaVersion { get; init; }
    public required string AppVersion { get; init; }
    public required string FoodPackageVersion { get; init; }
    public required DateTimeOffset ExportedAtUtc { get; init; }
    public required FoodItem[] Foods { get; init; }
    public required MealCategory[] Meals { get; init; }
    public required IntakeEntry[] Entries { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FoodPortion[]? Portions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MealTemplate[]? Templates { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemplateBatchReceipt[]? TemplateBatches { get; init; }
}

public sealed record FoodPackage
{
    public required string Version { get; init; }
    public required FoodItem[] Foods { get; init; }
}

public sealed record DailySummary(DateOnly Date, long EnergyKcalTenths, int Count)
{
    public decimal EnergyKcal => EnergyKcalTenths / 10m;
    public string State => Count == 0 ? "未记录" : $"已记录 {Count} 项";
}

public sealed record DailyIntake(DateOnly Date, IntakeEntry[] Entries)
{
    public long EnergyKcalTenths => Entries.Sum(x => x.EnergyKcalTenths);
    public string Summary => Entries.Length == 0 ? "未记录" : $"{EnergyKcalTenths / 10m:0.0} 大卡 · {Entries.Length} 项";
}
