using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace CalorieJournal.Core;

public static class FoodSearch
{
    private sealed class Node
    {
        public Dictionary<char, Node> Children { get; } = [];
        public string? Canonical { get; set; }
    }

    private sealed record ContextAlias(string Alias, string[] WhenNameContains, string[] ExceptNameContains, string Concept);
    private sealed record AliasFile(string Version, string[][] Groups, ContextAlias[] ContextAliases);
    private static ContextAlias[] ContextAliases = [];
    public static string TermsSha256 { get; } = ResourceHash("search-terms.json");
    public static string AliasesSha256 { get; } = ResourceHash("search-aliases.json");
    private static readonly Node Lexicon = LoadLexicon();
    private static readonly HashSet<string> StateTokens = LoadStateTokens();

    private static string ResourceHash(string name)
    {
        using var stream = typeof(FoodSearch).Assembly.GetManifestResourceStream("CalorieJournal.Core." + name)
            ?? throw new InvalidOperationException("缺少食物检索词表资源。");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Node LoadLexicon()
    {
        using var stream = typeof(FoodSearch).Assembly.GetManifestResourceStream("CalorieJournal.Core.search-aliases.json")
            ?? throw new InvalidOperationException("缺少食物检索别名资源。");
        var aliases = JsonSerializer.Deserialize<AliasFile>(stream) ?? throw new InvalidOperationException("食物别名资源为空。");
        Validation.Require(!string.IsNullOrWhiteSpace(aliases.Version) && aliases.Groups.Length > 0, "食物别名版本或词组无效。");
        ContextAliases = aliases.ContextAliases.Select(rule => rule with
        {
            Alias = Normalize(rule.Alias), Concept = Normalize(rule.Concept),
            WhenNameContains = rule.WhenNameContains.Select(Normalize).ToArray(), ExceptNameContains = rule.ExceptNameContains.Select(Normalize).ToArray()
        }).ToArray();
        var root = new Node();
        foreach (var group in aliases.Groups)
        {
            Validation.Require(group.Length > 0 && group.All(x => !string.IsNullOrWhiteSpace(x)), "食物别名词组无效。");
            var canonical = Normalize(group[0]);
            foreach (var alias in group)
            {
                var node = root;
                foreach (var character in Normalize(alias))
                {
                    if (!node.Children.TryGetValue(character, out var child)) node.Children[character] = child = new Node();
                    node = child;
                }
                Validation.Require(node.Canonical is null || node.Canonical == canonical, "食物别名对应多个不同词组。");
                node.Canonical = canonical;
            }
        }
        using var termsStream = typeof(FoodSearch).Assembly.GetManifestResourceStream("CalorieJournal.Core.search-terms.json")
            ?? throw new InvalidOperationException("缺少中文食材检索词表。");
        using var terms = JsonDocument.Parse(termsStream);
        foreach (var section in new[] { "FoodTerms", "StateTerms" })
            foreach (var entry in terms.RootElement.GetProperty(section).EnumerateObject())
            {
                var text = Normalize(entry.Value.GetString()!).Split('（')[0];
                if (text.Length == 0) continue;
                var node = root;
                foreach (var character in text)
                {
                    if (!node.Children.TryGetValue(character, out var child)) node.Children[character] = child = new Node();
                    node = child;
                }
                node.Canonical ??= text;
            }
        foreach (var entry in terms.RootElement.GetProperty("StateTerms").EnumerateObject())
        {
            var canonical = Normalize(entry.Value.GetString()!);
            var translatedNode = root;
            foreach (var character in canonical) translatedNode = translatedNode.Children[character];
            var node = root;
            foreach (var character in Normalize(entry.Name))
            {
                if (!node.Children.TryGetValue(character, out var child)) node.Children[character] = child = new Node();
                node = child;
            }
            node.Canonical ??= translatedNode.Canonical ?? canonical;
        }
        // 菰米的英文食物名不能拆成“野生”状态，避免误推断采集方式。
        var wildRice = root;
        foreach (var character in "wild rice")
        {
            if (!wildRice.Children.TryGetValue(character, out var child)) wildRice.Children[character] = child = new Node();
            wildRice = child;
        }
        wildRice.Canonical = "菰米";
        return root;
    }

    private static HashSet<string> LoadStateTokens()
    {
        using var stream = typeof(FoodSearch).Assembly.GetManifestResourceStream("CalorieJournal.Core.search-terms.json")!;
        using var terms = JsonDocument.Parse(stream);
        return terms.RootElement.GetProperty("StateTerms").EnumerateObject()
            .SelectMany(entry => Canonicalize(Normalize(entry.Value.GetString()!)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
    }

    public static string Normalize(string text) => Simplify(text).Trim().ToLowerInvariant();

    public static string Simplify(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC);
        if (!text.Any(c => c > 127)) return text;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("中文简繁转换需要Windows。");
        const uint simplifiedChinese = 0x02000000;
        var size = LCMapStringEx("zh-CN", simplifiedChinese, text, text.Length, null, 0, 0, 0, 0);
        if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows简繁转换失败。");
        var buffer = new char[size];
        var written = LCMapStringEx("zh-CN", simplifiedChinese, text, text.Length, buffer, buffer.Length, 0, 0, 0);
        if (written == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows简繁转换失败。");
        return new string(buffer, 0, written);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string localeName, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, nint version, nint reserved, nint sortHandle);

    private static string Canonicalize(string normalized, bool isQuery = false, Dictionary<string, string>? contextAliases = null)
    {
        var result = new StringBuilder();
        for (var index = 0; index < normalized.Length;)
        {
            var node = Lexicon; string? canonical = null; var end = index;
            for (var cursor = index; cursor < normalized.Length && node.Children.TryGetValue(normalized[cursor], out node!); cursor++)
                if (node.Canonical is not null) { canonical = node.Canonical; end = cursor + 1; }
            if (canonical is null) { result.Append(normalized[index++]); continue; }
            if (char.IsAsciiLetter(normalized[index]) &&
                (index > 0 && char.IsAsciiLetter(normalized[index - 1]) || end < normalized.Length && char.IsAsciiLetter(normalized[end])))
            {
                result.Append(normalized[index++]); continue;
            }
            if (!isQuery && end == index + 1 && "鸡猪牛羊鸭鹅心肝肾".Contains(normalized[index]) &&
                (index > 0 && char.IsLetter(normalized[index - 1]) || end < normalized.Length && char.IsLetter(normalized[end])))
            {
                // 单字食材别名不解释复合名称，例如牛番茄、羊角椒、田鸡。
                result.Append(normalized[index++]); continue;
            }
            if (contextAliases is not null && contextAliases.TryGetValue(canonical, out var contextual)) canonical = contextual;
            // 最长词优先，确保“生菜”“花生”等不会被单字加工状态切开。
            result.Append(' ').Append('\u001f').Append(canonical).Append('\u001f').Append(' '); index = end;
        }
        return result.ToString();
    }

    public sealed record Query(string Text, string CanonicalText, string[] Terms, string[] LiteralTerms, string[] RequiredStates);
    public sealed record Document(string Name, string CanonicalName, string[] Aliases, string Content, string[] LiteralFields)
    {
        public bool Matches(Query query) => query.Terms.All(term => Content.Contains(term, StringComparison.Ordinal)) ||
            query.LiteralTerms.Length > 0 && query.RequiredStates.All(term => Content.Contains(term, StringComparison.Ordinal)) &&
            query.LiteralTerms.All(term => LiteralFields.Any(field => field.Contains(term, StringComparison.Ordinal)));
        public int Rank(Query query) => query.Terms.Length == 0 || Name == query.Text || CanonicalName == query.CanonicalText ? 0
            : CanonicalName.StartsWith(query.CanonicalText, StringComparison.Ordinal) ? 1
            : Aliases.Contains(query.CanonicalText, StringComparer.Ordinal) ? 2 : 3;
    }

    public static Query PrepareQuery(string text)
    {
        Validation.Require(text.Length <= 100, "食物关键词最多100字。");
        var normalized = Normalize(text);
        var canonical = Canonicalize(normalized, true).Trim();
        var terms = canonical.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        return new Query(normalized, canonical, terms,
            normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Compact).Where(x => x.Length > 0).ToArray(),
            terms.Where(StateTokens.Contains).ToArray());
    }

    private static string Compact(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    public static Document PrepareDocument(string name, string aliases, params string[] other)
    {
        var fields = new[] { name, aliases }.Concat(other).Select(Normalize).ToArray();
        var normalized = string.Join(' ', fields);
        var identity = fields[0] + " " + (fields.Length > 2 ? fields[2] : "");
        var contextual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in ContextAliases)
            if (rule.WhenNameContains.Any(identity.Contains) && !rule.ExceptNameContains.Any(identity.Contains)) contextual[rule.Alias] = rule.Concept;
        var content = Canonicalize(normalized, contextAliases: contextual);
        // 具体部位仍属于对应肉类；保留具体词，额外支持一般食材查询。
        if (content.Contains("鸡胸肉") || content.Contains("鸡翅") || content.Contains("鸡腿肉")) content += " \u001f鸡肉\u001f";
        if (content.Contains("大米") && content.Contains("熟制")) content += " \u001f米饭\u001f";
        if (content.Contains("白蘑菇") || content.Contains("香菇") || content.Contains("平菇") || content.Contains("金针菇")) content += " \u001f蘑菇\u001f";
        if (content.Contains("四季豆") || content.Contains("豇豆")) content += " 豆角";
        if (content.Contains("生抽") || content.Contains("老抽")) content += " \u001f酱油\u001f";
        if (content.Contains("\u001f蛋清\u001f")) content += " 蛋白";
        return new Document(Normalize(name), Canonicalize(Normalize(name), contextAliases: contextual).Trim(),
            aliases.Split([';', '；', '|', '，', ','], StringSplitOptions.RemoveEmptyEntries).Select(alias => Canonicalize(Normalize(alias), contextAliases: contextual).Trim()).ToArray(), content,
            fields.Select(Compact).ToArray());
    }
}
