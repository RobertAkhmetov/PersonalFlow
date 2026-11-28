using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
namespace PersonalFlow.Hosting;
public static class VersionedSqlMigrations
{
    private const string HistoryTable = "__PersonalFlowMigrations";

    public static async Task<bool> HandleCommandAsync(DbContext db, string[] args, long lockId, CancellationToken cancellationToken = default)
    {
        if (args.Contains("--export-schema", StringComparer.Ordinal))
        {
            Console.Out.Write(db.Database.GenerateCreateScript());
            return true;
        }
        if (!args.Contains("--migrate", StringComparer.Ordinal)) return false;
        await ApplyAsync(db, lockId, cancellationToken);
        Console.WriteLine("Database migrations are up to date.");
        return true;
    }

    public static async Task ApplyAsync(DbContext db, long lockId, CancellationToken cancellationToken = default)
    {
        var migrations = GetMigrations(db.GetType().Assembly);
        if (migrations.Count == 0) throw new InvalidOperationException("The service image contains no database migration scripts.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [lockId], cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"CREATE TABLE IF NOT EXISTS \"{HistoryTable}\" (\"Version\" text PRIMARY KEY, \"Checksum\" text NOT NULL, \"AppliedAt\" timestamptz NOT NULL DEFAULT now())", cancellationToken);
        var applied = await ReadHistoryAsync(db, cancellationToken);
        var byId = migrations.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var version in applied.Keys)
            if (!byId.ContainsKey(version)) throw new InvalidOperationException($"Database contains migration '{version}' which is absent from this release.");

        foreach (var migration in migrations)
        {
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql)));
            if (applied.TryGetValue(migration.Id, out var previousChecksum))
            {
                if (!string.Equals(previousChecksum, checksum, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Applied migration '{migration.Id}' was modified. Add a new migration instead.");
                continue;
            }
            await db.Database.ExecuteSqlRawAsync(migration.Sql, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"{HistoryTable}\" (\"Version\", \"Checksum\") VALUES ({migration.Id}, {checksum})", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<bool> IsCurrentAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var migrations = GetMigrations(db.GetType().Assembly);
        if (migrations.Count == 0 || !await db.Database.CanConnectAsync(cancellationToken)) return false;
        try
        {
            var applied = await ReadHistoryAsync(db, cancellationToken);
            if (applied.Count != migrations.Count) return false;
            for (var index = 0; index < migrations.Count; index++)
            {
                var migration = migrations[index];
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql)));
                if (!applied.TryGetValue(migration.Id, out var value) || !string.Equals(value, checksum, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static async Task<Dictionary<string, string>> ReadHistoryAsync(DbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<string>($"SELECT \"Version\" || ':' || \"Checksum\" AS \"Value\" FROM \"{HistoryTable}\"").ToListAsync(cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var separator = row.IndexOf(':');
            if (separator <= 0 || separator == row.Length - 1 || !result.TryAdd(row[..separator], row[(separator + 1)..]))
                throw new InvalidOperationException("Database migration history is malformed.");
        }
        return result;
    }

    private static List<MigrationScript> GetMigrations(Assembly assembly)
    {
        const string marker = ".Migrations.";
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.Contains(marker, StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Cannot read migration resource {resource}.");
            using var reader = new StreamReader(stream);
            var id = resource[(resource.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..^4];
            scripts.Add(new(id, reader.ReadToEnd()));
        }
        return scripts;
    }

    private sealed record MigrationScript(string Id, string Sql);
}
