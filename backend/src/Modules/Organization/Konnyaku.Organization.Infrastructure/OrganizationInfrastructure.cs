using Konnyaku.Infrastructure.Persistence;
using Konnyaku.Organization.Application;
using Konnyaku.Organization.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Organization.Infrastructure;

public static class OrganizationInfrastructure
{
    public static IServiceCollection AddOrganizationInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string connectionName)
    {
        services.AddModuleDbContext<OrganizationDbContext>(configuration, connectionName, OrganizationDbContext.SchemaName);
        services.AddScoped<IOrganizationDbContext>(sp => sp.GetRequiredService<OrganizationDbContext>());
        services.AddScoped<IOrganizationApi, InProcessOrganizationApi>();
        return services;
    }
}
