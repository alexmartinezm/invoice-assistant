using System.Security.Claims;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Actions;

/// <summary>
/// Where an approved write has got to, as its own resource.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the approval response can no longer be the last word. An execution can be
/// waiting on a delivery that settles minutes later, so a card that showed the answer once and
/// stopped would be showing a guess. This is what it polls.
/// </para>
/// <para>
/// Visibility follows the proposal's, not the execution's: an Admin who could resolve the action can
/// see what became of it, and nobody else can. An auto-allowed write has no proposal, so it is
/// visible to the user it ran for.
/// </para>
/// </remarks>
public static class ActionExecutionEndpoints
{
    public static IEndpointRouteBuilder MapActionExecutionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/api/action-executions")
            .WithTags("Assistant")
            .RequireAuthorization()
            .MapGet("/{id:guid}", GetAsync);

        return routes;
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var execution = await db.ActionExecutions.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (execution is null || !await MaySeeAsync(principal, execution, db, cancellationToken))
        {
            // The same answer for "does not exist" and "not yours": a 403 here would confirm that
            // somebody else's execution exists, which is a question this endpoint does not answer.
            return Results.Problem(
                title: "Execution not found",
                detail: $"There is no action execution with id '{id}'.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "execution_not_found" });
        }

        var deliveryStatus = await DeliveryStatusAsync(execution, db, cancellationToken);
        return Results.Ok(ActionExecutionView.Of(execution, deliveryStatus));
    }

    /// <summary>
    /// The proposal's visibility rules, reused rather than restated — the two must not be able to
    /// disagree about who is allowed to look at one decision.
    /// </summary>
    private static async Task<bool> MaySeeAsync(
        ClaimsPrincipal principal,
        ActionExecution execution,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (principal.Role() is Role.Admin || execution.UserId == principal.Id())
        {
            return true;
        }

        return execution.PendingActionId is { } actionId
            && await db.PendingActions.AsNoTracking()
                .AnyAsync(action => action.Id == actionId && action.UserId == principal.Id(), cancellationToken);
    }

    internal static async Task<string?> DeliveryStatusAsync(
        ActionExecution execution,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (execution.DeliveryId is not { } deliveryId)
        {
            return null;
        }

        var status = await db.InvoiceDeliveries.AsNoTracking()
            .Where(delivery => delivery.Id == deliveryId)
            .Select(delivery => (InvoiceDeliveryStatus?)delivery.Status)
            .SingleOrDefaultAsync(cancellationToken);

        return status?.ToString().ToLowerInvariant();
    }
}
