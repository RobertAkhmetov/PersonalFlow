using System.Security.Cryptography;
using System.Text;
using PersonalFlow.Core;

namespace PersonalFlow.Client;

// Property names match the versioned Life sync contract.
public sealed record Recurrence(
    Guid Id,
    Guid PlanItemId,
    string Title,
    string? Time,
    DateOnly StartsOn,
    RecurrenceFrequency Frequency,
    int Interval,
    DayOfWeek[] Weekdays,
    int[] MonthDays,
    DateOnly? EndsOn = null,
    string? ExclusionCategory = null,
    Guid? WorkoutDayId = null);

public sealed record Vacation(Guid Id, string Category, DateOnly Start, DateOnly End, string Title);

// One stable override per original occurrence; a move never changes the rule.
public sealed record OccurrenceOverride(
    Guid Id,
    Guid SeriesId,
    DateOnly OriginalDate,
    DateOnly? MovedTo = null,
    bool? Done = null,
    bool Skipped = false);

public sealed record PlannedEntry(
    Guid Id,
    string Title,
    DateOnly Date,
    bool Done,
    string? Time,
    Guid? SeriesId = null,
    DateOnly? OriginalDate = null,
    Guid? WorkoutDayId = null);

public static class RecurrenceProjection
{
    public static Guid OccurrenceKey(Guid seriesId, DateOnly originalDate)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"personalflow:{seriesId:D}:{originalDate:yyyy-MM-dd}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static IReadOnlyList<PlannedEntry> Expand(
        DateOnly from,
        DateOnly through,
        IEnumerable<Recurrence> recurrences,
        IEnumerable<Vacation> vacations,
        IEnumerable<OccurrenceOverride> overrides)
    {
        if (through < from) return [];

        var exclusionDates = vacations
            .Where(vacation => vacation.End >= vacation.Start)
            .Select(vacation => new DateExclusion(vacation.Start, vacation.End, vacation.Category))
            .ToArray();
        var overrideMap = overrides.ToDictionary(x => (x.SeriesId, x.OriginalDate));
        var result = new Dictionary<Guid, PlannedEntry>();

        foreach (var series in recurrences)
        {
            var rule = ToRule(series);
            foreach (var originalDate in rule.Expand(from, through, exclusionDates))
            {
                overrideMap.TryGetValue((series.Id, originalDate), out var occurrenceOverride);
                if (occurrenceOverride?.Skipped == true) continue;

                var date = occurrenceOverride?.MovedTo ?? originalDate;
                if (date < from || date > through) continue;
                var id = OccurrenceKey(series.Id, originalDate);
                result[id] = new PlannedEntry(id, series.Title, date, occurrenceOverride?.Done ?? false,
                    series.Time, series.Id, originalDate, series.WorkoutDayId);
            }

            // A moved occurrence can originate before the displayed range.
            foreach (var occurrenceOverride in overrides.Where(x => x.SeriesId == series.Id && x.MovedTo is { } date && date >= from && date <= through && (x.OriginalDate < from || x.OriginalDate > through)))
            {
                if (occurrenceOverride.Skipped || occurrenceOverride.MovedTo is not { } movedTo) continue;
                if (!rule.Expand(occurrenceOverride.OriginalDate, occurrenceOverride.OriginalDate, exclusionDates).Contains(occurrenceOverride.OriginalDate)) continue;
                var id = OccurrenceKey(series.Id, occurrenceOverride.OriginalDate);
                result[id] = new PlannedEntry(id, series.Title, movedTo, occurrenceOverride.Done ?? false,
                    series.Time, series.Id, occurrenceOverride.OriginalDate, series.WorkoutDayId);
            }
        }

        return result.Values.OrderBy(x => x.Date).ThenBy(x => x.Time ?? "99:99", StringComparer.Ordinal).ToArray();
    }

    public static RecurrenceRule ToRule(Recurrence recurrence) => new(
        recurrence.StartsOn,
        recurrence.Frequency,
        recurrence.Interval,
        recurrence.Weekdays.ToHashSet(),
        recurrence.MonthDays.ToHashSet(),
        recurrence.EndsOn,
        recurrence.ExclusionCategory);
}
