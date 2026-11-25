using Konnyaku.Academic.Contracts;
using Konnyaku.Application.Authorization;
using Konnyaku.Application.Tenancy;
using Konnyaku.Billing.Domain;
using Konnyaku.Crm.Contracts;
using Konnyaku.Organization.Contracts;
using Konnyaku.Platform.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Billing.Application;

/// <summary>
/// Content of a receipt (phiếu thu, for a payment) or refund voucher (phiếu chi, for a refund). It carries the student's
/// name only — no contact details, birth year or account.
/// </summary>
public sealed record ReceiptView(
    string Kind, Guid Id, Guid OrderId, long Number, string Code, DateTimeOffset IssuedAt, string TimeZone, bool Voided, string CenterName,
    IReadOnlyDictionary<string, string>? BranchName, string StudentName, string PackageCode, IReadOnlyDictionary<string, string> PackageName,
    string? ClassCode, decimal Amount, string Currency, string AmountInWords, string Method, string? Reference, string? Reason,
    string? IssuedByName, decimal OrderAmount, decimal? PaidCreditsReturned, decimal? BonusCreditsForfeited);

/// <summary>A payment or refund in a student's own history (<c>/learn</c>).</summary>
public sealed record MyPaymentView(
    string Kind, Guid Id, string Code, DateTimeOffset At, decimal Amount, string Currency, string Method, bool Voided, Guid OrderId,
    string StudentName, string PackageCode, IReadOnlyDictionary<string, string> PackageName);

public sealed record ReceiptPrintView(Guid Id, string Action, string Audience, string? ActorName, DateTimeOffset OccurredAt);

public sealed record ReceiptFile(byte[] Content, string FileName);

/// <summary>Limits on printing a receipt (each print or download is one): per document, per person, per 24 hours.</summary>
public sealed record ReceiptSettings(int PrintsPerDay = 20);

/// <summary>Draws a receipt as a PDF; <paramref name="stamp"/> is the masked operator line printed in the footer.</summary>
public interface IReceiptPdfRenderer
{
    byte[] Render(ReceiptView receipt, string locale, string stamp);
}

public static class ReceiptCodes
{
    /// <summary><c>PT-000123</c> for a receipt (phiếu thu), <c>PC-000045</c> for a refund voucher (phiếu chi).</summary>
    public static string For(string kind, long number) => $"{(kind == DocumentKinds.RefundVoucher ? "PC" : "PT")}-{number:D6}";
}

