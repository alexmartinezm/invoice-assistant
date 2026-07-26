using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Customers;

public sealed record CustomerView(Guid Id, string Name, string TaxId, string Email);

public sealed record CustomerList(int Count, IReadOnlyList<CustomerView> Customers);

public static class CustomerEndpoints
{
    private const int MaxResults = 25;

    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/customers").WithTags("Customers").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        string? query = null)
    {
        var customers = db.Customers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            customers = customers.Where(c =>
                EF.Functions.ILike(c.Name, $"%{term}%") ||
                EF.Functions.ILike(c.TaxId, $"%{term}%") ||
                EF.Functions.ILike(c.Email, $"%{term}%"));
        }

        var results = await customers
            .OrderBy(c => c.Name)
            .Take(MaxResults)
            .Select(c => new CustomerView(c.Id, c.Name, c.TaxId, c.Email))
            .ToListAsync(cancellationToken);

        return Results.Ok(new CustomerList(results.Count, results));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new CustomerView(c.Id, c.Name, c.TaxId, c.Email))
            .SingleOrDefaultAsync(cancellationToken);

        return customer is null
            ? Results.Problem(
                title: "Customer not found",
                detail: $"There is no customer with id '{id}'.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "customer_not_found" })
            : Results.Ok(customer);
    }
}
