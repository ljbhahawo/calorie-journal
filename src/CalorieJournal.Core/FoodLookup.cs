using System.Text;

namespace CalorieJournal.Core;

public sealed record FoodReference
{
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public string OriginalName { get; init; } = "";
    public string Aliases { get; init; } = "";
    public string Preparation { get; init; } = "";
    public string Category { get; init; } = "";
    public required long KcalPer100gHundredths { get; init; }
    public string SourceNote { get; init; } = "";
}

public sealed record FoodReferenceCatalog
{
    public int FormatVersion { get; init; } = 1;
    public required string Provider { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset RetrievedAtUtc { get; init; }
    public required string SourceUrl { get; init; }
    public required string LicenseUrl { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required FoodReference[] Foods { get; init; }

    public void Validate()
    {
        Validation.Require(FormatVersion == 1, "不支持此参考目录格式。请更新软件。");
        Validation.Require(Provider is "USDA SR Legacy" or "TFDA", "参考目录来源无效。");
        Validation.Require(!string.IsNullOrWhiteSpace(Version) && Version.Length <= 100 && RetrievedAtUtc != default, "参考目录版本无效。");
        Validation.Require(SourceUrl == (Provider == "TFDA" ? "https://data.gov.tw/dataset/8543" : "https://fdc.nal.usda.gov/download-datasets/"), "参考目录网址无效。");
        Validation.Require(LicenseUrl == (Provider == "TFDA" ? "https://data.gov.tw/license" : "https://creativecommons.org/publicdomain/zero/1.0/"), "参考目录授权网址无效。");
        Validation.Require(ArchiveSha256 is { Length: 64 } && ArchiveSha256.All(Uri.IsHexDigit), "参考档案校验值无效。");
        Validation.Require(Foods is { Length: > 0 and <= 20000 }, "参考目录条目数无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Foods)
        {
            Validation.Require(item is not null && !string.IsNullOrWhiteSpace(item.SourceId) && item.SourceId.Length <= 100 && ids.Add(item.SourceId), "参考目录来源编号为空或重复。");
            Validation.Require(item.SourceId.All(c => c is >= '0' and <= '9' || Provider == "TFDA" && c is >= 'A' and <= 'Z'), "参考目录来源编号包含无效字符。");
            Validation.Require(!string.IsNullOrWhiteSpace(item.Name) && item.Name.Length <= 500, "参考食物名称无效。");
            Validation.Text(item.OriginalName, "参考原名", 500); Validation.Text(item.Aliases, "参考别名", 500);
            Validation.Text(item.Preparation, "参考加工状态", 500); Validation.Text(item.Category, "参考分类", 100); Validation.Text(item.SourceNote, "参考来源说明", 1000);
            Validation.Require(item.KcalPer100gHundredths is >= 0 and <= 1000000, "参考食物热量无效。");
        }
    }
}

public sealed record FoodReferenceHit(FoodReferenceCatalog Catalog, FoodReference Food)
{
    public string Name => Food.Name;
    public string Provider => Catalog.Provider;
    public decimal KcalPer100g => Food.KcalPer100gHundredths / 100m;
    public string Preparation => Food.Preparation;
    public string SourceUrl => Provider == "USDA SR Legacy"
        ? $"https://fdc.nal.usda.gov/food-details/{Food.SourceId}/nutrients" : Catalog.SourceUrl;
    public string Details => $"{Food.OriginalName}\n{Food.Aliases}\n{Food.Preparation}\n{Provider} · {Food.SourceId} · {Catalog.Version}\n资料获取：{Catalog.RetrievedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}（非检测日期）\n{Food.SourceNote}\n{SourceUrl}\n授权：{Catalog.LicenseUrl}";
    public FoodItem CreateFood() => new()
    {
        Id = Guid.NewGuid(), Name = Food.Name, OriginalName = Food.OriginalName, Aliases = Food.Aliases,
        Preparation = Food.Preparation, Category = Food.Category, KcalPer100gHundredths = Food.KcalPer100gHundredths,
        SourceKind = Provider, SourceId = Food.SourceId, SourceVersion = Catalog.Version, SourceUrl = SourceUrl,
        SourceNote = Food.SourceNote, UserModified = true
    };
}

public static class FoodReferenceSearch
{
    public static FoodReferenceHit[] Search(IEnumerable<FoodReferenceCatalog> catalogs, string query, string provider, out int total)
    {
        query = query.Trim().Normalize(NormalizationForm.FormKC);
        Validation.Require(query.Length is > 0 and <= 100, "请输入1至100字的食物关键词。");
        Validation.Require(provider is "全部来源" or "USDA SR Legacy" or "TFDA", "请选择有效资料来源。");
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = catalogs.Where(x => provider == "全部来源" || x.Provider == provider)
            .SelectMany(catalog => catalog.Foods.Select(food => new FoodReferenceHit(catalog, food)))
            .Where(hit => terms.All(term => string.Join(' ', hit.Name, hit.Food.OriginalName, hit.Food.Aliases, hit.Preparation, hit.Food.Category, hit.Food.SourceId)
                .Normalize(NormalizationForm.FormKC).Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(hit => hit.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : hit.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(hit => hit.Provider).ThenBy(hit => hit.Name).ToArray();
        total = matches.Length;
        // 每个来源保留最多100项，使较大的目录不会挤掉另一个来源的结果。
        return matches.GroupBy(x => x.Provider).SelectMany(x => x.Take(100)).ToArray();
    }
}
