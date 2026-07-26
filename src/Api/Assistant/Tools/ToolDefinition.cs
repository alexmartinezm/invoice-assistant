using Api.Domain;
using Microsoft.Extensions.AI;

namespace Api.Assistant.Tools;

public enum ToolSideEffect
{
    Read,
    Write,
}

public enum ToolRiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// A tool plus the metadata the write gate decides on. The policy engine (F2) matches rules
/// against <see cref="SideEffect"/> and <see cref="RequiredRole"/> — never against the tool name
/// pattern — so adding a tool cannot silently escape an existing rule.
/// </summary>
public sealed record ToolDefinition(
    AIFunction Function,
    ToolSideEffect SideEffect,
    Role RequiredRole,
    ToolRiskLevel RiskLevel)
{
    public string Name => Function.Name;

    public string Description => Function.Description;
}
