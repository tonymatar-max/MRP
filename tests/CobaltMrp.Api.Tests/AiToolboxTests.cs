using System.Text.Json;
using System.Text.Json.Nodes;
using CobaltMrp.Api.Ai;
using CobaltMrp.Domain;
using Xunit;

namespace CobaltMrp.Api.Tests;

public class AiToolboxTests
{
    private static readonly DateOnly Start = new(2026, 10, 12);

    private static MrpRun BuildRun(int orders = 60)
    {
        var run = new MrpRun { HorizonStart = Start, HorizonEnd = Start.AddDays(180), BucketDays = 7, RunBy = "tester" };
        for (var i = 0; i < orders; i++)
            run.PlannedOrders.Add(new PlannedOrder
            {
                ItemCode = $"ITM-{i % 12:00}", WarehouseCode = i % 2 == 0 ? "WH01" : "WH02",
                FromWarehouse = i % 5 == 0 ? "WH03" : null,
                Type = (PlannedOrderType)(i % 4), Quantity = 100m + i + 0.123456m, DueDate = Start.AddDays(10 + i),
                ReleaseDate = Start.AddDays(3 + i), LeadTimeDays = 7, VendorCode = "V001",
            });
        var first = run.PlannedOrders.FirstOrDefault();
        if (first is not null) run.Pegging.Add(new PeggingLink { PlannedOrderId = first.Id, DemandSource = DemandSource.SalesOrder, Quantity = 40m, DemandDueDate = Start.AddDays(11), DemandDocKey = "ORDR:77" });
        if (first is not null) run.Pegging.Add(new PeggingLink { PlannedOrderId = first.Id, DemandSource = DemandSource.DependentDemand, Quantity = 60.123456m, DemandDueDate = Start.AddDays(12), ParentItemCode = "FG-1" });
        run.Exceptions.Add(new ExceptionMessage { Type = ExceptionType.Defer, ItemCode = "ITM-01", Message = "Defer PO", RelatedDate = Start.AddDays(5), Quantity = 5m });
        run.Exceptions.Add(new ExceptionMessage { Type = ExceptionType.ReleaseInPast, ItemCode = "ITM-02", Message = "Lead time too long", RelatedDate = Start.AddDays(9), Quantity = 50m, PlannedOrderId = first?.Id });
        run.Exceptions.Add(new ExceptionMessage { Type = ExceptionType.PastDue, ItemCode = "ITM-02", Message = "Past due", Quantity = 1m, ActionStatus = "Done" });
        run.Events.Add(new CalendarEvent { Date = Start.AddDays(1), Kind = CalendarEventKind.NonWorking, Label = "Weekend" });
        run.Events.Add(new CalendarEvent { Date = Start.AddDays(2), Kind = CalendarEventKind.Demand, ItemCode = "ITM-02", WarehouseCode = "WH01", Quantity = 30m, Label = "Sales order due" });
        run.Events.Add(new CalendarEvent { Date = Start.AddDays(4), Kind = CalendarEventKind.Demand, ItemCode = "ITM-02", WarehouseCode = "WH01", Quantity = 20.5m, Label = "Sales order due" });
        run.Events.Add(new CalendarEvent { Date = Start.AddDays(6), Kind = CalendarEventKind.Supply, ItemCode = "ITM-02", WarehouseCode = "WH01", Quantity = 10m, Label = "Purchase order due" });
        run.Events.Add(new CalendarEvent { Date = Start.AddDays(7), Kind = CalendarEventKind.Supply, ItemCode = "ITM-02", WarehouseCode = "WH01", Quantity = 8m, Label = "Production order due" });
        return run;
    }

    private static async Task<JsonObject> Call(RunToolbox t, string name, string args = "{}") =>
        (JsonObject)JsonNode.Parse(await t.ExecuteAsync(name, args, CancellationToken.None))!;

