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
        var path = CreateRecoveryBackup("恢复前");
        store.ReplaceSnapshot(snapshot);
        return path;
    }

    public string CreateImportBackup() => CreateRecoveryBackup("导入前");

    public void ExportDatabase(string path)
    {
        Validation.Require(File.Exists(store.DatabasePath), "没有可归档的数据库。");
        Validation.Require(!File.Exists(path), "目标文件已存在，请选择新名称。");
        var fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var staging = fullPath + "." + Guid.NewGuid().ToString("N") + ".writing";
        try { store.BackupDatabase(staging); File.Move(staging, fullPath, false); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private string CreateRecoveryBackup(string prefix)
    {
        Directory.CreateDirectory(recoveryDirectory);
        var path = Path.Combine(recoveryDirectory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.calorie-backup.json");
        Export(path);
        return path;
    }
}
