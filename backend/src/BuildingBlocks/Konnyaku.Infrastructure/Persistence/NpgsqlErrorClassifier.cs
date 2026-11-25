using Konnyaku.Application.Persistence;
using Npgsql;

namespace Konnyaku.Infrastructure.Persistence;

public sealed class NpgsqlErrorClassifier : IDbErrorClassifier
{
    public bool IsExclusionViolation(Exception exception) => Find(exception)?.SqlState == PostgresErrorCodes.ExclusionViolation;

    public bool IsUniqueViolation(Exception exception) => Find(exception)?.SqlState == PostgresErrorCodes.UniqueViolation;

    public bool IsDeadlock(Exception exception) => Find(exception)?.SqlState == PostgresErrorCodes.DeadlockDetected;

    private static PostgresException? Find(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