    [Fact]
    public void Tools_AreReadOnly_ByName()
    {
        Assert.Equal(6, RunToolbox.ToolNames.Count);
        Assert.All(RunToolbox.ToolNames, n => Assert.True(n.StartsWith("get_") || n.StartsWith("list_"), n));
        var schemaNames = RunToolbox.Schemas().Select(s => s!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(RunToolbox.ToolNames.OrderBy(x => x), schemaNames.OrderBy(x => x));
        foreach (var bad in new[] { "create", "release", "execute", "move", "edit", "delete", "update", "close", "write", "set_", "post", "patch" })
            Assert.DoesNotContain(schemaNames, n => n.Contains(bad, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Toolbox_HasNoWriteDependency()
    {
        // It is built from a run and a lookup delegate only: no store, settings, Service Layer or HTTP types.
        var ctors = typeof(RunToolbox).GetConstructors();
        var ctor = Assert.Single(ctors);
        Assert.Equal(new[] { typeof(MrpRun), typeof(ItemLookup) }, ctor.GetParameters().Select(p => p.ParameterType));
        var fields = typeof(RunToolbox).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.All(fields, f => Assert.True(f.FieldType == typeof(MrpRun) || f.FieldType == typeof(ItemLookup), f.FieldType.Name));
    }

    [Fact]
    public async Task EveryTool_LeavesTheRunUnchanged()
    {
        var run = BuildRun();
        string Snapshot() => JsonSerializer.Serialize(new { run.PlannedOrders, run.Pegging, run.Exceptions, run.Events, run.CapacityLoads, run.Notes });
        var before = Snapshot();
        var t = new RunToolbox(run, (c, _) => Task.FromResult<ItemMaster?>(null));
        foreach (var (n, a) in new[]
        {
            ("get_run_summary", "{}"), ("list_planned_orders", "{\"limit\":5}"), ("get_pegging", "{\"itemCode\":\"ITM-00\"}"),
            ("list_exceptions", "{}"), ("get_item", "{\"itemCode\":\"ITM-02\"}"), ("get_calendar_events", "{}"),
        }) await t.ExecuteAsync(n, a, CancellationToken.None);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ListPlannedOrders_CapsRows()
    {
        var t = new RunToolbox(BuildRun(60));
        var d = await Call(t, "list_planned_orders");
        Assert.Equal(60, d["total"]!.GetValue<int>());
        Assert.Equal(RunToolbox.DefaultRows, d["orders"]!.AsArray().Count);
        Assert.True(d["truncated"]!.GetValue<bool>());

        Assert.Equal(RunToolbox.MaxRows, (await Call(t, "list_planned_orders", "{\"limit\":1000}"))["orders"]!.AsArray().Count);
        Assert.Equal(5, (await Call(t, "list_planned_orders", "{\"limit\":5}"))["orders"]!.AsArray().Count);
        Assert.Equal(1, (await Call(t, "list_planned_orders", "{\"limit\":-3}"))["orders"]!.AsArray().Count);
        Assert.Equal(5, (await Call(t, "list_planned_orders", "{\"limit\":\"5\"}"))["orders"]!.AsArray().Count);
    }

    [Fact]
    public async Task ListPlannedOrders_Filters()
    {
        var t = new RunToolbox(BuildRun(60));
        var byItem = await Call(t, "list_planned_orders", "{\"item\":\"itm-03\",\"limit\":40}");
        Assert.All(byItem["orders"]!.AsArray(), o => Assert.Equal("ITM-03", o!["item"]!.GetValue<string>()));
        Assert.Equal(5, byItem["total"]!.GetValue<int>());

        var byType = await Call(t, "list_planned_orders", "{\"type\":\"TR\",\"limit\":40}");
        Assert.Equal(15, byType["total"]!.GetValue<int>());
        Assert.All(byType["orders"]!.AsArray(), o => Assert.Equal("TransferRequest", o!["type"]!.GetValue<string>()));

        // The sending warehouse of a transfer also matches the warehouse filter.
        var wh = await Call(t, "list_planned_orders", "{\"warehouse\":\"WH03\",\"limit\":40}");
        Assert.Equal(12, wh["total"]!.GetValue<int>());

        Assert.Contains("Unknown type", (await Call(t, "list_planned_orders", "{\"type\":\"Banana\"}"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Numbers_AreRounded_AndIntegersStayIntegers()
    {
        var run = new MrpRun { HorizonStart = Start, HorizonEnd = Start.AddDays(30) };
        run.PlannedOrders.Add(new PlannedOrder { ItemCode = "A", WarehouseCode = "W", Quantity = 100.000000m, DueDate = Start, ReleaseDate = Start });
        run.PlannedOrders.Add(new PlannedOrder { ItemCode = "B", WarehouseCode = "W", Quantity = 12.3456789m, DueDate = Start, ReleaseDate = Start });
        var text = await new RunToolbox(run).ExecuteAsync("list_planned_orders", "{}", CancellationToken.None);
        Assert.Contains("\"qty\":100,", text);
        Assert.Contains("\"qty\":12.346,", text);
        Assert.DoesNotContain("100.0", text);
    }

    [Fact]
    public async Task Pegging_ByOrderId_AndByItem()
    {
        var run = BuildRun();
        var t = new RunToolbox(run);
        var id = run.PlannedOrders[0].Id.ToString();
        var d = await Call(t, "get_pegging", $"{{\"plannedOrderId\":\"{id}\"}}");
        Assert.Equal(2, d["linkCount"]!.GetValue<int>());
        Assert.Equal(100.123, d["peggedQty"]!.GetValue<double>(), 3);
        Assert.Equal("ORDR:77", d["links"]![0]!["document"]!.GetValue<string>());
        Assert.Equal("FG-1", d["links"]![1]!["parentItem"]!.GetValue<string>());

        var byItem = await Call(t, "get_pegging", "{\"itemCode\":\"ITM-00\"}");
        Assert.Equal(5, byItem["orderCount"]!.GetValue<int>());
        Assert.True(byItem["orders"]!.AsArray().Count <= 8);

        Assert.Contains("GUID", (await Call(t, "get_pegging", "{\"plannedOrderId\":\"nope\"}"))["error"]!.GetValue<string>());
        Assert.Contains("Give", (await Call(t, "get_pegging"))["error"]!.GetValue<string>());
        Assert.Contains("No planned orders", (await Call(t, "get_pegging", "{\"itemCode\":\"ZZZ\"}"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Exceptions_MostUrgentFirst_StatusFilter()
    {
        var t = new RunToolbox(BuildRun());
        var all = await Call(t, "list_exceptions");
        Assert.Equal(3, all["total"]!.GetValue<int>());
        Assert.Equal("ReleaseInPast", all["exceptions"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("PastDue", all["exceptions"]![1]!["type"]!.GetValue<string>());

        var open = await Call(t, "list_exceptions", "{\"status\":\"open\"}");
        Assert.Equal(2, open["total"]!.GetValue<int>());
        Assert.Equal(1, (await Call(t, "list_exceptions", "{\"status\":\"done\"}"))["total"]!.GetValue<int>());
        Assert.Equal(1, (await Call(t, "list_exceptions", "{\"type\":\"defer\"}"))["total"]!.GetValue<int>());
        Assert.Contains("Unknown type", (await Call(t, "list_exceptions", "{\"type\":\"x\"}"))["error"]!.GetValue<string>());
        Assert.Contains("status", (await Call(t, "list_exceptions", "{\"status\":\"x\"}"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetItem_CombinesMasterDataAndRunFacts()
    {
        var master = new ItemMaster("ITM-02", "Widget", "Parts", "Buy", "V001", "Acme", 7, "Supplier", "Mrp", "LotForLot", 10m, 5m, 20m, 0m, 0m, 0m, new[] { "leadTimeDays" });
        var t = new RunToolbox(BuildRun(), (c, _) => Task.FromResult<ItemMaster?>(string.Equals(c, "ITM-02", StringComparison.OrdinalIgnoreCase) ? master : null));
        var d = await Call(t, "get_item", "{\"itemCode\":\"itm-02\"}");
        Assert.Equal("Acme", d["master"]!["supplierName"]!.GetValue<string>());
        Assert.Equal(50.5, d["inRun"]!["salesOrderDemand"]!["qty"]!.GetValue<double>(), 3);
        Assert.Equal(2, d["inRun"]!["salesOrderDemand"]!["count"]!.GetValue<int>());
        Assert.Equal(10, d["inRun"]!["openPurchaseOrders"]!["qty"]!.GetValue<int>());
        Assert.Equal(8, d["inRun"]!["openProductionOrders"]!["qty"]!.GetValue<int>());
        Assert.Equal(2, d["inRun"]!["exceptions"]!["count"]!.GetValue<int>());
        Assert.Contains("On-hand stock is not stored", d["inRun"]!["scope"]!.GetValue<string>());

        // Without a master lookup the run facts are still returned, with a note saying so.
        var noMaster = await Call(new RunToolbox(BuildRun()), "get_item", "{\"itemCode\":\"ITM-02\"}");
        Assert.Null(noMaster["master"]);
        Assert.Contains("not available", noMaster["masterNote"]!.GetValue<string>());

        Assert.Contains("not found", (await Call(t, "get_item", "{\"itemCode\":\"NOPE\"}"))["error"]!.GetValue<string>());
        Assert.Contains("required", (await Call(t, "get_item"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetItem_MasterFailure_IsReportedNotThrown()
    {
        var t = new RunToolbox(BuildRun(), (_, _) => throw new InvalidOperationException("secret connection string xyz"));
        var text = await t.ExecuteAsync("get_item", "{\"itemCode\":\"ITM-02\"}", CancellationToken.None);
        Assert.Contains("could not be read", text);
        Assert.DoesNotContain("xyz", text);
    }

    [Fact]
    public async Task CalendarEvents_ExcludeNonWorking_ValidateDates_Cap()
    {
        var t = new RunToolbox(BuildRun());
        var d = await Call(t, "get_calendar_events", "{\"item\":\"ITM-02\"}");
        Assert.Equal(4, d["total"]!.GetValue<int>());
        Assert.DoesNotContain(d["events"]!.AsArray(), e => e!["kind"]!.GetValue<string>() == "NonWorking");
        Assert.Equal("2026-10-14", d["events"]![0]!["date"]!.GetValue<string>());

        var win = await Call(t, "get_calendar_events", "{\"item\":\"ITM-02\",\"from\":\"2026-10-16\",\"to\":\"2026-10-17\"}");
        Assert.Equal(1, win["total"]!.GetValue<int>());
        Assert.Contains("yyyy-MM-dd", (await Call(t, "get_calendar_events", "{\"from\":\"soon\"}"))["error"]!.GetValue<string>());
        Assert.Contains("at most", (await Call(t, "get_calendar_events", "{\"from\":\"2026-01-01\",\"to\":\"2028-01-01\"}"))["error"]!.GetValue<string>());
        Assert.Contains("Unknown kind", (await Call(t, "get_calendar_events", "{\"kinds\":\"Nope\"}"))["error"]!.GetValue<string>());
        Assert.Contains("Unknown kind", (await Call(t, "get_calendar_events", "{\"kinds\":\"NonWorking\"}"))["error"]!.GetValue<string>());

        var big = BuildRun(0);
        for (var i = 0; i < 200; i++) big.Events.Add(new CalendarEvent { Date = Start.AddDays(i % 100), Kind = CalendarEventKind.Demand, ItemCode = "X", Quantity = 1, Label = "Sales order due" });
        var capped = await Call(new RunToolbox(big), "get_calendar_events", "{\"limit\":999}");
        Assert.Equal(RunToolbox.MaxRows, capped["events"]!.AsArray().Count);
        Assert.Equal(204, capped["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunSummary_HasCountsAndNoSecrets()
    {
        var run = BuildRun();
        run.CriteriaJson = new RunCriteria { Source = "b1-sql", Level = PlanningLevel.Company }.ToJson();
        var d = await Call(new RunToolbox(run), "get_run_summary");
        Assert.Equal(60, d["plannedOrders"]!["total"]!.GetValue<int>());
        Assert.Equal(15, d["plannedOrders"]!["byType"]!["TransferRequest"]!.GetValue<int>());
        Assert.Equal(2, d["exceptions"]!["open"]!.GetValue<int>());
        Assert.Equal("Company", d["criteria"]!["planningLevel"]!.GetValue<string>());
        Assert.Equal(60 - 0, d["plannedOrders"]!["total"]!.GetValue<int>());
        Assert.True(d["plannedOrders"]!["releaseDateBeforeRunStart"]!.GetValue<int>() >= 0);
    }

    [Fact]
    public async Task BadInput_ReturnsErrorJson_NeverThrows()
    {
        var t = new RunToolbox(BuildRun());
        Assert.Contains("not valid JSON", (await Call(t, "list_planned_orders", "{oops")) ["error"]!.GetValue<string>());
        Assert.Contains("Unknown tool", (await Call(t, "delete_everything"))["error"]!.GetValue<string>());
        // A non-object argument is treated as no arguments.
        Assert.Equal(60, (await Call(t, "list_planned_orders", "[1,2]"))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task OversizedResult_IsRefusedWithAHint()
    {
        var run = BuildRun(0);
        run.Exceptions.AddRange(Enumerable.Range(0, 40).Select(i => new ExceptionMessage
        { Type = ExceptionType.PastDue, ItemCode = "I" + i, Message = new string('x', 400), Quantity = 1 }));
        var text = await new RunToolbox(run).ExecuteAsync("list_exceptions", "{\"limit\":40}", CancellationToken.None);
        Assert.True(text.Length <= RunToolbox.MaxResultChars);
    }

    [Fact]
    public void Describe_IsShort()
    {
        Assert.Equal("item=TUB-01, limit=10", RunToolbox.Describe("{\"item\":\"TUB-01\",\"limit\":10}"));
        Assert.Equal("", RunToolbox.Describe("{}"));
        Assert.Equal("", RunToolbox.Describe("nonsense"));
        Assert.True(RunToolbox.Describe("{\"item\":\"" + new string('a', 500) + "\"}").Length <= 125);
    }
}
