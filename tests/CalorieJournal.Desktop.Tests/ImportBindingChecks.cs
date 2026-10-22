using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CalorieJournal.Core;
using CalorieJournal.Desktop;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class ImportBindingChecks
{
    public static async Task Verify()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("未配置测试目录。" );
        var directory = Path.Combine(root,"import_wpf_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory,"journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory,"data","foods.json")));
        var journal = new JournalService(store);
        var backups = new BackupService(store,Path.Combine(directory,"recovery"));
        var vm = new ImportViewModel(journal,backups);
        var window = new ImportWindow(vm); window.Show(); Pump();
        ((TextBox)window.FindName("ImportYearBox")).Text = "2020";
        ((TextBox)window.FindName("ImportRawBox")).Text = "10.17\n早餐约95大卡\n- 明确导入验收甲约80大卡\n- 明确导入验收乙约10—20大卡";
        Pump(); ((Button)window.FindName("ParseImportButton")).Command.Execute(null); Pump();
        Assert.AreEqual(3,((DataGrid)window.FindName("ImportGrid")).Items.Count);
        Assert.AreEqual(2,vm.Rows.Count(x=>x.Included));
        Assert.IsEmpty(store.ReadSnapshot().Entries);
        vm.SelectedRow=vm.Rows.Last();
        ((Button)window.FindName("MiddleImportButton")).Command.Execute(null); Pump();
        Assert.AreEqual("15.0",vm.SelectedRow.EnergyText);
        ((Button)window.FindName("ValidateImportButton")).Command.Execute(null); Pump();
        Assert.IsTrue(vm.Status.StartsWith("校验通过"));
        ((CheckBox)window.FindName("ImportReviewedBox")).IsChecked=true; Pump();
        Assert.IsTrue(((Button)window.FindName("CommitImportButton")).IsEnabled);

        var checks = new List<object>();
        var grid = (Grid)window.Content;
        foreach (var factor in new[] {1.0,1.25,1.5,2.0})
        {
            window.Width=920*factor; window.Height=600*factor; grid.LayoutTransform=new ScaleTransform(factor,factor);
            foreach(var (tab,names) in new[]
            {
                (0,new[]{"ParseImportButton","CloseImportButton"}),
                (1,new[]{"PreferDetailsButton","PreferTotalsButton","LowerImportButton","MiddleImportButton","UpperImportButton","ValidateImportButton","CommitImportButton","CloseImportButton"})
            })
            {
                vm.TabIndex=tab; Pump(); window.UpdateLayout();
                foreach(var name in names)
                {
                    var button=(Button)window.FindName(name); button.BringIntoView(); Pump();
                    var bounds=button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(button.IsVisible && bounds.Width>0 && bounds.Height>0 && bounds.Left>=0 && bounds.Right<=window.ActualWidth+1 && bounds.Top>=0 && bounds.Bottom<=window.ActualHeight+1,$"导入窗口缩放{factor}下{name}不可完整访问：{bounds}");
                    checks.Add(new{Scale=factor,Tab=tab,Control=name});
                }
                if(tab==1) Assert.IsTrue(((DataGrid)window.FindName("ImportGrid")).ActualHeight>=86,$"最小窗口须保留表头及一行数据，缩放{factor}时实际高度{((DataGrid)window.FindName("ImportGrid")).ActualHeight}。");
            }
        }
        grid.LayoutTransform=Transform.Identity; window.Width=1180; window.Height=800; Pump();
        await vm.CommitCommand.ExecuteAsync(null); Pump();
        Assert.IsTrue(vm.Imported,vm.Status);
        Assert.HasCount(2,store.ReadSnapshot().Entries);
        Assert.AreEqual(950L,store.ReadSnapshot().Entries.Sum(x=>x.EnergyKcalTenths));
        Assert.IsTrue(File.Exists(vm.RecoveryPath));
        Assert.IsEmpty(backups.Inspect(vm.RecoveryPath).Entries);
        await vm.CommitCommand.ExecuteAsync(null);
        Assert.HasCount(2,store.ReadSnapshot().Entries);
        var retry=new ImportViewModel(journal,backups){Year="2020",RawText=vm.RawText};
        retry.ParseCommand.Execute(null);
        Assert.IsTrue(retry.Rows.All(x=>!x.Included));
        retry.PreferTotalsCommand.Execute(null); retry.ValidateCommand.Execute(null);
        Assert.IsFalse(retry.CanCommit);
        Assert.IsTrue(retry.Rows.Any(x=>x.State.Contains("同餐已采用另一种")));
        window.Close();

        var blockedPath=Path.Combine(directory,"无法创建备份的明确验收路径");
        File.WriteAllText(blockedPath,"这是明确的备份故障测试文件");
        var failed=new ImportViewModel(journal,new BackupService(store,blockedPath))
        {
            Year="2020", RawText="2020-10-18\n午餐\n- 明确备份失败验收约20大卡"
        };
        failed.ParseCommand.Execute(null); failed.ValidateCommand.Execute(null); failed.ReviewConfirmed=true;
        await failed.CommitCommand.ExecuteAsync(null);
        Assert.IsFalse(failed.Imported); Assert.IsTrue(failed.Status.StartsWith("未完成导入"));
        Assert.HasCount(2,store.ReadSnapshot().Entries);
        File.WriteAllText(Path.Combine(directory,"import-checks.json"),JsonSerializer.Serialize(new
        {
            PasteAndParse=true,EditableReview=true,AtomicAppend=true,PreImportBackup=true,BackupFailurePreventsWrites=true,
            ReimportPrevented=true,CrossImportTotalOverlapPrevented=true,LayoutChecks=checks
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void Pump()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
}
