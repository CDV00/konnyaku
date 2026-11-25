using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Http;

namespace Konnyaku.Infrastructure.Http;

/// <summary>Maps domain errors to HTTP: body is always <c>{ code, params }</c>, translated by the frontend.</summary>
public static class ApiResults
{
    public static IResult Error(Error error)
    {
        var body = new ErrorBody(error.Code, error.Params ?? new Dictionary<string, object?>());
        return error.Kind switch
        {
            ErrorKind.NotFound => Results.NotFound(body),
            ErrorKind.Conflict => Results.Conflict(body),
            ErrorKind.Forbidden => Results.Json(body, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.BadRequest(body),
        };
    }

    public static IResult From<T>(Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : Error(result.Error!);
}

public sealed record ErrorBody(string Code, IReadOnlyDictionary<string, object?> Params);
