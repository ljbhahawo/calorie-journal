using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class ChineseSearchTests
{
    public TestContext TestContext { get; set; } = null!;
    private sealed record QueryCase(string Query, string OriginalPattern);

    [TestMethod]
    public void ChineseQueriesFindExpectedOfficialFoodsAndKeepNutritionUnchanged()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "data", "lookup");
        var service = new FoodLookupService(directory, Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "chinese_unused_cache"));
        var catalogs = service.Load(true);
        var official = JsonFiles.Read<FoodReferenceCatalog>(Path.Combine(directory, "usda.json"));
        var byId = catalogs[0].Foods.ToDictionary(x => x.SourceId);
        foreach (var source in official.Foods)
        {
            var translated = byId[source.SourceId];
            Assert.AreEqual(source.OriginalName, translated.OriginalName);
            Assert.AreEqual(source.KcalPer100gHundredths, translated.KcalPer100gHundredths);
            Assert.IsTrue(translated.Name.Any(c => c is >= '\u3400' and <= '\u9fff'));
        }
        var cases = JsonFiles.Read<QueryCase[]>(Path.Combine(AppContext.BaseDirectory, "data", "search", "query-cases.json"));
        Assert.IsTrue(cases.Length >= 100);
        var results = new List<object>(); var failures = new List<string>();
        foreach (var item in cases)
        {
            // 期望来自独立审核的英文食物描述，未用中文映射或搜索实现反推答案。
            var expected = official.Foods.Where(x => Regex.IsMatch(x.OriginalName, item.OriginalPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(x => x.SourceId).ToHashSet();
            if (expected.Count == 0) { failures.Add($"真实目录中没有验收食物：{item.Query} / {item.OriginalPattern}"); continue; }
            var watch = Stopwatch.StartNew();
            var found = FoodReferenceSearch.Search(catalogs, item.Query, "USDA SR Legacy", out var total);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var ids = found.Where(x => expected.Contains(x.Food.SourceId)).Select(x => x.Food.SourceId).ToArray();
            if (ids.Length == 0) failures.Add($"中文查询未找到预期来源：{item.Query} / {item.OriginalPattern}；返回{total}项");
            results.Add(new { item.Query, item.OriginalPattern, ExpectedSourceIds = expected.Order().ToArray(), MatchedSourceIds = ids, Milliseconds = elapsed, Total = total });
        }
        var reportPath = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "chinese-search-cases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Version = catalogs[0].ChineseIndexVersion, Count = cases.Length, Failures = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.WriteLine($"中文真实目录查询{cases.Length}例，失败{failures.Count}；报告：{reportPath}");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void PersonalLibrarySearchNormalizesAliasesAndDoesNotRewriteUserValues()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "personal_chinese_" + Guid.NewGuid().ToString("N"));
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json")));
        var journal = new JournalService(store);
        var before = store.ReadSnapshot();
        Assert.IsNotEmpty(journal.Foods("馬鈴薯 生"));
        Assert.IsNotEmpty(journal.Foods("西紅柿"));
        Assert.IsNotEmpty(journal.Foods("雞胸肉生"));
        Assert.IsNotEmpty(journal.Foods("chicken\tbreast\nraw"));
        Assert.IsEmpty(journal.Foods("明确没有这种食物"));
        var after = store.ReadSnapshot();
        CollectionAssert.AreEqual(before.Foods, after.Foods);
        CollectionAssert.AreEqual(before.Meals, after.Meals);
        CollectionAssert.AreEqual(before.Entries, after.Entries);
        Assert.AreEqual(before.FoodPackageVersion, after.FoodPackageVersion);
        var potato = journal.Foods("马铃薯 生").First();
        journal.SaveFood(potato with { Name = "自定土豆名称", Aliases = "我的自定义别名", UserModified = true });
        var updated = journal.Foods("我的自定义别名").Single();
        Assert.AreEqual("自定土豆名称", updated.Name);
        Assert.IsTrue(journal.Foods("马铃薯").Any(x => x.Id == updated.Id));
    }

    [TestMethod]
    public void AliasesKeepDistinctStatesAndLongestChineseWords()
    {
        Assert.IsFalse(FoodSearch.PrepareDocument("生菜", "").Matches(FoodSearch.PrepareQuery("生")));
        Assert.IsFalse(FoodSearch.PrepareDocument("花生", "").Matches(FoodSearch.PrepareQuery("生")));
        Assert.IsFalse(FoodSearch.PrepareDocument("原味不加糖酸奶", "").Matches(FoodSearch.PrepareQuery("加糖")));
        Assert.IsFalse(FoodSearch.PrepareDocument("低脂牛奶", "").Matches(FoodSearch.PrepareQuery("脱脂")));
        Assert.IsFalse(FoodSearch.PrepareDocument("蛋白质粉", "").Matches(FoodSearch.PrepareQuery("蛋清")));
        Assert.IsFalse(FoodSearch.PrepareDocument("牛番茄", "").Matches(FoodSearch.PrepareQuery("牛肉")));
        Assert.IsFalse(FoodSearch.PrepareDocument("羊角椒", "").Matches(FoodSearch.PrepareQuery("羊肉")));
        Assert.IsFalse(FoodSearch.PrepareDocument("安心奶粉", "").Matches(FoodSearch.PrepareQuery("心脏")));
        Assert.IsTrue(FoodSearch.PrepareDocument("自定义鸭腿（熟）", "").Matches(FoodSearch.PrepareQuery("鸭腿 熟")));
        Assert.IsTrue(FoodSearch.PrepareDocument("鸭腿（熟）", "").Matches(FoodSearch.PrepareQuery("鸭腿熟")));
        Assert.IsTrue(FoodSearch.PrepareDocument("鸡肝", "").Matches(FoodSearch.PrepareQuery("鸡肝")));
        Assert.IsFalse(FoodSearch.PrepareDocument("不加糖酸奶", "").Matches(FoodSearch.PrepareQuery("加糖酸奶")));
        Assert.IsFalse(FoodSearch.PrepareDocument("Strawberry preserves", "").Matches(FoodSearch.PrepareQuery("raw")));
        Assert.IsFalse(FoodSearch.PrepareDocument("生菜", "").Matches(FoodSearch.PrepareQuery("！！！")));
        Assert.IsTrue(FoodSearch.PrepareDocument("去皮鸡胸肉", "").Matches(FoodSearch.PrepareQuery("無皮雞脯")));
        var query = FoodSearch.PrepareQuery("马铃薯");
        Assert.IsTrue(FoodSearch.PrepareDocument("土豆", "马铃薯").Rank(query) < FoodSearch.PrepareDocument("土豆泥配菜", "").Rank(query));
    }

    [TestMethod]
    public void ChineseIndexMismatchIsRejectedWithoutChangingCatalog()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "data", "lookup");
        var source = JsonFiles.Read<FoodReferenceCatalog>(Path.Combine(directory, "usda.json"));
        var original = JsonSerializer.Serialize(source);
        var wrong = source with { Version = "不存在的营养版本" };
        Assert.Throws<ValidationException>(() => ChineseFoodIndex.Apply(wrong, Path.Combine(directory, "usda-zh.json"), Path.Combine(directory, "usda.json")));
        Assert.AreEqual(original, JsonSerializer.Serialize(source));
    }

    [TestMethod]
    public void NewTfdaCacheSerializationRetainsLegacyFormat()
    {
        var catalogs = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data", "lookup"), "unused").Load(true);
        var directory = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!, "chinese_cache_compat_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "tfda.json");
        JsonFiles.WriteNew(path, catalogs[1]);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.IsFalse(json.RootElement.TryGetProperty("ChineseIndexVersion", out _));
        Assert.AreEqual(1, json.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.HasCount(2213, JsonFiles.Read<FoodReferenceCatalog>(path).Foods);
        TestContext.WriteLine("实际1.6类型序列化的公开TFDA缓存：" + path);
    }

    [TestMethod]
    public void RealTfdaTomatoesDoNotMatchBeefAndProteinKeepsItsFoodMeaning()
    {
        var catalogs = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data", "lookup"), "unused").Load(true);
        var tomatoes = catalogs[1].Foods.Where(x => x.Name.Contains("牛番茄")).ToArray();
        Assert.IsNotEmpty(tomatoes);
        foreach (var food in tomatoes)
            Assert.IsFalse(FoodSearch.PrepareDocument(food.Name, food.Aliases, food.OriginalName, food.Preparation, food.Category).Matches(FoodSearch.PrepareQuery("牛肉")));
        var protein = FoodReferenceSearch.Search(catalogs, "蛋白粉", "USDA SR Legacy", out _);
        Assert.IsNotEmpty(protein);
        Assert.IsTrue(protein.All(x => Regex.IsMatch(x.Food.OriginalName, "protein", RegexOptions.IgnoreCase)));
        var chard = catalogs[0].Foods.Where(x => x.OriginalName.StartsWith("Chard, swiss", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsNotEmpty(chard);
        Assert.IsTrue(chard.All(x => !x.Name.Contains("奶酪") && !x.Aliases.Contains("奶酪")));
        var roundPizzas = catalogs[0].Foods.Where(x => x.OriginalName.Contains("round", StringComparison.OrdinalIgnoreCase) && x.OriginalName.Contains("pizza", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.IsNotEmpty(roundPizzas); Assert.IsTrue(roundPizzas.All(x => !x.Name.Contains("后腿")));
    }

    [TestMethod]
    public void RegionalPotatoAliasesDoNotTurnPeanutsIntoPotatoesOrMergeFishSpecies()
    {
        var catalogs = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data", "lookup"), "unused").Load(true);
        var potatoes = FoodReferenceSearch.Search(catalogs, "马铃薯 生", "TFDA", out _);
        Assert.IsNotEmpty(potatoes);
        Assert.IsTrue(potatoes.All(x => !x.Name.Contains("花生")));
        Assert.IsTrue(FoodReferenceSearch.Search(catalogs, "土豆 生", "TFDA", out _).Any(x => x.Food.SourceId == "C1700101"));
        var salmon = FoodReferenceSearch.Search(catalogs, "大西洋鲑 生", "USDA SR Legacy", out _);
        Assert.IsNotEmpty(salmon);
        Assert.IsTrue(salmon.All(x => x.Food.OriginalName.Contains("Atlantic", StringComparison.OrdinalIgnoreCase)));
        var cod = FoodReferenceSearch.Search(catalogs, "大西洋鳕 生", "USDA SR Legacy", out _);
        Assert.IsNotEmpty(cod);
        Assert.IsTrue(cod.All(x => x.Food.OriginalName.Contains("Atlantic", StringComparison.OrdinalIgnoreCase)));
    }
}
