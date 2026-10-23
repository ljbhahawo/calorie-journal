namespace CalorieJournal.Core;

public sealed partial class JournalService
{
    public MealTemplate[] Templates(string query = "")
    {
        Validation.Require(query.Length <= 100, "模板关键词最多100字。");
        var normalized = FoodSearch.Normalize(query);
        return store.ReadTemplates().Where(x => !x.IsDeleted && FoodSearch.Normalize(x.Name).Contains(normalized, StringComparison.Ordinal))
            .OrderBy(x => x.Name).ToArray();
    }

    public TemplateItem CreateTemplateItem(Guid id, Guid foodId, long gramsTenths, WeightCalculationSnapshot? calculation = null)
    {
        var food = store.ReadFoods().SingleOrDefault(x => x.Id == foodId);
        Validation.Require(food is not null, "模板引用的食物不存在或已删除，请重新选择。");
        if (calculation is not null)
        {
            Validation.Require(calculation.Kind == "portion", "模板使用可食重量或常用份量。");
            var portion = store.ReadPortions().SingleOrDefault(x => x.Id == calculation.PortionId && x.FoodId == foodId && !x.IsDeleted);
            Validation.Require(portion is not null, $"“{food.Name}”的份量不存在或已删除，请重新选择。");
            calculation = calculation with { PortionName = portion.Name, PortionGramsTenths = portion.GramsTenths };
            gramsTenths = WeightCalculator.Calculate(calculation);
        }
        var item = new TemplateItem { Id = id, FoodId = food.Id, FoodNameSnapshot = food.Name, PreparationSnapshot = food.Preparation,
            KcalPer100gHundredthsSnapshot = food.KcalPer100gHundredths, ProteinPer100gHundredthsSnapshot = food.ProteinPer100gHundredths,
            KilojoulesPer100gHundredthsSnapshot = food.KilojoulesPer100gHundredths, SourceSnapshot = food.SourceSummary + " " + food.SourceUrl,
            WeightGramsTenths = gramsTenths, WeightCalculation = calculation };
        TemplateValidation.Item(item);
        return item;
    }

    public TemplateItem[] RefreshTemplateItems(MealTemplate template)
    {
        TemplateValidation.Template(template);
        return template.Items.Select(x => CreateTemplateItem(x.Id, x.FoodId, x.WeightGramsTenths, x.WeightCalculation)).ToArray();
    }

    public void SaveTemplate(MealTemplate value)
    {
        value = value with { Name = Validation.Text(value.Name, "模板名称", 100, true), Note = Validation.Text(value.Note, "模板备注", 1000), UpdatedAtUtc = DateTimeOffset.UtcNow };
        TemplateValidation.Template(value);
        Validation.Require(value.IsDeleted || value.PreferredMealId is null || store.ReadMeals().Any(x => x.Id == value.PreferredMealId && x.IsActive), "模板默认餐次已停用，请重新选择或设为不指定。");
        if (!value.IsDeleted) value = value with { Items = RefreshTemplateItems(value) };
        Validation.Require(store.ReadTemplates().Any(x => x.Id == value.Id) || store.ReadTemplates().Length < 1000, "模板数量已达到1000项上限。");
        store.SaveTemplate(value);
    }

    public void SaveTemplateBatch(Guid batchId, Guid templateId, IntakeEntry[] entries)
    {
        var receipt = new TemplateBatchReceipt { Id = batchId, TemplateId = templateId, EntryIds = entries.Select(x => x.Id).ToArray() };
        TemplateValidation.Receipt(receipt);
        Validation.Require(store.ReadTemplates().Any(x => x.Id == templateId && !x.IsDeleted), "模板不存在或已删除，请重新带入。");
        Validation.Require(!store.ReadTemplateBatches().Any(x => x.Id == batchId), "该批餐食已经保存，不能重复提交；需要再次记录时请明确重新带入模板。");
        Validation.Require(store.ReadTemplateBatches().Length < 100000, "已保存餐食批次达到10万条上限。");
        Validation.Require(store.ReadSnapshot().Entries.Length + entries.Length <= 100000, "保存后摄入不能超过10万条。");
        var meals = store.ReadMeals().Where(x => x.IsActive).ToDictionary(x => x.Id);
        var foods = store.ReadFoods().Select(x => x.Id).ToHashSet(); var portions = store.ReadPortions();
        foreach (var entry in entries)
        {
            Validation.Entry(entry);
            Validation.Require(entry.InputMode == "weight" && entry.FoodId is not null && foods.Contains(entry.FoodId.Value), "整餐中的食物不存在或已删除，请重新核对。");
            Validation.Require(meals.TryGetValue(entry.MealCategoryId, out var meal) && meal.Name == entry.MealNameSnapshot, "整餐餐次已变更或停用，请重新选择。");
            if (entry.WeightCalculation?.Kind == "portion")
                Validation.Require(portions.Any(x => x.Id == entry.WeightCalculation.PortionId && x.FoodId == entry.FoodId && !x.IsDeleted && x.GramsTenths == entry.WeightCalculation.PortionGramsTenths), "整餐中的份量已变更或删除，请重新核对。");
        }
        store.AppendTemplateBatch(receipt, entries);
    }
}
