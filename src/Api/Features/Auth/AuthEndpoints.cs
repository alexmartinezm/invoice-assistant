using System.Security.Claims;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthenticatedUser(string Email, string DisplayName, string Role);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, AuthenticatedUser User);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapGet("/demo-users", DemoUsersAsync).AllowAnonymous();
        group.MapGet("/me", MeAsync).RequireAuthorization();

        return routes;
    }

    /// <summary>
    /// The seeded users, for the login screen's selector. Anonymous because it renders before
    /// anyone has a token, and it reads them from the database rather than letting the SPA keep a
    /// second hardcoded copy — the display names follow the configured market, so a copy would go
    /// stale the moment anyone changed it. The shared demo password is deliberately not returned:
    /// it is a fixed literal the page already knows, and an endpoint that hands out passwords is
    /// not a shape worth teaching even when the password is public.
    /// </summary>
    private static async Task<IResult> DemoUsersAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);

        // Ordered in memory, most privileged first: Role is persisted as a string, so ordering in
        // SQL would sort it alphabetically and put the Accountant above the Admin.
        return Results.Ok(users
            .OrderByDescending(u => u.Role)
            .Select(u => new AuthenticatedUser(u.Email, u.DisplayName, u.Role.ToString())));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        TokenIssuer tokenIssuer,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == request.Email, cancellationToken);

        // Same response whether the user is unknown or the password is wrong.
        if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            return Results.Problem(
                title: "Invalid credentials",
                detail: "Email or password is not valid.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = tokenIssuer.Issue(user);
        return Results.Ok(new LoginResponse(
            token.AccessToken,
            token.ExpiresAt,
            new AuthenticatedUser(user.Email, user.DisplayName, user.Role.ToString())));
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == principal.Id(), cancellationToken);

        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new AuthenticatedUser(user.Email, user.DisplayName, user.Role.ToString()));
    }
}
