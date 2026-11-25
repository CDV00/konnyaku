using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Messaging;
using Konnyaku.Learning.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Scheduling.Application;

/// <summary>
/// Offers freed seats to the waitlist in order of arrival. An offer holds the seat (counted as taken) until the learner
/// accepts or the hold expires; an expired offer frees the seat for the next in line.
/// </summary>
public sealed class WaitlistOffers(ISchedulingDbContext db, SeatLedger seats, BookingSettingsStore settings, TimeProvider clock)
{
    public async Task<int> OfferAsync(Guid sessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        var waiting = await db.Waitlist.Where(w => w.SessionId == sessionId && w.Status == WaitlistStatus.Waiting)
            .OrderBy(w => w.CreatedAt).ToListAsync(ct);
        if (session is null || waiting.Count == 0)
        {
            return 0;
        }

        if (!session.IsChangeable || session.StartsAt <= now)
        {
            waiting.ForEach(w => w.Expire());
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var hold = TimeSpan.FromHours((await settings.GetAsync(ct)).WaitlistHoldHours);
        var expiresAt = now + hold < session.StartsAt ? now + hold : session.StartsAt;
        var offered = 0;
        foreach (var entry in waiting)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var makeup = entry.Kind == BookingKind.Makeup ? MakeupSeat.Required : MakeupSeat.None;
            if (!await seats.TryTakeAsync(sessionId, entry.Mode, makeup, ct))
            {
                continue;
            }

            entry.Offer(now, expiresAt);
            db.Publish(new WaitlistSeatOfferedV1(entry.Id, sessionId, entry.StudentId, expiresAt));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            offered++;
        }

        return offered;
    }

    /// <summary>Offers past their hold: the seat goes back and the next learner in line gets it.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.Waitlist.Where(w => w.Status == WaitlistStatus.Offered && w.OfferExpiresAt <= now).ToListAsync(ct);
        foreach (var entry in due)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            entry.Expire();
            await seats.ReleaseAsync(entry.SessionId, entry.Mode, entry.Kind == BookingKind.Makeup, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        foreach (var sessionId in due.Select(e => e.SessionId).Distinct())
        {
            await OfferAsync(sessionId, ct);
        }

        return due.Count;
    }
}

/// <summary>Worker: a seat became free, the waitlist of the session gets it.</summary>
public sealed class SessionSeatReleasedConsumer(WaitlistOffers offers) : IIntegrationEventHandler<SessionSeatReleasedV1>
{
    public Task HandleAsync(SessionSeatReleasedV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        offers.OfferAsync(@event.SessionId, ct);
}

/// <summary>A new enrollment gets a Regular booking in every coming session of its class.</summary>
public sealed class EnrollmentActivatedBookingConsumer(BookingSync sync) : IIntegrationEventHandler<EnrollmentActivatedV1>
{
    public Task HandleAsync(EnrollmentActivatedV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        sync.SyncEnrollmentAsync(@event.EnrollmentId, ct);
}

/// <summary>A dropped enrollment frees every coming seat it held (own class, make-ups, flexible sessions).</summary>
public sealed class EnrollmentDroppedBookingConsumer(BookingSync sync) : IIntegrationEventHandler<EnrollmentDroppedV1>
{
    public Task HandleAsync(EnrollmentDroppedV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        sync.ReleaseEnrollmentAsync(@event.EnrollmentId, ct);
}

/// <summary>Frozen: seats inside the freeze window are released (the enrollment's current window is re-read, so order does not matter).</summary>
public sealed class EnrollmentFrozenBookingConsumer(BookingSync sync) : IIntegrationEventHandler<EnrollmentFrozenV1>
{
    public Task HandleAsync(EnrollmentFrozenV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        sync.SyncEnrollmentAsync(@event.EnrollmentId, ct);
}

public sealed class EnrollmentUnfrozenBookingConsumer(BookingSync sync) : IIntegrationEventHandler<EnrollmentUnfrozenV1>
{
    public Task HandleAsync(EnrollmentUnfrozenV1 @event, IntegrationEventContext context, CancellationToken ct) =>
        sync.SyncEnrollmentAsync(@event.EnrollmentId, ct);
}

/// <summary>Confirmed attendance closes the booking as Attended or NoShow (the postpone history and roster read it).</summary>
public sealed class AttendanceConfirmedBookingConsumer(ISchedulingDbContext db, TimeProvider clock) : IIntegrationEventHandler<AttendanceConfirmedV1>
{
    public async Task HandleAsync(AttendanceConfirmedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var booking = await db.SessionBookings.FirstOrDefaultAsync(b => b.SessionId == @event.SessionId && b.StudentId == @event.StudentId, ct);
        if (booking is null)
        {
            return;
        }

        booking.RecordAttendance(@event.Status is not ("Absent" or "ExcusedAbsent"), clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }
}
