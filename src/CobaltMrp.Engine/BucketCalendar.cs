namespace CobaltMrp.Engine;

/// <summary>
/// A time-phased calendar of equal-length buckets spanning the planning horizon.
/// Maps any date to a bucket index; dates before the horizon fold into bucket 0
/// (and are flagged past-due by the engine); dates after the horizon are out of range.
/// </summary>
public sealed class BucketCalendar
{
    private readonly DateOnly _start;
    private readonly int _bucketDays;

    public int Count { get; }
    public DateOnly Start => _start;
    public DateOnly End { get; }

    public BucketCalendar(DateOnly start, int horizonDays, int bucketDays)
    {
        if (bucketDays < 1) bucketDays = 1;
        if (horizonDays < bucketDays) horizonDays = bucketDays;
        _start = start;
        _bucketDays = bucketDays;
        Count = (int)Math.Ceiling(horizonDays / (double)bucketDays);
        End = start.AddDays(horizonDays);
    }

    /// <summary>Bucket index for a date, or -1 if past the horizon end.</summary>
    public int IndexOf(DateOnly date)
    {
        if (date < _start) return 0;          // past-due folds into the first bucket
        if (date >= End) return -1;           // beyond the horizon
        var days = date.DayNumber - _start.DayNumber;
        return days / _bucketDays;
    }

    /// <summary>The first date of a bucket.</summary>
    public DateOnly StartOf(int index) => _start.AddDays(index * _bucketDays);
}