/// <summary>
/// The single place receipts are read and printed. Every PDF goes through <see cref="PrintAsync"/>: it checks who may see
/// the document (the branch for staff, ownership for learners), enforces <see cref="ReceiptSettings.PrintsPerDay"/>,
/// writes the print log and stamps the masked name of the person printing — all server-side.
/// </summary>
public sealed class ReceiptService(
    IBillingDbContext db, ICrmApi crm, IAcademicApi academic, IOrganizationApi organization, IPlatformApi platform, ITenantContext tenant,
    IAccessControl access, IReceiptPdfRenderer renderer, ReceiptSettings settings, TimeProvider clock)
{
    public const string Staff = "staff";
    public const string Learn = "learn";

    public async Task<Result<ReceiptView>> GetAsync(string kind, Guid id, CancellationToken ct)
    {
        var document = await LoadAsync(kind, id, ct);
        if (document is null)
        {
            return Error.NotFound("billing.receipt_not_found");
        }

        return await access.EnsureAsync(PermissionFor(kind), ct, document.Order.BranchId) is { } forbidden
            ? forbidden
            : await BuildAsync(document, ct);
    }

    public async Task<IReadOnlyList<ReceiptPrintView>> ListPrintsAsync(Guid documentId, CancellationToken ct) =>
        await db.ReceiptPrints.AsNoTracking().Where(p => p.DocumentId == documentId).OrderByDescending(p => p.OccurredAt).Take(100)
            .Select(p => new ReceiptPrintView(p.Id, p.Action, p.Audience, p.ActorName, p.OccurredAt))
            .ToListAsync(ct);

    /// <summary>
    /// Renders the PDF for staff (<paramref name="learnerUserId"/> null: branch permission) or for a learner (the order
    /// must belong to one of their student profiles).
    /// </summary>
    public async Task<Result<ReceiptFile>> PrintAsync(string kind, Guid id, bool download, string? locale, RequestActor actor, Guid? learnerUserId,
        CancellationToken ct)
    {
        var document = await LoadAsync(kind, id, ct);
        if (document is null)
        {
            return Error.NotFound("billing.receipt_not_found");
        }

        if (learnerUserId is { } userId)
        {
            var students = await crm.ListStudentsOfUserAsync(userId, ct);
            if (students.All(s => s.Id != document.Order.StudentId))
            {
                return Error.NotFound("billing.receipt_not_found");
            }
        }
        else if (await access.EnsureAsync(PermissionFor(kind), ct, document.Order.BranchId) is { } forbidden)
        {
            return forbidden;
        }

        var now = clock.GetUtcNow();
        var since = now.AddDays(-1);
        var recent = await db.ReceiptPrints.CountAsync(p => p.DocumentId == id && p.ActorId == actor.UserId && p.OccurredAt >= since, ct);
        if (recent >= settings.PrintsPerDay)
        {
            return Error.Conflict("billing.receipt_print_limit", new Dictionary<string, object?> { ["limit"] = settings.PrintsPerDay });
        }

        var receipt = await BuildAsync(document, ct);
        db.ReceiptPrints.Add(new ReceiptPrintLog(kind, id, document.Number, download ? ReceiptPrintLog.Download : ReceiptPrintLog.Print,
            learnerUserId is null ? Staff : Learn, actor.UserId, actor.Name, actor.ClientIp, now));
        await db.SaveChangesAsync(ct);

        var language = locale is "en" ? "en" : "vi";
        var at = TimeZones.ToLocal(now, receipt.TimeZone);
        var stamp = $"{Masking.Name(actor.Name)} · {at:HH:mm dd/MM/yyyy}";
        return new ReceiptFile(renderer.Render(receipt, language, stamp), $"{receipt.Code}.pdf");
    }

    /// <summary>Payments and refunds of the learner's student profiles, newest first.</summary>
    public async Task<IReadOnlyList<MyPaymentView>> ListMineAsync(Guid userId, CancellationToken ct)
    {
        var students = await crm.ListStudentsOfUserAsync(userId, ct);
        if (students.Count == 0)
        {
            return [];
        }

        var studentIds = students.Select(s => s.Id).ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => studentIds.Contains(o.StudentId)).ToListAsync(ct);
        var orderIds = orders.Select(o => o.Id).ToList();
        var packageIds = orders.Select(o => o.PackageId).Distinct().ToList();
        var packages = await db.Packages.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var payments = await db.Payments.AsNoTracking().Where(p => orderIds.Contains(p.OrderId)).ToListAsync(ct);
        var refunds = await db.Refunds.AsNoTracking().Where(r => orderIds.Contains(r.OrderId)).ToListAsync(ct);
        var orderById = orders.ToDictionary(o => o.Id);
        var nameById = students.ToDictionary(s => s.Id, s => s.FullName);

        MyPaymentView View(string kind, Guid id, long number, DateTimeOffset at, decimal amount, string method, bool voided, Guid orderId)
        {
            var order = orderById[orderId];
            var package = packages.GetValueOrDefault(order.PackageId);
            return new MyPaymentView(kind, id, ReceiptCodes.For(kind, number), at, amount, order.Currency, method, voided, orderId,
                nameById.GetValueOrDefault(order.StudentId, string.Empty), package?.Code ?? string.Empty,
                package?.Name.Values ?? new Dictionary<string, string>());
        }

        return payments.Select(p => View(DocumentKinds.Receipt, p.Id, p.ReceiptNo, p.ReceivedAt, p.Amount, p.Method, p.VoidedAt is not null, p.OrderId))
            .Concat(refunds.Select(r => View(DocumentKinds.RefundVoucher, r.Id, r.ReceiptNo, r.RefundedAt, r.Amount, r.Method, false, r.OrderId)))
            .OrderByDescending(v => v.At)
            .ToList();
    }

    private static string PermissionFor(string kind) => kind == DocumentKinds.RefundVoucher ? Permissions.Refunds : Permissions.Payments;

    private sealed record Document(string Kind, Guid Id, long Number, Order Order, Payment? Payment, Refund? Refund);

    private async Task<Document?> LoadAsync(string kind, Guid id, CancellationToken ct)
    {
        Payment? payment = null;
        Refund? refund = null;
        Guid orderId;
        long number;
        switch (kind)
        {
            case DocumentKinds.Receipt:
                payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
                if (payment is null)
                {
                    return null;
                }

                (orderId, number) = (payment.OrderId, payment.ReceiptNo);
                break;
            case DocumentKinds.RefundVoucher:
                refund = await db.Refunds.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
                if (refund is null)
                {
                    return null;
                }

                (orderId, number) = (refund.OrderId, refund.ReceiptNo);
                break;
            default:
                return null;
        }

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId, ct);
        return new Document(kind, id, number, order, payment, refund);
    }

    private async Task<ReceiptView> BuildAsync(Document d, CancellationToken ct)
    {
        var order = d.Order;
        var center = tenant.TenantId is { } tenantId ? await platform.GetTenantAsync(tenantId, ct) : null;
        var branch = order.BranchId is { } branchId ? await organization.GetBranchAsync(branchId, ct) : null;
        var student = await crm.GetStudentAsync(order.StudentId, ct);
        var cls = order.ClassId is { } classId ? await academic.GetClassAsync(classId, ct) : null;
        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == order.PackageId, ct);
        var amount = d.Payment?.Amount ?? d.Refund!.Amount;
        return new ReceiptView(
            d.Kind, d.Id, order.Id, d.Number, ReceiptCodes.For(d.Kind, d.Number), d.Payment?.ReceivedAt ?? d.Refund!.RefundedAt,
            branch?.TimeZone ?? center?.TimeZone ?? "UTC", d.Payment?.VoidedAt is not null, center?.Name ?? string.Empty, branch?.Name,
            student?.FullName ?? string.Empty, package?.Code ?? string.Empty, package?.Name.Values ?? new Dictionary<string, string>(), cls?.Code,
            amount, order.Currency, VietnameseNumberWords.Amount(amount, order.Currency), d.Payment?.Method ?? d.Refund!.Method,
            d.Payment?.Reference ?? d.Refund?.Reference, d.Refund?.Reason, d.Payment?.ReceivedByName ?? d.Refund?.ApprovedByName, order.Amount,
            d.Refund?.PaidCreditsReturned, d.Refund?.BonusCreditsForfeited);
    }
}

