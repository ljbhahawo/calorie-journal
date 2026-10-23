using System.Text.Json;
using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore
{
    private static JournalSettings Settings(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var text = Scalar(connection, transaction, "SELECT value FROM metadata WHERE key='settings'") as string;
        Validation.Require(text is not null, "缺少录入设置，已停止操作。");
        var value = JsonSerializer.Deserialize<JournalSettings>(text, JsonFiles.Options);
        Validation.Require(value is not null, "录入设置为空。"); JournalSettings.Validate(value); return value;
    }
    public JournalSettings ReadSettings() { using var connection = Open(); return Settings(connection, null); }
    public void SaveSettings(JournalSettings value)
    {
        JournalSettings.Validate(value); using var connection = Open(); using var transaction = connection.BeginTransaction();
        PutMetadata(connection, transaction, "settings", JsonSerializer.Serialize(value, JsonFiles.Options)); transaction.Commit();
    }
    public bool SaveEntryAndFood(IntakeEntry value, FoodItem food)
    {
        Validation.Entry(value); Validation.Food(food);
        Validation.Require(value.FoodId is null && value.InputMode == "weight" && JournalService.SameFoodData(food, value), "新食物与摄入资料不一致。");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var capacity = new WriteCapacity(connection, transaction);
        var previousMealMatches = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM entries WHERE id=$id AND meal_id=$meal", ("$id", value.Id.ToString()), ("$meal", value.MealCategoryId.ToString()))) == 1;
        Validation.Require(Meals(connection, transaction).Any(x => x.Id == value.MealCategoryId && (x.IsActive || previousMealMatches)), "餐次已停用，请重新选择。");
        var existing = Foods(connection, transaction, false).FirstOrDefault(x => JournalService.SameFoodData(x, value));
        if (existing is null)
        {
            Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM foods")) < 100000, "食物资料已达到10万项上限。");
            PutFood(connection, transaction, food);
        }
        PutEntry(connection, transaction, value with { FoodId = existing?.Id ?? food.Id }); capacity.Commit();
        return existing is null;
    }
}
