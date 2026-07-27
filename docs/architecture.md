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

Added by the write gate (F2), and deliberately not created before there was anything to write:

- **AuditEvent**: timestamp, userId, action, toolName?, payload (json), decision (`auto|confirmed|denied|blocked`), conversationId?.
- **PendingAction**: id, userId, toolName, argsJson, summary, createdAt, expiresAt, status (`pending|approved|rejected|expired`).
- **IdempotencyRecord**: key, userId, operation, statusCode, response (json), createdAt — one write, remembered for 24 hours, so a retry returns the first answer.

Added by cost accounting (F4):

- **UsageRecord**: conversationId?, userId, model, promptTokens, completionTokens, toolCallCount,
  costEur, latencyMs, createdAt — one row per **model call**, not per turn, because a turn that uses
  tools makes several (ADR 008).

An allowed **read** produces no `AuditEvent`. That is deliberate: it keeps `auto` meaning exactly
"a change happened and nobody approved it", which is the thing `evals/cases/injection-03.yaml`
asserts the absence of. A read the gate *refuses* is audited — see ADR 007.

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

Shared demo password: `demo1234`. The three addresses are fixed in every market — the docs, the
eval cases and the tests all name them — while the display names follow `Invoicing:Market`.

| User | Role | Headline |
|---|---|---|
| ana@demo | Admin | Everything |
| carlos@demo | Accountant | Read everything; create drafts; mark paid up to the settlement ceiling |
| lucia@demo | Viewer | Read-only |

The full matrix, enforced per endpoint:

| Action | Viewer | Accountant | Admin |
|---|---|---|---|
| Read invoices, customers, receivables | ✅ | ✅ | ✅ |
| Create draft | ❌ | ✅ | ✅ |
| Send | ❌ | ✅ | ✅ |
| Change due date | ❌ | ✅ | ✅ |
| Mark paid | ❌ | ✅ up to the ceiling | ✅ |
| Cancel | ❌ | ❌ | ✅ |

Cancelling is Admin-only because it is the one business action that cannot be undone. The ceiling is
`Invoicing:AccountantMarkPaidLimit` (€1,000 by default), checked by the endpoint — so it applies to
curl exactly as it applies to the assistant.

Seed: 8 customers and 41 invoices, generated relative to today so every aging bucket is populated
whichever day the repo is cloned. One overdue invoice carries a prompt injection in a line
description on purpose: seed data is untrusted input too, and the F3 injection cases read it back
through `get_invoice`.

The customers come from `MarketFixtures`, which ships sets for `es-ES`, `en-US`, `en-GB` and
`de-DE` — each with the company forms, tax-identifier shape and email domains that market actually
uses (`B12345678` for a Spanish NIF, `41-2039571` for a US EIN, `GB123456789` for a UK VAT number).
An unrecognised market falls back rather than refusing to boot, preferring the same language first
and logging what it chose: quietly serving Spanish customers to someone who configured Japan is the
kind of thing that only surfaces mid-demo.

Note the split between `Market` and `Locale`. The locale formats figures for whoever is reading;
the market decides whose ledger it is. A London controller reviewing a Spanish subsidiary is a real
arrangement, so the two are allowed to differ and neither derives from the other.

Company names and tax identifiers are data, so they stay in the form their market uses. Everything
the repo *writes* — line item descriptions, comments, documentation — is English regardless.

## Money, tax and locale

Everything about money is configuration under `Invoicing:` — see `.env.example` for the environment
variable names:

| Setting | Default | Reaches |
|---|---|---|
| `Currency` | `EUR` | The `currency` field on every list and report response, and the SPA's number formatting |
| `CurrencySymbol` | `€` | Only the example inside the assistant's formatting instruction |
| `Locale` | `en-GB` | Number and date rendering on both sides |
| `Market` | `es-ES` | Which seed fixtures: customers, tax-id shape, email domains, staff names |
| `TaxLabel` | `VAT` | The tax line on the invoice detail |
| `DefaultVatRate` | `0.21` | New drafts that do not specify a rate, and the seeded ledger |
| `ReducedVatRate` | `0.10` | The lower rate sprinkled through the seed |
| `AccountantMarkPaidLimit` | `1000` | The endpoint's authorization check and the login screen's copy |

