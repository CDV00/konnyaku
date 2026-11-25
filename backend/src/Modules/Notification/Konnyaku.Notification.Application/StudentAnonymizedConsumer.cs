using Konnyaku.Application.Messaging;
using Konnyaku.Crm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Notification.Application;

/// <summary>A student's personal data was erased: the messages addressed to their account lose address, name and data.</summary>
public sealed class StudentAnonymizedEraser(INotificationDbContext db) : IIntegrationEventHandler<StudentAnonymizedV1>
{
    public async Task HandleAsync(StudentAnonymizedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (@event.UserId is not { } userId)
        {
            return;
        }

        var messages = await db.Messages.Where(m => m.RecipientUserId == userId && m.RecipientAddress != string.Empty).ToListAsync(cancellationToken);
        foreach (var message in messages)
        {
            message.ErasePersonalData();
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
