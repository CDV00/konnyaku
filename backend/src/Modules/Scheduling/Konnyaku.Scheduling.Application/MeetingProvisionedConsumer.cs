using Konnyaku.Application.Messaging;
using Konnyaku.Workspace.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Scheduling.Application;

/// <summary>A meeting room is ready: the session moves to Provisioned (idempotent by state).</summary>
public sealed class MeetingProvisionedConsumer(ISchedulingDbContext db) : IIntegrationEventHandler<MeetingProvisionedV1>
{
    public async Task HandleAsync(MeetingProvisionedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == @event.SessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.MarkProvisioned();
        await db.SaveChangesAsync(cancellationToken);
    }
}
