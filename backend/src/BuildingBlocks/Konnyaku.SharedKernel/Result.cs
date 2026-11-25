namespace Konnyaku.SharedKernel;

/// <summary>Error returned to clients as a code plus parameters; the frontend translates the code.</summary>
public sealed record Error(string Code, ErrorKind Kind = ErrorKind.Validation, IReadOnlyDictionary<string, object?>? Params = null)
{
    public static Error Validation(string code, IReadOnlyDictionary<string, object?>? @params = null) => new(code, ErrorKind.Validation, @params);
    public static Error Conflict(string code, IReadOnlyDictionary<string, object?>? @params = null) => new(code, ErrorKind.Conflict, @params);
    public static Error NotFound(string code) => new(code, ErrorKind.NotFound);

    /// <summary>The signed-in member may not act on this record (e.g. a branch outside their scope).</summary>
    public static Error Forbidden(string code) => new(code, ErrorKind.Forbidden);
}

public enum ErrorKind
{
    Validation,
    Conflict,
    NotFound,
    Forbidden,
}

public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result failed with {Error!.Code}.");

    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
