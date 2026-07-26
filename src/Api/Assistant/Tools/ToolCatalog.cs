using Microsoft.Extensions.AI;

namespace Api.Assistant.Tools;

/// <summary>
/// Everything the assistant is able to do. There is no delete tool, no bulk operation and no
/// administration tool: whatever is not declared here is unreachable no matter what the model is
/// asked to do. The first line of defence is the size of this list, not the wording of the prompt.
/// </summary>
/// <remarks>
/// The write tools are here from F2 onwards, which does not widen that boundary — every one of them
/// runs through <see cref="ToolGate"/>, and each affects exactly one invoice that the caller names.
/// </remarks>
public sealed class ToolCatalog
{
    public ToolCatalog(ReadTools readTools, WriteTools writeTools)
    {
        Tools =
        [
            Define(readTools.ListInvoicesAsync, ReadToolCatalog.ListInvoices, ToolRiskLevel.Low),
            Define(readTools.GetInvoiceAsync, ReadToolCatalog.GetInvoice, ToolRiskLevel.Low),
            Define(readTools.SearchCustomersAsync, ReadToolCatalog.SearchCustomers, ToolRiskLevel.Low),
            Define(readTools.GetReceivablesSummaryAsync, ReadToolCatalog.GetReceivablesSummary, ToolRiskLevel.Low),

            Define(writeTools.CreateDraftInvoiceAsync, WriteToolCatalog.CreateDraftInvoice, ToolRiskLevel.Medium),
            Define(writeTools.SendInvoiceAsync, WriteToolCatalog.SendInvoice, ToolRiskLevel.Medium),
            Define(writeTools.MarkInvoicePaidAsync, WriteToolCatalog.MarkInvoicePaid, ToolRiskLevel.High),
            Define(writeTools.CancelInvoiceAsync, WriteToolCatalog.CancelInvoice, ToolRiskLevel.High),
            Define(writeTools.UpdateDueDateAsync, WriteToolCatalog.UpdateDueDate, ToolRiskLevel.Medium),
        ];
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public IList<AITool> ChatTools => [.. Tools.Select(definition => (AITool)definition.Function)];

    public ToolDefinition? Find(string name) =>
        Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Binds a delegate to the identity the gate evaluates it under. One identity per tool, shared
    /// with <see cref="ToolGate"/>, so the name in the model's schema and the name a policy rule
    /// matches on cannot drift apart.
    /// </summary>
    private static ToolDefinition Define(Delegate method, ToolIdentity identity, ToolRiskLevel risk) => new(
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = identity.Name }),
        identity.SideEffect,
        identity.RequiredRole,
        risk);
}
