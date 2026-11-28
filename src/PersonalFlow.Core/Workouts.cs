namespace PersonalFlow.Core;

public sealed record SetTarget(Guid Id, int? Repetitions, decimal? Load, string? Unit, string? Note);

public sealed record ExerciseTemplate(Guid Id, string Name, IReadOnlyList<SetTarget> Sets, string? Note = null);

public sealed record WorkoutTemplate(Guid Id, string Name, IReadOnlyList<ExerciseTemplate> Exercises);

public sealed record SetResult(Guid SetId, int? Repetitions, decimal? Load, bool Completed);

public sealed class WorkoutSession
{
    private readonly Dictionary<Guid, SetResult> results = [];
    private readonly HashSet<Guid> setIds;

    public Guid Id { get; }
    public WorkoutTemplate Snapshot { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public IReadOnlyCollection<SetResult> Results => results.Values.ToArray();

    public WorkoutSession(Guid id, WorkoutTemplate template, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template.Name))
            throw new ArgumentException("Workout name is required.");
        if (template.Exercises.Any(exercise => string.IsNullOrWhiteSpace(exercise.Name)))
            throw new ArgumentException("Exercise name is required.");
        var targets = template.Exercises.SelectMany(exercise => exercise.Sets).ToArray();
        if (targets.Any(target => target.Repetitions < 0 || target.Load < 0))
            throw new ArgumentException("Targets cannot be negative.");
        setIds = targets.Select(target => target.Id).ToHashSet();
        if (setIds.Count != targets.Length)
            throw new ArgumentException("Set identifiers must be unique.");
        Id = id;
        StartedAt = startedAt;
        Snapshot = template with
        {
            Exercises = Array.AsReadOnly(template.Exercises.Select(exercise =>
                exercise with { Sets = Array.AsReadOnly(exercise.Sets.ToArray()) }).ToArray())
        };
    }

    public void Record(SetResult result)
    {
        if (FinishedAt is not null)
            throw new InvalidOperationException("Reopen the session before editing.");
        if (!setIds.Contains(result.SetId))
            throw new ArgumentException("Unknown set.");
        if (result.Repetitions < 0 || result.Load < 0)
            throw new ArgumentException("Results cannot be negative.");
        results[result.SetId] = result;
    }

    public void Finish(DateTimeOffset at)
    {
        if (at < StartedAt) throw new ArgumentException("Finish precedes start.");
        FinishedAt ??= at;
    }
}
