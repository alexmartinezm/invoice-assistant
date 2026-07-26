using System.Security.Claims;
using Api.Domain;

namespace Api.Features.Auth;

/// <summary>
/// Reads the caller's identity from the validated JWT. Everything downstream — endpoints, tools,
/// and the policy engine in F2 — takes the role from here, never from anything the model said.
/// </summary>
public static class CurrentUser
{
    public static Guid Id(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The token carries no usable subject claim.");

    public static string Email(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

    public static Role Role(this ClaimsPrincipal principal) =>
        Enum.TryParse<Role>(principal.FindFirstValue(ClaimTypes.Role), out var role)
            ? role
            : Domain.Role.Viewer;
}
