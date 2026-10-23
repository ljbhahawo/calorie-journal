using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalorieJournal.Core;

namespace CalorieJournal.Infrastructure;

public sealed class FoodLookupService(string catalogDirectory, string cacheDirectory)
{
    public const string TfdaDownloadUrl = "https://data.fda.gov.tw/data/opendata/export/20/json";
    private const int MaximumDownloadBytes = 12 * 1024 * 1024;
    private readonly string cachePath = Path.Combine(cacheDirectory, "tfda-protein-v1.json");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    public FoodReferenceCatalog[] Load(bool bundledOnly = false)
    {
        var usdaPath = Path.Combine(catalogDirectory, "usda.json");
        var usda = ProteinSupplement.Apply(ChineseFoodIndex.Apply(LoadOne(usdaPath, "USDA SR Legacy"), Path.Combine(catalogDirectory, "usda-zh.json"), usdaPath), catalogDirectory, true);
        var legacyCache = Path.Combine(cacheDirectory, "tfda.json");
        var tfdaPath = !bundledOnly && File.Exists(cachePath) ? cachePath : !bundledOnly && File.Exists(legacyCache) ? legacyCache : Path.Combine(catalogDirectory, "tfda.json");
        var tfda = ProteinSupplement.Apply(LoadOne(tfdaPath, "TFDA"), catalogDirectory, tfdaPath == Path.Combine(catalogDirectory, "tfda.json"));
        return [usda, tfda];
    }

    private static FoodReferenceCatalog LoadOne(string path, string provider)
    {
        var catalog = JsonFiles.Read<FoodReferenceCatalog>(path);
        catalog.Validate();
        Validation.Require(catalog.Provider == provider, "参考目录与来源不匹配。");
        return Normalize(catalog);
    }

    public static string Simplify(string text) => FoodSearch.Simplify(text);

    private static FoodReferenceCatalog Normalize(FoodReferenceCatalog catalog) => catalog with
    {
        Foods = catalog.Foods.Select(food => food with
        {
            Name = Simplify(food.Name), Aliases = Simplify(food.Aliases),
            Preparation = Simplify(food.Preparation), Category = Simplify(food.Category)
        }).ToArray()
    };