The point is that there is exactly one source. `GET /api/config` hands the display settings to the
SPA before its first paint, and `SystemPromptProvider` renders the same values into the assistant's
formatting instruction — so the chat and the table beside it cannot disagree about how a number
looks. `InvoicingConfigurationTests` boots the whole app as a US deployment and checks that,
including that the seeder stopped using constants of its own.

One subtlety worth knowing: the prompt hash recorded on each conversation covers the *rendered*
prompt, not the file on disk. Two deployments running the same `prompts/system.md` against different
currencies were not given the same instruction, and the hash should say so.

## Business API

Classic REST with role-based authZ on every endpoint (the assistant's write gate is an additional layer, not the only one):

- `POST /api/auth/login` → JWT (60 min) · `GET /api/auth/me`
- `GET /api/auth/demo-users` (anonymous) — the seeded users for the login selector, so the SPA keeps no second copy of names that vary by market. The shared password is deliberately not returned.
- `GET /api/config` (anonymous) — currency, locale, tax label and the settlement ceiling
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
- `GET /api/usage/summary?from=&to=` · `GET /api/usage/conversations` · `GET /api/usage/conversations/{id}`

Query semantics worth stating once, because the tool descriptions depend on them: `status` accepts
only the four persisted values, `overdue=true` selects invoices that were sent and are past due, and
`from`/`to` bound the **due** date — which is what "what is overdue" and "what falls due this month"
are actually asking about.

A domain rule violation is a `409` whose body carries a machine-readable `code`
(`invalid_transition`, `invalid_due_date`, `ambiguous_customer`, …). Tool results are handed to the
model verbatim, so these errors are written to be relayed as-is.

- `POST /api/actions/{id}/approve` · `POST /api/actions/{id}/reject` · `GET /api/actions/{id}`

Writes accept an `Idempotency-Key` header, and the assistant always sends one: a tool call can be
retried by the model, by the HTTP stack, or by a user clicking approve twice, and "send the invoice"
is not something to do twice because a socket hiccuped. The same key on a *different* operation is a
`409 idempotency_key_reused` rather than a silent replay of the other operation's response.

## Anatomy of a chat turn

```text
UI ──POST /api/chat (SSE)──► ChatOrchestrator
        │ 0. daily budget check → 429 daily_budget_exhausted if it is spent   (F4)
        │ 1. loads history + system prompt (prompts/system.md, versioned)
        │ 2. IChatClient.GetStreamingResponseAsync with registered tools
        │      └─ UsageCollector meters each model call and re-checks the budget  (F4)
        │ 3. every tool call, read or write, enters ToolGate:
        │      per-turn limits → ToolPolicyEngine.Evaluate(tool, args, role)
        │        ├─ Allow          → HTTP to our own API with the user's JWT; writes audit `auto`
        │        ├─ RequireConfirm → PendingAction + TurnJournal → SSE `approval_required`
        │        └─ Deny           → AuditEvent `denied` + SSE `blocked` → the model explains it
        │      a limit exceeded    → AuditEvent `blocked` + SSE `blocked`
        │      (a write proposal spends the per-turn write budget too — ADR 007)
        └─ 4. text streaming + activity events, draining the journal after each tool result
```

`ChatOrchestrator` reads top to bottom: load or create the conversation (recording the prompt hash),
build system prompt + bounded history + the new message, stream from the model with the tool catalog
attached, save the turn. Tool calls are executed by `Microsoft.Extensions.AI`'s function-invoking
client, and each one goes over HTTP to our own API carrying the caller's bearer token.

**The gate is not in the orchestrator, and cannot be.** That middleware owns execution; the
orchestrator only watches it. So the gate sits inside the tool delegates — the one choke point every
call must pass — and a scoped `TurnJournal` carries approvals and blocks back out, which step 4
drains into the stream. This is the single extra file jump a reader has to make; ADR 007 explains
the trade and why the alternative was worse.

The SSE events are `conversation` (with the trace id), `activity` (a tool starting or finishing),
`token`, `approval_required`, `blocked`, `done` and `error`. Once the response has started there is
no status code left to fail with, so a failure mid-turn arrives as an `error` event rather than an
HTTP error. `blocked` is separate from `error` because nothing went wrong: the system did its job.

## Two ceilings, layered

`policies.json` lets an Accountant settle invoices up to **100** without asking; the endpoint's own
`Invoicing:AccountantMarkPaidLimit` is **1000**. That is not a contradiction, it is the whole
argument of the repo in three rows:

| Invoice total | What happens |
|---|---|
| ≤ 100 | Policy `allow` → executes, audited `auto` |
| 100 – 1000 | Policy `require_confirmation` → `PendingAction`; audited `confirmed` on approval |
| > 1000 | A human approves — and the **API still refuses** an Accountant (`amount_limit_exceeded`) |

The third row is the interesting one: the person said yes and the server said no anyway, because
endpoint authorization was never delegated to the gate. There is a test per band in
`tests/Api.Tests/WriteGateTests.cs`.

Tool results are returned to the model as parsed JSON, not as a JSON string: a stringified result is
re-encoded into the conversation with every quote escaped, which costs tokens and buries the data a
level deeper than the model expects.

## Tool catalog

Every tool declares: `name`, `description`, args JSON schema, `sideEffect: read|write`, `requiredRole`, `riskLevel`.

**Read** (`defaults.read` is `allow`, so they execute): `list_invoices`, `get_invoice`,
`search_customers`, `get_receivables_summary` (aging computed by the API, never by the model). They
still pass through the gate, which is what makes `defaults.read` a real setting rather than
decoration — a deployment can require confirmation for reads, or deny them for a role, without a
code change.

**Write** (confirmation unless an explicit rule applies): `create_draft_invoice`, `send_invoice`,
`mark_invoice_paid`, `cancel_invoice`, `update_due_date`.

Tool parameter names are snake_case because they are a public contract: they appear in the schema
sent to the model and in the eval cases under `evals/cases/`, so renaming one is a breaking change.
`GET /api/assistant/tools` returns the live catalog with each tool's metadata.

**Capability boundary:** there is no delete tool, no bulk operations, no admin, and no write that
touches more than the one invoice the caller names. Whatever is not in the catalog is physically
impossible. The first line of defense is which tools you expose, not what you forbid in the prompt.

## Cost, traces and the kill switch

`UsageCollector` is an `IChatClient` sitting underneath the function-invoking client, so it sees
every model call rather than every turn — a turn that uses tools makes several. Each call is
recorded with its model, tokens, latency and cost in euros, priced from `Usage:Prices` (euros per
million tokens, matched by longest model-id prefix). ADR 008 has the reasoning, including why an
unpriced model is recorded at zero with a warning instead of a guessed price.

The spend kill switch is a global daily cap, `Usage:DailyBudgetEur` (1€ by default), summed from
`usage_records` for the current UTC day. It is enforced twice: `/api/chat` refuses with `429
daily_budget_exhausted` before the stream starts, and the collector re-checks before every model
call so a turn that crosses the line mid-flight stops there with an `error` event. The per-user rate
limit protects against one impatient user; the euro cap is what protects against a scraper with many
IPs, which is the difference that makes a public demo safe to leave running.

| Endpoint | Answers |
|---|---|
| `GET /api/usage/summary?from=&to=` | Totals plus today's spend against the cap |
| `GET /api/usage/conversations` | One row per conversation: calls, tools, tokens, cost |
| `GET /api/usage/conversations/{id}` | The turn timeline: each model call interleaved with the gate's decisions |

Everyone sees their own conversations; an Admin sees everyone's. The budget figures stay global on
every view, because a per-user slice of one shared wallet would misstate how close the demo is to
pausing. A conversation belonging to someone else answers `404`, the same as one that never existed.

Traces: one trace per request, carrying `assistant.turn`, one `assistant.tool_call` per tool
(tagged with the gate's decision — `allowed`, `pending_approval`, `denied` or `blocked`) and one
`assistant.model_call` per call to the model (tagged with model, tokens, latency and cost).
`assistant.tool_call` is the parent of the policy evaluation and of the outgoing API call.

The decision is on the tool-call span rather than only on the policy span because a call stopped by
a per-turn limit returns before the policy engine runs — that span is the only place a trace can
explain it.

Worth knowing before you open a trace viewer: **`assistant.turn` is a sibling of the tool and model
spans, not their parent.** It is started inside an async iterator, so `Activity.Current` is restored
when the method yields and everything the enumeration drives afterwards attaches to the ASP.NET
request span instead. Everything shares the request's trace id — which is the id the chat footer
shows, so correlation works and nothing is lost — but a turn cannot be collapsed as one subtree.
Giving it real children means passing the turn's `ActivityContext` explicitly as the parent; it has
not been done because the flat shape has been sufficient to read. Metrics are
under the `InvoiceAssistant.Assistant` meter — model calls, tokens by direction, spend, budget
rejections and unpriced calls. Console exporter in development, OTLP whenever
`OTEL_EXPORTER_OTLP_ENDPOINT` is set. The chat footer shows the turn's trace id, which is the point:
the id in the UI is the id in the collector.

## Deployment

One container. The multi-stage `Dockerfile` builds the SPA with node, copies it into the API's
`wwwroot` and publishes the API around it; `policies.json` and `prompts/system.md` are copied beside
the binary, where `RepositoryFile.Find` already looks. `docker compose up` brings up PostgreSQL and
the app together, with a working default for every variable — the demo runs before anyone has a
provider key, and only `/api/chat` answers 503 until they do.

A public demo should sit behind basic auth at the proxy (Coolify or Traefik middleware), not in the
app: the app's own auth is the JWT demo being demonstrated, and putting a second login in front of
it inside the same codebase would confuse the thing on display. The daily cap is what makes leaving
it up affordable.

## Cross-cutting security

- AuthZ on every business endpoint: if someone calls the API without going through the assistant, the rules still apply.
- Rate limit on `/api/chat`, partitioned per user rather than per IP (20 turns/minute by default), plus a 2,000-character input cap.
- No CORS configuration anywhere: the Vite dev server proxies `/api`, and in production the Api serves the built SPA from `wwwroot`. The browser only ever talks to one origin.
- Model output is rendered as sanitized markdown, never as raw HTML — and neither is invoice line text, which is customer-supplied.
- Secrets only via environment variables; never user data in info-level logs.
- Bounded conversation history and per-turn token budget.
- Spend kill switch: global daily cap in € evaluated before every model call; once reached, `/api/chat` returns 429 and the rest of the app keeps working.
- The container runs unprivileged (`USER $APP_UID`) and writes nothing to disk; all state is in PostgreSQL.

## Keeping the assistant honest

The behaviours this document promises are checked by 35 declarative eval cases that run against a
real model in CI (`evals/`): tool selection, writes always ending as proposals, role and amount
limits, injection resistance, honest domain-error reporting and out-of-scope refusals. The asserts
are facts — recorded tool calls, gate decisions, database diffs — never response prose, and one
red case turns the pipeline red. Format, placeholders and mechanics in
[`docs/ai/evaluation.md`](ai/evaluation.md).

## Recorded decisions

See [`docs/adr/`](adr/). Changing an existing decision requires a new ADR that supersedes it, not a silent edit.
