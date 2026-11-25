using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Konnyaku.Application.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Konnyaku.Infrastructure.Storage;

/// <summary>
/// Any S3-compatible store (Garage in the compose stack; the object storage of Viettel, BizFly, FPT… later): only the
/// <c>Storage__S3__*</c> variables change. Signed URLs use the public endpoint (<c>files.&lt;domain&gt;</c>), every other
/// call the internal one.
/// </summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly S3Options _options;
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _signer;
    private readonly TimeProvider _time;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options, TimeProvider time)
    {
        _options = options.Value.S3;
        _time = time;
        if (string.IsNullOrWhiteSpace(_options.ServiceUrl) || string.IsNullOrWhiteSpace(_options.Bucket)
            || string.IsNullOrWhiteSpace(_options.AccessKey) || string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            throw new InvalidOperationException("Storage:S3 needs ServiceUrl, Bucket, AccessKey and SecretKey.");
        }

        var credentials = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
        _client = new AmazonS3Client(credentials, Config(_options.ServiceUrl));
        _signer = new AmazonS3Client(credentials, Config(string.IsNullOrWhiteSpace(_options.PublicUrl) ? _options.ServiceUrl : _options.PublicUrl));
    }

    public string Bucket => _options.Bucket!;

    public async Task<SignedRequest> PresignPutAsync(string key, string contentType, TimeSpan expiresIn, CancellationToken cancellationToken)
    {
        var expires = _time.GetUtcNow().Add(expiresIn);
        var url = await _signer.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expires.UtcDateTime,
            Protocol = ProtocolOf(_signer),
        });
        return new SignedRequest(new Uri(url), "PUT", new Dictionary<string, string> { ["Content-Type"] = contentType }, expires);
    }

    public async Task<SignedRequest> PresignGetAsync(string key, string contentType, string fileName, bool inline, TimeSpan expiresIn,
        CancellationToken cancellationToken)
    {
        var expires = _time.GetUtcNow().Add(expiresIn);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expires.UtcDateTime,
            Protocol = ProtocolOf(_signer),
        };
        request.ResponseHeaderOverrides.ContentType = contentType;
        request.ResponseHeaderOverrides.ContentDisposition = ContentDisposition.Of(fileName, inline);
        request.ResponseHeaderOverrides.CacheControl = "private, max-age=300";
        var url = await _signer.GetPreSignedURLAsync(request);
        return new SignedRequest(new Uri(url), "GET", new Dictionary<string, string>(), expires);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _client.GetObjectMetadataAsync(Bucket, key, cancellationToken);
            return new ObjectInfo(metadata.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, long? maxBytes, CancellationToken cancellationToken)
    {
        var request = new GetObjectRequest { BucketName = Bucket, Key = key };
        if (maxBytes is { } max and > 0)
        {
            request.ByteRange = new ByteRange(0, max - 1);
        }

        var response = await _client.GetObjectAsync(request, cancellationToken);
        return new OwnedStream(response.ResponseStream, response);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        }, cancellationToken);

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(Bucket, key, cancellationToken);

    /// <summary>Lets the configured origins PUT and GET with signed URLs (the browser uploads straight to the bucket).</summary>
    public async Task ApplyCorsAsync(IReadOnlyList<string> origins, CancellationToken cancellationToken) =>
        await _client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = Bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = [.. origins],
                        AllowedMethods = ["GET", "PUT", "HEAD"],
                        // Garage compares header names as sent (browsers send "content-type"): allow any header.
                        AllowedHeaders = ["*"],
                        ExposeHeaders = ["ETag"],
                        MaxAgeSeconds = 600,
                    },
                ],
            },
        }, cancellationToken);

    public void Dispose()
    {
        _client.Dispose();
        _signer.Dispose();
    }

    private AmazonS3Config Config(string serviceUrl) => new()
    {
        ServiceURL = serviceUrl,
        ForcePathStyle = _options.ForcePathStyle,
        AuthenticationRegion = _options.Region,
        UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        // Self-hosted stores do not all speak the newer flexible checksums: send/verify them only when required.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };

    private static Protocol ProtocolOf(AmazonS3Client client) =>
        client.Config.ServiceURL?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? Protocol.HTTP : Protocol.HTTPS;

    /// <summary>The response body; disposing it also releases the HTTP response.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
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
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Applies <see cref="S3Options.CorsOrigins"/> to the bucket when the API starts (a failure is logged, not fatal).</summary>
internal sealed class S3CorsInitializer(IObjectStorage storage, IOptions<ObjectStorageOptions> options, ILogger<S3CorsInitializer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var origins = options.Value.S3.CorsOrigins.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (storage is not S3ObjectStorage s3 || origins.Count == 0)
        {
            return;
        }

        for (var attempt = 1; attempt <= 5 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await s3.ApplyCorsAsync(origins, stoppingToken);
                logger.LogInformation("Object storage CORS rules applied to bucket {Bucket}", s3.Bucket);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not apply CORS rules to bucket {Bucket} (attempt {Attempt})", s3.Bucket, attempt);
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stoppingToken);
            }
        }
    }
}
