namespace CobaltMrp.Domain;

/// <summary>What was sent to SAP B1.</summary>
public static class SapPushActions
{
    public const string CreatePurchaseOrder = "CreatePurchaseOrder";
    public const string CreatePurchaseRequest = "CreatePurchaseRequest";
    public const string CreateTransferRequest = "CreateTransferRequest";
    public const string CreateProductionOrder = "CreateProductionOrder";
    public const string MoveDueDate = "MoveDueDate";
    public const string CloseDocument = "CloseDocument";
    /// <summary>The planner pushed Cobalt master-data overrides to the SAP B1 item master.</summary>
    public const string UpdateItem = "UpdateItem";
    /// <summary>The Service Layer session itself failed (login, network), so no document was attempted.</summary>
    public const string Session = "Session";

    public static string ForCreate(PlannedOrderType t) => t switch
    {
        PlannedOrderType.PurchaseOrder => CreatePurchaseOrder,
        PlannedOrderType.PurchaseRequest => CreatePurchaseRequest,
        PlannedOrderType.TransferRequest => CreateTransferRequest,
        _ => CreateProductionOrder,
    };
}

/// <summary>
/// One attempt to write to SAP B1 through the Service Layer, kept for good whether it worked or not:
/// who did it, when, what was sent, and what SAP B1 answered. Never holds credentials or the raw request body.
/// </summary>
public sealed class SapPushLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string UserName { get; set; } = "";
    /// <summary>One of <see cref="SapPushActions"/>.</summary>
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public Guid? RunId { get; set; }
    public Guid? PlannedOrderId { get; set; }
    public Guid? ExceptionId { get; set; }
    public string? ItemCode { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal? Quantity { get; set; }
    /// <summary>The SAP B1 document, for example "OPOR:123" (purchase order 123); for a created document, the new DocEntry.</summary>
    public string? Target { get; set; }
    /// <summary>A short plain-language description of what was sent.</summary>
    public string Summary { get; set; } = "";
    /// <summary>SAP B1's complete error message when it failed.</summary>
    public string? Error { get; set; }
}

/// <summary>Filters for reading the log. Every field is optional.</summary>
public sealed record SapPushLogQuery(
    bool? Success = null, string? Action = null, string? Item = null, string? User = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 200);
