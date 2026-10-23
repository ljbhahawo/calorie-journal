using System.Globalization;
using System.Security.Cryptography;
using CalorieJournal.Core;

namespace CalorieJournal.Infrastructure;

public sealed record ProteinReference(string SourceId, string OriginalAmount, long? ProteinPer100gHundredths);
public sealed record ProteinSource(string Provider, string NutritionVersion, string ArchiveSha256, string CatalogSha256, string Analysis, string Unit, ProteinReference[] Foods);
public sealed record ProteinSupplement(int FormatVersion, string Version, ProteinSource[] Catalogs)
{
    public const string Sha256 = "5B6527ED1F9DA7F16635A2D5F03F3574C7F6CE063EB95068837533890C9B7F4F";
    public static FoodReferenceCatalog Apply(FoodReferenceCatalog catalog, string directory, bool bundled)
    {
        var supplementPath = Path.Combine(directory, "protein.json");
        using (var stream = File.OpenRead(supplementPath)) Validation.Require(Convert.ToHexString(SHA256.HashData(stream)) == Sha256, "蛋白质补充文件校验不一致，请使用完整程序包。");
        var supplement = JsonFiles.Read<ProteinSupplement>(supplementPath);
        Validation.Require(supplement.FormatVersion == 1 && supplement.Version == "protein-2026-10-23.1" && supplement.Catalogs is { Length: 2 } && supplement.Catalogs.Select(x => x.Provider).Distinct().Count() == 2, "蛋白质补充目录版本或来源无效。");
        var source = supplement.Catalogs.SingleOrDefault(x => x.Provider == catalog.Provider);
        Validation.Require(source is not null && source.Unit == "g/100g" && source.Analysis == (catalog.Provider == "TFDA" ? "粗蛋白/g" : "1003 Protein/G"), "蛋白质来源字段或单位不符。");
        if (bundled)
        {
            var path = Path.Combine(directory, catalog.Provider == "TFDA" ? "tfda.json" : "usda.json"); using var stream = File.OpenRead(path);
            Validation.Require(source.CatalogSha256 == Convert.ToHexString(SHA256.HashData(stream)), "蛋白质与原热量目录校验不一致。");
        }
        if (source.NutritionVersion != catalog.Version || !source.ArchiveSha256.Equals(catalog.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        { Validation.Require(!bundled, "蛋白质与内置官方原档版本不符。"); return catalog; }
        Validation.Require(source.Foods is not null && source.Foods.Length == catalog.Foods.Length && source.Foods.All(x => x is not null) && source.Foods.Select(x => x.SourceId).Distinct().Count() == source.Foods.Length, "蛋白质目录数量或编号无效。");
        var byId = source.Foods.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        foreach (var value in source.Foods)
        {
            Nutrition.UnitProtein(value.ProteinPer100gHundredths);
            Validation.Require(value.OriginalAmount is not null && (value.ProteinPer100gHundredths is null ? value.OriginalAmount.Length == 0 : decimal.TryParse(value.OriginalAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount is >= 0 and <= 100 && Nutrition.Round(amount * 100) == value.ProteinPer100gHundredths), "蛋白质原始数值与补充值不符。");
        }
        return catalog with { Foods = catalog.Foods.Select(food =>
        {
            Validation.Require(byId.TryGetValue(food.SourceId, out var protein), "蛋白质目录缺少来源编号。");
            return food with { ProteinPer100gHundredths = protein.ProteinPer100gHundredths,
                SourceNote = food.SourceNote + $"；蛋白质分析字段{source.Analysis}，同一官方原档，每100克" + (protein.ProteinPer100gHundredths is null ? "缺失" : $"原值{protein.OriginalAmount}g") };
        }).ToArray() };
    }
    public static FoodPackage EnrichBuiltin(FoodPackage package, string directory)
    {
        var path = Path.Combine(directory, "usda.json");
        var catalog = Apply(JsonFiles.Read<FoodReferenceCatalog>(path), directory, true); var byId = catalog.Foods.ToDictionary(x => x.SourceId);
        return package with { Foods = package.Foods.Select(food =>
        {
            Validation.Require(food.SourceKind == "USDA SR Legacy" && food.SourceVersion == catalog.Version && byId.TryGetValue(food.SourceId, out _), "内置食物与蛋白质来源不匹配。");
            return food with { ProteinPer100gHundredths = byId[food.SourceId].ProteinPer100gHundredths };
        }).ToArray() };
    }
}
