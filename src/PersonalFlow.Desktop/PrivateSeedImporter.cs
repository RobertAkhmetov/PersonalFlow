using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalFlow.Client;
using PersonalFlow.Core;

namespace PersonalFlow.Desktop;

internal static class PrivateSeedImporter
{
    private static readonly Guid StateId = Guid.Parse("f58346c0-5601-4a62-9a74-a43a8d447d32");
    private static readonly JsonSerializerOptions Json = CreateOptions();

    public static void ImportIfPresent(LocalStore store)
    {
#if DEBUG
        if (store.All<PrivateSeedImportState>().Any(state => state.Id == StateId && state.Version >= 1)) return;
        var path = Path.Combine(AppContext.BaseDirectory, "personalflow-private-seed.json");
        if (!File.Exists(path)) return;

        PrivateSeed? seed;
        try { seed = JsonSerializer.Deserialize<PrivateSeed>(File.ReadAllText(path), Json); }
        catch (IOException) { return; }
        catch (JsonException) { return; }
        if (seed?.Version != 1) return;

        var recurrenceIds = store.All<Recurrence>().Select(item => item.Id).ToHashSet();
        foreach (var rule in seed.Recurrences.Where(item => !recurrenceIds.Contains(item.Id)))
            store.Save(rule.Id, rule);

        var vacationIds = store.All<Vacation>().Select(item => item.Id).ToHashSet();
        foreach (var vacation in seed.Vacations.Where(item => !vacationIds.Contains(item.Id)))
            store.Save(vacation.Id, vacation);

        var workoutIds = store.All<TrainingDay>().Select(item => item.Id).ToHashSet();
        foreach (var workout in seed.Workouts.Where(item => !workoutIds.Contains(item.Id)))
            store.Save(workout.Id, new TrainingDay(workout.Id, workout.Name, WorkoutDocuments.WritePlan(workout)));

        store.Save(StateId, new PrivateSeedImportState(StateId, seed.Version));
#endif
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PrivateSeed(int Version, List<Recurrence> Recurrences, List<Vacation> Vacations, List<WorkoutTemplate> Workouts);
    private sealed record PrivateSeedImportState(Guid Id, int Version);
}
