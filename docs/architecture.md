# Architecture

## Vision

invoice-assistant demonstrates an AI assistant embedded in an invoicing app with production-system guarantees. The LLM orchestrates (picks tools, writes responses); the server calculates, authorizes, persists and executes.

## Domain

Minimal entities:

- **Customer**: id, name, taxId, email.
- **Invoice**: id, number (`2026-0001`), customerId, status, issueDate, dueDate, lines[], subtotal, vatRate, vatAmount, total, paidAt?.
- **InvoiceLine**: description, quantity, unitPrice, amount.
- **AuditEvent**: timestamp, userId, action, toolName?, payload (json), decision (`auto|confirmed|denied|blocked`), conversationId?.
- **Conversation / Message**: chat history per user.
- **PendingAction**: id, userId, toolName, argsJson, summary, createdAt, expiresAt, status (`pending|approved|rejected|expired`).

Invoice states (enforced in the domain, not in the prompt):

```text
Draft → Sent → Paid
Draft|Sent → Cancelled
Sent → Overdue        (derived from dates, not persisted)
```

Marking a `Draft` invoice as paid or cancelling a `Paid` one fails in the API with a clear domain error. The assistant reports that error; it never pretends the operation worked (there is an eval for this).

## Seed users

| User | Role | Can |
|---|---|---|
| ana@demo | Admin | Everything |
| carlos@demo | Accountant | Read everything; create drafts; mark paid up to €1,000 |
| lucia@demo | Viewer | Read-only |

Seed: ~8 customers, ~40 invoices spread across states and dates.

## Business API

Classic REST with role-based authZ on every endpoint (the assistant's write gate is an additional layer, not the only one):

- `POST /api/auth/login` → JWT (60 min)
- `GET /api/customers`, `GET /api/customers/{id}`
- `GET /api/invoices?status=&customerId=&from=&to=&overdue=`
- `GET /api/invoices/{number}`
- `POST /api/invoices` (creates Draft)
- `POST /api/invoices/{number}/send`
- `POST /api/invoices/{number}/mark-paid` (validates per-role amount limit)
- `POST /api/invoices/{number}/cancel`
- `PATCH /api/invoices/{number}/due-date`
- `GET /api/reports/receivables` (aging buckets: current, 1-30, 31-60, 60+)

Writes use an idempotency key (`Idempotency-Key` header); the assistant's executor always sends it.

## Anatomy of a chat turn

```text
UI ──POST /api/chat (SSE)──► ChatOrchestrator
        │ 1. loads history + system prompt (prompts/system.md, versioned)
        │ 2. IChatClient.GetStreamingResponseAsync with registered tools
        │ 3. for each model tool call:
        │      ToolPolicyEngine.Evaluate(tool, args, user)
        │        ├─ Allow          → ToolExecutor → HTTP to our own API with the user's JWT
        │        ├─ RequireConfirm → creates PendingAction → SSE `approval_required` → ends the turn
        │        └─ Deny           → error to the model + AuditEvent `blocked` → the model explains it
        │ 4. text streaming + activity events
        └─ 5. UsageCollector records tokens/cost/latency for the turn
```

## Tool catalog

Every tool declares: `name`, `description`, args JSON schema, `sideEffect: read|write`, `requiredRole`, `riskLevel`.

**Read** (auto-execute): `list_invoices`, `get_invoice`, `search_customers`, `get_receivables_summary` (aging computed by the API, never by the model).

**Write** (confirmation unless an explicit rule applies): `create_draft_invoice`, `send_invoice`, `mark_invoice_paid`, `cancel_invoice`, `update_due_date`.

**Capability boundary:** there is no delete tool, no bulk operations, no admin. Whatever is not in the catalog is physically impossible. The first line of defense is which tools you expose, not what you forbid in the prompt.

## Cross-cutting security

- AuthZ on every business endpoint: if someone calls the API without going through the assistant, the rules still apply.
- Rate limit on `/api/chat` and input size cap.
- Secrets only via environment variables; never user data in info-level logs.
- Bounded conversation history and per-turn token budget.
- Spend kill switch: global daily cap in € evaluated before every model call; once reached, `/api/chat` returns 429.

## Recorded decisions

See [`docs/adr/`](adr/). Changing an existing decision requires a new ADR that supersedes it, not a silent edit.
