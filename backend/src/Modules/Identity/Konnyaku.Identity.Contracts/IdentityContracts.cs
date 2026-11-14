using Konnyaku.SharedKernel;

namespace Konnyaku.Identity.Contracts;

public sealed record MeDto(
    Guid UserId, string DisplayName, string? Email, string PreferredLocale, Guid TenantId, IReadOnlyList<string> Roles);

public interface IIdentityApi
{
    Task<MeDto?> GetMemberAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Active members of the current tenant holding a role (e.g. consultants for lead assignment).</summary>
    Task<IReadOnlyList<Guid>> ListUsersWithRoleAsync(string role, CancellationToken cancellationToken);
}

public sealed record LearnerAccountCreatedV1(Guid UserId, string Username, string Role) : IIntegrationEvent;
