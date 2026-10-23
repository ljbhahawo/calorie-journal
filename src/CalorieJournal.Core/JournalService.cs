namespace CalorieJournal.Core;

public interface IJournalStore
{
    FoodItem[] ReadFoods(bool includeDeleted = false);
    MealCategory[] ReadMeals();
    IntakeEntry[] ReadEntries(DateOnly from, DateOnly to);
    IntakeEntry? FindEntry(Guid id);
    void SaveFood(FoodItem value);
    void SaveMeal(MealCategory value);
    FoodPortion[] ReadPortions();
    void SavePortion(FoodPortion value);
    MealTemplate[] ReadTemplates();
    void SaveTemplate(MealTemplate value);
    TemplateBatchReceipt[] ReadTemplateBatches();
    void AppendTemplateBatch(TemplateBatchReceipt receipt, IntakeEntry[] entries);
    void SaveEntry(IntakeEntry value);
    void AppendEntries(IntakeEntry[] values);
    void DeleteEntry(Guid id);
    JournalSnapshot ReadSnapshot();
    void ReplaceSnapshot(JournalSnapshot snapshot);
}

public sealed partial class JournalService(IJournalStore store)
{
    public FoodItem[] Foods(string query = "")
    {
        var search = FoodSearch.PrepareQuery(query);
        return store.ReadFoods().Select(food => (Food: food, Document: FoodSearch.PrepareDocument(food.Name, food.Aliases, food.OriginalName, food.Category, food.Preparation)))
            .Where(x => x.Document.Matches(search)).OrderBy(x => x.Document.Rank(search))
            .ThenBy(x => x.Food.Category).ThenBy(x => x.Food.Name).Select(x => x.Food).ToArray();
    }

    public MealCategory[] Meals() => store.ReadMeals().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToArray();
    public IntakeEntry[] Entries(DateOnly day)
    {
        var order = Meals().ToDictionary(x => x.Id, x => x.SortOrder);
        return store.ReadEntries(day, day).OrderBy(x => order[x.MealCategoryId]).ThenBy(x => x.LocalTime).ThenBy(x => x.CreatedAtUtc).ToArray();
    }

    public DailySummary[] History(DateOnly from, DateOnly to)
    {
        Validation.Require(to >= from && to.DayNumber - from.DayNumber <= 3660, "日期区间须有效且不超过3661天。");
        var groups = store.ReadEntries(from, to).GroupBy(x => x.EntryDate).ToDictionary(x => x.Key, x => x.ToArray());
        return Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i =>
        {
            var day = from.AddDays(i);
            var values = groups.GetValueOrDefault(day) ?? [];
            return new DailySummary(day, values.Sum(x => x.EnergyKcalTenths), values.Length);
        }).Reverse().ToArray();
    }

    public DailyIntake[] BrowseDays(DateOnly from, DateOnly to)
    {
        Validation.Require(to >= from && to.DayNumber - from.DayNumber <= 365, "连续列表区间须有效且不超过366天。");
        Validation.Require(to <= DateOnly.FromDateTime(DateTime.Today), "连续列表日期不能晚于今天。");
        var order = Meals().ToDictionary(x => x.Id, x => x.SortOrder);
        var groups = store.ReadEntries(from, to).GroupBy(x => x.EntryDate).ToDictionary(x => x.Key,
            x => x.OrderBy(e => order[e.MealCategoryId]).ThenBy(e => e.LocalTime).ThenBy(e => e.CreatedAtUtc).ToArray());
        return Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Reverse()
            .Select(i => { var day = from.AddDays(i); return new DailyIntake(day, groups.GetValueOrDefault(day) ?? []); }).ToArray();
    }

    public void SaveFood(FoodItem value)
    {
        value = value with { Name = Validation.Text(value.Name, "食物名称", 100, true), UpdatedAtUtc = DateTimeOffset.UtcNow };
        Validation.Food(value);
        store.SaveFood(value);
    }

    public void DeleteFood(FoodItem value) => SaveFood(value with { IsDeleted = true, UserModified = true });

    public void SaveMeal(MealCategory value)
    {
        value = value with { Name = Validation.Text(value.Name, "餐次名称", 30, true) };
        Validation.Meal(value);
        var meals = store.ReadMeals();
        Validation.Require(!meals.Any(x => x.Id != value.Id && x.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)), "餐次名称已经存在。");
        Validation.Require(value.IsActive || meals.Any(x => x.Id != value.Id && x.IsActive), "至少保留一个启用餐次。");
        store.SaveMeal(value);
    }

    public void SaveEntry(IntakeEntry value)
    {
        value = value with { FoodNameSnapshot = Validation.Text(value.FoodNameSnapshot, "食物名称", 100, true), UpdatedAtUtc = DateTimeOffset.UtcNow };
        Validation.Entry(value);
        var previous = store.FindEntry(value.Id);
        var meal = store.ReadMeals().SingleOrDefault(x => x.Id == value.MealCategoryId);
        Validation.Require(meal is not null, "餐次不存在。");
        Validation.Require(meal.IsActive || previous?.MealCategoryId == meal.Id, "该餐次已停用，请选择启用餐次。");
        if (value.FoodId is not null)
        {
            var food = store.ReadFoods(true).SingleOrDefault(x => x.Id == value.FoodId);
            Validation.Require(food is not null && (!food.IsDeleted || previous?.FoodId == food.Id), "引用食物不存在或已经删除，请重新选择。");
        }
        store.SaveEntry(value);
    }

    public void DeleteEntry(Guid id) => store.DeleteEntry(id);

    public IntakeEntry[] ExistingImportEntries(DateOnly from, DateOnly to) => store.ReadEntries(from, to);

    public void ValidateImport(IntakeEntry[] values)
    {
        Validation.Require(values.Length is > 0 and <= 1000, "每批须选择1到1000条摄入。" );
        Validation.Require(values.Select(x => x.Id).Distinct().Count() == values.Length, "本批记录ID重复。" );
        var existing = store.ReadEntries(DateOnly.MinValue, DateOnly.MaxValue);
        Validation.Require(existing.Length + values.Length <= 100000, "导入后记录不能超过当前备份格式的10万条限制。" );
        var existingIds = existing.Select(x => x.Id).ToHashSet();
        Validation.Require(values.All(x => !existingIds.Contains(x.Id)), "该原文行已经导入，请取消重复选择。" );
        var meals = store.ReadMeals().Where(x => x.IsActive).ToDictionary(x => x.Id);
        foreach (var value in values)
        {
            Validation.Entry(value);
            Validation.Require(value.InputMode == "direct" && value.FoodId is null, "文字导入须采用未关联食物库的热量快照。" );
            Validation.Require(meals.TryGetValue(value.MealCategoryId, out var meal) && meal.Name == value.MealNameSnapshot, "导入餐次须对应当前启用餐次。" );
        }
    }

    public void ImportEntries(IntakeEntry[] values)
    {
        ValidateImport(values);
        store.AppendEntries(values);
    }
}
