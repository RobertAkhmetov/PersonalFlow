using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using PersonalFlow.Hosting;
using PersonalFlow.Learning.Api;
using System.Threading.RateLimiting;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
HostingDefaults.Configure(builder);
builder.Services.AddOpenApi();
builder.Services.AddDbContext<LearningDb>(options => options.UseNpgsql(DatabaseSecrets.GetConnectionString(builder.Configuration)));
var authority = builder.Configuration["Identity:Authority"] ?? throw new InvalidOperationException("Configure Identity:Authority.");
var ownerId = builder.Configuration["Identity:OwnerId"] ?? throw new InvalidOperationException("Configure Identity:OwnerId.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false; options.Authority = authority; options.Audience = "personalflow"; options.RequireHttpsMetadata = true;
});
builder.Services.AddAuthorization(options => options.AddPolicy("owner", policy => policy.RequireAuthenticatedUser().RequireClaim("sub", ownerId)));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("writes", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("sub")?.Value ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("publishing", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddScoped<LearningSyncService>();
var publisherKey = DatabaseSecrets.Get(builder.Configuration, "Publishing:ApiKey", "Publishing:ApiKeyFile");
var app = builder.Build();
HostingDefaults.ConfigurePipeline(app); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LearningDb>();
    if (await VersionedSqlMigrations.HandleCommandAsync(db, args, 70312420)) return;
    if (args.Contains("--rotate-sync-epoch", StringComparer.Ordinal))
    {
        await VersionedSqlMigrations.ApplyAsync(db, 70312420);
        Console.WriteLine($"SYNC_EPOCH={await LearningSyncService.RotateEpochAsync(db)}");
        return;
    }
}

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (LearningDb db, CancellationToken ct) => await VersionedSqlMigrations.IsCurrentAsync(db, ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
if (app.Environment.IsDevelopment()) app.MapOpenApi();

var api = app.MapGroup("/v1");
api.MapGet("/sync/state", async (LearningDb db, CancellationToken ct) =>
{
    var state = await db.SyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "learning", ct);
    return state is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(new { epoch = state.Epoch });
}).RequireAuthorization("owner");
api.MapGet("/lessons", async (DateOnly? from, DateOnly? to, LearningDb db, CancellationToken ct) =>
{
    if (from > to) return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["The start date must not follow the end date."] });
    var query = db.Lessons.AsNoTracking();
    if (from is { } start) query = query.Where(lesson => lesson.Date >= start);
    if (to is { } end) query = query.Where(lesson => lesson.Date <= end);
    var rows = await query.OrderBy(lesson => lesson.Date).ThenBy(lesson => lesson.Id).ToListAsync(ct);
    return Results.Ok(rows.Select(lesson => new { lesson.Id, lesson.Version, lesson.Date, Content = JsonSerializer.Deserialize<Lesson>(lesson.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }));
}).RequireAuthorization("owner");
api.MapPost("/progress/sync", async (LearningMutation mutation, LearningSyncService sync, CancellationToken ct) => await sync.Apply(mutation, ct)).RequireAuthorization("owner").RequireRateLimiting("writes");
api.MapGet("/progress/changes", async (long? after, Guid? epoch, LearningDb db, CancellationToken ct) =>
{
    var state = await db.SyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "learning", ct);
    if (state is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (epoch is null || epoch != state.Epoch) return Results.Conflict(new { code = "sync_epoch_changed", epoch = state.Epoch });
    if (after is < 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["after"] = ["Cursor must be zero or greater."] });
    var rows = await db.Changes.AsNoTracking().Where(change => change.Revision > (after ?? 0)).OrderBy(change => change.Revision).Take(200).Select(change => new { change.Revision, change.LessonId, change.ContentVersion, change.Body, change.Deleted }).ToListAsync(ct);
    return Results.Ok(new { items = rows, cursor = rows.LastOrDefault()?.Revision ?? after ?? 0, epoch = state.Epoch });
}).RequireAuthorization("owner");
api.MapGet("/progress/conflicts", async (LearningDb db, CancellationToken ct) => Results.Ok(await db.Conflicts.AsNoTracking().Where(x => !x.Resolved).OrderBy(x => x.Id).ToListAsync(ct))).RequireAuthorization("owner");
api.MapPost("/packs", async (HttpContext context, LessonPack pack, LearningDb db, CancellationToken ct) =>
{
    var supplied = context.Request.Headers["X-PersonalFlow-Publisher-Key"].ToString();
    if (!FixedTimeEquals(publisherKey, supplied)) return Results.Unauthorized();
    if (pack is null || pack.Id == Guid.Empty || pack.Version < 1 || pack.Lessons is null || pack.Lessons.Count is < 1 or > 31)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pack"] = ["The lesson pack is invalid."] });
    var totalContent = JsonSerializer.Serialize(pack).Length;
    if (totalContent > 4_500_000 || pack.Lessons.Any(x => x is null) || pack.Lessons.Select(x => (x.Id, x.Version)).Distinct().Count() != pack.Lessons.Count || pack.Lessons.Select(x => x.Date).Distinct().Count() != pack.Lessons.Count)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["pack"] = ["The pack is too large or contains duplicate lesson IDs, versions, or dates."] });
    foreach (var lesson in pack.Lessons)
    {
        if (lesson is null || lesson.Id == Guid.Empty || lesson.Version < 1 || string.IsNullOrWhiteSpace(lesson.Title) || lesson.Title.Length > 200 ||
            lesson.EstimatedMinutes is < 1 or > 60 || string.IsNullOrWhiteSpace(lesson.Theory) || lesson.Theory.Length > 100_000 || lesson.SelfCheck is { Length: > 20_000 } ||
            lesson.Sources is null || lesson.Sources.Count is < 1 or > 20 ||
            lesson.Sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Title) || source.Title.Length > 300 || string.IsNullOrWhiteSpace(source.Url) || source.Url.Length > 2048 || !Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || source.Section?.Length > 300) ||
            (lesson.Date.DayOfWeek == DayOfWeek.Sunday && (lesson.EstimatedMinutes != 5 || !string.IsNullOrWhiteSpace(lesson.Practice))) ||
            (lesson.Date.DayOfWeek != DayOfWeek.Sunday && (lesson.EstimatedMinutes > 20 || string.IsNullOrWhiteSpace(lesson.Practice) || lesson.Practice.Length > 20_000)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { [$"lesson:{lesson?.Id}"] = ["Lesson metadata, sources, duration, or Sunday practice rules are invalid."] });
    }
    var content = JsonSerializer.Serialize(pack, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(70312421)", ct);
    var prior = await db.Packs.FindAsync([pack.Id, pack.Version], ct);
    if (prior is not null) return prior.Digest == digest ? Results.Ok(new { status = "already_published", digest }) : Results.Conflict(new { code = "immutable_version" });
    foreach (var lesson in pack.Lessons)
    {
        var existing = await db.Lessons.FindAsync([lesson.Id, lesson.Version], ct);
        var serialized = JsonSerializer.Serialize(lesson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (existing is not null && existing.Content != serialized) return Results.Conflict(new { code = "lesson_version_conflict", lessonId = lesson.Id, lessonVersion = lesson.Version });
        if (existing is null) db.Lessons.Add(new LessonRow { Id = lesson.Id, Version = lesson.Version, Date = lesson.Date, Content = serialized });
    }
    db.Packs.Add(new PackRow { Id = pack.Id, Version = pack.Version, Content = content, Digest = digest });
    await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Ok(new { status = "published", digest });
}).AllowAnonymous().RequireRateLimiting("publishing");

app.Run();

static bool FixedTimeEquals(string expected, string supplied)
{
    var left = Encoding.UTF8.GetBytes(expected); var right = Encoding.UTF8.GetBytes(supplied);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
