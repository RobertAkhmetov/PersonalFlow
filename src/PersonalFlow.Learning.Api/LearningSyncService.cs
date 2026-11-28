using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace PersonalFlow.Learning.Api;

public sealed class LearningSyncService(LearningDb db)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Fields = ["Answer", "Notes", "Evidence", "Status", "ViewedAt"];

    public static async Task<Guid> RotateEpochAsync(LearningDb db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(70312420)", cancellationToken);
        var state = await db.SyncStates.FindAsync(["learning"], cancellationToken)
            ?? throw new InvalidOperationException("The Learning sync state has not been initialized.");
        state.Epoch = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return state.Epoch;
    }

    public async Task<IResult> Apply(LearningMutation mutation, CancellationToken cancellationToken)
    {
        if (mutation is null || mutation.Id == Guid.Empty || mutation.LessonId == Guid.Empty ||
            mutation.ContentVersion < 1 || mutation.BaseRevision < 0 || mutation.Base is null ||
            mutation.Patch is null || mutation.Epoch == Guid.Empty || mutation.Base.Count > 8 || mutation.Patch.Count > 8 ||
            mutation.Base.Select(pair => pair.Key).Concat(mutation.Patch.Select(pair => pair.Key)).Any(key => !Fields.Contains(key)) ||
            (mutation.Delete && mutation.Patch.Count != 0) ||
            JsonSerializer.Serialize(mutation, Wire).Length > 110_000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mutation"] = ["The progress change is invalid or exceeds the supported size."] });

        if (!await db.Lessons.AnyAsync(lesson => lesson.Id == mutation.LessonId && lesson.Version == mutation.ContentVersion, cancellationToken))
            return Results.NotFound(new { code = "lesson_version_not_found" });

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mutation, Wire))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(70312420)", cancellationToken);
        var state = await db.SyncStates.FindAsync(["learning"], cancellationToken);
        if (state is null || state.Epoch != mutation.Epoch)
            return Results.Conflict(new { code = "sync_epoch_changed", epoch = state?.Epoch });
        var receipt = await db.Receipts.FindAsync([mutation.Id], cancellationToken);
        if (receipt is not null)
            return receipt.Digest == digest ? Results.Content(receipt.Result, "application/json") : Results.Conflict(new { code = "mutation_id_reused" });

        var row = await db.Progress.FindAsync([mutation.LessonId, mutation.ContentVersion], cancellationToken);
        var current = JsonNode.Parse(row?.Body ?? "{}")!.AsObject();
        var conflict = row is null ? mutation.BaseRevision != 0 : row.Deleted ||
            (!mutation.RecoveryCompare && mutation.BaseRevision > row.Revision);
        if (row is not null && (row.Revision != mutation.BaseRevision || mutation.RecoveryCompare))
        {
            if (mutation.Delete) conflict = true;
            else foreach (var field in mutation.Patch)
            {
                mutation.Base.TryGetPropertyValue(field.Key, out var oldValue);
                current.TryGetPropertyValue(field.Key, out var serverValue);
                if (!JsonNode.DeepEquals(oldValue, serverValue) && !JsonNode.DeepEquals(serverValue, field.Value)) conflict = true;
            }
        }

        ProgressConflict? resolving = null;
        if (mutation.Resolves is { } conflictId)
        {
            resolving = await db.Conflicts.FindAsync([conflictId], cancellationToken);
            if (resolving is null || resolving.LessonId != mutation.LessonId || resolving.ContentVersion != mutation.ContentVersion)
                return Results.BadRequest(new { code = "invalid_resolution" });
            var resolutionConverged = row is not null && row.Deleted == mutation.Delete && mutation.Patch.All(field =>
                JsonNode.DeepEquals(current[field.Key], field.Value));
            if (resolving.Resolved && !resolutionConverged) conflict = true;
            if (row?.Revision != mutation.BaseRevision && !resolutionConverged) conflict = true;
            if (resolving.ServerDeleted && row?.Deleted == true && row.Revision == mutation.BaseRevision)
                conflict = false;
        }

        object outcome;
        if (conflict)
        {
            var saved = new ProgressConflict
            {
                Id = Guid.NewGuid(), LessonId = mutation.LessonId, ContentVersion = mutation.ContentVersion,
                Base = mutation.Base.ToJsonString(), Local = mutation.Patch.ToJsonString(),
                Server = current.ToJsonString(), ServerRevision = row?.Revision ?? 0,
                LocalDeleted = mutation.Delete, ServerDeleted = row?.Deleted == true
            };
            db.Conflicts.Add(saved);
            if (resolving is not null) resolving.Resolved = true;
            outcome = new { status = "conflict", conflict = saved };
        }
        else
        {
            foreach (var field in mutation.Patch) current[field.Key] = field.Value?.DeepClone();
            var revision = (await db.Changes.MaxAsync(change => (long?)change.Revision, cancellationToken) ?? 0) + 1;
            if (row is null) { row = new ProgressRow { LessonId = mutation.LessonId, ContentVersion = mutation.ContentVersion }; db.Progress.Add(row); }
            row.Body = current.ToJsonString(); row.Revision = revision; row.Deleted = mutation.Delete;
            db.Changes.Add(new ProgressChange { Revision = revision, LessonId = mutation.LessonId, ContentVersion = mutation.ContentVersion, Body = row.Body, Deleted = row.Deleted });
            if (resolving is not null) resolving.Resolved = true;
            outcome = new { status = "applied", revision, body = current, deleted = mutation.Delete };
        }

        var json = JsonSerializer.Serialize(outcome, Wire);
        db.Receipts.Add(new ProgressReceipt { Id = mutation.Id, Digest = digest, Result = json });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return Results.Content(json, "application/json");
    }
}
