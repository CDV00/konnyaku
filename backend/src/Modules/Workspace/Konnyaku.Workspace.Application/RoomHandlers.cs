using Konnyaku.Application.Persistence;
using Konnyaku.Application.Tenancy;
using Konnyaku.SharedKernel;
using Konnyaku.Workspace.Contracts;
using Konnyaku.Workspace.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Workspace.Application;

/// <summary>Built-in classroom rooms are addressed as <c>/room/{code}</c> in the web app; the lease keeps <c>rooms/{code}</c>.</summary>
public static class RoomCodes
{
    public const string SpacePrefix = "rooms/";

    /// <summary>The random tail of the session UUIDv7 (its head is a timestamp), as <c>abc-defg-hij</c>.</summary>
    public static string For(Guid sessionId)
    {
        var tail = sessionId.ToString("N")[^10..];
        return $"{tail[..3]}-{tail[3..7]}-{tail[7..]}";
    }

    public static bool IsValid(string code) =>
        code.Length == 12 && code[3] == '-' && code[8] == '-' && code.Where(c => c != '-').All(Uri.IsHexDigit);
}

public static class RoomRoles
{
    /// <summary>Teacher of the session or a coordinator: may end the room.</summary>
    public const string Host = "host";

    public const string Learner = "learner";
}

/// <summary>What the signaling service trusts: who joins which room of which tenant, until when.</summary>
public sealed record RoomTicketClaims(Guid TenantId, Guid SessionId, Guid UserId, string DisplayName, string Role, DateTimeOffset SessionEndsAt);

