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

internal static class StatisticsBindingChecks
{
    public static void Verify()
    {
        var tests = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var root = Path.Combine(tests, "wpf_statistics_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(root, "journal.db")); store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json")));
        // 使用前序真实WPF用例保存的资料，保留全部七类数据用于兼容回退检查。
        var template = new SqliteJournalStore(Path.Combine(Directory.GetDirectories(tests, "wpf_template_*").OrderByDescending(Directory.GetLastWriteTimeUtc).First(), "journal.db")).ReadSnapshot();
        var body = new SqliteJournalStore(Path.Combine(Directory.GetDirectories(tests, "wpf_body_*").OrderByDescending(Directory.GetLastWriteTimeUtc).First(), "journal.db")).ReadSnapshot();
        new BackupService(store, Path.Combine(root, "recovery")).Restore(template with { BodyMeasurements = body.BodyMeasurements });
        var journal = new JournalService(store); journal.SaveSettings(new JournalSettings { DefaultWeightMode = false, NewFoodMode = "never" });
        var from = new DateOnly(2024, 2, 26); var to = new DateOnly(2024, 3, 1); var meal = journal.Meals().First(x => x.IsActive);
        foreach(var (date, tenths) in new[] { (from, 0L), (from.AddDays(3), 1000L), (to, 2000L) }) journal.SaveEntry(new IntakeEntry { Id=Guid.NewGuid(), EntryDate=date, MealCategoryId=meal.Id, MealNameSnapshot=meal.Name, FoodNameSnapshot="统计界面明确输入", EnergyKcalTenths=tenths });
        var vm = new MainViewModel(journal, new BackupService(store, Path.Combine(root, "recovery")), root); var window = new MainWindow(vm); window.Show(); vm.TabIndex=5; Pump();
        ((ComboBox)window.FindName("StatisticsModeBox")).SelectedIndex=1; ((DatePicker)window.FindName("StatisticsAnchorPicker")).SelectedDate=new DateTime(2024,2,15); Click("QueryStatisticsButton"); Assert.HasCount(29,vm.StatisticsDays);
        Click("PreviousStatisticsButton"); Assert.AreEqual(new DateOnly(2024,1,1),vm.StatisticsDays[0].Date); Click("NextStatisticsButton"); Assert.HasCount(29,vm.StatisticsDays);
        vm.StatisticsMode=2; vm.StatisticsFrom=from.ToDateTime(TimeOnly.MinValue); vm.StatisticsTo=to.ToDateTime(TimeOnly.MinValue); Click("QueryStatisticsButton");
        Assert.HasCount(5,vm.StatisticsDays); Assert.AreEqual("300.0 大卡",vm.StatisticsTotal); Assert.AreEqual("100.0 大卡/已记录日",vm.StatisticsAverage);
        Assert.IsTrue(vm.StatisticsTrend[0].IsZero && !vm.StatisticsTrend[0].IsMissing); Assert.IsTrue(vm.StatisticsTrend[1].IsMissing); Assert.AreEqual(60d,vm.StatisticsTrend[3].BarHeight);
        var chart=(ItemsControl)window.FindName("StatisticsTrendItems"); Pump();
        var zero=chart.ItemContainerGenerator.ContainerFromIndex(0); var missing=chart.ItemContainerGenerator.ContainerFromIndex(1);
        Assert.IsTrue(Descendants<System.Windows.Shapes.Ellipse>(zero).Single(x=>x.Name=="StatisticsZeroMarker").IsVisible);
        Assert.IsTrue(Descendants<TextBlock>(missing).Single(x=>x.Name=="StatisticsMissingMarker").IsVisible);
        Assert.AreEqual(120d,Descendants<Border>(chart.ItemContainerGenerator.ContainerFromIndex(4)).Single(x=>x.Name=="StatisticsBar").ActualHeight);
        vm.SelectedStatisticsDay=vm.StatisticsDays[3]; Click("OpenStatisticsDayButton"); Assert.AreEqual(0,vm.TabIndex); Assert.AreEqual(from.AddDays(3).ToDateTime(TimeOnly.MinValue),vm.SelectedDate);
        vm.SelectedEntry=vm.DayEntries.Single(); vm.EditEntryCommand.Execute(null); vm.DirectKcal="150"; vm.SaveEntryCommand.Execute(null); Assert.AreEqual("350.0 大卡",vm.StatisticsTotal);
        vm.SelectedEntry=vm.DayEntries.Single(); Assert.AreEqual(1,ConfirmationChecks.Respond(()=>vm.DeleteEntryCommand.Execute(null),true)); Assert.AreEqual("200.0 大卡",vm.StatisticsTotal); Assert.IsNull(vm.StatisticsDays[3].EnergyKcal);
        vm.StatisticsFrom=DateTime.Today.AddDays(1); Click("QueryStatisticsButton"); Assert.HasCount(5,vm.StatisticsDays);
        vm.SelectedDate=from.AddDays(1).ToDateTime(TimeOnly.MinValue); vm.EntryName="非法筛选后仍按有效区间刷新"; vm.DirectKcal="50"; vm.SaveEntryCommand.Execute(null); Assert.AreEqual("250.0 大卡",vm.StatisticsTotal); Assert.HasCount(5,vm.StatisticsDays);
        vm.TabIndex=5; vm.SelectedStatisticsDay=vm.StatisticsDays[0]; vm.EntryName="待保存统计跳转草稿"; vm.DirectKcal="1";
        Assert.AreEqual(1,ConfirmationChecks.Respond(()=>vm.OpenStatisticsDayCommand.Execute(null),false)); Assert.AreEqual(5,vm.TabIndex); Assert.AreEqual("待保存统计跳转草稿",vm.EntryName);
        Assert.AreEqual(1,ConfirmationChecks.Respond(()=>vm.OpenStatisticsDayCommand.Execute(null),true)); Assert.AreEqual(0,vm.TabIndex);
        vm.TabIndex=5; vm.StatisticsMode=0; vm.StatisticsAnchor=DateTime.Today; Click("QueryStatisticsButton"); Assert.IsFalse(vm.NextStatisticsCommand.CanExecute(null));
        var checks=new List<object>(); var page=(Grid)window.Content;
        foreach(var scale in new[] {1.0,1.25,1.5,2.0})
        {
            window.Width=920*scale; window.Height=600*scale; page.LayoutTransform=new ScaleTransform(scale,scale);
            foreach(var mode in new[]{0,2})
            {
                vm.StatisticsMode=mode; vm.StatisticsFrom=from.ToDateTime(TimeOnly.MinValue); vm.StatisticsTo=to.ToDateTime(TimeOnly.MinValue); vm.StatisticsAnchor=new DateTime(2024,2,15); Click("QueryStatisticsButton");
                foreach(var name in mode==0 ? new[]{"QueryStatisticsButton","CurrentStatisticsButton","OpenStatisticsDayButton","PreviousStatisticsButton","NextStatisticsButton"} : new[]{"QueryStatisticsButton","CurrentStatisticsButton","OpenStatisticsDayButton"})
                { var button=(Button)window.FindName(name); button.BringIntoView(); Pump(); var bounds=button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize)); Assert.IsTrue(button.IsVisible && bounds.Left>=0 && bounds.Right<=window.ActualWidth+1 && bounds.Top>=0 && bounds.Bottom<=window.ActualHeight+1,name+"不可访问");checks.Add(new{Scale=scale,Mode=mode,Control=name,Bounds=bounds.ToString()}); }
                Assert.IsTrue(((DataGrid)window.FindName("StatisticsGrid")).ActualHeight>=79); Assert.IsTrue(((ScrollViewer)window.FindName("StatisticsTrendPanel")).ActualHeight>0);
            }
        }
        page.LayoutTransform=Transform.Identity; window.Width=1240;window.Height=820;Pump();Assert.IsTrue(vm.CanClose());window.Close();
        JsonFiles.WriteNew(Path.Combine(root,"statistics-checks.json"),new{Success=true,WeekMonthCustom=true,RealZeroAndMissingMarks=true,RealBarHeight=true,EditDeleteRefresh=true,InvalidFilterPreserved=true,DraftProtection=true,LayoutChecks=checks});
        void Click(string name){var button=(Button)window.FindName(name);((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();Pump();}
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)yield return value;foreach(var nested in Descendants<T>(child))yield return nested;}}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
}
