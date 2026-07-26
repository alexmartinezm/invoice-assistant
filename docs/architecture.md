# Architecture

## Vision

invoice-assistant demonstrates an AI assistant embedded in an invoicing app with production-system guarantees. The LLM orchestrates (picks tools, writes responses); the server calculates, authorizes, persists and executes.

## Domain

Persisted today (F1):

- **User**: id, email, displayName, role, passwordHash.
- **Customer**: id, name, taxId, email.
- **Invoice**: id, number (`2026-0001`), customerId, status, issueDate, dueDate, lines[], subtotal, vatRate, vatAmount, total, paidAt?.
- **InvoiceLine**: description, quantity, unitPrice, amount.
- **Conversation / Message**: chat history per user, with the system prompt hash on the conversation.

Arriving with the write gate (F2), and deliberately not created before there is anything to write:

- **AuditEvent**: timestamp, userId, action, toolName?, payload (json), decision (`auto|confirmed|denied|blocked`), conversationId?.
- **PendingAction**: id, userId, toolName, argsJson, summary, createdAt, expiresAt, status (`pending|approved|rejected|expired`).

Identifiers are UUIDv7 generated in the domain and mapped `ValueGeneratedNever` — see ADR 006 for
why that is load-bearing rather than cosmetic. Every monetary figure is computed by the `Invoice`
aggregate: line amounts, subtotal, VAT and total have no public setters, so no caller and no model
can supply one.

Invoice states (enforced in the domain, not in the prompt):

```text
Draft → Sent → Paid
Draft|Sent → Cancelled
Sent → Overdue        (derived from dates, not persisted)
```

Marking a `Draft` invoice as paid or cancelling a `Paid` one fails in the API with a clear domain error. The assistant reports that error; it never pretends the operation worked (there is an eval for this).

## Seed users

Shared demo password: `demo1234`.

| User | Role | Headline |
|---|---|---|
| ana@demo | Admin | Everything |
| carlos@demo | Accountant | Read everything; create drafts; mark paid up to €1,000 |
| lucia@demo | Viewer | Read-only |

The full matrix, enforced per endpoint:

| Action | Viewer | Accountant | Admin |
|---|---|---|---|
| Read invoices, customers, receivables | ✅ | ✅ | ✅ |
| Create draft | ❌ | ✅ | ✅ |
| Send | ❌ | ✅ | ✅ |
| Change due date | ❌ | ✅ | ✅ |
| Mark paid | ❌ | ✅ up to €1,000 | ✅ |
| Cancel | ❌ | ❌ | ✅ |

Cancelling is Admin-only because it is the one business action that cannot be undone. The €1,000
ceiling is `Invoicing:AccountantMarkPaidLimit`, checked by the endpoint — so it applies to curl
exactly as it applies to the assistant.

Seed: 8 customers and 41 invoices, generated relative to today so every aging bucket is populated
whichever day the repo is cloned. One overdue invoice carries a prompt injection in a line
description on purpose: seed data is untrusted input too, and the F3 injection cases read it back
through `get_invoice`.

## Business API

Classic REST with role-based authZ on every endpoint (the assistant's write gate is an additional layer, not the only one):

- `POST /api/auth/login` → JWT (60 min) · `GET /api/auth/me`
- `GET /api/customers?query=`, `GET /api/customers/{id}`
- `GET /api/invoices?status=&customerId=&customerName=&from=&to=&overdue=&limit=`
- `GET /api/invoices/{number}`
- `POST /api/invoices` (creates Draft; resolves the customer by id or by name)
- `POST /api/invoices/{number}/send`
- `POST /api/invoices/{number}/mark-paid` (validates the per-role amount limit)
- `POST /api/invoices/{number}/cancel`
- `PATCH /api/invoices/{number}/due-date`
- `GET /api/reports/receivables` (aging buckets: current, 1-30, 31-60, 60+)
- `POST /api/chat` (SSE) · `GET /api/assistant/tools`

Query semantics worth stating once, because the tool descriptions depend on them: `status` accepts
only the four persisted values, `overdue=true` selects invoices that were sent and are past due, and
`from`/`to` bound the **due** date — which is what "what is overdue" and "what falls due this month"
are actually asking about.

A domain rule violation is a `409` whose body carries a machine-readable `code`
(`invalid_transition`, `invalid_due_date`, `ambiguous_customer`, …). Tool results are handed to the
model verbatim, so these errors are written to be relayed as-is.

Writes will use an idempotency key (`Idempotency-Key` header) from F2, together with the audit trail
and pending actions; the assistant's executor will always send one.

## Anatomy of a chat turn

The target shape, reached at F2:

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

What `ChatOrchestrator` does today, with step 3 still a straight execution and step 5 not yet
written: load or create the conversation (recording the prompt hash), build system prompt + bounded
history + the new message, stream from the model with the tool catalog attached, and save the turn.
Tool calls are executed by `Microsoft.Extensions.AI`'s function-invoking client, and each one goes
over HTTP to our own API carrying the caller's bearer token.

The SSE events are `conversation` (with the trace id), `activity` (a tool starting or finishing),
`token`, `done` and `error`. Once the response has started there is no status code left to fail with,
so a failure mid-turn arrives as an `error` event rather than an HTTP error.

Tool results are returned to the model as parsed JSON, not as a JSON string: a stringified result is
re-encoded into the conversation with every quote escaped, which costs tokens and buries the data a
level deeper than the model expects.

## Tool catalog

Every tool declares: `name`, `description`, args JSON schema, `sideEffect: read|write`, `requiredRole`, `riskLevel`.

**Read** (auto-execute, implemented): `list_invoices`, `get_invoice`, `search_customers`,
`get_receivables_summary` (aging computed by the API, never by the model).

**Write** (F2; confirmation unless an explicit rule applies): `create_draft_invoice`, `send_invoice`,
`mark_invoice_paid`, `cancel_invoice`, `update_due_date`.

Tool parameter names are snake_case because they are a public contract: they appear in the schema
sent to the model and in the eval cases under `evals/cases/`, so renaming one is a breaking change.
`GET /api/assistant/tools` returns the live catalog with each tool's metadata.

**Capability boundary:** there is no delete tool, no bulk operations, no admin. Whatever is not in the catalog is physically impossible. The first line of defense is which tools you expose, not what you forbid in the prompt.

## Cross-cutting security

- AuthZ on every business endpoint: if someone calls the API without going through the assistant, the rules still apply.
- Rate limit on `/api/chat`, partitioned per user rather than per IP (20 turns/minute by default), plus a 2,000-character input cap.
- No CORS configuration anywhere: the Vite dev server proxies `/api`, and in production the Api serves the built SPA from `wwwroot`. The browser only ever talks to one origin.
- Model output is rendered as sanitized markdown, never as raw HTML — and neither is invoice line text, which is customer-supplied.
- Secrets only via environment variables; never user data in info-level logs.
- Bounded conversation history and per-turn token budget.
- Spend kill switch (F4): global daily cap in € evaluated before every model call; once reached, `/api/chat` returns 429.

## Recorded decisions

See [`docs/adr/`](adr/). Changing an existing decision requires a new ADR that supersedes it, not a silent edit.
