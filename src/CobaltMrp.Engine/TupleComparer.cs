namespace CobaltMrp.Engine;

/// <summary>Case-insensitive equality for (item, warehouse) dictionary keys.</summary>
internal sealed class TupleComparer : IEqualityComparer<(string, string)>
{
    public static readonly TupleComparer Instance = new();

    public bool Equals((string, string) x, (string, string) y) =>
        string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string, string) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
}
