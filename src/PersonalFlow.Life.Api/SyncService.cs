using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace PersonalFlow.Life.Api;

public sealed class LifeDb(DbContextOptions<LifeDb> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<Change> Changes => Set<Change>();
    public DbSet<Conflict> Conflicts => Set<Conflict>();
    public DbSet<LifeSyncState> SyncStates => Set<LifeSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Item>().HasKey(item => new { item.Kind, item.Id });
        builder.Entity<Item>().HasIndex(item => item.Revision);
        builder.Entity<Receipt>().HasKey(receipt => receipt.Id);
        builder.Entity<Change>().HasKey(change => change.Revision);
        builder.Entity<Change>().Property(change => change.Revision).ValueGeneratedNever();
        builder.Entity<Change>().HasIndex(change => new { change.Kind, change.EntityId });
        builder.Entity<Conflict>().HasKey(conflict => conflict.Id);
        builder.Entity<Conflict>().HasIndex(conflict => new { conflict.Resolved, conflict.EntityId });
        builder.Entity<LifeSyncState>().HasKey(state => state.Id);
    }
}

public sealed class Item
{
    public string Kind { get; set; } = "";
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public string Body { get; set; } = "{}";
    public bool Deleted { get; set; }
}

public sealed class Receipt
{
    public Guid Id { get; set; }
    public string Digest { get; set; } = "";
    public string Result { get; set; } = "";
}

public sealed class Change
{
    public long Revision { get; set; }
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Body { get; set; } = "{}";
    public bool Deleted { get; set; }
}

public sealed class Conflict
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Base { get; set; } = "{}";
    public string Local { get; set; } = "{}";
    public string Server { get; set; } = "{}";
    public long ServerRevision { get; set; }
    public bool LocalDeleted { get; set; }
    public bool ServerDeleted { get; set; }
    public bool Resolved { get; set; }
}

public sealed class LifeSyncState { public string Id { get; set; } = "life"; public Guid Epoch { get; set; } }

public sealed record Mutation(Guid Id, string Kind, Guid EntityId, long BaseRevision, JsonObject Base, JsonObject Patch, bool Delete = false, Guid? Resolves = null, Guid Epoch = default, bool RecoveryCompare = false);

