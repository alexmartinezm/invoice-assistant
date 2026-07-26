using Api.Domain;
using Microsoft.Extensions.AI;

namespace Api.Assistant.Tools;

/// <summary>
/// Everything the assistant is able to do. There is no delete tool, no bulk operation and no
/// administration tool: whatever is not declared here is unreachable no matter what the model is
/// asked to do. The first line of defence is the size of this list, not the wording of the prompt.
/// </summary>
public sealed class ToolCatalog
{
    public ToolCatalog(ReadTools readTools)
    {
        Tools =
        [
            Read(readTools.ListInvoicesAsync, "list_invoices"),
            Read(readTools.GetInvoiceAsync, "get_invoice"),
            Read(readTools.SearchCustomersAsync, "search_customers"),
            Read(readTools.GetReceivablesSummaryAsync, "get_receivables_summary"),

            // F2 adds the write tools here: create_draft_invoice, send_invoice,
            // mark_invoice_paid, cancel_invoice and update_due_date.
        ];
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public IList<AITool> ChatTools => [.. Tools.Select(definition => (AITool)definition.Function)];

    public ToolDefinition? Find(string name) =>
        Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Read tools carry no side effect, so the default policy auto-executes them and every
    /// authenticated role may call them — the endpoint behind each one still filters by identity.
    /// </summary>
    private static ToolDefinition Read(Delegate method, string name) => new(
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name }),
        ToolSideEffect.Read,
        Role.Viewer,
        ToolRiskLevel.Low);
}
