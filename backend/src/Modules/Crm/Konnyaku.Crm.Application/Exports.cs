using System.Globalization;
using System.Text;
using Konnyaku.Application.Authorization;
using Konnyaku.Crm.Domain;
using Konnyaku.Organization.Contracts;
using Konnyaku.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Konnyaku.Crm.Application;

/// <summary>Personal data rules a deployment may tune (<c>Privacy:*</c> in configuration).</summary>
public sealed record PrivacySettings
{
    /// <summary>A CSV export or print holds at most this many rows; a larger list must be filtered first.</summary>
    public int ExportMaxRows { get; init; } = 2000;

    /// <summary>Exports and prints one member may make in 24 hours.</summary>
    public int ExportsPerDay { get; init; } = 20;

    /// <summary>Leads never converted are anonymized this many months after their last change (0 turns the job off).</summary>
    public int LeadRetentionMonths { get; init; } = 24;

    /// <summary>Deadline of a data subject request after it is received (Nghị định 13/2023: 72 hours).</summary>
    public int RequestDueHours { get; init; } = 72;
}

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>A printed row: names and branch, contact details masked (paper leaves no trace once it leaves the building).</summary>
public sealed record PrintRow(Guid Id, string Name, string? BirthYear, string? Phone, string? Email, Guid? BranchId, string Status);

/// <summary>The rows to print and the stamp printed under them (operator masked, server time).</summary>
public sealed record PrintView(IReadOnlyList<PrintRow> Rows, int Total, string PrintedBy, DateTimeOffset PrintedAt);

