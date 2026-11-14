namespace Konnyaku.Infrastructure.Http;

public static class RateLimitPolicies
{
    /// <summary>Anonymous public forms (registration): limited per client IP by the API host.</summary>
    public const string PublicForms = "public-forms";
}
