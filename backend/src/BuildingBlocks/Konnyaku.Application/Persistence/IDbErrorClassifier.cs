namespace Konnyaku.Application.Persistence;

/// <summary>Recognises database constraint violations without the application layer depending on a provider.</summary>
public interface IDbErrorClassifier
{
    /// <summary>An exclusion constraint rejected overlapping rows (e.g. a teacher booked twice at the same time).</summary>
    bool IsExclusionViolation(Exception exception);

    bool IsUniqueViolation(Exception exception);
}
