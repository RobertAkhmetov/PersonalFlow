using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace PersonalFlow.Client;
public sealed record PlanItem(Guid Id, string Title, DateOnly Date, bool Done = false, string? Time = null, Guid? WorkoutDayId = null);
public sealed record NoteItem(Guid Id, string Text, DateTimeOffset Created, bool Pinned = false);
public sealed record TrainingDay(Guid Id, string Name, string Exercises);
public sealed record TrainingLog(Guid Id, Guid DayId, string Name, string Snapshot, DateTimeOffset Started,
    string Results = "", bool Finished = false, Guid? ScheduleItemId = null,
    Guid? ScheduleSeriesId = null, DateOnly? ScheduleOriginalDate = null);
public sealed record LessonAnswer(Guid Id, string Text, bool Read = false);
public sealed record LearningProgress(Guid LessonId, int ContentVersion, string Answer = "", string Notes = "", string Evidence = "", string Status = "InProgress", DateTimeOffset? ViewedAt = null);
public sealed record CachedLesson(Guid Id, int Version, DateOnly Date, string Content);
public sealed record LearningSource(string Title, string Url, string? Section, DateOnly VerifiedOn);
public sealed record LearningLessonContent(Guid Id, int Version, DateOnly Date, string Title, int EstimatedMinutes, string Theory, string? Practice, List<LearningSource> Sources, string? SelfCheck = null);
public sealed partial class LocalStore
{
    // Increase this when the on-device SQLite schema changes. A recoverable snapshot is
    // made before applying each new version, and the complete schema change is committed
    // atomically so an interrupted app update cannot leave half-applied tables/columns.
    const int CurrentSchemaVersion = 1;
    readonly string connectionString;
    public LocalStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existingDatabase = File.Exists(path) && new FileInfo(path).Length > 0;
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        var storedSchemaVersion = GetSchemaVersion(db);
        if (storedSchemaVersion > CurrentSchemaVersion)
            throw new InvalidOperationException($"This PersonalFlow build understands local database schema versions up to {CurrentSchemaVersion}, but this device has version {storedSchemaVersion}. Update the app instead of opening this database with an older build.");
        if (existingDatabase && storedSchemaVersion < CurrentSchemaVersion)
            CreatePreMigrationSnapshot(db, path, CurrentSchemaVersion);

