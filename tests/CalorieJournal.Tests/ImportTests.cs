using CalorieJournal.Core;
using CalorieJournal.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Tests;

[TestClass]
public sealed class ImportParserTests
{
    [TestMethod]
    public void HierarchyRangesCorrectionsAndAdditionRemainReviewable()
    {
        var parsed = IntakeTextParser.Parse("10.17\n早餐：约600大卡\n- 100g示例甲 约12.5大卡\n- 示例乙 约30—40大卡\n- 示例丙 约50大卡——调整约45大卡\n晚餐约80+20 =", 2020, "自由文字");
        Assert.HasCount(5, parsed.Rows);
        Assert.AreEqual("2020-10-17", parsed.Rows[0].DateText);
        Assert.AreEqual(ImportRowKind.MealTotal, parsed.Rows[0].Kind);
        Assert.AreEqual("12.5", parsed.Rows[1].EnergyText);
        Assert.AreEqual("100g示例甲", parsed.Rows[1].Name);
        Assert.AreEqual("", parsed.Rows[2].EnergyText);
        Assert.AreEqual(30m, parsed.Rows[2].Lower); Assert.AreEqual(40m, parsed.Rows[2].Upper);
        Assert.AreEqual("45", parsed.Rows[3].EnergyText); Assert.IsTrue(parsed.Rows[3].Hint.Contains("多处"));
        Assert.AreEqual("100", parsed.Rows[4].EnergyText);
        Assert.AreEqual(parsed.Rows[1].Id, IntakeTextParser.Parse("10.17\n早餐：约600大卡\n- 100g示例甲 约12.5大卡\n- 示例乙 约30—40大卡\n- 示例丙 约50大卡——调整约45大卡\n晚餐约80+20 =",2020,"自由文字").Rows[1].Id);
    }

    [TestMethod]
    public void LowerBoundsUnquantifiedTailAndExplicitZeroArePreserved()
    {
        var parsed = IntakeTextParser.Parse("10.17\n上午约800\n下午约500\n早上：无\n中午\n- 两个示例100g\n- 示例甲约50大卡，一点未定量配料\n- 示例乙约150往上\n- 示例丙约100+", 2020, "自由文字");
        Assert.AreEqual("上午", parsed.Rows[0].MealName);
        Assert.AreEqual("下午", parsed.Rows[1].MealName);
        Assert.AreEqual("0", parsed.Rows[2].EnergyText);
        Assert.AreEqual("", parsed.Rows[3].EnergyText);
        Assert.IsTrue(parsed.Rows.Any(x => x.Name == "一点未定量配料" && x.EnergyText == ""));
        Assert.IsTrue(parsed.Rows.Any(x => x.Lower == 150m && x.Upper is null && x.EnergyText == ""));
        Assert.IsTrue(parsed.Rows.Any(x => x.Lower == 100m && x.Upper is null && x.EnergyText == ""));
    }

    [TestMethod]
    public void QuantitiesNegativeAndUnsupportedNumericExpressionsNeverBecomeCalories()
    {
        foreach (var text in new[] { "100g", "1e3大卡", "-5大卡", "1,000大卡", "104*0.9=93.6g", "3/4大卡", "100kJ" })
        {
            var parsed = IntakeTextParser.Parse("2020-10-17\n早餐\n" + text, 2020, "自由文字");
            Assert.AreEqual("", parsed.Rows.Single().EnergyText, text);
        }
        Assert.IsFalse(IntakeTextParser.TryDate("2.30",2020,out _));
        Assert.Throws<ValidationException>(() => IntakeTextParser.Parse(new string('a',200001),2020,"自由文字"));
        Assert.Throws<ValidationException>(() => IntakeTextParser.Parse("10.17",2020,"自由文字"));
    }

    [TestMethod]
    public void RealDelimitedParserHandlesOrderQuotedCommasAndNewlines()
    {
        var tsv = IntakeTextParser.Parse("名称\t热量\t日期\t餐别\t时间\t备注\n表格示例\t12.5\t10.17\t早上\t08:12\t明确验收说明",2020,"表格（制表符）");
        Assert.AreEqual("表格示例",tsv.Rows.Single().Name);
        Assert.AreEqual("早餐",tsv.Rows.Single().MealName);
        Assert.AreEqual("08:12",tsv.Rows.Single().LocalTime);
        Assert.AreEqual("明确验收说明",tsv.Rows.Single().Note);
        var csv = IntakeTextParser.Parse("日期,餐次,名称,大卡,备注\n2020-10-17,午餐,\"示例,甲\",25,\"第一行\n第二行\"",2020,"CSV（逗号）");
        Assert.AreEqual("示例,甲",csv.Rows.Single().Name);
        Assert.AreEqual("第一行\n第二行",csv.Rows.Single().Note);
        Assert.IsTrue(csv.Rows.Single().Raw.Contains("\"示例,甲\""));
        Assert.Throws<ValidationException>(() => IntakeTextParser.Parse("未知,列名\n1,2",2020,"CSV（逗号）"));
        Assert.Throws<ValidationException>(() => IntakeTextParser.Parse("日期,餐次,名称,大卡\n2020-10-17,早餐,\"未闭合,50",2020,"CSV（逗号）"));
    }
}

