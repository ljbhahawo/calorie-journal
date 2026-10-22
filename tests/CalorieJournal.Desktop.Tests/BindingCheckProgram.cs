using System.IO;
using System.Windows.Threading;

namespace CalorieJournal.Desktop.Tests;

public static class BindingCheckProgram
{
    [STAThread]
    public static int Main()
    {
        var directory = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("未配置隔离测试目录。");
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "binding-check-result.txt");
        var result = 1;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                new BindingTests().RealControlsBindThroughViewModelIntoSqliteAndRefreshViews();
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
