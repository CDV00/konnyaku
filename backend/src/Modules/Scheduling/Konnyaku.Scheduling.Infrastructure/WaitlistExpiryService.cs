using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Platform.Contracts;
using Konnyaku.Scheduling.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Scheduling.Infrastructure;

/// <summary>
/// Worker job: every few minutes, tenant by tenant (own scope and tenant context, so RLS applies), seats held for a
/// waitlisted learner past their hold go back and pass to the next learner in line.
/// </summary>
public sealed class WaitlistExpiryService(IServiceScopeFactory scopeFactory, ILogger<WaitlistExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
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
                logger.LogError(ex, "Waitlist expiry run failed");
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
            var count = await scope.ServiceProvider.GetRequiredService<WaitlistOffers>().ExpireDueAsync(cancellationToken);
            if (count > 0)
            {
                logger.LogInformation("Waitlist expired {Count} seat offers of tenant {TenantId}", count, tenantId);
            }

            total += count;
        }

        return total;
    }
}
