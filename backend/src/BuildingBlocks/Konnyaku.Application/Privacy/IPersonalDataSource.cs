namespace Konnyaku.Application.Privacy;

/// <summary>The person a data subject request is about: a student profile and, when one was issued, their account.</summary>
public sealed record PersonalDataSubject(Guid StudentId, Guid? UserId);

/// <summary>
/// What one module holds about a student, for the "give me my data" export of a data subject request. Every module
/// that stores rows about a student registers one (DI, like <c>IIntegrationEventHandler</c>), so the export never reads
/// another module's tables: each source reads its own schema in the current tenant.
/// </summary>
public interface IPersonalDataSource
{
    /// <summary>Section name in the export file (the module name).</summary>
    string Section { get; }

    /// <summary>Plain data (records, lists, primitives) serialized to JSON as is; null when the module holds nothing.</summary>
    Task<object?> ExportAsync(PersonalDataSubject subject, CancellationToken cancellationToken);
}
