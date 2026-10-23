using System.IO;
using System.Windows.Threading;

namespace CalorieJournal.Desktop.Tests;

public static class BindingCheckProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        var directory = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("未配置隔离测试目录。");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "binding-check-result.txt");
        var result = 1;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args is ["--migration-check", var legacyDatabase])
                {
                    MigrationChecks.Verify(legacyDatabase);
                    File.WriteAllText(reportPath, "PASS: 实际旧版数据库迁移、升级前备份及真实失败保护通过。");
                    result = 0; return;
                }
                if (args is ["--lookup-online"])
                {
                    await LookupBindingChecks.VerifyOnline();
                    File.WriteAllText(reportPath, "PASS: 实际官方联网、缓存重读、取消和真实缓存写入失败检查通过。");
                    result = 0; return;
                }
                new BindingTests().RealControlsBindThroughViewModelIntoSqliteAndRefreshViews();
                await ImportBindingChecks.Verify();
                BrowseBindingChecks.Verify();
                LookupBindingChecks.Verify();
                WeightBindingChecks.Verify();
                TemplateBindingChecks.Verify();
                SettingsMealBindingChecks.Verify();
                BodyHierarchyBindingChecks.Verify();
                StatisticsBindingChecks.Verify();
                PersonalBindingChecks.Verify();
                NutritionBindingChecks.Verify();
                File.WriteAllText(reportPath, "PASS: 独立STA进程中的真实WPF绑定、用例和SQLite检查全部通过。");
                result = 0;
            }
            catch (Exception exception) { File.WriteAllText(reportPath, exception.ToString()); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        }));
        Dispatcher.Run();
        return result;
    }
}
