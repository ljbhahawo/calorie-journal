using CalorieJournal.Core;

namespace CalorieJournal.Infrastructure;

public sealed class BackupService(SqliteJournalStore store, string recoveryDirectory)
{
    public JournalSnapshot Inspect(string path)
    {
        var snapshot = JsonFiles.Read<JournalSnapshot>(path);
        Validation.Snapshot(snapshot);
        return snapshot;
    }

    public void Export(string path)
    {
        var snapshot = store.ReadSnapshot();
        Validation.Snapshot(snapshot);
        JsonFiles.WriteNew(path, snapshot);
    }

    public string Restore(JournalSnapshot snapshot)
    {
        Validation.Snapshot(snapshot);
        Directory.CreateDirectory(recoveryDirectory);
        var path = Path.Combine(recoveryDirectory, $"恢复前_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.calorie-backup.json");
        Export(path);
        store.ReplaceSnapshot(snapshot);
        return path;
    }
}
