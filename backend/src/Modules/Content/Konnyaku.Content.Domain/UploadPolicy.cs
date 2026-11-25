using Konnyaku.SharedKernel;

namespace Konnyaku.Content.Domain;

public enum FileKind
{
    Image,
    Audio,
    Video,
    Pdf,
    Office,
}

/// <summary>
/// The allow-list of content types (images, audio, PDF, Office, short video — never HTML, SVG or scripts) and the
/// magic-byte family each one must start with.
/// </summary>
public static class FileTypes
{
    private static readonly Dictionary<string, (FileKind Kind, string Signature)> Allowed = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = (FileKind.Image, FileSignature.Jpeg),
        ["image/png"] = (FileKind.Image, FileSignature.Png),
        ["image/gif"] = (FileKind.Image, FileSignature.Gif),
        ["image/webp"] = (FileKind.Image, FileSignature.Webp),
        ["audio/mpeg"] = (FileKind.Audio, FileSignature.Mp3),
        ["audio/mp4"] = (FileKind.Audio, FileSignature.Mp4),
        ["audio/ogg"] = (FileKind.Audio, FileSignature.Ogg),
        ["audio/wav"] = (FileKind.Audio, FileSignature.Wav),
        ["audio/webm"] = (FileKind.Audio, FileSignature.Ebml),
        ["video/mp4"] = (FileKind.Video, FileSignature.Mp4),
        ["video/webm"] = (FileKind.Video, FileSignature.Ebml),
        ["application/pdf"] = (FileKind.Pdf, FileSignature.Pdf),
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = (FileKind.Office, FileSignature.Zip),
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = (FileKind.Office, FileSignature.Zip),
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = (FileKind.Office, FileSignature.Zip),
        ["application/msword"] = (FileKind.Office, FileSignature.Ole),
        ["application/vnd.ms-excel"] = (FileKind.Office, FileSignature.Ole),
        ["application/vnd.ms-powerpoint"] = (FileKind.Office, FileSignature.Ole),
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["image/jpg"] = "image/jpeg",
        ["image/pjpeg"] = "image/jpeg",
        ["audio/mp3"] = "audio/mpeg",
        ["audio/x-m4a"] = "audio/mp4",
        ["audio/m4a"] = "audio/mp4",
        ["audio/x-wav"] = "audio/wav",
        ["audio/wave"] = "audio/wav",
        ["audio/vnd.wave"] = "audio/wav",
    };

    /// <summary>The canonical allowed type (lower case, no parameters, aliases resolved), or null when not allowed.</summary>
    public static string? Normalize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        type = Aliases.GetValueOrDefault(type, type);
        return Allowed.ContainsKey(type) ? type : null;
    }

    public static FileKind KindOf(string normalizedType) => Allowed[normalizedType].Kind;

    /// <summary>Whether the first bytes of the object match the declared (normalized) type.</summary>
    public static bool Matches(string normalizedType, ReadOnlySpan<byte> head) =>
        Allowed.TryGetValue(normalizedType, out var entry) && FileSignature.Detect(head) == entry.Signature;

    /// <summary>Shown in the browser (inline) rather than downloaded: media and PDF.</summary>
    public static bool IsInline(string normalizedType) => KindOf(normalizedType) is not FileKind.Office;
}

/// <summary>Who may upload what, which kinds, and up to which size (docs/10-hoc-tap-va-danh-gia.md, 10.3).</summary>
public static class UploadPolicy
{
    public const long MB = 1024 * 1024;

    private sealed record Rule(IReadOnlySet<FileKind> Kinds, long StaffMax, long LearnerMax);

