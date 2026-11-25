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

/// <summary>
/// Paging and sorting query parameters shared by every list endpoint (<c>[AsParameters] ListParams list</c>):
/// <c>page</c>, <c>pageSize</c>, <c>sort</c>, <c>dir=asc|desc</c>. The response is a <see cref="Konnyaku.SharedKernel.Page{T}"/>:
/// <c>{ items, total, pageNumber, pageSize }</c>.
/// </summary>
public sealed record ListParams(int? Page = null, int? PageSize = null, string? Sort = null, string? Dir = null)
{
    /// <summary>The page to read; a sort key outside <paramref name="sorts"/> falls back to the list's default order.</summary>
    public Konnyaku.SharedKernel.PageRequest ToRequest(IReadOnlySet<string>? sorts = null) =>
        Konnyaku.SharedKernel.PageRequest.Of(Page, PageSize, sorts is not null && Sort is not null && !sorts.Contains(Sort) ? null : Sort, Dir);
}
