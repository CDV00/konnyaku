using System.Reflection;
using System.Text.Json;
using Konnyaku.Application.Messaging;
using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Infrastructure.Messaging;

/// <summary>
/// Dispatches one module's outbox to in-process handlers. Rows are claimed with FOR UPDATE SKIP LOCKED so several
/// workers can run in parallel. Each handler runs in its own scope with the event's tenant as tenant context.
/// </summary>
public sealed class OutboxProcessor<TContext>(IServiceScopeFactory scopeFactory, ILogger<OutboxProcessor<TContext>> logger)
    where TContext : ModuleDbContext
{
    private static readonly MethodInfo DispatchMethod =
        typeof(OutboxProcessor<TContext>).GetMethod(nameof(DispatchAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var sql = "SELECT * FROM \"" + db.Schema + "\".outbox_message " +
                  "WHERE processed_at IS NULL AND attempts < {0} " +
                  "ORDER BY occurred_at LIMIT {1} FOR UPDATE SKIP LOCKED";
        var messages = await db.OutboxMessages
            .FromSqlRaw(sql, OutboxMessage.MaxAttempts, batchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                var type = Type.GetType(message.Type, throwOnError: true)!;
                var payload = (IIntegrationEvent)JsonSerializer.Deserialize(message.Payload, type, OutboxMessage.SerializerOptions)!;
                var context = new IntegrationEventContext(message.Id, message.TenantId, message.OccurredAt, message.CorrelationId);
                await (Task)DispatchMethod.MakeGenericMethod(type)
                    .Invoke(null, [scopeFactory, payload, context, cancellationToken])!;
                message.MarkProcessed(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                logger.LogError(error, "Outbox message {MessageId} ({Type}) failed", message.Id, message.Type);
                message.MarkFailed(error.ToString());
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    private static async Task DispatchAsync<TEvent>(
        IServiceScopeFactory scopeFactory, TEvent payload, IntegrationEventContext context, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(context.TenantId);
        foreach (var handler in scope.ServiceProvider.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            await handler.HandleAsync(payload, context, cancellationToken);
        }
    }
}

public sealed class OutboxBackgroundService<TContext>(OutboxProcessor<TContext> processor, ILogger<OutboxBackgroundService<TContext>> logger)
    : BackgroundService where TContext : ModuleDbContext
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await processor.ProcessBatchAsync(50, stoppingToken) > 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox loop for {Context} failed", typeof(TContext).Name);
            }

            await Task.Delay(IdleDelay, stoppingToken);
        }
    }
}

public static class OutboxServiceCollectionExtensions
{
    public static IServiceCollection AddOutboxProcessor<TContext>(this IServiceCollection services) where TContext : ModuleDbContext
    {
        services.AddSingleton<OutboxProcessor<TContext>>();
        services.AddHostedService<OutboxBackgroundService<TContext>>();
        return services;
    }
}
