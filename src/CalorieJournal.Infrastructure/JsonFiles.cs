using System.Text.Json;
using System.Text.Json.Serialization;
using CalorieJournal.Core;

namespace CalorieJournal.Infrastructure;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static T Read<T>(string path)
    {
        var file = new FileInfo(path);
        Validation.Require(file.Exists && file.Length is > 0 and <= 52_428_800, "文件不存在、为空或超过50MB限制。");
        using var stream = file.OpenRead();
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new ValidationException("文件内容为空。");
    }

    public static void WriteNew<T>(string path, T value)
    {
        Validation.Require(!File.Exists(path), "目标文件已存在，请选择新名称。");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var staging = fullPath + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            File.Move(staging, fullPath, false);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
