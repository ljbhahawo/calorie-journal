using System.IO;

namespace CalorieJournal.Desktop.Tests;

internal static class BindingFixtures
{
    private static readonly Dictionary<string, string> Paths = [];
    public static string Get(string name) => Paths.TryGetValue(name, out var path) ? path : throw new InvalidOperationException($"本次运行未生成{name}资料，请执行明确的前置检查。");
    public static void Register(string name, string pattern, HashSet<string> before)
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!;
        var created = Directory.GetDirectories(root, pattern).Where(x => !before.Contains(x)).ToArray();
        if (created.Length != 1) throw new InvalidOperationException($"{name}必须在本次阶段生成唯一资料目录。");
        Paths.Add(name, Path.Combine(created[0], "journal.db"));
    }
}
