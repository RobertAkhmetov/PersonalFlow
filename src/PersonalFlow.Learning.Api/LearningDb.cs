using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
namespace PersonalFlow.Learning.Api;

public sealed class LearningDb(DbContextOptions<LearningDb> options) : DbContext(options)
{
    public DbSet<LessonRow> Lessons => Set<LessonRow>();
    public DbSet<PackRow> Packs => Set<PackRow>();
    public DbSet<ProgressRow> Progress => Set<ProgressRow>();
    public DbSet<ProgressReceipt> Receipts => Set<ProgressReceipt>();
    public DbSet<ProgressChange> Changes => Set<ProgressChange>();
    public DbSet<ProgressConflict> Conflicts => Set<ProgressConflict>();
    public DbSet<LearningSyncState> SyncStates => Set<LearningSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<LessonRow>().HasKey(x => new { x.Id, x.Version });
        builder.Entity<LessonRow>().HasIndex(x => x.Date);
        builder.Entity<PackRow>().HasKey(x => new { x.Id, x.Version });
        builder.Entity<ProgressRow>().HasKey(x => new { x.LessonId, x.ContentVersion });
        builder.Entity<ProgressReceipt>().HasKey(x => x.Id);
        builder.Entity<ProgressChange>().HasKey(x => x.Revision);
        builder.Entity<ProgressChange>().Property(x => x.Revision).ValueGeneratedNever();
        builder.Entity<ProgressChange>().HasIndex(x => new { x.LessonId, x.ContentVersion });
        builder.Entity<ProgressConflict>().HasKey(x => x.Id);
        builder.Entity<ProgressConflict>().HasIndex(x => new { x.Resolved, x.LessonId });
        builder.Entity<LearningSyncState>().HasKey(x => x.Id);
    }
}

public sealed class LessonRow { public Guid Id { get; set; } public int Version { get; set; } public DateOnly Date { get; set; } public string Content { get; set; } = ""; }
public sealed class PackRow { public Guid Id { get; set; } public int Version { get; set; } public string Digest { get; set; } = ""; public string Content { get; set; } = ""; }
public sealed record LessonPack(Guid Id, int Version, List<Lesson> Lessons);
public sealed record Lesson(Guid Id, int Version, DateOnly Date, string Title, int EstimatedMinutes, string Theory, string? Practice, List<LessonSource> Sources,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SelfCheck = null);
public sealed record LessonSource(string Title, string Url, string? Section, DateOnly VerifiedOn);

public sealed class ProgressRow { public Guid LessonId { get; set; } public int ContentVersion { get; set; } public long Revision { get; set; } public string Body { get; set; } = "{}"; public bool Deleted { get; set; } }
public sealed class ProgressReceipt { public Guid Id { get; set; } public string Digest { get; set; } = ""; public string Result { get; set; } = ""; }
public sealed class ProgressChange { public long Revision { get; set; } public Guid LessonId { get; set; } public int ContentVersion { get; set; } public string Body { get; set; } = "{}"; public bool Deleted { get; set; } }
public sealed class ProgressConflict { public Guid Id { get; set; } public Guid LessonId { get; set; } public int ContentVersion { get; set; } public string Base { get; set; } = "{}"; public string Local { get; set; } = "{}"; public string Server { get; set; } = "{}"; public long ServerRevision { get; set; } public bool LocalDeleted { get; set; } public bool ServerDeleted { get; set; } public bool Resolved { get; set; } }
public sealed class LearningSyncState { public string Id { get; set; } = "learning"; public Guid Epoch { get; set; } }
public sealed record LearningMutation(Guid Id, Guid LessonId, int ContentVersion, long BaseRevision, JsonObject Base, JsonObject Patch, bool Delete = false, Guid? Resolves = null, Guid Epoch = default, bool RecoveryCompare = false);
