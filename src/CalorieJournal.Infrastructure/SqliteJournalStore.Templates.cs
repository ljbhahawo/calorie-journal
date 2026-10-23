using System.Globalization;
using System.Text.Json;
using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore
{
    private static MealTemplate[] Templates(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,body FROM meal_templates ORDER BY id");
        using var reader = command.ExecuteReader(); var result = new List<MealTemplate>();
        while (reader.Read())
        {
            var value = JsonSerializer.Deserialize<MealTemplate>(reader.GetString(1), JsonFiles.Options) ?? throw new ValidationException("模板内容为空。");
            Validation.Require(value.Id.ToString() == reader.GetString(0), "模板存储ID不一致。");
            TemplateValidation.Template(value); result.Add(value);
        }
        return result.ToArray();
    }

    private static TemplateBatchReceipt[] TemplateBatches(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,body FROM template_batches ORDER BY id");
        using var reader = command.ExecuteReader(); var result = new List<TemplateBatchReceipt>();
        while (reader.Read())
        {
            var value = JsonSerializer.Deserialize<TemplateBatchReceipt>(reader.GetString(1), JsonFiles.Options) ?? throw new ValidationException("餐食批次内容为空。");
            Validation.Require(value.Id.ToString() == reader.GetString(0), "餐食批次存储ID不一致。");
            TemplateValidation.Receipt(value); result.Add(value);
        }
        return result.ToArray();
    }

    public MealTemplate[] ReadTemplates() { using var connection = Open(); return Templates(connection, null); }
    public TemplateBatchReceipt[] ReadTemplateBatches() { using var connection = Open(); return TemplateBatches(connection, null); }
    private static void PutTemplate(SqliteConnection connection, SqliteTransaction transaction, MealTemplate value) => Execute(connection, transaction,
        "INSERT INTO meal_templates VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body",
        ("$id", value.Id.ToString()), ("$body", JsonSerializer.Serialize(value, JsonFiles.Options)));
    private static void PutTemplateBatch(SqliteConnection connection, SqliteTransaction transaction, TemplateBatchReceipt value) => Execute(connection, transaction,
        "INSERT INTO template_batches VALUES($id,$body)", ("$id", value.Id.ToString()), ("$body", JsonSerializer.Serialize(value, JsonFiles.Options)));

    public void SaveTemplate(MealTemplate value)
    {
        TemplateValidation.Template(value);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var capacity = new WriteCapacity(connection, transaction);
        var foods = Foods(connection, transaction, value.IsDeleted).Select(x => x.Id).ToHashSet();
        var meals = Meals(connection, transaction).Where(x => value.IsDeleted || x.IsActive).Select(x => x.Id).ToHashSet();
        Validation.Require(value.Items.All(x => foods.Contains(x.FoodId)), "模板食物已删除，请重新选择。");
        Validation.Require(value.PreferredMealId is null || meals.Contains(value.PreferredMealId.Value), "模板餐次不存在或已停用。");
        PutTemplate(connection, transaction, value); capacity.Commit();
    }

    public void AppendTemplateBatch(TemplateBatchReceipt receipt, IntakeEntry[] entries)
    {
        TemplateValidation.Receipt(receipt);
        Validation.Require(receipt.EntryIds.SequenceEqual(entries.Select(x => x.Id)), "餐食批次和条目清单不一致。");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        var capacity = new WriteCapacity(connection, transaction);
        Validation.Require(Templates(connection, transaction).Any(x => x.Id == receipt.TemplateId && !x.IsDeleted), "模板不存在或已删除。");
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM template_batches"), CultureInfo.InvariantCulture) < 100000, "已保存餐食批次达到10万条上限。");
        var foods = Foods(connection, transaction, false).Select(x => x.Id).ToHashSet();
        var meals = Meals(connection, transaction).Where(x => x.IsActive).ToDictionary(x => x.Id);
        var portions = Portions(connection, transaction);
        PutTemplateBatch(connection, transaction, receipt);
        foreach (var entry in entries)
        {
            Validation.Entry(entry);
            Validation.Require(entry.InputMode == "weight" && entry.FoodId is not null && foods.Contains(entry.FoodId.Value), "整餐食物已删除，请重新核对。");
            Validation.Require(meals.TryGetValue(entry.MealCategoryId, out var meal) && meal.Name == entry.MealNameSnapshot, "整餐餐次已变更或停用。");
            if (entry.WeightCalculation?.Kind == "portion")
                Validation.Require(portions.Any(x => x.Id == entry.WeightCalculation.PortionId && x.FoodId == entry.FoodId && !x.IsDeleted && x.GramsTenths == entry.WeightCalculation.PortionGramsTenths), "整餐份量已变更或删除，请重新核对。");
            Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM entries WHERE id=$id", ("$id", entry.Id.ToString())), CultureInfo.InvariantCulture) == 0, "整餐中的记录已保存，不能重复写入。");
            PutEntry(connection, transaction, entry);
        }
        capacity.Commit();
    }
}
