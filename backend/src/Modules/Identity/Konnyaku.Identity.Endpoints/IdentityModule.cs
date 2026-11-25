using System.Security.Claims;
using Konnyaku.Application.Authorization;
using Konnyaku.Identity.Application;
using Konnyaku.Identity.Contracts;
using Konnyaku.Identity.Domain;
using Konnyaku.Identity.Infrastructure;
using Konnyaku.Infrastructure.Auditing;
using Konnyaku.Infrastructure.Auth;
using Konnyaku.Infrastructure.Http;
using Konnyaku.Infrastructure.Messaging;
using Konnyaku.Infrastructure.Modules;
using Konnyaku.Infrastructure.Tenancy;
using Konnyaku.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Konnyaku.Identity.Endpoints;

/// <summary>
/// A finished sign-in carries the access token. A staff sign-in may stop after the password: <see cref="Challenge"/> is
/// then set and <see cref="NextStep"/> says what it needs (<c>verify</c>: TOTP code; <c>enroll</c>: set up 2FA first).
/// </summary>
public sealed record SignInResponse(string? AccessToken, string Audience, DateTimeOffset ExpiresAt, string DisplayName, IReadOnlyList<string> Roles,
    bool MustChangePassword, string? Challenge = null, string? NextStep = null);

public sealed record TwoFactorEnrolledResponse(IReadOnlyList<string> RecoveryCodes, SignInResponse SignIn);

public sealed record ChallengeCommand(string Challenge);

public sealed class IdentityModule : IModule
{
    public string Name => "identity";

    public IReadOnlyList<Type> DbContextTypes => [typeof(IdentityDbContext)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityInfrastructure(configuration, Name);
        services.AddScoped<StaffLoginHandler>();
        services.AddScoped<StaffSecondFactorHandler>();
        services.AddScoped<LearnerLoginHandler>();
        services.AddScoped<MyAccountHandler>();
        services.AddScoped<PasswordResetHandler>();
        services.AddScoped<AccountAdminHandler>();
        services.AddScoped<CreateLearnerAccountHandler>();
        services.AddScoped<BootstrapTenantAdminHandler>();
        services.AddScoped<AddStaffMemberHandler>();
        services.AddScoped<StaffQueries>();
        services.AddScoped<StaffList>();
        services.AddScoped<Konnyaku.Application.Messaging.IIntegrationEventHandler<Konnyaku.Crm.Contracts.StudentAnonymizedV1>, StudentAnonymizedConsumer>();
        services.AddScoped<StaffAdminHandler>();
    }

    public void AddWorkerServices(IServiceCollection services) => services.AddOutboxProcessor<IdentityDbContext>();

