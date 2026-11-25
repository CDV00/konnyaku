using Konnyaku.SharedKernel;

namespace Konnyaku.Content.Contracts;

/// <summary>Purpose of a stored file (same names as the Content module's own enum).</summary>
public enum ContentFilePurpose
{
    QuestionMedia,
    Submission,
    Material,
    ChatAttachment,
    Avatar,
}

/// <summary>A file of the current tenant. <see cref="Status"/>: Pending, Ready, Quarantined or Deleted.</summary>
public sealed record ContentFileDto(
    Guid Id, Guid OwnerUserId, ContentFilePurpose Purpose, string FileName, string ContentType, long Size, string Status,
    DateTimeOffset CreatedAt);

/// <summary>A signed download URL, valid until <see cref="ExpiresAt"/> (5 minutes).</summary>
public sealed record ContentDownloadDto(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Files for other modules, which store only the file id (a question's picture, a homework answer, a chat attachment).
/// Every call is limited to the current tenant.
/// </summary>
public interface IContentApi
{
    /// <summary>The file whatever its status, or null when the tenant has none with this id.</summary>
    Task<ContentFileDto?> GetFileAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks a file id sent by a client before a module keeps it: the file exists in this tenant, is Ready, has
    /// <paramref name="purpose"/> and — when <paramref name="ownerUserId"/> is given — was uploaded by that user.
    /// Errors: <c>content.file_not_found</c>, <c>content.file_not_ready</c>, <c>content.file_purpose_mismatch</c>,
    /// <c>content.file_not_owned</c>.
    /// </summary>
    Task<Result<ContentFileDto>> ValidateAsync(Guid fileId, ContentFilePurpose purpose, Guid? ownerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// A download URL for a Ready file, for a caller that has already checked the reader's right to it (a learner of
    /// the class that owns the question). Null when the file is missing or not Ready.
    /// </summary>
    Task<ContentDownloadDto?> GetDownloadUrlAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>Deletes the file (its owner record was removed); frees its quota. Nothing happens when it does not exist.</summary>
    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken);
}
