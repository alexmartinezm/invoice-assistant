# ADR 001 · Write gate on the server, not in the prompt

**Status:** accepted · 2026-07

## Context

An assistant with write tools can be manipulated (direct or indirect prompt injection) or simply make mistakes. Asking for good behavior in the system prompt provides no guarantee.

## Decision

Every write goes through a server-side `ToolPolicyEngine` that evaluates `policies.json` rules before executing:

- `allow` → executes and audits as `auto`.
- `require_confirmation` → creates a `PendingAction` (human-readable summary, frozen args, expires after 5 min, single use) and ends the turn; a human approves or rejects.
- `deny` → does not execute; the model receives the error and must communicate it; audits as `denied`.

Per-turn limits as anti-injection and anti-loop brakes: `maxWritesPerTurn: 1`, `maxWritesPerConversation: 5`, `maxToolCallsPerTurn: 8`; excesses audit as `blocked`.

Approval (`POST /api/actions/{id}/approve|reject`) executes with the approver's JWT, re-validating policy and role at that moment, not at proposal time. After execution, the result is injected as a tool result and the model produces a closing turn.

## Consequences

- The system prompt contains no critical security rules: the prompt is UX, the policy is security.
- No write reaches the DB without an explicit `allow` or a human approval recorded in `AuditEvent` (acceptance criterion verified by evals against the DB and the audit trail, not against response text).
