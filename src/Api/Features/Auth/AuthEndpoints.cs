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
        group.MapGet("/me", MeAsync).RequireAuthorization();

        return routes;
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
