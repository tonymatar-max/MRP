namespace CobaltMrp.Domain;

/// <summary>
/// A saved set of Run MRP wizard options, so the planner does not pick the same options every time.
/// The "plan as of" date is never stored: a template always plans from today.
/// </summary>
public sealed class RunTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string CriteriaJson { get; set; } = "{}";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
