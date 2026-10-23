using System.Text.Json;
using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore
{
    private static IntakeGoal[] IntakeGoals(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT id,effective_date,body FROM intake_goals ORDER BY effective_date DESC");
        using var reader = command.ExecuteReader(); var values = new List<IntakeGoal>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<IntakeGoal>(reader.GetString(2), JsonFiles.Options) ?? throw new ValidationException("目标记录内容为空。");
            Validation.Require(item.Id.ToString() == reader.GetString(0) && item.EffectiveDate.ToString("yyyy-MM-dd") == reader.GetString(1), "目标索引与内容不一致。");
            PersonalRecordValidation.Goal(item); values.Add(item);
        }
        return values.ToArray();
    }
    private static ExerciseRecord[] Exercises(SqliteConnection connection, SqliteTransaction? transaction, DateOnly from, DateOnly to)
    {
        using var command = Command(connection, transaction, "SELECT id,entry_date,body FROM exercise_records WHERE entry_date >= $from AND entry_date <= $to ORDER BY entry_date DESC,id", ("$from", from.ToString("yyyy-MM-dd")), ("$to", to.ToString("yyyy-MM-dd")));
        using var reader = command.ExecuteReader(); var values = new List<ExerciseRecord>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<ExerciseRecord>(reader.GetString(2), JsonFiles.Options) ?? throw new ValidationException("锻炼记录内容为空。");
            Validation.Require(item.Id.ToString() == reader.GetString(0) && item.Date.ToString("yyyy-MM-dd") == reader.GetString(1), "锻炼索引与内容不一致。");
            PersonalRecordValidation.Exercise(item, false); values.Add(item);
        }
        return values.ToArray();
    }
    private static void PutIntakeGoal(SqliteConnection connection, SqliteTransaction transaction, IntakeGoal value) => Execute(connection, transaction,
        "INSERT INTO intake_goals VALUES($id,$date,$body) ON CONFLICT(id) DO UPDATE SET effective_date=excluded.effective_date,body=excluded.body",
        ("$id", value.Id.ToString()), ("$date", value.EffectiveDate.ToString("yyyy-MM-dd")), ("$body", JsonSerializer.Serialize(value, JsonFiles.Options)));
    private static void PutExercise(SqliteConnection connection, SqliteTransaction transaction, ExerciseRecord value) => Execute(connection, transaction,
        "INSERT INTO exercise_records VALUES($id,$date,$body) ON CONFLICT(id) DO UPDATE SET entry_date=excluded.entry_date,body=excluded.body",
        ("$id", value.Id.ToString()), ("$date", value.Date.ToString("yyyy-MM-dd")), ("$body", JsonSerializer.Serialize(value, JsonFiles.Options)));
    public IntakeGoal[] ReadIntakeGoals() { using var connection = Open(); return IntakeGoals(connection, null); }
    public ExerciseRecord[] ReadExercises(DateOnly from, DateOnly to) { using var connection = Open(); return Exercises(connection, null, from, to); }
    public void SaveIntakeGoal(IntakeGoal value)
    {
        PersonalRecordValidation.Goal(value); using var connection = Open(); using var transaction = connection.BeginTransaction(); var capacity = new WriteCapacity(connection, transaction);
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM intake_goals WHERE effective_date=$date AND id<>$id", ("$date", value.EffectiveDate.ToString("yyyy-MM-dd")), ("$id", value.Id.ToString()))) == 0, "该生效日期已有目标，请选择已有记录后修改。");
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM intake_goals")) < 100000 || Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM intake_goals WHERE id=$id", ("$id", value.Id.ToString()))) == 1, "目标已达到10万项上限。");
        PutIntakeGoal(connection, transaction, value); capacity.Commit();
    }
    public void SaveExercise(ExerciseRecord value)
    {
        PersonalRecordValidation.Exercise(value); using var connection = Open(); using var transaction = connection.BeginTransaction(); var capacity = new WriteCapacity(connection, transaction);
        Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM exercise_records")) < 100000 || Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM exercise_records WHERE id=$id", ("$id", value.Id.ToString()))) == 1, "锻炼记录已达到10万项上限。");
        PutExercise(connection, transaction, value); capacity.Commit();
    }
    public void DeleteIntakeGoal(Guid id)
    {
        using var connection = Open(); using var command = Command(connection, null, "DELETE FROM intake_goals WHERE id=$id", ("$id", id.ToString()));
        Validation.Require(command.ExecuteNonQuery() == 1, "目标不存在或已经删除。");
    }
    public void DeleteExercise(Guid id)
    {
        using var connection = Open(); using var command = Command(connection, null, "DELETE FROM exercise_records WHERE id=$id", ("$id", id.ToString()));
        Validation.Require(command.ExecuteNonQuery() == 1, "锻炼记录不存在或已经删除。");
    }
}
