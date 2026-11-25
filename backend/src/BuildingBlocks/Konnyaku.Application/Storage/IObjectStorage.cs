namespace Konnyaku.Application.Storage;

/// <summary>
/// Object storage for files (docs/10-hoc-tap-va-danh-gia.md, 10.3). Browsers upload and download straight to the store
/// with short-lived signed URLs, so file bodies never pass through the API in production. Implementations:
/// <c>LocalDiskStorage</c> (dev and tests: a folder, the API serves the signed URLs itself) and <c>S3ObjectStorage</c>
/// (Garage in the compose stack, or the object storage of a VPS provider: switching is configuration only).
/// </summary>
public interface IObjectStorage
{
    /// <summary>
    /// A URL the browser sends the file body to with <see cref="SignedRequest.Method"/> (PUT) and the
    /// <see cref="SignedRequest.Headers"/> given (the content type is part of the signature).
    /// </summary>
    Task<SignedRequest> PresignPutAsync(string key, string contentType, TimeSpan expiresIn, CancellationToken cancellationToken);

    /// <summary>A GET URL serving the object with <paramref name="contentType"/> and a content disposition (inline or attachment).</summary>
    Task<SignedRequest> PresignGetAsync(string key, string contentType, string fileName, bool inline, TimeSpan expiresIn,
        CancellationToken cancellationToken);

    /// <summary>Size of the stored object, or null when there is none.</summary>
    Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Reads the object (or its first <paramref name="maxBytes"/> bytes): magic bytes, hashing, scanning. Streams, never buffers it all.</summary>
    Task<Stream> OpenReadAsync(string key, long? maxBytes, CancellationToken cancellationToken);

    /// <summary>Replaces the object (an image rewritten without its metadata).</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>Deletes the object; nothing happens when it does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>A signed request for the browser: send <see cref="Method"/> to <see cref="Url"/> with <see cref="Headers"/>.</summary>
public sealed record SignedRequest(Uri Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

public sealed record ObjectInfo(long Size);
