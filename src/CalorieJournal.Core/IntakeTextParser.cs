using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace CalorieJournal.Core;

public enum ImportRowKind { Detail, MealTotal, Unrecognized }
public sealed record ImportDraft(
    Guid Id, int LineNumber, string DateText, string MealName, string Name, string EnergyText,
    decimal? Lower, decimal? Upper, string Hint, string Raw, string Note, ImportRowKind Kind, string LocalTime = "");
public sealed record ImportDocument(string Fingerprint, ImportDraft[] Rows, string[] Messages);

public static class IntakeTextParser
{
    public const string RuleVersion = "text-1";
    private const string Number = @"\d+(?:\.\d+)?";
    private static readonly Regex DatePattern = new(@"^(?:(?<year>\d{4})[年./-])?(?<month>\d{1,2})[月./-](?<day>\d{1,2})日?$", RegexOptions.CultureInvariant);
    private static readonly Regex MealPattern = new(@"^(?<meal>早餐|早上|早晨|早饭|午餐|中午|午饭|晚餐|晚上|晚饭|加餐|夜宵|下午茶|上午|下午)(?:[：:\s]*(?<rest>.*))$", RegexOptions.CultureInvariant);
    private static readonly Regex EnergyPattern = new(
        $@"(?:(?:约|大约|可能|大卡[：:]?|热量[：:]?)\s*)?(?<value>{Number}(?:\s*\+\s*{Number})*)(?:\s*(?<range>[—–~～至到-])\s*(?<upper>{Number}))?(?:\s*大?卡|\s*kcal)?(?<plus>\s*\+|\s*往上|\s*以上)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryDate(string value, int year, out DateOnly date)
    {
        date = default;
        var match = DatePattern.Match(value.Trim());
        if (!match.Success) return false;
        var yearText = match.Groups["year"].Success ? match.Groups["year"].Value : year.ToString(CultureInfo.InvariantCulture);
        return DateOnly.TryParseExact($"{yearText}-{match.Groups["month"].Value.PadLeft(2, '0')}-{match.Groups["day"].Value.PadLeft(2, '0')}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static string NormalizeMeal(string meal) => meal.Trim() switch
    {
        "早上" or "早晨" or "早饭" => "早餐", "中午" or "午饭" => "午餐",
        "晚上" or "晚饭" => "晚餐", var value => value
    };

    public static ImportDocument Parse(string text, int year, string format)
    {
        Validation.Require(year is >= 1900 and <= 9999, "缺省年份须为1900到9999。" );
        Validation.Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 200000, "请粘贴记录，单次最多20万字符。" );
        Validation.Require(format is "自由文字" or "表格（制表符）" or "CSV（逗号）", "请选择支持的粘贴格式。" );
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{RuleVersion}:{year}:{format}:{normalized}")));
        var rows = format == "自由文字" ? ParseText(normalized, year, fingerprint) : ParseTable(normalized, year, fingerprint, format == "CSV（逗号）" ? "," : "\t");
        Validation.Require(rows.Length is > 0 and <= 1000, "未找到记录，或草稿超过1000行。请检查格式或分批粘贴。" );
        return new(fingerprint, rows, ["日期中的缺省年份已明确补齐，请核对；原文和估算说明将随导入记录保存。", "餐次合计默认仅作参考；同餐不能同时导入合计与明细。"]);
    }

    private static Guid RowId(string fingerprint, string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint + ":" + key)).AsSpan(0, 16));
    private static ImportDraft Draft(string fingerprint, int line, string key, string date, string meal, string body, string raw, ImportRowKind kind, string name = "", string note = "")
    {
        Validation.Require(raw.Length <= 2000, $"第{line}行超过2000字，请分行后再识别。" );
        if (Regex.IsMatch(body, @"\d,\d{3}(?!\d)"))
            return new(RowId(fingerprint,key),line,date,meal,name.Length > 0 ? name : body,"",null,null,"数值含千位逗号或多组数字，请手动核对热量",raw,note,kind);
        if (kind == ImportRowKind.MealTotal && body.Trim(' ', '：', ':') is "无" or "没吃" or "未吃")
            return new(RowId(fingerprint,key),line,date,meal,$"{meal}（原文记为无）","0",0,0,"明确记为无；按零热量导入",raw,note,kind);
        var matches = EnergyPattern.Matches(body).Cast<Match>().Where(m => IsEnergy(body, m)).ToArray();
        if (matches.Length == 0)
            return new(RowId(fingerprint,key),line,date,meal,name.Length > 0 ? name : body.Trim(),"",null,null,"未识别到确定热量，请补齐或取消选择",raw,note,kind == ImportRowKind.MealTotal ? kind : ImportRowKind.Unrecognized);
        // 同一行的修正值作为候选，保留前值和完整原文供用户核对。
        var selected = matches[^1];
        var value = ReadSum(selected.Groups["value"].Value);
        decimal? upper = selected.Groups["upper"].Success ? Validation.Number(selected.Groups["upper"].Value,"区间上限") : value;
        var lowerOnly = selected.Groups["plus"].Success;
        if (lowerOnly) upper = null;
        var range = selected.Groups["range"].Success;
        var hint = range ? $"区间 {value:0.###}–{upper:0.###}，请选择估计值" : lowerOnly ? $"下限 {value:0.###}，请填写估计值" : "原文热量候选，请核对";
        if (matches.Length > 1) hint += "；本行多处数值，采用最后一处热量候选";
        if (!Regex.IsMatch(selected.Value, @"大?卡|kcal", RegexOptions.IgnoreCase)) hint += "；原文省略大卡单位";
        if (value > upper) hint += "；原区间上下限相反，请手动核对";
        var originalName = body[..selected.Index].Trim(' ', '：', ':', '，', ',', '约', '大');
        if (name.Length == 0) name = kind == ImportRowKind.MealTotal ? meal + "合计（原文估计）" : originalName;
        var energy = range || lowerOnly ? "" : value.ToString("0.###", CultureInfo.InvariantCulture);
        return new(RowId(fingerprint,key),line,date,meal,name,energy,value,upper,hint,raw,note,kind);
    }

