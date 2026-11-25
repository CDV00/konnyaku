using Konnyaku.Composition;
using Konnyaku.Infrastructure.Modules;

var builder = Host.CreateApplicationBuilder(args);

// One line per entry, with the trace id of the outbox message or job being processed (Loki → Tempo).
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.IncludeScopes = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.AddKonnyakuTelemetry("konnyaku-worker", http: false);
builder.AddKonnyakuErrorReporting();
builder.AddModules(HostRole.Worker, KonnyakuModules.All);

builder.Build().Run();
