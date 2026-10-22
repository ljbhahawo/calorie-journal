using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class ImportRowViewModel : ObservableObject
{
    public ImportDraft Original { get; }
    public string Kind => Original.Kind switch { ImportRowKind.MealTotal => "餐次合计", ImportRowKind.Unrecognized => "待核对片段", _ => "明细" };
    public string[] MealOptions { get; }
    [ObservableProperty] private bool included;
    [ObservableProperty] private string dateText;
    [ObservableProperty] private string mealName;
    [ObservableProperty] private string name;
    [ObservableProperty] private string energyText;
    [ObservableProperty] private string localTime;
    [ObservableProperty] private string note;
    [ObservableProperty] private string state;

    public ImportRowViewModel(ImportDraft original, string[] meals)
    {
        Original = original; dateText = original.DateText; mealName = original.MealName; name = original.Name;
        energyText = original.EnergyText; localTime = original.LocalTime; note = original.Note; state = original.Hint;
        MealOptions = meals.Append(original.MealName).Distinct().ToArray();
    }
    public ImportCandidate Candidate() => new(Original, Included, DateText, MealName, Name, EnergyText, LocalTime, Note);
}

public sealed partial class ImportViewModel : ObservableObject
{
    private readonly JournalService journal;
    private readonly BackupService backups;
    private ImportDocument? document;
    private bool updating;
    private bool validated;
    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];
    public string[] Formats { get; } = ["自由文字", "表格（制表符）", "CSV（逗号）"];
    [ObservableProperty] private string rawText = "";
    [ObservableProperty] private string year = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string format = "自由文字";
    [ObservableProperty] private string status = "粘贴记录后生成草稿；此时不会写入任何摄入。";
    [ObservableProperty] private int tabIndex;
    [ObservableProperty] private ImportRowViewModel? selectedRow;
    [ObservableProperty] private bool reviewConfirmed;
    [ObservableProperty] private bool allowDuplicates;
    [ObservableProperty] private bool isBusy;
    public bool NotBusy => !IsBusy;
    public bool CanEdit => !IsBusy && !Imported;
    public bool CanCommit => validated && ReviewConfirmed && NotBusy && !Imported;
    public bool Imported { get; private set; }
    public string RecoveryPath { get; private set; } = "";
    public string SelectedOriginal => SelectedRow?.Original.Raw ?? "选择一行查看原文；可左右滚动编辑所有字段。";
    public string SelectedHint => SelectedRow?.Original.Hint ?? "";
    public string SelectionSummary => $"共{Rows.Count}行，选择{Rows.Count(x => x.Included)}行；默认采用明细，有合计的行保留作参考。";
    public IRelayCommand ParseCommand { get; }
    public IRelayCommand ValidateCommand { get; }
    public IRelayCommand PreferDetailsCommand { get; }
    public IRelayCommand PreferTotalsCommand { get; }
    public IRelayCommand LowerCommand { get; }
    public IRelayCommand MiddleCommand { get; }
    public IRelayCommand UpperCommand { get; }
    public IAsyncRelayCommand CommitCommand { get; }

    public ImportViewModel(JournalService journal, BackupService backups)
    {
        this.journal = journal; this.backups = backups;
        ParseCommand = new RelayCommand(() => Run(Parse));
        ValidateCommand = new RelayCommand(() => Run(() => Validate()));
        PreferDetailsCommand = new RelayCommand(() => SelectMode(false));
        PreferTotalsCommand = new RelayCommand(() => SelectMode(true));
        LowerCommand = new RelayCommand(() => Run(() => Resolve("下限")));
        MiddleCommand = new RelayCommand(() => Run(() => Resolve("中值")));
        UpperCommand = new RelayCommand(() => Run(() => Resolve("上限")));
        CommitCommand = new AsyncRelayCommand(CommitAsync);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(RawText) or nameof(Year) or nameof(Format))
        {
            document = null; validated = false; ReviewConfirmed = false;
            if (Rows.Count > 0) Status = "原文或解析设置已改变，请重新生成草稿。";
        }
        if (e.PropertyName == nameof(SelectedRow)) { OnPropertyChanged(nameof(SelectedOriginal)); OnPropertyChanged(nameof(SelectedHint)); }
        if (e.PropertyName == nameof(AllowDuplicates)) { validated = false; ReviewConfirmed = false; }
        if (e.PropertyName == nameof(IsBusy)) { OnPropertyChanged(nameof(NotBusy)); OnPropertyChanged(nameof(CanEdit)); }
        if (e.PropertyName is nameof(ReviewConfirmed) or nameof(IsBusy) or nameof(AllowDuplicates)) OnPropertyChanged(nameof(CanCommit));
    }

    private void Parse()
    {
        Validation.Require(int.TryParse(Year, out var number), "缺省年份须为整数。" );
        var parsed = IntakeTextParser.Parse(RawText, number, Format);
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows.Clear();
        document = parsed; validated = false; ReviewConfirmed = false;
        var meals = journal.Meals().Where(x => x.IsActive).Select(x => x.Name).ToArray();
        var existing = Existing();
        var ids = existing.Select(x => x.Id).ToHashSet();
        foreach (var row in parsed.Rows)
        {
            var value = new ImportRowViewModel(row, meals) { Included = !ids.Contains(row.Id) };
            if (ids.Contains(row.Id)) value.State = "该原文行已导入，默认排除；再次选择会被拒绝。";
            Rows.Add(value); value.PropertyChanged += RowChanged;
        }
        SelectMode(false); TabIndex = 1; SelectedRow = Rows.FirstOrDefault();
        Status = "草稿已生成。餐次合计仅作参考；区间、下限、未知餐次和缺失热量须补齐或取消选择。";
        OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanCommit));
    }

    private IntakeEntry[] Existing() => journal.ExistingImportEntries(DateOnly.MinValue, DateOnly.MaxValue);

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (updating || e.PropertyName == nameof(ImportRowViewModel.State)) return;
        validated = false; ReviewConfirmed = false;
        OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanCommit));
        Status = "草稿已修改，请重新校验并确认核对。";
    }

    private void SelectMode(bool totals)
    {
        if (document is null) return;
        updating = true;
        var ids = Existing().Select(x => x.Id).ToHashSet();
        foreach (var group in Rows.GroupBy(x => (x.DateText, x.MealName)))
        {
            var useTotal = totals && group.Any(x => x.Original.Kind == ImportRowKind.MealTotal);
            var hasDetails = group.Any(x => x.Original.Kind != ImportRowKind.MealTotal);
            foreach (var row in group)
                row.Included = !ids.Contains(row.Original.Id) && (useTotal || !hasDetails
                    ? row.Original.Kind == ImportRowKind.MealTotal : row.Original.Kind != ImportRowKind.MealTotal);
        }
        updating = false; validated = false; ReviewConfirmed = false;
        OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanCommit));
    }

    private void Resolve(string mode)
    {
        var row = SelectedRow;
        if (row?.Original.Lower is not decimal lower) { Status = "请先选中有热量范围的行。"; return; }
        if (mode != "下限" && row.Original.Upper is null) { Status = "这条原文只有下限，无法得到中值或上限，请手动填写估计值。"; return; }
        if (row.Original.Upper < lower) { Status = "原区间上下限相反，请手动核对。"; return; }
        var value = mode switch { "中值" => (lower + row.Original.Upper!.Value) / 2, "上限" => row.Original.Upper!.Value, _ => lower };
        row.EnergyText = decimal.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
        row.Note = Validation.Text(row.Note + $" 依据原文选择{mode}估计值。", "备注", 1000);
    }

    private ImportReview Validate()
    {
        validated = false;
        Validation.Require(document is not null, "请先生成草稿；原文改变后须重新识别。" );
        var review = ImportPlanner.Review(Rows.Select(x => x.Candidate()).ToArray(), document.Fingerprint, journal.Meals(), Existing());
        updating = true;
        foreach (var row in Rows)
        {
            var issue = review.Issues.SingleOrDefault(x => x.Id == row.Original.Id);
            row.State = !row.Included ? "未选择 · " + row.Original.Hint : issue?.Error.Length > 0 ? issue.Error
                : issue?.Duplicate == true ? "疑似重复，须明确允许或取消选择" : "可导入 · " + row.Original.Hint;
        }
        updating = false;
        validated = !review.HasErrors && (!review.HasDuplicates || AllowDuplicates);
        Status = review.HasErrors ? "校验未通过，请处理状态列中的问题。" : review.HasDuplicates && !AllowDuplicates
            ? "存在疑似重复项，默认不导入；请取消重复行或明确允许后重新校验。"
            : $"校验通过：将追加{review.Entries.Length}项，共{review.Entries.Sum(x => x.EnergyKcalTenths) / 10m:0.0}大卡。请核对后勾选确认。";
        OnPropertyChanged(nameof(CanCommit));
        return review;
    }

    private async Task CommitAsync()
    {
        if (IsBusy || Imported) return;
        try
        {
            Validation.Require(ReviewConfirmed && validated, "请先校验并勾选已核对所选记录。" );
            var review = Validate();
            Validation.Require(validated, "草稿尚有问题，未写入数据库。" );
            journal.ValidateImport(review.Entries);
            IsBusy = true; Status = "正在备份并导入，请稍候…";
            var backup = await Task.Run(() =>
            {
                var path = backups.CreateImportBackup();
                journal.ImportEntries(review.Entries);
                return path;
            });
            RecoveryPath = backup; Imported = true; validated = false;
            Status = $"已导入{review.Entries.Length}项。导入前完整备份：{backup}。关闭窗口即可查看。";
        }
        catch (Exception ex) when (Expected(ex)) { Status = "未完成导入：" + ex.Message; validated = false; ReviewConfirmed = false; }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanCommit)); }
    }

    private static bool Expected(Exception ex) => ex is ValidationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException;
    private void Run(Action action)
    {
        if (IsBusy || Imported) return;
        try { action(); }
        catch (Exception ex) when (Expected(ex)) { Status = "操作未完成：" + ex.Message; validated = false; ReviewConfirmed = false; OnPropertyChanged(nameof(CanCommit)); }
    }
}
