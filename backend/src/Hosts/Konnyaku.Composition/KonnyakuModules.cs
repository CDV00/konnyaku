using Konnyaku.Catalog.Endpoints;
using Konnyaku.Identity.Endpoints;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Organization.Endpoints;
using Konnyaku.Platform.Endpoints;

namespace Konnyaku.Composition;

/// <summary>The modules hosted by the monolith. A service extracted later gets its own, shorter list.</summary>
public static class KonnyakuModules
{
    public static IModule[] All =>
    [
        new PlatformModule(),
        new CatalogModule(),
        new IdentityModule(),
        new OrganizationModule(),
    ];
}
