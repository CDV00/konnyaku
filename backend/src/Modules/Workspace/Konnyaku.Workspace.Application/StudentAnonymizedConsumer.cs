using Konnyaku.Application.Messaging;
using Konnyaku.Crm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Workspace.Application;

/// <summary>
/// A student's personal data was erased: the name recorded with their classroom connections goes too (the rows stay:
/// attendance was computed from them). Idempotent, so no inbox mark is needed.
/// </summary>
public sealed class StudentAnonymizedConsumer(IWorkspaceDbContext db) : IIntegrationEventHandler<StudentAnonymizedV1>
{
    public async Task HandleAsync(StudentAnonymizedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (@event.UserId is not { } userId)
        {
            return;
        }

        var name = "#" + userId.ToString("N")[^8..];
        await db.RoomPresences.Where(p => p.UserId == userId && p.DisplayName != name)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayName, name), cancellationToken);
    }
}
