using System.IO;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Desktop;

internal sealed class EntryFoodSuggestion
{
    public EntryFoodSuggestion(FoodItem food) => LibraryFood = food;
    public EntryFoodSuggestion(FoodReferenceHit hit) => Reference = hit;
    public FoodItem? LibraryFood { get; }
    public FoodReferenceHit? Reference { get; }
    public string Name => LibraryFood?.Name ?? Reference!.Name;
    public string Preparation => LibraryFood?.Preparation ?? Reference!.Preparation;
    public string OriginalName => LibraryFood?.OriginalName ?? Reference!.Food.OriginalName;
    public decimal KcalPer100g => LibraryFood?.KcalPer100g ?? Reference!.KcalPer100g;
    public string SourceLabel => Reference?.Provider ?? "个人库";
    public string SourceSummary => LibraryFood?.SourceSummary ?? $"{Reference!.Provider} · {Reference.Food.SourceId} · {Reference.Catalog.Version}";
    public string AccessibleName => $"{Name}，{SourceLabel}，每100克 {KcalPer100g:0.00} 大卡";

    public FoodItem CreateReferenceDraft()
    {
        var hit = Reference ?? throw new InvalidOperationException("候选不是参考资料。");
        return hit.CreateFood() with { Name = Shorten(hit.Name, 100), Preparation = Shorten(hit.Preparation, 200) };
    }

    private static string Shorten(string value, int maximum)
    {
        value = value.Trim();
        if (value.Length <= maximum) return value;
        var end = char.IsHighSurrogate(value[maximum - 2]) ? maximum - 2 : maximum - 1;
        return value[..end].TrimEnd() + "…";
    }
}

internal sealed record EntryFoodSearchResult(EntryFoodSuggestion[] Items, string Warning);

internal sealed class EntryFoodSearch(JournalService journal, FoodLookupService lookup)
{
    public EntryFoodSearchResult Find(string query)
    {
        var catalogs = new List<FoodReferenceCatalog>(2);
        var warnings = new List<string>(2);
        Load("USDA", lookup.LoadUsda);
        Load("TFDA", () => lookup.LoadTfda());
        var personal = journal.Foods(query).Take(12).Select(x => new EntryFoodSuggestion(x)).ToArray();
        var reference = FoodReferenceSearch.Search(catalogs, query, "全部来源", out _)
            .GroupBy(x => x.Provider).OrderByDescending(x => x.Key == "USDA SR Legacy")
            .Select(x => x.Take(12).Select(hit => new EntryFoodSuggestion(hit)).ToArray());
        var groups = new[] { personal }.Concat(reference).ToArray();
        // 交替展示各来源，避免某个目录的宽泛匹配占满整个下拉框。
        var results = Enumerable.Range(0, 12).SelectMany(index => groups.Where(group => index < group.Length).Select(group => group[index])).Take(12).ToArray();
        return new(results, string.Join("；", warnings));

        void Load(string provider, Func<FoodReferenceCatalog> read)
        {
            try { catalogs.Add(read()); }
            catch (Exception ex) when (IsExpectedError(ex)) { warnings.Add(provider + " 资料无法读取：" + ex.Message); }
        }
    }

    public static bool IsExpectedError(Exception ex) => ex is ValidationException or IOException or UnauthorizedAccessException or SqliteException or JsonException;
}
