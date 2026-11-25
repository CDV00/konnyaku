using System.Security.Cryptography;
using Konnyaku.Application.Persistence;
using Konnyaku.Application.Storage;
using Konnyaku.Content.Domain;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Konnyaku.Content.Application;

public interface IContentDbContext
{
    DbSet<StoredFile> Files { get; }
    DbSet<TenantQuota> Quotas { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Section <c>Content</c>.</summary>
public sealed class ContentOptions
{
    public const string Section = "Content";

    /// <summary>Storage a center gets until its plan says otherwise (5 GiB).</summary>
    public long DefaultQuotaBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Pending uploads (signed URL handed out, never completed) are removed after this many hours.</summary>
    public int PendingTtlHours { get; set; } = 24;

    /// <summary>Unfinished uploads one member may have at a time.</summary>
    public int MaxPendingPerUser { get; set; } = 20;

    public ClamAvOptions ClamAv { get; set; } = new();
}

/// <summary>Optional ClamAV daemon (compose profile <c>clamav</c>). Empty host: no scanning.</summary>
public sealed class ClamAvOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Virus scanning hook. Without a scanner every checked file becomes Ready (learners then upload fewer kinds).</summary>
public interface IVirusScanner
{
    bool Enabled { get; }

    /// <summary>Clean, or the name of what was found. Throws when the scanner cannot be reached.</summary>
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

public sealed record ScanResult(bool Clean, string? Finding);

/// <summary>Who calls: the signed-in member, their realm, and whether they manage every file of the center.</summary>
public sealed record FileActor(Guid UserId, UploaderKind Kind, bool IsFilesAdmin);

public sealed record CreateUploadCommand(string? FileName, string? ContentType, long Size, FilePurpose Purpose);

/// <summary>Send the file with <see cref="Method"/> to <see cref="Url"/> with <see cref="Headers"/>, then call complete.</summary>
public sealed record UploadTicket(Guid FileId, Uri Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

public sealed record FileView(
    Guid Id, Guid OwnerUserId, UploaderKind OwnerKind, FilePurpose Purpose, string FileName, string ContentType, long Size,
    FileStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record DownloadView(Uri Url, DateTimeOffset ExpiresAt);

public sealed record PurposeUsage(FilePurpose Purpose, int Count, long Bytes);

public sealed record UsageView(long LimitBytes, long UsedBytes, long PendingBytes, int QuarantinedCount, IReadOnlyList<PurposeUsage> ByPurpose);

/// <summary>
/// Upload, complete, download and delete (docs/10-hoc-tap-va-danh-gia.md, 10.3). The browser talks to the object
/// store with signed URLs; the API only checks and records.
/// </summary>
public sealed class ContentHandlers(
    IContentDbContext db,
    IObjectStorage storage,
    IVirusScanner scanner,
    IOptions<ContentOptions> options,
    TimeProvider time,
    ILogger<ContentHandlers> logger)
{
    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(5);

    private static readonly Error NotFound = Error.NotFound("content.file_not_found");

    public async Task<Result<UploadTicket>> CreateUploadAsync(CreateUploadCommand command, FileActor actor, CancellationToken ct)
    {
        var name = StoredFile.CleanFileName(command.FileName);
        if (name is null)
        {
            return Error.Validation("content.file_name_invalid");
        }

        var checkedType = UploadPolicy.Check(command.Purpose, actor.Kind, command.ContentType, command.Size, scanner.Enabled);
        if (!checkedType.IsSuccess)
        {
            return checkedType.Error!;
        }

        var now = time.GetUtcNow();
        var pendingSince = now.AddHours(-options.Value.PendingTtlHours);
        var mine = await db.Files.CountAsync(f => f.OwnerUserId == actor.UserId && f.Status == FileStatus.Pending && f.CreatedAt > pendingSince, ct);
        if (mine >= options.Value.MaxPendingPerUser)
        {
            return Error.Conflict("content.too_many_pending");
        }

        var quota = await QuotaAsync(ct);
        var pending = await db.Files.Where(f => f.Status == FileStatus.Pending && f.CreatedAt > pendingSince).SumAsync(f => (long?)f.Size, ct) ?? 0;
        if (!quota.Fits(command.Size, pending))
        {
            return Error.Conflict("content.quota_exceeded");
        }

        var file = StoredFile.Create(quota.TenantId, actor.UserId, actor.Kind, command.Purpose, name, checkedType.Value, command.Size, now);
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);

        var signed = await storage.PresignPutAsync(file.ObjectKey, file.ContentType, UploadUrlLifetime, ct);
        return new UploadTicket(file.Id, signed.Url, signed.Method, signed.Headers, signed.ExpiresAt);
    }

    /// <summary>Checks the object really uploaded (size, magic bytes), removes image metadata, scans, counts it, marks it Ready.</summary>
    public async Task<Result<FileView>> CompleteAsync(Guid id, FileActor actor, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || file.OwnerUserId != actor.UserId)
        {
            return NotFound;
        }

        if (file.Status == FileStatus.Ready)
        {
            return View(file);
        }

        if (file.Status != FileStatus.Pending)
        {
            return Error.Conflict("content.file_not_pending");
        }

        var head = await storage.HeadAsync(file.ObjectKey, ct);
        if (head is null)
        {
            return Error.Validation("content.upload_missing");
        }

        if (head.Size != file.Size)
        {
            return await RejectAsync(file, "content.size_mismatch", ct);
        }

        byte[] first;
        await using (var stream = await storage.OpenReadAsync(file.ObjectKey, FileSignature.HeadLength, ct))
        {
            first = await ReadAllAsync(stream, FileSignature.HeadLength, ct);
        }

        if (!FileTypes.Matches(file.ContentType, first))
        {
            return await RejectAsync(file, "content.type_mismatch", ct);
        }

        long size;
        string sha256;
        if (ImageMetadata.Supports(file.ContentType))
        {
            byte[] original;
            await using (var stream = await storage.OpenReadAsync(file.ObjectKey, null, ct))
            {
                original = await ReadAllAsync(stream, UploadPolicy.MaxImageBytes, ct);
            }

            var stripped = ImageMetadata.Strip(original, file.ContentType);
            if (stripped is null)
            {
                return await RejectAsync(file, "content.image_invalid", ct);
            }

            if (!stripped.AsSpan().SequenceEqual(original))
            {
                using var cleaned = new MemoryStream(stripped, writable: false);
                await storage.PutAsync(file.ObjectKey, cleaned, file.ContentType, ct);
            }

            size = stripped.Length;
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stripped));
        }
        else
        {
            await using var stream = await storage.OpenReadAsync(file.ObjectKey, null, ct);
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            size = head.Size;
        }

        ScanResult scan = new(true, null);
        if (scanner.Enabled)
        {
            try
            {
                await using var stream = await storage.OpenReadAsync(file.ObjectKey, null, ct);
                scan = await scanner.ScanAsync(stream, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Virus scan of file {FileId} failed", file.Id);
                return Error.Conflict("content.scan_unavailable");
            }
        }

        var now = time.GetUtcNow();
        if (!await TryUseQuotaAsync(size, now, ct))
        {
            return await RejectAsync(file, "content.quota_exceeded", ct, Error.Conflict("content.quota_exceeded"));
        }

        if (scan.Clean)
        {
            file.MarkReady(size, sha256, now);
        }
        else
        {
            logger.LogWarning("File {FileId} quarantined: {Finding}", file.Id, scan.Finding);
            file.Quarantine(size, sha256, scan.Finding ?? "infected", now);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await ReleaseQuotaAsync(size, now, CancellationToken.None);
            throw;
        }

        return scan.Clean ? View(file) : Error.Validation("content.file_quarantined");
    }

    public async Task<Result<DownloadView>> GetDownloadAsync(Guid id, FileActor actor, CancellationToken ct)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || file.Status == FileStatus.Deleted)
        {
            return NotFound;
        }

        if (!CanRead(file, actor))
        {
            return Error.Forbidden("content.file_forbidden");
        }

        if (file.Status != FileStatus.Ready)
        {
            return Error.Conflict("content.file_not_ready");
        }

        return await DownloadAsync(file, ct);
    }

