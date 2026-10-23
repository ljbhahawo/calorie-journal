using System.Text.Json.Serialization;

namespace CalorieJournal.Core;

public sealed record BodyMeasurement
{
    public required Guid Id { get; init; }
    public required DateOnly Date { get; init; }
    public required long WeightKgHundredths { get; init; }
    public long? HeightCmTenths { get; init; }
    public long? WaistCmTenths { get; init; }
    public int? AgeYears { get; init; }
    public string FormulaSex { get; init; } = "unspecified";
    public string FormulaVersion { get; init; } = BodyCalculator.Version;
    public string Note { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public decimal WeightKg => WeightKgHundredths / 100m;
    [JsonIgnore] public decimal? HeightCm => HeightCmTenths / 10m;
    [JsonIgnore] public decimal? WaistCm => WaistCmTenths / 10m;
    [JsonIgnore] public decimal? Bmi => HeightCm is > 0 ? WeightKg / ((HeightCm.Value / 100m) * (HeightCm.Value / 100m)) : null;
    [JsonIgnore] public decimal? RestingKcal => BodyCalculator.Resting(this);
    [JsonIgnore] public string BmiText => Bmi is { } bmi ? decimal.Round(bmi, 2, MidpointRounding.AwayFromZero).ToString("0.00") : "缺少身高";
    [JsonIgnore] public string RestingText => RestingKcal is { } kcal ? $"{decimal.Round(kcal, 1, MidpointRounding.AwayFromZero):0.0} 大卡/日" : "未估算";
    [JsonIgnore] public string RestingReason => HeightCm is null ? "请填写身高" : AgeYears is null ? "请填写当时年龄" : FormulaSex == "unspecified" ? "请选择公式性别选项" : AgeYears is < 19 or > 78 ? "原研究年龄19–78岁，本记录未估算" : RestingKcal is null ? "输入对应非正结果，请核对资料" : "Mifflin–St Jeor 1990静息估算";
    [JsonIgnore] public string FormulaSexText => FormulaSex switch { "male" => "男公式", "female" => "女公式", _ => "未选择" };
}

public static class BodyCalculator
{
    public const string Version = "mifflin-1990-simple.v1";
    public static decimal? Resting(BodyMeasurement value)
    {
        if (value.HeightCm is null || value.AgeYears is null or < 19 or > 78 || value.FormulaSex is not ("male" or "female")) return null;
        var result = 10m * value.WeightKg + 6.25m * value.HeightCm.Value - 5m * value.AgeYears.Value + (value.FormulaSex == "male" ? 5m : -161m);
        return result > 0 ? result : null;
    }
    public static void Validate(BodyMeasurement value, bool checkToday = true)
    {
        Validation.Require(value.Id != Guid.Empty && value.Date != default && (!checkToday || value.Date <= DateOnly.FromDateTime(DateTime.Today)), "测量日期须有效且不能晚于今天。");
        Validation.Require(value.WeightKgHundredths is >= 100 and <= 100000, "体重须为1–1000kg，最多两位小数。");
        Validation.Require(value.HeightCmTenths is null or >= 500 and <= 3000, "身高须为50–300cm，最多一位小数。");
        Validation.Require(value.WaistCmTenths is null or >= 10 and <= 3000, "腰围须为1–300cm，最多一位小数。");
        Validation.Require(value.AgeYears is null or >= 1 and <= 120, "年龄须为1–120整数周岁，或留空。");
        Validation.Require(value.FormulaSex is "unspecified" or "male" or "female", "公式性别选项无效。");
        Validation.Require(value.FormulaVersion == Version, "身体记录公式版本不兼容。");
        Validation.Text(value.Note, "身体记录备注", 2000);
        Validation.Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "身体记录时间无效。");
    }
    public static void Snapshot(JournalSnapshot value)
    {
        if (value.FormatVersion < 5) { Validation.Require(value.BodyMeasurements is null, "旧格式不能包含身体记录。"); return; }
        Validation.Require(value.BodyMeasurements is not null && value.BodyMeasurements.Length <= 100000 && value.BodyMeasurements.All(x => x is not null), "身体记录缺失或条目无效。");
        foreach (var item in value.BodyMeasurements) Validate(item, false);
        Validation.Require(value.BodyMeasurements.Select(x => x.Id).Distinct().Count() == value.BodyMeasurements.Length && value.BodyMeasurements.Select(x => x.Date).Distinct().Count() == value.BodyMeasurements.Length, "身体记录ID或日期重复。");
    }
}

public sealed partial class JournalService
{
    public BodyMeasurement[] BodyMeasurements(DateOnly from, DateOnly to)
    {
        Validation.Require(to >= from && to <= DateOnly.FromDateTime(DateTime.Today), "身体记录查询区间无效或晚于今天。");
        return store.ReadBodyMeasurements().Where(x => x.Date >= from && x.Date <= to).OrderByDescending(x => x.Date).ToArray();
    }
    public void SaveBodyMeasurement(BodyMeasurement value)
    {
        value = value with { Note = Validation.Text(value.Note, "身体记录备注", 2000), UpdatedAtUtc = DateTimeOffset.UtcNow };
        BodyCalculator.Validate(value); store.SaveBodyMeasurement(value);
    }
    public void DeleteBodyMeasurement(Guid id) => store.DeleteBodyMeasurement(id);
}