    public void MapEndpoints(RouteGroupBuilder group)
    {
        MapSignIn(group);
        MapMyAccount(group);
        MapAdministration(group);

        // Platform operation: runs in its own scope with the target tenant as context.
        group.MapPost("/bootstrap/tenant-admin", async (BootstrapTenantAdminCommand command, IServiceScopeFactory scopes, CancellationToken ct) =>
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().Set(command.TenantId);
                var result = await scope.ServiceProvider.GetRequiredService<BootstrapTenantAdminHandler>().HandleAsync(command, ct);
                return ApiResults.From(result, id => Results.Ok(new { userId = id }));
            })
            .RequirePlatformKey();
    }

    private static void MapSignIn(RouteGroupBuilder group)
    {
        group.MapPost("/staff/login", async (StaffLoginCommand command, StaffLoginHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), result => Results.Ok(result.SignedIn is { } signedIn
                ? Issue(tokens, signedIn, Audiences.Staff)
                : Challenge(tokens, result.Pending!))));

        // Second step: the code of the authenticator app, or one of the single-use recovery codes.
        group.MapPost("/staff/login/2fa", async (SecondFactorCommand command, StaffSecondFactorHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            await tokens.ValidateChallengeAsync(command.Challenge) is not { } challenge
                ? ApiResults.Error(Error.Validation("identity.sign_in_expired"))
                : ApiResults.From(await handler.VerifyAsync(Pending(challenge), command, ct), s => Results.Ok(Issue(tokens, s, Audiences.Staff))));

        // Roles that must use 2FA enroll before their first session: secret + QR link, then a code from the app.
        group.MapPost("/staff/login/2fa/setup", async (ChallengeCommand command, StaffSecondFactorHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            await tokens.ValidateChallengeAsync(command.Challenge) is not { } challenge
                ? ApiResults.Error(Error.Validation("identity.sign_in_expired"))
                : ApiResults.From(await handler.SetupAsync(Pending(challenge), ct), Results.Ok));

        group.MapPost("/staff/login/2fa/enable", async (SecondFactorCommand command, StaffSecondFactorHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            await tokens.ValidateChallengeAsync(command.Challenge) is not { } challenge
                ? ApiResults.Error(Error.Validation("identity.sign_in_expired"))
                : ApiResults.From(await handler.EnableAsync(Pending(challenge), command, ct),
                    e => Results.Ok(new TwoFactorEnrolledResponse(e.RecoveryCodes, Issue(tokens, e.SignedIn, Audiences.Staff)))));

        group.MapPost("/learn/login", async (LearnerLoginCommand command, LearnerLoginHandler handler, ITokenIssuer tokens, CancellationToken ct) =>
            ApiResults.From(await handler.HandleAsync(command, ct), s => Results.Ok(Issue(tokens, s, Audiences.Learn))));

        // Forgot password (staff, by email). Always 202: the answer never tells whether the email has an account.
        group.MapPost("/password/forgot", async (ForgotPasswordCommand command, PasswordResetHandler handler, CancellationToken ct) =>
            {
                await handler.RequestAsync(command, ct);
                return Results.Accepted();
            })
            .RequireRateLimiting(RateLimitPolicies.PasswordReset);

        group.MapPost("/password/reset", async (ResetPasswordCommand command, PasswordResetHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.ResetAsync(command, ct), _ => Results.NoContent()))
            .RequireRateLimiting(RateLimitPolicies.PasswordReset);
    }

    private static void MapMyAccount(RouteGroupBuilder group)
    {
        group.MapGet("/me", async (ClaimsPrincipal user, IIdentityApi identity, CancellationToken ct) =>
                await identity.GetMemberAsync(user.UserId()!.Value, ct) is { } me ? Results.Ok(me) : Results.Unauthorized())
            .RequireAuthorization();

        // Ends the session of this token (the web app clears its cookie too).
        group.MapPost("/logout", async (ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.SignOutAsync(user.UserId()!.Value, ct), _ => Results.NoContent()))
            .RequireAuthorization();

        // Own sessions (devices), 2FA state, whether the password must be changed.
        group.MapGet("/me/security", async (ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.SecurityAsync(user.UserId()!.Value, Realm(user), ct)))
            .RequireAuthorization();

        group.MapPost("/me/sessions/{id:guid}/revoke", async (Guid id, ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.RevokeSessionAsync(user.UserId()!.Value, id, ct), _ => Results.NoContent()))
            .RequireAuthorization();

        group.MapPost("/learn/change-password", async (ChangePasswordCommand command, ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.ChangePasswordAsync(user.UserId()!.Value, Realms.Learn, command, ct), _ => Results.NoContent()))
            .RequireAuthorization(Policies.Learn);

        group.MapPost("/staff/change-password", async (ChangePasswordCommand command, ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.ChangePasswordAsync(user.UserId()!.Value, Realms.Staff, command, ct), _ => Results.NoContent()))
            .RequireAuthorization(Policies.Staff);

        var twoFactor = group.MapGroup("/me/2fa").RequireAuthorization(Policies.Staff);
        twoFactor.MapPost("/setup", async (ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.BeginTwoFactorAsync(user.UserId()!.Value, ct), Results.Ok));
        twoFactor.MapPost("/enable", async (TwoFactorCodeCommand command, ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.EnableTwoFactorAsync(user.UserId()!.Value, command, ct), codes => Results.Ok(new { recoveryCodes = codes })));
        twoFactor.MapPost("/disable", async (DisableTwoFactorCommand command, ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.DisableTwoFactorAsync(user.UserId()!.Value, command, ct), _ => Results.NoContent()));
        twoFactor.MapPost("/recovery-codes", async (TwoFactorCodeCommand command, ClaimsPrincipal user, MyAccountHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.RegenerateRecoveryCodesAsync(user.UserId()!.Value, command, ct), codes => Results.Ok(new { recoveryCodes = codes })));
    }

    private static void MapAdministration(RouteGroupBuilder group)
    {
        // Learner accounts are issued by whoever keeps student records (consultants, branch managers, coordinators, admins).
        group.MapPost("/learners", async (CreateLearnerAccountCommand command, CreateLearnerAccountHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), a => Results.Created($"learners/{a.UserId}", a)))
            .RequirePermission(Permissions.Students);

        // What the signed-in member may change (the ops screens hide the rest; the API checks again on every write).
        group.MapGet("/me/access", async (IAccessControl access, CancellationToken ct) => Results.Ok(await access.ListAsync(ct)))
            .RequireAuthorization(Policies.Staff);

        group.MapPost("/staff", async (AddStaffMemberCommand command, AddStaffMemberHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.HandleAsync(command, ct), id => Results.Ok(new { userId = id })))
            .RequirePermission(Permissions.Staff);

        // Colleagues (names, emails, roles, branches) for members who change something: coordinators pick teachers,
        // accountants set rates, consultants hand over leads. A teacher-only member does not list staff.
        group.MapGet("/staff", async ([AsParameters] StaffFilter filter, [AsParameters] ListParams list, StaffList staff, CancellationToken ct) =>
                Results.Ok(await staff.ListAsync(filter, list.ToRequest(StaffList.Sorts), ct)))
            .RequirePermission([.. Permissions.All]);

        group.MapPut("/staff/{userId:guid}", async (Guid userId, UpdateStaffMemberCommand command, StaffAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.UpdateAsync(userId, command, ct), Results.Ok))
            .RequirePermission(Permissions.Staff);

        group.MapPost("/staff/{userId:guid}/deactivate", async (Guid userId, StaffAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.SuspendAsync(userId, ct), Results.Ok))
            .RequirePermission(Permissions.Staff);

        group.MapPost("/staff/{userId:guid}/activate", async (Guid userId, StaffAdminHandler handler, CancellationToken ct) =>
                ApiResults.From(await handler.ActivateAsync(userId, ct), Results.Ok))
            .RequirePermission(Permissions.Staff);

        // Account security of any member (staff, student, parent): the administrator only.
        var accounts = group.MapGroup("/accounts").RequirePermission(Permissions.Accounts);
        accounts.MapGet("/learners", async (AccountAdminHandler handler, CancellationToken ct) => Results.Ok(await handler.ListLearnersAsync(ct)));
        accounts.MapPost("/{userId:guid}/suspend", async (Guid userId, AccountAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.SuspendAsync(userId, ct), _ => Results.NoContent()));
        accounts.MapPost("/{userId:guid}/activate", async (Guid userId, AccountAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.ActivateAsync(userId, ct), _ => Results.NoContent()));
        accounts.MapPost("/{userId:guid}/reset-password", async (Guid userId, AccountAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.ResetPasswordAsync(userId, ct), Results.Ok));
        accounts.MapPost("/{userId:guid}/reset-2fa", async (Guid userId, AccountAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.ResetTwoFactorAsync(userId, ct), _ => Results.NoContent()));
        accounts.MapPost("/{userId:guid}/sign-out", async (Guid userId, AccountAdminHandler handler, CancellationToken ct) =>
            ApiResults.From(await handler.SignOutEverywhereAsync(userId, ct), n => Results.Ok(new { revoked = n })));

        group.MapGet("/security-events", async (Guid? userId, string? kind, int? take, AccountAdminHandler handler, CancellationToken ct) =>
                Results.Ok(await handler.EventsAsync(userId, kind, take ?? 200, ct)))
            .RequirePermission(Permissions.Accounts);

        group.MapChangeHistory<IdentityDbContext>(Permissions.Staff, Permissions.Accounts);
    }

    private static string Realm(ClaimsPrincipal user) =>
        user.HasClaim(KonnyakuClaims.Audience, Audiences.Learn) ? Realms.Learn : Realms.Staff;

    private static PendingSignIn Pending(SignInChallenge challenge) =>
        new(challenge.UserId, challenge.TenantId, challenge.Purpose, challenge.Stamp);

    private static SignInResponse Issue(ITokenIssuer tokens, SignedIn signedIn, string audience)
    {
        var token = tokens.Issue(signedIn.UserId, signedIn.TenantId, audience, signedIn.DisplayName, signedIn.Roles, signedIn.SessionId, signedIn.ExpiresAt);
        return new SignInResponse(token.AccessToken, audience, token.ExpiresAt, signedIn.DisplayName, signedIn.Roles, signedIn.MustChangePassword);
    }

    private static SignInResponse Challenge(ITokenIssuer tokens, PendingSignIn pending)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(TokenLifetimes.Challenge);
        var challenge = tokens.IssueChallenge(new SignInChallenge(pending.UserId, pending.TenantId, pending.Purpose, pending.Stamp), expiresAt);
        return new SignInResponse(null, Audiences.Staff, expiresAt, string.Empty, [], false, challenge, pending.Purpose);
    }
}