    private static readonly Dictionary<FilePurpose, Rule> Rules = new()
    {
        [FilePurpose.Avatar] = new(new HashSet<FileKind> { FileKind.Image }, 2 * MB, 2 * MB),
        [FilePurpose.QuestionMedia] = new(new HashSet<FileKind> { FileKind.Image, FileKind.Audio, FileKind.Video, FileKind.Pdf }, 50 * MB, 0),
        [FilePurpose.Material] = new(Enum.GetValues<FileKind>().ToHashSet(), 100 * MB, 0),
        [FilePurpose.Submission] = new(Enum.GetValues<FileKind>().ToHashSet(), 50 * MB, 25 * MB),
        [FilePurpose.ChatAttachment] = new(Enum.GetValues<FileKind>().ToHashSet(), 10 * MB, 10 * MB),
    };

    /// <summary>Without a virus scanner, learners may only send kinds that cannot carry macros or exploits as easily.</summary>
    private static readonly IReadOnlySet<FileKind> LearnerKindsWithoutScanner = new HashSet<FileKind> { FileKind.Image, FileKind.Pdf, FileKind.Audio };

    /// <summary>Largest object any purpose accepts (the signed PUT endpoint and the reverse proxy allow this much).</summary>
    public static long MaxBytes => Rules.Values.Max(r => Math.Max(r.StaffMax, r.LearnerMax));

    /// <summary>Images are rewritten without their metadata in memory, so they stay small whatever the purpose allows.</summary>
    public const long MaxImageBytes = 20 * MB;

    public static long MaxSize(FilePurpose purpose, UploaderKind uploader) =>
        uploader == UploaderKind.Staff ? Rules[purpose].StaffMax : Rules[purpose].LearnerMax;

    /// <summary>The normalized content type when <paramref name="uploader"/> may upload this file, else the error.</summary>
    public static Result<string> Check(FilePurpose purpose, UploaderKind uploader, string? contentType, long size, bool scannerEnabled)
    {
        if (!Rules.TryGetValue(purpose, out var rule))
        {
            return Error.Validation("content.purpose_invalid");
        }

        var max = MaxSize(purpose, uploader);
        if (max == 0)
        {
            return Error.Forbidden("content.purpose_forbidden");
        }

        var type = FileTypes.Normalize(contentType);
        if (type is null || !rule.Kinds.Contains(FileTypes.KindOf(type))
            || (uploader == UploaderKind.Learner && !scannerEnabled && !LearnerKindsWithoutScanner.Contains(FileTypes.KindOf(type))))
        {
            return Error.Validation("content.type_not_allowed");
        }

        if (size <= 0)
        {
            return Error.Validation("content.file_empty");
        }

        if (FileTypes.KindOf(type) == FileKind.Image)
        {
            max = Math.Min(max, MaxImageBytes);
        }

        if (size > max)
        {
            return Error.Validation("content.file_too_large", new Dictionary<string, object?> { ["maxMb"] = max / MB });
        }

        return type;
    }
}

/// <summary>Recognizes a file family from its first bytes (the declared type of a browser is not trusted).</summary>
public static class FileSignature
{
    public const int HeadLength = 512;

    public const string Jpeg = "jpeg";
    public const string Png = "png";
    public const string Gif = "gif";
    public const string Webp = "webp";
    public const string Mp3 = "mp3";
    public const string Mp4 = "mp4";
    public const string Ogg = "ogg";
    public const string Wav = "wav";
    public const string Ebml = "ebml";
    public const string Pdf = "pdf";
    public const string Zip = "zip";
    public const string Ole = "ole";

    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return Jpeg;
        }

        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return Png;
        }

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return Gif;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8))
        {
            var format = head.Slice(8, 4);
            return format.SequenceEqual("WEBP"u8) ? Webp : format.SequenceEqual("WAVE"u8) ? Wav : null;
        }

        if (head.StartsWith("%PDF-"u8))
        {
            return Pdf;
        }

        if (head.StartsWith("OggS"u8))
        {
            return Ogg;
        }

        if (head.StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
        {
            return Ebml;
        }

        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return Mp4;
        }

        if (head.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }))
        {
            return Zip;
        }

        if (head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            return Ole;
        }

        if (head.StartsWith("ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
        {
            return Mp3;
        }

        return null;
    }
}
