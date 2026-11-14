using Konnyaku.Infrastructure.Modules;
using Konnyaku.Organization.Endpoints;

namespace Konnyaku.Api;

public static class KonnyakuModules
{
    public static IModule[] All => [new OrganizationModule()];
}
