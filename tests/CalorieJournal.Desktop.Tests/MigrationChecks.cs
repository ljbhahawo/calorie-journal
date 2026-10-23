using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class MigrationChecks
{
    public static void Verify(string legacyDatabase)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT")!);
        legacyDatabase = Path.GetFullPath(legacyDatabase);
        Assert.IsTrue(legacyDatabase.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var original = new SqliteJournalStore(legacyDatabase).ReadSnapshot();
        Assert.IsTrue(original.SchemaVersion is 1 or 2, "必须使用实际旧版程序产生的数据库。");
        var package = JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory, "data/foods.json"));
        var healthy = Copy("healthy");
        healthy.Initialize(package);
        Assert.IsNotNull(healthy.LastMigrationBackup);
        var before = JsonFiles.Read<JournalSnapshot>(healthy.LastMigrationBackup);
        Assert.AreEqual(original.FormatVersion, before.FormatVersion);
        EqualData(original, before);
        var upgraded = healthy.ReadSnapshot();
        Assert.AreEqual(SqliteJournalStore.SchemaVersion, upgraded.SchemaVersion); Assert.IsEmpty(upgraded.Templates!); Assert.IsEmpty(upgraded.TemplateBatches!); EqualData(original, upgraded);
        var backupDatabase = Path.ChangeExtension(healthy.LastMigrationBackup.Replace(".calorie-backup.json", ".db"), ".db");
        Assert.IsTrue(File.Exists(backupDatabase));
        EqualData(original, new SqliteJournalStore(backupDatabase).ReadSnapshot());
        healthy.Initialize(package);
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(Path.GetDirectoryName(healthy.DatabasePath)!, "recovery"), "*.db"));

        var blocked = Copy("blocked");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(blocked.DatabasePath)!, "recovery"), "明确的备份目录文件冲突检查");
        var blockedHash = Hash(blocked.DatabasePath);
        Assert.Throws<IOException>(() => blocked.Initialize(package));
        Assert.AreEqual(blockedHash, Hash(blocked.DatabasePath));

        var failed = Copy("transaction-failure");
        using (var connection = new SqliteConnection($"Data Source={failed.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER deny_upgrade_metadata BEFORE INSERT ON metadata WHEN NEW.key='food_package' BEGIN SELECT RAISE(ABORT,'明确迁移事务故障'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => failed.Initialize(package));
        var failedData = failed.ReadSnapshot(); Assert.AreEqual(original.SchemaVersion, failedData.SchemaVersion); EqualData(original, failedData);
        using (var connection = new SqliteConnection($"Data Source={failed.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('meal_templates','template_batches')";
            Assert.AreEqual(0L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('entries') WHERE name='weight_calculation'";
            Assert.AreEqual(original.SchemaVersion >= 2 ? 1L : 0L, command.ExecuteScalar());
        }
        JsonFiles.WriteNew(Path.Combine(root, "migration-component.json"), new
        {
            Success = true, ActualLegacySchema = original.SchemaVersion, NewSchema = upgraded.SchemaVersion,
            OriginalDataRetained = true, UpgradeBackupsVerified = true, BackupFailurePreservesBytes = true,
            RealSqliteFailureRollsBackSchemaAndData = true, HealthyDataDirectory = Path.GetDirectoryName(healthy.DatabasePath),
            UpgradeBackup = healthy.LastMigrationBackup, UpgradeDatabase = backupDatabase
        });

        SqliteJournalStore Copy(string name)
        {
            var path = Path.Combine(root, name, "journal.db"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(legacyDatabase, path, false);
            return new SqliteJournalStore(path);
        }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void EqualData(JournalSnapshot expected, JournalSnapshot actual)
    {
        Assert.AreEqual(JsonSerializer.Serialize(expected.Foods), JsonSerializer.Serialize(actual.Foods));
        Assert.AreEqual(JsonSerializer.Serialize(expected.Meals), JsonSerializer.Serialize(actual.Meals));
        Assert.AreEqual(JsonSerializer.Serialize(expected.Entries), JsonSerializer.Serialize(actual.Entries));
        Assert.AreEqual(JsonSerializer.Serialize(expected.Portions ?? []), JsonSerializer.Serialize(actual.Portions ?? []));
    }
}
