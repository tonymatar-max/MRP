using CobaltMrp.Domain;

namespace CobaltMrp.Engine;

/// <summary>
/// Turns a raw net requirement into an order quantity according to the item's lot-sizing rule.
/// Min order qty, order multiple and max order qty are applied on top of every rule.
/// </summary>
public static class LotSizer
{
    /// <summary>
    /// Compute the order quantity for a single net requirement.
    /// PeriodOfSupply accumulation is handled by the caller (it aggregates buckets first);
    /// this method sizes the already-accumulated quantity.
    /// </summary>
    public static decimal Size(PlanItem item, decimal netRequirement)
    {
        if (netRequirement <= 0) return 0;

        decimal qty = item.LotSizing switch
        {
            LotSizingRule.FixedQuantity when item.FixedLotSize > 0
                => RoundUpToCover(netRequirement, item.FixedLotSize),
            _ => netRequirement, // LotForLot, PeriodOfSupply (pre-accumulated), MinMaxMultiple base
        };

        // Minimum order quantity.
        if (item.MinOrderQty > 0 && qty < item.MinOrderQty)
            qty = item.MinOrderQty;

        // Order multiple (round up).
        if (item.OrderMultiple > 0)
        {
            var multiples = Math.Ceiling(qty / item.OrderMultiple);
            qty = multiples * item.OrderMultiple;
        }

        // Maximum order quantity caps a single order; caller may need to split, but for
        // planning suggestions we cap and emit the remainder on the next pass/bucket.
        if (item.MaxOrderQty > 0 && qty > item.MaxOrderQty)
            qty = item.MaxOrderQty;

        return qty;
    }

    /// <summary>Round a requirement up to whole multiples of a fixed lot.</summary>
    private static decimal RoundUpToCover(decimal requirement, decimal lot)
        => Math.Ceiling(requirement / lot) * lot;
}
