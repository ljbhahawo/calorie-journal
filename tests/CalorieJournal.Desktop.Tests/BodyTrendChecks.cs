using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class BodyTrendChecks
{
    public static void Verify()
    {
        var root=Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!,"wpf_trend_"+Guid.NewGuid().ToString("N"));
        var store=new SqliteJournalStore(Path.Combine(root,"journal.db"));store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory,"data/foods.json")));
        var journal=new JournalService(store);var backups=new BackupService(store,Path.Combine(root,"recovery"));
        var start=new DateOnly(2023,12,31); var records=new[] { Measurement(start,7000,800),Measurement(start.AddDays(1),7000,null),Measurement(start.AddDays(31),6900,790) };
        foreach(var value in records)journal.SaveBodyMeasurement(value);
        var vm=new MainViewModel(journal,backups,root);var window=new MainWindow(vm);window.Show();vm.TabIndex=3;vm.BodyFrom=start.ToDateTime(TimeOnly.MinValue);vm.BodyTo=start.AddDays(31).ToDateTime(TimeOnly.MinValue);vm.QueryBodyCommand.Execute(null);((TabControl)window.FindName("BodyViews")).SelectedIndex=1;Pump();
        var weight=(BodyTrendChart)window.FindName("BodyWeightChart");var waist=(BodyTrendChart)window.FindName("BodyWaistChart");
        Assert.AreEqual(3,weight.Series.Count);Assert.AreEqual(2,waist.Series.Count);Assert.AreEqual("kg",weight.Series.Unit);Assert.AreEqual("cm",waist.Series.Unit);Assert.IsNull(waist.Series.Points[1].Value);
        var area=new Rect(0,0,310,100);Assert.AreEqual(10.0,weight.Series.Position(weight.Series.Points[1],area).X,.001);Assert.AreEqual(310.0,weight.Series.Position(weight.Series.Points[2],area).X,.001);
        Assert.IsGreaterThan(0m,weight.Series.Maximum-weight.Series.Minimum);Assert.AreEqual(5,Lines(weight));Assert.AreEqual(3,Lines(waist));
        var before=weight.Series;vm.BodyFrom=DateTime.Today.AddDays(1);vm.QueryBodyCommand.Execute(null);Assert.AreSame(before,weight.Series);Assert.HasCount(3,vm.BodyRows);
        vm.BodyFrom=start.ToDateTime(TimeOnly.MinValue);vm.BodyDate=start.AddDays(2).ToDateTime(TimeOnly.MinValue);vm.BodyWeight="68";vm.BodyWaist="78";vm.SaveBodyCommand.Execute(null);Pump();Assert.AreEqual(4,weight.Series.Count);Assert.AreEqual(3,waist.Series.Count);
        vm.SelectedBody=vm.BodyRows.Single(x=>x.Date==start.AddDays(2));vm.EditBodyCommand.Execute(null);vm.BodyWeight="67";vm.SaveBodyCommand.Execute(null);Pump();Assert.AreEqual(67m,weight.Series.Points.Single(x=>x.Date==start.AddDays(2)).Value);
        vm.SelectedBody=vm.BodyRows.Single(x=>x.Date==start.AddDays(2));Assert.AreEqual(1,ConfirmationChecks.Respond(()=>vm.DeleteBodyCommand.Execute(null),true));Pump();Assert.AreEqual(3,weight.Series.Count);
        var snapshot=store.ReadSnapshot();backups.Restore(snapshot with {BodyMeasurements=[]});vm.QueryBodyCommand.Execute(null);Pump();Assert.AreEqual(0,weight.Series.Count);Assert.AreEqual(0,waist.Series.Count);
        backups.Restore(snapshot);vm.QueryBodyCommand.Execute(null);Pump();Assert.AreEqual(3,weight.Series.Count);
        vm.BodyTo=start.ToDateTime(TimeOnly.MinValue);vm.QueryBodyCommand.Execute(null);Pump();Assert.AreEqual(1,weight.Series.Count);Assert.IsGreaterThan(0m,weight.Series.Maximum-weight.Series.Minimum);Assert.AreEqual(155.0,weight.Series.Position(weight.Series.Points[0],area).X,.001);
        vm.BodyFrom=start.AddDays(1).ToDateTime(TimeOnly.MinValue);vm.BodyTo=vm.BodyFrom;vm.QueryBodyCommand.Execute(null);Pump();Assert.AreEqual(0,waist.Series.Count);Assert.AreEqual(1,weight.Series.Count);
        var checks=new List<object>();var page=(Grid)window.Content;
        foreach(var scale in new[]{1.0,1.25,1.5,2.0})
        {
            window.Width=920*scale;window.Height=600*scale;page.LayoutTransform=new ScaleTransform(scale,scale);Pump();
            foreach(var name in new[]{"BodyTrendTab","BodyWeightChart","BodyWaistChart"})
            {var control=(FrameworkElement)window.FindName(name);control.BringIntoView();Pump();Assert.IsTrue(control.IsVisible&&control.ActualWidth>100&&control.ActualHeight>0);var rect=control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));Assert.IsTrue(rect.Left>=0&&rect.Right<=window.ActualWidth+1&&rect.Top>=0&&rect.Bottom<=window.ActualHeight+1,name+"无法完整浏览："+rect);checks.Add(new{Scale=scale,Control=name,Bounds=rect.ToString()});}
        }
        page.LayoutTransform=Transform.Identity;window.Width=1240;window.Height=820;vm.BodyFrom=start.ToDateTime(TimeOnly.MinValue);vm.BodyTo=start.AddDays(31).ToDateTime(TimeOnly.MinValue);vm.QueryBodyCommand.Execute(null);Pump();
        window.Close();var restarted=new MainViewModel(new JournalService(new SqliteJournalStore(store.DatabasePath)),backups,root);restarted.BodyFrom=vm.BodyFrom;restarted.BodyTo=vm.BodyTo;restarted.QueryBodyCommand.Execute(null);Assert.AreEqual(3,new BodyTrendSeries(restarted.BodyRows,false).Count);
        JsonFiles.WriteNew(Path.Combine(root,"body-trend-checks.json"),new{Success=true,ActualSqliteAndWpf=true,ActualDateSpacing=true,MissingWaistBreaksLine=true,SingleConstantAndEmpty=true,CrudAndAppliedRange=true,FullBackupRestoreAndRestart=true,LayoutChecks=checks});
        static BodyMeasurement Measurement(DateOnly date,long weight,long? waist)=>new(){Id=Guid.NewGuid(),Date=date,WeightKgHundredths=weight,WaistCmTenths=waist};
        static int Lines(BodyTrendChart chart)=>VisualTreeHelper.GetDrawing(chart).Children.OfType<GeometryDrawing>().Count(x=>x.Geometry is LineGeometry);
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
}
