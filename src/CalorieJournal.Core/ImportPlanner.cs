using System.Globalization;

namespace CalorieJournal.Core;

public sealed record ImportCandidate(ImportDraft Original, bool Included, string DateText, string MealName,
    string Name, string EnergyText, string LocalTime, string Note);
public sealed record ImportIssue(Guid Id, string Error, bool Duplicate);
public sealed record ImportReview(IntakeEntry[] Entries, ImportIssue[] Issues)
{
    public bool HasErrors => Issues.Any(x => x.Error.Length > 0);
    public bool HasDuplicates => Issues.Any(x => x.Duplicate);
}

public static class ImportPlanner
{
    public static ImportReview Review(ImportCandidate[] candidates, string fingerprint, MealCategory[] meals, IntakeEntry[] existing)
    {
        var entries = new List<IntakeEntry>();
        var issues = new Dictionary<Guid, ImportIssue>();
        var selected = candidates.Where(x => x.Included).ToArray();
        Validation.Require(selected.Length is > 0 and <= 1000, "请选择1到1000条记录。" );
        Validation.Require(selected.Select(x => x.Original.Id).Distinct().Count() == selected.Length, "导入草稿ID重复，请重新生成。" );
        var existingIds = existing.Select(x => x.Id).ToHashSet();
        foreach (var row in selected)
        {
            try
            {
                Validation.Require(!existingIds.Contains(row.Original.Id), "已导入过该原文行，不能覆盖；请取消选择。" );
                Validation.Require(DateOnly.TryParseExact(row.DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date), "日期须为yyyy-MM-dd，请补齐年份和有效月日。" );
                var meal = meals.SingleOrDefault(x => x.IsActive && x.Name == row.MealName);
                Validation.Require(meal is not null, "请将原餐次对应到已启用餐次；可先到设置新增餐次。" );
                var entry = new IntakeEntry
                {
                    Id = row.Original.Id, EntryDate = date, LocalTime = row.LocalTime.Trim(),
                    MealCategoryId = meal.Id, MealNameSnapshot = meal.Name,
                    FoodNameSnapshot = Validation.Text(row.Name, "食物名称", 100, true),
                    EnergyKcalTenths = Validation.Scaled(Validation.Number(row.EnergyText, "本项大卡"), 10, 0, 99999.9m, "本项大卡"),
                    Note = row.Note.Trim(),
                    SourceSnapshot = $"文字导入 {IntakeTextParser.RuleVersion} · 批次{fingerprint} · 行{row.Original.LineNumber} · {row.Original.Kind}\n识别：{row.Original.Hint}\n原文：{row.Original.Raw}"
                };
                Validation.Entry(entry);
                entries.Add(entry);
            }
            catch (ValidationException ex) { issues[row.Original.Id] = new(row.Original.Id, ex.Message, false); }
        }
        var originals = selected.ToDictionary(x => x.Original.Id);
        foreach (var group in entries.GroupBy(x => (x.EntryDate, x.MealCategoryId)))
        {
            if (group.Any(x => originals[x.Id].Original.Kind == ImportRowKind.MealTotal)
                && group.Any(x => originals[x.Id].Original.Kind != ImportRowKind.MealTotal))
                foreach (var entry in group) issues[entry.Id] = new(entry.Id, "同一天同餐合计与明细不能同时导入，请选择一种。", false);
            if (group.Count(x => originals[x.Id].Original.Kind == ImportRowKind.MealTotal) > 1)
                foreach (var entry in group) issues[entry.Id] = new(entry.Id, "同一天同餐选择了多条合计，请保留一条。", false);
        }
        var signatures = existing.Select(Signature).ToHashSet();
        var repeated = entries.GroupBy(Signature).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        foreach (var entry in entries)
        {
            var isTotal = originals[entry.Id].Original.Kind == ImportRowKind.MealTotal;
            var sameMeal = existing.Where(x => x.EntryDate == entry.EntryDate && x.MealCategoryId == entry.MealCategoryId).ToArray();
            var sameSource = sameMeal.Where(x => x.SourceSnapshot.StartsWith($"文字导入 {IntakeTextParser.RuleVersion} · 批次{fingerprint} · ", StringComparison.Ordinal)).ToArray();
            if (sameSource.Any(x => x.SourceSnapshot.Split('\n')[0].EndsWith("· MealTotal", StringComparison.Ordinal) != isTotal))
                issues[entry.Id] = new(entry.Id, "这份原文的同餐已采用另一种合计方式；不能再叠加，请先处理已有记录。", false);
            var duplicate = signatures.Contains(Signature(entry)) || repeated.Contains(Signature(entry))
                || isTotal && sameMeal.Length > 0;
            if (!issues.ContainsKey(entry.Id)) issues[entry.Id] = new(entry.Id, "", duplicate);
        }
        return new(entries.ToArray(), issues.Values.ToArray());
    }

    private static string Signature(IntakeEntry entry) => string.Join("\u001F", entry.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        entry.MealCategoryId.ToString(), entry.LocalTime, entry.FoodNameSnapshot, entry.EnergyKcalTenths.ToString(CultureInfo.InvariantCulture));
}
