using System.Collections.ObjectModel;
using System.Globalization;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel
{
    private bool goalDirty, exerciseDirty, personalLoading;
    private IntakeGoal? editingGoal;
    private ExerciseRecord? editingExercise;
    private Guid newGoalId = Guid.NewGuid(), newExerciseId = Guid.NewGuid();
    private DateOnly goalAppliedCompareDate = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly exerciseAppliedFrom = DateOnly.FromDateTime(DateTime.Today.AddDays(-29));
    private DateOnly exerciseAppliedTo = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private DateTime goalDate = DateTime.Today;
    [ObservableProperty] private string goalEnergy = "";
    [ObservableProperty] private bool goalPaused;
    [ObservableProperty] private string goalNote = "";
    [ObservableProperty] private GoalPeriod? selectedGoal;
    [ObservableProperty] private DateTime goalCompareDate = DateTime.Today;
    [ObservableProperty] private string goalComparisonText = "";
    [ObservableProperty] private string dayGoalText = "";
    [ObservableProperty] private DateTime exerciseDate = DateTime.Today;
    [ObservableProperty] private string exerciseActivity = "";
    [ObservableProperty] private string exerciseDuration = "";
    [ObservableProperty] private string exerciseEnergy = "";
    [ObservableProperty] private string exerciseNote = "";
    [ObservableProperty] private DateTime exerciseFrom = DateTime.Today.AddDays(-29);
    [ObservableProperty] private DateTime exerciseTo = DateTime.Today;
    [ObservableProperty] private ExerciseRecord? selectedExercise;
    public ObservableCollection<GoalPeriod> GoalRows { get; } = [];
    public ObservableCollection<ExerciseRecord> ExerciseRows { get; } = [];
    public string GoalHeading => editingGoal is null ? "新增目标调整" : "修正已有目标";
    public string ExerciseHeading => editingExercise is null ? "添加锻炼记录" : "修改锻炼记录";
    public bool GoalEnergyEnabled => !GoalPaused;
    public string ExerciseSummary => ExerciseRows.Count == 0 ? "当前区间未记录锻炼" :
        $"{exerciseAppliedFrom:yyyy-MM-dd} 至 {exerciseAppliedTo:yyyy-MM-dd} · {ExerciseRows.Count} 次 · {ExerciseRows.Sum(x => x.DurationMinutesTenths) / 10m:0.0} 分钟\n" +
        (ExerciseRows.Any(x => x.EnergyKcalTenths is not null) ? $"已填写消耗合计 {ExerciseRows.Sum(x => x.EnergyKcalTenths ?? 0) / 10m:0.0} 大卡" : "消耗全部未填写") + $" · {ExerciseRows.Count(x => x.EnergyKcalTenths is null)} 次未填写消耗（未计入）";
    public IRelayCommand SaveGoalCommand { get; private set; } = null!;
    public IRelayCommand NewGoalCommand { get; private set; } = null!;
    public IRelayCommand EditGoalCommand { get; private set; } = null!;
    public IRelayCommand DeleteGoalCommand { get; private set; } = null!;
    public IRelayCommand CompareGoalCommand { get; private set; } = null!;
    public IRelayCommand SaveExerciseCommand { get; private set; } = null!;
    public IRelayCommand NewExerciseCommand { get; private set; } = null!;
    public IRelayCommand EditExerciseCommand { get; private set; } = null!;
    public IRelayCommand DeleteExerciseCommand { get; private set; } = null!;
    public IRelayCommand QueryExerciseCommand { get; private set; } = null!;

    private void InitializePersonal()
    {
        PropertyChanged += (_, e) =>
        {
            if (!personalLoading && e.PropertyName is nameof(GoalDate) or nameof(GoalEnergy) or nameof(GoalPaused) or nameof(GoalNote)) goalDirty = true;
            if (!personalLoading && e.PropertyName is nameof(ExerciseDate) or nameof(ExerciseActivity) or nameof(ExerciseDuration) or nameof(ExerciseEnergy) or nameof(ExerciseNote)) exerciseDirty = true;
            if (e.PropertyName == nameof(GoalPaused)) OnPropertyChanged(nameof(GoalEnergyEnabled));
        };
        SaveGoalCommand = new RelayCommand(() => Run(() =>
        {
            var value = new IntakeGoal { Id = editingGoal?.Id ?? newGoalId, EffectiveDate = DateOnly.FromDateTime(GoalDate),
                TargetKcalTenths = GoalPaused ? null : PersonalNumber(GoalEnergy, "每日摄入目标", 0.1m, 99999.9m), Note = GoalNote,
                CreatedAtUtc = editingGoal?.CreatedAtUtc ?? DateTimeOffset.UtcNow };
            PersonalRecordValidation.Goal(value);
            Validation.Require(!journal.Goals().Any(x => x.Goal.Id != value.Id && x.Goal.EffectiveDate == value.EffectiveDate), "该生效日期已有目标，请选择已有记录后修改。");
            var originalPeriod = editingGoal is null ? null : journal.Goals().Single(x => x.Goal.Id == editingGoal.Id);
            if ((editingGoal is not null || value.EffectiveDate < DateOnly.FromDateTime(DateTime.Today)) &&
                !Confirm($"{(originalPeriod is null ? "新增历史目标" : $"修正原区间：{originalPeriod.Goal.EffectiveDate:yyyy-MM-dd}起，结束：{originalPeriod.UntilText}")}。\n新生效日期{value.EffectiveDate:yyyy-MM-dd}，{value.TargetText}。\n这会改变受影响日期的目标比较；各条目标持续至下一条生效日前一天，摄入原值不变。是否保存？")) return;
            journal.SaveIntakeGoal(value); LoadGoal(null); RefreshPersonal(); Status = "目标调整已保存，历史按各自生效日期比较。";
        }));
        NewGoalCommand = new RelayCommand(() => Run(() => { if (ConfirmGoalDiscard()) LoadGoal(null); }));
        EditGoalCommand = new RelayCommand(() => Run(() =>
        { Validation.Require(SelectedGoal is not null, "请先选择目标记录。"); if (ConfirmGoalDiscard()) LoadGoal(SelectedGoal.Goal); }));
        DeleteGoalCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedGoal is not null, "请先选择目标记录。"); var period = SelectedGoal;
            if (!ConfirmGoalDiscard() || !Confirm($"删除{period.Goal.EffectiveDate:yyyy-MM-dd}至{period.UntilText}的目标调整（{period.Goal.TargetText}）？\n该区间将沿用更早目标，或变为未设置；摄入原值不变。")) return;
            journal.DeleteIntakeGoal(period.Goal.Id); LoadGoal(null); RefreshPersonal(); Status = "目标调整已删除。";
        }));
        CompareGoalCommand = new RelayCommand(() => Run(() =>
        { var date = DateOnly.FromDateTime(GoalCompareDate); var result = journal.CompareGoal(date); goalAppliedCompareDate = date; GoalComparisonText = result.Description; }));
        SaveExerciseCommand = new RelayCommand(() => Run(() =>
        {
            var value = new ExerciseRecord { Id = editingExercise?.Id ?? newExerciseId, Date = DateOnly.FromDateTime(ExerciseDate), Activity = ExerciseActivity,
                DurationMinutesTenths = PersonalNumber(ExerciseDuration, "时长分钟", 0.1m, 1440m),
                EnergyKcalTenths = string.IsNullOrWhiteSpace(ExerciseEnergy) ? null : PersonalNumber(ExerciseEnergy, "手动消耗大卡", 0m, 99999.9m),
                Note = ExerciseNote, CreatedAtUtc = editingExercise?.CreatedAtUtc ?? DateTimeOffset.UtcNow };
            journal.SaveExercise(value); LoadExercise(null); RefreshExercises();
            Status = value.Date < exerciseAppliedFrom || value.Date > exerciseAppliedTo ? "锻炼已保存，日期在当前查询区间外；请调整区间查看。摄入未抵扣。" : "锻炼已保存；消耗单独记录，摄入未抵扣。";
        }));
        NewExerciseCommand = new RelayCommand(() => Run(() => { if (ConfirmExerciseDiscard()) LoadExercise(null); }));
        EditExerciseCommand = new RelayCommand(() => Run(() =>
        { Validation.Require(SelectedExercise is not null, "请先选择锻炼记录。"); if (ConfirmExerciseDiscard()) LoadExercise(SelectedExercise); }));
        DeleteExerciseCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SelectedExercise is not null, "请先选择锻炼记录。"); var value = SelectedExercise;
            if (!ConfirmExerciseDiscard() || !Confirm($"删除{value.Date:yyyy-MM-dd}的{value.Activity}锻炼记录？")) return;
            journal.DeleteExercise(value.Id); LoadExercise(null); RefreshExercises(); Status = "锻炼已删除；摄入未变化。";
        }));
        QueryExerciseCommand = new RelayCommand(() => Run(() =>
        {
            var from = DateOnly.FromDateTime(ExerciseFrom); var to = DateOnly.FromDateTime(ExerciseTo); var rows = journal.Exercises(from, to);
            exerciseAppliedFrom = from; exerciseAppliedTo = to; Fill(ExerciseRows, rows); OnPropertyChanged(nameof(ExerciseSummary));
        }));
        LoadGoal(null); LoadExercise(null); RefreshPersonal();
    }
    private static long PersonalNumber(string text, string label, decimal minimum, decimal maximum) => Validation.Scaled(Validation.Number(text.Trim(), label), 10, minimum, maximum, label);
    private bool ConfirmGoalDiscard() => !goalDirty || Confirm("目标内容尚未保存，是否放弃修改？");
    private bool ConfirmExerciseDiscard() => !exerciseDirty || Confirm("锻炼内容尚未保存，是否放弃修改？");
    private void LoadGoal(IntakeGoal? value)
    {
        personalLoading = true;
        try { editingGoal = value; newGoalId = Guid.NewGuid(); GoalDate = value?.EffectiveDate.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
            GoalEnergy = value?.TargetKcalTenths is { } target ? (target / 10m).ToString("0.0", CultureInfo.InvariantCulture) : ""; GoalPaused = value is not null && value.TargetKcalTenths is null; GoalNote = value?.Note ?? ""; }
        finally { personalLoading = false; }
        goalDirty = false; OnPropertyChanged(nameof(GoalHeading));
    }
    private void LoadExercise(ExerciseRecord? value)
    {
        personalLoading = true;
        try { editingExercise = value; newExerciseId = Guid.NewGuid(); ExerciseDate = value?.Date.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
            ExerciseActivity = value?.Activity ?? ""; ExerciseDuration = value?.DurationMinutes.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
            ExerciseEnergy = value?.EnergyKcalTenths is { } energy ? (energy / 10m).ToString("0.0", CultureInfo.InvariantCulture) : ""; ExerciseNote = value?.Note ?? ""; }
        finally { personalLoading = false; }
        exerciseDirty = false; OnPropertyChanged(nameof(ExerciseHeading));
    }
    private void RefreshGoalComparison() => GoalComparisonText = journal.CompareGoal(goalAppliedCompareDate).Description;
    private void RefreshPersonal()
    {
        Fill(GoalRows, journal.Goals()); RefreshExercises();
        RefreshGoalComparison();
        DayGoalText = journal.CompareGoal(DateOnly.FromDateTime(SelectedDate)).Description;
    }
    private void RefreshExercises() { Fill(ExerciseRows, journal.Exercises(exerciseAppliedFrom, exerciseAppliedTo)); OnPropertyChanged(nameof(ExerciseSummary)); }
}
