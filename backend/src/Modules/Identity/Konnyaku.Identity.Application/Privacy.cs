using Konnyaku.Application.Messaging;
using Konnyaku.Application.Privacy;
using Konnyaku.Crm.Contracts;
using Konnyaku.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Identity.Application;

/// <summary>The parts of the Identity store the privacy features use (implemented by the module's DbContext).</summary>
public interface IIdentityPrivacyStore
{
    DbSet<User> Users { get; }
    DbSet<LocalCredential> LocalCredentials { get; }
    DbSet<Membership> Memberships { get; }

    Task<bool> TryAcceptAsync(Guid eventId, string consumer, CancellationToken cancellationToken);

    Task<int> RedactChangeLogAsync(Guid subjectId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A student's personal data was erased in Crm: their learner account loses its name and email, its sign-in
/// (username) is removed and the membership suspended, and the values in its change history are blanked. A person who
/// is also staff of the center keeps their account (only the learner sign-in goes).
/// </summary>
public sealed class StudentAnonymizedConsumer(IIdentityPrivacyStore db) : IIntegrationEventHandler<StudentAnonymizedV1>
{
    public async Task HandleAsync(StudentAnonymizedV1 @event, IntegrationEventContext context, CancellationToken cancellationToken)
    {
        if (@event.UserId is not { } userId || !await db.TryAcceptAsync(context.EventId, nameof(StudentAnonymizedConsumer), cancellationToken))
        {
            return;
        }

        var membership = await db.Memberships.Include(m => m.Roles).FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        var credentials = await db.LocalCredentials.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        db.LocalCredentials.RemoveRange(credentials);
        var staff = membership?.Roles.Any(r => Roles.Staff.Contains(r.Role)) == true;
        if (membership is not null && !staff)
        {
            membership.Suspend();
            // The account belongs to this center (it has a membership here and no staff role): erase its name and email.
            if (await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken) is { } user)
            {
                user.Anonymize();
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (!staff)
        {
            await db.RedactChangeLogAsync(userId, cancellationToken);
        }
    }
}

/// <summary>The learner account of a student: name, email, usernames, roles and status in this center.</summary>
public sealed class IdentityPersonalData(IIdentityPrivacyStore db) : IPersonalDataSource
{
    public string Section => "identity";

    public async Task<object?> ExportAsync(PersonalDataSubject subject, CancellationToken cancellationToken)
    {
        if (subject.UserId is not { } userId)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var membership = await db.Memberships.AsNoTracking().Include(m => m.Roles).FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        var usernames = await db.LocalCredentials.AsNoTracking().Where(c => c.UserId == userId).Select(c => c.Username).ToListAsync(cancellationToken);
        return user is null && membership is null
            ? null
            : new
            {
                account = user is null ? null : new { user.Id, user.DisplayName, user.Email, user.PreferredLocale, user.CreatedAt },
                usernames,
                membership = membership is null
                    ? null
                    : new { status = membership.Status.ToString(), membership.CreatedAt, roles = membership.Roles.Select(r => r.Role).Distinct().ToList() },
            };
    }
}
