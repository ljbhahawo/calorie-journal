namespace CalorieJournal.Core;

public static class Nutrition
{
    public const decimal KilojoulesPerKcal = 4.184m;
    public static long Kilojoules(decimal value, decimal maximumKcal) => Validation.Scaled(value, 100, 0, maximumKcal * KilojoulesPerKcal, "千焦kJ");
    public static long UnitKcal(long kilojoulesHundredths)
    {
        Validation.Require(kilojoulesHundredths is >= 0 and <= 4184000, "每100克千焦超出0–41840kJ范围。");
        return Round(kilojoulesHundredths / KilojoulesPerKcal);
    }
    public static long DirectKcal(long kilojoulesHundredths)
    {
        Validation.Require(kilojoulesHundredths >= 0 && kilojoulesHundredths / 100m <= 99999.9m * KilojoulesPerKcal, "本项千焦超出范围。");
        return Round(kilojoulesHundredths / (10m * KilojoulesPerKcal));
    }
    public static long Energy(long gramsTenths, long kcalHundredths, long? kilojoulesHundredths = null)
    {
        var kcalResult = Validation.CalculateEnergy(gramsTenths, kcalHundredths);
        if (kilojoulesHundredths is null) return kcalResult;
        Validation.Require(UnitKcal(kilojoulesHundredths.Value) == kcalHundredths, "千焦原值与每100克大卡不一致。");
        return Round(gramsTenths * (decimal)kilojoulesHundredths.Value / (10000m * KilojoulesPerKcal));
    }
    public static long? Protein(long gramsTenths, long? proteinHundredths)
    {
        UnitProtein(proteinHundredths);
        Validation.Require(gramsTenths is >= 1 and <= 1000000, "蛋白质计算需要有效可食重量。");
        return proteinHundredths is { } value ? Round(gramsTenths * (decimal)value / 1000m) : null;
    }
    public static void UnitProtein(long? value) => Validation.Require(value is null or >= 0 and <= 10000, "每100克蛋白质须为0–100g，或留空。");
    public static long Round(decimal value) => checked((long)decimal.Round(value, 0, MidpointRounding.AwayFromZero));
    public static string ProteinText(long? value) => value is { } grams ? $"{grams / 100m:0.00} g" : "未填写";
    public static string ProteinSummary(IEnumerable<IntakeEntry> entries)
    {
        var values = entries.ToArray(); if (values.Length == 0) return "蛋白质未记录";
        var known = values.Where(x => x.ProteinGramsHundredths is not null).ToArray(); var missing = values.Length - known.Length;
        return known.Length == 0 ? $"蛋白质未填写 · {missing}项" : $"已记录蛋白质 {known.Sum(x => x.ProteinGramsHundredths!.Value) / 100m:0.00} g" + (missing > 0 ? $" · {missing}项未填写（未计入）" : "");
    }
    public static void Snapshot(JournalSnapshot value)
    {
        if (value.FormatVersion >= 7) return;
        Validation.Require(value.Foods.All(x => x is not null && x.ProteinPer100gHundredths is null && x.KilojoulesPer100gHundredths is null)
            && value.Entries.All(x => x is not null && x.ProteinGramsHundredths is null && x.ProteinPer100gHundredthsSnapshot is null && x.KilojoulesHundredthsSnapshot is null)
            && (value.Templates ?? []).All(x => x is not null && x.Items is not null && x.Items.All(i => i is not null && i.ProteinPer100gHundredthsSnapshot is null && i.KilojoulesPer100gHundredthsSnapshot is null)), "旧格式不能包含蛋白质或千焦原值字段。");
    }
}
