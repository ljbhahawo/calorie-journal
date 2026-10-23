using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CalorieJournal.Core;
using Microsoft.Data.Sqlite;

namespace CalorieJournal.Infrastructure;

public sealed partial class SqliteJournalStore : IJournalStore
{
    public const int SchemaVersion = 7;
    public const int BackupFormatVersion = 7;
    public string DatabasePath { get; }
    private readonly string connectionString;

    public SqliteJournalStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true,
            Pooling = false, DefaultTimeout = 2 }.ToString();
    }

    private SqliteConnection Open(bool allowLegacy = false)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA application_id";
        var applicationId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > 0 && applicationId != 0x43414C4A)
        {
            connection.Dispose();
            throw new ValidationException("该数据库不属于每日热量记录，已停止操作。");
        }
        if (version > SchemaVersion)
        {
            connection.Dispose();
            throw new ValidationException("数据库由更新版本创建，当前程序不能写入。请使用匹配版本或安全恢复旧备份。");
        }
        if (!allowLegacy && version > 0 && version < SchemaVersion)
        {
            connection.Dispose();
            throw new ValidationException("旧数据库须先完成升级前备份和结构迁移。");
        }
        return connection;
    }

    public void Initialize(FoodPackage package)
    {
        Validation.Require(package.Version.Length is > 0 and <= 100 && package.Foods.Length is >= 50 and <= 100000, "内置食物包无效或不足50项。");
        foreach (var food in package.Foods) Validation.Food(food);
        Validation.Require(package.Foods.All(x => x.BuiltinKey.Length > 0) && package.Foods.Select(x => x.BuiltinKey).Distinct().Count() == package.Foods.Length, "内置食物键无效或重复。");
        using var connection = Open(true);
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > 0 && version < SchemaVersion) BackupBeforeMigration(version);
        using var transaction = connection.BeginTransaction();
        if (version == 0)
            Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'"), CultureInfo.InvariantCulture) == 0, "发现无法识别的已有数据库，不会覆盖或重新初始化。");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS foods(
                id TEXT PRIMARY KEY, name TEXT NOT NULL, original_name TEXT NOT NULL, aliases TEXT NOT NULL,
                preparation TEXT NOT NULL, category TEXT NOT NULL, kcal_hundredths INTEGER NOT NULL CHECK(kcal_hundredths BETWEEN 0 AND 1000000),
                source_kind TEXT NOT NULL, source_id TEXT NOT NULL, source_version TEXT NOT NULL, source_url TEXT NOT NULL,
                source_note TEXT NOT NULL, builtin_key TEXT NOT NULL, user_modified INTEGER NOT NULL, is_deleted INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS food_builtin ON foods(builtin_key) WHERE builtin_key <> '';
            CREATE TABLE IF NOT EXISTS meals(id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                sort_order INTEGER NOT NULL, is_active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS entries(
                id TEXT PRIMARY KEY, entry_date TEXT NOT NULL, local_time TEXT NOT NULL,
                meal_id TEXT NOT NULL REFERENCES meals(id), meal_name TEXT NOT NULL,
                food_id TEXT REFERENCES foods(id), food_name TEXT NOT NULL, preparation TEXT NOT NULL,
                input_mode TEXT NOT NULL CHECK(input_mode IN ('direct','weight')), grams_tenths INTEGER,
                kcal_hundredths INTEGER, source_snapshot TEXT NOT NULL, energy_tenths INTEGER NOT NULL CHECK(energy_tenths >= 0),
                note TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS entries_day_meal ON entries(entry_date,meal_id);
            """);
        if (version < 2)
            Execute(connection, transaction, "ALTER TABLE entries ADD COLUMN weight_calculation TEXT;");
        if (version < 7)
            Execute(connection, transaction, """
                ALTER TABLE foods ADD COLUMN protein_hundredths INTEGER CHECK(protein_hundredths BETWEEN 0 AND 10000);
                ALTER TABLE foods ADD COLUMN kj_hundredths INTEGER CHECK(kj_hundredths BETWEEN 0 AND 4184000);
                ALTER TABLE entries ADD COLUMN protein_unit_hundredths INTEGER CHECK(protein_unit_hundredths BETWEEN 0 AND 10000);
                ALTER TABLE entries ADD COLUMN protein_hundredths INTEGER CHECK(protein_hundredths BETWEEN 0 AND 10000000);
                ALTER TABLE entries ADD COLUMN kj_hundredths INTEGER CHECK(kj_hundredths >= 0);
                """);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS portions(
                id TEXT PRIMARY KEY, food_id TEXT NOT NULL REFERENCES foods(id), name TEXT NOT NULL COLLATE NOCASE,
                grams_tenths INTEGER NOT NULL CHECK(grams_tenths BETWEEN 1 AND 1000000), is_deleted INTEGER NOT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS portion_food_name ON portions(food_id,name) WHERE is_deleted=0;
            CREATE TABLE IF NOT EXISTS meal_templates(id TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS template_batches(id TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS body_measurements(id TEXT PRIMARY KEY, entry_date TEXT NOT NULL UNIQUE, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS intake_goals(id TEXT PRIMARY KEY, effective_date TEXT NOT NULL UNIQUE, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS exercise_records(id TEXT PRIMARY KEY, entry_date TEXT NOT NULL, body TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS exercises_day ON exercise_records(entry_date);
            """);
        var initialized = Scalar(connection, transaction, "SELECT value FROM metadata WHERE key='initialized'") as string;
        if (initialized is null)
        {
            var names = new[] { "早餐", "午餐", "晚餐", "加餐" };
            for (var i = 0; i < names.Length; i++)
                PutMeal(connection, transaction, new MealCategory { Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:000000000000}"), Name = names[i], SortOrder = i });
        }
        foreach (var food in package.Foods)
        {
            var exists = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM foods WHERE builtin_key=$key", ("$key", food.BuiltinKey)), CultureInfo.InvariantCulture);
            if (exists == 0) PutFood(connection, transaction, food);
            else if (food.ProteinPer100gHundredths is { } protein)
                Execute(connection, transaction, "UPDATE foods SET protein_hundredths=$protein WHERE builtin_key=$key AND user_modified=0 AND is_deleted=0 AND protein_hundredths IS NULL", ("$protein", protein), ("$key", food.BuiltinKey));
        }
        PutMetadata(connection, transaction, "initialized", "1");
        PutMetadata(connection, transaction, "food_package", package.Version);
        if (version == 0) Execute(connection, transaction, "PRAGMA application_id=0x43414C4A;");
        if (version < 4) PutMetadata(connection, transaction, "settings", JsonSerializer.Serialize(JournalSettings.Default, JsonFiles.Options));
        if (version < SchemaVersion) Execute(connection, transaction, "PRAGMA user_version=7;");
        transaction.Commit();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);

    private static FoodItem[] Foods(SqliteConnection connection, SqliteTransaction? transaction, bool deleted)
    {
        using var command = Command(connection, transaction, "SELECT * FROM foods" + (deleted ? "" : " WHERE is_deleted=0"));
        using var reader = command.ExecuteReader();
        var values = new List<FoodItem>();
        while (reader.Read()) values.Add(new FoodItem
        {
            Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), OriginalName = reader.GetString(2),
            Aliases = reader.GetString(3), Preparation = reader.GetString(4), Category = reader.GetString(5),
            KcalPer100gHundredths = reader.GetInt64(6), SourceKind = reader.GetString(7), SourceId = reader.GetString(8),
            SourceVersion = reader.GetString(9), SourceUrl = reader.GetString(10), SourceNote = reader.GetString(11),
            BuiltinKey = reader.GetString(12), UserModified = reader.GetBoolean(13), IsDeleted = reader.GetBoolean(14),
            CreatedAtUtc = Time(reader.GetString(15)), UpdatedAtUtc = Time(reader.GetString(16)),
            ProteinPer100gHundredths = reader.FieldCount < 19 || reader.IsDBNull(17) ? null : reader.GetInt64(17),
            KilojoulesPer100gHundredths = reader.FieldCount < 19 || reader.IsDBNull(18) ? null : reader.GetInt64(18)
        });
        return values.ToArray();
    }

    private static MealCategory[] Meals(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT * FROM meals ORDER BY sort_order,name");
        using var reader = command.ExecuteReader();
        var values = new List<MealCategory>();
        while (reader.Read()) values.Add(new MealCategory { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), SortOrder = reader.GetInt32(2), IsActive = reader.GetBoolean(3) });
        return values.ToArray();
    }

    private static IntakeEntry[] Entries(SqliteConnection connection, SqliteTransaction? transaction, DateOnly from, DateOnly to, Guid? id = null)
    {
        using var command = Command(connection, transaction, "SELECT * FROM entries WHERE entry_date >= $from AND entry_date <= $to AND ($id IS NULL OR id=$id)", ("$from", from.ToString("yyyy-MM-dd")), ("$to", to.ToString("yyyy-MM-dd")), ("$id", id?.ToString()));
        using var reader = command.ExecuteReader();
        var values = new List<IntakeEntry>();
        while (reader.Read()) values.Add(new IntakeEntry
        {
            Id = Guid.Parse(reader.GetString(0)), EntryDate = DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            LocalTime = reader.GetString(2), MealCategoryId = Guid.Parse(reader.GetString(3)), MealNameSnapshot = reader.GetString(4),
            FoodId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), FoodNameSnapshot = reader.GetString(6),
            PreparationSnapshot = reader.GetString(7), InputMode = reader.GetString(8),
            WeightGramsTenths = reader.IsDBNull(9) ? null : reader.GetInt64(9), KcalPer100gHundredthsSnapshot = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            SourceSnapshot = reader.GetString(11), EnergyKcalTenths = reader.GetInt64(12), Note = reader.GetString(13),
            CreatedAtUtc = Time(reader.GetString(14)), UpdatedAtUtc = Time(reader.GetString(15)),
            WeightCalculation = reader.FieldCount <= 16 || reader.IsDBNull(16) ? null : JsonSerializer.Deserialize<WeightCalculationSnapshot>(reader.GetString(16), JsonFiles.Options),
            ProteinPer100gHundredthsSnapshot = reader.FieldCount < 20 || reader.IsDBNull(17) ? null : reader.GetInt64(17),
            ProteinGramsHundredths = reader.FieldCount < 20 || reader.IsDBNull(18) ? null : reader.GetInt64(18),
            KilojoulesHundredthsSnapshot = reader.FieldCount < 20 || reader.IsDBNull(19) ? null : reader.GetInt64(19)
        });
        return values.ToArray();
    }

    public FoodItem[] ReadFoods(bool includeDeleted = false) { using var connection = Open(); return Foods(connection, null, includeDeleted); }
    public MealCategory[] ReadMeals() { using var connection = Open(); return Meals(connection, null); }
    public IntakeEntry[] ReadEntries(DateOnly from, DateOnly to) { using var connection = Open(); return Entries(connection, null, from, to); }
    public IntakeEntry? FindEntry(Guid id) { using var connection = Open(); return Entries(connection, null, DateOnly.MinValue, DateOnly.MaxValue, id).SingleOrDefault(); }

    private static void PutFood(SqliteConnection connection, SqliteTransaction transaction, FoodItem value)
    {
        Execute(connection, transaction, """
            INSERT INTO foods VALUES($id,$name,$original,$aliases,$preparation,$category,$kcal,$kind,$sourceId,$sourceVersion,$url,$note,$key,$modified,$deleted,$created,$updated,$protein,$kj)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,original_name=excluded.original_name,aliases=excluded.aliases,
            preparation=excluded.preparation,category=excluded.category,kcal_hundredths=excluded.kcal_hundredths,source_kind=excluded.source_kind,
            source_id=excluded.source_id,source_version=excluded.source_version,source_url=excluded.source_url,source_note=excluded.source_note,
            builtin_key=excluded.builtin_key,user_modified=excluded.user_modified,is_deleted=excluded.is_deleted,updated_at=excluded.updated_at,protein_hundredths=excluded.protein_hundredths,kj_hundredths=excluded.kj_hundredths
            """, ("$id", value.Id.ToString()), ("$name", value.Name), ("$original", value.OriginalName), ("$aliases", value.Aliases),
            ("$preparation", value.Preparation), ("$category", value.Category), ("$kcal", value.KcalPer100gHundredths),
            ("$kind", value.SourceKind), ("$sourceId", value.SourceId), ("$sourceVersion", value.SourceVersion), ("$url", value.SourceUrl),
            ("$note", value.SourceNote), ("$key", value.BuiltinKey), ("$modified", value.UserModified), ("$deleted", value.IsDeleted),
            ("$created", Stamp(value.CreatedAtUtc)), ("$updated", Stamp(value.UpdatedAtUtc)), ("$protein", value.ProteinPer100gHundredths), ("$kj", value.KilojoulesPer100gHundredths));
    }

    private static void PutMeal(SqliteConnection connection, SqliteTransaction transaction, MealCategory value) => Execute(connection, transaction,
        "INSERT INTO meals VALUES($id,$name,$sort,$active) ON CONFLICT(id) DO UPDATE SET name=excluded.name,sort_order=excluded.sort_order,is_active=excluded.is_active",
        ("$id", value.Id.ToString()), ("$name", value.Name), ("$sort", value.SortOrder), ("$active", value.IsActive));

    private static void PutEntry(SqliteConnection connection, SqliteTransaction transaction, IntakeEntry value) => Execute(connection, transaction, """
        INSERT INTO entries VALUES($id,$date,$time,$meal,$mealName,$food,$foodName,$preparation,$mode,$grams,$kcal,$source,$energy,$note,$created,$updated,$calculation,$proteinUnit,$protein,$kj)
        ON CONFLICT(id) DO UPDATE SET entry_date=excluded.entry_date,local_time=excluded.local_time,meal_id=excluded.meal_id,meal_name=excluded.meal_name,
        food_id=excluded.food_id,food_name=excluded.food_name,preparation=excluded.preparation,input_mode=excluded.input_mode,grams_tenths=excluded.grams_tenths,
        kcal_hundredths=excluded.kcal_hundredths,source_snapshot=excluded.source_snapshot,energy_tenths=excluded.energy_tenths,note=excluded.note,updated_at=excluded.updated_at,weight_calculation=excluded.weight_calculation,
        protein_unit_hundredths=excluded.protein_unit_hundredths,protein_hundredths=excluded.protein_hundredths,kj_hundredths=excluded.kj_hundredths
        """, ("$id", value.Id.ToString()), ("$date", value.EntryDate.ToString("yyyy-MM-dd")), ("$time", value.LocalTime),
        ("$meal", value.MealCategoryId.ToString()), ("$mealName", value.MealNameSnapshot), ("$food", value.FoodId?.ToString()),
        ("$foodName", value.FoodNameSnapshot), ("$preparation", value.PreparationSnapshot), ("$mode", value.InputMode),
        ("$grams", value.WeightGramsTenths), ("$kcal", value.KcalPer100gHundredthsSnapshot), ("$source", value.SourceSnapshot),
        ("$energy", value.EnergyKcalTenths), ("$note", value.Note), ("$created", Stamp(value.CreatedAtUtc)), ("$updated", Stamp(value.UpdatedAtUtc)),
        ("$calculation", value.WeightCalculation is null ? null : JsonSerializer.Serialize(value.WeightCalculation, JsonFiles.Options)),
        ("$proteinUnit", value.ProteinPer100gHundredthsSnapshot), ("$protein", value.ProteinGramsHundredths), ("$kj", value.KilojoulesHundredthsSnapshot));

    private static void PutMetadata(SqliteConnection connection, SqliteTransaction transaction, string key, string value) => Execute(connection, transaction,
        "INSERT INTO metadata VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value WHERE metadata.value<>excluded.value", ("$key", key), ("$value", value));

    public void SaveFood(FoodItem value) { using var connection = Open(); using var transaction = connection.BeginTransaction(); PutFood(connection, transaction, value); transaction.Commit(); }
    public void SaveMeal(MealCategory value) { using var connection = Open(); using var transaction = connection.BeginTransaction(); PutMeal(connection, transaction, value); transaction.Commit(); }
    public void SaveEntry(IntakeEntry value) { using var connection = Open(); using var transaction = connection.BeginTransaction(); PutEntry(connection, transaction, value); transaction.Commit(); }
    public void AppendEntries(IntakeEntry[] values)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var meals = Meals(connection, transaction).Where(x => x.IsActive).ToDictionary(x => x.Id);
        foreach (var value in values)
        {
            Validation.Entry(value);
            Validation.Require(meals.ContainsKey(value.MealCategoryId), "导入期间餐次发生变化，请重新核对。" );
            Validation.Require(Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM entries WHERE id=$id", ("$id", value.Id.ToString())), CultureInfo.InvariantCulture) == 0,
                "该原文中至少一条记录已经导入；已停止整批追加，不覆盖原记录。" );
            PutEntry(connection, transaction, value);
        }
        transaction.Commit();
    }
    public void DeleteEntry(Guid id)
    {
        using var connection = Open();
        using var command = Command(connection, null, "DELETE FROM entries WHERE id=$id", ("$id", id.ToString()));
        Validation.Require(command.ExecuteNonQuery() == 1, "记录不存在或已经删除。");
    }

    public JournalSnapshot ReadSnapshot()
    {
        using var connection = Open(true);
        using var transaction = connection.BeginTransaction();
        var version = Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        var result = new JournalSnapshot
        {
            FormatVersion = version, SchemaVersion = version,
            AppVersion = typeof(SqliteJournalStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            FoodPackageVersion = (string?)Scalar(connection, transaction, "SELECT value FROM metadata WHERE key='food_package'") ?? throw new ValidationException("缺少食物包版本。"),
            ExportedAtUtc = DateTimeOffset.UtcNow, Foods = Foods(connection, transaction, true), Meals = Meals(connection, transaction),
            Entries = Entries(connection, transaction, DateOnly.MinValue, DateOnly.MaxValue),
            Portions = version < 2 ? null : Portions(connection, transaction),
            Templates = version < 3 ? null : Templates(connection, transaction),
            TemplateBatches = version < 3 ? null : TemplateBatches(connection, transaction),
            Settings = version < 4 ? null : Settings(connection, transaction),
            BodyMeasurements = version < 5 ? null : BodyMeasurements(connection, transaction),
            IntakeGoals = version < 6 ? null : IntakeGoals(connection, transaction),
            Exercises = version < 6 ? null : Exercises(connection, transaction, DateOnly.MinValue, DateOnly.MaxValue)
        };
        transaction.Commit();
        return result;
    }

    public void ReplaceSnapshot(JournalSnapshot snapshot)
    {
        Validation.Snapshot(snapshot);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM exercise_records; DELETE FROM intake_goals; DELETE FROM body_measurements; DELETE FROM template_batches; DELETE FROM meal_templates; DELETE FROM entries; DELETE FROM portions; DELETE FROM foods; DELETE FROM meals;");
        foreach (var food in snapshot.Foods) PutFood(connection, transaction, food);
        foreach (var meal in snapshot.Meals) PutMeal(connection, transaction, meal);
        foreach (var entry in snapshot.Entries) PutEntry(connection, transaction, entry);
        foreach (var portion in snapshot.Portions ?? []) PutPortion(connection, transaction, portion);
        foreach (var template in snapshot.Templates ?? []) PutTemplate(connection, transaction, template);
        foreach (var batch in snapshot.TemplateBatches ?? []) PutTemplateBatch(connection, transaction, batch);
        foreach (var item in snapshot.BodyMeasurements ?? []) PutBodyMeasurement(connection, transaction, item);
        foreach (var goal in snapshot.IntakeGoals ?? []) PutIntakeGoal(connection, transaction, goal);
        foreach (var exercise in snapshot.Exercises ?? []) PutExercise(connection, transaction, exercise);
        PutMetadata(connection, transaction, "food_package", snapshot.FoodPackageVersion);
        PutMetadata(connection, transaction, "settings", JsonSerializer.Serialize(snapshot.Settings ?? JournalSettings.Default, JsonFiles.Options));
        transaction.Commit();
    }

    public void BackupDatabase(string destination)
    {
        Validation.Require(!File.Exists(destination), "数据库备份路径已存在。");
        using var source = Open(true);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(destination), Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
        using var check = target.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Validation.Require((string?)check.ExecuteScalar() == "ok", "数据库备份完整性检查失败。");
    }
}
