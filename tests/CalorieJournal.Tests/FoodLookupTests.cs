using System.IO.Compression;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class FoodLookupTests
{
    private string directory = "";
    private FoodLookupService lookup = null!;
    private FoodReferenceCatalog[] catalogs = [];
    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("请指定隔离测试目录。"), "lookup_" + Guid.NewGuid().ToString("N"));
        lookup = new FoodLookupService(Path.Combine(AppContext.BaseDirectory, "data", "lookup"), Path.Combine(directory, "lookup"));
        catalogs = lookup.Load();
    }

    [TestMethod]
    public void OfficialCatalogsSearchAcrossLanguagesAndKeepSourcesSeparate()
    {
        Assert.AreEqual(7793, catalogs[0].Foods.Length); Assert.AreEqual(2213, catalogs[1].Foods.Length);
        var hits = FoodReferenceSearch.Search(catalogs, FoodLookupService.Simplify("香蕉"), "全部来源", out var count);
        Assert.IsTrue(count >= 2); Assert.AreEqual(2, hits.Select(x => x.Provider).Distinct().Count());
        Assert.IsTrue(hits.All(x => x.SourceUrl.StartsWith("https://")));
        Assert.IsNotEmpty(FoodReferenceSearch.Search(catalogs, FoodLookupService.Simplify("雞蛋"), "TFDA", out _));
        Assert.IsNotEmpty(FoodReferenceSearch.Search(catalogs, "chicken breast raw", "USDA SR Legacy", out _));
        Assert.IsTrue(FoodReferenceSearch.Search(catalogs, "香蕉", "TFDA", out _).All(x => x.Provider == "TFDA"));
        Assert.IsEmpty(FoodReferenceSearch.Search(catalogs, "明确无匹配的查询测试", "全部来源", out _));
        Assert.Throws<ValidationException>(() => FoodReferenceSearch.Search(catalogs, " ", "全部来源", out _));
        var broad = FoodReferenceSearch.Search(catalogs, "a", "全部来源", out var broadCount);
        Assert.IsTrue(broadCount > broad.Length); Assert.IsTrue(broad.GroupBy(x => x.Provider).All(x => x.Count() <= 100));
    }

    [TestMethod]
    public void ReferencedFoodsPersistWithProvenanceAndBackupWithoutChangingSeed()
    {
        var store = new SqliteJournalStore(Path.Combine(directory, "journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data", "foods.json")));
        var journal = new JournalService(store); Assert.HasCount(60, journal.Foods());
        foreach (var provider in new[] { "USDA SR Legacy", "TFDA" })
        {
            var hit = FoodReferenceSearch.Search(catalogs, "香蕉", provider, out _).First();
            var food = hit.CreateFood() with { Name = "来源验证 " + provider + " " + hit.Name };
            journal.SaveFood(food);
            var selected = journal.Foods().Single(x => x.Id == food.Id);
            Assert.AreEqual(hit.Food.OriginalName, selected.OriginalName); Assert.AreEqual(hit.SourceUrl, selected.SourceUrl);
            Assert.AreEqual(hit.Catalog.Version, selected.SourceVersion); Assert.AreEqual(hit.Food.SourceId, selected.SourceId);
            var meal = journal.Meals()[0];
            journal.SaveEntry(new IntakeEntry { Id = Guid.NewGuid(), EntryDate = DateOnly.FromDateTime(DateTime.Today), MealCategoryId = meal.Id,
                MealNameSnapshot = meal.Name, FoodId = food.Id, FoodNameSnapshot = food.Name, PreparationSnapshot = food.Preparation,
                InputMode = "weight", WeightGramsTenths = 1500, KcalPer100gHundredthsSnapshot = food.KcalPer100gHundredths,
                EnergyKcalTenths = Validation.CalculateEnergy(1500, food.KcalPer100gHundredths), SourceSnapshot = food.SourceSummary + " " + food.SourceUrl });
        }
        var backups = new BackupService(store, Path.Combine(directory, "recovery"));
        var path = Path.Combine(directory, "lookup.calorie-backup.json"); backups.Export(path);
        var before = store.ReadSnapshot(); backups.Restore(backups.Inspect(path));
        Assert.AreEqual(JsonSerializer.Serialize(before.Foods), JsonSerializer.Serialize(store.ReadSnapshot().Foods));
        Assert.AreEqual(JsonSerializer.Serialize(before.Entries), JsonSerializer.Serialize(store.ReadSnapshot().Entries));
        Assert.HasCount(62, new JournalService(new SqliteJournalStore(store.DatabasePath)).Foods());
    }

    [TestMethod]
    public void CatalogRejectsDuplicatesNullFieldsAndUnsafeSourceIds()
    {
        var catalog = catalogs[1]; var first = catalog.Foods[0];
        Assert.Throws<ValidationException>(() => (catalog with { Foods = [first, first] }).Validate());
        Assert.Throws<ValidationException>(() => (catalog with { Foods = [first with { Aliases = null! }] }).Validate());
        Assert.Throws<ValidationException>(() => (catalog with { Foods = [first with { SourceId = "../invalid" }] }).Validate());
        Assert.Throws<ValidationException>(() => (catalog with { Foods = [first with { KcalPer100gHundredths = -1 }] }).Validate());
        Assert.Throws<ValidationException>(() => (catalog with { Foods = [] }).Validate());
    }

    [TestMethod]
    public async Task RealTfdaSampleUsesOnlyOrdinaryEnergyAndRejectsInvalidUnitsAndMissingValues()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "tfda-sample.zip");
        await using var stream = File.OpenRead(fixture);
        var parsed = await FoodLookupService.ReadTfdaAsync(stream, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.IsNotEmpty(parsed.Foods);
        foreach (var item in parsed.Foods)
            Assert.AreEqual(catalogs[1].Foods.Single(x => x.SourceId == item.SourceId).KcalPer100gHundredths, item.KcalPer100gHundredths);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        using var json = JsonDocument.Parse(archive.Entries[0].Open());
        var official = json.RootElement.EnumerateArray().First(x => x.GetProperty("分析項").GetString() == "熱量");
        foreach (var (key, value) in new[] { ("含量單位", "kJ"), ("每100克含量", "") })
        {
            // 在真实公开行上修改一个无效输入字段，验证解析边界；不替换HTTP或存储实现。
            var invalid = official.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.Null ? null : x.Value.GetString());
            invalid[key] = value;
            using var zip = new MemoryStream();
            using (var writer = new ZipArchive(zip, ZipArchiveMode.Create, true))
            {
                using var content = writer.CreateEntry("invalid-input.json").Open();
                JsonSerializer.Serialize(content, new[] { invalid });
            }
            zip.Position = 0;
            await Assert.ThrowsAsync<ValidationException>(() => FoodLookupService.ReadTfdaAsync(zip, DateTimeOffset.UtcNow, CancellationToken.None));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); stream.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => FoodLookupService.ReadTfdaAsync(stream, DateTimeOffset.UtcNow, canceled.Token));
    }

    [TestMethod]
    public void DamagedCacheIsVisibleAndExplicitBundledReadKeepsCacheUnchanged()
    {
        var cache = Path.Combine(directory, "lookup"); Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, "tfda.json"); File.WriteAllText(path, "明确损坏的缓存输入");
        Assert.Throws<JsonException>(() => lookup.Load());
        Assert.HasCount(2, lookup.Load(true)); Assert.AreEqual("明确损坏的缓存输入", File.ReadAllText(path));
        Assert.IsTrue(catalogs.SelectMany(x => x.Foods).Any(x => x.KcalPer100gHundredths == 0));
    }
}
