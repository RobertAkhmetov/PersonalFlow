namespace PersonalFlow.Core;

public enum RecurrenceFrequency { Daily, Weekly, Monthly }

public sealed record DateExclusion(DateOnly Start, DateOnly End, string Category)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

public sealed record RecurrenceRule(
    DateOnly StartsOn,
    RecurrenceFrequency Frequency,
    int Interval,
    IReadOnlySet<DayOfWeek> Weekdays,
    IReadOnlySet<int> MonthDays,
    DateOnly? EndsOn = null,
    string? ExclusionCategory = null)
{
    public void Validate()
    {
        if (Interval < 1 || !Enum.IsDefined(Frequency))
            throw new ArgumentException("Invalid recurrence frequency or interval.");
        if (EndsOn < StartsOn)
            throw new ArgumentException("End date precedes start date.");
        if (Weekdays.Any(day => !Enum.IsDefined(day)) ||
            MonthDays.Any(day => day is < 1 or > 31))
            throw new ArgumentException("Invalid recurrence day.");
        if (Frequency == RecurrenceFrequency.Weekly && Weekdays.Count == 0 ||
            Frequency == RecurrenceFrequency.Monthly && MonthDays.Count == 0)
            throw new ArgumentException("Select at least one recurrence day.");
    }

    public IReadOnlyList<DateOnly> Expand(
        DateOnly from, DateOnly through, IEnumerable<DateExclusion> exclusions)
    {
        Validate();
        if (through < from) throw new ArgumentException("Invalid date window.");
        if (through.DayNumber - from.DayNumber > 3660)
            throw new ArgumentException("Date window exceeds ten years.");

        var periods = exclusions.ToArray();
        if (periods.Any(period => period.End < period.Start))
            throw new ArgumentException("Invalid exclusion period.");
        var result = new List<DateOnly>();
        for (var number = from.DayNumber; number <= through.DayNumber; number++)
        {
            var date = DateOnly.FromDayNumber(number);
            if (date < StartsOn || EndsOn is { } end && date > end) continue;
            var matches = Frequency switch
            {
                RecurrenceFrequency.Daily =>
                    (number - StartsOn.DayNumber) % Interval == 0,
                RecurrenceFrequency.Weekly =>
                    (Monday(number, date.DayOfWeek) -
                     Monday(StartsOn.DayNumber, StartsOn.DayOfWeek)) / 7 % Interval == 0
                    && Weekdays.Contains(date.DayOfWeek),
                RecurrenceFrequency.Monthly =>
                    ((date.Year - StartsOn.Year) * 12 + date.Month - StartsOn.Month)
                        % Interval == 0 && MonthDays.Contains(date.Day),
                _ => false
            };
            if (matches && !periods.Any(period =>
                    period.Category == ExclusionCategory && period.Contains(date)))
                result.Add(date);
        }
        return result;
    }

    private static int Monday(int number, DayOfWeek weekday) =>
        number - ((int)weekday + 6) % 7;
}

public readonly record struct OccurrenceId(Guid SeriesId, DateOnly OriginalDate);

public sealed record TaskOccurrence(
    OccurrenceId Id, DateOnly ScheduledDate, TimeOnly? Time,
    string Title, bool Completed, long Revision)
{
    public TaskOccurrence Move(DateOnly date) => this with { ScheduledDate = date };
}