/// <summary>
/// Lists leaving the system (CSV) or printed: the server is the only place that enforces who may, how many rows, how
/// often, and that each file carries who made it. Every export and print is logged with its filters and row count.
/// </summary>
public sealed class ExportHandlers(
    ICrmDbContext db, CrmListQueries lists, PersonalDataLog log, IOrganizationApi organization, PrivacySettings settings, TimeProvider clock)
{
    public async Task<Result<ExportFile>> ExportStudentsAsync(StudentFilter filter, string? locale, RequestActor actor, CancellationToken ct)
    {
        var rows = await StudentRowsAsync(filter, actor, ct);
        if (!rows.IsSuccess)
        {
            return rows.Error!;
        }

        var text = Labels.For(locale);
        var branches = await BranchNamesAsync(rows.Value.Select(s => s.HomeBranchId), locale, ct);
        var csv = new CsvWriter();
        csv.Row(text["name"], text["birthYear"], text["guardian"], text["phone"], text["email"], text["branch"], text["status"], text["account"], text["createdAt"]);
        foreach (var s in rows.Value)
        {
            csv.Row(s.FullName, s.BirthYear?.ToString(CultureInfo.InvariantCulture), s.GuardianName, s.Phone, s.Email,
                s.HomeBranchId is { } b ? branches.GetValueOrDefault(b) : null, text[s.IsActive ? "active" : "inactive"],
                text[s.UserId is null ? "no" : "yes"], s.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return await FinishAsync(csv, text, "students", filter.ForLog(), rows.Value.Count, actor, ct);
    }

    public async Task<Result<ExportFile>> ExportLeadsAsync(LeadFilter filter, string? locale, RequestActor actor, CancellationToken ct)
    {
        var rows = await LeadRowsAsync(filter, actor, ct);
        if (!rows.IsSuccess)
        {
            return rows.Error!;
        }

        var text = Labels.For(locale);
        var branches = await BranchNamesAsync(rows.Value.Select(l => l.PreferredBranchId), locale, ct);
        var csv = new CsvWriter();
        csv.Row(text["name"], text["birthYear"], text["guardian"], text["phone"], text["email"], text["language"], text["branch"],
            text["status"], text["goal"], text["createdAt"]);
        foreach (var l in rows.Value)
        {
            csv.Row(l.StudentName, l.BirthYear?.ToString(CultureInfo.InvariantCulture), l.GuardianName, l.Phone, l.Email, l.LanguageCode,
                l.PreferredBranchId is { } b ? branches.GetValueOrDefault(b) : null, text.GetValueOrDefault("lead." + l.Status, l.Status.ToString()),
                l.Goal, l.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return await FinishAsync(csv, text, "leads", filter.ForLog(), rows.Value.Count, actor, ct);
    }

    public async Task<Result<PrintView>> PrintStudentsAsync(StudentFilter filter, RequestActor actor, CancellationToken ct)
    {
        var rows = await StudentRowsAsync(filter, actor, ct);
        if (!rows.IsSuccess)
        {
            return rows.Error!;
        }

        await log.WriteAsync(PersonalDataAction.Print, "students", null, actor, ct, filter: filter.ForLog(), rowCount: rows.Value.Count);
        return new PrintView(
            rows.Value.Select(s => new PrintRow(s.Id, s.FullName, PersonalDataMask.BirthYear(s.BirthYear), PersonalDataMask.Phone(s.Phone),
                PersonalDataMask.Email(s.Email), s.HomeBranchId, s.IsActive ? "Active" : "Inactive")).ToList(),
            rows.Value.Count, PersonalDataMask.Name(actor.DisplayName) ?? "—", clock.GetUtcNow());
    }

    public async Task<Result<PrintView>> PrintLeadsAsync(LeadFilter filter, RequestActor actor, CancellationToken ct)
    {
        var rows = await LeadRowsAsync(filter, actor, ct);
        if (!rows.IsSuccess)
        {
            return rows.Error!;
        }

        await log.WriteAsync(PersonalDataAction.Print, "leads", null, actor, ct, filter: filter.ForLog(), rowCount: rows.Value.Count);
        return new PrintView(
            rows.Value.Select(l => new PrintRow(l.Id, l.StudentName, PersonalDataMask.BirthYear(l.BirthYear), PersonalDataMask.Phone(l.Phone),
                PersonalDataMask.Email(l.Email), l.PreferredBranchId, l.Status.ToString())).ToList(),
            rows.Value.Count, PersonalDataMask.Name(actor.DisplayName) ?? "—", clock.GetUtcNow());
    }

    private async Task<Result<List<StudentProfile>>> StudentRowsAsync(StudentFilter filter, RequestActor actor, CancellationToken ct)
    {
        // A branch manager exports the students of their branches only.
        if (await lists.LeadScopeAsync(Permissions.PersonalDataExport, ct) is not { } scope)
        {
            return Error.Forbidden("access.forbidden");
        }

        if (await LimitAsync(actor, ct) is { } limited)
        {
            return limited;
        }

        var query = lists.StudentQuery(filter with { Ids = null }, scope.Count > 0 ? scope : null);
        var total = await query.CountAsync(ct);
        if (total > settings.ExportMaxRows)
        {
            return TooLarge(total);
        }

        return await query.OrderBy(s => s.FullName).ThenBy(s => s.Id).ToListAsync(ct);
    }

    private async Task<Result<List<Lead>>> LeadRowsAsync(LeadFilter filter, RequestActor actor, CancellationToken ct)
    {
        if (await lists.LeadScopeAsync(Permissions.PersonalDataExport, ct) is not { } exportScope ||
            await lists.LeadScopeAsync(Permissions.Leads, ct) is not { } leadScope)
        {
            return Error.Forbidden("access.forbidden");
        }

        if (await LimitAsync(actor, ct) is { } limited)
        {
            return limited;
        }

        // Both scopes apply: the leads the member may see, of the branches they may export.
        var scope = exportScope.Count == 0 ? leadScope : leadScope.Count == 0 ? exportScope : leadScope.Intersect(exportScope).ToList();
        if (exportScope.Count > 0 && leadScope.Count > 0 && scope.Count == 0)
        {
            return Error.Forbidden("access.forbidden");
        }

        var query = lists.LeadQuery(filter, scope);
        var total = await query.CountAsync(ct);
        if (total > settings.ExportMaxRows)
        {
            return TooLarge(total);
        }

        return await query.OrderBy(l => l.StudentName).ThenBy(l => l.Id).ToListAsync(ct);
    }

    private Error TooLarge(int total) => Error.Validation("crm.export_too_large",
        new Dictionary<string, object?> { ["limit"] = settings.ExportMaxRows, ["total"] = total });

    /// <summary>Too many exports by one member in a day looks like someone copying the database: stop and let an admin look.</summary>
    private async Task<Error?> LimitAsync(RequestActor actor, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-1);
        var recent = await db.PersonalDataAccesses.AsNoTracking().CountAsync(a =>
            a.ActorId == actor.UserId && (a.Action == PersonalDataAction.Export || a.Action == PersonalDataAction.Print) && a.OccurredAt >= since, ct);
        return recent >= settings.ExportsPerDay
            ? Error.Conflict("crm.export_limit_reached", new Dictionary<string, object?> { ["limit"] = settings.ExportsPerDay })
            : null;
    }

    private async Task<Result<ExportFile>> FinishAsync(
        CsvWriter csv, IReadOnlyDictionary<string, string> text, string subject, IReadOnlyDictionary<string, string> filter, int count,
        RequestActor actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The stamp is the file's last line: who exported it (masked) and when, from the token and the server clock.
        csv.Row(string.Format(CultureInfo.InvariantCulture, text["stamp"], PersonalDataMask.Name(actor.DisplayName) ?? "—",
            now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), count));
        await log.WriteAsync(PersonalDataAction.Export, subject, null, actor, ct, filter: filter, rowCount: count);
        return new ExportFile($"{subject}-{now:yyyyMMdd-HHmm}.csv", "text/csv; charset=utf-8", csv.ToBytes());
    }

    private async Task<IReadOnlyDictionary<Guid, string>> BranchNamesAsync(IEnumerable<Guid?> ids, string? locale, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var id in ids.OfType<Guid>().Distinct())
        {
            if (await organization.GetBranchAsync(id, ct) is { } branch)
            {
                names[id] = branch.Name.GetValueOrDefault(locale ?? "vi") ?? branch.Name.GetValueOrDefault("vi") ?? branch.Code;
            }
        }

        return names;
    }

    /// <summary>Column titles of the files (the file is a document for people, in the exporting member's language).</summary>
    private static class Labels
    {
        private static readonly IReadOnlyDictionary<string, string> Vi = new Dictionary<string, string>
        {
            ["name"] = "Họ tên", ["birthYear"] = "Năm sinh", ["guardian"] = "Người giám hộ", ["phone"] = "Điện thoại", ["email"] = "Email",
            ["branch"] = "Cơ sở", ["status"] = "Trạng thái", ["account"] = "Tài khoản", ["createdAt"] = "Ngày tạo", ["language"] = "Ngôn ngữ",
            ["goal"] = "Mục tiêu", ["active"] = "Đang học", ["inactive"] = "Đã nghỉ", ["yes"] = "Có", ["no"] = "Không",
            ["lead.New"] = "Mới", ["lead.Contacted"] = "Đã liên hệ", ["lead.Converted"] = "Đã chuyển đổi", ["lead.Lost"] = "Không theo",
            ["stamp"] = "Xuất bởi {0} lúc {1} UTC · {2} dòng · Dữ liệu cá nhân: chỉ dùng cho công việc, không chia sẻ",
        };

        private static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
        {
            ["name"] = "Name", ["birthYear"] = "Birth year", ["guardian"] = "Guardian", ["phone"] = "Phone", ["email"] = "Email",
            ["branch"] = "Branch", ["status"] = "Status", ["account"] = "Account", ["createdAt"] = "Created", ["language"] = "Language",
            ["goal"] = "Goal", ["active"] = "Active", ["inactive"] = "Inactive", ["yes"] = "Yes", ["no"] = "No",
            ["lead.New"] = "New", ["lead.Contacted"] = "Contacted", ["lead.Converted"] = "Converted", ["lead.Lost"] = "Lost",
            ["stamp"] = "Exported by {0} at {1} UTC · {2} rows · Personal data: for work only, do not share",
        };

        public static IReadOnlyDictionary<string, string> For(string? locale) => locale == "en" ? En : Vi;
    }
}

/// <summary>RFC 4180 CSV, UTF-8 with BOM (Excel), cells that start like a formula neutralized (CSV injection).</summary>
public sealed class CsvWriter
{
    private readonly StringBuilder _text = new();

    public void Row(params string?[] cells)
    {
        _text.AppendJoin(',', cells.Select(Cell));
        _text.Append("\r\n");
    }

    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_text.ToString())];

    public override string ToString() => _text.ToString();

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
