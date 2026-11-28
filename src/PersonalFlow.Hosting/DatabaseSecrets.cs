using Microsoft.Extensions.Configuration;
namespace PersonalFlow.Hosting;
public static class DatabaseSecrets
{
    public static string GetConnectionString(IConfiguration configuration, string fileKey = "ConnectionStrings:DatabaseFile")
    {
        var path = configuration[fileKey];
        if (!string.IsNullOrWhiteSpace(path)) { var value = File.ReadAllText(path).Trim(); return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("The database connection secret is empty.") : value; }
        return configuration.GetConnectionString("Database") ?? throw new InvalidOperationException("Configure the database connection string.");
    }
    public static string Get(IConfiguration configuration, string key, string? fileKey = null)
    {
        if (fileKey is not null && !string.IsNullOrWhiteSpace(configuration[fileKey])) return File.ReadAllText(configuration[fileKey]!).Trim();
        return configuration[key] ?? throw new InvalidOperationException($"Configure {key}.");
    }
}
