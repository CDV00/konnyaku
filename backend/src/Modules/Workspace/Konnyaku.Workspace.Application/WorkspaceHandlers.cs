using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Application.Persistence;
using Konnyaku.Crm.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.SharedKernel;
using Konnyaku.Workspace.Contracts;
using Konnyaku.Workspace.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Konnyaku.Workspace.Application;

public interface IWorkspaceDbContext
{
    DbSet<WorkspaceConnection> Connections { get; }
    DbSet<HostAccount> Hosts { get; }
    DbSet<HostLease> Leases { get; }
    DbSet<Recording> Recordings { get; }
    DbSet<RoomPresence> RoomPresences { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record CreatedSpace(string SpaceName, string MeetingUri);

/// <summary>A video-meeting platform. Google Meet in production, a fake one in development and CI.</summary>
public interface IVirtualRoomProvider
{
    string Name { get; }

    /// <summary>False when rooms need no licensed organizer account, so sessions do not take a host from the pool.</summary>
    bool UsesHostPool => true;

    Task<CreatedSpace> CreateSpaceAsync(string? hostEmail, Guid sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken);

    Task GrantRecordingAccessAsync(string fileRef, IReadOnlyCollection<Guid> viewerUserIds, CancellationToken cancellationToken);
}

public interface IVirtualRoomProviders
{
    IVirtualRoomProvider? Find(string providerName);
}

/// <summary>
/// Picks a free host for a session: best fit first (the host whose previous lease ends closest before), so free hosts
/// stay available for last-minute classes. The exclusion constraint settles races between parallel workers.
/// </summary>
public sealed class HostAllocator(IWorkspaceDbContext db, IDbErrorClassifier errors)
{
    public async Task<HostLease?> AcquireAsync(Guid sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct)
    {
        var windowStart = startsAt - HostLease.Padding;
        var windowEnd = endsAt + HostLease.Padding;
        var hosts = await db.Hosts.Where(h => h.Status == HostStatus.Active).ToListAsync(ct);
        var nearby = await db.Leases
            .Where(l => (l.Status == LeaseStatus.Reserved || l.Status == LeaseStatus.Provisioned)
                        && l.EndsAt > windowStart.AddDays(-1) && l.StartsAt < windowEnd.AddDays(1))
            .ToListAsync(ct);

        var candidates = hosts
            .Where(h => !nearby.Any(l => l.HostAccountId == h.Id && l.StartsAt < windowEnd && l.EndsAt > windowStart))
            .OrderByDescending(h => nearby.Where(l => l.HostAccountId == h.Id && l.EndsAt <= windowStart)
                .Select(l => (DateTimeOffset?)l.EndsAt).Max() ?? DateTimeOffset.MinValue);

        foreach (var host in candidates)
        {
            var lease = new HostLease(host.Id, sessionId, startsAt, endsAt);
            db.Leases.Add(lease);
            try
            {
                await db.SaveChangesAsync(ct);
                return lease;
            }
            catch (DbUpdateException ex) when (errors.IsExclusionViolation(ex))
            {
                db.Entry(lease).State = EntityState.Detached;
            }
        }

        return null;
    }
}

/// <summary>Gets a meeting room ready for a session. Idempotent by state: re-delivery resumes where it stopped.</summary>
public sealed class ProvisionMeetingHandler(IWorkspaceDbContext db, HostAllocator allocator, IVirtualRoomProviders providers)
{
    public async Task<HostLease?> ProvisionAsync(Guid sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(ct);
        var provider = connection is null ? null : providers.Find(connection.Provider);
        if (provider is null)
        {
            return null;
        }

        var lease = await db.Leases.FirstOrDefaultAsync(l => l.SessionId == sessionId &&
            (l.Status == LeaseStatus.Reserved || l.Status == LeaseStatus.Provisioned), ct)
            ?? (provider.UsesHostPool ? await allocator.AcquireAsync(sessionId, startsAt, endsAt, ct) : NewHostlessLease(sessionId, startsAt, endsAt));
        if (lease is null || lease.Status == LeaseStatus.Provisioned)
        {
            // No free host: reported by the pool capacity view; a reconciliation job retries later.
            return lease;
        }

        var hostEmail = lease.HostAccountId is { } hostId ? (await db.Hosts.FirstAsync(h => h.Id == hostId, ct)).Email : null;
        var space = await provider.CreateSpaceAsync(hostEmail, sessionId, startsAt, endsAt, ct);
        lease.Provisioned(space.SpaceName, space.MeetingUri);
        db.Publish(new MeetingProvisionedV1(sessionId, space.MeetingUri));
        await db.SaveChangesAsync(ct);
        return lease;
    }

    private HostLease NewHostlessLease(Guid sessionId, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        var lease = new HostLease(null, sessionId, startsAt, endsAt);
        db.Leases.Add(lease);
        return lease;
    }

    public async Task CancelAsync(Guid sessionId, CancellationToken ct)
    {
        var leases = await db.Leases.Where(l => l.SessionId == sessionId &&
            (l.Status == LeaseStatus.Reserved || l.Status == LeaseStatus.Provisioned)).ToListAsync(ct);
        foreach (var lease in leases)
        {
            lease.Cancel();
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class SessionScheduledConsumer(ProvisionMeetingHandler provision) : IIntegrationEventHandler<SessionScheduledV1>
{
    public Task HandleAsync(SessionScheduledV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        @event.DeliveryMode == "Offline" ? Task.CompletedTask : provision.ProvisionAsync(@event.SessionId, @event.StartsAt, @event.EndsAt, ct);
}

public sealed class SessionRescheduledConsumer(ProvisionMeetingHandler provision, ISchedulingApi scheduling) : IIntegrationEventHandler<SessionRescheduledV1>
{
    public async Task HandleAsync(SessionRescheduledV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        await provision.CancelAsync(@event.SessionId, ct);
        var session = await scheduling.GetSessionAsync(@event.SessionId, ct);
        if (session is not null && session.DeliveryMode != "Offline")
        {
            await provision.ProvisionAsync(session.Id, session.StartsAt, session.EndsAt, ct);
        }
    }
}

public sealed class SessionCancelledConsumer(ProvisionMeetingHandler provision) : IIntegrationEventHandler<SessionCancelledV1>
{
    public Task HandleAsync(SessionCancelledV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        provision.CancelAsync(@event.SessionId, ct);
}

public sealed record JoinInfo(Guid SessionId, string MeetingUri);

/// <summary>The only way into a class: checks the person may attend this session, then returns the meeting link.</summary>
public sealed class JoinHandler(IWorkspaceDbContext db, ISchedulingApi scheduling, IAcademicApi academic, ICrmApi crm)
{
    private static readonly string[] CoordinatorRoles = ["TenantAdmin", "Coordinator"];

    public async Task<Result<JoinInfo>> HandleAsync(Guid sessionId, Guid userId, bool isLearner, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        if (await AuthorizeAsync(sessionId, userId, isLearner, roles, ct) is null)
        {
            return Error.NotFound("workspace.session_not_found");
        }

        var lease = await db.Leases.AsNoTracking()
            .Where(l => l.SessionId == sessionId && l.MeetingUri != null && l.Status != LeaseStatus.Cancelled)
            .OrderByDescending(l => l.StartsAt).FirstOrDefaultAsync(ct);
        return lease?.MeetingUri is { } uri
            ? new JoinInfo(sessionId, uri)
            : Error.Conflict("workspace.meeting_not_ready");
    }

    /// <summary>The session when the person may attend it: a learner holding a seat, its teacher, or a coordinator.</summary>
    public async Task<SessionDto?> AuthorizeAsync(Guid sessionId, Guid userId, bool isLearner, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var session = await scheduling.GetSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        var allowed = isLearner
            ? await HoldsSeatAsync(session.ClassId, userId, ct)
            : session.TeacherId == userId || roles.Any(CoordinatorRoles.Contains);
        return allowed ? session : null;
    }

    private async Task<bool> HoldsSeatAsync(Guid classId, Guid userId, CancellationToken ct)
    {
        var students = (await crm.ListStudentsOfUserAsync(userId, ct)).Select(s => s.Id).ToHashSet();
        return students.Count > 0 && (await academic.GetRosterAsync(classId, ct)).Any(r => students.Contains(r.StudentId));
    }
}

public sealed record SetConnectionCommand(string Provider, string? Domain);

public sealed record AddHostCommand(string Email);

public sealed record HostView(Guid Id, string Email, string Status);

public sealed record LeaseView(Guid Id, Guid? HostAccountId, Guid SessionId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, string? MeetingUri);

public sealed class PoolHandlers(IWorkspaceDbContext db, TimeProvider clock)
{
    public async Task<Result<string>> SetConnectionAsync(SetConnectionCommand command, CancellationToken ct)
    {
        if (!Providers.All.Contains(command.Provider))
        {
            return Error.Validation("workspace.provider_unknown");
        }

        // A tenant may switch platform (e.g. from the fake one to the built-in classroom): sessions provisioned
        // from now on use the new provider, rooms already created keep their links.
        var existing = await db.Connections.FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            existing.Change(command.Provider, command.Domain);
        }
        else
        {
            db.Connections.Add(new WorkspaceConnection(command.Provider, command.Domain, clock.GetUtcNow()));
        }

        await db.SaveChangesAsync(ct);
        return command.Provider;
    }

    public async Task<Result<HostView>> AddHostAsync(AddHostCommand command, CancellationToken ct)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(ct);
        if (connection is null)
        {
            return Error.Conflict("workspace.connection_missing");
        }

        if (string.IsNullOrWhiteSpace(command.Email) || !command.Email.Contains('@'))
        {
            return Error.Validation("workspace.host_email_invalid");
        }

        var host = new HostAccount(connection.Id, command.Email);
        if (await db.Hosts.AnyAsync(h => h.Email == host.Email, ct))
        {
            return Error.Conflict("workspace.host_exists");
        }

        db.Hosts.Add(host);
        await db.SaveChangesAsync(ct);
        return new HostView(host.Id, host.Email, host.Status.ToString());
    }

    public async Task<IReadOnlyList<HostView>> ListHostsAsync(CancellationToken ct) =>
        await db.Hosts.AsNoTracking().OrderBy(h => h.Email).Select(h => new HostView(h.Id, h.Email, h.Status.ToString())).ToListAsync(ct);

    public async Task<IReadOnlyList<LeaseView>> ListLeasesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        await db.Leases.AsNoTracking().Where(l => l.StartsAt < to && l.EndsAt > from).OrderBy(l => l.StartsAt)
            .Select(l => new LeaseView(l.Id, l.HostAccountId, l.SessionId, l.StartsAt, l.EndsAt, l.Status.ToString(), l.MeetingUri))
            .ToListAsync(ct);
}

public sealed record SimulatedParticipant(Guid? UserId, string DisplayName, int JoinedMinute, int LeftMinute);

public sealed record SimulateMeetingCommand(IReadOnlyList<SimulatedParticipant> Participants, bool Recording = true);

/// <summary>
/// Development only (fake provider): plays the end of a meeting — participants and recording — so the downstream
/// flow (attendance, lesson consumption, recording access) runs without anyone opening a real meeting.
/// </summary>
public sealed class SimulateMeetingHandler(IWorkspaceDbContext db, ISchedulingApi scheduling, TimeProvider clock)
{
    public async Task<Result<bool>> HandleAsync(Guid sessionId, SimulateMeetingCommand command, CancellationToken ct)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(ct);
        if (connection?.Provider != Providers.Fake)
        {
            return Error.Conflict("workspace.simulation_requires_fake_provider");
        }

        var session = await scheduling.GetSessionAsync(sessionId, ct);
        var lease = await db.Leases.FirstOrDefaultAsync(l => l.SessionId == sessionId && l.Status == LeaseStatus.Provisioned, ct);
        if (session is null || lease is null)
        {
            return Error.Conflict("workspace.meeting_not_ready");
        }

        var participants = command.Participants
            .Select(p => new ParticipantDto(p.UserId, p.DisplayName, session.StartsAt.AddMinutes(p.JoinedMinute), session.StartsAt.AddMinutes(p.LeftMinute)))
            .ToList();
        lease.Release();
        db.Publish(new ParticipantsCollectedV1(sessionId, session.StartsAt, session.EndsAt, participants));
        if (command.Recording)
        {
            var recording = new Recording(sessionId, $"fake://recordings/{sessionId}", clock.GetUtcNow());
            db.Recordings.Add(recording);
            db.Publish(new RecordingAvailableV1(sessionId, recording.Id));
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}
