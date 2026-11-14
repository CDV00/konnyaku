using Konnyaku.Application.Messaging;
using Konnyaku.Learning.Contracts;
using Konnyaku.Workspace.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Workspace.Application;

/// <summary>Applies the viewers decided by Learning on the meeting platform (Drive permissions for Google).</summary>
public sealed class RecordingAccessGrantedConsumer(IWorkspaceDbContext db, IVirtualRoomProviders providers) : IIntegrationEventHandler<RecordingAccessGrantedV1>
{
    public async Task HandleAsync(RecordingAccessGrantedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        var recording = await db.Recordings.FirstOrDefaultAsync(r => r.Id == @event.RecordingId, cancellationToken);
        var connection = await db.Connections.FirstOrDefaultAsync(cancellationToken);
        if (recording is null || connection is null || providers.Find(connection.Provider) is not { } provider)
        {
            return;
        }

        await provider.GrantRecordingAccessAsync(recording.FileRef, @event.ViewerUserIds, cancellationToken);
        recording.Publish(@event.ViewerUserIds);
        db.Publish(new RecordingPublishedV1(recording.SessionId, recording.Id, recording.ViewerUserIds.Count));
        await db.SaveChangesAsync(cancellationToken);
    }
}
