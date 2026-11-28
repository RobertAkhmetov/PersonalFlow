using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PersonalFlow.Core;

namespace PersonalFlow.Client;

public sealed record WorkoutPlanDocument(int Version, WorkoutTemplate Template);
public sealed record WorkoutLogDocument(int Version, IReadOnlyList<SetResult> Sets, string Notes);

public sealed class WorkoutExerciseDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public List<WorkoutSetDraft> Sets { get; set; } = [new()];
}

public sealed class WorkoutSetDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Repetitions { get; set; } = "10";
    public string Load { get; set; } = string.Empty;
    public string Unit { get; set; } = "кг";
}

public static partial class WorkoutDocuments
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex RepetitionPattern = RepetitionRegex();
    private static readonly Regex LoadPattern = LoadRegex();

    public static WorkoutTemplate ReadPlan(Guid id, string name, string source)
    {
        if (!string.IsNullOrWhiteSpace(source) && source.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(source);
                if (document.RootElement.TryGetProperty("template", out _))
                    return JsonSerializer.Deserialize<WorkoutPlanDocument>(source, Json)!.Template;
                return JsonSerializer.Deserialize<WorkoutTemplate>(source, Json)!;
            }
            catch (JsonException) { }
        }

        var exercises = new List<ExerciseTemplate>();
        foreach (var line in source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            exercises.Add(ParseLegacyExercise(line));
        return new WorkoutTemplate(id, name, exercises);
    }

    public static string WritePlan(WorkoutTemplate template) =>
        JsonSerializer.Serialize(new WorkoutPlanDocument(1, template), Json);

    public static WorkoutLogDocument ReadLog(string source)
    {
        if (!string.IsNullOrWhiteSpace(source) && source.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(source);
                if (document.RootElement.TryGetProperty("sets", out _))
                    return JsonSerializer.Deserialize<WorkoutLogDocument>(source, Json)!;
            }
            catch (JsonException) { }
        }
        return new WorkoutLogDocument(1, [], source);
    }

    public static string WriteLog(WorkoutLogDocument document) => JsonSerializer.Serialize(document, Json);

    public static List<WorkoutExerciseDraft> ToDrafts(WorkoutTemplate template) => template.Exercises.Select(exercise =>
        new WorkoutExerciseDraft
        {
            Id = exercise.Id,
            Name = exercise.Name,
            Note = exercise.Note ?? string.Empty,
            Sets = exercise.Sets.Select(set => new WorkoutSetDraft
            {
                Id = set.Id,
                Repetitions = set.Repetitions?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Load = set.Load?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Unit = set.Unit ?? string.Empty
            }).ToList()
        }).ToList();

    public static bool TryBuildTemplate(Guid id, string name, IReadOnlyList<WorkoutExerciseDraft> drafts,
        out WorkoutTemplate? template, out string error)
    {
        template = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) { error = "Укажите название дня тренировки"; return false; }
        if (drafts.Count == 0) { error = "Добавьте хотя бы одно упражнение"; return false; }

        var exercises = new List<ExerciseTemplate>();
        foreach (var exercise in drafts)
        {
            if (string.IsNullOrWhiteSpace(exercise.Name)) { error = "Заполните название каждого упражнения"; return false; }
            if (exercise.Sets.Count == 0) { error = $"Добавьте подход для упражнения «{exercise.Name}»"; return false; }
            var sets = new List<SetTarget>();
            foreach (var set in exercise.Sets)
            {
                int? repetitions = null;
                decimal? load = null;
                if (!string.IsNullOrWhiteSpace(set.Repetitions))
                {
                    if (!int.TryParse(set.Repetitions, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 1000)
                    { error = $"Проверьте число повторов: {exercise.Name}"; return false; }
                    repetitions = parsed;
                }
                if (!string.IsNullOrWhiteSpace(set.Load))
                {
                    if (!decimal.TryParse(set.Load.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 100_000)
                    { error = $"Проверьте нагрузку: {exercise.Name}"; return false; }
                    load = parsed;
                }
                if (repetitions is null && load is null && string.IsNullOrWhiteSpace(exercise.Note))
                { error = $"Укажите цель подхода или заметку: {exercise.Name}"; return false; }
                sets.Add(new SetTarget(set.Id, repetitions, load, NullIfBlank(set.Unit), null));
            }
            exercises.Add(new ExerciseTemplate(exercise.Id, exercise.Name.Trim(), sets, NullIfBlank(exercise.Note)));
        }
        template = new WorkoutTemplate(id, name.Trim(), exercises);
        return true;
    }

    public static string FormatTarget(SetTarget target)
    {
        var parts = new List<string>();
        if (target.Repetitions is { } reps) parts.Add($"{reps} повт.");
        if (target.Load is { } load) parts.Add($"{load.ToString(CultureInfo.InvariantCulture)}{(string.IsNullOrWhiteSpace(target.Unit) ? string.Empty : $" {target.Unit}")}");
        if (!string.IsNullOrWhiteSpace(target.Note)) parts.Add(target.Note.Trim());
        if (parts.Count == 0) return "Свободная цель";
        return string.Join(" · ", parts);
    }

    public static string FormatSetResult(SetTarget target, SetResult? result)
    {
        if (result is null) return "Не заполнен";
        var parts = new List<string>();
        if (result.Repetitions is { } reps) parts.Add($"{reps} повт.");
        if (result.Load is { } load) parts.Add($"{load.ToString(CultureInfo.InvariantCulture)}{(string.IsNullOrWhiteSpace(target.Unit) ? string.Empty : $" {target.Unit}")}");
        return parts.Count == 0 ? (result.Completed ? "Выполнен" : "Пропущен") : string.Join(" · ", parts);
    }

    private static ExerciseTemplate ParseLegacyExercise(string line)
    {
        var repetition = RepetitionPattern.Match(line);
        if (!repetition.Success || !int.TryParse(repetition.Groups["sets"].Value, out var setCount) || !int.TryParse(repetition.Groups["reps"].Value, out var reps) || setCount is < 1 or > 30)
            return new ExerciseTemplate(Guid.NewGuid(), line, [new SetTarget(Guid.NewGuid(), null, null, null, null)]);

        var rest = repetition.Groups["rest"].Value;
        var loadMatch = LoadPattern.Match(rest);
        decimal? load = null;
        string? unit = null;
        if (loadMatch.Success && decimal.TryParse(loadMatch.Groups["load"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedLoad))
        {
            load = parsedLoad;
            unit = NullIfBlank(loadMatch.Groups["unit"].Value);
            rest = rest.Remove(loadMatch.Index, loadMatch.Length);
        }
        var note = NullIfBlank(rest.Trim());
        var sets = Enumerable.Range(0, setCount).Select(_ => new SetTarget(Guid.NewGuid(), reps, load, unit, null)).ToArray();
        return new ExerciseTemplate(Guid.NewGuid(), repetition.Groups["name"].Value.Trim(), sets, note);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^(?<name>.+?)\s+(?<sets>\d+)\s*[xх×]\s*(?<reps>\d+)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepetitionRegex();

    [GeneratedRegex(@"\((?<load>\d{1,6}(?:[.,]\d{1,3})?)(?<unit>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex LoadRegex();
}
