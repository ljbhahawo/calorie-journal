namespace CalorieJournal.Core;

public sealed record TemplateItem
{
    public required Guid Id { get; init; }
    public required Guid FoodId { get; init; }
    public required string FoodNameSnapshot { get; init; }
    public string PreparationSnapshot { get; init; } = "";
    public required long KcalPer100gHundredthsSnapshot { get; init; }
    public string SourceSnapshot { get; init; } = "";
    public required long WeightGramsTenths { get; init; }
    public WeightCalculationSnapshot? WeightCalculation { get; init; }
}

public sealed record MealTemplate
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public Guid? PreferredMealId { get; init; }
    public string Note { get; init; } = "";
    public required TemplateItem[] Items { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TemplateBatchReceipt
{
    public required Guid Id { get; init; }
    public required Guid TemplateId { get; init; }
    public required Guid[] EntryIds { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class TemplateValidation
{
    public static void Item(TemplateItem value)
    {
        Validation.Require(value.Id != Guid.Empty && value.FoodId != Guid.Empty, "模板明细或食物ID无效。");
        Validation.Text(value.FoodNameSnapshot, "模板食物名称", 100, true);
        Validation.Text(value.PreparationSnapshot, "模板食物状态", 200);
        Validation.Text(value.SourceSnapshot, "模板来源", 3000);
        _ = Validation.CalculateEnergy(value.WeightGramsTenths, value.KcalPer100gHundredthsSnapshot);
        if (value.WeightCalculation is not null)
        {
            Validation.Require(value.WeightCalculation.Kind == "portion", "模板明细使用可食重量或常用份量。");
            Validation.Require(value.WeightGramsTenths == WeightCalculator.Calculate(value.WeightCalculation), "模板重量与份量依据不一致。");
        }
    }

    public static void Template(MealTemplate value)
    {
        Validation.Require(value.Id != Guid.Empty && value.PreferredMealId != Guid.Empty, "模板或默认餐次ID无效。");
        Validation.Text(value.Name, "模板名称", 100, true); Validation.Text(value.Note, "模板备注", 1000);
        Validation.Require(value.CreatedAtUtc != default && value.UpdatedAtUtc != default, "模板时间无效。");
        Validation.Require(value.Items is not null && value.Items.Length is > 0 and <= 100 && value.Items.All(x => x is not null), "模板须有1到100项有效食物。");
        foreach (var item in value.Items) Item(item);
        Validation.Require(value.Items.Select(x => x.Id).Distinct().Count() == value.Items.Length, "模板明细ID重复。");
    }

    public static void Receipt(TemplateBatchReceipt value)
    {
        Validation.Require(value.Id != Guid.Empty && value.TemplateId != Guid.Empty && value.CreatedAtUtc != default, "餐食批次信息无效。");
        Validation.Require(value.EntryIds is not null && value.EntryIds.Length is > 0 and <= 100 && value.EntryIds.All(x => x != Guid.Empty), "餐食批次须有1到100个记录ID。");
        Validation.Require(value.EntryIds.Distinct().Count() == value.EntryIds.Length, "餐食批次记录ID重复。");
    }

    public static void Snapshot(JournalSnapshot value)
    {
        if (value.FormatVersion < 3)
        {
            Validation.Require((value.Templates is null || value.Templates.Length == 0) && (value.TemplateBatches is null || value.TemplateBatches.Length == 0), "旧格式不能包含模板或餐食批次。");
            return;
        }
        Validation.Require(value.Templates is not null && value.Templates.Length <= 1000 && value.Templates.All(x => x is not null), "备份缺少模板或条目无效。");
        Validation.Require(value.TemplateBatches is not null && value.TemplateBatches.Length <= 100000 && value.TemplateBatches.All(x => x is not null), "备份缺少餐食批次或条目无效。");
        foreach (var template in value.Templates) Template(template);
        foreach (var receipt in value.TemplateBatches) Receipt(receipt);
        Validation.Require(value.Templates.Select(x => x.Id).Distinct().Count() == value.Templates.Length, "模板ID重复。");
        Validation.Require(value.TemplateBatches.Select(x => x.Id).Distinct().Count() == value.TemplateBatches.Length, "餐食批次ID重复。");
        var foods = value.Foods.Select(x => x.Id).ToHashSet(); var meals = value.Meals.Select(x => x.Id).ToHashSet();
        var templates = value.Templates.Select(x => x.Id).ToHashSet();
        Validation.Require(value.Templates.All(x => (x.PreferredMealId is null || meals.Contains(x.PreferredMealId.Value)) && x.Items.All(i => foods.Contains(i.FoodId))), "模板包含不存在的食物或餐次引用。");
        Validation.Require(value.TemplateBatches.All(x => templates.Contains(x.TemplateId)), "餐食批次引用的模板不存在。");
        // 摄入以后可被用户删除，批次中的原ID清单仍保留，用于阻止重复提交。
    }
}
