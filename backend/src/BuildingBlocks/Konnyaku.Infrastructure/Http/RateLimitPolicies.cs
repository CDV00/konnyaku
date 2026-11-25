namespace Konnyaku.Infrastructure.Http;

public static class RateLimitPolicies
{
    /// <summary>Anonymous public forms (registration): limited per client IP by the API host.</summary>
    public const string PublicForms = "public-forms";

    /// <summary>Forgot / reset password: limited per client IP (on top of the per-account limit of the handler).</summary>
    public const string PasswordReset = "password-reset";
}
