using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace CalorieJournal.Core;

public sealed class ValidationException(string message) : Exception(message);

public static class Validation
{
    public static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new ValidationException(message);
    }

    public static string Text(string? value, string label, int maximum, bool required = false)
    {
        Require(value is not null, $"{label}不能为null。");
        var result = value!.Trim();
        Require(!required || result.Length > 0, $"请填写{label}。");
        Require(result.Length <= maximum, $"{label}最多{maximum}字。");
        return result;
    }

    public static long Scaled(decimal value, int scale, decimal min, decimal max, string label)
    {
        Require(value >= min && value <= max, $"{label}须在{min}到{max}之间。");
        Require(decimal.Truncate(value * scale) == value * scale, $"{label}的小数位过多。");
        return checked((long)(value * scale));
    }

    public static decimal Number(string text, string label)
    {
        Require(decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value), $"{label}须为有效数字，小数点使用英文句点。");
        return value;
    }

    public static long CalculateEnergy(long gramsTenths, long kcalHundredths)
    {
        Require(gramsTenths > 0 && gramsTenths <= 1_000_000, "重量须大于0且不超过100000克。");
        Require(kcalHundredths >= 0 && kcalHundredths <= 1_000_000, "每100克热量超出范围。");
        // 克×10与每100克大卡×100相乘后，除10000得到大卡×10。
        return checked((long)decimal.Round(gramsTenths * (decimal)kcalHundredths / 10_000m, 0, MidpointRounding.AwayFromZero));
    }

    public static void Food(FoodItem value)
    {
        Require(value.Id != Guid.Empty, "食物ID无效。");
        Text(value.Name, "食物名称", 100, true);
        Text(value.OriginalName, "原始名称", 500); Text(value.Aliases, "别名", 500);
        Text(value.Preparation, "食物状态", 200); Text(value.Category, "食物分类", 100);
        Text(value.SourceKind, "来源类型", 100, true); Text(value.SourceId, "来源编号", 100);
        Text(value.SourceVersion, "来源版本", 100); Text(value.SourceUrl, "来源网址", 2000);
        Text(value.SourceNote, "来源说明", 1000); Text(value.BuiltinKey, "内置键", 100);
        Require(value.KcalPer100gHundredths is >= 0 and <= 1_000_000, "单位热量超出范围。");
        Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "食物时间无效。");
    }

    public static void Meal(MealCategory value)
    {
        Require(value.Id != Guid.Empty, "餐次ID无效。");
        Text(value.Name, "餐次名称", 30, true);
        Require(value.SortOrder is >= 0 and <= 10000, "餐次顺序须在0到10000之间。");
    }

    public static void Entry(IntakeEntry value, bool checkToday = true)
    {
        Require(value.Id != Guid.Empty && value.MealCategoryId != Guid.Empty, "摄入记录ID或餐次无效。");
        Require(value.EntryDate != default, "请选择有效日期。");
        if (checkToday) Require(value.EntryDate <= DateOnly.FromDateTime(DateTime.Today), "摄入日期不能晚于今天。");
        Text(value.MealNameSnapshot, "餐次名称", 30, true); Text(value.FoodNameSnapshot, "食物名称", 100, true);
        Text(value.PreparationSnapshot, "食物状态", 200); Text(value.SourceSnapshot, "来源", 3000);
        Text(value.Note, "备注", 1000); Text(value.LocalTime, "时间", 5);
        Require(value.LocalTime.Length == 0 || TimeOnly.TryParseExact(value.LocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "时间格式须为HH:mm或留空。");
        Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "记录时间无效。");
        if (value.InputMode == "weight")
        {
            Require(value.WeightGramsTenths.HasValue && value.KcalPer100gHundredthsSnapshot.HasValue, "按重量计算需要重量和单位热量。");
            Require(value.EnergyKcalTenths == CalculateEnergy(value.WeightGramsTenths!.Value, value.KcalPer100gHundredthsSnapshot!.Value), "保存的热量与重量计算不一致。");
            if (value.WeightCalculation is not null)
                Require(value.WeightGramsTenths == WeightCalculator.Calculate(value.WeightCalculation), "可食重量与换算依据不一致。");
        }
        else
        {
            Require(value.InputMode == "direct", "未知录入方式。");
            Require(value.WeightGramsTenths is null && value.KcalPer100gHundredthsSnapshot is null && value.WeightCalculation is null, "直接录入不能包含重量计算字段。");
            Require(value.EnergyKcalTenths is >= 0 and <= 999_999, "单项大卡须在0到99999.9之间。");
        }
    }

    public static void Snapshot(JournalSnapshot value)
    {
        Require(value.FormatVersion == value.SchemaVersion && value.FormatVersion is 1 or 2 or 3 or 4 or 5 or 6, "备份格式或数据库版本不兼容。");
        PersonalRecordValidation.Snapshot(value);
        BodyCalculator.Snapshot(value);
        if (value.FormatVersion >= 4) { Require(value.Settings is not null, "备份缺少录入设置。"); JournalSettings.Validate(value.Settings); }
        else Require(value.Settings is null, "旧格式不能包含录入设置。");
        Text(value.AppVersion, "软件版本", 100, true); Text(value.FoodPackageVersion, "食物包版本", 100, true);
        Require(value.ExportedAtUtc != default, "备份时间无效。");
        Require(value.Foods is not null && value.Meals is not null && value.Entries is not null, "备份缺少必要数据。");
        Require(value.Foods!.Length <= 100000 && value.Meals!.Length <= 1000 && value.Entries!.Length <= 100000, "备份条目超出限制。");
        Require(value.Foods.All(x => x is not null) && value.Meals.All(x => x is not null) && value.Entries.All(x => x is not null), "备份中存在空条目。");
        foreach (var food in value.Foods) Food(food);
        foreach (var meal in value.Meals) Meal(meal);
        foreach (var entry in value.Entries) Entry(entry, false);
        Require(value.Foods.Select(x => x.Id).Distinct().Count() == value.Foods.Length, "食物ID重复。");
        Require(value.Meals.Select(x => x.Id).Distinct().Count() == value.Meals.Length, "餐次ID重复。");
        Require(value.Entries.Select(x => x.Id).Distinct().Count() == value.Entries.Length, "摄入ID重复。");
        Require(value.Meals.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.Meals.Length, "餐次名称重复。");
        var keys = value.Foods.Where(x => x.BuiltinKey.Length > 0).Select(x => x.BuiltinKey).ToArray();
        Require(keys.Distinct().Count() == keys.Length, "内置食物键重复。");
        Require(value.Meals.Any(x => x.IsActive), "至少需要一个启用餐次。");
        var foods = value.Foods.Select(x => x.Id).ToHashSet();
        var meals = value.Meals.Select(x => x.Id).ToHashSet();
        Require(value.Entries.All(x => meals.Contains(x.MealCategoryId) && (x.FoodId is null || foods.Contains(x.FoodId.Value))), "备份中存在无效食物或餐次引用。");
        if (value.FormatVersion == 1)
            Require((value.Portions is null || value.Portions.Length == 0) && value.Entries.All(x => x.WeightCalculation is null), "旧格式备份不能包含新份量或换算字段。");
        else
        {
            Require(value.Portions is not null && value.Portions.Length <= 100000 && value.Portions.All(x => x is not null), "备份缺少份量数据或条目无效。");
            foreach (var portion in value.Portions) WeightCalculator.Portion(portion);
            Require(value.Portions.Select(x => x.Id).Distinct().Count() == value.Portions.Length, "份量ID重复。");
            Require(value.Portions.All(x => foods.Contains(x.FoodId)), "份量引用食物不存在。");
            Require(value.Portions.Where(x => !x.IsDeleted).Select(x => (x.FoodId, Name: x.Name.Trim().ToUpperInvariant())).Distinct().Count() == value.Portions.Count(x => !x.IsDeleted), "同一食物的份量名称重复。");
        }
        TemplateValidation.Snapshot(value);
    }
}