    private static bool IsEnergy(string body, Match match)
    {
        var following = body[(match.Index + match.Length)..].TrimStart();
        var before = body[..match.Index];
        if (Regex.IsMatch(before, @"[A-Za-z\d.+/\-]$")) return false;
        if (following == "=" && match.Groups["value"].Value.Contains('+')) return true;
        if (Regex.IsMatch(following, @"^(?:kg|g|克|千克|个|颗|勺|斤|毫升|ml|kJ|千焦|焦耳|卡路里|\*|×|=|%|\.\d)", RegexOptions.IgnoreCase)) return false;
        var value = match.Value;
        return Regex.IsMatch(value, @"约|可能|热量|大?卡|kcal", RegexOptions.IgnoreCase)
            || following.Length == 0 || (before.TrimEnd().EndsWith('=') && following.Length == 0);
    }

    private static decimal ReadSum(string expression)
    {
        try { return expression.Split('+').Sum(x => Validation.Number(x.Trim(), "热量")); }
        catch (OverflowException) { throw new ValidationException("热量表达式超出数值范围。" ); }
    }

    private static ImportDraft[] ParseText(string text, int year, string fingerprint)
    {
        var result = new List<ImportDraft>();
        var date = ""; var meal = "未标餐次"; var blankAfterDetail = false;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index].Trim();
            if (raw.Length == 0) { blankAfterDetail = result.LastOrDefault()?.Kind == ImportRowKind.Detail; continue; }
            var body = Regex.Replace(raw, @"^(?:[-*]\s+|[•·]\s*)", "");
            if (DatePattern.IsMatch(body))
            {
                date = TryDate(body, year, out var parsed) ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : body;
                meal = "未标餐次"; blankAfterDetail = false; continue;
            }
            var mealMatch = MealPattern.Match(body);
            var kind = ImportRowKind.Detail;
            if (mealMatch.Success)
            {
                meal = NormalizeMeal(mealMatch.Groups["meal"].Value); body = mealMatch.Groups["rest"].Value.Trim(' ', '：', ':');
                blankAfterDetail = false;
                if (body.Length == 0) continue;
                kind = ImportRowKind.MealTotal;
            }
            else if (blankAfterDetail) meal = "未标餐次";
            blankAfterDetail = false;
            var draft = Draft(fingerprint,index+1,$"line:{index}",date,meal,body,raw,kind);
            result.Add(draft);
            // 明确分隔出的未定量食物须留作独立待核对行，不能在已有热量中隐含包含。
            if (kind == ImportRowKind.Detail)
            {
                var tail = Regex.Match(body, @"(?:大?卡|kcal)[，,；;]\s*(?<tail>[^\d]+)$", RegexOptions.IgnoreCase);
                if (tail.Success)
                    result.Add(new(RowId(fingerprint,$"line:{index}:tail"),index+1,date,meal,tail.Groups["tail"].Value,"",null,null,"原文另有未定量内容，请补齐热量或取消选择",raw,"",ImportRowKind.Unrecognized));
            }
            Validation.Require(result.Count <= 1000, "草稿超过1000行，请分批粘贴。" );
        }
        return result.ToArray();
    }

    private static ImportDraft[] ParseTable(string text, int year, string fingerprint, string delimiter)
    {
        using var reader = new StringReader(text);
        using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(delimiter);
        try
        {
            var headers = parser.ReadFields() ?? [];
            int Column(params string[] names) => Array.FindIndex(headers, h => names.Contains(h.Trim(), StringComparer.OrdinalIgnoreCase));
            var dateColumn = Column("日期","记录日期","date"); var mealColumn = Column("餐次","餐别","meal");
            var nameColumn = Column("名称","食物","食物名称","项目","name","food");
            var energyColumn = Column("大卡","热量","摄入量","本项大卡","kcal","calories");
            var noteColumn = Column("备注","说明","note"); var timeColumn = Column("时间","time");
            Validation.Require(dateColumn >= 0 && mealColumn >= 0 && nameColumn >= 0 && energyColumn >= 0,
                "表格首行需要日期、餐次、名称、大卡列名（列顺序不限）。可直接修改首行；可选列为时间和备注。" );
            var result = new List<ImportDraft>();
            while (!parser.EndOfData)
            {
                var line = checked((int)parser.LineNumber);
                var values = parser.ReadFields() ?? [];
                Validation.Require(values.Length == headers.Length, $"表格第{line}行列数与首行不一致。" );
                var dateText = TryDate(values[dateColumn], year, out var parsed) ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : values[dateColumn];
                var endLine = parser.EndOfData ? text.Split('\n').Length + 1 : checked((int)parser.LineNumber);
                var raw = string.Join("\n", text.Split('\n').Skip(line - 1).Take(endLine - line));
                var note = noteColumn >= 0 ? values[noteColumn] : "";
                result.Add(Draft(fingerprint,line,$"table:{line}",dateText,NormalizeMeal(values[mealColumn]),values[energyColumn],raw,ImportRowKind.Detail,values[nameColumn],note)
                    with { LocalTime = timeColumn >= 0 ? values[timeColumn].Trim() : "" });
                Validation.Require(result.Count <= 1000, "草稿超过1000行，请分批粘贴。" );
            }
            return result.ToArray();
        }
        catch (MalformedLineException ex) { throw new ValidationException($"表格引号或分隔格式无效，第{ex.LineNumber}行。" ); }
    }
}
