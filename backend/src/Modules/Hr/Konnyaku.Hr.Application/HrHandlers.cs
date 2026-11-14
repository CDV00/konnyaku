using Konnyaku.Application.Messaging;
using Konnyaku.Application.Persistence;
using Konnyaku.Hr.Contracts;
using Konnyaku.Hr.Domain;
using Konnyaku.Identity.Contracts;
using Konnyaku.Learning.Contracts;
using Konnyaku.Scheduling.Contracts;
using Konnyaku.SharedKernel;
using Konnyaku.Workspace.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Hr.Application;

public interface IHrDbContext
{
    DbSet<PayRate> PayRates { get; }
    DbSet<Timesheet> Timesheets { get; }
    DbSet<PayrollPeriod> PayrollPeriods { get; }

    void Publish(IIntegrationEvent integrationEvent, string? correlationId = null);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

internal static class Rates
{
    public static Task<PayRate?> EffectiveAsync(IHrDbContext db, Guid teacherId, DateOnly date, CancellationToken ct) =>
        db.PayRates.Where(r => r.TeacherId == teacherId && r.EffectiveFrom <= date)
            .OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
}

/// <summary>The teacher's presence in the meeting log becomes (or refines) the timesheet of the session.</summary>
public sealed class ParticipantsCollectedTimesheetConsumer(IHrDbContext db, ISchedulingApi scheduling, IDbErrorClassifier errors)
    : IIntegrationEventHandler<ParticipantsCollectedV1>
{
    public async Task HandleAsync(ParticipantsCollectedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var session = await scheduling.GetSessionAsync(@event.SessionId, ct);
        if (session?.TeacherId is not { } teacherId)
        {
            return;
        }

        var joins = @event.Participants.Where(p => p.UserId == teacherId).ToList();
        var seconds = joins.Sum(p => Overlap(p.JoinedAt, p.LeftAt, session.StartsAt, session.EndsAt));
        var late = joins.Count == 0 ? 0 : Math.Max(0, (int)(joins.Min(p => p.JoinedAt) - session.StartsAt).TotalMinutes);
        var planned = (int)(session.EndsAt - session.StartsAt).TotalMinutes;

        var timesheet = await db.Timesheets.FirstOrDefaultAsync(t => t.SessionId == session.Id && t.TeacherId == teacherId, ct);
        if (timesheet is null)
        {
            timesheet = Timesheet.ForMeeting(teacherId, session.Id, session.BranchId, session.LocalDate, planned, seconds / 60, late);
            db.Timesheets.Add(timesheet);
        }
        else
        {
            timesheet.RecordMeeting(seconds / 60, late);
        }

        timesheet.Price(await Rates.EffectiveAsync(db, teacherId, session.LocalDate, ct));
        await SaveIgnoringDuplicateAsync(db, errors, ct);
    }

    internal static async Task SaveIgnoringDuplicateAsync(IHrDbContext db, IDbErrorClassifier errors, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (errors.IsUniqueViolation(ex))
        {
            // Another delivery created the same timesheet concurrently; redelivery will refine it.
            throw new InvalidOperationException("Timesheet created concurrently; retry.", ex);
        }
    }

    private static int Overlap(DateTimeOffset joined, DateTimeOffset left, DateTimeOffset start, DateTimeOffset end)
    {
        var from = joined > start ? joined : start;
        var to = left < end ? left : end;
        return to > from ? (int)(to - from).TotalSeconds : 0;
    }
}

/// <summary>
/// Confirmed attendance proves the lesson happened. Without a meeting log (offline class) the planned length is paid.
/// </summary>
public sealed class AttendanceConfirmedTimesheetConsumer(IHrDbContext db, ISchedulingApi scheduling, IDbErrorClassifier errors)
    : IIntegrationEventHandler<SessionAttendanceConfirmedV1>
{
    public async Task HandleAsync(SessionAttendanceConfirmedV1 @event, IntegrationEventContext context, CancellationToken ct)
    {
        var session = await scheduling.GetSessionAsync(@event.SessionId, ct);
        if (session?.TeacherId is not { } teacherId ||
            await db.Timesheets.AnyAsync(t => t.SessionId == session.Id && t.TeacherId == teacherId, ct))
        {
            return;
        }

        var timesheet = Timesheet.ForPlanned(teacherId, session.Id, session.BranchId, session.LocalDate,
            (int)(session.EndsAt - session.StartsAt).TotalMinutes);
        timesheet.Price(await Rates.EffectiveAsync(db, teacherId, session.LocalDate, ct));
        db.Timesheets.Add(timesheet);
        await ParticipantsCollectedTimesheetConsumer.SaveIgnoringDuplicateAsync(db, errors, ct);
    }
}

public sealed record SetRateCommand(decimal HourlyRate, string Currency, DateOnly EffectiveFrom);

public sealed record RateView(Guid TeacherId, decimal HourlyRate, string Currency, DateOnly EffectiveFrom);

public sealed record TimesheetView(
    Guid Id, Guid TeacherId, Guid SessionId, Guid BranchId, DateOnly LocalDate, int PlannedMinutes, int ActualMinutes, int PaidMinutes,
    int LateMinutes, string Source, decimal? HourlyRate, string? Currency, decimal Amount, string Status);

public sealed record CreatePeriodCommand(DateOnly From, DateOnly To);

public sealed record PayslipLine(Guid TeacherId, string TeacherName, string Currency, int Sessions, int PaidMinutes, decimal Amount, int UnpricedSessions);

public sealed record PeriodView(Guid Id, DateOnly From, DateOnly To, string Status, IReadOnlyList<PayslipLine> Payslips);

public sealed class PayrollHandlers(IHrDbContext db, IIdentityApi identity, TimeProvider clock)
{
    public const string TeacherRole = "Teacher";

