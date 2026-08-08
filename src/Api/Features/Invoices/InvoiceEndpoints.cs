using System.Security.Claims;
using System.Text.Json;
using Api.Domain;
using Api.Infrastructure.Delivery;
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

        // Writes require an Idempotency-Key, and the filter owns the transaction they run in: the
        // business change and the receipt that lets a retry replay it commit together or not at all.
        // Nothing in this group may call an external service — see TransactionalIdempotencyFilter.
        var writes = routes.MapGroup("/api/invoices")
            .WithTags("Invoices")
            .RequireAuthorization()
            .AddEndpointFilter<TransactionalIdempotencyFilter>();

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

        // Counted before the page is taken. The assistant reads this payload as-is, so a page that
        // does not say how much it left behind is a wrong answer waiting to be relayed as a fact.
        var total = await query.CountAsync(cancellationToken);

        var invoices = await query
            .OrderBy(i => i.DueDate)
            .ThenBy(i => i.Number)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var summaries = invoices.Select(i => i.ToSummary(today)).ToList();
        return Results.Ok(new InvoiceList(
            today, settings.Currency, summaries.Count, total, total > summaries.Count, summaries));
    }

    private static async Task<IResult> GetAsync(
        string number,
        HttpContext http,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var invoice = await FindAsync(db, number, tracking: false, cancellationToken);
        return invoice is null ? NotFound(number) : await DetailAsync(http, db, invoice, clock.Today, cancellationToken);
    }

    private static async Task<IResult> CreateDraftAsync(
        CreateInvoiceRequest request,
        HttpContext http,
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
        http.Response.Headers.ETag = InvoicePrecondition.ETagFor(created!.Revision);

        return Results.Created($"/api/invoices/{invoice.Number}", created.ToDetail(clock.Today));
    }

    /// <summary>
    /// Moves the invoice out of draft and queues the delivery, in one transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answers <c>202</c>, not <c>200</c>, and the difference is the honest part: the ledger change
    /// is done, the email is not. Nothing here calls the provider — that would put somebody else's
    /// network inside our transaction — so what commits is an <see cref="InvoiceDelivery"/> and the
    /// outbox row that will carry it, alongside the status change and the idempotency receipt.
    /// </para>
    /// <para>
    /// The response carries the delivery, so <c>Sent</c> never has to double as a claim that the
    /// customer received anything.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SendAsync(
        string number,
        HttpContext http,
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

        if (InvoicePrecondition.Check(http.Request, invoice) is { } stale)
        {
            return stale;
        }

        invoice.Send();

        var delivery = new InvoiceDelivery
        {
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.Number,
            ExecutionId = await ExecutionBehindAsync(http, db, cancellationToken),
            ProviderKey = Guid.CreateVersion7().ToString(),
            Recipient = invoice.Customer?.Email ?? string.Empty,
            CreatedAt = clock.UtcNow,
        };

        var payload = new InvoiceDeliveryPayload(
            invoice.Number,
            delivery.Recipient,
            invoice.Customer?.Name ?? string.Empty,
            invoice.Total,
            options.Value.Currency,
            invoice.DueDate);

        db.InvoiceDeliveries.Add(delivery);
        db.OutboxMessages.Add(OutboxMessage.ForDelivery(
            delivery, JsonSerializer.Serialize(payload, DeliveryJson), clock.UtcNow));

        await db.SaveChangesAsync(cancellationToken);

        http.Response.Headers.ETag = InvoicePrecondition.ETagFor(invoice.Revision);

        return Results.Accepted(
            $"/api/invoices/{invoice.Number}",
            invoice.ToDetail(clock.Today, delivery));
    }

    /// <summary>
    /// The assistant execution this request belongs to, if any. The idempotency key of an
    /// assistant write <em>is</em> its execution id, so the delivery can be linked back without the
    /// caller having to say so — and a key from curl or the SPA simply matches nothing.
    /// </summary>
    private static async Task<Guid?> ExecutionBehindAsync(
        HttpContext http,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var candidate))
        {
            return null;
        }

        return await db.ActionExecutions.AsNoTracking().AnyAsync(e => e.Id == candidate, cancellationToken)
            ? candidate
            : null;
    }

    private static async Task<IResult> MarkPaidAsync(
        string number,
        HttpContext http,
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

        if (InvoicePrecondition.Check(http.Request, invoice) is { } stale)
        {
            return stale;
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
        return await DetailAsync(http, db, invoice, clock.Today, cancellationToken);
    }

    private static Task<IResult> CancelAsync(
        string number,
        HttpContext http,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken) =>
        TransitionAsync(number, http, db, clock, cancellationToken, invoice => invoice.Cancel());

    private static Task<IResult> UpdateDueDateAsync(
        string number,
        UpdateDueDateRequest request,
        HttpContext http,
        AppDbContext db,
        IClock clock,
        CancellationToken cancellationToken) =>
        TransitionAsync(number, http, db, clock, cancellationToken, invoice => invoice.ChangeDueDate(request.DueDate));

    /// <summary>
    /// Load, apply the aggregate method, save. The transition rules themselves live in
    /// <see cref="Invoice"/>; a violation surfaces as a 409 via <see cref="DomainExceptionHandler"/>.
    /// </summary>
    private static async Task<IResult> TransitionAsync(
        string number,
        HttpContext http,
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

        if (InvoicePrecondition.Check(http.Request, invoice) is { } stale)
        {
            return stale;
        }

        transition(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(http, db, invoice, clock.Today, cancellationToken);
    }

    /// <summary>
    /// An invoice, the ETag a later conditional write is checked against, and its delivery. All
    /// three travel together so a caller never has to guess which revision the body it is holding
    /// was, or read "Sent" as "the customer has it".
    /// </summary>
    private static async Task<IResult> DetailAsync(
        HttpContext http,
        AppDbContext db,
        Invoice invoice,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var delivery = await db.InvoiceDeliveries.AsNoTracking()
            .Where(d => d.InvoiceId == invoice.Id)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        http.Response.Headers.ETag = InvoicePrecondition.ETagFor(invoice.Revision);
        return Results.Ok(invoice.ToDetail(today, delivery));
    }

    /// <summary>The payload the outbox carries. Web defaults, so it round-trips as the API writes it.</summary>
    private static readonly JsonSerializerOptions DeliveryJson = new(JsonSerializerDefaults.Web);

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