    public async Task<FoodReferenceCatalog> UpdateTfdaAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, TfdaDownloadUrl);
        request.Headers.UserAgent.ParseAdd("CalorieJournal/1.5.0");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Validation.Require(response.Content.Headers.ContentLength is null or > 0 and <= MaximumDownloadBytes, "官方资料下载大小超出限制。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var download = new MemoryStream();
        var buffer = new byte[81920];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            Validation.Require(download.Length + length <= MaximumDownloadBytes, "官方资料下载超过12MB，已停止。");
            await download.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        download.Position = 0;
        var catalog = await ReadTfdaAsync(download, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var normalized = Normalize(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDirectory);
        var staging = cachePath + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            JsonFiles.WriteNew(staging, catalog);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, cachePath, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return normalized;
    }

    public static async Task<FoodReferenceCatalog> ReadTfdaAsync(Stream archiveStream, DateTimeOffset retrievedAtUtc, CancellationToken cancellationToken)
    {
        Validation.Require(archiveStream.CanSeek && archiveStream.Length is > 0 and <= MaximumDownloadBytes, "TFDA压缩资料大小无效。");
        archiveStream.Position = 0;
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false));
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, true);
        Validation.Require(archive.Entries.Count == 1 && archive.Entries[0].Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase), "TFDA压缩资料结构已变化。");
        var entry = archive.Entries[0];
        Validation.Require(entry.Length is > 0 and <= 160 * 1024 * 1024, "TFDA解压大小超出160MB限制。");
        await using var json = new BoundedReadStream(entry.Open(), 160 * 1024 * 1024);
        var foods = new List<FoodReference>();
        var proteins = new Dictionary<string, long?>(StringComparer.Ordinal);
        var options = new JsonSerializerOptions { AllowDuplicateProperties = false, MaxDepth = 32 };
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<TfdaRow>(json, options, cancellationToken).ConfigureAwait(false))
        {
            if (row?.Analysis == "粗蛋白")
            {
                Validation.Require(!string.IsNullOrWhiteSpace(row.Id) && row.Unit?.Trim() == "g" && !proteins.ContainsKey(row.Id), "TFDA蛋白质编号、单位或重复行无效。");
                long? protein = null;
                if (!string.IsNullOrWhiteSpace(row.Amount))
                { Validation.Require(decimal.TryParse(row.Amount.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amountProtein) && amountProtein is >= 0 and <= 100, "TFDA蛋白质数值无效。"); protein = Nutrition.Round(amountProtein * 100); }
                proteins.Add(row.Id, protein); continue;
            }
            if (row?.Analysis != "熱量") continue;
            Validation.Require(row.Unit?.Trim().Equals("kcal", StringComparison.OrdinalIgnoreCase) == true, "TFDA热量单位已变化，未更新资料。");
            Validation.Require(decimal.TryParse(row.Amount?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && amount is >= 0 and <= 10000, "TFDA热量缺失或数值无效，未更新资料。");
            Validation.Require(!string.IsNullOrWhiteSpace(row.Id) && !string.IsNullOrWhiteSpace(row.Name), "TFDA食物缺少名称或整合编号。");
            foods.Add(new FoodReference
            {
                SourceId = row.Id, Name = row.Name, OriginalName = row.Name,
                Aliases = (row.Aliases ?? "") + " " + (row.EnglishName ?? ""), Preparation = row.Description ?? "", Category = row.Category ?? "",
                KcalPer100gHundredths = (long)decimal.Round(amount * 100, 0, MidpointRounding.AwayFromZero),
                SourceNote = "衛生福利部食品藥物管理署，食品營養成分資料集；分析項：熱量；每100克可食部分；政府資料開放授權條款第1版 https://data.gov.tw/license" +
                    (amount * 100 == decimal.Truncate(amount * 100) ? "" : $"；原始值{amount.ToString(CultureInfo.InvariantCulture)}kcal，显示值四舍五入至两位")
            });
            Validation.Require(foods.Count <= 20000, "TFDA食物条目超出限制。");
        }
        var catalog = new FoodReferenceCatalog
        {
            Provider = "TFDA", Version = "获取" + retrievedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RetrievedAtUtc = retrievedAtUtc, SourceUrl = "https://data.gov.tw/dataset/8543", LicenseUrl = "https://data.gov.tw/license",
            ArchiveSha256 = sha, Foods = foods.Select(food => food with { ProteinPer100gHundredths = proteins.GetValueOrDefault(food.SourceId), SourceNote = food.SourceNote + "；蛋白质分析：粗蛋白/g，同一官方档案，缺失保留未填写" }).OrderBy(food => food.SourceId, StringComparer.Ordinal).ToArray()
        };
        catalog.Validate();
        return catalog;
    }

    private sealed record TfdaRow
    {
        [JsonPropertyName("分析項")] public string? Analysis { get; init; }
        [JsonPropertyName("含量單位")] public string? Unit { get; init; }
        [JsonPropertyName("每100克含量")] public string? Amount { get; init; }
        [JsonPropertyName("整合編號")] public string? Id { get; init; }
        [JsonPropertyName("樣品名稱")] public string? Name { get; init; }
        [JsonPropertyName("俗名")] public string? Aliases { get; init; }
        [JsonPropertyName("樣品英文名稱")] public string? EnglishName { get; init; }
        [JsonPropertyName("內容物描述")] public string? Description { get; init; }
        [JsonPropertyName("食品分類")] public string? Category { get; init; }
    }

    private sealed class BoundedReadStream(Stream inner, long maximum) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        private int Count(int count)
        {
            consumed += count;
            if (consumed > maximum) throw new IOException("TFDA实际展开资料超过160MB，已停止读取。");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
