using Konnyaku.Infrastructure.Modules;
using Konnyaku.Organization.Endpoints;

var builder = Host.CreateApplicationBuilder(args);

builder.AddModules(HostRole.Worker, new OrganizationModule());

builder.Build().Run();
