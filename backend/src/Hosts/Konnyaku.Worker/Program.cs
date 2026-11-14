using Konnyaku.Composition;
using Konnyaku.Infrastructure.Modules;

var builder = Host.CreateApplicationBuilder(args);

builder.AddModules(HostRole.Worker, KonnyakuModules.All);

builder.Build().Run();
