using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Identity.Application;
using Konnyaku.Identity.Domain;
using Konnyaku.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Konnyaku.Identity.Infrastructure;

/// <summary>
/// "Is this session still active?" answers kept for <c>Auth:SessionCacheSeconds</c> (default 30) per API process, so a
/// revocation reaches every replica within that time while most requests never touch the database.
/// </summary>
internal sealed class SessionCache(IMemoryCache cache, IConfiguration configuration) : ISessionCache
{
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Auth:SessionCacheSeconds", 30), 0, 300));

    public bool TryGet(Guid sessionId, out bool active) => cache.TryGetValue(Key(sessionId), out active);

    public void Set(Guid sessionId, bool active)
    {
        if (_ttl > TimeSpan.Zero)
        {
            cache.Set(Key(sessionId), active, _ttl);
        }
    }

    public void Evict(IEnumerable<Guid> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            cache.Remove(Key(id));
        }
    }

    private static string Key(Guid sessionId) => $"kk:session:{sessionId}";
}

/// <summary>
/// A session is active when it is not revoked or expired, its credential still has the stamp it started with (no new
/// password since) and — for learners — it was used within the idle limit. Refreshes "last seen" every few minutes.
/// </summary>
internal sealed class SessionValidator(IdentityDbContext db, SessionCache cache, TimeProvider clock) : ISessionValidator
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    public async Task<bool> IsActiveAsync(Guid sessionId, Guid userId, string audience, CancellationToken cancellationToken)
    {
        if (cache.TryGet(sessionId, out var cached))
        {
            return cached;
        }

        var now = clock.GetUtcNow();
        var session = await db.UserSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .Select(s => new { s.Audience, s.RevokedAt, s.ExpiresAt, s.LastSeenAt, s.CredentialStamp })
            .FirstOrDefaultAsync(cancellationToken);
        var active = session is not null && session.Audience == audience && session.RevokedAt is null && session.ExpiresAt > now
                     && (audience != Realms.Learn || session.LastSeenAt.Add(TokenLifetimes.LearnIdle) > now);
        if (active)
        {
            var stamp = audience == Realms.Staff
                ? await db.PasswordCredentials.AsNoTracking().Where(c => c.UserId == userId).Select(c => (Guid?)c.SecurityStamp).FirstOrDefaultAsync(cancellationToken)
                : await db.LocalCredentials.AsNoTracking().Where(c => c.UserId == userId).Select(c => (Guid?)c.SecurityStamp).FirstOrDefaultAsync(cancellationToken);
            active = stamp == session!.CredentialStamp;
        }

        if (active && session!.LastSeenAt.Add(LastSeenResolution) < now)
        {
            await db.UserSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), cancellationToken);
        }

        cache.Set(sessionId, active);
        return active;
    }
}

internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    private HttpContext? Http => accessor.HttpContext;

    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;

    public Guid? ActorId => Http?.User.UserId();

    public string? ActorName => Http?.User.FindFirstValue(KonnyakuClaims.Name);

    public Guid? SessionId => Http?.User.SessionId();
}

internal static class TwoFactorConfiguration
{
    /// <summary>
    /// <c>Auth:TwoFactor:Enforce</c> (default true) and <c>Auth:TwoFactor:RequiredRoles</c> (default TenantAdmin, Accountant).
    /// Development and test settings turn enforcement off so seed scripts can sign in with a password alone.
    /// </summary>
    public static TwoFactorPolicy Policy(IConfiguration configuration)
    {
        if (!configuration.GetValue("Auth:TwoFactor:Enforce", true))
        {
            return new TwoFactorPolicy(new HashSet<string>());
        }

        var roles = configuration.GetSection("Auth:TwoFactor:RequiredRoles").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : [Roles.TenantAdmin, Roles.Accountant];
        return new TwoFactorPolicy(roles.ToHashSet());
    }
}

/// <summary>
/// Stand-in until the Notification module sends email: logs that a link was requested, with the link itself in
/// Development only (never in production logs).
/// </summary>
internal sealed class LoggingPasswordResetMailer(ILogger<LoggingPasswordResetMailer> logger, IHostEnvironment environment) : IPasswordResetMailer
{
    public Task SendAsync(PasswordResetMail mail, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            logger.LogInformation("Password reset link for {UserId} (no mail service yet): {ResetUrl}", mail.UserId, mail.ResetUrl);
        }
        else
        {
            logger.LogWarning("Password reset requested for {UserId}, but no mail service is configured; the link was not sent", mail.UserId);
        }

        return Task.CompletedTask;
    }
}