public sealed class SyncService(LifeDb db)
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, HashSet<string>> Fields = new(StringComparer.Ordinal)
    {
        ["PlanItem"] = ["Id", "Title", "Date", "Done", "Time", "WorkoutDayId"],
        ["NoteItem"] = ["Id", "Text", "Created", "Pinned"],
        ["TrainingDay"] = ["Id", "Name", "Exercises"],
        ["TrainingLog"] = ["Id", "DayId", "Name", "Snapshot", "Started", "Results", "Finished", "ScheduleItemId", "ScheduleSeriesId", "ScheduleOriginalDate"],
        ["Recurrence"] = ["Id", "PlanItemId", "Title", "Time", "StartsOn", "Frequency", "Interval", "Weekdays", "MonthDays", "EndsOn", "ExclusionCategory", "WorkoutDayId"],
        ["Vacation"] = ["Id", "Category", "Start", "End", "Title"],
        ["OccurrenceOverride"] = ["Id", "SeriesId", "OriginalDate", "MovedTo", "Done", "Skipped"]
    };

    public async Task<IResult> Apply(Mutation mutation, CancellationToken cancellationToken)
    {
        if (!IsValid(mutation))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["mutation"] = ["The mutation is invalid or exceeds the supported size."]
            });

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mutation, Wire))));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(70312419)", cancellationToken);

        var state = await db.SyncStates.FindAsync(["life"], cancellationToken);
        if (state is null || state.Epoch != mutation.Epoch)
            return Results.Conflict(new { code = "sync_epoch_changed", epoch = state?.Epoch });

        var receipt = await db.Receipts.FindAsync([mutation.Id], cancellationToken);
        if (receipt is not null)
            return receipt.Digest == digest
                ? Results.Content(receipt.Result, "application/json")
                : Results.Conflict(new { code = "mutation_id_reused" });

        var item = await db.Items.FindAsync([mutation.Kind, mutation.EntityId], cancellationToken);
        var current = JsonNode.Parse(item?.Body ?? "{}")!.AsObject();
        var conflict = item is null
            ? mutation.BaseRevision != 0
            : item.Deleted || (!mutation.RecoveryCompare && mutation.BaseRevision > item.Revision) ||
              (!mutation.RecoveryCompare && mutation.BaseRevision == 0 && !mutation.Delete);

        if (item is not null && (mutation.BaseRevision != item.Revision || mutation.RecoveryCompare))
        {
            if (mutation.Delete)
                conflict = true;
            else
            {
                foreach (var field in mutation.Patch)
                {
                    mutation.Base.TryGetPropertyValue(field.Key, out var oldValue);
                    current.TryGetPropertyValue(field.Key, out var serverValue);
                    if (!JsonNode.DeepEquals(oldValue, serverValue) && !JsonNode.DeepEquals(serverValue, field.Value))
                        conflict = true;
                }
            }
        }

        Conflict? resolving = null;
        if (mutation.Resolves is { } conflictId)
        {
            resolving = await db.Conflicts.FindAsync([conflictId], cancellationToken);
            if (resolving is null || resolving.Kind != mutation.Kind || resolving.EntityId != mutation.EntityId)
                return Results.BadRequest(new { code = "invalid_resolution" });
            var resolutionConverged = item is not null && item.Deleted == mutation.Delete && mutation.Patch.All(field =>
                JsonNode.DeepEquals(current[field.Key], field.Value));
            if (resolving.Resolved && !resolutionConverged) conflict = true;
            if (item?.Revision != mutation.BaseRevision && !resolutionConverged)
                conflict = true;
            if (resolving.ServerDeleted && item?.Deleted == true && item.Revision == mutation.BaseRevision)
                conflict = false;
        }

        object outcome;
        if (conflict)
        {
            var storedConflict = new Conflict
            {
                Id = Guid.NewGuid(), Kind = mutation.Kind, EntityId = mutation.EntityId,
                Base = mutation.Base.ToJsonString(), Local = mutation.Patch.ToJsonString(),
                Server = current.ToJsonString(), ServerRevision = item?.Revision ?? 0,
                LocalDeleted = mutation.Delete, ServerDeleted = item?.Deleted == true
            };
            db.Conflicts.Add(storedConflict);
            if (resolving is not null)
                resolving.Resolved = true;
            outcome = new { status = "conflict", conflict = storedConflict };
        }
        else
        {
            foreach (var field in mutation.Patch)
                current[field.Key] = field.Value?.DeepClone();

            var revision = (await db.Changes.MaxAsync(change => (long?)change.Revision, cancellationToken) ?? 0) + 1;
            if (item is null)
            {
                item = new Item { Kind = mutation.Kind, Id = mutation.EntityId };
                db.Items.Add(item);
            }
            item.Body = current.ToJsonString();
            item.Revision = revision;
            item.Deleted = mutation.Delete;
            db.Changes.Add(new Change
            {
                Revision = revision, Kind = mutation.Kind, EntityId = mutation.EntityId,
                Body = item.Body, Deleted = mutation.Delete
            });
            if (resolving is not null)
                resolving.Resolved = true;
            outcome = new { status = "applied", revision, body = current, deleted = mutation.Delete };
        }

        var json = JsonSerializer.Serialize(outcome, Wire);
        db.Receipts.Add(new Receipt { Id = mutation.Id, Digest = digest, Result = json });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Content(json, "application/json");
    }

    public static async Task<Guid> RotateEpochAsync(LifeDb db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(70312419)", cancellationToken);
        var state = await db.SyncStates.FindAsync(["life"], cancellationToken)
            ?? throw new InvalidOperationException("The Life sync state has not been initialized.");
        state.Epoch = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state.Epoch;
    }

    private static bool IsValid(Mutation? mutation)
    {
        if (mutation is null || mutation.Id == Guid.Empty || mutation.EntityId == Guid.Empty ||
            !Fields.TryGetValue(mutation.Kind, out var allowed) || mutation.BaseRevision < 0 ||
            mutation.Epoch == Guid.Empty || mutation.Base is null || mutation.Patch is null || mutation.Base.Count > 40 ||
            mutation.Patch.Count > 40 || (mutation.Delete && mutation.Patch.Count != 0))
            return false;

        if (mutation.Base.Select(pair => pair.Key).Concat(mutation.Patch.Select(pair => pair.Key)).Any(key => !allowed.Contains(key)))
            return false;

        var json = JsonSerializer.Serialize(mutation, Wire);
        return json.Length <= 100_000 && mutation.Patch.All(pair => !string.IsNullOrWhiteSpace(pair.Key));
    }
}
