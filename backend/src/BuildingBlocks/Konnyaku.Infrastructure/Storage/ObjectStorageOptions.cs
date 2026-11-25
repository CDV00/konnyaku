namespace Konnyaku.Infrastructure.Storage;

/// <summary>
/// Section <c>Storage</c> (environment variables <c>Storage__Provider</c>, <c>Storage__S3__ServiceUrl</c>…).
/// <c>LocalDisk</c> for dev and tests, <c>S3</c> for any S3-compatible store (Garage, or a VPS provider's object storage).
/// </summary>
public sealed class ObjectStorageOptions
{
    public const string Section = "Storage";
    public const string LocalDiskProvider = "LocalDisk";
    public const string S3Provider = "S3";

    public string Provider { get; set; } = LocalDiskProvider;

    public LocalDiskOptions LocalDisk { get; set; } = new();

    public S3Options S3 { get; set; } = new();
}

public sealed class LocalDiskOptions
{
    /// <summary>Folder holding the objects; the API and the Worker of one machine must share it.</summary>
    public string? Root { get; set; }

    /// <summary>Base URL the browser reaches the API at (signed URLs point there).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Path of the signed-URL endpoints in the API (mapped by the Content module).</summary>
    public string PathPrefix { get; set; } = "/api/content/storage";

    /// <summary>HMAC key of the signed URLs; empty: derived from <c>Auth:SigningKey</c>.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Largest body accepted by the signed PUT endpoint.</summary>
    public long MaxUploadBytes { get; set; } = 100 * 1024 * 1024;
}

public sealed class S3Options
{
    /// <summary>Endpoint the API and Worker call (inside the compose network: <c>http://garage:3900</c>).</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Endpoint browsers reach, used in signed URLs (<c>https://files.&lt;domain&gt;</c>); empty: <see cref="ServiceUrl"/>.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Signing region (Garage: the <c>s3_region</c> of garage.toml).</summary>
    public string Region { get; set; } = "garage";

    public string? Bucket { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Path-style URLs (<c>host/bucket/key</c>): Garage and most self-hosted stores need it.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Origins allowed to PUT/GET with signed URLs, applied to the bucket's CORS rules when the API starts: <c>*</c>
    /// (tenants have their own hosts and custom domains; the URL signature is the permission) or exact origins — Garage
    /// does not match <c>https://*.domain</c> patterns. Empty: the bucket's CORS rules are left as they are.
    /// </summary>
    public string[] CorsOrigins { get; set; } = [];
}
