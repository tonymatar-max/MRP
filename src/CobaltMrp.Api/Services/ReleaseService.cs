using CobaltMrp.Api.Settings;
using CobaltMrp.Data.ServiceLayer;
using CobaltMrp.Domain;

namespace CobaltMrp.Api.Services;

public sealed record ReleaseFailure(Guid? PlannedOrderId, string Error);
public sealed record ReleaseOutcome(bool WritebackEnabled, int Pushed, List<ReleaseFailure> Failures);

/// <summary>
/// Firms planned orders and, when Service Layer write-back is enabled, creates them in SAP B1 one by one.
/// Shared by "Release" on Planned Orders and by "Execute" on a release-in-past exception, so both behave the same.
/// </summary>
public sealed class ReleaseService
{
    private readonly SettingsService _settings;
    private readonly ILogger<ReleaseService> _log;
    public ReleaseService(SettingsService settings, ILogger<ReleaseService> log) { _settings = settings; _log = log; }

    public async Task<ReleaseOutcome> ReleaseAsync(IRunStore store, IReadOnlyList<PlannedOrder> selected, CancellationToken ct)
    {
        foreach (var o in selected) o.Firmed = true;
        await store.SaveChangesAsync(ct);

        var sl = _settings.Get().ServiceLayer;
        var enabled = sl.Enabled && sl.IsConfigured;
        var failures = new List<ReleaseFailure>();
        var pushed = 0;
        if (!enabled) return new ReleaseOutcome(false, 0, failures);

        using var client = new ServiceLayerClient(ServiceLayerSession.Options(sl));
        try
        {
            await client.LoginAsync(ct);
            var summary = await new PlannedOrderWriteback(client, o => VendorResolver.Resolve(o, sl).Code).PushAsync(selected, ct);
            pushed = summary.Succeeded.Count();
            failures.AddRange(summary.Failed.Select(f => new ReleaseFailure(f.PlannedOrderId, f.Error ?? "Unknown error")));
            // Keep SAP B1's complete message: the UI shows it once, the Event Log keeps it for good.
            foreach (var f in summary.Failed)
            {
                var o = selected.FirstOrDefault(x => x.Id == f.PlannedOrderId);
                _log.LogWarning("Release to SAP B1 failed for {Item} ({Type}, qty {Qty}, planned order {Id}): {Error}",
                    o?.ItemCode, o?.Type, o?.Quantity, f.PlannedOrderId, f.Error);
            }
            await store.SaveChangesAsync(ct);   // persists B1DocEntry for the ones that went through
            await client.LogoutAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Service Layer write-back failed");
            failures.Add(new ReleaseFailure(null, $"Service Layer: {ex.Message}"));
        }
        return new ReleaseOutcome(true, pushed, failures);
    }
}

public static class ServiceLayerSession
{
    public static ServiceLayerOptions Options(ServiceLayerSettings sl) => new()
    {
        BaseUrl = sl.BaseUrl, CompanyDb = sl.CompanyDb, UserName = sl.UserName,
        Password = sl.Password, AllowInvalidCertificates = sl.AllowInvalidCertificates,
    };
}
