namespace CobaltMrp.Domain;

/// <summary>
/// Which days a plant works: weekly non-working days plus individual closures (holidays).
/// Used to size capacity per bucket and to keep recommended release dates off non-working days.
/// An empty calendar (<see cref="AllDays"/>) treats every day as a working day.
/// </summary>
public sealed class WorkCalendar
{
    private readonly HashSet<DayOfWeek> _nonWorkingWeekdays;
    private readonly Dictionary<DateOnly, string> _closures;

    public WorkCalendar(IEnumerable<DayOfWeek>? nonWorkingWeekdays = null,
                        IEnumerable<KeyValuePair<DateOnly, string>>? closures = null)
    {
        _nonWorkingWeekdays = new HashSet<DayOfWeek>(nonWorkingWeekdays ?? Array.Empty<DayOfWeek>());
        _closures = new Dictionary<DateOnly, string>();
        foreach (var c in closures ?? Array.Empty<KeyValuePair<DateOnly, string>>())
            _closures[c.Key] = c.Value;
    }

    public static WorkCalendar AllDays { get; } = new();

    public IReadOnlyCollection<DayOfWeek> NonWorkingWeekdays => _nonWorkingWeekdays;
    public IReadOnlyDictionary<DateOnly, string> Closures => _closures;

    public bool IsWorkingDay(DateOnly d) => NonWorkingReason(d) is null;

    /// <summary>Why a day is not worked, or null when it is a working day.</summary>
    public string? NonWorkingReason(DateOnly d)
    {
        if (_closures.TryGetValue(d, out var why)) return string.IsNullOrWhiteSpace(why) ? "Closed" : why;
        if (_nonWorkingWeekdays.Contains(d.DayOfWeek))
            return d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Weekend" : "Non-working day";
        return null;
    }

    /// <summary>Number of working days in <paramref name="days"/> consecutive days starting at <paramref name="start"/>.</summary>
    public int WorkingDays(DateOnly start, int days)
    {
        if (_nonWorkingWeekdays.Count == 0 && _closures.Count == 0) return Math.Max(days, 0);
        var n = 0;
        for (var i = 0; i < days; i++) if (IsWorkingDay(start.AddDays(i))) n++;
        return n;
    }

    /// <summary>The given day if it is a working day, otherwise the closest earlier working day.
    /// Gives up after 31 days (a calendar with no working days) and returns the input.</summary>
    public DateOnly OnOrBeforeWorkingDay(DateOnly d)
    {
        var x = d;
        for (var i = 0; i < 31; i++, x = x.AddDays(-1)) if (IsWorkingDay(x)) return x;
        return d;
    }

    /// <summary>Merge: closures and weekly days of both calendars apply.</summary>
    public WorkCalendar Union(WorkCalendar other) => new(
        _nonWorkingWeekdays.Union(other._nonWorkingWeekdays),
        _closures.Concat(other._closures.Where(c => !_closures.ContainsKey(c.Key))));
}
