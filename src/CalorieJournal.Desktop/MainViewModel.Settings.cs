using System.Collections.ObjectModel;
using System.Windows;
using CalorieJournal.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalorieJournal.Desktop;

public sealed partial class MainViewModel
{
    private bool settingsDirty;
    public string[] NewFoodModes { get; } = ["每次询问", "自动加入", "不加入"];
    [ObservableProperty] private bool settingsDefaultWeight;
    [ObservableProperty] private int settingsFoodMode;
    [ObservableProperty] private bool mealMode;
    public ObservableCollection<DailyMealGroup> DayMeals { get; } = [];
    public IRelayCommand SaveSettingsCommand { get; private set; } = null!;
    public IRelayCommand ReloadSettingsCommand { get; private set; } = null!;
    public IRelayCommand ShowMealsCommand { get; private set; } = null!;
    public IRelayCommand<DailyMealGroup> AddMealEntryCommand { get; private set; } = null!;

    private void InitializeSettings()
    {
        SaveSettingsCommand = new RelayCommand(() => Run(() =>
        {
            Validation.Require(SettingsFoodMode is >= 0 and <= 2, "请选择新资料入库方式。");
            journal.SaveSettings(new JournalSettings { DefaultWeightMode = SettingsDefaultWeight, NewFoodMode = new[] { "ask", "auto", "never" }[SettingsFoodMode] });
            settingsDirty = false; Status = "录入设置已保存，后续新建采用新默认；当前草稿和历史保持不变。";
        }));
        ReloadSettingsCommand = new RelayCommand(() => Run(() => { LoadSettings(); Status = "已恢复上次保存的录入设置，当前摄入草稿保持不变。"; }));
        ShowMealsCommand = new RelayCommand(() => { ContinuousMode = false; MealMode = true; });
        AddMealEntryCommand = new RelayCommand<DailyMealGroup>(group => Run(() =>
        {
            Validation.Require(group is not null, "请选择餐次。");
            Validation.Require(journal.Meals().Any(x => x.Id == group.MealId && x.IsActive), "该餐次已停用，请在设置中启用或选择其他餐次。");
            if (!ConfirmEntryDiscard()) return;
            ResetEntry(); EntryMeal = EntryMeals.Single(x => x.Id == group.MealId);
            Status = "请在左侧填写本餐摄入，核对后保存。";
        }));
        LoadSettings();
    }
    private void LoadSettings()
    {
        var value = journal.Settings(); var wasLoading = loading; loading = true;
        SettingsDefaultWeight = value.DefaultWeightMode;
        SettingsFoodMode = value.NewFoodMode switch { "ask" => 0, "auto" => 1, "never" => 2, _ => throw new ValidationException("入库设置无效。") };
        loading = wasLoading; settingsDirty = false;
    }
    partial void OnSettingsDefaultWeightChanged(bool value) { if (!loading) settingsDirty = true; }
    partial void OnSettingsFoodModeChanged(int value) { if (!loading) settingsDirty = true; }

    private bool SaveWithFoodPolicy(IntakeEntry entry, out string message, out bool added)
    {
        Validation.Entry(entry); message = ""; added = false; var policy = journal.Settings().NewFoodMode;
        var candidate = policy == "never" ? null : journal.UnlistedFood(entry);
        var add = candidate is not null && policy == "auto";
        if (candidate is not null && policy == "ask")
        {
            var answer = MessageBox.Show($"检测到新的食物资料：{candidate.Name}\n状态：{candidate.Preparation}\n每100克 {candidate.KcalPer100g:0.00} 大卡，蛋白质 {candidate.ProteinText}。\n" + (candidate.KilojoulesPer100gHundredths is { } kj ? $"原始能量 {kj / 100m:0.00} kJ/100g。\n" : "") + "是否同时加入个人热量库？\n是：加入并保存摄入；否：仅保存摄入；取消：保留草稿。", "新食物资料", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return false;
            add = answer == MessageBoxResult.Yes;
        }
        added = journal.SaveManualEntry(entry, add);
        if (added) message = "新食物已加入热量库。";
        else if (entry.FoodId is null && entry.InputMode == "direct" && policy != "never") message = "仅有本项大卡，未生成每100克资料；需要加入库时请补充有效单位热量。";
        return true;
    }
}
