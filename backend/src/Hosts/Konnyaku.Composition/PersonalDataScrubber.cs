using System.Diagnostics;
using System.Text.RegularExpressions;
using Sentry;

namespace Konnyaku.Composition;

/// <summary>
/// Last line of defence before an error leaves the server: removes request data the SDK could attach (cookies, query
/// string, body, headers other than a few technical ones, user) and masks emails, phone numbers, tokens and passwords
/// that an exception or log message may quote.
/// </summary>
public static partial class PersonalDataScrubber
{
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Content-Type", "Content-Length", "X-Tenant-Host", "traceparent",
    };

    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = Email().Replace(text, "[email]");
        text = Jwt().Replace(text, "[token]");
        text = Bearer().Replace(text, "Bearer [token]");
        text = Secret().Replace(text, "$1=[redacted]");
        return Phone().Replace(text, "[phone]");
    }

    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        sentryEvent.User = new SentryUser();
        sentryEvent.ServerName = null;
        var request = sentryEvent.Request;
        request.Cookies = null;
        request.QueryString = null;
        request.Data = null;
        foreach (var header in request.Headers.Keys.Where(h => !SafeHeaders.Contains(h)).ToList())
        {
            request.Headers.Remove(header);
        }

        if (sentryEvent.Message is { } message)
        {
            sentryEvent.Message = new SentryMessage { Message = message.Message, Formatted = Scrub(message.Formatted) };
        }

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = Scrub(exception.Value);
        }

        // The trace of the failing request, to open it in Grafana (Tempo) from the Sentry/GlitchTip issue.
        if (Activity.Current is { } activity)
        {
            sentryEvent.SetTag("trace_id", activity.TraceId.ToHexString());
        }

        return sentryEvent;
    }

    public static Breadcrumb Scrub(Breadcrumb breadcrumb) =>
        new(Scrub(breadcrumb.Message) ?? string.Empty, breadcrumb.Type ?? "default", data: null, breadcrumb.Category, breadcrumb.Level);

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/\-]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token|api[_-]?key)\s*[=:]\s*[^;\s,""']+")]
    private static partial Regex Secret();

    // Vietnamese and international numbers: +84 / 0 followed by 8-10 digits, optionally separated by spaces, dots, dashes.
    [GeneratedRegex(@"(?<![\w-])(?:\+\d{1,3}|0)(?:[\s.\-]?\d){8,11}(?!\d)")]
    private static partial Regex Phone();
}
