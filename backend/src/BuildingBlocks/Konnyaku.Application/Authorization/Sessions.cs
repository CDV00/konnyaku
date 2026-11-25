namespace Konnyaku.Application.Authorization;

/// <summary>
/// Every staff and learner access token names a server-side sign-in session (<c>sid</c>). The API checks on each
/// request that the session is still active (sign-out, password change, lock, role change revoke it), through a short
/// cache so most requests do not reach the database. Implemented by the Identity module.
/// </summary>
public interface ISessionValidator
{
    Task<bool> IsActiveAsync(Guid sessionId, Guid userId, string audience, CancellationToken cancellationToken);
}

public static class TokenLifetimes
{
    public static readonly TimeSpan Staff = TimeSpan.FromHours(12);

    /// <summary>Learners stay signed in on their own device; an unused session still ends after <see cref="LearnIdle"/>.</summary>
    public static readonly TimeSpan Learn = TimeSpan.FromDays(30);

    public static readonly TimeSpan LearnIdle = TimeSpan.FromDays(14);

    /// <summary>Between the password step and the second factor (or the forced 2FA enrollment) of a staff sign-in.</summary>
    public static readonly TimeSpan Challenge = TimeSpan.FromMinutes(10);
}
