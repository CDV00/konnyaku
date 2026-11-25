using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Konnyaku.Application.Messaging;
using Konnyaku.Infrastructure.Observability;
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
            var eventName = EventName(message.Type);
            var tags = new TagList { { "module", db.Schema }, { "event", eventName } };
            // One span per event, child of the request that published it; the handlers' queries and calls nest below.
            using var activity = KonnyakuTelemetry.Source.StartActivity(
                $"outbox {eventName}", ActivityKind.Consumer, KonnyakuTelemetry.ParentFrom(message.CorrelationId));
            activity?.SetTag("messaging.system", "konnyaku.outbox");
            activity?.SetTag("messaging.operation.type", "process");
            activity?.SetTag("messaging.destination.name", db.Schema);
            activity?.SetTag("messaging.message.id", message.Id.ToString());
            activity?.SetTag("konnyaku.event", eventName);
            activity?.SetTag("konnyaku.tenant_id", message.TenantId.ToString());
            activity?.SetTag("konnyaku.attempt", message.Attempts + 1);
            KonnyakuTelemetry.OutboxLag.Record((DateTimeOffset.UtcNow - message.OccurredAt).TotalSeconds, tags);
            try
            {
                var type = Type.GetType(message.Type, throwOnError: true)!;
                var payload = (IIntegrationEvent)JsonSerializer.Deserialize(message.Payload, type, OutboxMessage.SerializerOptions)!;
                var context = new IntegrationEventContext(message.Id, message.TenantId, message.OccurredAt, message.CorrelationId);
                await (Task)DispatchMethod.MakeGenericMethod(type)
                    .Invoke(null, [scopeFactory, payload, context, cancellationToken])!;
                message.MarkProcessed(DateTimeOffset.UtcNow);
                tags.Add("result", "ok");
            }
            catch (Exception ex)
            {
                var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                logger.LogError(error, "Outbox message {MessageId} ({Type}) failed", message.Id, message.Type);
                message.MarkFailed(error.ToString());
                // The exception type only: messages may quote data (Sentry and the outbox row keep the details).
                activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
                tags.Add("result", "error");
            }

            KonnyakuTelemetry.OutboxProcessed.Add(1, tags);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    /// <summary>Samples the backlog (waiting, dead, age of the oldest) for the <c>konnyaku.outbox.*</c> gauges.</summary>
    public async Task RecordBacklogAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT count(*) FILTER (WHERE attempts < @max), count(*) FILTER (WHERE attempts >= @max), " +
                "coalesce(extract(epoch FROM now() - min(occurred_at) FILTER (WHERE attempts < @max)), 0)::float8 " +
                "FROM \"" + db.Schema + "\".outbox_message WHERE processed_at IS NULL";
            var max = command.CreateParameter();
            max.ParameterName = "max";
            max.Value = OutboxMessage.MaxAttempts;
            command.Parameters.Add(max);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                KonnyakuTelemetry.RecordOutbox(db.Schema, reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>"Konnyaku.Crm.Contracts.LeadCreatedV1, Konnyaku.Crm.Contracts" → "LeadCreatedV1".</summary>
    public static string EventName(string assemblyQualifiedType)
    {
        var fullName = assemblyQualifiedType.Split(',')[0];
        return fullName[(fullName.LastIndexOf('.') + 1)..];
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
    private static readonly TimeSpan BacklogInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextBacklog = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextBacklog)
                {
                    nextBacklog = DateTimeOffset.UtcNow + BacklogInterval;
                    await processor.RecordBacklogAsync(stoppingToken);
                }

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
