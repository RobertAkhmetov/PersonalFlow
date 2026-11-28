using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalFlow.Client;

public sealed partial class LocalStore
{
    static readonly JsonSerializerOptions LearningWire = new(JsonSerializerDefaults.Web);
    static readonly HashSet<string> LearningFields = ["Answer", "Notes", "Evidence", "Status", "ViewedAt"];

    static void InitializeLearningSync(SqliteConnection db, SqliteTransaction transaction)
    {
        using var c = db.CreateCommand();
        c.Transaction = transaction;
        c.CommandText = """
            CREATE TABLE IF NOT EXISTS learning_outbox(
                id TEXT PRIMARY KEY, lesson_id TEXT NOT NULL, content_version INTEGER NOT NULL,
                body TEXT, created TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS learning_sync_base(
                mutation TEXT PRIMARY KEY, lesson_id TEXT NOT NULL, content_version INTEGER NOT NULL,
                body TEXT NOT NULL, wire TEXT, conflict TEXT, revision INTEGER NOT NULL, recovery INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS learning_shadow(
                lesson_id TEXT NOT NULL, content_version INTEGER NOT NULL, revision INTEGER NOT NULL,
                body TEXT NOT NULL, deleted INTEGER NOT NULL,
                PRIMARY KEY(lesson_id, content_version)
            );
            CREATE TABLE IF NOT EXISTS learning_lessons(
                lesson_id TEXT NOT NULL, content_version INTEGER NOT NULL, lesson_date TEXT NOT NULL,
                content TEXT NOT NULL, PRIMARY KEY(lesson_id, content_version)
            );
            """;
        c.ExecuteNonQuery();
        c.CommandText = "PRAGMA table_info(learning_sync_base)";
        using (var reader = c.ExecuteReader())
        {
            var hasRecovery = false;
            while (reader.Read()) if (reader.GetString(1) == "recovery") hasRecovery = true;
            reader.Close();
            if (!hasRecovery) { c.CommandText = "ALTER TABLE learning_sync_base ADD COLUMN recovery INTEGER NOT NULL DEFAULT 0"; c.ExecuteNonQuery(); }
        }
    }

