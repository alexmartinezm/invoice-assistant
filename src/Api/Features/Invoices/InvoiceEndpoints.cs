using System.Security.Claims;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Features.Invoices;

public static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/invoices").WithTags("Invoices").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{number}", GetAsync);

        // Writes accept an Idempotency-Key so a retry — by the assistant, by the HTTP stack, or by a
        // user clicking approve twice — returns the first answer instead of doing the work again.
        var writes = routes.MapGroup("/api/invoices")
            .WithTags("Invoices")
            .RequireAuthorization()
            .AddEndpointFilter<IdempotencyFilter>();

        writes.MapPost("/", CreateDraftAsync).RequireAuthorization(Policies.Accountant);
        writes.MapPost("/{number}/send", SendAsync).RequireAuthorization(Policies.Accountant);
        writes.MapPost("/{number}/mark-paid", MarkPaidAsync).RequireAuthorization(Policies.Accountant);
        writes.MapPatch("/{number}/due-date", UpdateDueDateAsync).RequireAuthorization(Policies.Accountant);
        writes.MapPost("/{number}/cancel", CancelAsync).RequireAuthorization(Policies.Admin);

        return routes;
    }

    /// <summary>
    /// <paramref name="from"/> and <paramref name="to"/> bound the <em>due</em> date, which is
    /// what "what is overdue / what falls due this month" questions are actually about.
    /// </summary>
    private static async Task<IResult> ListAsync(
        AppDbContext db,
        IClock clock,
        IOptions<InvoicingOptions> options,
        CancellationToken cancellationToken,
        string? status = null,
        Guid? customerId = null,
        string? customerName = null,
        DateOnly? from = null,
        DateOnly? to = null,
        bool? overdue = null,
        int? limit = null)
    {
        var settings = options.Value;
        var today = clock.Today;

        InvoiceStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out var value))
            {
                return InvalidStatus(status);
            }

            parsedStatus = value;
        }

        var query = db.Invoices.AsNoTracking().Include(i => i.Customer).AsQueryable();

        if (parsedStatus is { } wanted)
        {
            query = query.Where(i => i.Status == wanted);
        }

        if (customerId is { } id)
        {
            query = query.Where(i => i.CustomerId == id);
        }

        if (!string.IsNullOrWhiteSpace(customerName))
        {
            query = query.Where(i => EF.Functions.ILike(i.Customer!.Name, $"%{customerName.Trim()}%"));
        }

        if (from is { } fromDate)
        {
            query = query.Where(i => i.DueDate >= fromDate);
        }

        if (to is { } toDate)
        {
            query = query.Where(i => i.DueDate <= toDate);
        }

        if (overdue is true)
        {
            query = query.Where(i => i.Status == InvoiceStatus.Sent && i.DueDate < today);
        }
        else if (overdue is false)
        {
            query = query.Where(i => i.Status != InvoiceStatus.Sent || i.DueDate >= today);
        }

        var pageSize = Math.Clamp(limit ?? settings.DefaultPageSize, 1, settings.MaxPageSize);
        var invoices = await query
            .OrderBy(i => i.DueDate)
            .ThenBy(i => i.Number)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var summaries = invoices.Select(i => i.ToSummary(today)).ToList();
        return Results.Ok(new InvoiceList(today, settings.Currency, summaries.Count, summaries));
    }

    private static async Task<IResult> GetAsync(
        string number,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var invoice = await FindAsync(db, number, tracking: false, cancellationToken);
        return invoice is null ? NotFound(number) : Results.Ok(invoice.ToDetail(clock.Today));
    }

    private static async Task<IResult> CreateDraftAsync(
        CreateInvoiceRequest request,
        AppDbContext db,
        IClock clock,
        IOptions<InvoicingOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (request.Lines is null or { Count: 0 })
        {
            throw new DomainException("empty_invoice", "An invoice needs at least one line.");
        }

        var customer = await ResolveCustomerAsync(db, request.CustomerId, request.CustomerName, cancellationToken);
        if (customer is null)
        {
            return Results.Problem(
                title: "Customer not found",
                detail: $"No customer matches '{request.CustomerName ?? request.CustomerId?.ToString()}'.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "customer_not_found" });
        }

        var issueDate = request.IssueDate ?? clock.Today;
        var dueDate = request.DueDate ?? issueDate.AddDays(settings.DefaultPaymentTermDays);

        var invoice = Invoice.CreateDraft(
            await NextNumberAsync(db, issueDate.Year, cancellationToken),
            customer,
            issueDate,
            dueDate,
            request.VatRate ?? settings.DefaultVatRate,
            request.Lines.Select(l => new NewInvoiceLine(l.Description, l.Quantity, l.UnitPrice)));

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);

        var created = await FindAsync(db, invoice.Number, tracking: false, cancellationToken);
        return Results.Created($"/api/invoices/{invoice.Number}", created!.ToDetail(clock.Today));
    }

    private static Task<IResult> SendAsync(
        string number,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken) =>
        TransitionAsync(number, db, clock, cancellationToken, invoice => invoice.Send());

    private static async Task<IResult> MarkPaidAsync(
        string number,
        ClaimsPrincipal principal,
        AppDbContext db,
        IClock clock,
        IOptions<InvoicingOptions> options,
        CancellationToken cancellationToken)
    {
        var invoice = await FindAsync(db, number, tracking: true, cancellationToken);
        if (invoice is null)
        {
            return NotFound(number);
        }

        var limit = options.Value.AccountantMarkPaidLimit;
        if (principal.Role() is Role.Accountant && invoice.Total > limit)
        {
            return Results.Problem(
                title: "Amount limit exceeded",
                detail: $"An Accountant can settle invoices up to {limit:0.00} {options.Value.Currency}; "
                    + $"{invoice.Number} totals {invoice.Total:0.00}. An Admin has to approve it.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "amount_limit_exceeded" });
        }

        invoice.MarkPaid(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(invoice.ToDetail(clock.Today));
    }

    private static Task<IResult> CancelAsync(
        string number,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken) =>
        TransitionAsync(number, db, clock, cancellationToken, invoice => invoice.Cancel());

    private static Task<IResult> UpdateDueDateAsync(
        string number,
        UpdateDueDateRequest request,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken) =>
        TransitionAsync(number, db, clock, cancellationToken, invoice => invoice.ChangeDueDate(request.DueDate));

    /// <summary>
    /// Load, apply the aggregate method, save. The transition rules themselves live in
    /// <see cref="Invoice"/>; a violation surfaces as a 409 via <see cref="DomainExceptionHandler"/>.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        string number,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken,
        Action<Invoice> transition)
    {
        var invoice = await FindAsync(db, number, tracking: true, cancellationToken);
        if (invoice is null)
        {
            return NotFound(number);
        }

        transition(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(invoice.ToDetail(clock.Today));
    }

    private static Task<Invoice?> FindAsync(AppDbContext db, string number, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Invoices.Include(i => i.Customer).Include(i => i.Lines).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.SingleOrDefaultAsync(i => i.Number == number, cancellationToken);
    }

    private static async Task<Customer?> ResolveCustomerAsync(
        AppDbContext db,
        Guid? customerId,
        string? customerName,
        CancellationToken cancellationToken)
    {
        if (customerId is { } id)
        {
            return await db.Customers.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(customerName))
        {
            throw new DomainException("customer_required", "Either a customer id or a customer name is required.");
        }

        var name = customerName.Trim();
        var matches = await db.Customers
            .Where(c => EF.Functions.ILike(c.Name, $"%{name}%"))
            .OrderBy(c => c.Name)
            .Take(5)
            .ToListAsync(cancellationToken);

        return matches switch
        {
            [] => null,
            [var single] => single,
            // An ambiguous name is a question for the user, not a coin flip for the model.
            _ => throw new DomainException(
                "ambiguous_customer",
                $"'{name}' matches {matches.Count} customers ({string.Join(", ", matches.Select(c => c.Name))}). Be more specific."),
        };
    }

    private static async Task<string> NextNumberAsync(AppDbContext db, int year, CancellationToken cancellationToken)
    {
        var prefix = $"{year:0000}-";
        var lastNumber = await db.Invoices
            .Where(i => i.Number.StartsWith(prefix))
            .OrderByDescending(i => i.Number)
            .Select(i => i.Number)
            .FirstOrDefaultAsync(cancellationToken);

        var lastSequence = lastNumber is null ? 0 : int.Parse(lastNumber[prefix.Length..]);
        return Invoice.FormatNumber(year, lastSequence + 1);
    }

    private static IResult NotFound(string number) => Results.Problem(
        title: "Invoice not found",
        detail: $"There is no invoice with number '{number}'.",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?> { ["code"] = "invoice_not_found" });

    private static IResult InvalidStatus(string status) => Results.Problem(
        title: "Unknown status",
        detail: $"'{status}' is not a valid status. Valid values: {string.Join(", ", Enum.GetNames<InvoiceStatus>())}. "
            + "An invoice past its due date is Sent with overdue=true, not a status of its own.",
        statusCode: StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["code"] = "invalid_status" });
}
