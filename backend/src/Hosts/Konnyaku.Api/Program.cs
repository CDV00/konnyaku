using Konnyaku.Api;
using Konnyaku.Composition;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Modules;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.AddModules(HostRole.Api, KonnyakuModules.All);
builder.Services.AddKonnyakuAuth();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
// Malformed request bodies are client errors (400) in every environment, never 500.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapModules();

app.Run();

public partial class Program;
