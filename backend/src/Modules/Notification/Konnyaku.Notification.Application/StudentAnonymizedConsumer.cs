using Konnyaku.Application.Messaging;
using Konnyaku.Crm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Notification.Application;

/// <summary>
/// A student's personal data was erased: the messages addressed to their account lose address, name and data; their
/// inbox items lose what they said; their channel preferences and push subscriptions (browsers) go.
/// </summary>
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

        foreach (var item in await db.InboxItems.Where(i => i.UserId == userId).ToListAsync(cancellationToken))
        {
            item.ErasePersonalData();
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.PushSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Preferences.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }
}
