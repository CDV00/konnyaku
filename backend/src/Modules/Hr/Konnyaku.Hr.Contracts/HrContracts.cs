using Konnyaku.SharedKernel;

namespace Konnyaku.Hr.Contracts;

/// <summary>A pay period was locked: payslips are final (notify teachers, export to accounting).</summary>
public sealed record PayrollPeriodLockedV1(Guid PeriodId, DateOnly From, DateOnly To) : IIntegrationEvent;
