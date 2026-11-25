using System.Globalization;
using Konnyaku.Billing.Application;
using Konnyaku.Billing.Domain;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Konnyaku.Billing.Infrastructure.Receipts;

/// <summary>
/// A5 landscape receipt / refund voucher drawn with PDFsharp (MIT). Fonts are embedded in the assembly, so the output is
/// the same on every server. Labels come in Vietnamese or English; the amount in words is always Vietnamese.
/// </summary>
internal sealed class ReceiptPdfRenderer : IReceiptPdfRenderer
{
    private const string Family = "BeVietnamPro";
    private const double Margin = 34; // ≈ 12 mm

    public byte[] Render(ReceiptView receipt, string locale, string stamp)
    {
        EmbeddedFonts.EnsureRegistered();
        var l = locale == "en" ? Labels.En : Labels.Vi;
        var culture = CultureInfo.GetCultureInfo(locale == "en" ? "en-US" : "vi-VN");
        var refund = receipt.Kind == DocumentKinds.RefundVoucher;

        using var document = new PdfDocument();
        document.Info.Title = receipt.Code;
        document.Info.Creator = "Konnyaku";
        var page = document.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(148);
        using var gfx = XGraphics.FromPdfPage(page);
        var text = new XTextFormatter(gfx);
        var width = page.Width.Point - (2 * Margin);

        var regular = new XFont(Family, 9.5, XFontStyleEx.Regular);
        var bold = new XFont(Family, 9.5, XFontStyleEx.Bold);
        var small = new XFont(Family, 7.5, XFontStyleEx.Regular);
        var title = new XFont(Family, 16, XFontStyleEx.Bold);
        var muted = new XSolidBrush(XColor.FromArgb(110, 110, 120));

        // Header: center and branch on the left, number and date on the right.
        var issued = TimeZones.ToLocal(receipt.IssuedAt, receipt.TimeZone);
        gfx.DrawString(receipt.CenterName, bold, XBrushes.Black, new XRect(Margin, Margin, width * 0.6, 14), XStringFormats.TopLeft);
        if (Localized(receipt.BranchName, locale) is { } branch)
        {
            gfx.DrawString($"{l.Branch}: {branch}", regular, muted, new XRect(Margin, Margin + 14, width * 0.6, 14), XStringFormats.TopLeft);
        }

        gfx.DrawString($"{l.Number}: {receipt.Code}", bold, XBrushes.Black, new XRect(Margin, Margin, width, 14), XStringFormats.TopRight);
        gfx.DrawString($"{l.Date}: {issued:dd/MM/yyyy HH:mm}", regular, muted, new XRect(Margin, Margin + 14, width, 14), XStringFormats.TopRight);

        var y = Margin + 44;
        gfx.DrawString(refund ? l.RefundTitle : l.ReceiptTitle, title, XBrushes.Black, new XRect(Margin, y, width, 22), XStringFormats.TopCenter);
        y += 34;

        void Row(string label, string? value, XFont? font = null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            gfx.DrawString(label, regular, muted, new XRect(Margin, y, 150, 14), XStringFormats.TopLeft);
            var valueRect = new XRect(Margin + 150, y, width - 150, 28);
            text.DrawString(value, font ?? regular, XBrushes.Black, valueRect, XStringFormats.TopLeft);
            y += gfx.MeasureString(value, font ?? regular).Width > width - 150 ? 28 : 17;
        }

        var package = string.Join(" · ", new[] { receipt.PackageCode, Localized(receipt.PackageName, locale) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (receipt.ClassCode is { } classCode)
        {
            package += $" ({l.Class} {classCode})";
        }

        Row(refund ? l.Recipient : l.Payer, receipt.StudentName, bold);
        Row(l.Package, package);
        Row(refund ? l.RefundReason : l.Purpose, refund ? receipt.Reason : l.TuitionFor);
        Row(l.Amount, Money(receipt.Amount, receipt.Currency, culture), bold);
        Row(l.InWords, receipt.AmountInWords);
        var method = l.Methods.GetValueOrDefault(receipt.Method, receipt.Method);
        Row(l.Method, receipt.Reference is { } reference ? $"{method} · {reference}" : method);
        if (refund)
        {
            Row(l.CreditsReturned, string.Format(culture, l.CreditsValue, receipt.PaidCreditsReturned ?? 0, receipt.BonusCreditsForfeited ?? 0));
        }

        // Signatures.
        y = Math.Max(y + 10, page.Height.Point - Margin - 92);
        var half = width / 2;
        gfx.DrawString(refund ? l.ReceivedBy : l.PaidBy, bold, XBrushes.Black, new XRect(Margin, y, half, 14), XStringFormats.TopCenter);
        gfx.DrawString(refund ? l.PaidOutBy : l.CollectedBy, bold, XBrushes.Black, new XRect(Margin + half, y, half, 14), XStringFormats.TopCenter);
        gfx.DrawString(l.SignHint, small, muted, new XRect(Margin, y + 13, half, 12), XStringFormats.TopCenter);
        gfx.DrawString(l.SignHint, small, muted, new XRect(Margin + half, y + 13, half, 12), XStringFormats.TopCenter);
        gfx.DrawString(receipt.StudentName, regular, XBrushes.Black, new XRect(Margin, y + 58, half, 14), XStringFormats.TopCenter);
        gfx.DrawString(receipt.IssuedByName ?? string.Empty, regular, XBrushes.Black, new XRect(Margin + half, y + 58, half, 14), XStringFormats.TopCenter);

        // Footer: who printed it (masked, from the signed-in identity) and when.
        var footerY = page.Height.Point - Margin + 6;
        gfx.DrawLine(new XPen(XColor.FromArgb(210, 210, 215), 0.5), Margin, footerY - 4, Margin + width, footerY - 4);
        gfx.DrawString($"{l.PrintedBy}: {stamp}", small, muted, new XRect(Margin, footerY, width, 10), XStringFormats.TopLeft);
        gfx.DrawString(l.Confidential, small, muted, new XRect(Margin, footerY, width, 10), XStringFormats.TopRight);

        if (receipt.Voided)
        {
            var voided = new XFont(Family, 54, XFontStyleEx.Bold);
            var state = gfx.Save();
            gfx.RotateAtTransform(-20, new XPoint(page.Width.Point / 2, page.Height.Point / 2));
            gfx.DrawString(l.Voided, voided, new XSolidBrush(XColor.FromArgb(70, 220, 38, 38)),
                new XRect(0, 0, page.Width.Point, page.Height.Point), XStringFormats.Center);
            gfx.Restore(state);
        }

        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static string? Localized(IReadOnlyDictionary<string, string>? values, string locale)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return values.TryGetValue(locale, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : values.TryGetValue("vi", out var vi) && !string.IsNullOrWhiteSpace(vi) ? vi : values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string Money(decimal amount, string currency, CultureInfo culture)
    {
        var digits = currency is "VND" or "JPY" or "KRW" ? 0 : 2;
        return $"{amount.ToString($"N{digits}", culture)} {currency}";
    }

    private sealed record Labels(
        string ReceiptTitle, string RefundTitle, string Number, string Date, string Branch, string Payer, string Recipient, string Package,
        string Class, string Purpose, string TuitionFor, string RefundReason, string Amount, string InWords, string Method, string CreditsReturned,
        string CreditsValue, string PaidBy, string CollectedBy, string ReceivedBy, string PaidOutBy, string SignHint, string PrintedBy,
        string Confidential, string Voided, IReadOnlyDictionary<string, string> Methods)
    {
        public static readonly Labels Vi = new(
            "PHIẾU THU", "PHIẾU CHI (HOÀN TIỀN)", "Số", "Ngày", "Cơ sở", "Họ tên người nộp", "Họ tên người nhận", "Gói học", "lớp",
            "Lý do nộp", "Thanh toán học phí", "Lý do hoàn", "Số tiền", "Bằng chữ", "Hình thức", "Buổi học",
            "Hoàn {0:0.##} buổi trả phí chưa dùng · {1:0.##} buổi tặng không hoàn", "Người nộp tiền", "Người thu tiền", "Người nhận tiền",
            "Người chi tiền", "(Ký, ghi rõ họ tên)", "In bởi", "Tài liệu có dữ liệu cá nhân — không chia sẻ", "ĐÃ HỦY",
            new Dictionary<string, string> { ["cash"] = "Tiền mặt", ["bank_transfer"] = "Chuyển khoản", ["card"] = "Thẻ" });

        public static readonly Labels En = new(
            "RECEIPT", "REFUND VOUCHER", "No.", "Date", "Branch", "Received from", "Paid to", "Package", "class",
            "For", "Tuition payment", "Refund reason", "Amount", "In words (Vietnamese)", "Method", "Lessons",
            "{0:0.##} unused paid lessons refunded · {1:0.##} bonus lessons not refundable", "Payer", "Collected by", "Recipient",
            "Paid out by", "(Signature and full name)", "Printed by", "Contains personal data — do not share", "VOID",
            new Dictionary<string, string> { ["cash"] = "Cash", ["bank_transfer"] = "Bank transfer", ["card"] = "Card" });
    }
}

/// <summary>Serves the embedded Be Vietnam Pro faces to PDFsharp (registered once per process).</summary>
internal sealed class EmbeddedFonts : IFontResolver
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (Gate)
        {
            if (!_registered)
            {
                GlobalFontSettings.FontResolver ??= new EmbeddedFonts();
                _registered = true;
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "BeVietnamPro-Bold" : "BeVietnamPro-Regular");

    public byte[]? GetFont(string faceName)
    {
        using var stream = typeof(EmbeddedFonts).Assembly.GetManifestResourceStream($"Konnyaku.Billing.Fonts.{faceName}.ttf");
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
