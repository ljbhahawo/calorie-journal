using System.IO;
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

internal static class BodyHierarchyBindingChecks
{
    public static void Verify()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "wpf_body_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var journal = new JournalService(store); journal.SaveSettings(new JournalSettings { DefaultWeightMode = false, NewFoodMode = "never" });
        var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(root, "recovery")), root); var window = new MainWindow(vm); window.Show(); vm.TabIndex = 4; Pump();
        Text("BodyWeightBox", "70"); Text("BodyHeightBox", "175"); Text("BodyAgeBox", "30"); Text("BodyWaistBox", "80"); ((ComboBox)window.FindName("BodySexBox")).SelectedIndex = 1; Pump();
        Assert.IsTrue(((TextBlock)window.FindName("BodyPreviewText")).Text.Contains("1648.8")); Click("SaveBodyButton"); Assert.HasCount(1, store.ReadBodyMeasurements()); Assert.HasCount(1, vm.BodyRows);
        Text("BodyWeightBox", "69"); Click("SaveBodyButton"); Assert.HasCount(1, store.ReadBodyMeasurements()); Assert.IsTrue(vm.Status.Contains("该日期已有"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewBodyCommand.Execute(null), false)); Assert.AreEqual("69", vm.BodyWeight);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewBodyCommand.Execute(null), true));
        ((DatePicker)window.FindName("BodyDatePicker")).SelectedDate = DateTime.Today.AddDays(-1); Text("BodyWeightBox", "69"); Click("SaveBodyButton"); Assert.HasCount(2, vm.BodyRows);
        var grid = (DataGrid)window.FindName("BodyGrid"); grid.SelectedItem = vm.BodyRows[0]; Click("EditBodyButton"); Text("BodyWeightBox", "71"); Click("SaveBodyButton");
        Assert.AreEqual(7100L, store.ReadBodyMeasurements()[0].WeightKgHundredths); Assert.AreEqual(6900L, store.ReadBodyMeasurements()[1].WeightKgHundredths);
        Text("BodyWeightBox", "68.123"); Click("SaveBodyButton"); Assert.HasCount(2, store.ReadBodyMeasurements()); Assert.IsTrue(vm.Status.Contains("两位小数"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.CanClose(), false)); Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewBodyCommand.Execute(null), true));
        grid.SelectedItem = vm.BodyRows[1]; Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteBodyCommand.Execute(null), false)); Assert.HasCount(2, vm.BodyRows);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteBodyCommand.Execute(null), true)); Assert.HasCount(1, vm.BodyRows);
        vm.BodyDate = DateTime.Today.AddDays(-2); Text("BodyWeightBox", "68"); Text("BodyHeightBox", ""); Text("BodyAgeBox", ""); ((ComboBox)window.FindName("BodySexBox")).SelectedIndex = 0; Click("SaveBodyButton");
        Assert.IsTrue(vm.BodyRows[1].Bmi is null && vm.BodyRows[1].RestingKcal is null); Assert.IsTrue(vm.BodySummary.Contains("2 次实际测量"));
        vm.BodyFrom = DateTime.Today.AddDays(1); Click("QueryBodyButton"); Assert.HasCount(2, vm.BodyRows);
        vm.BodyFrom = DateTime.Today; Click("QueryBodyButton"); Assert.HasCount(1, vm.BodyRows); vm.BodyFrom = DateTime.Today.AddDays(-89); Click("QueryBodyButton");
        var restart = new JournalService(new SqliteJournalStore(store.DatabasePath)); Assert.HasCount(2, restart.BodyMeasurements(DateOnly.MinValue, DateOnly.FromDateTime(DateTime.Today)));
        vm.TabIndex = 0; Pump();
        foreach (var meal in journal.Meals().Take(3)) { vm.EntryMeal = meal; vm.EntryName = "层级界面 " + meal.Name; vm.DirectKcal = "100"; vm.SaveEntryCommand.Execute(null); }
        var checks = new List<object>(); var page = (Grid)window.Content;
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Width = 920 * scale; window.Height = 600 * scale; page.LayoutTransform = new ScaleTransform(scale, scale); vm.TabIndex = 0; vm.ShowMealsCommand.Execute(null); Pump();
            var dayList = (ListBox)window.FindName("DayMealsList"); dayList.ScrollIntoView(vm.DayMeals.First()); Pump();
            var day = (ListBoxItem)dayList.ItemContainerGenerator.ContainerFromItem(vm.DayMeals.First());
            var mealHeader = Descendants<Border>(day).First(x => x.Name == "DayMealHeader"); var food = Descendants<Border>(day).First(x => x.Name == "DayMealEntryCard");
            Assert.IsTrue(Left(food) > Left(mealHeader) + 15, "当日食物须在餐次下缩进");
            vm.ShowContinuousCommand.Execute(null); Pump(); var list = (ListBox)window.FindName("ContinuousDaysList"); list.ScrollIntoView(vm.BrowseDays[0]); Pump();
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.BrowseDays[0]);
            var date = Descendants<Border>(container).First(x => x.Name == "ContinuousDateHeader"); var meal = Descendants<Border>(container).First(x => x.Name == "ContinuousMealHeader"); var entry = Descendants<Border>(container).First(x => x.Name == "ContinuousEntryCard");
            Assert.IsTrue(Left(meal) > Left(date) + 12 && Left(entry) > Left(meal) + 12, "连续列表须为日期、餐次、食物三层递进");
            vm.TabIndex = 4; Pump();
            foreach (var name in new[] { "SaveBodyButton", "NewBodyButton", "QueryBodyButton", "EditBodyButton", "DeleteBodyButton" })
            { var button = (Button)window.FindName(name); button.BringIntoView(); Pump(); var rect = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize)); Assert.IsTrue(button.IsVisible && rect.Left >= 0 && rect.Top >= 0 && rect.Right <= window.ActualWidth + 1 && rect.Bottom <= window.ActualHeight + 1, name + "不可访问"); checks.Add(new { Scale = scale, Control = name, Bounds = rect.ToString() }); }
        }
        page.LayoutTransform = Transform.Identity; window.Width = 1240; window.Height = 820; Pump(); Assert.IsTrue(vm.CanClose()); window.Close();
        JsonFiles.WriteNew(Path.Combine(root, "body-hierarchy-checks.json"), new { Success = true, FormulaPreview = true, RealCrudAndConfirmations = true, MissingAndDuplicateDates = true, Restart = true, HierarchyScales = 4, LayoutChecks = checks });
        double Left(FrameworkElement item) => item.TransformToAncestor(window).Transform(new Point()).X;
        void Text(string name, string value) { ((TextBox)window.FindName(name)).Text = value; Pump(); }
        void Click(string name) { var button = (Button)window.FindName(name); ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); Pump(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item; foreach (var descendant in Descendants<T>(child)) yield return descendant; } }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
