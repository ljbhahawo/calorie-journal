using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class SettingsMealBindingChecks
{
    public static void Verify()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "wpf_settings_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        var journal = new JournalService(store); var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(directory, "recovery")), directory);
        var window = new MainWindow(vm); window.Show(); Pump(); Assert.IsTrue(vm.WeightMode); Assert.AreEqual(0, vm.SettingsFoodMode);
        Fill("询问同意"); Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 6));
        Assert.HasCount(61, journal.Foods()); Assert.HasCount(1, store.ReadSnapshot().Entries);
        Fill("询问同意"); Assert.AreEqual(0, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 6)); Assert.HasCount(61, journal.Foods());
        Fill("询问拒绝"); Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 7)); Assert.HasCount(61, journal.Foods()); Assert.HasCount(3, store.ReadSnapshot().Entries);
        Fill("询问取消"); Assert.AreEqual(1, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 2)); Assert.HasCount(3, store.ReadSnapshot().Entries); Assert.AreEqual("询问取消", vm.EntryName);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewEntryCommand.Execute(null), true));
        vm.TabIndex = 6; Pump(); ((ComboBox)window.FindName("NewFoodPolicyBox")).SelectedIndex = 1; Pump(); Assert.AreEqual(1, vm.SettingsFoodMode); Click("SaveSettingsButton"); Assert.AreEqual("auto", journal.Settings().NewFoodMode);
        vm.TabIndex = 0; Fill("自动加入"); Assert.AreEqual(0, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 6)); Assert.HasCount(62, journal.Foods());
        vm.TabIndex = 6; Pump(); ((ComboBox)window.FindName("NewFoodPolicyBox")).SelectedIndex = 2; Click("SaveSettingsButton");
        vm.TabIndex = 0; Fill("不加入"); Assert.AreEqual(0, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 6)); Assert.HasCount(62, journal.Foods());
        vm.TabIndex = 6; Pump(); ((CheckBox)window.FindName("DefaultWeightSettingBox")).IsChecked = false;
        ((ComboBox)window.FindName("NewFoodPolicyBox")).SelectedIndex = 0; Click("SaveSettingsButton"); Assert.IsTrue(vm.WeightMode, "设置不能改写现有草稿方式");
        vm.TabIndex = 0; vm.NewEntryCommand.Execute(null); Assert.IsFalse(vm.WeightMode);
        ((TextBox)window.FindName("EntryNameBox")).Text = "只有总大卡"; ((TextBox)window.FindName("DirectKcalBox")).Text = "10";
        Assert.AreEqual(0, ConfirmationChecks.RespondChoice(() => vm.SaveEntryCommand.Execute(null), 6)); Assert.IsTrue(vm.Status.Contains("未生成每100克资料")); Assert.HasCount(62, journal.Foods());
        var saved = store.ReadSnapshot().Entries.First(x => x.FoodId is not null); vm.SelectedEntry = vm.DayEntries.Single(x => x.Id == saved.Id); vm.EditEntryCommand.Execute(null); Assert.IsTrue(vm.WeightMode);
        vm.SaveEntryCommand.Execute(null); Assert.AreEqual(6, store.ReadSnapshot().Entries.Length);
        vm.SelectedEntry = vm.DayEntries.Single(x => x.Id == saved.Id); vm.CopyEntryCommand.Execute(null); Assert.IsTrue(vm.WeightMode);
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.NewEntryCommand.Execute(null), true));
        Click("MealsViewButton"); Assert.IsTrue(((Border)window.FindName("DayMealsPanel")).IsVisible); Assert.IsFalse(((Border)window.FindName("DayEntriesCard")).IsVisible);
        Assert.HasCount(4, vm.DayMeals); Assert.AreEqual(6, vm.DayMeals.Sum(x => x.Entries.Length));
        var lunch = vm.DayMeals.Single(x => x.Name == "午餐"); vm.AddMealEntryCommand.Execute(lunch); Assert.AreEqual(lunch.MealId, vm.EntryMeal!.Id);
        vm.EntryName = "午餐零值"; vm.DirectKcal = "0"; vm.SaveEntryCommand.Execute(null); Assert.IsTrue(vm.DayMeals.Single(x => x.Name == "午餐").Summary.Contains("0.0"));
        var zero = vm.DayMeals.Single(x => x.Name == "午餐").Entries.Single(); vm.EditListedEntryCommand.Execute(zero); vm.DirectKcal = "5"; vm.SaveEntryCommand.Execute(null);
        Assert.IsTrue(vm.DayMeals.Single(x => x.Name == "午餐").Summary.Contains("5.0"));
        Assert.AreEqual(1, ConfirmationChecks.Respond(() => vm.DeleteListedEntryCommand.Execute(store.FindEntry(zero.Id)), true)); Assert.AreEqual("未记录", vm.DayMeals.Single(x => x.Name == "午餐").Summary);
        var dimensions = new List<object>(); var root = (Grid)window.Content;
        foreach(var factor in new[] {1.0,1.25,1.5,2.0})
        {
            window.Width = 920*factor; window.Height = 600*factor; root.LayoutTransform = new ScaleTransform(factor,factor); vm.ShowSingleCommand.Execute(null); Pump();
            var total = (Border)window.FindName("DayTotalCard"); var details = (Border)window.FindName("DayEntriesCard");
            Assert.IsTrue(total.ActualHeight < 70); Assert.IsTrue(details.ActualHeight > total.ActualHeight*3);
            dimensions.Add(new { Scale=factor, Width=920, Height=600, TotalHeight=total.ActualHeight, DetailsHeight=details.ActualHeight, EntriesGridHeight=((DataGrid)window.FindName("EntriesGrid")).ActualHeight });
            vm.ShowMealsCommand.Execute(null); Pump(); var list = (ListBox)window.FindName("DayMealsList"); list.ScrollIntoView(vm.DayMeals.Last()); Pump();
            var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.DayMeals.Last()); var add = Descendants<Button>(last).First(x=>x.Name=="AddMealEntryButton"); add.BringIntoView(); Pump();
            var bounds=add.TransformToAncestor(window).TransformBounds(new Rect(add.RenderSize)); Assert.IsTrue(add.IsVisible && bounds.Top>=0 && bounds.Bottom<=window.ActualHeight+1);
        }
        root.LayoutTransform = Transform.Identity; window.Width=1240; window.Height=820; vm.ShowSingleCommand.Execute(null); Pump();
        dimensions.Add(new { Scale=1.0, Width=1240, Height=820, TotalHeight=((Border)window.FindName("DayTotalCard")).ActualHeight, EntriesGridHeight=((DataGrid)window.FindName("EntriesGrid")).ActualHeight });
        window.Close(); Pump();
        var restarted = new JournalService(new SqliteJournalStore(store.DatabasePath)); Assert.IsFalse(restarted.Settings().DefaultWeightMode); Assert.AreEqual("ask", restarted.Settings().NewFoodMode);
        JsonFiles.WriteNew(Path.Combine(directory,"settings-meals-checks.json"),new{Success=true,RealYesNoCancel=true,AllThreePolicies=true,DefaultAndDraftProtection=true,EditCopyPreserved=true,GroupingCrudAndScroll=true,SettingsRestart=true,LayoutComparisons=dimensions});
        void Fill(string name) { vm.EntryName=name; vm.Weight="150"; vm.UnitKcal="100"; Pump(); }
        void Click(string name) { var button=(Button)window.FindName(name); ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); Pump(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    { for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T typed)yield return typed;foreach(var descendant in Descendants<T>(child))yield return descendant;} }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
}
