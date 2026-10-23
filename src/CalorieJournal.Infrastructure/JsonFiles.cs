using System.Text.Json;
using System.Text.Json.Serialization;
using CalorieJournal.Core;

namespace CalorieJournal.Infrastructure;

public static class JsonFiles
{
    public const long MaximumBackupBytes = 1_073_741_824;
    public const long MaximumDocumentBytes = 52_428_800;
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static T Read<T>(string path)
    {
        var file = new FileInfo(path);
        var maximum = typeof(T) == typeof(JournalSnapshot) ? MaximumBackupBytes : MaximumDocumentBytes;
        Validation.Require(file.Exists && file.Length > 0 && file.Length <= maximum, $"文件不存在、为空或超过{maximum / 1048576}MiB限制。超限个人库可先保存SQLite数据库归档。");
        using var stream = file.OpenRead();
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new ValidationException("文件内容为空。");
    }

    public static void WriteNew<T>(string path, T value)
    {
        if (value is JournalSnapshot snapshot) Validation.Snapshot(snapshot);
        Validation.Require(!File.Exists(path), "目标文件已存在，请选择新名称。");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var staging = fullPath + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                var maximum = value is JournalSnapshot ? MaximumBackupBytes : MaximumDocumentBytes;
                Validation.Require(stream.Length <= maximum, $"输出超过{maximum / 1048576}MiB限制，未生成完成文件。请先保存SQLite数据库归档。");
                stream.Flush(true);
            }
            if (value is JournalSnapshot) Validation.Snapshot(Read<JournalSnapshot>(staging));
            File.Move(staging, fullPath, false);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
