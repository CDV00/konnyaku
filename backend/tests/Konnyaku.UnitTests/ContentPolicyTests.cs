using System.Buffers.Binary;
using System.Text;
using Konnyaku.Content.Domain;
using Konnyaku.Infrastructure.Storage;

namespace Konnyaku.UnitTests;

public class ContentPolicyTests
{
    [Theory]
    [InlineData("image/jpg", "image/jpeg")]
    [InlineData("Image/PNG; charset=binary", "image/png")]
    [InlineData("audio/mp3", "audio/mpeg")]
    [InlineData("audio/x-m4a", "audio/mp4")]
    [InlineData("image/svg+xml", null)]
    [InlineData("text/html", null)]
    [InlineData("application/x-msdownload", null)]
    [InlineData("application/zip", null)]
    [InlineData("", null)]
    public void Only_allow_listed_types_are_accepted(string declared, string? expected) =>
        FileTypes.Normalize(declared).ShouldBe(expected);

    [Fact]
    public void Limits_depend_on_purpose_and_uploader()
    {
        UploadPolicy.Check(FilePurpose.Material, UploaderKind.Learner, "application/pdf", 10, true).Error!.Code.ShouldBe("content.purpose_forbidden");
        UploadPolicy.Check(FilePurpose.Material, UploaderKind.Staff, "application/pdf", 100 * UploadPolicy.MB, false).IsSuccess.ShouldBeTrue();
        UploadPolicy.Check(FilePurpose.Material, UploaderKind.Staff, "application/pdf", (100 * UploadPolicy.MB) + 1, false).Error!.Code
            .ShouldBe("content.file_too_large");
        UploadPolicy.Check(FilePurpose.Submission, UploaderKind.Learner, "application/pdf", 26 * UploadPolicy.MB, false).Error!.Code
            .ShouldBe("content.file_too_large");
        UploadPolicy.Check(FilePurpose.Avatar, UploaderKind.Staff, "application/pdf", 10, false).Error!.Code.ShouldBe("content.type_not_allowed");
        UploadPolicy.Check(FilePurpose.Material, UploaderKind.Staff, "image/png", 21 * UploadPolicy.MB, false).Error!.Code.ShouldBe("content.file_too_large");
        UploadPolicy.Check(FilePurpose.Submission, UploaderKind.Learner, "application/pdf", 0, false).Error!.Code.ShouldBe("content.file_empty");
        // Learners send Office files and video only when a virus scanner checks them.
        UploadPolicy.Check(FilePurpose.Submission, UploaderKind.Learner, "video/mp4", 10, false).Error!.Code.ShouldBe("content.type_not_allowed");
        UploadPolicy.Check(FilePurpose.Submission, UploaderKind.Learner, "video/mp4", 10, true).IsSuccess.ShouldBeTrue();
        UploadPolicy.Check(FilePurpose.Submission, UploaderKind.Learner, "audio/webm", 10, false).Value.ShouldBe("audio/webm");
    }

    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, true)]
    [InlineData("image/png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, false)]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, true)]
    [InlineData("audio/mpeg", new byte[] { 0x49, 0x44, 0x33, 0x04 }, true)]
    [InlineData("audio/mpeg", new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, true)]
    [InlineData("video/mp4", new byte[] { 0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true)]
    [InlineData("application/msword", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, false)]
    [InlineData("application/pdf", new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, false)]
    public void Magic_bytes_must_match_the_declared_type(string type, byte[] head, bool matches) =>
        FileTypes.Matches(type, head).ShouldBe(matches);

    [Fact]
    public void Png_text_and_exif_chunks_are_removed()
    {
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        png.AddRange(Chunk("IHDR", new byte[13]));
        png.AddRange(Chunk("eXIf", Encoding.ASCII.GetBytes("MM\0*GPS-SECRET")));
        png.AddRange(Chunk("tEXt", Encoding.ASCII.GetBytes("Author\0Nguyen Van A")));
        png.AddRange(Chunk("IDAT", [1, 2, 3]));
        png.AddRange(Chunk("IEND", []));

        var stripped = ImageMetadata.Strip([.. png], "image/png")!;
        var text = Encoding.ASCII.GetString(stripped);
        text.ShouldNotContain("GPS-SECRET");
        text.ShouldNotContain("Nguyen");
        text.ShouldContain("IHDR");
        text.ShouldContain("IDAT");
        text.ShouldEndWith("IEND" + "\0\0\0\0");
    }

    [Fact]
    public void Webp_exif_and_xmp_chunks_are_removed_and_flags_cleared()
    {
        var vp8x = new byte[10];
        vp8x[0] = 0x0C;
        var chunks = new List<byte>();
        chunks.AddRange(RiffChunk("VP8X", vp8x));
        chunks.AddRange(RiffChunk("VP8 ", [1, 2, 3]));
        chunks.AddRange(RiffChunk("EXIF", Encoding.ASCII.GetBytes("GPS-SECRET")));
        chunks.AddRange(RiffChunk("XMP ", Encoding.ASCII.GetBytes("<x:xmpmeta/>")));
        var webp = new List<byte>();
        webp.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        webp.AddRange(BitConverter.GetBytes((uint)(4 + chunks.Count)));
        webp.AddRange(Encoding.ASCII.GetBytes("WEBP"));
        webp.AddRange(chunks);

        var stripped = ImageMetadata.Strip([.. webp], "image/webp")!;
        Encoding.ASCII.GetString(stripped).ShouldNotContain("GPS-SECRET");
        Encoding.ASCII.GetString(stripped).ShouldNotContain("xmpmeta");
        BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(4)).ShouldBe((uint)(stripped.Length - 8));
        stripped[20].ShouldBe((byte)0);
    }

    [Fact]
    public void Broken_images_are_rejected()
    {
        ImageMetadata.Strip([0xFF, 0xD8, 0x00, 0x00], "image/jpeg").ShouldBeNull();
        ImageMetadata.Strip([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 99], "image/png").ShouldBeNull();
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..", null)]
    [InlineData("C:\\Users\\me\\bai tap.pdf", "bai tap.pdf")]
    [InlineData("a\u0000b.pdf", "ab.pdf")]
    [InlineData("  ", null)]
    public void File_names_lose_paths_and_control_characters(string name, string? expected) =>
        StoredFile.CleanFileName(name).ShouldBe(expected);

    [Fact]
    public void Content_disposition_keeps_utf8_names_and_escapes_quotes()
    {
        ContentDisposition.Of("Bài \"1\".pdf", inline: false)
            .ShouldBe("attachment; filename=\"B_i _1_.pdf\"; filename*=UTF-8''B%C3%A0i%20%221%22.pdf");
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] RiffChunk(string fourCc, byte[] data)
    {
        var padded = data.Length + (data.Length & 1);
        var chunk = new byte[8 + padded];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }
}
