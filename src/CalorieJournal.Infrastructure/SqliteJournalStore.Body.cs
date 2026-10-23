using System.Text.Json;
using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore
{
    private static BodyMeasurement[] BodyMeasurements(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,entry_date,body FROM body_measurements ORDER BY entry_date DESC");
        using var reader = command.ExecuteReader(); var values = new List<BodyMeasurement>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<BodyMeasurement>(reader.GetString(2), JsonFiles.Options) ?? throw new ValidationException("身体记录内容为空。");
            Validation.Require(item.Id.ToString() == reader.GetString(0) && item.Date.ToString("yyyy-MM-dd") == reader.GetString(1), "身体记录索引与内容不一致。");
            BodyCalculator.Validate(item, false); values.Add(item);
        }
        return values.ToArray();
    }
    private static void PutBodyMeasurement(SqliteConnection connection, SqliteTransaction transaction, BodyMeasurement value) => Execute(connection, transaction,
        "INSERT INTO body_measurements VALUES($id,$date,$body) ON CONFLICT(id) DO UPDATE SET entry_date=excluded.entry_date,body=excluded.body",
        ("$id", value.Id.ToString()), ("$date", value.Date.ToString("yyyy-MM-dd")), ("$body", JsonSerializer.Serialize(value, JsonFiles.Options)));
    public BodyMeasurement[] ReadBodyMeasurements() { using var connection = Open(); return BodyMeasurements(connection, null); }
    public void SaveBodyMeasurement(BodyMeasurement value)
    {
        BodyCalculator.Validate(value); using var connection = Open(); using var transaction = connection.BeginTransaction();
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM body_measurements WHERE entry_date=$date AND id<>$id", ("$date", value.Date.ToString("yyyy-MM-dd")), ("$id", value.Id.ToString()))) == 0, "该日期已有体重记录，请选择已有记录后修改。");
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM body_measurements")) < 100000 || Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM body_measurements WHERE id=$id", ("$id", value.Id.ToString()))) == 1, "身体记录已达到10万项上限。");
        PutBodyMeasurement(connection, transaction, value); transaction.Commit();
    }
    public void DeleteBodyMeasurement(Guid id)
    {
        using var connection = Open(); using var command = Command(connection, null, "DELETE FROM body_measurements WHERE id=$id", ("$id", id.ToString()));
        Validation.Require(command.ExecuteNonQuery() == 1, "身体记录不存在或已经删除。");
    }
}
