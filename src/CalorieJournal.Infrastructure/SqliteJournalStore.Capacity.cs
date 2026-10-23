using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore
{
    // SQLite存储字节×6覆盖JSON最坏转义；固定字段空间及头部预算不依赖个人内容。
    private sealed class WriteCapacity
    {
        private static readonly (string Table, int Maximum)[] Tables =
        [ ("foods", 100000), ("meals", 1000), ("entries", 100000), ("portions", 100000),
          ("meal_templates", 1000), ("template_batches", 100000), ("body_measurements", 100000),
          ("intake_goals", 100000), ("exercise_records", 100000) ];
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        private readonly (long[] Counts, long Bytes) before;
        public WriteCapacity(SqliteConnection connection, SqliteTransaction transaction)
        {
            this.connection = connection; this.transaction = transaction; before = Read();
        }
        private (long[] Counts, long Bytes) Read()
        {
            var counts = new long[Tables.Length]; long bytes = 65536;
            for (var index = 0; index < Tables.Length; index++)
            {
                var table = Tables[index].Table; var fields = new List<string>();
                using (var columns = Command(connection, transaction, $"PRAGMA table_info({table})"))
                using (var reader = columns.ExecuteReader())
                    while (reader.Read()) if (reader.GetString(2) == "TEXT") fields.Add($"COALESCE(length(CAST(\"{reader.GetString(1)}\" AS BLOB)),0)");
                using var query = Command(connection, transaction, $"SELECT COUNT(*), COALESCE(SUM({string.Join("+", fields)}),0) FROM {table}");
                using var result = query.ExecuteReader(); result.Read(); counts[index] = result.GetInt64(0);
                bytes = checked(bytes + counts[index] * 4096 + result.GetInt64(1) * 6);
            }
            return (counts, bytes);
        }
        public void Commit()
        {
            var after = Read();
            for (var index = 0; index < Tables.Length; index++)
                Validation.Require(after.Counts[index] <= Tables[index].Maximum || after.Counts[index] <= before.Counts[index], $"{Tables[index].Table}达到完整备份数量上限；可修改已有资料，不能增加超限数量。");
            Validation.Require(after.Bytes <= JsonFiles.MaximumBackupBytes || after.Bytes <= before.Bytes, "当前资料达到安全备份字节预算，新增或扩大资料已停止。可先导出数据库归档，再保全和整理资料。");
            transaction.Commit();
        }
    }
}
