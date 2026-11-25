namespace Konnyaku.Application.Persistence;

/// <summary>Recognises database constraint violations without the application layer depending on a provider.</summary>
public interface IDbErrorClassifier
{
    /// <summary>An exclusion constraint rejected overlapping rows (e.g. a teacher booked twice at the same time).</summary>
    bool IsExclusionViolation(Exception exception);

    bool IsUniqueViolation(Exception exception);

    /// <summary>
    /// The database aborted this transaction to break a deadlock. Two concurrent writes checking the same exclusion
    /// constraint can end this way instead of with <see cref="IsExclusionViolation"/>: the other one won. EF reports it
    /// wrapped in an <see cref="InvalidOperationException"/> ("likely due to a transient failure"), so callers catch
    /// <see cref="Exception"/> and test with this method (it looks through inner exceptions).
    /// </summary>
    bool IsDeadlock(Exception exception) => false;
}
