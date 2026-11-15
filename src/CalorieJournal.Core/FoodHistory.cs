namespace CalorieJournal.Core;

public sealed record FoodHistoryPage(IntakeEntry[] Entries, bool HasNext);

public sealed partial class JournalService
{
    private const int FoodHistoryPageSize = 100;

    public FoodHistoryPage SearchIntakeHistory(string name, int page = 1)
    {
        Validation.Require(page >= 1 && page <= int.MaxValue / FoodHistoryPageSize, "摄入历史页码无效。");
        name = name.Trim();
        var query = FoodSearch.PrepareQuery(name);
        var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool MatchesName(string savedName)
        {
            if (name.Length == 0) return true;
            if (matches.TryGetValue(savedName, out var result)) return result;
            var literalName = FoodSearch.Normalize(savedName);
            result = literalName.Contains(query.Text, StringComparison.Ordinal) ||
                query.LiteralTerms.Length > 0 && FoodSearch.PrepareDocument(savedName, "").Matches(query);
            matches.Add(savedName, result);
            return result;
        }

        var entries = store.ReadEntryHistory(MatchesName, (page - 1) * FoodHistoryPageSize, FoodHistoryPageSize + 1);
        return entries.Length > FoodHistoryPageSize
            ? new FoodHistoryPage(entries[..FoodHistoryPageSize], true)
            : new FoodHistoryPage(entries, false);
    }
}
