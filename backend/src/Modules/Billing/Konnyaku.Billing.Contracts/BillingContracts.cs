using Konnyaku.SharedKernel;

namespace Konnyaku.Billing.Contracts;

public sealed record CreditAccountDto(
    Guid OrderId, Guid StudentId, Guid? EnrollmentId, decimal PaidCredits, decimal BonusCredits, decimal Total, string Currency);

public sealed record ConsumeResult(bool Consumed, decimal? RemainingCredits);

public interface IBillingApi
{
    Task<IReadOnlyList<CreditAccountDto>> ListCreditAccountsAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken);
}

/// <summary>An order is fully paid; when it targets a class, Academic enrolls the student.</summary>
public sealed record OrderPaidV1(Guid OrderId, Guid StudentId, Guid? ClassId, decimal PaidCredits, decimal BonusCredits) : IIntegrationEvent;

public sealed record CreditBalanceChangedV1(Guid OrderId, Guid StudentId, Guid? EnrollmentId, decimal PaidCredits, decimal BonusCredits) : IIntegrationEvent;

/// <summary>
/// Money was paid back and the order is closed (its unused lessons left the ledger). Academic drops the enrollment the
/// order paid for (<see cref="EnrollmentId"/>, null when the class never took the student).
/// </summary>
public sealed record OrderRefundedV1(Guid OrderId, Guid StudentId, Guid? ClassId, Guid? EnrollmentId, decimal Amount, string Currency) : IIntegrationEvent;

/// <summary>Few lessons left (or owed): trigger renewal care.</summary>
public sealed record CreditLowV1(Guid OrderId, Guid StudentId, Guid? EnrollmentId, decimal RemainingCredits) : IIntegrationEvent;
