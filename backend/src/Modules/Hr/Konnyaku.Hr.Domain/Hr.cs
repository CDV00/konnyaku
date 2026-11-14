using Konnyaku.SharedKernel;

namespace Konnyaku.Hr.Domain;

/// <summary>Hourly teaching rate of a teacher, valid from a date (a new rate never rewrites past timesheets).</summary>
public sealed class PayRate : Entity, ITenantOwned
{
    private PayRate()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid TeacherId { get; private set; }
    public decimal HourlyRate { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateOnly EffectiveFrom { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<PayRate> Create(Guid teacherId, decimal hourlyRate, string currency, DateOnly effectiveFrom, DateTimeOffset now)
    {
        if (hourlyRate < 0 || currency is not { Length: 3 })
        {
            return Error.Validation("hr.rate_invalid");
        }

        return new PayRate
        {
            TeacherId = teacherId,
            HourlyRate = hourlyRate,
            Currency = currency.ToUpperInvariant(),
            EffectiveFrom = effectiveFrom,
            CreatedAt = now,
        };
    }
}

public enum TimesheetSource
{
    /// <summary>Minutes measured from the meeting participant log.</summary>
    Meeting,

    /// <summary>No meeting log (offline class): the planned length, created when attendance is confirmed.</summary>
    Planned,
}

public enum TimesheetStatus
{
    Draft,
    Approved,
    Locked,
}

/// <summary>
/// One taught session of one teacher. Generated from real data — nobody types it. Priced with the rate effective on
/// the lesson date; the price is frozen once the payroll period approves it.
/// </summary>
public sealed class Timesheet : Entity, ITenantOwned
{
    private Timesheet()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid TeacherId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid BranchId { get; private set; }
    public DateOnly LocalDate { get; private set; }
    public int PlannedMinutes { get; private set; }
    public int ActualMinutes { get; private set; }
    public int LateMinutes { get; private set; }
    public TimesheetSource Source { get; private set; }
    public decimal? HourlyRate { get; private set; }
    public string? Currency { get; private set; }
    public decimal Amount { get; private set; }
    public TimesheetStatus Status { get; private set; }
    public Guid? PayrollPeriodId { get; private set; }

    /// <summary>Paid time: what was actually taught, never more than planned. Offline classes: the planned length.</summary>
    public int PaidMinutes => Source == TimesheetSource.Meeting ? Math.Min(ActualMinutes, PlannedMinutes) : PlannedMinutes;

    public static Timesheet ForMeeting(Guid teacherId, Guid sessionId, Guid branchId, DateOnly localDate, int plannedMinutes, int actualMinutes, int lateMinutes) => new()
    {
        TeacherId = teacherId,
        SessionId = sessionId,
        BranchId = branchId,
        LocalDate = localDate,
        PlannedMinutes = plannedMinutes,
        ActualMinutes = actualMinutes,
        LateMinutes = lateMinutes,
        Source = TimesheetSource.Meeting,
        Status = TimesheetStatus.Draft,
    };

    public static Timesheet ForPlanned(Guid teacherId, Guid sessionId, Guid branchId, DateOnly localDate, int plannedMinutes) => new()
    {
        TeacherId = teacherId,
        SessionId = sessionId,
        BranchId = branchId,
        LocalDate = localDate,
        PlannedMinutes = plannedMinutes,
        ActualMinutes = plannedMinutes,
        Source = TimesheetSource.Planned,
        Status = TimesheetStatus.Draft,
    };

    /// <summary>The meeting log is more precise than the plan: it replaces it while the timesheet is still a draft.</summary>
    public void RecordMeeting(int actualMinutes, int lateMinutes)
    {
        if (Status != TimesheetStatus.Draft)
        {
            return;
        }

        ActualMinutes = actualMinutes;
        LateMinutes = lateMinutes;
        Source = TimesheetSource.Meeting;
    }

    public void Price(PayRate? rate)
    {
        if (Status != TimesheetStatus.Draft)
        {
            return;
        }

        HourlyRate = rate?.HourlyRate;
        Currency = rate?.Currency;
        Amount = rate is null ? 0 : decimal.Round(rate.HourlyRate * PaidMinutes / 60m, 0, MidpointRounding.AwayFromZero);
    }

    public void Approve(Guid periodId)
    {
        PayrollPeriodId = periodId;
        Status = TimesheetStatus.Approved;
    }

    public void Lock() => Status = TimesheetStatus.Locked;
}

public enum PayrollStatus
{
    Open,
    Locked,
}

/// <summary>
/// A pay period. Opening it approves every draft timesheet up to its end date — including late ones from an earlier,
/// already locked period, which are paid now instead of reopening the past. Locking freezes everything.
/// </summary>
public sealed class PayrollPeriod : Entity, ITenantOwned
{
    private PayrollPeriod()
    {
    }

    public PayrollPeriod(DateOnly from, DateOnly to, DateTimeOffset now)
    {
        From = from;
        To = to;
        Status = PayrollStatus.Open;
        CreatedAt = now;
    }

    public Guid TenantId { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public PayrollStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LockedAt { get; private set; }

    public Result<PayrollPeriod> Lock(DateTimeOffset now)
    {
        if (Status == PayrollStatus.Locked)
        {
            return Error.Conflict("hr.period_locked");
        }

        Status = PayrollStatus.Locked;
        LockedAt = now;
        return this;
    }
}
