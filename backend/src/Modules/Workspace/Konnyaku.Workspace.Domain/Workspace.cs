using Konnyaku.SharedKernel;

namespace Konnyaku.Workspace.Domain;

public static class Providers
{
    /// <summary>In-memory meetings for development, CI and demos — no Google account needed.</summary>
    public const string Fake = "Fake";

    public const string Google = "Google";

    /// <summary>
    /// Built-in WebRTC classroom (peer-to-peer mesh, rooms served by the web app and the signaling service).
    /// Needs no organizer account, so sessions do not take a host from the pool.
    /// </summary>
    public const string Konnyaku = "Konnyaku";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Fake, Google, Konnyaku };
}

/// <summary>How a tenant connects its own video-meeting platform (its own Google Workspace domain, or the fake one).</summary>
public sealed class WorkspaceConnection : Entity, ITenantOwned
{
    private WorkspaceConnection()
    {
    }

    public WorkspaceConnection(string provider, string? domain, DateTimeOffset now)
    {
        Provider = provider;
        Domain = domain;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string? Domain { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Switches platform; rooms already created keep their links, new sessions use the new provider.</summary>
    public void Change(string provider, string? domain)
    {
        Provider = provider;
        Domain = domain;
    }
}

public enum HostStatus
{
    Active,
    Draining,
    Suspended,
}

/// <summary>A licensed meeting-organizer account of the tenant's pool, operated by the system only.</summary>
public sealed class HostAccount : Entity, ITenantOwned
{
    private HostAccount()
    {
    }

    public HostAccount(Guid connectionId, string email)
    {
        ConnectionId = connectionId;
        Email = email.Trim().ToLowerInvariant();
        Status = HostStatus.Active;
    }

    public Guid TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public HostStatus Status { get; private set; }
}

public enum LeaseStatus
{
    Reserved,
    Provisioned,
    Released,
    Cancelled,
}

/// <summary>
/// A host account rented for one session, padded before and after. PostgreSQL forbids two live leases of the same
/// host that overlap, so the pool can never double-book an organizer, even when provisioning runs in parallel.
/// Providers without organizer accounts (<see cref="Providers.Konnyaku"/>) lease no host: <see cref="HostAccountId"/> is null.
/// </summary>
public sealed class HostLease : Entity, ITenantOwned
{
    public static readonly TimeSpan Padding = TimeSpan.FromMinutes(15);

    private HostLease()
    {
    }

    public HostLease(Guid? hostAccountId, Guid sessionId, DateTimeOffset sessionStartsAt, DateTimeOffset sessionEndsAt)
    {
        HostAccountId = hostAccountId;
        SessionId = sessionId;
        StartsAt = sessionStartsAt - Padding;
        EndsAt = sessionEndsAt + Padding;
        Status = LeaseStatus.Reserved;
    }

    public Guid TenantId { get; private set; }
    public Guid? HostAccountId { get; private set; }
    public Guid SessionId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public LeaseStatus Status { get; private set; }
    public string? SpaceName { get; private set; }
    public string? MeetingUri { get; private set; }

    public bool IsLive => Status is LeaseStatus.Reserved or LeaseStatus.Provisioned;

    public void Provisioned(string spaceName, string meetingUri)
    {
        SpaceName = spaceName;
        MeetingUri = meetingUri;
        Status = LeaseStatus.Provisioned;
    }

    public void Release() => Status = LeaseStatus.Released;

    public void Cancel() => Status = LeaseStatus.Cancelled;
}

public enum RecordingStatus
{
    Available,
    Published,
}

/// <summary>Recording of a session. Who may watch is decided by Learning; this module only applies the access.</summary>
public sealed class Recording : Entity, ITenantOwned
{
    private Recording()
    {
    }

    public Recording(Guid sessionId, string fileRef, DateTimeOffset now)
    {
        SessionId = sessionId;
        FileRef = fileRef;
        Status = RecordingStatus.Available;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public string FileRef { get; private set; } = string.Empty;
    public RecordingStatus Status { get; private set; }
    public List<Guid> ViewerUserIds { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    public void Publish(IEnumerable<Guid> viewers)
    {
        ViewerUserIds = viewers.Distinct().ToList();
        Status = RecordingStatus.Published;
    }
}

/// <summary>
/// One connection of a person to a built-in classroom, reported by the signaling service: joined, then left.
/// A reconnect is a new row. When the room ends these become the participant log of the meeting.
/// </summary>
public sealed class RoomPresence : Entity, ITenantOwned
{
    private RoomPresence()
    {
    }

    public RoomPresence(Guid sessionId, Guid connectionId, Guid userId, string displayName, string role, DateTimeOffset joinedAt)
    {
        SessionId = sessionId;
        ConnectionId = connectionId;
        UserId = userId;
        DisplayName = displayName.Length > 200 ? displayName[..200] : displayName;
        Role = role;
        JoinedAt = joinedAt;
    }

    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid UserId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; private set; }
    public DateTimeOffset? LeftAt { get; private set; }

    public void Leave(DateTimeOffset at)
    {
        if (LeftAt is null)
        {
            LeftAt = at < JoinedAt ? JoinedAt : at;
        }
    }
}