/// <summary>Masks personal names for printed stamps and logs shown to others: <c>Nguyễn V** A**</c>.</summary>
public static class Masking
{
    public static string Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "—";
        }

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select((part, i) => i == 0 || part.Length <= 1 ? part : $"{part[0]}**"));
    }
}

/// <summary>Amounts in Vietnamese words as printed on receipts: "Một triệu năm trăm nghìn đồng".</summary>
public static class VietnameseNumberWords
{
    private static readonly string[] Digits = ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];
    private static readonly (int Scale, string Name)[] Scales = [(1_000_000, " triệu"), (1_000, " nghìn"), (1, "")];

    public static string Amount(decimal amount, string currency)
    {
        var whole = decimal.Truncate(Math.Abs(amount));
        var cents = (int)decimal.Round((Math.Abs(amount) - whole) * 100);
        var unit = currency.ToUpperInvariant() switch
        {
            "VND" => "đồng",
            "USD" => "đô la Mỹ",
            "EUR" => "euro",
            "JPY" => "yên",
            _ => currency.ToUpperInvariant(),
        };
        var text = $"{Words(whole)} {unit}";
        if (cents > 0)
        {
            text += $" {Words(cents)} xu";
        }

        if (amount < 0)
        {
            text = $"âm {text}";
        }

        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>A whole number in words; billions repeat ("một nghìn tỷ").</summary>
    public static string Words(decimal number) => number == 0 ? Digits[0] : Read(decimal.Truncate(number), false);

    private static string Read(decimal number, bool full)
    {
        var parts = new List<string>();
        var billions = decimal.Truncate(number / 1_000_000_000);
        if (billions > 0)
        {
            parts.Add($"{Read(billions, full)} tỷ");
            full = true;
        }

        var rest = (int)(number % 1_000_000_000);
        foreach (var (scale, name) in Scales)
        {
            var group = rest / scale % 1000;
            if (group == 0)
            {
                continue;
            }

            parts.Add(Hundreds(group, full) + name);
            full = true;
        }

        return string.Join(' ', parts);
    }

    /// <summary>0–999; <paramref name="full"/> reads leading zeros ("không trăm linh năm") when a higher group came first.</summary>
    private static string Hundreds(int n, bool full)
    {
        var hundred = n / 100;
        var ten = n / 10 % 10;
        var one = n % 10;
        var words = new List<string>();
        if (hundred > 0 || full)
        {
            words.Add($"{Digits[hundred]} trăm");
        }

        if (ten == 0)
        {
            if (one > 0 && words.Count > 0)
            {
                words.Add("linh");
            }
        }
        else if (ten == 1)
        {
            words.Add("mười");
        }
        else
        {
            words.Add($"{Digits[ten]} mươi");
        }

        if (one > 0)
        {
            words.Add(one switch
            {
                1 when ten > 1 => "mốt",
                4 when ten > 1 => "tư",
                5 when ten > 0 => "lăm",
                _ => Digits[one],
            });
        }

        return string.Join(' ', words);
    }
}
