using System.Security.Claims;
using Konnyaku.Application.Messaging;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Learning.Application;
using Konnyaku.Learning.Infrastructure;
using Konnyaku.Workspace.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Learning.Endpoints;

public sealed class LearningModule : IModule
{
    public string Name => "learning";

    public IReadOnlyList<Type> DbContextTypes => [typeof(LearningDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLearningInfrastructure(configuration, Name);
        services.AddScoped<AttendanceHandlers>();
        services.AddScoped<RecordingQueries>();
        services.AddScoped<Konnyaku.Application.Privacy.IPersonalDataSource, LearningPersonalData>();
        services.AddScoped<IIntegrationEventHandler<ParticipantsCollectedV1>, ParticipantsCollectedConsumer>();
        services.AddScoped<IIntegrationEventHandler<RecordingAvailableV1>, RecordingAvailableConsumer>();
        services.AddScoped<IIntegrationEventHandler<RecordingPublishedV1>, RecordingPublishedConsumer>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<LearningDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        static IReadOnlyCollection<string> Roles(ClaimsPrincipal user) => user.FindAll(KonnyakuClaims.Role).Select(c => c.Value).ToList();

        group.MapGet("/sessions/{id:guid}/attendance", async (Guid id, ClaimsPrincipal user, AttendanceHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.ListAsync(id, user.UserId()!.Value, Roles(user), ct), Results.Ok))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/sessions/{id:guid}/attendance/confirm", async (Guid id, ConfirmAttendanceCommand command, ClaimsPrincipal user, AttendanceHandlers handlers, CancellationToken ct) =>
                ApiResults.From(await handlers.ConfirmAsync(id, user.UserId()!.Value, Roles(user), command, ct), Results.Ok))
            .RequireAuthorization(Policies.Staff);

        group.MapGet("/my/recordings", async (ClaimsPrincipal user, RecordingQueries queries, CancellationToken ct) =>
                Results.Ok(await queries.ListForUserAsync(user.UserId()!.Value, ct)))
            .RequireAuthorization(Policies.Learn);
    }
}
