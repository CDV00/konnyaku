namespace Konnyaku.SharedKernel;

/// <summary>
/// An entity whose changes are written to its module's change log (who, when, which field, old and new value).
/// <see cref="AuditSubjectId"/> groups entries under the record a person looks at: a class's schedule pattern is
/// logged under the class, for example.
/// </summary>
public interface IAuditable
{
    Guid AuditSubjectId { get; }
}
