using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using PersonalFlow.Hosting;
using PersonalFlow.Life.Api;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
HostingDefaults.Configure(builder);
builder.Services.AddOpenApi();
builder.Services.AddDbContext<LifeDb>(options => options.UseNpgsql(DatabaseSecrets.GetConnectionString(builder.Configuration)));

var authority = builder.Configuration["Identity:Authority"]
    ?? throw new InvalidOperationException("Configure Identity:Authority.");
var ownerId = builder.Configuration["Identity:OwnerId"]
    ?? throw new InvalidOperationException("Configure Identity:OwnerId.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Authority = authority;
        options.Audience = "personalflow";
        options.RequireHttpsMetadata = true;
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("owner", policy => policy.RequireAuthenticatedUser().RequireClaim("sub", ownerId)));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("writes", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddScoped<SyncService>();

var app = builder.Build();
HostingDefaults.ConfigurePipeline(app);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LifeDb>();
    if (await VersionedSqlMigrations.HandleCommandAsync(db, args, 70312419))
        return;
    if (args.Contains("--rotate-sync-epoch", StringComparer.Ordinal))
    {
        await VersionedSqlMigrations.ApplyAsync(db, 70312419);
        Console.WriteLine($"SYNC_EPOCH={await SyncService.RotateEpochAsync(db)}");
        return;
    }
}

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (LifeDb db, CancellationToken ct) =>
    await VersionedSqlMigrations.IsCurrentAsync(db, ct)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var api = app.MapGroup("/v1").RequireAuthorization("owner");
api.MapGet("/sync/state", async (LifeDb db, CancellationToken ct) =>
{
    var state = await db.SyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "life", ct);
    return state is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(new { epoch = state.Epoch });
});
api.MapPost("/sync", async (Mutation mutation, SyncService sync, CancellationToken ct) =>
    await sync.Apply(mutation, ct)).RequireRateLimiting("writes");
api.MapGet("/changes", async (long? after, Guid? epoch, LifeDb db, CancellationToken ct) =>
{
    var state = await db.SyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == "life", ct);
    if (state is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (epoch is null || epoch != state.Epoch) return Results.Conflict(new { code = "sync_epoch_changed", epoch = state.Epoch });
    if (after is < 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["after"] = ["Cursor must be zero or greater."] });

    var rows = await db.Changes.AsNoTracking()
        .Where(change => change.Revision > (after ?? 0))
        .OrderBy(change => change.Revision)
        .Take(200)
        .Select(change => new { change.Revision, change.Kind, change.EntityId, change.Body, change.Deleted })
        .ToListAsync(ct);
    return Results.Ok(new { items = rows, cursor = rows.LastOrDefault()?.Revision ?? after ?? 0, epoch = state.Epoch });
});
api.MapGet("/conflicts", async (LifeDb db, CancellationToken ct) =>
    Results.Ok(await db.Conflicts.AsNoTracking()
        .Where(conflict => !conflict.Resolved)
        .OrderBy(conflict => conflict.Id)
        .Select(conflict => new
        {
            conflict.Id, conflict.Kind, conflict.EntityId, conflict.Base, conflict.Local,
            conflict.Server, conflict.ServerRevision, conflict.LocalDeleted, conflict.ServerDeleted, conflict.Resolved
        })
        .ToListAsync(ct)));

app.Run();
