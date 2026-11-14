using Konnyaku.Infrastructure.Modules;
using Konnyaku.Composition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Applies every module's migrations with the schema-owner connection (ConnectionStrings:Owner).
// Module roles used by Api/Worker only have DML rights on their own schema.
var modules = KonnyakuModules.All;

var builder = Host.CreateApplicationBuilder(args);
var owner = builder.Configuration.GetConnectionString("Owner")
    ?? throw new InvalidOperationException("Missing connection string 'Owner'.");
builder.Configuration.AddInMemoryCollection(
    modules.Select(m => new KeyValuePair<string, string?>($"ConnectionStrings:{m.Name}", owner)));

builder.AddModules(HostRole.Migrator, modules);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
foreach (var contextType in modules.SelectMany(m => m.DbContextTypes))
{
    var db = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
    Console.WriteLine($"Migrating {contextType.Name}...");
    await db.Database.MigrateAsync();
}

Console.WriteLine("Done.");
