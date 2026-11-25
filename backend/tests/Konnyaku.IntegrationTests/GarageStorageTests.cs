using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Konnyaku.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// <see cref="S3ObjectStorage"/> against a real Garage (the image and config of infra/deploy, bootstrapped like
/// scripts/garage-init.sh): signed PUT/GET from a plain HTTP client, as a browser would, plus head, range read,
/// replace, delete and the bucket CORS rules.
/// </summary>
public sealed class GarageStorageTests : IAsyncLifetime
{
    private const string Image = "dxflrs/garage:v2.5.0";
    private static readonly string KeyId = "GK" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
    private static readonly string KeySecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private readonly IContainer _garage = new ContainerBuilder(Image)
        .WithEnvironment("GARAGE_RPC_SECRET", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
        .WithEnvironment("GARAGE_ADMIN_TOKEN", "test-admin-token")
        .WithResourceMapping(File.ReadAllBytes(RepoFile("infra/deploy/garage/garage.toml")), "/etc/garage.toml")
        .WithPortBinding(3900, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 API server listening"))
        .Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _garage.StartAsync();
        var node = (await GarageAsync("node", "id", "-q")).Split('@')[0];
        await GarageAsync("layout", "assign", "-z", "dc1", "-c", "1G", node[..16]);
        await GarageAsync("layout", "apply", "--version", "1");
        await GarageAsync("bucket", "create", "konnyaku");
        await GarageAsync("key", "import", "--yes", "-n", "konnyaku-app", KeyId, KeySecret);
        await GarageAsync("bucket", "allow", "--read", "--write", "--owner", "konnyaku", "--key", KeyId);
    }

    public async ValueTask DisposeAsync() => await _garage.DisposeAsync();

    [Fact]
    public async Task Signed_urls_and_object_operations_work_against_garage()
    {
        var endpoint = $"http://{_garage.Hostname}:{_garage.GetMappedPublicPort(3900)}";
        using var storage = new S3ObjectStorage(Options.Create(new ObjectStorageOptions
        {
            Provider = ObjectStorageOptions.S3Provider,
            S3 = new S3Options { ServiceUrl = endpoint, Bucket = "konnyaku", AccessKey = KeyId, SecretKey = KeySecret, Region = "garage" },
        }), TimeProvider.System);
        using var http = new HttpClient();
        var key = $"{Guid.NewGuid():N}/material/2026/11/{Guid.NewGuid():N}";
        var body = Encoding.ASCII.GetBytes("%PDF-1.4 garage test body");

        // Browser upload: PUT with the signed content type; another content type breaks the signature.
        var put = await storage.PresignPutAsync(key, "application/pdf", TimeSpan.FromMinutes(10), Ct);
        var wrong = new ByteArrayContent(body);
        wrong.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        (await http.PutAsync(put.Url, wrong, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(put.Headers["Content-Type"]);
        (await http.PutAsync(put.Url, content, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await storage.HeadAsync(key, Ct))!.Size.ShouldBe(body.Length);
        await using (var head = await storage.OpenReadAsync(key, 5, Ct))
        {
            using var copy = new MemoryStream();
            await head.CopyToAsync(copy, Ct);
            Encoding.ASCII.GetString(copy.ToArray()).ShouldBe("%PDF-");
        }

        var get = await storage.PresignGetAsync(key, "application/pdf", "Bài kiểm tra.pdf", inline: false, TimeSpan.FromMinutes(5), Ct);
        var downloaded = await http.GetAsync(get.Url, Ct);
        downloaded.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await downloaded.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(body);
        downloaded.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        downloaded.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        downloaded.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("Bài kiểm tra.pdf");
        // Without its signature the object is private.
        (await http.GetAsync(new Uri(get.Url.GetLeftPart(UriPartial.Path)), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var replaced = Encoding.ASCII.GetBytes("%PDF-1.4 smaller");
        using (var stream = new MemoryStream(replaced))
        {
            await storage.PutAsync(key, stream, "application/pdf", Ct);
        }

        (await storage.HeadAsync(key, Ct))!.Size.ShouldBe(replaced.Length);

        // Garage matches "*" or exact origins only (no "https://*.domain"); the signature is the permission anyway.
        await storage.ApplyCorsAsync(["*"], Ct);
        var preflight = new HttpRequestMessage(HttpMethod.Options, put.Url);
        preflight.Headers.Add("Origin", "https://demo.konnyaku.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var cors = await http.SendAsync(preflight, Ct);
        cors.IsSuccessStatusCode.ShouldBeTrue($"{cors.StatusCode} {await cors.Content.ReadAsStringAsync(Ct)}");
        cors.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain(v => v == "https://demo.konnyaku.example.com" || v == "*");

        await storage.DeleteAsync(key, Ct);
        (await storage.HeadAsync(key, Ct)).ShouldBeNull();
        await storage.DeleteAsync(key, Ct);
    }

    private async Task<string> GarageAsync(params string[] args)
    {
        var result = await _garage.ExecAsync(["/garage", .. args], Ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"garage {string.Join(' ', args)}: {result.Stderr}");
        }

        return result.Stdout.Trim();
    }

    private static string RepoFile(string relative, [CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "infra")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."), relative);
    }
}
