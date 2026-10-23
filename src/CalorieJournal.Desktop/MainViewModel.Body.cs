using System.Collections.ObjectModel;
using System.Globalization;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel
{
    private bool bodyDirty;
    private bool bodyLoading;
    private BodyMeasurement? editingBody;
    private Guid newBodyId = Guid.NewGuid();
    private DateOnly bodyAppliedFrom = DateOnly.FromDateTime(DateTime.Today.AddDays(-89));
    private DateOnly bodyAppliedTo = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private DateTime bodyDate = DateTime.Today;
    [ObservableProperty] private string bodyWeight = "";
    [ObservableProperty] private string bodyHeight = "";
    [ObservableProperty] private string bodyWaist = "";
    [ObservableProperty] private string bodyAge = "";
    [ObservableProperty] private int bodySex;
    [ObservableProperty] private string bodyNote = "";
    [ObservableProperty] private DateTime bodyFrom = DateTime.Today.AddDays(-89);
    [ObservableProperty] private DateTime bodyTo = DateTime.Today;
    [ObservableProperty] private BodyMeasurement? selectedBody;
    public string[] BodySexChoices { get; } = ["未选择 · 只记录体重/BMI", "男公式", "女公式"];
    public ObservableCollection<BodyMeasurement> BodyRows { get; } = [];
    public string BodyHeading => editingBody is null ? "添加身体记录" : "修改身体记录";
    public string BodySummary => BodyRows.Count == 0 ? "当前区间未测量" : $"{bodyAppliedFrom:yyyy-MM-dd} 至 {bodyAppliedTo:yyyy-MM-dd} · {BodyRows.Count} 次实际测量\n最新 {BodyRows[0].Date:yyyy-MM-dd}：{BodyRows[0].WeightKg:0.00} kg · BMI {BodyRows[0].BmiText} · 静息 {BodyRows[0].RestingText}\n首末测量变化 {(BodyRows[0].WeightKg - BodyRows[^1].WeightKg):+0.00;-0.00;0.00} kg（未记录日期不填零）";
    public string BodyPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BodyWeight)) return "填写体重后显示计算；身高、年龄和公式选项可选。";
            try { var value = BuildBody(); return $"BMI：{value.BmiText}\n基础代谢参考（静息估算）：{value.RestingText}\n{value.RestingReason}"; }
            catch (ValidationException ex) { return ex.Message; }
        }
    }
    public IRelayCommand SaveBodyCommand { get; private set; } = null!;
    public IRelayCommand NewBodyCommand { get; private set; } = null!;
    public IRelayCommand EditBodyCommand { get; private set; } = null!;
    public IRelayCommand DeleteBodyCommand { get; private set; } = null!;
    public IRelayCommand QueryBodyCommand { get; private set; } = null!;

    private void InitializeBody()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BodyDate) or nameof(BodyWeight) or nameof(BodyHeight) or nameof(BodyWaist) or nameof(BodyAge) or nameof(BodySex) or nameof(BodyNote))
            { if (!bodyLoading) bodyDirty = true; OnPropertyChanged(nameof(BodyPreview)); }
        };
        SaveBodyCommand = new RelayCommand(() => Run(() =>
        {
            var value = BuildBody(); journal.SaveBodyMeasurement(value); bodyDirty = false; LoadBody(null); RefreshBody();
            Status = value.Date < bodyAppliedFrom || value.Date > bodyAppliedTo ? "身体记录已保存，日期在当前查询区间外；请调整区间查看。" : "身体记录已保存；摄入合计保持独立。";
        }));
        NewBodyCommand = new RelayCommand(() => Run(() => { if (ConfirmBodyDiscard()) LoadBody(null); }));
        EditBodyCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedBody is not null, "请先选择身体记录。"); if (ConfirmBodyDiscard()) LoadBody(SelectedBody);
        }));
        DeleteBodyCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedBody is not null, "请先选择身体记录。"); var value = SelectedBody;
            if (!ConfirmBodyDiscard() || !Confirm($"删除{value.Date:yyyy-MM-dd}的{value.WeightKg:0.00}kg身体记录？")) return;
            journal.DeleteBodyMeasurement(value.Id); LoadBody(null); RefreshBody(); Status = "身体记录已删除，摄入记录保持不变。";
        }));
        QueryBodyCommand = new RelayCommand(() => Run(() =>
        {
            var from = DateOnly.FromDateTime(BodyFrom); var to = DateOnly.FromDateTime(BodyTo);
            var values = journal.BodyMeasurements(from, to); bodyAppliedFrom = from; bodyAppliedTo = to; Fill(BodyRows, values); OnPropertyChanged(nameof(BodySummary));
        }));
        LoadBody(null); RefreshBody();
    }
    private bool ConfirmBodyDiscard() => !bodyDirty || Confirm("身体记录内容尚未保存，是否放弃修改？");
    private static long ParseBodyNumber(string text, string label, int scale, decimal maximum)
    {
        Validation.Require(decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= maximum, $"{label}请输入有效正数，使用小数点。");
        var scaled = value * scale; Validation.Require(decimal.Truncate(scaled) == scaled, $"{label}最多{(scale == 100 ? "两" : "一")}位小数。"); return (long)scaled;
    }
    private BodyMeasurement BuildBody()
    {
        int? age = null;
        if (!string.IsNullOrWhiteSpace(BodyAge)) { Validation.Require(int.TryParse(BodyAge.Trim(), out var parsed), "年龄请输入整数周岁。"); age = parsed; }
        Validation.Require(BodySex is >= 0 and <= 2, "请选择有效公式选项。");
        var value = new BodyMeasurement { Id = editingBody?.Id ?? newBodyId, Date = DateOnly.FromDateTime(BodyDate),
            WeightKgHundredths = ParseBodyNumber(BodyWeight, "体重", 100, 1000),
            HeightCmTenths = string.IsNullOrWhiteSpace(BodyHeight) ? null : ParseBodyNumber(BodyHeight, "身高", 10, 300),
            WaistCmTenths = string.IsNullOrWhiteSpace(BodyWaist) ? null : ParseBodyNumber(BodyWaist, "腰围", 10, 300),
            AgeYears = age, FormulaSex = BodySexChoices.Length > BodySex ? new[] { "unspecified", "male", "female" }[BodySex] : "invalid",
            Note = BodyNote, CreatedAtUtc = editingBody?.CreatedAtUtc ?? DateTimeOffset.UtcNow };
        BodyCalculator.Validate(value); return value;
    }
    private void LoadBody(BodyMeasurement? value)
    {
        var latest = value ?? journal.BodyMeasurements(DateOnly.MinValue, DateOnly.FromDateTime(DateTime.Today)).FirstOrDefault();
        bodyLoading = true;
        try
        {
            editingBody = value; newBodyId = Guid.NewGuid(); BodyDate = value?.Date.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
            BodyWeight = value?.WeightKg.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
            BodyHeight = latest?.HeightCm?.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
            BodyWaist = value?.WaistCm?.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
            BodyAge = latest?.AgeYears?.ToString(CultureInfo.InvariantCulture) ?? "";
            BodySex = latest?.FormulaSex switch { "male" => 1, "female" => 2, _ => 0 }; BodyNote = value?.Note ?? "";
        }
        finally { bodyLoading = false; }
        bodyDirty = false; OnPropertyChanged(nameof(BodyHeading)); OnPropertyChanged(nameof(BodyPreview));
    }
    private void RefreshBody() { Fill(BodyRows, journal.BodyMeasurements(bodyAppliedFrom, bodyAppliedTo)); OnPropertyChanged(nameof(BodySummary)); }
}