    public async Task<Result<RateView>> SetRateAsync(Guid teacherId, SetRateCommand command, CancellationToken ct)
    {
        if ((await identity.GetMemberAsync(teacherId, ct))?.Roles.Contains(TeacherRole) != true)
        {
            return Error.Validation("hr.teacher_unknown");
        }

        var created = PayRate.Create(teacherId, command.HourlyRate, command.Currency ?? string.Empty, command.EffectiveFrom, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        db.PayRates.Add(created.Value);

        // Drafts on or after the new date are re-priced now; approved/locked ones keep their frozen price.
        foreach (var draft in await db.Timesheets.Where(t => t.TeacherId == teacherId && t.Status == TimesheetStatus.Draft
                     && t.LocalDate >= command.EffectiveFrom).ToListAsync(ct))
        {
            draft.Price(created.Value);
        }

        await db.SaveChangesAsync(ct);
        return new RateView(teacherId, created.Value.HourlyRate, created.Value.Currency, created.Value.EffectiveFrom);
    }

    public async Task<IReadOnlyList<TimesheetView>> ListAsync(Guid? teacherId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var query = db.Timesheets.AsNoTracking().Where(t => t.LocalDate >= from && t.LocalDate <= to);
        if (teacherId is { } id)
        {
            query = query.Where(t => t.TeacherId == id);
        }

        return (await query.OrderBy(t => t.LocalDate).ToListAsync(ct)).Select(ToView).ToList();
    }

    /// <summary>Opens a period: approves every draft up to its end (stragglers from locked periods included).</summary>
    public async Task<Result<PeriodView>> CreatePeriodAsync(CreatePeriodCommand command, CancellationToken ct)
    {
        if (command.To < command.From)
        {
            return Error.Validation("hr.period_range_invalid");
        }

        if (await db.PayrollPeriods.AnyAsync(p => p.From <= command.To && p.To >= command.From, ct))
        {
            return Error.Conflict("hr.period_overlaps");
        }

        var period = new PayrollPeriod(command.From, command.To, clock.GetUtcNow());
        db.PayrollPeriods.Add(period);
        foreach (var draft in await db.Timesheets.Where(t => t.Status == TimesheetStatus.Draft && t.LocalDate <= command.To).ToListAsync(ct))
        {
            draft.Price(await Rates.EffectiveAsync(db, draft.TeacherId, draft.LocalDate, ct));
            draft.Approve(period.Id);
        }

        await db.SaveChangesAsync(ct);
        return await ViewAsync(period, ct);
    }

    public async Task<Result<PeriodView>> LockAsync(Guid periodId, CancellationToken ct)
    {
        var period = await db.PayrollPeriods.FirstOrDefaultAsync(p => p.Id == periodId, ct);
        if (period is null)
        {
            return Error.NotFound("hr.period_not_found");
        }

        var locked = period.Lock(clock.GetUtcNow());
        if (!locked.IsSuccess)
        {
            return locked.Error!;
        }

        foreach (var timesheet in await db.Timesheets.Where(t => t.PayrollPeriodId == periodId).ToListAsync(ct))
        {
            timesheet.Lock();
        }

        db.Publish(new PayrollPeriodLockedV1(period.Id, period.From, period.To));
        await db.SaveChangesAsync(ct);
        return await ViewAsync(period, ct);
    }

    public async Task<IReadOnlyList<PeriodView>> ListPeriodsAsync(CancellationToken ct)
    {
        var periods = await db.PayrollPeriods.AsNoTracking().OrderByDescending(p => p.From).Take(24).ToListAsync(ct);
        var views = new List<PeriodView>();
        foreach (var period in periods)
        {
            views.Add(await ViewAsync(period, ct));
        }

        return views;
    }

    private async Task<PeriodView> ViewAsync(PayrollPeriod period, CancellationToken ct)
    {
        var timesheets = await db.Timesheets.AsNoTracking().Where(t => t.PayrollPeriodId == period.Id).ToListAsync(ct);
        var lines = new List<PayslipLine>();
        foreach (var group in timesheets.GroupBy(t => t.TeacherId))
        {
            var name = (await identity.GetMemberAsync(group.Key, ct))?.DisplayName ?? string.Empty;
            foreach (var byCurrency in group.GroupBy(t => t.Currency ?? string.Empty))
            {
                lines.Add(new PayslipLine(group.Key, name, byCurrency.Key, byCurrency.Count(), byCurrency.Sum(t => t.PaidMinutes),
                    byCurrency.Sum(t => t.Amount), byCurrency.Count(t => t.HourlyRate is null)));
            }
        }

        return new PeriodView(period.Id, period.From, period.To, period.Status.ToString(), lines.OrderBy(l => l.TeacherName).ToList());
    }

    private static TimesheetView ToView(Timesheet t) => new(t.Id, t.TeacherId, t.SessionId, t.BranchId, t.LocalDate, t.PlannedMinutes,
        t.ActualMinutes, t.PaidMinutes, t.LateMinutes, t.Source.ToString(), t.HourlyRate, t.Currency, t.Amount, t.Status.ToString());
}
