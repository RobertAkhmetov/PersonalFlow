using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalFlow.Client;

public sealed record ClientSyncConflict(
    Guid Id,
    string Service,
    string Kind,
    Guid EntityId,
    Guid? LessonId,
    int? ContentVersion,
    string Base,
    string Local,
    string Server,
    long ServerRevision,
    bool HasLocalMutation,
    bool LocalDeleted = false,
    bool ServerDeleted = false,
    bool ResolutionPending = false,
    bool ArchivedEpoch = false);

public sealed partial class LocalStore
{
    static void InitializeConflictInbox(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS remote_conflicts(service TEXT NOT NULL,id TEXT NOT NULL,body TEXT NOT NULL,resolution_pending INTEGER NOT NULL DEFAULT 0,archived INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(service,id));";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(remote_conflicts)";
        using var reader = command.ExecuteReader();
        var hasPending = false;
        var hasArchived = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "resolution_pending") hasPending = true;
            if (reader.GetString(1) == "archived") hasArchived = true;
        }
        reader.Close();
        if (!hasPending)
        {
            command.CommandText = "ALTER TABLE remote_conflicts ADD COLUMN resolution_pending INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        if (!hasArchived)
        {
            command.CommandText = "ALTER TABLE remote_conflicts ADD COLUMN archived INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
    }

    public List<ClientSyncConflict> GetSyncConflictInbox()
    {
        var conflicts = new Dictionary<string, ClientSyncConflict>(StringComparer.Ordinal);
        using var db = Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT conflict FROM sync_base WHERE conflict IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonNode.Parse(reader.GetString(0)) is JsonObject result && result["conflict"] is JsonObject item &&
                        ParseConflict("life", item, true) is { } conflict)
                        conflicts[ConflictKey(conflict)] = conflict;
                }
                catch (JsonException) { }
            }
        }

        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT conflict FROM learning_sync_base WHERE conflict IS NOT NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonNode.Parse(reader.GetString(0)) is JsonObject result && result["conflict"] is JsonObject item &&
                        ParseConflict("learning", item, true) is { } conflict)
                        conflicts[ConflictKey(conflict)] = conflict;
                }
                catch (JsonException) { }
            }
        }

        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT service,body,resolution_pending,archived FROM remote_conflicts";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (JsonNode.Parse(reader.GetString(1)) is JsonObject item &&
                        ParseConflict(reader.GetString(0), item, false) is { } conflict)
                    {
                        if (reader.GetInt32(2) != 0) conflict = conflict with { ResolutionPending = true };
                        if (reader.GetInt32(3) != 0) conflict = conflict with { ArchivedEpoch = true };
                        var key = ConflictKey(conflict);
                        if (!conflicts.TryGetValue(key, out var existing) || existing.ArchivedEpoch && !conflict.ArchivedEpoch)
                            conflicts[key] = conflict;
                    }
                }
                catch (JsonException) { }
            }
        }

        return conflicts.Values.OrderBy(x => x.ArchivedEpoch).ThenBy(x => x.Service, StringComparer.Ordinal).ThenBy(x => x.Id).ToList();
    }

    public void ReplaceRemoteConflictSnapshot(string service, string json)
    {
        if (service is not ("life" or "learning")) throw new ArgumentOutOfRangeException(nameof(service));
        if (JsonNode.Parse(json) is not JsonArray items || items.Count > 500)
            throw new InvalidDataException("The server conflict list is invalid.");

        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM remote_conflicts WHERE service=$service AND archived=0";
        command.Parameters.AddWithValue("$service", service);
        command.ExecuteNonQuery();
        foreach (var item in items.OfType<JsonObject>())
        {
            if (!Guid.TryParse(item["id"]?.GetValue<string>(), out var id) || id == Guid.Empty) continue;
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO remote_conflicts(service,id,body,resolution_pending,archived) VALUES($service,$id,$body,0,0) ON CONFLICT(service,id) DO UPDATE SET body=excluded.body,resolution_pending=0,archived=0";
            command.Parameters.AddWithValue("$service", service);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$body", item.ToJsonString());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string? GetConflictCurrentBody(ClientSyncConflict conflict)
    {
        var storageKind = conflict.Service == "learning" ? "LearningProgress" : conflict.Kind;
        var storageId = conflict.Service == "learning" && conflict.LessonId is { } lesson && conflict.ContentVersion is { } version
            ? LearningProgressStorageId(lesson, version)
            : conflict.EntityId;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT body FROM items WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", storageKind);
        command.Parameters.AddWithValue("$id", storageId.ToString());
        return command.ExecuteScalar() as string;
    }

    static void ArchiveRemoteConflict(SqliteConnection db, SqliteTransaction transaction, string service, string conflictJson)
    {
        try
        {
            if (JsonNode.Parse(conflictJson) is not JsonObject envelope || envelope["conflict"] is not JsonObject conflict ||
                !Guid.TryParse(conflict["id"]?.ToString(), out var id) || id == Guid.Empty)
                return;
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO remote_conflicts(service,id,body,resolution_pending,archived) VALUES($service,$id,$body,0,1) ON CONFLICT(service,id) DO UPDATE SET body=excluded.body,resolution_pending=0,archived=1";
            command.Parameters.AddWithValue("$service", service);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$body", conflict.ToJsonString());
            command.ExecuteNonQuery();
        }
        catch (JsonException) { }
    }

    public void ResolveSyncConflict(ClientSyncConflict conflict, JsonObject? resolvedBody, bool delete)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (conflict.ArchivedEpoch)
            throw new InvalidOperationException("An archived conflict is read-only. Resolve its current replacement conflict instead.");
        var server = ParseObject(conflict.Server);
        var body = resolvedBody is null ? null : (JsonObject)resolvedBody.DeepClone();

        if (conflict.Service == "life")
        {
            if (conflict.Kind is not ("PlanItem" or "NoteItem" or "TrainingDay" or "TrainingLog" or "Recurrence" or "Vacation" or "OccurrenceOverride") ||
                (!delete && (!Guid.TryParse(body?["Id"]?.ToString(), out var bodyId) || bodyId != conflict.EntityId)))
                throw new InvalidDataException("The selected version does not match this Life record.");

            var patch = delete ? new JsonObject() : Diff(server, body!);
            using var db = Open();
            using var transaction = db.BeginTransaction();
            var epoch = ReadRequiredEpoch(db, transaction, "life");
            var mutationId = Guid.NewGuid();
            var wire = JsonSerializer.Serialize(new
            {
                Id = mutationId,
                Kind = conflict.Kind,
                EntityId = conflict.EntityId,
                BaseRevision = conflict.ServerRevision,
                Base = server,
                Patch = patch,
                Delete = delete,
                Resolves = conflict.Id,
                Epoch = epoch
            }, Wire);
            ReplaceLifeQueue(db, transaction, conflict, mutationId, server.ToJsonString(), body?.ToJsonString(), wire, delete);
            MarkConflictResolutionPending(db, transaction, conflict);
            transaction.Commit();
            return;
        }

        if (conflict.Service != "learning" || conflict.LessonId is not { } lessonId ||
            conflict.ContentVersion is not { } contentVersion ||
            (!delete && (!Guid.TryParse(body?["LessonId"]?.ToString(), out var bodyLesson) || bodyLesson != lessonId ||
                        !int.TryParse(body?["ContentVersion"]?.ToString(), out var bodyVersion) || bodyVersion != contentVersion)))
            throw new InvalidDataException("The selected version does not match this Learning progress record.");

        if (body is not null)
            foreach (var key in body.Select(pair => pair.Key).Where(key => key is not ("LessonId" or "ContentVersion")))
                if (key is not ("Answer" or "Notes" or "Evidence" or "Status" or "ViewedAt"))
                    throw new InvalidDataException("The selected Learning version contains an unsupported field.");
        var learningServer = ProjectLearningFields(server);
        var learningBody = body is null ? new JsonObject() : ProjectLearningFields(body);
        var learningPatch = delete ? new JsonObject() : Diff(learningServer, learningBody);
        if (!delete)
        {
            learningBody["LessonId"] = lessonId;
            learningBody["ContentVersion"] = contentVersion;
        }

        using (var db = Open())
        using (var transaction = db.BeginTransaction())
        {
            var epoch = ReadRequiredEpoch(db, transaction, "learning");
            var mutationId = Guid.NewGuid();
            var wire = JsonSerializer.Serialize(new
            {
                Id = mutationId,
                LessonId = lessonId,
                ContentVersion = contentVersion,
                BaseRevision = conflict.ServerRevision,
                Base = learningServer,
                Patch = learningPatch,
                Delete = delete,
                Resolves = conflict.Id,
                Epoch = epoch
            }, LearningWire);
            ReplaceLearningQueue(db, transaction, conflict, mutationId, learningServer.ToJsonString(), delete ? null : learningBody.ToJsonString(), wire, delete);
            MarkConflictResolutionPending(db, transaction, conflict);
            transaction.Commit();
        }
    }

    static JsonObject? NormalizeConflict(string service, JsonObject item, bool hasLocalMutation)
    {
        if (!Guid.TryParse(item["id"]?.ToString(), out var id) || id == Guid.Empty) return null;
        var lesson = Guid.TryParse(item["lessonId"]?.ToString(), out var lessonId) ? lessonId : (Guid?)null;
        var version = int.TryParse(item["contentVersion"]?.ToString(), out var parsedVersion) ? parsedVersion : (int?)null;
        var entity = Guid.TryParse(item["entityId"]?.ToString(), out var entityId)
            ? entityId
            : lesson is { } lessonValue && version is { } lessonVersion
                ? LearningProgressStorageId(lessonValue, lessonVersion)
                : Guid.Empty;
        if (entity == Guid.Empty) return null;
        return new JsonObject
        {
            ["Id"] = id,
            ["Service"] = service,
            ["Kind"] = item["kind"]?.GetValue<string>() ?? "LearningProgress",
            ["EntityId"] = entity,
            ["LessonId"] = lesson,
            ["ContentVersion"] = version,
            ["Base"] = item["base"]?.DeepClone(),
            ["Local"] = item["local"]?.DeepClone(),
            ["Server"] = item["server"]?.DeepClone(),
            ["ServerRevision"] = item["serverRevision"]?.GetValue<long>() ?? 0,
            ["HasLocalMutation"] = hasLocalMutation,
            ["LocalDeleted"] = item["localDeleted"]?.GetValue<bool>() ?? false,
            ["ServerDeleted"] = item["serverDeleted"]?.GetValue<bool>() ?? false
        };
    }

    static ClientSyncConflict? ParseConflict(string service, JsonObject item, bool hasLocalMutation)
    {
        var normalized = NormalizeConflict(service, item, hasLocalMutation);
        if (normalized is null) return null;
        return new(
            normalized["Id"]!.GetValue<Guid>(),
            normalized["Service"]!.GetValue<string>(),
            normalized["Kind"]!.GetValue<string>(),
            normalized["EntityId"]!.GetValue<Guid>(),
            normalized["LessonId"]?.GetValue<Guid?>(),
            normalized["ContentVersion"]?.GetValue<int?>(),
            ReadJsonText(normalized["Base"]),
            ReadJsonText(normalized["Local"]),
            ReadJsonText(normalized["Server"]),
            normalized["ServerRevision"]!.GetValue<long>(),
            normalized["HasLocalMutation"]!.GetValue<bool>(),
            normalized["LocalDeleted"]!.GetValue<bool>(),
            normalized["ServerDeleted"]!.GetValue<bool>());
    }

    static string ConflictKey(ClientSyncConflict conflict) => conflict.Service + ":" + conflict.Id.ToString("D");

    static string ReadJsonText(JsonNode? value) => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
        ? text ?? "{}"
        : value?.ToJsonString() ?? "{}";

    static JsonObject ParseObject(string json) => JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    static JsonObject Diff(JsonObject baseline, JsonObject selected)
    {
        var patch = new JsonObject();
        foreach (var pair in selected)
        {
            baseline.TryGetPropertyValue(pair.Key, out var previous);
            if (!JsonNode.DeepEquals(previous, pair.Value)) patch[pair.Key] = pair.Value?.DeepClone();
        }
        return patch;
    }

    static string ReadRequiredEpoch(SqliteConnection db, SqliteTransaction transaction, string service)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT epoch FROM sync_cursor WHERE service=$service";
        command.Parameters.AddWithValue("$service", service);
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("Сначала синхронизируйте состояние сервера.");
    }

    static void ReplaceLifeQueue(SqliteConnection db, SqliteTransaction transaction, ClientSyncConflict conflict,
        Guid mutationId, string baseline, string? body, string wire, bool deleted)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM sync_base WHERE mutation IN (SELECT id FROM outbox WHERE kind=$kind AND entity=$entity); DELETE FROM outbox WHERE kind=$kind AND entity=$entity;";
        command.Parameters.AddWithValue("$kind", conflict.Kind);
        command.Parameters.AddWithValue("$entity", conflict.EntityId.ToString());
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = deleted
            ? "DELETE FROM items WHERE kind=$kind AND id=$entity"
            : "INSERT INTO items(kind,id,body) VALUES($kind,$entity,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        command.Parameters.AddWithValue("$kind", conflict.Kind);
        command.Parameters.AddWithValue("$entity", conflict.EntityId.ToString());
        if (!deleted) command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO outbox(id,kind,entity,body,created) VALUES($id,$kind,$entity,$body,$created); INSERT INTO sync_base(mutation,body,wire,revision) VALUES($id,$base,$wire,$revision)";
        command.Parameters.AddWithValue("$id", mutationId.ToString("D"));
        command.Parameters.AddWithValue("$kind", conflict.Kind);
        command.Parameters.AddWithValue("$entity", conflict.EntityId.ToString());
        command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$base", baseline);
        command.Parameters.AddWithValue("$wire", wire);
        command.Parameters.AddWithValue("$revision", conflict.ServerRevision);
        command.ExecuteNonQuery();
    }

    static void ReplaceLearningQueue(SqliteConnection db, SqliteTransaction transaction, ClientSyncConflict conflict,
        Guid mutationId, string baseline, string? body, string wire, bool deleted)
    {
        var lessonId = conflict.LessonId!.Value.ToString("D");
        var version = conflict.ContentVersion!.Value;
        var storageId = LearningProgressStorageId(conflict.LessonId.Value, version);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM learning_sync_base WHERE mutation IN (SELECT id FROM learning_outbox WHERE lesson_id=$lesson AND content_version=$version); DELETE FROM learning_outbox WHERE lesson_id=$lesson AND content_version=$version;";
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = deleted
            ? "DELETE FROM items WHERE kind='LearningProgress' AND id=$storage"
            : "INSERT INTO items(kind,id,body) VALUES('LearningProgress',$storage,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        command.Parameters.AddWithValue("$storage", storageId.ToString());
        if (!deleted) command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO learning_outbox(id,lesson_id,content_version,body,created) VALUES($id,$lesson,$version,$body,$created); INSERT INTO learning_sync_base(mutation,lesson_id,content_version,body,wire,revision) VALUES($id,$lesson,$version,$base,$wire,$revision)";
        command.Parameters.AddWithValue("$id", mutationId.ToString("D"));
        command.Parameters.AddWithValue("$lesson", lessonId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$base", baseline);
        command.Parameters.AddWithValue("$wire", wire);
        command.Parameters.AddWithValue("$revision", conflict.ServerRevision);
        command.ExecuteNonQuery();
    }

    static void MarkConflictResolutionPending(SqliteConnection db, SqliteTransaction transaction, ClientSyncConflict conflict)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        var body = JsonSerializer.Serialize(new
        {
            Id = conflict.Id,
            Kind = conflict.Kind,
            EntityId = conflict.Service == "learning" ? (Guid?)null : conflict.EntityId,
            LessonId = conflict.LessonId,
            ContentVersion = conflict.ContentVersion,
            Base = conflict.Base,
            Local = conflict.Local,
            Server = conflict.Server,
            ServerRevision = conflict.ServerRevision,
            LocalDeleted = conflict.LocalDeleted,
            ServerDeleted = conflict.ServerDeleted,
            Resolved = false
        }, Wire);
        command.CommandText = "INSERT INTO remote_conflicts(service,id,body,resolution_pending) VALUES($service,$id,$body,1) ON CONFLICT(service,id) DO UPDATE SET resolution_pending=1";
        command.Parameters.AddWithValue("$service", conflict.Service);
        command.Parameters.AddWithValue("$id", conflict.Id.ToString("D"));
        command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
    }
}
