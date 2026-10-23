using System.Text;

namespace CalorieJournal.Core;

public sealed record JournalSettings
{
    public required bool DefaultWeightMode { get; init; }
    public required string NewFoodMode { get; init; }
    public static JournalSettings Default => new() { DefaultWeightMode = true, NewFoodMode = "ask" };
    public static void Validate(JournalSettings value) => Validation.Require(value.NewFoodMode is "ask" or "auto" or "never", "新食物入库方式无效。");
}

public sealed record DailyMealGroup(DateOnly Date, Guid MealId, string Name, IntakeEntry[] Entries)
{
    public string ProteinSummary => Nutrition.ProteinSummary(Entries);
    public string Summary => Entries.Length == 0 ? "未记录" : $"{Entries.Sum(x => x.EnergyKcalTenths) / 10m:0.0} 大卡 · {Entries.Length} 项";
}

public sealed partial class JournalService
{
    public JournalSettings Settings() => store.ReadSettings();
    public void SaveSettings(JournalSettings value) { JournalSettings.Validate(value); store.SaveSettings(value); }
    public DailyMealGroup[] MealsForDay(DateOnly date)
    {
        var entries = Entries(date);
        return Meals().Where(x => x.IsActive || entries.Any(e => e.MealCategoryId == x.Id)).Select(meal =>
        {
            var rows = entries.Where(x => x.MealCategoryId == meal.Id).ToArray();
            var names = rows.Select(x => x.MealNameSnapshot).Distinct().ToArray();
            var name = string.Join(" / ", new[] { meal.Name }.Concat(names).Distinct());
            return new DailyMealGroup(date, meal.Id, name + (meal.IsActive ? "" : "（已停用）"), rows);
        }).ToArray();
    }
    public static bool SameFoodData(FoodItem food, IntakeEntry entry) =>
        food.Name.Trim().Normalize(NormalizationForm.FormKC).Equals(entry.FoodNameSnapshot.Trim().Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase)
        && food.Preparation.Trim().Normalize(NormalizationForm.FormKC).Equals(entry.PreparationSnapshot.Trim().Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase)
        && food.KcalPer100gHundredths == entry.KcalPer100gHundredthsSnapshot
        && food.ProteinPer100gHundredths == entry.ProteinPer100gHundredthsSnapshot
        && food.KilojoulesPer100gHundredths == entry.KilojoulesHundredthsSnapshot;

    public FoodItem? UnlistedFood(IntakeEntry entry)
    {
        _ = ValidateEntrySave(entry);
        if (entry.FoodId is not null || entry.InputMode != "weight" || store.ReadFoods().Any(x => SameFoodData(x, entry))) return null;
        var food = new FoodItem { Id = Guid.NewGuid(), Name = entry.FoodNameSnapshot.Trim(), Preparation = entry.PreparationSnapshot.Trim(),
            KcalPer100gHundredths = entry.KcalPer100gHundredthsSnapshot!.Value, ProteinPer100gHundredths = entry.ProteinPer100gHundredthsSnapshot,
            KilojoulesPer100gHundredths = entry.KilojoulesHundredthsSnapshot, UserModified = true, SourceKind = "用户录入",
            SourceNote = entry.SourceSnapshot.Length <= 1000 ? entry.SourceSnapshot : entry.SourceSnapshot[..960] + "…（完整来源保留在本次摄入快照中）" };
        Validation.Food(food); return food;
    }

    public bool SaveManualEntry(IntakeEntry entry, bool addFood)
    {
        var value = ValidateEntrySave(entry);
        var food = addFood ? UnlistedFood(value) : null;
        if (food is null) { store.SaveEntry(value); return false; }
        return store.SaveEntryAndFood(value, food);
    }
}
