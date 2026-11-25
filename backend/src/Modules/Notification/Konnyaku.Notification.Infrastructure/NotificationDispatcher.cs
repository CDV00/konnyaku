using System.Diagnostics;
using Konnyaku.Infrastructure.Observability;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.Notification.Application;
using Konnyaku.Notification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Notification.Infrastructure;

/// <summary>
/// Runs due work of every tenant: deliveries (with retries) and timed jobs such as session reminders. Rows are claimed
/// with FOR UPDATE SKIP LOCKED so several workers can run; each job runs in its own scope with the row's tenant.
/// </summary>
public sealed class NotificationDispatcher(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<NotificationDispatcher> logger)
{
    private static readonly TimeSpan AfterCrash = TimeSpan.FromMinutes(5);

    public async Task<int> ProcessDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var items = await db.DispatchItems
            .FromSqlRaw("SELECT * FROM notification.dispatch_item WHERE due_at <= {0} ORDER BY due_at LIMIT {1} FOR UPDATE SKIP LOCKED", now, batchSize)
            .ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            // Root of the trace of one job (delivery with its SMTP call, reminder…): ids and kind only.
            using var activity = KonnyakuTelemetry.Source.StartActivity($"notification {item.Kind}", ActivityKind.Consumer);
            activity?.SetTag("konnyaku.dispatch.kind", item.Kind);
            activity?.SetTag("konnyaku.dispatch.ref_id", item.RefId.ToString());
            activity?.SetTag("konnyaku.tenant_id", item.TenantId.ToString());
            DateTimeOffset? next;
            try
            {
                next = await RunAsync(item, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification job {Kind} {RefId} failed", item.Kind, item.RefId);
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                next = clock.GetUtcNow() + AfterCrash;
            }

            if (next is { } dueAt)
            {
                item.DueAt = dueAt;
            }
            else
            {
                db.DispatchItems.Remove(item);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return items.Count;
    }

    /// <summary>Samples the queue by kind (rows, how late the most overdue one is) for the <c>konnyaku.notification.*</c> gauges.</summary>
    public async Task RecordQueueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var now = clock.GetUtcNow();
        var rows = await db.DispatchItems.AsNoTracking()
            .GroupBy(i => i.Kind)
            .Select(g => new { Kind = g.Key, Count = g.LongCount(), OldestDue = g.Min(i => i.DueAt) })
            .ToListAsync(cancellationToken);
        // Known kinds report 0 when empty, so dashboards show a line rather than "no data".
        var byKind = new Dictionary<string, (long, double)>
        {
            [DispatchKinds.Send] = (0, 0),
            [DispatchKinds.SessionReminder] = (0, 0),
            [DispatchKinds.LearnerWelcome] = (0, 0),
        };
        foreach (var row in rows)
        {
            byKind[row.Kind] = (row.Count, Math.Max(0, (now - row.OldestDue).TotalSeconds));
        }

        KonnyakuTelemetry.RecordNotificationQueue(byKind);
    }

    private async Task<DateTimeOffset?> RunAsync(DispatchItem item, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(item.TenantId);
        var job = scope.ServiceProvider.GetServices<IDispatchJob>().FirstOrDefault(j => j.Kind == item.Kind);
        if (job is null)
        {
            logger.LogWarning("No job handles dispatch kind {Kind}; dropping {RefId}", item.Kind, item.RefId);
            return null;
        }

        return await job.RunAsync(item.RefId, cancellationToken);
    }
}

public sealed class NotificationDispatchService(NotificationDispatcher dispatcher, ILogger<NotificationDispatchService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QueueInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextQueueSample = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextQueueSample)
                {
                    nextQueueSample = DateTimeOffset.UtcNow + QueueInterval;
                    await dispatcher.RecordQueueAsync(stoppingToken);
                }

                if (await dispatcher.ProcessDueAsync(20, stoppingToken) > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification dispatch loop failed");
            }

            await Task.Delay(IdleDelay, stoppingToken);
        }
    }
}
