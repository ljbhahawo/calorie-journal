using System.Globalization;
using System.Security.Cryptography;
using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore
{
    public string? LastMigrationBackup { get; private set; }

    private void BackupBeforeMigration()
    {
        var root = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "recovery");
        Directory.CreateDirectory(root);
        var prefix = Path.Combine(root, $"升级前_结构1_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
        var database = prefix + ".db";
        BackupDatabase(database);
        var snapshot = new SqliteJournalStore(database).ReadSnapshot();
        Validation.Snapshot(snapshot);
        Validation.Require(snapshot.SchemaVersion == 1, "升级前数据库备份版本不符，停止迁移。");
        var backup = prefix + ".calorie-backup.json";
        JsonFiles.WriteNew(backup, snapshot);
        var reread = JsonFiles.Read<JournalSnapshot>(backup);
        Validation.Snapshot(reread);
        using var stream = File.OpenRead(database);
        JsonFiles.WriteNew(prefix + ".manifest.json", new
        {
            Application = "CalorieJournal", FromSchema = 1, ToSchema = SchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow, DatabaseFile = Path.GetFileName(database),
            BackupFile = Path.GetFileName(backup), DatabaseSha256 = Convert.ToHexString(SHA256.HashData(stream))
        });
        LastMigrationBackup = backup;
    }

    private static FoodPortion[] Portions(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,food_id,name,grams_tenths,is_deleted,created_at,updated_at FROM portions ORDER BY name");
        using var reader = command.ExecuteReader();
        var result = new List<FoodPortion>();
        while (reader.Read()) result.Add(new FoodPortion
        {
            Id = Guid.Parse(reader.GetString(0)), FoodId = Guid.Parse(reader.GetString(1)), Name = reader.GetString(2),
            GramsTenths = reader.GetInt64(3), IsDeleted = reader.GetBoolean(4), CreatedAtUtc = Time(reader.GetString(5)), UpdatedAtUtc = Time(reader.GetString(6))
        });
        return result.ToArray();
    }

    public FoodPortion[] ReadPortions() { using var connection = Open(); return Portions(connection, null); }

    private static void PutPortion(SqliteConnection connection, SqliteTransaction transaction, FoodPortion value) => Execute(connection, transaction, """
        INSERT INTO portions VALUES($id,$food,$name,$grams,$deleted,$created,$updated)
        ON CONFLICT(id) DO UPDATE SET name=excluded.name,grams_tenths=excluded.grams_tenths,is_deleted=excluded.is_deleted,updated_at=excluded.updated_at
        """, ("$id", value.Id.ToString()), ("$food", value.FoodId.ToString()), ("$name", value.Name),
        ("$grams", value.GramsTenths), ("$deleted", value.IsDeleted), ("$created", Stamp(value.CreatedAtUtc)), ("$updated", Stamp(value.UpdatedAtUtc)));

    public void SavePortion(FoodPortion value)
    {
        WeightCalculator.Portion(value);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM foods WHERE id=$id AND is_deleted=0", ("$id", value.FoodId.ToString())), CultureInfo.InvariantCulture) == 1, "份量引用食物不存在或已删除。");
        var previousFood = Scalar(connection, transaction, "SELECT food_id FROM portions WHERE id=$id", ("$id", value.Id.ToString())) as string;
        Validation.Require(previousFood is null || previousFood == value.FoodId.ToString(), "不能更改份量所属食物。");
        PutPortion(connection, transaction, value);
        transaction.Commit();
    }
}
