using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Konnyaku.IntegrationTests;

/// <summary>Proof-of-work captcha of the public registration form: signed, expiring, single use, per tenant.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CaptchaTests(PostgresFixture db) : IAsyncDisposable
{
    private const string Password = "Admin-Pass-123";
    private readonly KonnyakuApiFactory _api = new(db, captcha: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task A_solved_challenge_is_accepted_once()
    {
        var (tenant, admin) = await TenantAsync("cap-ok");
        var visitor = _api.ClientFor(tenant.Slug);
        var solution = await SolveAsync(visitor);

        (await visitor.PostAsJsonAsync("/api/crm/public/leads", Form("0931000001", solution), Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var replay = await visitor.PostAsJsonAsync("/api/crm/public/leads", Form("0931000002", solution), Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await replay.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code.ShouldBe("crm.captcha_used");

        (await admin.GetItemsAsync<object>("/api/crm/leads", Ct))!.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_or_wrong_solutions_are_rejected()
    {
        var (tenant, admin) = await TenantAsync("cap-bad");
        var visitor = _api.ClientFor(tenant.Slug);
        var solution = await SolveAsync(visitor);

        (await CodeAsync(visitor, Form("0932000001", null))).ShouldBe("crm.captcha_required");
        var wrongNumber = solution with { Number = (solution.Number + 1) % (Max + 1) };
        (await CodeAsync(visitor, Form("0932000002", wrongNumber))).ShouldBe("crm.captcha_invalid");
        (await CodeAsync(visitor, Form("0932000003", solution with { Signature = new string('0', 64) }))).ShouldBe("crm.captcha_invalid");
        var longerExpiry = solution with { Salt = solution.Salt.Split('.')[0] + ".99999999999" };
        (await CodeAsync(visitor, Form("0932000004", longerExpiry))).ShouldBe("crm.captcha_invalid");

        // A challenge issued to another center does not count here.
        var (other, _) = await TenantAsync("cap-oth");
        var foreign = await SolveAsync(_api.ClientFor(other.Slug));
        (await CodeAsync(visitor, Form("0932000005", foreign))).ShouldBe("crm.captcha_invalid");

        (await admin.GetItemsAsync<object>("/api/crm/leads", Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_expired_challenge_is_rejected()
    {
        var (tenant, _) = await TenantAsync("cap-exp");
        var visitor = _api.ClientFor(tenant.Slug);

        // Correctly signed with the server key, but its expiry (inside the signed salt) is one minute ago.
        var salt = $"0123456789abcdef01234567.{DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()}";
        var challenge = Hash(salt, 42);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(KonnyakuApiFactory.CaptchaKey),
            Encoding.UTF8.GetBytes($"{tenant.Id:N}|{salt}|{challenge}")));

        (await CodeAsync(visitor, Form("0933000001", new Solution(salt, challenge, signature, 42)))).ShouldBe("crm.captcha_expired");
    }

    [Fact]
    public async Task A_rejected_form_does_not_spend_the_solution()
    {
        var (tenant, _) = await TenantAsync("cap-val");
        var visitor = _api.ClientFor(tenant.Slug);
        var solution = await SolveAsync(visitor);

        (await CodeAsync(visitor, Form("0934000001", solution) with { BirthYear = DateTime.UtcNow.Year - 9 })).ShouldBe("crm.guardian_required");
        (await visitor.PostAsJsonAsync("/api/crm/public/leads",
                Form("0934000001", solution) with { BirthYear = DateTime.UtcNow.Year - 9, GuardianName = "Phụ huynh Test" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private const int Max = 100_000;

    private static async Task<Solution> SolveAsync(HttpClient visitor)
    {
        var challenge = (await visitor.GetFromJsonAsync<IssuedChallenge>("/api/crm/public/captcha", Ct))!;
        challenge.Algorithm.ShouldBe("SHA-256");
        challenge.MaxNumber.ShouldBe(Max);
        for (var n = 0; n <= challenge.MaxNumber; n++)
        {
            if (Hash(challenge.Salt, n) == challenge.Challenge)
            {
                return new Solution(challenge.Salt, challenge.Challenge, challenge.Signature, n);
            }
        }

        throw new InvalidOperationException("No solution found.");
    }

    private static string Hash(string salt, long number) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(CultureInfo.InvariantCulture))));

    private static async Task<string> CodeAsync(HttpClient visitor, LeadForm form)
    {
        var response = await visitor.PostAsJsonAsync("/api/crm/public/leads", form, Ct);
        response.IsSuccessStatusCode.ShouldBeFalse();
        return (await response.Content.ReadFromJsonAsync<ErrorDto>(Ct))!.Code;
    }

    private static LeadForm Form(string phone, Solution? captcha) =>
        new("Khách Test", 1995, null, phone, null, "ja", "Giao tiếp", "2026-11", true, "vi", null, captcha);

    private async Task<(TenantDto Tenant, HttpClient Admin)> TenantAsync(string prefix)
    {
        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var tenant = await _api.CreateTenantWithAdminAsync(prefix, email, Password);
        var login = await _api.StaffLoginAsync(tenant.Slug, email, Password);
        return (tenant, _api.ClientFor(tenant.Slug, login.AccessToken));
    }

    private sealed record IssuedChallenge(string Algorithm, string Challenge, int MaxNumber, string Salt, string Signature);

    private sealed record Solution(string Salt, string Challenge, string Signature, long Number);

    private sealed record LeadForm(string StudentName, int? BirthYear, string? GuardianName, string? Phone, string? Email,
        string LanguageCode, string? Goal, string ConsentVersion, bool ConsentGiven, string Locale, string? Website, Solution? Captcha);
}
