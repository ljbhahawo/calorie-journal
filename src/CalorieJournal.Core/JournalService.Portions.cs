namespace CalorieJournal.Core;

public sealed partial class JournalService
{
    public FoodPortion[] Portions(Guid foodId) => store.ReadPortions().Where(x => x.FoodId == foodId && !x.IsDeleted)
        .OrderBy(x => x.Name).ToArray();

    public void SavePortion(FoodPortion value)
    {
        value = value with { Name = Validation.Text(value.Name, "份量名称", 30, true), UpdatedAtUtc = DateTimeOffset.UtcNow };
        WeightCalculator.Portion(value);
        Validation.Require(store.ReadFoods().Any(x => x.Id == value.FoodId), "食物不存在或已删除，不能维护其份量。");
        var portions = store.ReadPortions();
        var previous = portions.SingleOrDefault(x => x.Id == value.Id);
        Validation.Require(previous is null || previous.FoodId == value.FoodId, "不能将已有份量改到其他食物。");
        Validation.Require(value.IsDeleted || !portions.Any(x => x.Id != value.Id && x.FoodId == value.FoodId && !x.IsDeleted && x.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)), "该食物已经有同名份量。");
        store.SavePortion(value);
    }
}
