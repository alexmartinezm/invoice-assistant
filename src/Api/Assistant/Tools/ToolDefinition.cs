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
/// A tool plus the metadata the write gate decides on. The policy engine matches rules
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

    public ToolIdentity Identity => new(Name, SideEffect, RequiredRole);
}

/// <summary>
/// The part of a tool the policy engine actually decides on. Kept separate from
/// <see cref="ToolDefinition"/> so the gate does not need the catalog — the catalog builds the
/// tools, and a tool that needed the catalog to gate itself would be a cycle.
/// </summary>
public sealed record ToolIdentity(string Name, ToolSideEffect SideEffect, Role RequiredRole);
