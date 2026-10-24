using System.IO;
using System.Text.Json;
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
        var stages = new Dictionary<string, string>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args is ["--performance", var count])
                {
                    _ = new TestApplication(); PerformanceChecks.Verify(int.Parse(count, System.Globalization.CultureInfo.InvariantCulture));
                    File.WriteAllText(reportPath, "PASS: 真实SQLite与ViewModel规模测量完成，结果不冒充视觉性能。"); result = 0; return;
                }
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
                var checks = new (string Name, Func<Task> Action, string? Fixture, string[] Dependencies)[]
                {
                    ("bindings", () => Sync(() => new BindingTests().RealControlsBindThroughViewModelIntoSqliteAndRefreshViews()), null, []),
                    ("import", ImportBindingChecks.Verify, null, ["bindings"]),
                    ("browse", () => Sync(BrowseBindingChecks.Verify), null, ["bindings"]),
                    ("lookup", () => Sync(LookupBindingChecks.Verify), null, ["bindings"]),
                    ("weight", () => Sync(WeightBindingChecks.Verify), null, ["bindings"]),
                    ("template", () => Sync(TemplateBindingChecks.Verify), "wpf_template_*", ["bindings"]),
                    ("settings", () => Sync(SettingsMealBindingChecks.Verify), null, ["bindings"]),
                    ("body", () => Sync(BodyHierarchyBindingChecks.Verify), "wpf_body_*", ["bindings"]),
                    ("statistics", () => Sync(StatisticsBindingChecks.Verify), "wpf_statistics_*", ["template", "body"]),
                    ("personal", () => Sync(PersonalBindingChecks.Verify), "wpf_personal_*", ["statistics"]),
                    ("nutrition", () => Sync(NutritionBindingChecks.Verify), "wpf_nutrition_*", ["personal"]),
                    ("sheet", () => Sync(SheetBindingChecks.Verify), "wpf_sheet_*", ["nutrition"])
                    ,("form", () => Sync(FormExperienceChecks.Verify), null, ["bindings"])
                    ,("trend", () => Sync(BodyTrendChecks.Verify), null, ["bindings"])
                };
                var requested = args is ["--check", var name] ? name : args.Length == 0 ? "all" : throw new InvalidOperationException("未知WPF检查参数。");
                var selected = new HashSet<string>();
                void Include(string name)
                {
                    var check = checks.Single(x => x.Name == name);
                    if (!selected.Add(name)) return;
                    foreach (var dependency in check.Dependencies) Include(dependency);
                }
                if (requested == "all") foreach (var check in checks) Include(check.Name); else Include(requested);
                foreach (var check in checks) stages[check.Name] = selected.Contains(check.Name) ? "未执行" : "未选择";
                foreach (var check in checks.Where(x => selected.Contains(x.Name)))
                {
                    stages[check.Name] = "执行中"; SaveStages();
                    var before = Directory.GetDirectories(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        await check.Action();
                        if (check.Fixture is not null) BindingFixtures.Register(check.Name, check.Fixture, before);
                        stages[check.Name] = "通过"; SaveStages();
                    }
                    catch { stages[check.Name] = "失败"; SaveStages(); throw; }
                }
                File.WriteAllText(reportPath, "PASS: 独立STA进程中的真实WPF绑定、用例和SQLite检查全部通过。");
                result = 0;
            }
            catch (Exception exception) { File.WriteAllText(reportPath, exception.ToString()); }
            finally { SaveStages(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        }));
        Dispatcher.Run();
        return result;
        void SaveStages() => File.WriteAllText(Path.Combine(directory, "wpf-stages.json"), JsonSerializer.Serialize(stages, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
}
