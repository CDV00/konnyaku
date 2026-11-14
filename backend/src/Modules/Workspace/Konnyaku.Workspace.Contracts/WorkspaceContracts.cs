using Konnyaku.SharedKernel;

namespace Konnyaku.Workspace.Contracts;

/// <summary>Someone in the meeting. <see cref="UserId"/> is null for guests (e.g. children with center-issued accounts).</summary>
public sealed record ParticipantDto(Guid? UserId, string DisplayName, DateTimeOffset JoinedAt, DateTimeOffset LeftAt);

public sealed record MeetingProvisionedV1(Guid SessionId, string MeetingUri) : IIntegrationEvent;

/// <summary>The meeting ended; participants with their presence intervals (one row per join).</summary>
public sealed record ParticipantsCollectedV1(Guid SessionId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, IReadOnlyList<ParticipantDto> Participants)
    : IIntegrationEvent;

public sealed record RecordingAvailableV1(Guid SessionId, Guid RecordingId) : IIntegrationEvent;

public sealed record RecordingPublishedV1(Guid SessionId, Guid RecordingId, int ViewerCount) : IIntegrationEvent;
