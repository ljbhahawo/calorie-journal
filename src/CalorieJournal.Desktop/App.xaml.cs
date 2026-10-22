using System.IO;
using System.Text.Json;
using System.Windows;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;

namespace CalorieJournal.Desktop;

public partial class App : Application
{
    private FileStream? instanceLock;
    private string? diagnosticDirectory;

    private void ReportFailure(Exception exception, string title)
    {
        if (diagnosticDirectory is not null)
            File.WriteAllText(Path.Combine(diagnosticDirectory, "startup-error.json"), JsonSerializer.Serialize(new
            {
                ExceptionType = exception.GetType().FullName, exception.Message, exception.StackTrace
            }, JsonFiles.Options));
        else
            MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFailure(args.Exception, "程序错误，应用将关闭");
            args.Handled = true;
            Shutdown(1);
        };
        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalorieJournal");
            var startupCheck = false;
            string? exportPath = null;
            string? restorePath = null;
            string? reportPath = null;
            var restoreConfirmed = false;
            for (var i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) dataDirectory = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--startup-check") startupCheck = true;
                else if (e.Args[i] == "--export-backup" && i + 1 < e.Args.Length) exportPath = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--restore-backup" && i + 1 < e.Args.Length) restorePath = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--report" && i + 1 < e.Args.Length) reportPath = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--confirm-restore") restoreConfirmed = true;
                else throw new ValidationException("未知参数。请查阅随包提供的使用说明。");
            }
            Directory.CreateDirectory(dataDirectory);
            if (startupCheck || exportPath is not null || restorePath is not null) diagnosticDirectory = dataDirectory;
            Validation.Require(exportPath is null || restorePath is null, "不能同时执行导出与恢复。");
            instanceLock = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var package = JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json"));
            var store = new SqliteJournalStore(Path.Combine(dataDirectory, "journal.db"));
            store.Initialize(package);
            var backupService = new BackupService(store, Path.Combine(dataDirectory, "recovery"));
            if (exportPath is not null || restorePath is not null)
            {
                string? recoveryPath = null;
                if (exportPath is not null) backupService.Export(exportPath);
                else
                {
                    Validation.Require(restoreConfirmed, "命令行恢复须明确提供 --confirm-restore；现有数据将先备份。");
                    recoveryPath = backupService.Restore(backupService.Inspect(restorePath!));
                }
                if (reportPath is not null) JsonFiles.WriteNew(reportPath, new { Success = true, RecoveryPath = recoveryPath, ExportPath = exportPath });
                Shutdown(); return;
            }
            var window = new MainWindow(new MainViewModel(new JournalService(store), backupService, dataDirectory));
            MainWindow = window;
            window.Show();
            if (startupCheck)
            {
                window.ContentRendered += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    File.WriteAllText(Path.Combine(dataDirectory, "startup-check.json"), JsonSerializer.Serialize(new
                    {
                        StartedAtUtc = DateTimeOffset.UtcNow, window.Title, window.IsVisible, window.ActualWidth, window.ActualHeight,
                        DpiX = System.Windows.Media.VisualTreeHelper.GetDpi(window).PixelsPerInchX,
                        DpiY = System.Windows.Media.VisualTreeHelper.GetDpi(window).PixelsPerInchY,
                        FoodCount = store.ReadFoods().Length, MealCount = store.ReadMeals().Length,
                        EntryCount = store.ReadSnapshot().Entries.Length
                    }, JsonFiles.Options));
                    Shutdown();
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ValidationException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            ReportFailure(ex, "启动失败，请检查权限、数据版本或运行实例");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceLock?.Dispose();
        base.OnExit(e);
    }
}
