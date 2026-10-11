using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Assigns each item a low-level code = the deepest position it occupies in any BOM.
/// MRP must net an item only after every parent that consumes it has been processed,
/// so items are planned in ascending low-level-code order. Also detects BOM cycles.
/// </summary>
public static class LowLevelCoder
{
    public static void Assign(IReadOnlyList<PlanItem> items, IReadOnlyList<BomComponent> bom)
    {
        var byCode = items.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
        var children = bom
            .GroupBy(b => b.ParentItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ComponentItemCode).ToArray(),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var i in items) i.LowLevelCode = 0;

        // Depth-first from every parent, pushing children deeper. Cycle-guarded.
        foreach (var item in items)
        {
            var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Visit(item.ItemCode, 0, byCode, children, path);
        }
    }

    private static void Visit(
        string code, int depth,
        Dictionary<string, PlanItem> byCode,
        Dictionary<string, string[]> children,
        HashSet<string> path)
    {
        if (byCode.TryGetValue(code, out var item) && depth > item.LowLevelCode)
            item.LowLevelCode = depth;

        if (!children.TryGetValue(code, out var kids)) return;
        if (!path.Add(code))
            throw new InvalidOperationException($"BOM cycle detected at item '{code}'.");

        foreach (var child in kids)
            Visit(child, depth + 1, byCode, children, path);

        path.Remove(code);
    }
}
