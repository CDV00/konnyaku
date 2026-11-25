using Konnyaku.Crm.Application;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Crm.Infrastructure;

/// <summary>
/// Worker job: once a day, tenant by tenant (each in its own scope and tenant context, so RLS applies), anonymizes the
/// leads past the retention period. Logs counts only, never names or contact details.
/// </summary>
public sealed class LeadRetentionService(IServiceScopeFactory scopeFactory, ILogger<LeadRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the other workers start first; then run daily.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lead retention run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<IPlatformApi>().ListTenantIdsAsync(cancellationToken);
        }

        var total = 0;
        foreach (var tenantId in tenants)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
            var count = await scope.ServiceProvider.GetRequiredService<LeadRetention>().RunAsync(cancellationToken);
            if (count > 0)
            {
                logger.LogInformation("Lead retention anonymized {Count} leads of tenant {TenantId}", count, tenantId);
            }

            total += count;
        }

        return total;
    }
}