    public async Task<Result<FileView>> DeleteAsync(Guid id, FileActor actor, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || (file.OwnerUserId != actor.UserId && !actor.IsFilesAdmin))
        {
            return NotFound;
        }

        await RemoveAsync(file, "deleted", ct);
        return View(file);
    }

    public async Task<Page<FileView>> ListAsync(FilePurpose? purpose, FileStatus? status, Guid? ownerUserId, PageRequest page, CancellationToken ct)
    {
        var query = db.Files.AsNoTracking();
        query = status is { } s ? query.Where(f => f.Status == s) : query.Where(f => f.Status != FileStatus.Deleted);
        if (purpose is { } p)
        {
            query = query.Where(f => f.Purpose == p);
        }

        if (ownerUserId is { } owner)
        {
            query = query.Where(f => f.OwnerUserId == owner);
        }

        query = page.Sort switch
        {
            "size" => page.Descending ? query.OrderByDescending(f => f.Size).ThenBy(f => f.Id) : query.OrderBy(f => f.Size).ThenBy(f => f.Id),
            "name" => page.Descending ? query.OrderByDescending(f => f.FileName).ThenBy(f => f.Id) : query.OrderBy(f => f.FileName).ThenBy(f => f.Id),
            _ => page.Descending ? query.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id) : query.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id),
        };
        var total = await query.CountAsync(ct);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new Page<FileView>(items.Select(View).ToList(), total, page.Page, page.PageSize);
    }

    public async Task<UsageView> UsageAsync(CancellationToken ct)
    {
        var quota = await db.Quotas.AsNoTracking().FirstOrDefaultAsync(ct);
        var pendingSince = time.GetUtcNow().AddHours(-options.Value.PendingTtlHours);
        var byPurpose = await db.Files.AsNoTracking()
            .Where(f => f.Status == FileStatus.Ready || f.Status == FileStatus.Quarantined)
            .GroupBy(f => f.Purpose)
            .Select(g => new PurposeUsage(g.Key, g.Count(), g.Sum(f => f.Size)))
            .ToListAsync(ct);
        var pending = await db.Files.Where(f => f.Status == FileStatus.Pending && f.CreatedAt > pendingSince).SumAsync(f => (long?)f.Size, ct) ?? 0;
        var quarantined = await db.Files.CountAsync(f => f.Status == FileStatus.Quarantined, ct);
        return new UsageView(
            quota?.LimitBytes ?? options.Value.DefaultQuotaBytes, quota?.UsedBytes ?? 0, pending, quarantined,
            Enum.GetValues<FilePurpose>().Select(p => byPurpose.FirstOrDefault(u => u.Purpose == p) ?? new PurposeUsage(p, 0, 0)).ToList());
    }

    /// <summary>Removes the uploads of the current tenant left Pending past the TTL (Worker job). Returns how many.</summary>
    public async Task<int> CleanupPendingAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddHours(-options.Value.PendingTtlHours);
        var stale = await db.Files.Where(f => f.Status == FileStatus.Pending && f.CreatedAt <= cutoff).OrderBy(f => f.CreatedAt).Take(500).ToListAsync(ct);
        foreach (var file in stale)
        {
            await storage.DeleteAsync(file.ObjectKey, ct);
            file.MarkDeleted("expired", time.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    internal async Task<DownloadView> DownloadAsync(StoredFile file, CancellationToken ct)
    {
        var signed = await storage.PresignGetAsync(file.ObjectKey, file.ContentType, file.FileName, FileTypes.IsInline(file.ContentType),
            DownloadUrlLifetime, ct);
        return new DownloadView(signed.Url, signed.ExpiresAt);
    }

    /// <summary>Deletes the object and marks the file Deleted, freeing its quota.</summary>
    internal async Task RemoveAsync(StoredFile file, string reason, CancellationToken ct)
    {
        if (file.Status == FileStatus.Deleted)
        {
            return;
        }

        var counted = file.CountsAgainstQuota ? file.Size : 0;
        await storage.DeleteAsync(file.ObjectKey, ct);
        var now = time.GetUtcNow();
        file.MarkDeleted(reason, now);
        await db.SaveChangesAsync(ct);
        if (counted > 0)
        {
            await ReleaseQuotaAsync(counted, now, ct);
        }
    }

    /// <summary>
    /// Who may read a Ready file directly: its owner, a files administrator, anyone of the center for an avatar, any staff
    /// member for teaching material and question media. Other modules grant more through <c>IContentApi</c>.
    /// </summary>
    public static bool CanRead(StoredFile file, FileActor actor) =>
        file.OwnerUserId == actor.UserId
        || actor.IsFilesAdmin
        || file.Purpose == FilePurpose.Avatar
        || (actor.Kind == UploaderKind.Staff && file.Purpose is FilePurpose.Material or FilePurpose.QuestionMedia);

    internal static FileView View(StoredFile f) =>
        new(f.Id, f.OwnerUserId, f.OwnerKind, f.Purpose, f.FileName, f.ContentType, f.Size, f.Status, f.CreatedAt, f.CompletedAt);

    private async Task<Result<FileView>> RejectAsync(StoredFile file, string code, CancellationToken ct, Error? error = null)
    {
        await storage.DeleteAsync(file.ObjectKey, ct);
        file.MarkDeleted(code, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return error ?? Error.Validation(code);
    }

    private async Task<TenantQuota> QuotaAsync(CancellationToken ct)
    {
        if (await db.Quotas.FirstOrDefaultAsync(ct) is { } quota)
        {
            return quota;
        }

        quota = TenantQuota.Create(options.Value.DefaultQuotaBytes, time.GetUtcNow());
        db.Quotas.Add(quota);
        try
        {
            await db.SaveChangesAsync(ct);
            return quota;
        }
        catch (DbUpdateException)
        {
            // Created by a concurrent first upload of the same tenant (unique tenant_id): use that row.
            db.Quotas.Entry(quota).State = EntityState.Detached;
            return await db.Quotas.FirstAsync(ct);
        }
    }

    /// <summary>Atomic: adds <paramref name="bytes"/> only while the total stays within the limit.</summary>
    private async Task<bool> TryUseQuotaAsync(long bytes, DateTimeOffset now, CancellationToken ct)
    {
        await QuotaAsync(ct);
        var updated = await db.Quotas
            .Where(q => q.UsedBytes + bytes <= q.LimitBytes)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.UsedBytes, q => q.UsedBytes + bytes).SetProperty(q => q.UpdatedAt, now), ct);
        return updated > 0;
    }

    private async Task ReleaseQuotaAsync(long bytes, DateTimeOffset now, CancellationToken ct) =>
        await db.Quotas.ExecuteUpdateAsync(s => s
            .SetProperty(q => q.UsedBytes, q => q.UsedBytes > bytes ? q.UsedBytes - bytes : 0)
            .SetProperty(q => q.UpdatedAt, now), ct);

    private static async Task<byte[]> ReadAllAsync(Stream stream, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                buffer.Write(chunk, 0, (int)(max - buffer.Length));
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>Files for other modules (see <see cref="Contracts.IContentApi"/>).</summary>
public sealed class ContentApi(IContentDbContext db, ContentHandlers handlers) : Contracts.IContentApi
{
    public async Task<Contracts.ContentFileDto?> GetFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        return file is null ? null : Dto(file);
    }

    public async Task<Result<Contracts.ContentFileDto>> ValidateAsync(Guid fileId, Contracts.ContentFilePurpose purpose, Guid? ownerUserId,
        CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);
        if (file is null || file.Status == FileStatus.Deleted)
        {
            return Error.Validation("content.file_not_found");
        }

        if (file.Status != FileStatus.Ready)
        {
            return Error.Validation("content.file_not_ready");
        }

        if ((int)file.Purpose != (int)purpose)
        {
            return Error.Validation("content.file_purpose_mismatch");
        }

        if (ownerUserId is { } owner && file.OwnerUserId != owner)
        {
            return Error.Validation("content.file_not_owned");
        }

        return Dto(file);
    }

    public async Task<Contracts.ContentDownloadDto?> GetDownloadUrlAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.Status == FileStatus.Ready, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var download = await handlers.DownloadAsync(file, cancellationToken);
        return new Contracts.ContentDownloadDto(download.Url, download.ExpiresAt);
    }

    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken)
    {
        if (await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken) is { } file)
        {
            await handlers.RemoveAsync(file, "released", cancellationToken);
        }
    }

    private static Contracts.ContentFileDto Dto(StoredFile f) =>
        new(f.Id, f.OwnerUserId, (Contracts.ContentFilePurpose)(int)f.Purpose, f.FileName, f.ContentType, f.Size, f.Status.ToString(), f.CreatedAt);
}
