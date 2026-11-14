using Konnyaku.Academic.Endpoints;
using Konnyaku.Billing.Endpoints;
using Konnyaku.Catalog.Endpoints;
using Konnyaku.Crm.Endpoints;
using Konnyaku.Hr.Endpoints;
using Konnyaku.Identity.Endpoints;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Learning.Endpoints;
using Konnyaku.Organization.Endpoints;
using Konnyaku.Platform.Endpoints;
using Konnyaku.Scheduling.Endpoints;
using Konnyaku.Workspace.Endpoints;

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
        new CrmModule(),
        new AcademicModule(),
        new SchedulingModule(),
        new BillingModule(),
        new WorkspaceModule(),
        new LearningModule(),
        new HrModule(),
    ];
}
