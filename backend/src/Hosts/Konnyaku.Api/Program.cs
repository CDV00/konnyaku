using Konnyaku.Api;
using Konnyaku.Composition;
using System.Text.Json.Serialization;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Modules;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog writes the console (with the trace id of the request, see TraceIdEnricher); other logging providers only
// receive what Serilog forwards to them, i.e. Sentry when Sentry:Dsn is set.
builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.With<TraceIdEnricher>()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{TraceSuffix:l}{NewLine}{Exception}"),
    writeToProviders: true);
builder.AddKonnyakuTelemetry("konnyaku-api", http: true);
builder.AddKonnyakuErrorReporting();

builder.AddModules(HostRole.Api, KonnyakuModules.All);
builder.Services.AddKonnyakuAuth();
// The API is only called by the web app, which relays the visitor IP in X-Forwarded-For. Trust that header from
// loopback (dev) and the networks in ForwardedHeaders:KnownNetworks (the web containers), so the public-form rate
// limit is per visitor instead of one shared bucket for the web server's IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});
// Per-IP limits of public forms and password reset; shared by every replica through Redis when Redis:ConnectionString is set.
builder.Services.AddKonnyakuRateLimiting(builder.Configuration);
builder.Services.AddOpenApi();
// Enums travel as names ("Online", "Regular") in requests and responses.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// Malformed request bodies are client errors (400) in every environment, never 500.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var app = builder.Build();

// Rolling deploys (infra/deploy/scripts/deploy.sh): on SIGTERM keep serving for Shutdown:DrainSeconds and answer
// with "Connection: close", so callers drop their pooled keep-alive connections after a complete response instead of
// having one closed under a request; only then does Kestrel stop listening (callers retry refused connections).
var drainSeconds = app.Configuration.GetValue("Shutdown:DrainSeconds", 0);
if (drainSeconds > 0)
{
    app.Lifetime.ApplicationStopping.Register(() => Thread.Sleep(TimeSpan.FromSeconds(drainSeconds)));
}
app.Use((context, next) =>
{
    if (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        context.Response.Headers.Connection = "close";
    }
    return next(context);
});

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseAuthentication();
// /health is liveness for container health checks: it must not depend on PostgreSQL (tenant lookup).
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseMiddleware<TenantResolutionMiddleware>());
// After the tenant is known (sessions are tenant data): a revoked or expired sign-in session makes the token anonymous.
app.UseMiddleware<SessionValidationMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapModules();

app.Run();

public partial class Program;