public sealed record IssuedTicket(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>A STUN/TURN server for <c>RTCPeerConnection</c>; TURN ones carry short-lived credentials.</summary>
public sealed record IceServerView(IReadOnlyList<string> Urls, string? Username, string? Credential);

/// <summary>Signs room tickets and service tokens with the secret shared with the signaling service; hands out ICE servers.</summary>
public interface IRoomTickets
{
    bool IsConfigured { get; }

    string SignalingUrl { get; }

    IssuedTicket Issue(RoomTicketClaims claims);

    IReadOnlyList<IceServerView> IceServers(Guid userId);

    /// <summary>Checks a token the signaling service attaches to its reports.</summary>
    Task<bool> IsValidServiceTokenAsync(string token);
}

public sealed record RoomTicketView(
    Guid SessionId,
    string Code,
    string Ticket,
    DateTimeOffset ExpiresAt,
    string SignalingUrl,
    IReadOnlyList<IceServerView> IceServers,
    string Role,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

/// <summary>
/// Entry to a built-in classroom. Same permission rule as the join link (a learner holding a seat, the teacher, a
/// coordinator), checked again on every ticket: the room page asks for one when it connects and on every reconnect.
/// </summary>
public sealed class RoomTicketHandler(IWorkspaceDbContext db, JoinHandler join, IRoomTickets tickets, ITenantContext tenant)
{
    public async Task<Result<RoomTicketView>> HandleAsync(
        string code, Guid userId, string displayName, bool isLearner, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var lease = RoomCodes.IsValid(code)
            ? await db.Leases.AsNoTracking()
                .Where(l => l.SpaceName == RoomCodes.SpacePrefix + code && l.Status != LeaseStatus.Cancelled)
                .OrderByDescending(l => l.StartsAt).FirstOrDefaultAsync(ct)
            : null;
        var session = lease is null ? null : await join.AuthorizeAsync(lease.SessionId, userId, isLearner, roles, ct);
        if (lease is null || session is null)
        {
            return Error.NotFound("workspace.room_not_found");
        }

        if (lease.Status != LeaseStatus.Provisioned)
        {
            return Error.Conflict("workspace.room_ended");
        }

        if (!tickets.IsConfigured)
        {
            return Error.Conflict("workspace.room_not_configured");
        }

        var role = isLearner ? RoomRoles.Learner : RoomRoles.Host;
        var ticket = tickets.Issue(new RoomTicketClaims(tenant.RequiredTenantId, session.Id, userId, displayName, role, session.EndsAt));
        return new RoomTicketView(session.Id, code, ticket.Ticket, ticket.ExpiresAt, tickets.SignalingUrl, tickets.IceServers(userId), role,
            session.StartsAt, session.EndsAt);
    }
}

public static class RoomEventKinds
{
    public const string Joined = "joined";
    public const string Left = "left";
    public const string Ended = "ended";
}

/// <summary>A report of the signaling service: someone joined or left a room, or the room ended.</summary>
public sealed record RoomEventCommand(
    Guid TenantId, Guid SessionId, string Kind, DateTimeOffset At, Guid? ConnectionId = null, Guid? UserId = null, string? DisplayName = null,
    string? Role = null);

/// <summary>
/// Turns the signaling service's reports into the participant log. When the room ends, publishes
/// <see cref="ParticipantsCollectedV1"/> exactly like the other providers, so attendance and timesheets follow.
/// Every report is idempotent: the signaling service retries until it gets an answer.
/// </summary>
public sealed class RoomEventsHandler(IWorkspaceDbContext db, IDbErrorClassifier errors)
{
    public async Task<Result<bool>> HandleAsync(RoomEventCommand command, CancellationToken ct)
    {
        var lease = await db.Leases
            .Where(l => l.SessionId == command.SessionId && l.SpaceName != null && l.SpaceName.StartsWith(RoomCodes.SpacePrefix)
                        && l.Status != LeaseStatus.Cancelled)
            .OrderByDescending(l => l.StartsAt).FirstOrDefaultAsync(ct);
        if (lease is null)
        {
            return Error.NotFound("workspace.room_not_found");
        }

        if (lease.Status != LeaseStatus.Provisioned)
        {
            // The room already ended: late reports change nothing.
            return true;
        }

        return command.Kind switch
        {
            RoomEventKinds.Joined => await JoinedAsync(command, ct),
            RoomEventKinds.Left => await LeftAsync(command, ct),
            RoomEventKinds.Ended => await EndedAsync(lease, command, ct),
            _ => Error.Validation("workspace.room_event_unknown"),
        };
    }

    private async Task<Result<bool>> JoinedAsync(RoomEventCommand command, CancellationToken ct)
    {
        if (command is not { ConnectionId: { } connectionId, UserId: { } userId })
        {
            return Error.Validation("workspace.room_event_invalid");
        }

        if (await db.RoomPresences.AnyAsync(p => p.ConnectionId == connectionId, ct))
        {
            return true;
        }

        // One person is in the room once: an older connection still open (its "left" was lost, e.g. the signaling
        // service restarted) ends when the new one starts, so presence is never counted twice.
        var stale = await db.RoomPresences
            .Where(p => p.SessionId == command.SessionId && p.UserId == userId && p.LeftAt == null && p.JoinedAt <= command.At)
            .ToListAsync(ct);
        foreach (var presence in stale)
        {
            presence.Leave(command.At);
        }

        db.RoomPresences.Add(new RoomPresence(command.SessionId, connectionId, userId, command.DisplayName ?? string.Empty,
            command.Role ?? RoomRoles.Learner, command.At));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            // The same report was handled concurrently by another API replica.
        }

        return true;
    }

    private async Task<Result<bool>> LeftAsync(RoomEventCommand command, CancellationToken ct)
    {
        if (command.ConnectionId is not { } connectionId)
        {
            return Error.Validation("workspace.room_event_invalid");
        }

        var presence = await db.RoomPresences.FirstOrDefaultAsync(p => p.ConnectionId == connectionId, ct);
        presence?.Leave(command.At);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<Result<bool>> EndedAsync(HostLease lease, RoomEventCommand command, CancellationToken ct)
    {
        var presences = await db.RoomPresences.Where(p => p.SessionId == command.SessionId).OrderBy(p => p.JoinedAt).ToListAsync(ct);
        foreach (var presence in presences)
        {
            presence.Leave(command.At);
        }

        lease.Release();
        var participants = presences.Select(p => new ParticipantDto(p.UserId, p.DisplayName, p.JoinedAt, p.LeftAt ?? command.At)).ToList();
        var startedAt = presences.Count > 0 ? presences.Min(p => p.JoinedAt) : command.At;
        db.Publish(new ParticipantsCollectedV1(command.SessionId, startedAt, command.At, participants));
        await db.SaveChangesAsync(ct);
        return true;
    }
}
