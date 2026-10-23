using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class WeightBindingChecks
{
    public static void Verify()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var directory = Path.Combine(root, "wpf_weight_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var journal = new JournalService(store); var food = journal.Foods("长粒白米饭").Single();
        var main = new MainViewModel(journal, new BackupService(store, Path.Combine(directory, "recovery")), directory);
        var mainWindow = new MainWindow(main); mainWindow.Show(); Pump();
        main.ChosenFood = main.FoodChoices.Single(x => x.Id == food.Id); Pump();
        Assert.IsTrue(((Button)mainWindow.FindName("WeightConversionButton")).IsEnabled);
        var vm = new WeightConversionViewModel(journal, food.Id, food.KcalPer100gHundredths, null);
        var window = new WeightConversionWindow(vm, journal); window.Show(); Pump();
        ((TextBox)window.FindName("GrossWeightBox")).Text = "0.536";
        ((ComboBox)window.FindName("GrossUnitBox")).SelectedItem = "kg";
        vm.Deductions[0].Name = "梗"; vm.Deductions[0].Weight = "221";
        Click(window, "AddDeductionButton"); vm.Deductions[1].Name = "外皮"; vm.Deductions[1].Weight = "10"; Pump();
        Assert.IsTrue(((TextBlock)window.FindName("WeightPreviewText")).Text.Contains("305.0g"));
        var checks = Layout(window, new[] { "AddDeductionButton", "RemoveDeductionButton", "ApplyWeightButton", "CancelWeightButton" });
        vm.UsePortion = true; Pump(); checks.AddRange(Layout(window, new[] { "ManagePortionsButton", "ApplyWeightButton", "CancelWeightButton" }));
        vm.UsePortion = false; Pump(); Click(window, "ApplyWeightButton");
        Assert.IsNotNull(window.Result); Assert.IsEmpty(store.ReadSnapshot().Entries);
        main.ApplyWeightCalculation(window.Result); Pump();
        Assert.AreEqual("305.0", ((TextBox)mainWindow.FindName("WeightBox")).Text);
        main.SaveEntryCommand.Execute(null); Pump();
        var grossEntry = store.ReadSnapshot().Entries.Single(); Assert.AreEqual(3965L, grossEntry.EnergyKcalTenths);
        Assert.HasCount(2, grossEntry.WeightCalculation!.Deductions);
        main.SelectedEntry = main.DayEntries.Single(); main.EditEntryCommand.Execute(null); Pump();
        Assert.IsTrue(main.WeightCalculationDescription.Contains("梗"));
        main.UnitKcal = "200"; main.SaveEntryCommand.Execute(null); Pump();
        Assert.AreEqual(6100L, store.FindEntry(grossEntry.Id)!.EnergyKcalTenths);
        main.SelectedEntry = main.DayEntries.Single(); main.CopyEntryCommand.Execute(null); Pump();
        main.Weight = "300"; Assert.IsTrue(main.Status.Contains("依据已取消"));
        main.SaveEntryCommand.Execute(null); Pump();
        Assert.IsNull(store.ReadSnapshot().Entries.Single(x => x.Id != grossEntry.Id).WeightCalculation);

        var editorVm = new PortionEditorViewModel(journal, food.Id); var editor = new PortionEditorWindow(editorVm); editor.Show(); Pump();
        ((TextBox)editor.FindName("PortionNameBox")).Text = "一碗";
        ((TextBox)editor.FindName("PortionGramsBox")).Text = "0.15";
        ((ComboBox)editor.FindName("PortionUnitBox")).SelectedItem = "kg";
        Click(editor, "SavePortionButton");
        Assert.HasCount(1, journal.Portions(food.Id));
        Assert.AreEqual(1500L, journal.Portions(food.Id)[0].GramsTenths);
        checks.AddRange(Layout(editor, new[] { "SavePortionButton", "NewPortionButton", "DeletePortionButton", "ClosePortionsButton" }));
        ((ListBox)editor.FindName("SavedPortionsList")).SelectedItem = editorVm.Portions.Single(); Pump();
        ((TextBox)editor.FindName("PortionGramsBox")).Text = "200";
        Assert.AreEqual(1, ConfirmationChecks.Respond(editorVm.New, false));
        Assert.AreEqual("200", editorVm.Grams);
        Assert.AreEqual(1, ConfirmationChecks.Respond(editorVm.New, true));
        ((ListBox)editor.FindName("SavedPortionsList")).SelectedItem = editorVm.Portions.Single(); Pump();
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click(editor, "DeletePortionButton"), false));
        Assert.HasCount(1, journal.Portions(food.Id));
        var definition = journal.Portions(food.Id).Single();
        var portionVm = new WeightConversionViewModel(journal, food.Id, food.KcalPer100gHundredths, null) { UsePortion = true, PortionCount = "1.5" };
        var portionWindow = new WeightConversionWindow(portionVm, journal); portionWindow.Show(); Pump();
        Click(portionWindow, "ApplyWeightButton"); Assert.IsNotNull(portionWindow.Result);
        main.ChosenFood = main.FoodChoices.Single(x => x.Id == food.Id);
        main.ApplyWeightCalculation(portionWindow.Result); main.SaveEntryCommand.Execute(null); Pump();
        var saved = store.ReadSnapshot().Entries.Single(x => x.WeightCalculation?.Kind == "portion");
        Assert.AreEqual(2925L, saved.EnergyKcalTenths);
        journal.SavePortion(definition with { GramsTenths = 3000 });
        var historical = new WeightConversionViewModel(journal, food.Id, food.KcalPer100gHundredths, saved.WeightCalculation);
        Assert.IsTrue(historical.SelectedPortion!.Historical); Assert.AreEqual(2250L, historical.CreateSnapshot().NetGramsTenths);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => Click(editor, "DeletePortionButton"), true));
        Assert.IsEmpty(journal.Portions(food.Id));
        Assert.AreEqual(1500L, store.FindEntry(saved.Id)!.WeightCalculation!.PortionGramsTenths);
        editor.Close(); mainWindow.Close(); Pump();
        JsonFiles.WriteNew(Path.Combine(directory, "weight-checks.json"), new { Success = true, GrossAndMultipleDeductions = true, PortionCrudAndRealConfirmations = true,
            ApplyOnlyUpdatesDraft = true, ManualWeightChangeClearsBasis = true, HistoricalPortionRetained = true, LayoutChecks = checks });
    }

    private static List<object> Layout(Window window, string[] names)
    {
        var checks = new List<object>(); var grid = (Grid)window.Content;
        foreach (var factor in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = window.MinWidth * factor; window.Height = window.MinHeight * factor; grid.LayoutTransform = new ScaleTransform(factor, factor); Pump();
            foreach (var name in names)
            {
                var control = (Button)window.FindName(name); control.BringIntoView(); Pump();
                var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                Assert.IsTrue(control.IsVisible && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight + 1, $"重量窗口{factor}下{name}不可达：{bounds}");
                checks.Add(new { Window = window.Title, Scale = factor, Control = name });
            }
        }
        grid.LayoutTransform = Transform.Identity; window.Width = 700; window.Height = 650; Pump();
        return checks;
    }
    private static void Click(Window window, string name) { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
