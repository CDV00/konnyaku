using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Konnyaku.Content.Application;
using Konnyaku.Content.Contracts;
using Konnyaku.Content.Domain;
using Konnyaku.Content.Infrastructure;
using Konnyaku.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Konnyaku.IntegrationTests;

/// <summary>
/// File storage (Content module) with LocalDiskStorage: signed upload, completion checks, metadata removal, who may
/// read what, quota, cleanup and quarantine.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContentTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kk-content-tests", Guid.NewGuid().ToString("N"));
    private readonly List<KonnyakuApiFactory> _factories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Staff_upload_goes_straight_to_storage_and_is_read_with_a_short_signed_url()
    {
        var api = Api();
        var c = await CenterAsync(api, "cnt-flow");

        var ticket = await c.Admin.UploadTicketAsync("Giáo trình A1.pdf", "application/pdf", Pdf.Length, "Material");
        ticket.Method.ShouldBe("PUT");
        ticket.ExpiresAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(5));

        // Before the body is sent the file is not there yet.
        await (await c.Admin.PostAsync($"/api/content/uploads/{ticket.FileId}/complete", null, Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "content.upload_missing");

        (await PutAsync(api, ticket, Pdf)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var ready = await CompleteAsync(c.Admin, ticket.FileId);
        ready.GetProperty("status").GetString().ShouldBe("Ready");
        ready.GetProperty("size").GetInt64().ShouldBe(Pdf.Length);

        var download = await DownloadUrlAsync(c.Admin, ticket.FileId);
        var body = await api.CreateClient().GetAsync(download, Ct);
        body.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await body.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Pdf);
        body.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        body.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        body.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("Giáo trình A1.pdf");

        // A signed URL is only good as signed: another key, another operation or a changed header is refused.
        var tampered = download.Replace("ct=application%2Fpdf", "ct=text%2Fhtml", StringComparison.Ordinal);
        (await api.CreateClient().GetAsync(tampered, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await api.CreateClient().PutAsync(download, new ByteArrayContent(Pdf), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var wrongType = new ByteArrayContent(Pdf);
        wrongType.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        (await api.CreateClient().PutAsync(ticket.Url, wrongType, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The object key holds ids only, never the file name.
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ShouldAllBe(f => !f.Contains("Giáo", StringComparison.Ordinal));

        var usage = await c.Admin.GetFromJsonAsync<JsonElement>("/api/content/usage", Ct);
        usage.GetProperty("usedBytes").GetInt64().ShouldBe(Pdf.Length);
        var listed = await c.Admin.GetPageAsync<JsonElement>("/api/content/files?purpose=Material", Ct);
        listed.Items.ShouldHaveSingleItem().GetProperty("fileName").GetString().ShouldBe("Giáo trình A1.pdf");

        // Delete frees the quota and removes the object.
        (await c.Admin.DeleteAsync($"/api/content/files/{ticket.FileId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.Admin.GetFromJsonAsync<JsonElement>("/api/content/usage", Ct)).GetProperty("usedBytes").GetInt64().ShouldBe(0);
        (await api.CreateClient().GetAsync(download, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await (await c.Admin.GetAsync($"/api/content/files/{ticket.FileId}/download", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "content.file_not_found");
    }

    [Fact]
    public async Task Photo_metadata_is_removed_but_its_orientation_kept()
    {
        var api = Api();
        var c = await CenterAsync(api, "cnt-exif");
        var photo = JpegWithExif(orientation: 6, secret: "GPS-10.7769N-106.7009E");

        var ticket = await c.Admin.UploadTicketAsync("photo.jpg", "image/jpg", photo.Length, "Avatar");
        await PutAsync(api, ticket, photo);
        var ready = await CompleteAsync(c.Admin, ticket.FileId);

        var stored = await (await api.CreateClient().GetAsync(await DownloadUrlAsync(c.Admin, ticket.FileId), Ct)).Content.ReadAsByteArrayAsync(Ct);
        Encoding.ASCII.GetString(stored).ShouldNotContain("GPS-10.7769N");
        stored.Length.ShouldBeLessThan(photo.Length);
        ready.GetProperty("size").GetInt64().ShouldBe(stored.Length);
        ready.GetProperty("contentType").GetString().ShouldBe("image/jpeg");
        ImageMetadataProbe.Orientation(stored).ShouldBe((ushort)6);
    }

    [Fact]
    public async Task Completion_checks_the_real_object_not_the_declared_type_or_size()
    {
        var api = Api();
        var c = await CenterAsync(api, "cnt-check");

        // A PDF announced as a PNG.
        var png = await c.Admin.UploadTicketAsync("x.png", "image/png", Pdf.Length, "Material");
        await PutAsync(api, png, Pdf);
        await (await c.Admin.PostAsync($"/api/content/uploads/{png.FileId}/complete", null, Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "content.type_mismatch");
        Directory.GetFiles(_root, $"{png.FileId:N}", SearchOption.AllDirectories).ShouldBeEmpty();
        await (await c.Admin.GetAsync($"/api/content/files/{png.FileId}/download", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "content.file_not_found");

        // More bytes than announced.
        var big = await c.Admin.UploadTicketAsync("x.pdf", "application/pdf", 10, "Material");
        await PutAsync(api, big, Pdf);
        await (await c.Admin.PostAsync($"/api/content/uploads/{big.FileId}/complete", null, Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "content.size_mismatch");

        // Types outside the allow-list and sizes over the purpose limit never get a URL.
        await (await c.Admin.PostAsJsonAsync("/api/content/uploads", Upload("a.svg", "image/svg+xml", 100, "Material"), Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "content.type_not_allowed");
        await (await c.Admin.PostAsJsonAsync("/api/content/uploads", Upload("a.html", "text/html", 100, "Material"), Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "content.type_not_allowed");
        await (await c.Admin.PostAsJsonAsync("/api/content/uploads", Upload("big.png", "image/png", 3 * 1024 * 1024, "Avatar"), Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "content.file_too_large");
        await (await c.Admin.PostAsJsonAsync("/api/content/uploads", Upload("../", "application/pdf", 100, "Material"), Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "content.file_name_invalid");
    }

    [Fact]
    public async Task Who_may_read_a_file_depends_on_its_owner_purpose_and_tenant()
    {
        var api = Api();
        var c = await CenterAsync(api, "cnt-perm");
        var teacher = await c.AddStaffAsync("Teacher");
        var (learnerId, learner) = await c.AddLearnerAsync();
        var (_, otherLearner) = await c.AddLearnerAsync();

        // Learners upload their own work and avatars, not teaching material or question media.
        await (await learner.PostAsJsonAsync("/api/content/uploads", Upload("m.pdf", "application/pdf", 100, "Material"), Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "content.purpose_forbidden");
        await (await learner.PostAsJsonAsync("/api/content/uploads", Upload("q.pdf", "application/pdf", 100, "QuestionMedia"), Ct))
            .ShouldFailWith(HttpStatusCode.Forbidden, "content.purpose_forbidden");
        // Without a virus scanner, learners send images, PDF and audio only.
        await (await learner.PostAsJsonAsync("/api/content/uploads",
                Upload("bai.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 100, "Submission"), Ct))
            .ShouldFailWith(HttpStatusCode.BadRequest, "content.type_not_allowed");

        var submission = await UploadAsync(api, learner, "bai-lam.pdf", "Submission");
        var material = await UploadAsync(api, teacher, "slides.pdf", "Material");

        // The learner's work: the learner and the files administrator, not another learner nor a teacher by default
        // (assessment grants teachers access through IContentApi).
        await DownloadUrlAsync(learner, submission);
        await DownloadUrlAsync(c.Admin, submission);
        await (await otherLearner.GetAsync($"/api/content/files/{submission}/download", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "content.file_forbidden");
        await (await teacher.GetAsync($"/api/content/files/{submission}/download", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "content.file_forbidden");
        // Only the owner (or an administrator) deletes.
        (await otherLearner.DeleteAsync($"/api/content/files/{submission}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherLearner.PostAsync($"/api/content/uploads/{submission}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Teaching material: every staff member, no learner.
        await DownloadUrlAsync(c.Admin, material);
        await (await learner.GetAsync($"/api/content/files/{material}/download", Ct)).ShouldFailWith(HttpStatusCode.Forbidden, "content.file_forbidden");

        // The center's file list and usage are the administrator's.
        (await teacher.GetAsync("/api/content/files", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await learner.GetAsync("/api/content/usage", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var mine = await learner.GetPageAsync<JsonElement>("/api/content/my/files", Ct);
        mine.Items.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(submission);
        mine.Items[0].GetProperty("ownerUserId").GetGuid().ShouldBe(learnerId);

        // Another center does not see the file at all (RLS), not even its administrator.
        var other = await CenterAsync(api, "cnt-othr");
        await (await other.Admin.GetAsync($"/api/content/files/{material}/download", Ct)).ShouldFailWith(HttpStatusCode.NotFound, "content.file_not_found");
        (await other.Admin.DeleteAsync($"/api/content/files/{material}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Admin.GetPageAsync<JsonElement>("/api/content/files", Ct)).Total.ShouldBe(0);
        // A token of one center on another center's host is rejected outright.
        var crossHost = api.ClientFor(other.Slug, c.AdminToken);
        (await crossHost.GetAsync($"/api/content/files/{material}/download", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Other modules: validate ids sent by clients (purpose, owner) and hand out URLs after their own checks.
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(c.TenantId);
        var contentApi = scope.ServiceProvider.GetRequiredService<IContentApi>();
        (await contentApi.ValidateAsync(submission, ContentFilePurpose.Submission, learnerId, Ct)).IsSuccess.ShouldBeTrue();
        (await contentApi.ValidateAsync(submission, ContentFilePurpose.QuestionMedia, null, Ct)).Error!.Code.ShouldBe("content.file_purpose_mismatch");
        (await contentApi.ValidateAsync(submission, ContentFilePurpose.Submission, Guid.NewGuid(), Ct)).Error!.Code.ShouldBe("content.file_not_owned");
        (await contentApi.ValidateAsync(Guid.NewGuid(), ContentFilePurpose.Submission, null, Ct)).Error!.Code.ShouldBe("content.file_not_found");
        (await contentApi.GetDownloadUrlAsync(submission, Ct)).ShouldNotBeNull();
        await contentApi.DeleteAsync(submission, Ct);
        (await contentApi.GetFileAsync(submission, Ct))!.Status.ShouldBe("Deleted");
    }

    [Fact]
    public async Task Quota_counts_pending_and_stored_files()
    {
        var api = Api(new() { ["Content:DefaultQuotaBytes"] = "150" });
        var c = await CenterAsync(api, "cnt-quota");

        var first = await c.Admin.UploadTicketAsync("a.pdf", "application/pdf", Pdf.Length, "Material");
        // The first upload is still pending: it reserves its bytes.
        await (await c.Admin.PostAsJsonAsync("/api/content/uploads", Upload("b.pdf", "application/pdf", 101, "Material"), Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "content.quota_exceeded");
        await PutAsync(api, first, Pdf);
        await CompleteAsync(c.Admin, first.FileId);
        var usage = await c.Admin.GetFromJsonAsync<JsonElement>("/api/content/usage", Ct);
        usage.GetProperty("limitBytes").GetInt64().ShouldBe(150);
        usage.GetProperty("usedBytes").GetInt64().ShouldBe(Pdf.Length);
        usage.GetProperty("byPurpose").EnumerateArray().Single(p => p.GetProperty("purpose").GetString() == "Material")
            .GetProperty("count").GetInt32().ShouldBe(1);
        await (await c.Admin.PostAsJsonAsync("/api/content/uploads", Upload("c.pdf", "application/pdf", 101, "Material"), Ct))
            .ShouldFailWith(HttpStatusCode.Conflict, "content.quota_exceeded");
    }

    [Fact]
    public async Task Unfinished_uploads_are_cleaned_up_by_the_worker()
    {
        var api = Api(new() { ["Content:PendingTtlHours"] = "0" });
        var c = await CenterAsync(api, "cnt-clean");
        var ticket = await c.Admin.UploadTicketAsync("never.pdf", "application/pdf", Pdf.Length, "Material");
        await PutAsync(api, ticket, Pdf);

        var cleanup = new PendingUploadCleanupService(api.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PendingUploadCleanupService>.Instance);
        (await cleanup.RunOnceAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        Directory.GetFiles(_root, $"{ticket.FileId:N}", SearchOption.AllDirectories).ShouldBeEmpty();
        await (await c.Admin.PostAsync($"/api/content/uploads/{ticket.FileId}/complete", null, Ct)).ShouldFailWith(HttpStatusCode.Conflict, "content.file_not_pending");
    }

    [Fact]
    public async Task A_file_flagged_by_the_scanner_is_quarantined_and_never_served()
    {
        var api = Api(services: s => s.Replace(ServiceDescriptor.Singleton<IVirusScanner, FakeScanner>()));
        var c = await CenterAsync(api, "cnt-virus");
        var (_, learner) = await c.AddLearnerAsync();

        // With a scanner configured learners may also send Office files.
        var docx = Encoding.ASCII.GetBytes("PK\u0003\u0004[Content_Types].xml EICAR-TEST");
        var ticket = await learner.UploadTicketAsync("bai.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            docx.Length, "Submission");
        await PutAsync(api, ticket, docx);
        await (await learner.PostAsync($"/api/content/uploads/{ticket.FileId}/complete", null, Ct)).ShouldFailWith(HttpStatusCode.BadRequest, "content.file_quarantined");
        await (await learner.GetAsync($"/api/content/files/{ticket.FileId}/download", Ct)).ShouldFailWith(HttpStatusCode.Conflict, "content.file_not_ready");

        var quarantined = await c.Admin.GetPageAsync<JsonElement>("/api/content/files?status=Quarantined", Ct);
        quarantined.Items.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(ticket.FileId);
        (await c.Admin.GetFromJsonAsync<JsonElement>("/api/content/usage", Ct)).GetProperty("quarantinedCount").GetInt32().ShouldBe(1);
    }

    private KonnyakuApiFactory Api(Dictionary<string, string>? settings = null, Action<IServiceCollection>? services = null)
    {
        var all = new Dictionary<string, string>
        {
            ["Storage:Provider"] = "LocalDisk",
            ["Storage:LocalDisk:Root"] = _root,
            ["Storage:LocalDisk:PublicBaseUrl"] = "http://localhost",
        };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        var factory = new KonnyakuApiFactory(db) { Settings = all, TestServices = services };
        _factories.Add(factory);
        return factory;
    }

    private static async Task<Center> CenterAsync(KonnyakuApiFactory api, string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await api.CreateTenantWithAdminAsync(prefix, email, Password);
        var login = await api.StaffLoginAsync(tenant.Slug, email, Password);
        return new Center(api, tenant.Id, tenant.Slug, login.AccessToken, api.ClientFor(tenant.Slug, login.AccessToken));
    }

    private static object Upload(string fileName, string contentType, long size, string purpose) => new { fileName, contentType, size, purpose };

    private static async Task<Guid> UploadAsync(KonnyakuApiFactory api, HttpClient client, string name, string purpose)
    {
        var ticket = await client.UploadTicketAsync(name, "application/pdf", Pdf.Length, purpose);
        (await PutAsync(api, ticket, Pdf)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await CompleteAsync(client, ticket.FileId);
        return ticket.FileId;
    }

    private static async Task<HttpResponseMessage> PutAsync(KonnyakuApiFactory api, Ticket ticket, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ticket.Headers["Content-Type"]);
        return await api.CreateClient().PutAsync(ticket.Url, content, Ct);
    }

    private static async Task<JsonElement> CompleteAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"/api/content/uploads/{id}/complete", null, Ct);
        await response.ShouldSucceed();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<string> DownloadUrlAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/content/files/{id}/download", Ct);
        await response.ShouldSucceed();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
        return body.GetProperty("url").GetString()!;
    }

    /// <summary>A minimal JPEG: JFIF, an EXIF block (orientation + a GPS-like marker), a scan and the end marker.</summary>
    private static byte[] JpegWithExif(ushort orientation, string secret)
    {
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00 };
        tiff.AddRange([(byte)orientation, (byte)(orientation >> 8), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        tiff.AddRange(Encoding.ASCII.GetBytes(secret));
        var app1 = new List<byte>();
        app1.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
        app1.AddRange(tiff);
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        byte[] jfif = [0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];
        jpeg.AddRange([0xFF, 0xE0, 0x00, (byte)(jfif.Length + 2)]);
        jpeg.AddRange(jfif);
        jpeg.AddRange([0xFF, 0xE1, (byte)((app1.Count + 2) >> 8), (byte)(app1.Count + 2)]);
        jpeg.AddRange(app1);
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0x33, 0xFF, 0xD9]);
        return [.. jpeg];
    }

    private sealed class FakeScanner : IVirusScanner
    {
        public bool Enabled => true;

        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(content);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return text.Contains("EICAR", StringComparison.Ordinal) ? new ScanResult(false, "Eicar-Test-Signature") : new ScanResult(true, null);
        }
    }

    private sealed record Center(KonnyakuApiFactory Api, Guid TenantId, string Slug, string AdminToken, HttpClient Admin)
    {
        public async Task<HttpClient> AddStaffAsync(string role)
        {
            var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
            await (await Admin.PostAsJsonAsync("/api/identity/staff",
                new { email, displayName = $"{role} Test", initialPassword = Password, roles = new[] { role } }, Ct)).ShouldSucceed();
            var login = await Api.StaffLoginAsync(Slug, email, Password);
            return Api.ClientFor(Slug, login.AccessToken);
        }

        public async Task<(Guid UserId, HttpClient Client)> AddLearnerAsync()
        {
            var created = await Admin.PostAsJsonAsync("/api/identity/learners", new { displayName = "Học viên Tệp", role = "Student" }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            var learner = (await created.Content.ReadFromJsonAsync<LearnerCreated>(Ct))!;
            var login = await Api.ClientFor(Slug).PostAsJsonAsync("/api/identity/learn/login",
                new { username = learner.Username, password = learner.TemporaryPassword }, Ct);
            await login.ShouldSucceed();
            var token = (await login.Content.ReadFromJsonAsync<SignInDto>(Ct))!.AccessToken;
            return (learner.UserId, Api.ClientFor(Slug, token));
        }
    }

    private sealed record LearnerCreated(Guid UserId, string Username, string TemporaryPassword);
}

internal sealed record Ticket(Guid FileId, string Url, string Method, Dictionary<string, string> Headers, DateTimeOffset ExpiresAt);

internal static class ContentClientExtensions
{
    public static async Task<Ticket> UploadTicketAsync(this HttpClient client, string fileName, string contentType, long size, string purpose)
    {
        var response = await client.PostAsJsonAsync("/api/content/uploads", new { fileName, contentType, size, purpose },
            TestContext.Current.CancellationToken);
        await response.ShouldSucceed();
        return (await response.Content.ReadFromJsonAsync<Ticket>(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>Reads the EXIF orientation left in a JPEG (through the same parser the module uses).</summary>
internal static class ImageMetadataProbe
{
    public static ushort? Orientation(byte[] jpeg)
    {
        for (var i = 2; i + 4 < jpeg.Length;)
        {
            if (jpeg[i] != 0xFF)
            {
                return null;
            }

            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker == 0xE1)
            {
                return ImageMetadata.ReadExifOrientation(jpeg.AsSpan(i + 4, length - 2));
            }

            if (marker == 0xDA)
            {
                return null;
            }

            i += 2 + length;
        }

        return null;
    }
}
