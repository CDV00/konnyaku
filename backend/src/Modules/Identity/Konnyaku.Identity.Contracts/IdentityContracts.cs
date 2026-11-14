using Konnyaku.SharedKernel;

namespace Konnyaku.Identity.Contracts;

public sealed record MeDto(
    Guid UserId, string DisplayName, string? Email, string PreferredLocale, Guid TenantId, IReadOnlyList<string> Roles);

public interface IIdentityApi
{
    Task<MeDto?> GetMemberAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record LearnerAccountCreatedV1(Guid UserId, string Username, string Role) : IIntegrationEvent;
