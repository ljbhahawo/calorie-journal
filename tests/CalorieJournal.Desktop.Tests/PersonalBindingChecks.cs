using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class PersonalBindingChecks
{
    public static void Verify()
    {
        var tests = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var root = Path.Combine(tests, "wpf_personal_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var preceding = new SqliteJournalStore(Path.Combine(Directory.GetDirectories(tests, "wpf_statistics_*").OrderByDescending(Directory.GetLastWriteTimeUtc).First(), "journal.db")).ReadSnapshot();
        var backups = new BackupService(store, Path.Combine(root, "recovery")); backups.Restore(preceding);
        var journal = new JournalService(store); var intakeBefore = JsonSerializer.Serialize(store.ReadSnapshot().Entries);
        var vm = new MainViewModel(journal, backups, root); var window = new MainWindow(vm); window.Show(); vm.TabIndex = 6; Pump();
        var personal = (TabControl)window.FindName("PersonalTabs");
        Text("GoalEnergyBox", "1800.0"); Click("SaveGoalButton"); Assert.HasCount(1, vm.GoalRows); Assert.IsTrue(vm.DayGoalText.Contains("1800.0"));
        Text("GoalEnergyBox", "1900"); Click("SaveGoalButton"); Assert.HasCount(1, vm.GoalRows); Assert.IsTrue(vm.Status.Contains("已有目标"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewGoalCommand.Execute(null), false)); Assert.AreEqual("1900", vm.GoalEnergy);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewGoalCommand.Execute(null), true));
        ((DatePicker)window.FindName("GoalDatePicker")).SelectedDate = DateTime.Today.AddDays(-2); Text("GoalEnergyBox", "1900");
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.SaveGoalCommand.Execute(null), true)); Assert.HasCount(2, vm.GoalRows);
        var goals = (DataGrid)window.FindName("GoalsGrid"); goals.SelectedItem = vm.GoalRows[1]; Click("EditGoalButton"); Text("GoalEnergyBox", "2000");
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.SaveGoalCommand.Execute(null), false)); Assert.AreEqual(19000L, store.ReadIntakeGoals()[1].TargetKcalTenths);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.SaveGoalCommand.Execute(null), true)); Assert.AreEqual(20000L, store.ReadIntakeGoals()[1].TargetKcalTenths); Assert.IsTrue(vm.DayGoalText.Contains("1800.0"));
        ((DatePicker)window.FindName("GoalDatePicker")).SelectedDate = DateTime.Today.AddDays(2); ((CheckBox)window.FindName("GoalPauseBox")).IsChecked = true; Pump();
        Assert.IsFalse(((TextBox)window.FindName("GoalEnergyBox")).IsEnabled); Click("SaveGoalButton"); Assert.HasCount(3, vm.GoalRows);
        goals.SelectedItem = vm.GoalRows[0]; Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteGoalCommand.Execute(null), false)); Assert.HasCount(3, vm.GoalRows);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteGoalCommand.Execute(null), true)); Assert.HasCount(2, vm.GoalRows);
        ((DatePicker)window.FindName("GoalComparePicker")).SelectedDate = DateTime.Today.AddDays(-1); Click("CompareGoalButton"); Assert.IsTrue(vm.GoalComparisonText.Contains("当天未记录"));
        ((DatePicker)window.FindName("GoalComparePicker")).SelectedDate = DateTime.Today; Click("CompareGoalButton"); var comparison = vm.GoalComparisonText;
        vm.GoalCompareDate = DateTime.Today.AddDays(1); Click("CompareGoalButton"); Assert.AreEqual(comparison, vm.GoalComparisonText);
        Text("GoalEnergyBox", "1800.01"); Click("SaveGoalButton"); Assert.IsTrue(vm.Status.Contains("小数位过多"));
        vm.RestoreCommand.Execute(null); Assert.IsTrue(vm.Status.Contains("请先保存")); Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewGoalCommand.Execute(null), true));
        personal.SelectedIndex = 1; Pump(); Text("ExerciseActivityBox", "明确的界面步行输入"); Text("ExerciseDurationBox", "30.5"); Click("SaveExerciseButton");
        Assert.HasCount(1, vm.ExerciseRows); Assert.IsNull(vm.ExerciseRows[0].EnergyKcalTenths); Assert.IsTrue(vm.ExerciseSummary.Contains("全部未填写"));
        Text("ExerciseActivityBox", "明确的零消耗输入"); Text("ExerciseDurationBox", "20"); Text("ExerciseEnergyBox", "0"); Click("SaveExerciseButton");
        Assert.HasCount(2, vm.ExerciseRows); Assert.IsTrue(vm.ExerciseRows.Any(x => x.EnergyText == "0.0")); Assert.IsTrue(vm.ExerciseSummary.Contains("0.0 大卡"));
        var exerciseGrid = (DataGrid)window.FindName("ExercisesGrid"); exerciseGrid.SelectedItem = vm.ExerciseRows.Single(x => x.EnergyKcalTenths is null); Click("EditExerciseButton"); Text("ExerciseEnergyBox", "160"); Text("ExerciseDurationBox", "40"); Click("SaveExerciseButton");
        Assert.IsTrue(vm.ExerciseSummary.Contains("160.0 大卡"));
        exerciseGrid.SelectedItem = vm.ExerciseRows.Single(x => x.EnergyKcalTenths == 0); Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteExerciseCommand.Execute(null), false)); Assert.HasCount(2, vm.ExerciseRows);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteExerciseCommand.Execute(null), true)); Assert.HasCount(1, vm.ExerciseRows);
        Text("ExerciseActivityBox", "第二次明确活动"); Text("ExerciseDurationBox", "0"); Click("SaveExerciseButton"); Assert.HasCount(1, vm.ExerciseRows);
        Text("ExerciseDurationBox", "20.01"); Click("SaveExerciseButton"); Assert.IsTrue(vm.Status.Contains("小数位过多"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.CanClose(), false)); vm.RestoreCommand.Execute(null); Assert.IsTrue(vm.Status.Contains("请先保存"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewExerciseCommand.Execute(null), false)); Assert.AreEqual("第二次明确活动", vm.ExerciseActivity);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewExerciseCommand.Execute(null), true));
        vm.ExerciseFrom = DateTime.Today.AddDays(1); Click("QueryExerciseButton"); Assert.HasCount(1, vm.ExerciseRows);
        Text("ExerciseActivityBox", "第二次明确活动"); Text("ExerciseDurationBox", "20"); Click("SaveExerciseButton"); Assert.HasCount(2, vm.ExerciseRows);
        Assert.AreEqual(comparison, vm.GoalComparisonText); Assert.AreEqual(intakeBefore, JsonSerializer.Serialize(store.ReadSnapshot().Entries));
        vm.TabIndex = 0; Pump(); Assert.IsTrue(((TextBlock)window.FindName("DayGoalTextBlock")).Text.Contains("1800.0"));
        var checks = new List<object>(); var page = (Grid)window.Content;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = 920 * scale; window.Height = 600 * scale; page.LayoutTransform = new ScaleTransform(scale, scale); vm.TabIndex = 6;
            foreach (var subpage in new[] { 0, 1 })
            {
                personal.SelectedIndex = subpage; Pump();
                var names = subpage == 0 ? new[] { "SaveGoalButton", "NewGoalButton", "EditGoalButton", "DeleteGoalButton", "CompareGoalButton" } : new[] { "SaveExerciseButton", "NewExerciseButton", "EditExerciseButton", "DeleteExerciseButton", "QueryExerciseButton" };
                foreach (var name in names)
                { var button = (Button)window.FindName(name); button.BringIntoView(); Pump(); var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize)); Assert.IsTrue(button.IsVisible && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, name + "不可访问"); checks.Add(new { Scale = scale, Control = name, Bounds = bounds.ToString() }); }
                Assert.IsTrue(((DataGrid)window.FindName(subpage == 0 ? "GoalsGrid" : "ExercisesGrid")).ActualHeight >= 65);
            }
        }
        page.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820; Pump(); Assert.IsTrue(vm.CanClose()); window.Close();
        var restarted = new JournalService(new SqliteJournalStore(store.DatabasePath)); Assert.HasCount(2, restarted.Goals()); Assert.HasCount(2, restarted.Exercises(DateOnly.FromDateTime(DateTime.Today.AddDays(-29)), DateOnly.FromDateTime(DateTime.Today)));
        var file = Path.Combine(root, "personal-complete.calorie-backup.json"); backups.Export(file); var read = backups.Inspect(file); Assert.HasCount(2, read.IntakeGoals!); Assert.HasCount(2, read.Exercises!);
        JsonFiles.WriteNew(Path.Combine(root, "personal-checks.json"), new { Success = true, EffectiveHistoryAndFuturePause = true, CorrectionAndDeleteConfirmations = true, RealCrudAndRestart = true, MissingAndZeroEnergy = true, ExerciseNeverOffsetsIntakeOrGoal = true, DraftAndRestoreProtection = true, InvalidFilterDoesNotPolluteRefresh = true, FullBackup = true, LayoutChecks = checks });
        void Text(string name, string value) { ((TextBox)window.FindName(name)).Text = value; Pump(); }
        void Click(string name) { var button = (Button)window.FindName(name); ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); Pump(); }
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
