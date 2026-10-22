using System.Security.Cryptography;
using CalorieJournal.Core;

namespace CalorieJournal.Infrastructure;

public sealed record ChineseFoodDescription(string SourceId, string Name, string Aliases, string Preparation);

public sealed record ChineseFoodIndex(int FormatVersion, string Version, string Provider, string NutritionVersion,
    string CatalogSha256, string TermsSha256, string AliasesSha256, string Method, ChineseFoodDescription[] Foods)
{
    public static FoodReferenceCatalog Apply(FoodReferenceCatalog catalog, string indexPath, string catalogPath)
    {
        var index = JsonFiles.Read<ChineseFoodIndex>(indexPath);
        Validation.Require(index.FormatVersion == 1 && index.Provider == catalog.Provider && index.NutritionVersion == catalog.Version,
            "中文索引与营养目录版本不匹配，请使用完整程序包。");
        Validation.Text(index.Version, "中文索引版本", 100, true);
        Validation.Text(index.Method, "中文索引整理说明", 500, true);
        using var source = File.OpenRead(catalogPath);
        Validation.Require(index.CatalogSha256 == Convert.ToHexString(SHA256.HashData(source)), "营养目录与中文索引校验不一致。");
        Validation.Require(index.TermsSha256 == FoodSearch.TermsSha256 && index.AliasesSha256 == FoodSearch.AliasesSha256,
            "中文索引与程序中的食材或别名词表不匹配，请使用完整程序包。");
        Validation.Require(index.Foods is not null && index.Foods.Length == catalog.Foods.Length, "中文索引条目数不匹配。");
        var byId = new Dictionary<string, ChineseFoodDescription>(StringComparer.Ordinal);
        foreach (var item in index.Foods)
        {
            Validation.Require(item is not null && !string.IsNullOrWhiteSpace(item.SourceId) && byId.TryAdd(item.SourceId, item), "中文索引来源编号为空或重复。");
            Validation.Text(item.Name, "中文食物名称", 100, true); Validation.Text(item.Aliases, "中文食物别名", 500);
            Validation.Text(item.Preparation, "中文食物状态", 200);
            Validation.Require(item.Name.Any(c => c is >= '\u3400' and <= '\u9fff'), "中文索引缺少食物描述。");
        }
        var foods = catalog.Foods.Select(food =>
        {
            Validation.Require(byId.TryGetValue(food.SourceId, out var translated), "中文索引缺少来源食物。");
            return food with { Name = translated.Name, Aliases = translated.Aliases, Preparation = translated.Preparation,
                SourceNote = food.SourceNote + $"；中文检索索引{index.Version}：{index.Method}" };
        }).ToArray();
        var result = catalog with { Foods = foods, ChineseIndexVersion = index.Version };
        result.Validate();
        return result;
    }
}
