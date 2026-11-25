using Konnyaku.SharedKernel;

namespace Konnyaku.Content.Domain;

/// <summary>What a file is for: decides who may upload it, its types and size, and who may read it.</summary>
public enum FilePurpose
{
    /// <summary>Image, audio or video of a question (staff).</summary>
    QuestionMedia,

    /// <summary>A learner's (or teacher's) homework or test answer.</summary>
    Submission,

    /// <summary>Teaching material of the center library (staff).</summary>
    Material,

    /// <summary>Attached to a chat message.</summary>
    ChatAttachment,

    /// <summary>Profile picture.</summary>
    Avatar,
}

public enum FileStatus
{
    /// <summary>Signed upload URL handed out; the object may not exist yet. Cleaned up after 24 h.</summary>
    Pending,

    /// <summary>Checked (size, real type, metadata removed, scanned when a scanner is configured): may be read.</summary>
    Ready,

    /// <summary>The virus scanner flagged it: kept for review, never served.</summary>
    Quarantined,

    /// <summary>Removed (by its owner, an admin, a failed check or the cleanup); the object is gone.</summary>
    Deleted,
}

/// <summary>Which sign-in realm uploaded the file (learners and staff have separate limits).</summary>
public enum UploaderKind
{
    Staff,
    Learner,
}

/// <summary>
/// One file of a tenant in the object store. Other modules keep only its id (<c>IContentApi</c>). The object key never
/// contains the file name (personal data): <c>{tenant}/{purpose}/{yyyy}/{MM}/{id}</c>.
/// </summary>
public sealed class StoredFile : Entity, ITenantOwned
{
    public const int MaxFileNameLength = 255;

    private StoredFile()
    {
    }

    public Guid TenantId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public UploaderKind OwnerKind { get; private set; }
    public FilePurpose Purpose { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;

    /// <summary>Declared size while Pending, then the size actually stored.</summary>
    public long Size { get; private set; }

    /// <summary>Hex SHA-256 of the stored object (after metadata removal); null while Pending.</summary>
    public string? Sha256 { get; private set; }

    public FileStatus Status { get; private set; }
    public string ObjectKey { get; private set; } = string.Empty;

    /// <summary>Why a file was quarantined or rejected (scanner signature or check code); not shown to learners.</summary>
    public string? StatusReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Bytes that count against the tenant quota (stored and not deleted).</summary>
    public bool CountsAgainstQuota => Status is FileStatus.Ready or FileStatus.Quarantined;

    public static StoredFile Create(Guid tenantId, Guid ownerUserId, UploaderKind ownerKind, FilePurpose purpose, string fileName,
        string contentType, long size, DateTimeOffset now)
    {
        var file = new StoredFile
        {
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            OwnerKind = ownerKind,
            Purpose = purpose,
            FileName = fileName,
            ContentType = contentType,
            Size = size,
            Status = FileStatus.Pending,
            CreatedAt = now,
        };
        file.ObjectKey = $"{tenantId:N}/{purpose.ToString().ToLowerInvariant()}/{now:yyyy}/{now:MM}/{file.Id:N}";
        return file;
    }

    public void MarkReady(long size, string sha256, DateTimeOffset now)
    {
        Size = size;
        Sha256 = sha256;
        Status = FileStatus.Ready;
        CompletedAt = now;
    }

    public void Quarantine(long size, string sha256, string reason, DateTimeOffset now)
    {
        Size = size;
        Sha256 = sha256;
        Status = FileStatus.Quarantined;
        StatusReason = Truncate(reason, 200);
        CompletedAt = now;
    }

    public void MarkDeleted(string? reason, DateTimeOffset now)
    {
        Status = FileStatus.Deleted;
        StatusReason = reason is null ? StatusReason : Truncate(reason, 200);
        DeletedAt = now;
    }

    /// <summary>A file name safe to keep and send back: no path, no control characters, bounded length.</summary>
    public static string? CleanFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var leaf = name.Replace('\\', '/').Split('/').Last();
        var cleaned = new string(leaf.Where(c => !char.IsControl(c)).ToArray()).Trim().Normalize(System.Text.NormalizationForm.FormC);
        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            return null;
        }

        return cleaned.Length <= MaxFileNameLength ? cleaned : cleaned[..MaxFileNameLength];
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Storage used and allowed of a tenant (one row per tenant, created on first upload).</summary>
public sealed class TenantQuota : Entity, ITenantOwned
{
    private TenantQuota()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>Bytes allowed; changed by the plan of the center (platform console, later).</summary>
    public long LimitBytes { get; private set; }

    /// <summary>Bytes of Ready and Quarantined files.</summary>
    public long UsedBytes { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TenantQuota Create(long limitBytes, DateTimeOffset now) => new() { LimitBytes = limitBytes, UpdatedAt = now };

    public bool Fits(long additionalBytes, long pendingBytes = 0) => UsedBytes + pendingBytes + additionalBytes <= LimitBytes;

    public void SetLimit(long limitBytes, DateTimeOffset now)
    {
        LimitBytes = Math.Max(0, limitBytes);
        UpdatedAt = now;
    }
}
