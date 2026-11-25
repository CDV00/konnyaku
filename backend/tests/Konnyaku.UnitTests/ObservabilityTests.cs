using System.Diagnostics;
using Konnyaku.Composition;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Observability;
using OpenTelemetry.Trace;
using Sentry;

namespace Konnyaku.UnitTests;

public class ObservabilityTests
{
    [Theory]
    [InlineData("SMTP rejected lan.nguyen@example.com", "SMTP rejected [email]")]
    [InlineData("call 0912 345 678 or +84 912345678", "call [phone] or [phone]")]
    [InlineData("Host=postgres;Username=kk_crm;Password=s3cret;Pooling=true", "Host=postgres;Username=kk_crm;Password=[redacted];Pooling=true")]
    [InlineData("Authorization: Bearer abc.def-ghi", "Authorization: Bearer [token]")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl expired", "token [token] expired")]
    [InlineData("Session 0190f3a2-7c4e-7b61-9d2e-5a1f0c3b8e47 not found (attempt 3 of 10)", "Session 0190f3a2-7c4e-7b61-9d2e-5a1f0c3b8e47 not found (attempt 3 of 10)")]
    public void Personal_data_is_masked_in_error_text(string text, string expected) =>
        PersonalDataScrubber.Scrub(text).ShouldBe(expected);

    [Fact]
    public void Error_events_lose_request_data_and_user()
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("no account for lan@example.com"))
        {
            User = new SentryUser { Email = "lan@example.com", IpAddress = "203.0.113.7" },
            Message = new SentryMessage { Message = "Login of {Email} failed", Formatted = "Login of lan@example.com failed" },
        };
        sentryEvent.Request.QueryString = "?search=Nguyen";
        sentryEvent.Request.Cookies = "kk_staff=secret";
        sentryEvent.Request.Headers["Authorization"] = "Bearer abc";
        sentryEvent.Request.Headers["Content-Type"] = "application/json";
        sentryEvent.SentryExceptions = [new Sentry.Protocol.SentryException { Value = "no account for lan@example.com" }];

        var scrubbed = PersonalDataScrubber.Scrub(sentryEvent);

        scrubbed.User.Email.ShouldBeNull();
        scrubbed.User.IpAddress.ShouldBeNull();
        scrubbed.Request.QueryString.ShouldBeNull();
        scrubbed.Request.Cookies.ShouldBeNull();
        scrubbed.Request.Headers.Keys.ShouldBe(["Content-Type"]);
        scrubbed.Message!.Formatted.ShouldBe("Login of [email] failed");
        scrubbed.SentryExceptions!.Single().Value.ShouldBe("no account for [email]");
    }

    [Fact]
    public void Background_polls_do_not_start_traces_but_requests_and_jobs_do()
    {
        var sampler = new RequestRootSampler(1.0);
        var traceId = ActivityTraceId.CreateRandom();

        Decision(sampler, traceId, ActivityKind.Client).ShouldBe(SamplingDecision.Drop);
        Decision(sampler, traceId, ActivityKind.Server).ShouldBe(SamplingDecision.RecordAndSample);
        Decision(sampler, traceId, ActivityKind.Consumer).ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Fact]
    public void Outbox_dispatch_joins_the_trace_of_the_publishing_request()
    {
        var parent = KonnyakuTelemetry.ParentFrom("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        parent.TraceId.ToHexString().ShouldBe("0af7651916cd43dd8448eb211c80319c");
        KonnyakuTelemetry.ParentFrom(null).ShouldBe(default);
        KonnyakuTelemetry.ParentFrom("not-a-traceparent").ShouldBe(default);
        OutboxProcessor<Konnyaku.Crm.Infrastructure.CrmDbContext>.EventName("Konnyaku.Crm.Contracts.LeadCreatedV1, Konnyaku.Crm.Contracts")
            .ShouldBe("LeadCreatedV1");
    }

    private static SamplingDecision Decision(Sampler sampler, ActivityTraceId traceId, ActivityKind kind) =>
        sampler.ShouldSample(new SamplingParameters(default, traceId, "span", kind)).Decision;
}
