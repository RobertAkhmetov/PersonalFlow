using Microsoft.Data.Sqlite;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace PersonalFlow.Client;

public sealed record LocalSyncEpochConflict(string Service, string LocalEpoch, string ServerEpoch, string Detected, bool RecoveryStarted, bool SnapshotComplete);

public sealed partial class LocalStore
{
    readonly SemaphoreSlim syncGate = new(1, 1);
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    static readonly HashSet<string> LifeKinds = ["PlanItem", "NoteItem", "TrainingDay", "TrainingLog", "Recurrence", "Vacation", "OccurrenceOverride"];
    static void InitializeSync(SqliteConnection db, SqliteTransaction transaction)
    {
        using var c = db.CreateCommand();
        c.Transaction = transaction;
        c.CommandText = "CREATE TABLE IF NOT EXISTS sync_base(mutation TEXT PRIMARY KEY,body TEXT NOT NULL,wire TEXT,conflict TEXT,recovery INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS shadow(kind TEXT NOT NULL,id TEXT NOT NULL,revision INTEGER NOT NULL,body TEXT NOT NULL,deleted INTEGER NOT NULL,PRIMARY KEY(kind,id)); CREATE TABLE IF NOT EXISTS sync_cursor(service TEXT PRIMARY KEY,value INTEGER NOT NULL,epoch TEXT); CREATE TABLE IF NOT EXISTS sync_epoch_conflicts(service TEXT PRIMARY KEY,local_epoch TEXT NOT NULL,server_epoch TEXT NOT NULL,detected TEXT NOT NULL,recovery_started INTEGER NOT NULL DEFAULT 0,snapshot_complete INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS sync_epoch_shadow_backup(service TEXT NOT NULL,kind TEXT NOT NULL,entity TEXT NOT NULL,revision INTEGER NOT NULL,body TEXT NOT NULL,deleted INTEGER NOT NULL,PRIMARY KEY(service,kind,entity));";
        c.ExecuteNonQuery();
        c.CommandText = "PRAGMA table_info(sync_cursor)";
        using (var cursorReader = c.ExecuteReader())
        {
            var hasEpoch = false;
            while (cursorReader.Read()) if (cursorReader.GetString(1) == "epoch") hasEpoch = true;
            cursorReader.Close();
            if (!hasEpoch) { c.CommandText = "ALTER TABLE sync_cursor ADD COLUMN epoch TEXT"; c.ExecuteNonQuery(); }
        }
        c.CommandText = "PRAGMA table_info(sync_base)";
        using (var reader = c.ExecuteReader())
        {
            var hasRevision = false;
            var hasRecovery = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "revision") hasRevision = true;
                if (reader.GetString(1) == "recovery") hasRecovery = true;
            }
            reader.Close();
            if (!hasRevision) { c.CommandText = "ALTER TABLE sync_base ADD COLUMN revision INTEGER NOT NULL DEFAULT 0"; c.ExecuteNonQuery(); }
            if (!hasRecovery) { c.CommandText = "ALTER TABLE sync_base ADD COLUMN recovery INTEGER NOT NULL DEFAULT 0"; c.ExecuteNonQuery(); }
        }
        c.CommandText = "PRAGMA table_info(sync_epoch_conflicts)";
        using (var reader = c.ExecuteReader())
        {
            var hasRecoveryStarted = false;
            var hasSnapshotComplete = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "recovery_started") hasRecoveryStarted = true;
                if (reader.GetString(1) == "snapshot_complete") hasSnapshotComplete = true;
            }
            reader.Close();
            if (!hasRecoveryStarted) { c.CommandText = "ALTER TABLE sync_epoch_conflicts ADD COLUMN recovery_started INTEGER NOT NULL DEFAULT 0"; c.ExecuteNonQuery(); }
            if (!hasSnapshotComplete) { c.CommandText = "ALTER TABLE sync_epoch_conflicts ADD COLUMN snapshot_complete INTEGER NOT NULL DEFAULT 0"; c.ExecuteNonQuery(); }
        }
    }
    static string ReadBody(SqliteConnection db, SqliteTransaction tx, string kind, Guid id)
    {
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT body FROM items WHERE kind=$kind AND id=$id";
        c.Parameters.AddWithValue("$kind", kind); c.Parameters.AddWithValue("$id", id.ToString());
        return c.ExecuteScalar() as string ?? "{}";
    }
    static void CaptureBase(SqliteConnection db, SqliteTransaction tx, string mutation, string body, string kind, Guid entity)
    {
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "INSERT INTO sync_base(mutation,body,revision,recovery) VALUES($id,$body,COALESCE((SELECT revision FROM shadow WHERE kind=$kind AND id=$entity),0),COALESCE((SELECT recovery_started FROM sync_epoch_conflicts WHERE service='life'),0))";
        c.Parameters.AddWithValue("$kind",kind); c.Parameters.AddWithValue("$entity",entity.ToString()); c.Parameters.AddWithValue("$id", mutation); c.Parameters.AddWithValue("$body", body); c.ExecuteNonQuery();
    }
    public List<string> SyncConflicts()
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT conflict FROM sync_base WHERE conflict IS NOT NULL";
        using var reader = c.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }
    public List<string> SyncEpochConflicts()
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT service || ': ' || local_epoch || ' → ' || server_epoch FROM sync_epoch_conflicts ORDER BY service";
        using var reader = c.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }

    public List<LocalSyncEpochConflict> GetSyncEpochConflicts()
    {
        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT service,local_epoch,server_epoch,detected,recovery_started,snapshot_complete FROM sync_epoch_conflicts ORDER BY service";
        using var reader = c.ExecuteReader();
        var result = new List<LocalSyncEpochConflict>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0));
        return result;
    }

    public void BeginSyncEpochRecovery(string service, Guid serverEpoch)
    {
        if (serverEpoch == Guid.Empty || service is not ("life" or "learning"))
            throw new ArgumentException("A supported service and current server epoch are required.");
        if (service == "learning")
        {
            BeginLearningEpochRecovery(serverEpoch);
            return;
        }

        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT server_epoch,recovery_started FROM sync_epoch_conflicts WHERE service='life'";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("No Life sync-epoch conflict is waiting for recovery.");
            var recordedServerEpoch = reader.GetString(0);
            var recoveryStarted = reader.GetInt32(1) != 0;
            reader.Close();
            if (!Guid.TryParse(recordedServerEpoch, out var expected) || expected != serverEpoch)
            {
                command.CommandText = "UPDATE sync_epoch_conflicts SET server_epoch=$server,recovery_started=0,snapshot_complete=0,detected=$detected WHERE service='life'";
                command.Parameters.AddWithValue("$server", serverEpoch.ToString("D"));
                command.Parameters.AddWithValue("$detected", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
                transaction.Commit();
                throw new SyncEpochChangedException("The server changed again. Review the updated epoch before recovery.");
            }
            if (recoveryStarted)
            {
                transaction.Commit();
                return;
            }

            using (var conflicts = db.CreateCommand())
            {
                conflicts.Transaction = transaction;
                conflicts.CommandText = "SELECT conflict FROM sync_base WHERE conflict IS NOT NULL";
                using var readerConflicts = conflicts.ExecuteReader();
                var snapshots = new List<string>();
                while (readerConflicts.Read()) snapshots.Add(readerConflicts.GetString(0));
                readerConflicts.Close();
                foreach (var snapshot in snapshots) ArchiveRemoteConflict(db, transaction, "life", snapshot);
            }

            using (var archive = db.CreateCommand())
            {
                archive.Transaction = transaction;
                archive.CommandText = "UPDATE remote_conflicts SET archived=1,resolution_pending=0 WHERE service='life'";
                archive.ExecuteNonQuery();
            }

            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM sync_epoch_shadow_backup WHERE service='life'";
            if ((long)command.ExecuteScalar()! == 0)
            {
                command.CommandText = "INSERT INTO sync_epoch_shadow_backup(service,kind,entity,revision,body,deleted) SELECT 'life',kind,id,revision,body,deleted FROM shadow";
                command.ExecuteNonQuery();
            }
            command.CommandText = "INSERT OR IGNORE INTO sync_epoch_shadow_backup(service,kind,entity,revision,body,deleted) VALUES('life','_snapshot','_complete',0,'{}',0)";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM shadow";
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO sync_cursor(service,value,epoch) VALUES('life',0,$epoch) ON CONFLICT(service) DO UPDATE SET value=0,epoch=excluded.epoch";
            command.Parameters.AddWithValue("$epoch", serverEpoch.ToString("D"));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "UPDATE sync_epoch_conflicts SET recovery_started=1,snapshot_complete=0 WHERE service='life'";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    // The caller supplies an authenticated client. Tokens never enter SQLite.
    public async Task SynchronizeLifeAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        if (client.BaseAddress is null || client.BaseAddress.Scheme != "https") throw new InvalidOperationException("HTTPS API address required.");
        await syncGate.WaitAsync(cancellationToken);
        try
        {
            var serverState = await client.GetFromJsonAsync<ServerSyncState>("v1/sync/state", Wire, cancellationToken)
                ?? throw new InvalidDataException("Empty sync state.");
            var localState = CursorState();
            var epochConflict = GetSyncEpochConflicts().FirstOrDefault(conflict => conflict.Service == "life");
            if (epochConflict is not null &&
                (!epochConflict.RecoveryStarted || !Guid.TryParse(epochConflict.ServerEpoch, out var recoveryEpoch) || recoveryEpoch != serverState.Epoch))
            {
                if (!Guid.TryParse(epochConflict.ServerEpoch, out var recordedEpoch) || recordedEpoch != serverState.Epoch)
                    RecordEpochConflict(epochConflict.LocalEpoch, serverState.Epoch);
                throw new SyncEpochChangedException("The Life server history changed. Review the epoch recovery action in settings.");
            }
            if (localState.Epoch is null)
            {
                if (localState.Cursor != 0 || ShadowCount() != 0)
                {
                    RecordEpochConflict("untracked", serverState.Epoch);
                    throw new SyncEpochChangedException("This local cache predates sync epochs; its data is preserved for recovery.");
                }
                SaveEpoch(serverState.Epoch);
            }
            else if (!Guid.TryParse(localState.Epoch, out var localEpoch) || localEpoch != serverState.Epoch)
            {
                RecordEpochConflict(localState.Epoch, serverState.Epoch);
                throw new SyncEpochChangedException("The server sync epoch changed. Local records and queued edits were preserved.");
            }

            if (epochConflict is { RecoveryStarted: true, SnapshotComplete: false })
            {
                await DownloadLifeChangesAsync(client, serverState.Epoch, cancellationToken);
                ReconcileLifeEpochSnapshot(serverState.Epoch);
            }

            var blocked = new HashSet<string>();
            while (Prepare(blocked, serverState.Epoch) is { } pending)
            {
                using var response = await client.PostAsync("v1/sync", new StringContent(pending.Wire, Encoding.UTF8, "application/json"), cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
                    if (error?["code"]?.GetValue<string>() == "sync_epoch_changed")
                    {
                        var newEpoch = error["epoch"]?.GetValue<Guid>() ?? Guid.Empty;
                        if (newEpoch != Guid.Empty) RecordEpochConflict(serverState.Epoch.ToString("D"), newEpoch);
                        throw new SyncEpochChangedException("The server sync epoch changed during synchronization. Local changes were preserved.");
                    }
                }
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? throw new InvalidDataException("Empty sync response.");
                if (result["status"]?.GetValue<string>() == "conflict")
                {
                    using var db = Open(); using var c = db.CreateCommand();
                    c.CommandText = "UPDATE sync_base SET conflict=$value WHERE mutation=$id";
                    c.Parameters.AddWithValue("$id", pending.Id); c.Parameters.AddWithValue("$value", result.ToJsonString()); c.ExecuteNonQuery();
                    blocked.Add(pending.Kind + ":" + pending.Entity); continue;
                }
                if (result["status"]?.GetValue<string>() != "applied") throw new InvalidDataException("Unknown sync outcome.");
                Acknowledge(pending, result);
            }
            await DownloadLifeChangesAsync(client, serverState.Epoch, cancellationToken);
            CompleteEpochRecovery("life", serverState.Epoch);
        }
        finally { syncGate.Release(); }
    }

    async Task DownloadLifeChangesAsync(HttpClient client, Guid epoch, CancellationToken cancellationToken)
    {
        while (true)
        {
            var cursor = Cursor();
            using var response = await client.GetAsync($"v1/changes?after={cursor}&epoch={epoch:D}", cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var error = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
                if (error?["code"]?.GetValue<string>() == "sync_epoch_changed")
                {
                    var newEpoch = error["epoch"]?.GetValue<Guid>() ?? Guid.Empty;
                    if (newEpoch != Guid.Empty) RecordEpochConflict(epoch.ToString("D"), newEpoch);
                    throw new SyncEpochChangedException("The server sync epoch changed while downloading. Local records were preserved.");
                }
            }
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<ChangePage>(Wire, cancellationToken)
                ?? throw new InvalidDataException("Empty change page.");
            if (page.Epoch != epoch)
            {
                RecordEpochConflict(epoch.ToString("D"), page.Epoch);
                throw new SyncEpochChangedException("The server sync epoch changed while downloading. Local records were preserved.");
            }
            if (page.Items is null || page.Items.Count > 200 || page.Cursor < cursor ||
                page.Items.Any(change => change.Revision <= cursor || !LifeKinds.Contains(change.Kind)))
                throw new InvalidDataException("Invalid change page.");
            var expectedCursor = cursor;
            foreach (var change in page.Items)
            {
                if (change.Revision <= expectedCursor) throw new InvalidDataException("Unordered change feed.");
                expectedCursor = change.Revision;
            }
            if (page.Cursor != expectedCursor) throw new InvalidDataException("Cursor does not match change page.");
            using (var db = Open())
            using (var transaction = db.BeginTransaction())
            {
                foreach (var change in page.Items) Accept(db, transaction, change);
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO sync_cursor(service,value,epoch) VALUES('life',$value,$epoch) ON CONFLICT(service) DO UPDATE SET value=excluded.value,epoch=excluded.epoch";
                command.Parameters.AddWithValue("$value", page.Cursor);
                command.Parameters.AddWithValue("$epoch", page.Epoch.ToString("D"));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            if (page.Items.Count < 200) break;
            if (page.Cursor == cursor) throw new InvalidDataException("Non-advancing cursor.");
        }
    }

    void ReconcileLifeEpochSnapshot(Guid epoch)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT recovery_started,snapshot_complete,server_epoch FROM sync_epoch_conflicts WHERE service='life'";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt32(0) == 0 || reader.GetInt32(1) != 0 || reader.GetString(2) != epoch.ToString("D"))
                throw new InvalidOperationException("The Life recovery snapshot is not ready to reconcile.");
            reader.Close();
        }

        var previous = ReadEpochShadowBackup(db, transaction, "life");
        var current = ReadLifeShadow(db, transaction);
        var local = ReadLifeItems(db, transaction);
        var queued = ReadLifeRecoveryQueue(db, transaction);
        var keys = new HashSet<(string Kind, string Entity)>(previous.Keys);
        keys.UnionWith(current.Keys);
        keys.UnionWith(local.Keys);
        keys.UnionWith(queued.Keys);

        foreach (var key in keys)
        {
            if (!LifeKinds.Contains(key.Kind)) continue;
            previous.TryGetValue(key, out var oldServer);
            current.TryGetValue(key, out var server);
            local.TryGetValue(key, out var localBody);
            queued.TryGetValue(key, out var pending);
            if (pending is { HasLegacyOperation: true }) continue;

            var desiredBody = pending is null
                ? localBody
                : localBody ?? pending.LastBody;
            if (pending is not null && localBody is null && pending.LastBody is null)
                desiredBody = null;

            if (pending is not null)
            {
                if (desiredBody is null)
                {
                    if (server is { Deleted: false })
                        InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, pending.BaseBody ?? "{}", new JsonObject(), null, server.Revision, epoch, delete: true);
                    else
                        ClearLifeQueue(db, transaction, key.Kind, key.Entity);
                    continue;
                }

                if (server is { Deleted: false } && JsonNode.DeepEquals(ParseBody(desiredBody), ParseBody(server.Body)))
                {
                    ClearLifeQueue(db, transaction, key.Kind, key.Entity);
                    SetLifeItem(db, transaction, key.Kind, key.Entity, server.Body);
                    continue;
                }

                if (server is null)
                {
                    InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, "{}", ParseBody(desiredBody), desiredBody, 0, epoch, delete: false);
                    continue;
                }

                var baseBody = pending.BaseBody ?? "{}";
                var patch = server.Deleted
                    ? Diff(ParseBody(server.Body), ParseBody(desiredBody))
                    : Diff(ParseBody(baseBody), ParseBody(desiredBody));
                InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, baseBody, patch, desiredBody, server.Revision, epoch, delete: false);
                continue;
            }

            if (server is { Deleted: false })
            {
                if (desiredBody is not null)
                {
                    if (JsonNode.DeepEquals(ParseBody(desiredBody), ParseBody(server.Body))) continue;
                    var baseline = oldServer is { Deleted: false } ? oldServer.Body : "{}";
                    InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, baseline, ParseBody(desiredBody), desiredBody, server.Revision, epoch, delete: false);
                }
                else if (oldServer is { Deleted: true })
                {
                    InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, oldServer.Body, new JsonObject(), null, server.Revision, epoch, delete: true);
                }
                else
                {
                    SetLifeItem(db, transaction, key.Kind, key.Entity, server.Body);
                }
                continue;
            }

            if (server is { Deleted: true })
            {
                if (desiredBody is not null)
                {
                    var baseline = oldServer?.Body ?? "{}";
                    InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, baseline, ParseBody(desiredBody), desiredBody, server.Revision, epoch, delete: false);
                }
                continue;
            }

            if (desiredBody is not null)
                InsertLifeRecoveryMutation(db, transaction, key.Kind, key.Entity, "{}", ParseBody(desiredBody), desiredBody, 0, epoch, delete: false);
        }

        command.Parameters.Clear();
        command.CommandText = "UPDATE sync_epoch_conflicts SET snapshot_complete=1 WHERE service='life' AND server_epoch=$epoch AND recovery_started=1";
        command.Parameters.AddWithValue("$epoch", epoch.ToString("D"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    static Dictionary<(string Kind, string Entity), EpochShadow> ReadEpochShadowBackup(SqliteConnection db, SqliteTransaction transaction, string service)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind,entity,revision,body,deleted FROM sync_epoch_shadow_backup WHERE service=$service";
        command.Parameters.AddWithValue("$service", service);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), EpochShadow>();
        while (reader.Read()) result[(reader.GetString(0), reader.GetString(1))] = new(reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4) != 0);
        return result;
    }

    static Dictionary<(string Kind, string Entity), EpochShadow> ReadLifeShadow(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind,id,revision,body,deleted FROM shadow";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), EpochShadow>();
        while (reader.Read()) result[(reader.GetString(0), reader.GetString(1))] = new(reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4) != 0);
        return result;
    }

    static Dictionary<(string Kind, string Entity), string> ReadLifeItems(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        var kinds = LifeKinds.ToArray();
        command.CommandText = $"SELECT kind,id,body FROM items WHERE kind IN ({string.Join(",", kinds.Select((_, index) => "$kind" + index))})";
        for (var index = 0; index < kinds.Length; index++) command.Parameters.AddWithValue("$kind" + index, kinds[index]);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), string>();
        while (reader.Read()) result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        return result;
    }

    static Dictionary<(string Kind, string Entity), LifeRecoveryQueue> ReadLifeRecoveryQueue(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT o.id,o.kind,o.entity,o.body,b.body FROM outbox o LEFT JOIN sync_base b ON b.mutation=o.id ORDER BY o.rowid";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), LifeRecoveryQueue>();
        while (reader.Read())
        {
            var key = (reader.GetString(1), reader.GetString(2));
            var body = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (result.TryGetValue(key, out var prior))
                result[key] = prior with { LastBody = body, HasLegacyOperation = prior.HasLegacyOperation || reader.IsDBNull(4) };
            else
                result[key] = new(key.Item1, key.Item2, body, reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(4));
        }
        return result;
    }

    static void InsertLifeRecoveryMutation(SqliteConnection db, SqliteTransaction transaction, string kind, string entity, string baseBody, JsonObject patch, string? desiredBody, long revision, Guid epoch, bool delete)
    {
        ClearLifeQueue(db, transaction, kind, entity);
        var mutationId = Guid.NewGuid();
        var wire = JsonSerializer.Serialize(new
        {
            Id = mutationId, Kind = kind, EntityId = Guid.Parse(entity), BaseRevision = revision,
            Base = ParseBody(baseBody), Patch = delete ? new JsonObject() : patch,
            Delete = delete, Epoch = epoch, RecoveryCompare = true
        }, Wire);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO outbox(id,kind,entity,body,created) VALUES($id,$kind,$entity,$body,$created); INSERT INTO sync_base(mutation,body,wire,revision,recovery) VALUES($id,$base,$wire,$revision,1)";
        command.Parameters.AddWithValue("$id", mutationId.ToString("D"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$entity", entity);
        command.Parameters.AddWithValue("$body", (object?)desiredBody ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$base", baseBody);
        command.Parameters.AddWithValue("$wire", wire);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    static void ClearLifeQueue(SqliteConnection db, SqliteTransaction transaction, string kind, string entity)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM sync_base WHERE mutation IN (SELECT id FROM outbox WHERE kind=$kind AND entity=$entity); DELETE FROM outbox WHERE kind=$kind AND entity=$entity";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$entity", entity);
        command.ExecuteNonQuery();
    }

    static void SetLifeItem(SqliteConnection db, SqliteTransaction transaction, string kind, string entity, string body)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO items(kind,id,body) VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", entity);
        command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
    }

    static JsonObject ParseBody(string body) => JsonNode.Parse(body) as JsonObject ?? new JsonObject();
    sealed record EpochShadow(long Revision, string Body, bool Deleted);
    sealed record LifeRecoveryQueue(string Kind, string Entity, string? LastBody, string? BaseBody, bool HasLegacyOperation);
    sealed record PendingMutation(string Id, string Kind, string Entity, string Wire);
    PendingMutation? Prepare(HashSet<string> blocked, Guid epoch)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT o.id,o.kind,o.entity,o.body,b.body,b.wire,b.conflict,b.revision,b.recovery FROM outbox o LEFT JOIN sync_base b ON b.mutation=o.id ORDER BY o.rowid";
        using var reader = c.ExecuteReader(); PendingMutation? result = null;
        while (reader.Read())
        {
            var kind = reader.GetString(1); var entity = reader.GetString(2); var key = kind + ":" + entity;
            if (!LifeKinds.Contains(kind) || blocked.Contains(key)) continue;
            // Legacy operations lack a trustworthy base: retain them for explicit recovery.
            if (reader.IsDBNull(4) || !reader.IsDBNull(6)) { blocked.Add(key); continue; }
            var id = reader.GetString(0);
            if (!reader.IsDBNull(5))
            {
                var frozen = JsonNode.Parse(reader.GetString(5)) as JsonObject;
                if (frozen?["epoch"] is null || frozen["epoch"]!.GetValue<Guid>() != epoch) { blocked.Add(key); continue; }
                result = new(id, kind, entity, reader.GetString(5)); break;
            }
            var basis = JsonNode.Parse(reader.GetString(4))!.AsObject();
            var deleted = reader.IsDBNull(3); var desired = deleted ? new JsonObject() : JsonNode.Parse(reader.GetString(3))!.AsObject();
            var recoveryCompare = reader.GetInt32(8) != 0;
            var patch = new JsonObject();
            foreach (var field in desired) if (!JsonNode.DeepEquals(basis[field.Key], field.Value)) patch[field.Key] = field.Value?.DeepClone();
            var revision = reader.GetInt64(7);
            var wire = JsonSerializer.Serialize(new { Id = id, Kind = kind, EntityId = entity, BaseRevision = revision, Base = basis, Patch = patch, Delete = deleted, Epoch = epoch, RecoveryCompare = recoveryCompare }, Wire);
            result = new(id, kind, entity, wire); break;
        }
        reader.Close();
        if (result is not null)
        {
            c.CommandText = "UPDATE sync_base SET wire=$wire WHERE mutation=$id"; c.Parameters.AddWithValue("$wire", result.Wire); c.Parameters.AddWithValue("$id", result.Id); c.ExecuteNonQuery();
        }
        tx.Commit(); return result;
    }
    void Acknowledge(PendingMutation pending, JsonObject result)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "DELETE FROM outbox WHERE id=$id; DELETE FROM sync_base WHERE mutation=$id"; c.Parameters.AddWithValue("$id", pending.Id); c.ExecuteNonQuery();
        c.CommandText = "UPDATE sync_base SET revision=$revision WHERE wire IS NULL AND mutation IN (SELECT id FROM outbox WHERE kind=$kind AND entity=$entity)";
        c.Parameters.AddWithValue("$revision",result["revision"]!.GetValue<long>()); c.Parameters.AddWithValue("$kind",pending.Kind); c.Parameters.AddWithValue("$entity",pending.Entity); c.ExecuteNonQuery();
        Accept(db, tx, new(result["revision"]!.GetValue<long>(), pending.Kind, Guid.Parse(pending.Entity), result["body"]!.ToJsonString(), result["deleted"]!.GetValue<bool>()));
        tx.Commit();
    }
    static void Accept(SqliteConnection db, SqliteTransaction tx, RemoteChange change)
    {
        using var c = db.CreateCommand(); c.Transaction = tx;
        c.Parameters.AddWithValue("$kind", change.Kind); c.Parameters.AddWithValue("$id", change.EntityId.ToString()); c.Parameters.AddWithValue("$revision", change.Revision); c.Parameters.AddWithValue("$body", change.Body); c.Parameters.AddWithValue("$deleted", change.Deleted);
        c.CommandText = "SELECT revision FROM shadow WHERE kind=$kind AND id=$id";
        if (c.ExecuteScalar() is long existing && existing >= change.Revision) return;
        c.CommandText = "INSERT INTO shadow(kind,id,revision,body,deleted) VALUES($kind,$id,$revision,$body,$deleted) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision,body=excluded.body,deleted=excluded.deleted"; c.ExecuteNonQuery();
        c.Parameters.Clear();
        c.CommandText = "SELECT COUNT(*) FROM sync_epoch_conflicts WHERE service='life' AND recovery_started=1 AND snapshot_complete=0";
        if ((long)c.ExecuteScalar()! != 0) return;
        c.Parameters.Clear();
        c.Parameters.AddWithValue("$kind", change.Kind);
        c.Parameters.AddWithValue("$id", change.EntityId.ToString());
        c.CommandText = "SELECT COUNT(*) FROM outbox WHERE kind=$kind AND entity=$id";
        if ((long)c.ExecuteScalar()! != 0) return;
        c.CommandText = change.Deleted ? "DELETE FROM items WHERE kind=$kind AND id=$id" : "INSERT INTO items(kind,id,body) VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body"; c.ExecuteNonQuery();
    }
    long Cursor() => CursorState().Cursor;
    (long Cursor, string? Epoch) CursorState()
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT value,epoch FROM sync_cursor WHERE service='life'";
        using var reader = c.ExecuteReader(); return reader.Read() ? (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : (0, null);
    }
    long ShadowCount() { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM shadow"; return (long)c.ExecuteScalar()!; }
    void SaveEpoch(Guid epoch)
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "INSERT INTO sync_cursor(service,value,epoch) VALUES('life',0,$epoch) ON CONFLICT(service) DO UPDATE SET epoch=excluded.epoch"; c.Parameters.AddWithValue("$epoch", epoch.ToString("D")); c.ExecuteNonQuery();
    }
    void RecordEpochConflict(string localEpoch, Guid serverEpoch) => RecordEpochConflict(localEpoch, serverEpoch.ToString("D"));
    void RecordEpochConflict(string localEpoch, string serverEpoch)
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "INSERT INTO sync_epoch_conflicts(service,local_epoch,server_epoch,detected,recovery_started,snapshot_complete) VALUES('life',$local,$server,$detected,0,0) ON CONFLICT(service) DO UPDATE SET local_epoch=excluded.local_epoch,server_epoch=excluded.server_epoch,detected=excluded.detected,recovery_started=0,snapshot_complete=0";
        c.Parameters.AddWithValue("$local",localEpoch); c.Parameters.AddWithValue("$server",serverEpoch); c.Parameters.AddWithValue("$detected",DateTimeOffset.UtcNow.ToString("O")); c.ExecuteNonQuery();
    }
    void CompleteEpochRecovery(string service, Guid epoch)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = transaction;
        c.CommandText = "DELETE FROM sync_epoch_conflicts WHERE service=$service AND server_epoch=$epoch AND recovery_started=1";
        c.Parameters.AddWithValue("$service", service);
        c.Parameters.AddWithValue("$epoch", epoch.ToString("D"));
        if (c.ExecuteNonQuery() > 0)
        {
            c.Parameters.Clear();
            c.CommandText = "DELETE FROM sync_epoch_shadow_backup WHERE service=$service";
            c.Parameters.AddWithValue("$service", service);
            c.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    sealed record ServerSyncState(Guid Epoch);
    sealed record RemoteChange(long Revision, string Kind, Guid EntityId, string Body, bool Deleted);
    sealed record ChangePage(List<RemoteChange> Items, long Cursor, Guid Epoch);
}

public sealed class SyncEpochChangedException(string message) : InvalidOperationException(message);
