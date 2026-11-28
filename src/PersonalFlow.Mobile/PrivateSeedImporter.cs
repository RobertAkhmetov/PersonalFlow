using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalFlow.Client;
using PersonalFlow.Core;

namespace PersonalFlow.Mobile;

public sealed class PrivateSeedImporter(LocalStore store)
{
    private const string AssetName = "personalflow-private-seed.json";
    private const string ImportedKey = "personalflow.private-seed.version";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public async Task ImportIfNeededAsync()
    {
#if DEBUG
        if (Preferences.Default.Get(ImportedKey, 0) >= 1) return;

        PrivateSeed? seed;
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(AssetName);
            seed = await JsonSerializer.DeserializeAsync<PrivateSeed>(stream, Json);
        }
        catch (IOException)
        {
            return; // The private seed is optional and is absent from public checkouts.
        }
        catch (JsonException)
        {
            return; // Keep the app usable if a local seed file is malformed.
        }

        if (seed?.Version != 1) return;

        var recurrenceIds = store.All<Recurrence>().Select(x => x.Id).ToHashSet();
        foreach (var recurrence in seed.Recurrences.Where(x => !recurrenceIds.Contains(x.Id)))
            store.Save(recurrence.Id, recurrence);

        var vacationIds = store.All<Vacation>().Select(x => x.Id).ToHashSet();
        foreach (var vacation in seed.Vacations.Where(x => !vacationIds.Contains(x.Id)))
            store.Save(vacation.Id, vacation);

        var trainingIds = store.All<TrainingDay>().Select(x => x.Id).ToHashSet();
        foreach (var workout in seed.Workouts.Where(x => !trainingIds.Contains(x.Id)))
            store.Save(workout.Id, new TrainingDay(workout.Id, workout.Name, WorkoutDocuments.WritePlan(workout)));

        Preferences.Default.Set(ImportedKey, 1);
#endif
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PrivateSeed(
        int Version,
        List<Recurrence> Recurrences,
        List<Vacation> Vacations,
        List<WorkoutTemplate> Workouts);
}