[TestClass]
public sealed class ImportPersistenceTests
{
    private SqliteJournalStore store = null!;
    private JournalService journal = null!;
    private string directory = "";
    [TestInitialize]
    public void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("CALORIE_TEST_ROOT") ?? throw new InvalidOperationException("未配置测试目录。" );
        directory = Path.Combine(root,"import_" + Guid.NewGuid().ToString("N"));
        store = new SqliteJournalStore(Path.Combine(directory,"journal.db"));
        store.Initialize(JsonFiles.Read<FoodPackage>(Path.Combine(AppContext.BaseDirectory,"data","foods.json")));
        journal = new JournalService(store);
    }
    private static ImportCandidate Candidate(ImportDraft row) => new(row,true,row.DateText,row.MealName,row.Name,row.EnergyText,row.LocalTime,row.Note);

    [TestMethod]
    public void ReviewRejectsOverlappingTotalsAndRequiresResolvedValues()
    {
        var document = IntakeTextParser.Parse("2020-10-17\n早餐约100大卡\n- 明确示例约80大卡\n- 区间示例约10—20大卡",2020,"自由文字");
        var rows = document.Rows.Select(Candidate).ToArray();
        var review = ImportPlanner.Review(rows,document.Fingerprint,journal.Meals(),[]);
        Assert.IsTrue(review.HasErrors);
        Assert.IsTrue(review.Issues.Any(x=>x.Error.Contains("合计与明细")));
        rows[0] = rows[0] with { Included=false }; rows[2]=rows[2] with {EnergyText="15"};
        review=ImportPlanner.Review(rows,document.Fingerprint,journal.Meals(),[]);
        Assert.IsFalse(review.HasErrors); Assert.AreEqual(950L,review.Entries.Sum(x=>x.EnergyKcalTenths));
        journal.ImportEntries(review.Entries);
        Assert.HasCount(2,store.ReadSnapshot().Entries);
        Assert.IsTrue(store.ReadSnapshot().Entries.All(x=>x.SourceSnapshot.Contains("原文：")));
    }

    [TestMethod]
    public void UnknownMealsInvalidDatesAndFutureRecordsAreBlocked()
    {
        var doc=IntakeTextParser.Parse("10.17\n上午约80",2020,"自由文字");
        var row=Candidate(doc.Rows.Single());
        Assert.IsTrue(ImportPlanner.Review([row],doc.Fingerprint,journal.Meals(),[]).HasErrors);
        row=row with {MealName="早餐",DateText="2020-02-30"};
        Assert.IsTrue(ImportPlanner.Review([row],doc.Fingerprint,journal.Meals(),[]).HasErrors);
        row=row with {DateText=DateTime.Today.AddDays(1).ToString("yyyy-MM-dd")};
        Assert.IsTrue(ImportPlanner.Review([row],doc.Fingerprint,journal.Meals(),[]).HasErrors);
        Assert.IsEmpty(store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void StableIdsRejectReimportAndContentDuplicatesRequireReview()
    {
        var doc=IntakeTextParser.Parse("2020-10-17\n早餐\n- 明确示例约80大卡",2020,"自由文字");
        var rows=doc.Rows.Select(Candidate).ToArray();
        var first=ImportPlanner.Review(rows,doc.Fingerprint,journal.Meals(),[]);
        journal.ImportEntries(first.Entries);
        var existing=store.ReadSnapshot().Entries;
        Assert.IsTrue(ImportPlanner.Review(rows,doc.Fingerprint,journal.Meals(),existing).HasErrors);
        Assert.Throws<ValidationException>(()=>journal.ImportEntries(first.Entries));
        var changed=IntakeTextParser.Parse("2020-10-17\n早餐\n- 明确示例约80大卡\n",2020,"自由文字");
        Assert.AreEqual(doc.Rows.Single().Id,changed.Rows.Single().Id);
        var other=IntakeTextParser.Parse("日期\t餐次\t名称\t大卡\n2020-10-17\t早餐\t明确示例\t80",2020,"表格（制表符）");
        var duplicates=ImportPlanner.Review(other.Rows.Select(Candidate).ToArray(),other.Fingerprint,journal.Meals(),existing);
        Assert.IsFalse(duplicates.HasErrors); Assert.IsTrue(duplicates.HasDuplicates);
        Assert.HasCount(1,store.ReadSnapshot().Entries);
    }

    [TestMethod]
    public void RealTransactionFailureRollsBackTheEntireAppend()
    {
        var document=IntakeTextParser.Parse("2020-10-17\n早餐\n- 明确正常输入约10大卡\n- 明确中途拒绝输入约20大卡",2020,"自由文字");
        var entries=ImportPlanner.Review(document.Rows.Select(Candidate).ToArray(),document.Fingerprint,journal.Meals(),[]).Entries;
        using(var connection=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open(); using var command=connection.CreateCommand();
            command.CommandText="CREATE TRIGGER import_abort BEFORE INSERT ON entries WHEN NEW.food_name='明确中途拒绝输入' BEGIN SELECT RAISE(ABORT,'明确的事务验收失败'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(()=>journal.ImportEntries(entries));
        Assert.IsEmpty(store.ReadSnapshot().Entries);
        Assert.HasCount(60,store.ReadFoods());
    }

    [TestMethod]
    public void ImportBackupAndRestorationPreservePreexistingData()
    {
        var document=IntakeTextParser.Parse("2020-10-17\n早餐\n- 明确备份验收输入约10大卡",2020,"自由文字");
        var entries=ImportPlanner.Review(document.Rows.Select(Candidate).ToArray(),document.Fingerprint,journal.Meals(),[]).Entries;
        var backups=new BackupService(store,Path.Combine(directory,"recovery"));
        var path=backups.CreateImportBackup();
        journal.ImportEntries(entries);
        Assert.HasCount(1,store.ReadSnapshot().Entries);
        Assert.IsEmpty(backups.Inspect(path).Entries);
        var after=backups.Restore(backups.Inspect(path));
        Assert.IsEmpty(store.ReadSnapshot().Entries);
        Assert.AreEqual(entries.Single(),backups.Inspect(after).Entries.Single());
    }
}
