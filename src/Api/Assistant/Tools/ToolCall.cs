using System.Text.Json;

namespace Api.Assistant.Tools;

/// <summary>The request a tool makes against our own API, before anything has decided it may run.</summary>
public sealed record ToolCall(HttpMethod Method, string Url, object? Body = null);

/// <summary>
/// The mapping from a tool and its arguments to the API call it makes.
/// </summary>
/// <remarks>
/// It lives apart from <see cref="WriteTools"/> because there are two ways into it: the model
/// calling a tool now, and an approval replaying arguments frozen minutes ago. Those two must
/// produce the same request — an approval that executed something subtly different from what the
/// card described would defeat the point of asking.
/// </remarks>
public static class WriteToolPlans
{
    public static ToolCall CreateDraftInvoice(string customerName, IEnumerable<object> lines) =>
        new(HttpMethod.Post, "/api/invoices", new { customerName, lines });

    public static ToolCall SendInvoice(string number) =>
        new(HttpMethod.Post, $"/api/invoices/{Escape(number)}/send");

    public static ToolCall MarkInvoicePaid(string number) =>
        new(HttpMethod.Post, $"/api/invoices/{Escape(number)}/mark-paid");

    public static ToolCall CancelInvoice(string number) =>
        new(HttpMethod.Post, $"/api/invoices/{Escape(number)}/cancel");

    public static ToolCall UpdateDueDate(string number, DateOnly dueDate) =>
        new(HttpMethod.Patch, $"/api/invoices/{Escape(number)}/due-date", new { dueDate });

    /// <summary>
    /// Rebuilds a call from the arguments a <see cref="Domain.PendingAction"/> froze. Unknown tool
    /// names throw rather than fall through: a pending row naming a tool this build does not have is
    /// a deployment that changed underneath an open approval, and executing a guess is not the fix.
    /// </summary>
    public static (ToolIdentity Tool, ToolCall Call) Replay(string toolName, JsonElement args) => toolName switch
    {
        "create_draft_invoice" => (
            WriteToolCatalog.CreateDraftInvoice,
            CreateDraftInvoice(
                Text(args, "customer_name"),
                args.GetProperty("lines").EnumerateArray().Select(line => (object)new
                {
                    description = Text(line, "description"),
                    quantity = line.GetProperty("quantity").GetDecimal(),
                    unitPrice = line.GetProperty("unit_price").GetDecimal(),
                }).ToList())),

        "send_invoice" => (WriteToolCatalog.SendInvoice, SendInvoice(Text(args, "number"))),
        "mark_invoice_paid" => (WriteToolCatalog.MarkInvoicePaid, MarkInvoicePaid(Text(args, "number"))),
        "cancel_invoice" => (WriteToolCatalog.CancelInvoice, CancelInvoice(Text(args, "number"))),

        "update_due_date" => (
            WriteToolCatalog.UpdateDueDate,
            UpdateDueDate(Text(args, "number"), DateOnly.Parse(Text(args, "new_due_date")))),

        _ => throw new InvalidOperationException($"'{toolName}' is not a write tool this build knows about."),
    };

    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidOperationException($"Frozen arguments are missing '{property}'.");

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