    public static Guid LearningProgressStorageId(Guid lessonId, int contentVersion)
    {
        if (lessonId == Guid.Empty || contentVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(contentVersion), "A lesson ID and positive content version are required.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"PersonalFlow.LearningProgress:{lessonId:D}:{contentVersion}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public LearningProgress GetLearningProgress(Guid lessonId, int contentVersion)
    {
        var storageId = LearningProgressStorageId(lessonId, contentVersion);
        using (var db = Open())
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT body FROM items WHERE kind='LearningProgress' AND id=$id";
            c.Parameters.AddWithValue("$id", storageId.ToString());
            if (c.ExecuteScalar() is string saved)
            {
                var loaded = JsonSerializer.Deserialize<LearningProgress>(saved)!;
                RemoveLegacyLessonAnswer(lessonId);
                return loaded;
            }
        }

        var legacy = contentVersion == 1 ? All<LessonAnswer>().FirstOrDefault(x => x.Id == lessonId) : null;
        var progress = new LearningProgress(
            lessonId, contentVersion,
            Answer: legacy?.Text ?? "",
            Status: legacy?.Read == true ? "Read" : "InProgress");
        if (legacy is not null) SaveLearningProgress(progress);
        RemoveLegacyLessonAnswer(lessonId);
        return progress;
    }

    void RemoveLegacyLessonAnswer(Guid lessonId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT id FROM outbox WHERE kind='LessonAnswer' AND entity=$lesson";
        c.Parameters.AddWithValue("$lesson", lessonId.ToString());
        using var reader = c.ExecuteReader();
        var mutations = new List<string>();
        while (reader.Read()) mutations.Add(reader.GetString(0));
        reader.Close();
        foreach (var mutation in mutations)
        {
            c.Parameters.Clear();
            c.CommandText = "DELETE FROM sync_base WHERE mutation=$id";
            c.Parameters.AddWithValue("$id", mutation);
            c.ExecuteNonQuery();
            c.Parameters.Clear();
            c.CommandText = "DELETE FROM outbox WHERE id=$id";
            c.Parameters.AddWithValue("$id", mutation);
            c.ExecuteNonQuery();
        }
        c.Parameters.Clear();
        c.CommandText = "DELETE FROM items WHERE kind='LessonAnswer' AND id=$lesson";
        c.Parameters.AddWithValue("$lesson", lessonId.ToString());
        c.ExecuteNonQuery();
        tx.Commit();
    }

    public void SaveLearningProgress(LearningProgress progress)
    {
        var storageId = LearningProgressStorageId(progress.LessonId, progress.ContentVersion);
        using var db = Open();
        using var tx = db.BeginTransaction();
        var previous = ReadBody(db, tx, "LearningProgress", storageId);
        var body = JsonSerializer.Serialize(progress);
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "INSERT INTO items(kind,id,body) VALUES('LearningProgress',$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        c.Parameters.AddWithValue("$id", storageId.ToString());
        c.Parameters.AddWithValue("$body", body);
        c.ExecuteNonQuery();

        var mutation = Guid.NewGuid().ToString();
        c.Parameters.Clear();
        c.CommandText = "INSERT INTO learning_outbox(id,lesson_id,content_version,body,created) VALUES($id,$lesson,$version,$body,$created)";
        c.Parameters.AddWithValue("$id", mutation);
        c.Parameters.AddWithValue("$lesson", progress.LessonId.ToString());
        c.Parameters.AddWithValue("$version", progress.ContentVersion);
        c.Parameters.AddWithValue("$body", body);
        c.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        c.ExecuteNonQuery();

        c.Parameters.Clear();
        c.CommandText = """
            INSERT INTO learning_sync_base(mutation,lesson_id,content_version,body,revision,recovery)
            VALUES($id,$lesson,$version,$body,
                COALESCE((SELECT revision FROM learning_shadow WHERE lesson_id=$lesson AND content_version=$version),0),
                COALESCE((SELECT recovery_started FROM sync_epoch_conflicts WHERE service='learning'),0))
            """;
        c.Parameters.AddWithValue("$id", mutation);
        c.Parameters.AddWithValue("$lesson", progress.LessonId.ToString());
        c.Parameters.AddWithValue("$version", progress.ContentVersion);
        c.Parameters.AddWithValue("$body", previous);
        c.ExecuteNonQuery();
        tx.Commit();
    }

    public List<string> LearningSyncConflicts()
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT conflict FROM learning_sync_base WHERE conflict IS NOT NULL";
        using var reader = c.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    // The caller supplies an authenticated client. Credentials never enter SQLite.
    public async Task SynchronizeLearningAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        if (client.BaseAddress is null || client.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("HTTPS API address required.");

        await syncGate.WaitAsync(cancellationToken);
        try
        {
            var state = await client.GetFromJsonAsync<LearningSyncState>("v1/sync/state", LearningWire, cancellationToken)
                ?? throw new InvalidDataException("Empty Learning sync state.");
            var local = LearningCursorState();
            var epochConflict = GetSyncEpochConflicts().FirstOrDefault(conflict => conflict.Service == "learning");
            if (epochConflict is not null &&
                (!epochConflict.RecoveryStarted || !Guid.TryParse(epochConflict.ServerEpoch, out var recoveryEpoch) || recoveryEpoch != state.Epoch))
            {
                if (!Guid.TryParse(epochConflict.ServerEpoch, out var recordedEpoch) || recordedEpoch != state.Epoch)
                    RecordLearningEpochConflict(epochConflict.LocalEpoch, state.Epoch);
                throw new SyncEpochChangedException("The Learning server history changed. Review the epoch recovery action in settings.");
            }
            if (local.Epoch is null)
            {
                if (local.Cursor != 0 || LearningShadowCount() != 0)
                {
                    RecordLearningEpochConflict("untracked", state.Epoch);
                    throw new SyncEpochChangedException("This local Learning cache predates sync epochs; its data is preserved for recovery.");
                }
                SaveLearningEpoch(state.Epoch);
            }
            else if (!Guid.TryParse(local.Epoch, out var localEpoch) || localEpoch != state.Epoch)
            {
                RecordLearningEpochConflict(local.Epoch, state.Epoch);
                throw new SyncEpochChangedException("The Learning server sync epoch changed. Local progress and queued edits were preserved.");
            }

            if (epochConflict is { RecoveryStarted: true, SnapshotComplete: false })
            {
                await DownloadLearningChangesAsync(client, state.Epoch, cancellationToken);
                ReconcileLearningEpochSnapshot(state.Epoch);
            }

            var blocked = new HashSet<string>(StringComparer.Ordinal);
            while (PrepareLearning(blocked, state.Epoch) is { } pending)
            {
                using var response = await client.PostAsync(
                    "v1/progress/sync",
                    new StringContent(pending.Wire, Encoding.UTF8, "application/json"),
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonObject>(LearningWire, cancellationToken);
                    if (error?["code"]?.GetValue<string>() == "sync_epoch_changed")
                    {
                        var newEpoch = error["epoch"]?.GetValue<Guid>() ?? Guid.Empty;
                        if (newEpoch != Guid.Empty) RecordLearningEpochConflict(state.Epoch.ToString("D"), newEpoch);
                        throw new SyncEpochChangedException("The Learning server sync epoch changed during upload. Local progress was preserved.");
                    }
                }
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<JsonObject>(LearningWire, cancellationToken)
                    ?? throw new InvalidDataException("Empty Learning sync response.");
                if (result["status"]?.GetValue<string>() == "conflict")
                {
                    using var db = Open();
                    using var c = db.CreateCommand();
                    c.CommandText = "UPDATE learning_sync_base SET conflict=$conflict WHERE mutation=$id";
                    c.Parameters.AddWithValue("$id", pending.Id);
                    c.Parameters.AddWithValue("$conflict", result.ToJsonString());
                    c.ExecuteNonQuery();
                    blocked.Add(LearningKey(pending.LessonId, pending.ContentVersion));
                    continue;
                }
                if (result["status"]?.GetValue<string>() != "applied")
                    throw new InvalidDataException("Unknown Learning sync outcome.");
                AcknowledgeLearning(pending, result);
            }

            await DownloadLearningChangesAsync(client, state.Epoch, cancellationToken);
            CompleteEpochRecovery("learning", state.Epoch);
        }
        finally
        {
            syncGate.Release();
        }
    }

    async Task DownloadLearningChangesAsync(HttpClient client, Guid epoch, CancellationToken cancellationToken)
    {
        while (true)
        {
            var cursor = LearningCursorState().Cursor;
            using var response = await client.GetAsync($"v1/progress/changes?after={cursor}&epoch={epoch:D}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var error = await response.Content.ReadFromJsonAsync<JsonObject>(LearningWire, cancellationToken);
                if (error?["code"]?.GetValue<string>() == "sync_epoch_changed")
                {
                    var newEpoch = error["epoch"]?.GetValue<Guid>() ?? Guid.Empty;
                    if (newEpoch != Guid.Empty) RecordLearningEpochConflict(epoch.ToString("D"), newEpoch);
                    throw new SyncEpochChangedException("The Learning server sync epoch changed while downloading. Local progress was preserved.");
                }
            }
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<LearningChangePage>(LearningWire, cancellationToken)
                ?? throw new InvalidDataException("Empty Learning change page.");
            if (page.Epoch != epoch)
            {
                RecordLearningEpochConflict(epoch.ToString("D"), page.Epoch);
                throw new SyncEpochChangedException("The Learning server sync epoch changed while downloading. Local progress was preserved.");
            }
            if (page.Items is null || page.Items.Count > 200 || page.Cursor < cursor ||
                page.Items.Any(change => change.Revision <= cursor || change.LessonId == Guid.Empty || change.ContentVersion < 1))
                throw new InvalidDataException("Invalid Learning change page.");
            var expectedCursor = cursor;
            foreach (var change in page.Items)
            {
                if (change.Revision <= expectedCursor) throw new InvalidDataException("Unordered Learning change feed.");
                expectedCursor = change.Revision;
            }
            if (page.Cursor != expectedCursor) throw new InvalidDataException("Learning cursor does not match its change page.");

            using (var db = Open())
            using (var transaction = db.BeginTransaction())
            {
                foreach (var change in page.Items) AcceptLearning(db, transaction, change);
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO sync_cursor(service,value,epoch) VALUES('learning',$value,$epoch) ON CONFLICT(service) DO UPDATE SET value=excluded.value,epoch=excluded.epoch";
                command.Parameters.AddWithValue("$value", page.Cursor);
                command.Parameters.AddWithValue("$epoch", page.Epoch.ToString("D"));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            if (page.Items.Count < 200) break;
            if (page.Cursor == cursor) throw new InvalidDataException("Non-advancing Learning cursor.");
        }
    }

    void ReconcileLearningEpochSnapshot(Guid epoch)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT recovery_started,snapshot_complete,server_epoch FROM sync_epoch_conflicts WHERE service='learning'";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt32(0) == 0 || reader.GetInt32(1) != 0 || reader.GetString(2) != epoch.ToString("D"))
                throw new InvalidOperationException("The Learning recovery snapshot is not ready to reconcile.");
            reader.Close();
        }

        var previous = ReadLearningEpochBackup(db, transaction);
        var current = ReadLearningShadow(db, transaction);
        var local = ReadLearningItems(db, transaction);
        var queued = ReadLearningRecoveryQueue(db, transaction);
        var keys = new HashSet<(string LessonId, int Version)>(previous.Keys);
        keys.UnionWith(current.Keys);
        keys.UnionWith(local.Keys);
        keys.UnionWith(queued.Keys);

        foreach (var key in keys)
        {
            previous.TryGetValue(key, out var oldServer);
            current.TryGetValue(key, out var server);
            local.TryGetValue(key, out var localBody);
            queued.TryGetValue(key, out var pending);
            if (pending is { HasLegacyOperation: true }) continue;

            var desiredBody = pending is null ? localBody : localBody ?? pending.LastBody;
            if (pending is not null && localBody is null && pending.LastBody is null)
                desiredBody = null;
            var desiredFields = desiredBody is null ? new JsonObject() : ProjectLearningFields(ParseBody(desiredBody));

            if (pending is not null)
            {
                if (desiredBody is null)
                {
                    if (server is { Deleted: false })
                        InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, pending.BaseBody ?? "{}", new JsonObject(), null, server.Revision, epoch, delete: true);
                    else
                        ClearLearningQueue(db, transaction, key.LessonId, key.Version);
                    continue;
                }
                if (server is { Deleted: false } && JsonNode.DeepEquals(desiredFields, ProjectLearningFields(ParseBody(server.Body))))
                {
                    ClearLearningQueue(db, transaction, key.LessonId, key.Version);
                    SetLearningItem(db, transaction, key.LessonId, key.Version, desiredBody);
                    continue;
                }
                if (server is null)
                {
                    InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, "{}", desiredFields, desiredBody, 0, epoch, delete: false);
                    continue;
                }
                var baseBody = pending.BaseBody ?? "{}";
                var baseFields = ProjectLearningFields(ParseBody(baseBody));
                var patch = server.Deleted
                    ? Diff(ProjectLearningFields(ParseBody(server.Body)), desiredFields)
                    : Diff(baseFields, desiredFields);
                InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, baseFields.ToJsonString(), patch, desiredBody, server.Revision, epoch, delete: false);
                continue;
            }

            if (server is { Deleted: false })
            {
                if (desiredBody is not null)
                {
                    if (JsonNode.DeepEquals(desiredFields, ProjectLearningFields(ParseBody(server.Body)))) continue;
                    var baseline = oldServer is { Deleted: false } ? ProjectLearningFields(ParseBody(oldServer.Body)) : new JsonObject();
                    InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, baseline.ToJsonString(), desiredFields, desiredBody, server.Revision, epoch, delete: false);
                }
                else if (oldServer is { Deleted: true })
                {
                    InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, oldServer.Body, new JsonObject(), null, server.Revision, epoch, delete: true);
                }
                else
                {
                    SetLearningItem(db, transaction, key.LessonId, key.Version, server.Body);
                }
                continue;
            }

            if (server is { Deleted: true })
            {
                if (desiredBody is not null)
                {
                    var baseline = oldServer is null ? new JsonObject() : ProjectLearningFields(ParseBody(oldServer.Body));
                    var patch = Diff(ProjectLearningFields(ParseBody(server.Body)), desiredFields);
                    InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, baseline.ToJsonString(), patch, desiredBody, server.Revision, epoch, delete: false);
                }
                continue;
            }

            if (desiredBody is not null)
                InsertLearningRecoveryMutation(db, transaction, key.LessonId, key.Version, "{}", desiredFields, desiredBody, 0, epoch, delete: false);
        }

        command.Parameters.Clear();
        command.CommandText = "UPDATE sync_epoch_conflicts SET snapshot_complete=1 WHERE service='learning' AND server_epoch=$epoch AND recovery_started=1";
        command.Parameters.AddWithValue("$epoch", epoch.ToString("D"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    static Dictionary<(string LessonId, int Version), EpochShadow> ReadLearningEpochBackup(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT entity,revision,body,deleted FROM sync_epoch_shadow_backup WHERE service='learning'";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, int), EpochShadow>();
        while (reader.Read())
        {
            if (!TryParseLearningKey(reader.GetString(0), out var lessonId, out var version)) continue;
            result[(lessonId, version)] = new(reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3) != 0);
        }
        return result;
    }

    static Dictionary<(string LessonId, int Version), EpochShadow> ReadLearningShadow(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT lesson_id,content_version,revision,body,deleted FROM learning_shadow";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, int), EpochShadow>();
        while (reader.Read()) result[(reader.GetString(0), reader.GetInt32(1))] = new(reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4) != 0);
        return result;
    }

    static Dictionary<(string LessonId, int Version), string> ReadLearningItems(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM items WHERE kind='LearningProgress'";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, int), string>();
        while (reader.Read())
        {
            var body = reader.GetString(0);
            var progress = ParseBody(body);
            if (Guid.TryParse(progress["LessonId"]?.ToString(), out var lessonId) &&
                int.TryParse(progress["ContentVersion"]?.ToString(), out var version) && version > 0)
                result[(lessonId.ToString("D"), version)] = body;
        }
        return result;
    }

    static Dictionary<(string LessonId, int Version), LearningRecoveryQueue> ReadLearningRecoveryQueue(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT o.id,o.lesson_id,o.content_version,o.body,b.body FROM learning_outbox o LEFT JOIN learning_sync_base b ON b.mutation=o.id ORDER BY o.rowid";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, int), LearningRecoveryQueue>();
        while (reader.Read())
        {
            var key = (reader.GetString(1), reader.GetInt32(2));
            var body = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (result.TryGetValue(key, out var prior))
                result[key] = prior with { LastBody = body, HasLegacyOperation = prior.HasLegacyOperation || reader.IsDBNull(4) };
            else
                result[key] = new(key.Item1, key.Item2, body, reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(4));
        }
        return result;
    }

    static void InsertLearningRecoveryMutation(SqliteConnection db, SqliteTransaction transaction, string lessonId, int version, string baseBody, JsonObject patch, string? desiredBody, long revision, Guid epoch, bool delete)
    {
        ClearLearningQueue(db, transaction, lessonId, version);
        var mutationId = Guid.NewGuid();
        var baseFields = ProjectLearningFields(ParseBody(baseBody));
        var wire = JsonSerializer.Serialize(new
        {
            Id = mutationId, LessonId = Guid.Parse(lessonId), ContentVersion = version, BaseRevision = revision,
            Base = baseFields, Patch = delete ? new JsonObject() : patch, Delete = delete, Epoch = epoch, RecoveryCompare = true
        }, LearningWire);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO learning_outbox(id,lesson_id,content_version,body,created) VALUES($id,$lesson,$version,$body,$created); INSERT INTO learning_sync_base(mutation,lesson_id,content_version,body,wire,revision,recovery) VALUES($id,$lesson,$version,$base,$wire,$revision,1)";
        command.Parameters.AddWithValue("$id", mutationId.ToString("D"));
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$body", (object?)desiredBody ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$base", baseFields.ToJsonString());
        command.Parameters.AddWithValue("$wire", wire);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    static void ClearLearningQueue(SqliteConnection db, SqliteTransaction transaction, string lessonId, int version)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM learning_sync_base WHERE mutation IN (SELECT id FROM learning_outbox WHERE lesson_id=$lesson AND content_version=$version); DELETE FROM learning_outbox WHERE lesson_id=$lesson AND content_version=$version";
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    static void SetLearningItem(SqliteConnection db, SqliteTransaction transaction, string lessonId, int version, string body)
    {
        var progress = ProjectLearningFields(ParseBody(body));
        progress["LessonId"] = lessonId;
        progress["ContentVersion"] = version;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO items(kind,id,body) VALUES('LearningProgress',$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        command.Parameters.AddWithValue("$id", LearningProgressStorageId(Guid.Parse(lessonId), version).ToString());
        command.Parameters.AddWithValue("$body", progress.ToJsonString());
        command.ExecuteNonQuery();
    }

    static bool TryParseLearningKey(string key, out string lessonId, out int version)
    {
        var parts = key.Split(':', 2);
        lessonId = Guid.Empty.ToString("D");
        version = 0;
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var parsedId) || !int.TryParse(parts[1], out version) || version < 1)
            return false;
        lessonId = parsedId.ToString("D");
        return true;
    }

    sealed record LearningRecoveryQueue(string LessonId, int Version, string? LastBody, string? BaseBody, bool HasLegacyOperation);

    sealed record PendingLearningMutation(string Id, string LessonId, int ContentVersion, string StorageId, string Wire);
    sealed record LearningSyncState(Guid Epoch);
    sealed record LearningRemoteChange(long Revision, Guid LessonId, int ContentVersion, string Body, bool Deleted);
    sealed record LearningChangePage(List<LearningRemoteChange> Items, long Cursor, Guid Epoch);

    PendingLearningMutation? PrepareLearning(HashSet<string> blocked, Guid epoch)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = """
            SELECT o.id,o.lesson_id,o.content_version,o.body,b.body,b.wire,b.conflict,b.revision,b.recovery
            FROM learning_outbox o
            LEFT JOIN learning_sync_base b ON b.mutation=o.id
            ORDER BY o.rowid
            """;
        using var reader = c.ExecuteReader();
        PendingLearningMutation? result = null;
        while (reader.Read())
        {
            var lessonId = reader.GetString(1);
            var version = reader.GetInt32(2);
            var key = LearningKey(lessonId, version);
            if (blocked.Contains(key)) continue;
            if (reader.IsDBNull(4) || !reader.IsDBNull(6))
            {
                blocked.Add(key);
                continue;
            }

            var id = reader.GetString(0);
            var storageId = LearningProgressStorageId(Guid.Parse(lessonId), version).ToString();
            if (!reader.IsDBNull(5))
            {
                var frozen = JsonNode.Parse(reader.GetString(5)) as JsonObject;
                if (frozen?["epoch"] is null || frozen["epoch"]!.GetValue<Guid>() != epoch)
                {
                    blocked.Add(key);
                    continue;
                }
                result = new(id, lessonId, version, storageId, reader.GetString(5));
                break;
            }

            var basis = JsonNode.Parse(reader.GetString(4)) as JsonObject ?? new JsonObject();
            var deleted = reader.IsDBNull(3);
            var desired = deleted ? new JsonObject() : JsonNode.Parse(reader.GetString(3)) as JsonObject ?? new JsonObject();
            var baseFields = ProjectLearningFields(basis);
            var recoveryCompare = reader.GetInt32(8) != 0;
            var patch = new JsonObject();
            foreach (var field in LearningFields)
            {
                basis.TryGetPropertyValue(field, out var oldValue);
                desired.TryGetPropertyValue(field, out var newValue);
                if (!JsonNode.DeepEquals(oldValue, newValue))
                    patch[field] = newValue?.DeepClone();
            }

            var wire = JsonSerializer.Serialize(new
            {
                Id = Guid.Parse(id),
                LessonId = Guid.Parse(lessonId),
                ContentVersion = version,
                BaseRevision = reader.GetInt64(7),
                Base = baseFields,
                Patch = patch,
                Delete = deleted,
                Epoch = epoch,
                RecoveryCompare = recoveryCompare
            }, LearningWire);
            result = new(id, lessonId, version, storageId, wire);
            break;
        }
        reader.Close();

        if (result is not null)
        {
            c.CommandText = "UPDATE learning_sync_base SET wire=$wire WHERE mutation=$id";
            c.Parameters.AddWithValue("$wire", result.Wire);
            c.Parameters.AddWithValue("$id", result.Id);
            c.ExecuteNonQuery();
        }
        tx.Commit();
        return result;
    }

    static JsonObject ProjectLearningFields(JsonObject source)
    {
        var projected = new JsonObject();
        foreach (var field in LearningFields)
            if (source.TryGetPropertyValue(field, out var value))
                projected[field] = value?.DeepClone();
        return projected;
    }

    void AcknowledgeLearning(PendingLearningMutation pending, JsonObject result)
    {
        var revision = result["revision"]!.GetValue<long>();
        var body = result["body"]?.ToJsonString() ?? "{}";
        var deleted = result["deleted"]!.GetValue<bool>();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "DELETE FROM learning_outbox WHERE id=$id; DELETE FROM learning_sync_base WHERE mutation=$id";
        c.Parameters.AddWithValue("$id", pending.Id);
        c.ExecuteNonQuery();

        c.CommandText = "UPDATE learning_sync_base SET revision=$revision WHERE wire IS NULL AND lesson_id=$lesson AND content_version=$version";
        c.Parameters.AddWithValue("$revision", revision);
        c.Parameters.AddWithValue("$lesson", pending.LessonId);
        c.Parameters.AddWithValue("$version", pending.ContentVersion);
        c.ExecuteNonQuery();

        AcceptLearning(db, tx, new(revision, Guid.Parse(pending.LessonId), pending.ContentVersion, body, deleted));
        tx.Commit();
    }

    static void AcceptLearning(SqliteConnection db, SqliteTransaction tx, LearningRemoteChange change)
    {
        var lesson = change.LessonId.ToString();
        var version = change.ContentVersion;
        var storageId = LearningProgressStorageId(change.LessonId, version).ToString();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.Parameters.AddWithValue("$lesson", lesson);
        c.Parameters.AddWithValue("$version", version);
        c.Parameters.AddWithValue("$revision", change.Revision);
        c.Parameters.AddWithValue("$body", change.Body);
        c.Parameters.AddWithValue("$deleted", change.Deleted);

        c.CommandText = "SELECT revision FROM learning_shadow WHERE lesson_id=$lesson AND content_version=$version";
        if (c.ExecuteScalar() is long existing && existing >= change.Revision) return;

        c.CommandText = """
            INSERT INTO learning_shadow(lesson_id,content_version,revision,body,deleted)
            VALUES($lesson,$version,$revision,$body,$deleted)
            ON CONFLICT(lesson_id,content_version) DO UPDATE SET revision=excluded.revision,body=excluded.body,deleted=excluded.deleted
            """;
        c.ExecuteNonQuery();

        c.Parameters.Clear();
        c.CommandText = "SELECT COUNT(*) FROM sync_epoch_conflicts WHERE service='learning' AND recovery_started=1 AND snapshot_complete=0";
        if ((long)c.ExecuteScalar()! != 0) return;

        c.Parameters.Clear();
        c.Parameters.AddWithValue("$lesson", lesson);
        c.Parameters.AddWithValue("$version", version);
        c.CommandText = "SELECT COUNT(*) FROM learning_outbox WHERE lesson_id=$lesson AND content_version=$version";
        if ((long)c.ExecuteScalar()! != 0) return;

        c.Parameters.Clear();
        c.Parameters.AddWithValue("$kind", "LearningProgress");
        c.Parameters.AddWithValue("$id", storageId);
        c.CommandText = change.Deleted
            ? "DELETE FROM items WHERE kind=$kind AND id=$id"
            : "INSERT INTO items(kind,id,body) VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        if (!change.Deleted)
        {
            var localBody = JsonNode.Parse(change.Body) as JsonObject ?? new JsonObject();
            localBody["LessonId"] = change.LessonId;
            localBody["ContentVersion"] = change.ContentVersion;
            c.Parameters.AddWithValue("$body", localBody.ToJsonString());
        }
        c.ExecuteNonQuery();
    }

    (long Cursor, string? Epoch) LearningCursorState()
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT value,epoch FROM sync_cursor WHERE service='learning'";
        using var reader = c.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : (0, null);
    }

    long LearningShadowCount()
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM learning_shadow";
        return (long)c.ExecuteScalar()!;
    }

    void SaveLearningEpoch(Guid epoch)
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO sync_cursor(service,value,epoch) VALUES('learning',0,$epoch) ON CONFLICT(service) DO UPDATE SET epoch=excluded.epoch";
        c.Parameters.AddWithValue("$epoch", epoch.ToString("D"));
        c.ExecuteNonQuery();
    }

    void RecordLearningEpochConflict(string local, Guid server) =>
        RecordLearningEpochConflict(local, server.ToString("D"));

    void RecordLearningEpochConflict(string local, string server)
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            INSERT INTO sync_epoch_conflicts(service,local_epoch,server_epoch,detected,recovery_started,snapshot_complete)
            VALUES('learning',$local,$server,$detected,0,0)
            ON CONFLICT(service) DO UPDATE SET local_epoch=excluded.local_epoch,server_epoch=excluded.server_epoch,detected=excluded.detected,recovery_started=0,snapshot_complete=0
            """;
        c.Parameters.AddWithValue("$local", local);
        c.Parameters.AddWithValue("$server", server);
        c.Parameters.AddWithValue("$detected", DateTimeOffset.UtcNow.ToString("O"));
        c.ExecuteNonQuery();
    }

    void BeginLearningEpochRecovery(Guid serverEpoch)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT server_epoch,recovery_started FROM sync_epoch_conflicts WHERE service='learning'";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("No Learning sync-epoch conflict is waiting for recovery.");
            var recordedServerEpoch = reader.GetString(0);
            var recoveryStarted = reader.GetInt32(1) != 0;
            reader.Close();
            if (!Guid.TryParse(recordedServerEpoch, out var expected) || expected != serverEpoch)
            {
                command.CommandText = "UPDATE sync_epoch_conflicts SET server_epoch=$server,recovery_started=0,snapshot_complete=0,detected=$detected WHERE service='learning'";
                command.Parameters.AddWithValue("$server", serverEpoch.ToString("D"));
                command.Parameters.AddWithValue("$detected", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
                transaction.Commit();
                throw new SyncEpochChangedException("The Learning server changed again. Review the updated epoch before recovery.");
            }
            if (recoveryStarted)
            {
                transaction.Commit();
                return;
            }
        }

        using (var conflicts = db.CreateCommand())
        {
            conflicts.Transaction = transaction;
            conflicts.CommandText = "SELECT conflict FROM learning_sync_base WHERE conflict IS NOT NULL";
            using var reader = conflicts.ExecuteReader();
            var snapshots = new List<string>();
            while (reader.Read()) snapshots.Add(reader.GetString(0));
            reader.Close();
            foreach (var snapshot in snapshots) ArchiveRemoteConflict(db, transaction, "learning", snapshot);
        }
        using (var archive = db.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = "UPDATE remote_conflicts SET archived=1,resolution_pending=0 WHERE service='learning'";
            archive.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM sync_epoch_shadow_backup WHERE service='learning'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = "INSERT INTO sync_epoch_shadow_backup(service,kind,entity,revision,body,deleted) SELECT 'learning','LearningProgress',lesson_id || ':' || content_version,revision,body,deleted FROM learning_shadow";
            command.ExecuteNonQuery();
        }
        command.CommandText = "INSERT OR IGNORE INTO sync_epoch_shadow_backup(service,kind,entity,revision,body,deleted) VALUES('learning','_snapshot','_complete',0,'{}',0)";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM learning_shadow";
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO sync_cursor(service,value,epoch) VALUES('learning',0,$epoch) ON CONFLICT(service) DO UPDATE SET value=0,epoch=excluded.epoch";
        command.Parameters.AddWithValue("$epoch", serverEpoch.ToString("D"));
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "UPDATE sync_epoch_conflicts SET recovery_started=1,snapshot_complete=0 WHERE service='learning'";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    static string LearningKey(string lessonId, int version) => $"{lessonId}:{version}";
}
