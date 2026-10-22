namespace CalorieJournal.Core;

public interface IJournalStore
{
    FoodItem[] ReadFoods(bool includeDeleted = false);
    MealCategory[] ReadMeals();
    IntakeEntry[] ReadEntries(DateOnly from, DateOnly to);
    IntakeEntry? FindEntry(Guid id);
    void SaveFood(FoodItem value);
    void SaveMeal(MealCategory value);
    void SaveEntry(IntakeEntry value);
    void DeleteEntry(Guid id);
    JournalSnapshot ReadSnapshot();
    void ReplaceSnapshot(JournalSnapshot snapshot);
}

public sealed class JournalService(IJournalStore store)
{
    public FoodItem[] Foods(string query = "")
    {
        var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return store.ReadFoods().Where(x => terms.All(t => $"{x.Name} {x.OriginalName} {x.Aliases} {x.Category} {x.Preparation}".Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Category).ThenBy(x => x.Name).ToArray();
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
}
