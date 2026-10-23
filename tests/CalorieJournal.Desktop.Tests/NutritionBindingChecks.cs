using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class NutritionBindingChecks
{
    public static void Verify()
    {
        var tests = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!; var root = Path.Combine(tests, "wpf_nutrition_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var preceding = new SqliteJournalStore(Path.Combine(Directory.GetDirectories(tests, "wpf_personal_*").OrderByDescending(Directory.GetLastWriteTimeUtc).First(), "journal.db")).ReadSnapshot();
        var backups = new BackupService(store, Path.Combine(root, "recovery")); backups.Restore(preceding); var journal = new JournalService(store);
        journal.SaveSettings(new JournalSettings { DefaultWeightMode = true, NewFoodMode = "auto" });
        var vm = new MainViewModel(journal, backups, root); var window = new MainWindow(vm); window.Show(); vm.TabIndex = 1; Pump();
        Text("FoodNameBox", "明确WPF营养标签食物"); ((ComboBox)window.FindName("FoodEnergyUnitBox")).SelectedItem = "kJ"; Text("FoodUnitKcalBox", "418.4"); Text("FoodProteinBox", "20"); Click("SaveFoodButton");
        var food = journal.Foods("明确WPF营养标签").Single(); Assert.AreEqual(41840L, food.KilojoulesPer100gHundredths); Assert.AreEqual(2000L, food.ProteinPer100gHundredths);
        vm.SelectedLibraryFood = food; Click("UseFoodButton"); Text("WeightBox", "150"); Assert.IsTrue(vm.EnergyPreview.Contains("150.0")); Assert.IsTrue(vm.ProteinPreview.Contains("30.00")); Click("SaveEntryButton");
        var weighted = vm.DayEntries.Single(x => x.FoodId == food.Id); Assert.AreEqual(3000L, weighted.ProteinGramsHundredths); Assert.IsTrue(vm.DayProteinText.Contains("30.00"));
        var direct = (TextBox)window.FindName("DirectKcalBox"); direct.BringIntoView(); direct.Focus(); Pump(); Assert.IsFalse(vm.WeightMode); Assert.IsFalse(((CheckBox)window.FindName("WeightModeBox")).IsChecked!.Value);
        Text("EntryNameBox", "明确的直接零蛋白质输入"); Text("DirectKcalBox", "100"); Text("DirectProteinBox", "0"); vm.UnitKcal = "无效隐藏值"; vm.UnitProtein = "无效隐藏值"; Click("SaveEntryButton");
        var zero = vm.DayEntries.Single(x => x.FoodNameSnapshot == "明确的直接零蛋白质输入"); Assert.IsNull(zero.WeightGramsTenths); Assert.IsNull(zero.ProteinPer100gHundredthsSnapshot); Assert.AreEqual(0L, zero.ProteinGramsHundredths);
        Assert.IsTrue(vm.WeightMode); direct.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent }); Pump(); Assert.IsFalse(vm.WeightMode); Assert.IsTrue(journal.Settings().DefaultWeightMode);
        ((ComboBox)window.FindName("DirectEnergyUnitBox")).SelectedItem = "kJ"; Text("EntryNameBox", "明确的本项千焦输入"); Text("DirectKcalBox", "418.4"); Text("DirectProteinBox", "12.34"); Click("SaveEntryButton");
        var directKj = vm.DayEntries.Single(x => x.FoodNameSnapshot == "明确的本项千焦输入"); Assert.AreEqual(1000L, directKj.EnergyKcalTenths); Assert.AreEqual(1234L, directKj.ProteinGramsHundredths);
        vm.SelectedEntry = directKj; Click("EditEntryButton"); Assert.AreEqual("kJ", vm.DirectEnergyUnit); Assert.AreEqual("418.40", vm.DirectKcal); Assert.AreEqual("12.34", vm.DirectProtein);
        Click("CopyEntryButton"); Click("SaveEntryButton"); Assert.AreEqual(directKj, store.FindEntry(directKj.Id)); Assert.HasCount(2, vm.DayEntries.Where(x => x.FoodNameSnapshot == directKj.FoodNameSnapshot).ToArray());
        vm.SelectedEntry = weighted; Click("EditEntryButton"); Assert.AreEqual("kJ", vm.EntryEnergyUnit); Assert.AreEqual("418.40", vm.UnitKcal); Text("UnitProteinBox", "25"); Click("SaveEntryButton");
        Assert.AreEqual(3750L, store.FindEntry(weighted.Id)!.ProteinGramsHundredths); Assert.IsTrue(store.FindEntry(weighted.Id)!.SourceSnapshot.Contains("单位蛋白质已调整"));
        vm.TabIndex = 1; vm.SelectedLibraryFood = journal.Foods().Single(x => x.Id == food.Id); Text("FoodProteinBox", "40"); Click("SaveFoodButton"); Assert.AreEqual(3750L, store.FindEntry(weighted.Id)!.ProteinGramsHundredths);
        var template = new MealTemplate { Id = Guid.NewGuid(), Name = "明确营养整餐模板", Items = [journal.CreateTemplateItem(Guid.NewGuid(), food.Id, 1000)] }; journal.SaveTemplate(template);
        var templateVm = new TemplateViewModel(journal, DateTime.Today); var templateWindow = new TemplateWindow(templateVm); templateWindow.Show(); templateVm.Selected = journal.Templates().Single(x => x.Id == template.Id); templateVm.BeginReview();
        templateVm.ReviewItems[0].Amount = "200"; templateVm.ReviewMeal = templateVm.Meals[0]; Assert.IsTrue(templateVm.ReviewTotal.Contains("80.00")); templateVm.SaveBatch(); templateWindow.Close();
        var batchEntry = store.ReadSnapshot().Entries.Single(x => x.FoodId == food.Id && x.WeightGramsTenths == 2000); Assert.AreEqual(8000L, batchEntry.ProteinGramsHundredths); Assert.AreEqual(41840L, batchEntry.KilojoulesHundredthsSnapshot);
        vm.TabIndex = 0; vm.ChosenFood = journal.Foods().Single(x => x.Id == food.Id); Text("WeightBox", "10000"); Text("UnitKcalBox", "1.00"); Text("UnitProteinBox", "0.01"); Click("SaveEntryButton");
        Assert.IsTrue(vm.DayEntries.Any(x => x.WeightGramsTenths == 100000 && x.EnergyKcalTenths == 239 && x.ProteinGramsHundredths == 100));
        var snapshot = store.ReadSnapshot(); backups.Export(Path.Combine(root, "nutrition-complete.calorie-backup.json"));
        var layout = new List<object>(); var page = (Grid)window.Content;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = 920 * scale; window.Height = 600 * scale; page.LayoutTransform = new ScaleTransform(scale, scale);
            foreach (var tab in new[] { 0, 1 })
            {
                vm.TabIndex = tab; Pump();
                foreach (var name in tab == 0 ? new[] { "EntryEnergyUnitBox", "DirectEnergyUnitBox", "UnitProteinBox", "DirectProteinBox", "DirectKcalBox", "SaveEntryButton", "NewEntryButton" } : new[] { "FoodEnergyUnitBox", "FoodProteinBox", "SaveFoodButton", "UseFoodButton" })
                { var control = (FrameworkElement)window.FindName(name); control.BringIntoView(); Pump(); var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize)); Assert.IsTrue(control.IsVisible && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, name + "不可访问"); layout.Add(new { Scale = scale, Control = name, Bounds = bounds.ToString() }); }
            }
        }
        page.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820; Pump(); Assert.IsTrue(vm.CanClose()); window.Close();
        var restart = new SqliteJournalStore(store.DatabasePath).ReadSnapshot(); Assert.AreEqual(JsonSerializer.Serialize(snapshot.Entries), JsonSerializer.Serialize(restart.Entries));
        JsonFiles.WriteNew(Path.Combine(root, "nutrition-checks.json"), new { Success = true, RealLibraryAndWeightProtein = true, MouseAndKeyboardDirectMode = true, SettingsDefaultUnchanged = true, HiddenWeightFieldsIgnored = true, DirectKilojoules = true, HistoricalCopySnapshots = true, FoodEditsDoNotRecalculateHistory = true, TemplateNutrition = true, SingleRounding = true, MissingAndZeroProtein = true, FullBackupAndRestart = true, LayoutChecks = layout });
        void Text(string name, string value) { ((TextBox)window.FindName(name)).Text = value; Pump(); }
        void Click(string name) { var button = (Button)window.FindName(name); ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); Pump(); }
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
