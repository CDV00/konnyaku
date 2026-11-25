using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Konnyaku.Application.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Konnyaku.Infrastructure.Storage;

/// <summary>
/// Dev and test storage: objects are files under <see cref="LocalDiskOptions.Root"/>, and the API itself serves the
/// signed URLs (<see cref="LocalDiskStorageEndpoints"/>), with the same rules as S3: an expiry, a signature over the
/// method, key and headers, and no URL without one. Not for production (files would go through the API).
/// </summary>
public sealed partial class LocalDiskStorage : IObjectStorage
{
    private readonly LocalDiskOptions _options;
    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public LocalDiskStorage(IOptions<ObjectStorageOptions> options, Microsoft.Extensions.Configuration.IConfiguration configuration, TimeProvider time)
    {
        _options = options.Value.LocalDisk;
        _time = time;
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Root) ? Path.Combine(Path.GetTempPath(), "konnyaku-files") : _options.Root);
        Directory.CreateDirectory(Root);
        var secret = _options.SigningKey is { Length: > 0 } k ? k : "local-disk-storage:" + configuration["Auth:SigningKey"];
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public string Root { get; }

    public long MaxUploadBytes => _options.MaxUploadBytes;

    public Task<SignedRequest> PresignPutAsync(string key, string contentType, TimeSpan expiresIn, CancellationToken cancellationToken)
    {
        var expires = _time.GetUtcNow().Add(expiresIn);
        var query = new Dictionary<string, string?>
        {
            ["op"] = "put",
            ["exp"] = expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ct"] = contentType,
        };
        query["sig"] = Sign(key, query);
        return Task.FromResult(new SignedRequest(Url(key, query), "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType }, expires));
    }

    public Task<SignedRequest> PresignGetAsync(string key, string contentType, string fileName, bool inline, TimeSpan expiresIn,
        CancellationToken cancellationToken)
    {
        var expires = _time.GetUtcNow().Add(expiresIn);
        var query = new Dictionary<string, string?>
        {
            ["op"] = "get",
            ["exp"] = expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ct"] = contentType,
            ["cd"] = ContentDisposition.Of(fileName, inline),
        };
        query["sig"] = Sign(key, query);
        return Task.FromResult(new SignedRequest(Url(key, query), "GET", new Dictionary<string, string>(), expires));
    }

    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        var file = new FileInfo(PathOf(key));
        return Task.FromResult(file.Exists ? new ObjectInfo(file.Length) : null);
    }

    public Task<Stream> OpenReadAsync(string key, long? maxBytes, CancellationToken cancellationToken)
    {
        Stream stream = new FileStream(PathOf(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(maxBytes is { } max ? new LimitedStream(stream, max) : stream);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Checks a signed URL: right signature, not expired, same operation.</summary>
    internal bool Verify(string key, string op, IQueryCollection query)
    {
        if (!IsValidKey(key) || query["op"] != op || !long.TryParse(query["exp"], out var exp)
            || DateTimeOffset.FromUnixTimeSeconds(exp) < _time.GetUtcNow())
        {
            return false;
        }

        var values = query.Where(q => q.Key != "sig").ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
        var expected = Encoding.ASCII.GetBytes(Sign(key, values));
        var provided = Encoding.ASCII.GetBytes(query["sig"].ToString());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    internal string PathOf(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException("Invalid object key.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(Root, key));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid object key.", nameof(key));
        }

        return path;
    }

    internal static bool IsValidKey(string key) => KeyPattern().IsMatch(key) && !key.Contains("..", StringComparison.Ordinal);

    private string Sign(string key, IReadOnlyDictionary<string, string?> query)
    {
        var canonical = new StringBuilder(key);
        foreach (var (name, value) in query.OrderBy(q => q.Key, StringComparer.Ordinal))
        {
            canonical.Append('\n').Append(name).Append('=').Append(value);
        }

        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private Uri Url(string key, IReadOnlyDictionary<string, string?> query) =>
        new(QueryHelpers.AddQueryString(_options.PublicBaseUrl.TrimEnd('/') + _options.PathPrefix + "/" + key, query));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9/._-]{0,511}$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// The signed-URL endpoints of <see cref="LocalDiskStorage"/>: <c>PUT</c> (upload), <c>GET</c> (download) and the CORS
/// preflight, anonymous (the signature is the permission), like an S3 bucket.
/// </summary>
public static class LocalDiskStorageEndpoints
{
    public static void MapLocalDiskStorage(this IEndpointRouteBuilder group, string path = "/storage")
    {
        group.MapMethods(path + "/{**key}", ["OPTIONS"], (HttpContext context) =>
        {
            Cors(context);
            return Results.NoContent();
        }).AllowAnonymous().ExcludeFromDescription();

        group.MapPut(path + "/{**key}", async (string key, HttpContext context, IObjectStorage storage, CancellationToken ct) =>
        {
            Cors(context);
            if (storage is not LocalDiskStorage disk || !disk.Verify(key, "put", context.Request.Query))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!string.Equals(context.Request.ContentType, context.Request.Query["ct"], StringComparison.OrdinalIgnoreCase))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = disk.MaxUploadBytes;
            }

            if (context.Request.ContentLength > disk.MaxUploadBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await disk.PutAsync(key, context.Request.Body, context.Request.ContentType!, ct);
            return Results.Ok();
        }).AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();

        group.MapGet(path + "/{**key}", (string key, HttpContext context, IObjectStorage storage) =>
        {
            Cors(context);
            if (storage is not LocalDiskStorage disk || !disk.Verify(key, "get", context.Request.Query))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var file = disk.PathOf(key);
            if (!File.Exists(file))
            {
                return Results.NotFound();
            }

            context.Response.Headers[HeaderNames.ContentDisposition] = context.Request.Query["cd"].ToString();
            context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            context.Response.Headers[HeaderNames.CacheControl] = "private, max-age=300";
            return Results.File(file, context.Request.Query["ct"].ToString(), enableRangeProcessing: true);
        }).AllowAnonymous().ExcludeFromDescription();
    }

    private static void Cors(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers[HeaderNames.AccessControlAllowOrigin] = "*";
        headers[HeaderNames.AccessControlAllowMethods] = "GET, PUT";
        headers[HeaderNames.AccessControlAllowHeaders] = "Content-Type";
        headers[HeaderNames.AccessControlMaxAge] = "600";
    }
}

/// <summary>RFC 6266 Content-Disposition with an ASCII fallback and the UTF-8 file name.</summary>
public static class ContentDisposition
{
    public static string Of(string fileName, bool inline)
    {
        var ascii = new string(fileName.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' and not ';' ? c : '_').ToArray());
        return $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}

/// <summary>Reads at most <c>limit</c> bytes of the inner stream.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _remaining = limit;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
