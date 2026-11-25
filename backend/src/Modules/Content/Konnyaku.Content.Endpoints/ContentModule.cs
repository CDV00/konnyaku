using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Content.Application;
using Konnyaku.Content.Contracts;
using Konnyaku.Content.Domain;
using Konnyaku.Content.Infrastructure;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Content.Endpoints;

/// <summary>
/// File storage (docs/10-hoc-tap-va-danh-gia.md, 10.3): signed upload and download URLs, checks on completion, tenant
/// quota. Staff and learners alike upload; what they may upload and read depends on the purpose.
/// </summary>
public sealed class ContentModule : IModule
{
    public string Name => "content";

    public IReadOnlyList<Type> DbContextTypes => [typeof(ContentDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddContentInfrastructure(configuration, Name);
        services.AddScoped<ContentHandlers>();
        services.AddScoped<IContentApi, ContentApi>();
    }

    public void AddWorkerServices(IServiceCollection services)
    {
        services.AddOutboxProcessor<ContentDbContext>();
        services.AddContentWorker();
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // Dev/test storage only: the API serves the signed URLs of LocalDiskStorage itself.
        group.MapLocalDiskStorage();

        var member = group.MapGroup("").RequireAuthorization(p => p.RequireAuthenticatedUser()
            .RequireClaim(KonnyakuClaims.Audience, Audiences.Staff, Audiences.Learn));

        member.MapPost("/uploads", async (CreateUploadCommand command, ClaimsPrincipal user, IAccessControl access, ContentHandlers handlers,
                CancellationToken ct) =>
            ApiResults.From(await handlers.CreateUploadAsync(command, await ActorAsync(user, access, ct), ct), t => Results.Ok(t)));
        member.MapPost("/uploads/{id:guid}/complete", async (Guid id, ClaimsPrincipal user, IAccessControl access, ContentHandlers handlers,
                CancellationToken ct) =>
            ApiResults.From(await handlers.CompleteAsync(id, await ActorAsync(user, access, ct), ct), Results.Ok));
        member.MapGet("/files/{id:guid}/download", async (Guid id, ClaimsPrincipal user, IAccessControl access, ContentHandlers handlers,
                CancellationToken ct) =>
            ApiResults.From(await handlers.GetDownloadAsync(id, await ActorAsync(user, access, ct), ct), Results.Ok));
        member.MapDelete("/files/{id:guid}", async (Guid id, ClaimsPrincipal user, IAccessControl access, ContentHandlers handlers,
                CancellationToken ct) =>
            ApiResults.From(await handlers.DeleteAsync(id, await ActorAsync(user, access, ct), ct), _ => Results.NoContent()));
        member.MapGet("/my/files", async (FilePurpose? purpose, [AsParameters] ListParams list, ClaimsPrincipal user, ContentHandlers handlers,
                CancellationToken ct) =>
            Results.Ok(await handlers.ListAsync(purpose, null, user.UserId()!.Value, list.ToRequest(), ct)));

        // Every file of the center and its storage use: center-wide administrators.
        var admin = group.MapGroup("").RequireCenterPermission(Permissions.Files);
        admin.MapGet("/files", async (FilePurpose? purpose, FileStatus? status, [AsParameters] ListParams list, ContentHandlers handlers,
                CancellationToken ct) =>
            Results.Ok(await handlers.ListAsync(purpose, status, null, list.ToRequest(), ct)));
        admin.MapGet("/usage", async (ContentHandlers handlers, CancellationToken ct) => Results.Ok(await handlers.UsageAsync(ct)));
    }

    private static async Task<FileActor> ActorAsync(ClaimsPrincipal user, IAccessControl access, CancellationToken ct)
    {
        var staff = user.HasClaim(KonnyakuClaims.Audience, Audiences.Staff);
        return new FileActor(
            user.UserId()!.Value,
            staff ? UploaderKind.Staff : UploaderKind.Learner,
            staff && await access.CanAsync(Permissions.Files, null, ct));
    }
}