        using (var journalMode = db.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL";
            journalMode.ExecuteNonQuery();
        }

        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS items(kind TEXT NOT NULL,id TEXT NOT NULL,body TEXT NOT NULL,PRIMARY KEY(kind,id)); CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY,kind TEXT NOT NULL,entity TEXT NOT NULL,body TEXT,created TEXT NOT NULL);";
        command.ExecuteNonQuery();
        InitializeSync(db, transaction);
        InitializeConflictInbox(db, transaction);
        InitializeLearningSync(db, transaction);
        command.CommandText = $"PRAGMA user_version={CurrentSchemaVersion}";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    static int GetSchemaVersion(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    static void CreatePreMigrationSnapshot(SqliteConnection source, string path, int targetVersion)
    {
        var snapshotPath = $"{path}.pre-v{targetVersion}-{Guid.NewGuid():N}.bak";
        var temporaryPath = $"{snapshotPath}.tmp";
        try
        {
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporaryPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                backup.Open();
                source.BackupDatabase(backup);
                using var integrity = backup.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The local pre-migration snapshot did not pass SQLite integrity_check. The original database was left untouched.");
            }
            File.Move(temporaryPath, snapshotPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
    SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public List<T> All<T>()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT body FROM items WHERE kind=$kind ORDER BY rowid";
        command.Parameters.AddWithValue("$kind", typeof(T).Name);
        using var reader = command.ExecuteReader(); var result = new List<T>();
        while(reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return result;
    }
    public void Save<T>(Guid id, T item) => WriteMany([(typeof(T).Name, id, JsonSerializer.Serialize(item))]);
    public void Delete<T>(Guid id) => WriteMany([(typeof(T).Name, id, (string?)null)]);

    public void CompleteWorkout(TrainingLog log, PlanItem? planItem, OccurrenceOverride? occurrenceOverride)
    {
        var changes = new List<(string Kind, Guid Id, string? Body)>
        {
            (nameof(TrainingLog), log.Id, JsonSerializer.Serialize(log))
        };
        if (planItem is not null) changes.Add((nameof(PlanItem), planItem.Id, JsonSerializer.Serialize(planItem)));
        if (occurrenceOverride is not null) changes.Add((nameof(OccurrenceOverride), occurrenceOverride.Id, JsonSerializer.Serialize(occurrenceOverride)));
        WriteMany(changes);
    }

    public void DeleteWorkoutTemplate(Guid workoutDayId)
    {
        var changes = new List<(string Kind, Guid Id, string? Body)>
        {
            (nameof(TrainingDay), workoutDayId, null)
        };
        changes.AddRange(All<PlanItem>().Where(item => item.WorkoutDayId == workoutDayId)
            .Select(item => (nameof(PlanItem), item.Id, (string?)JsonSerializer.Serialize(item with { WorkoutDayId = null }))));
        changes.AddRange(All<Recurrence>().Where(item => item.WorkoutDayId == workoutDayId)
            .Select(item => (nameof(Recurrence), item.Id, (string?)JsonSerializer.Serialize(item with { WorkoutDayId = null }))));
        WriteMany(changes);
    }

    private void WriteMany(IReadOnlyList<(string Kind, Guid Id, string? Body)> changes)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var change in changes)
        {
            var previous = ReadBody(db, tx, change.Kind, change.Id);
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = change.Body is null
                ? "DELETE FROM items WHERE kind=$kind AND id=$id"
                : "INSERT INTO items(kind,id,body) VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
            command.Parameters.AddWithValue("$kind", change.Kind);
            command.Parameters.AddWithValue("$id", change.Id.ToString());
            if (change.Body is not null) command.Parameters.AddWithValue("$body", change.Body);
            command.ExecuteNonQuery();

            var mutationId = Guid.NewGuid().ToString();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO outbox(id,kind,entity,body,created) VALUES($mutation,$kind,$id,$payload,$created)";
            command.Parameters.AddWithValue("$mutation", mutationId);
            command.Parameters.AddWithValue("$kind", change.Kind);
            command.Parameters.AddWithValue("$id", change.Id.ToString());
            command.Parameters.AddWithValue("$payload", (object?)change.Body ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            CaptureBase(db, tx, mutationId, previous, change.Kind, change.Id);
        }
        tx.Commit();
    }
    public LocalSyncStatus SyncStatus
    {
        get
        {
            var conflictCount = GetSyncConflictInbox().Count(conflict => !conflict.ArchivedEpoch);
            using var db = Open(); using var c = db.CreateCommand();
            c.CommandText = "SELECT (SELECT COUNT(*) FROM outbox)+(SELECT COUNT(*) FROM learning_outbox),(SELECT COUNT(*) FROM sync_base WHERE conflict IS NOT NULL)+(SELECT COUNT(*) FROM learning_sync_base WHERE conflict IS NOT NULL),(SELECT COUNT(*) FROM sync_epoch_conflicts)";
            using var reader = c.ExecuteReader();
            if (!reader.Read()) return new(0, 0, 0);
            var pending = reader.GetInt64(0);
            var epochConflicts = reader.GetInt64(2);
            return new(pending, conflictCount, epochConflicts);
        }
    }
    public long Pending { get { using var db = Open(); using var c = db.CreateCommand(); c.CommandText="SELECT (SELECT COUNT(*) FROM outbox)+(SELECT COUNT(*) FROM learning_outbox)"; return (long)c.ExecuteScalar()!; } }
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow")).DateTime);
}
public sealed record LocalSyncStatus(long Pending, long Conflicts, long EpochConflicts);
