# Production-grade AI assistant in .NET: tool calling, write safety, evals & cost tracking

[![CI](https://github.com/alexmartinezm/invoice-assistant/actions/workflows/ci.yml/badge.svg)](https://github.com/alexmartinezm/invoice-assistant/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**invoice-assistant** is a production-grade AI assistant embedded in a demo invoicing app. It is a reference repo: it demonstrates in code the pieces that separate a demo from a production system.

> **Guiding principle: the LLM orchestrates but does not calculate.** The model decides which tool to call and writes responses; all calculations (totals, VAT, aging) and all security decisions live in deterministic server-side code.

## The 4 pieces

1. **Tool calling with propagated identity** — the assistant can never do more than the logged-in user: its tools call our own REST API over HTTP with the user's bearer token.
2. **Server-side write gate** — writes are proposed, not executed: human confirmation via `PendingAction`, structured policy in `policies.json`, prompt injection stopped by per-turn limits.
3. **Evals in CI** — prompt regressions break the pipeline. Own xUnit harness, declarative YAML cases, and [a one-line regression you can run yourself](docs/ai/evaluation.md#proving-a-regression-turns-it-red).
4. **Cost and traces per conversation** — tokens, euros, latency and OpenTelemetry, with a daily spend kill switch.

## Anatomy of a turn

```mermaid
flowchart TD
    UI[Chat UI] -->|POST /api/chat SSE| ORCH[ChatOrchestrator]
    ORCH --> LOOP[Tool-calling loop]
    LOOP --> USAGE[UsageCollector · daily cap, then tokens, €, latency]
    USAGE --> MODEL[Model provider]
    LOOP -->|tool call| GATE[ToolGate + ToolPolicyEngine]
    GATE -->|allow| EXEC[HTTP to our own API with the user's JWT]
    GATE -->|require_confirmation| PA[PendingAction → human approval]
    GATE -->|deny or limit reached| BLOCK[AuditEvent → the model explains it]
    EXEC --> API[REST API · role-based authZ · deterministic domain]
```

The golden rule: **the system prompt can ask for good behavior; only the server can guarantee it.** The prompt is UX, the policy is security. Key decisions are documented as ADRs in [`docs/adr/`](docs/adr/).

## What it looks like

The invoicing app, with the assistant docked to the right on every page. Around 40 seeded invoices,
three demo users, `gpt-4o-mini` behind `/api/chat`: the model picked its own tools, and every figure
in its answers came back from the API.

![The invoices page with the receivables strip, filters and the ledger table, next to the assistant drawer](docs/screenshots/invoice-ledger.png)

Asking an Accountant's assistant to settle a €1,004.30 invoice does not settle it. The gate turns
the tool call into a proposal, described by the server rather than by the model that proposed it,
and nothing reaches the ledger until a human confirms it.

![An approval card asking to confirm marking invoice 2026-0025 as paid, with Approve and Reject](docs/screenshots/assistant-approval-required.png)

Every model call is metered where it happens and priced from a table in configuration, with the
day's spend checked against the kill switch before each one.

![The usage page showing today's spend, the daily budget bar and one row per conversation](docs/screenshots/usage-per-conversation.png)

The whole tour is in [**docs/screenshots.md**](docs/screenshots.md): the invoice detail and the
aging buckets, the tool catalog a Viewer can open from the drawer, an approved settlement still
refused by the API's own ceiling, a Viewer's write denied by policy, and the per-turn limit
stopping a bulk cancellation after its first call.

## Repo structure

```text
invoice-assistant/
├── AGENTS.md                     # Canonical contract for development agents
├── src/
│   ├── Api/                      # .NET 10: domain, endpoints, assistant, gate, usage
│   └── Web/                      # React 19 + Vite + TypeScript + Tailwind
├── tests/Api.Tests/              # Domain unit tests + integration tests on real PostgreSQL
├── evals/
│   ├── InvoiceAssistant.Evals/   # xUnit harness
│   └── cases/                    # Declarative *.yaml cases
├── prompts/system.md             # Versioned system prompt (hash per conversation)
├── policies.json                 # Write gate: structured rules, no DSL
├── docs/                         # Architecture + ADRs + deployment + screenshots
├── Dockerfile                    # SPA build → API publish → one runtime image
├── docker-compose.yml            # The whole demo, or just PostgreSQL for development
├── docker-compose.coolify.yml    # The same stack with the panel owning domain, TLS and proxy
└── .github/workflows/ci.yml      # repo checks → backend → frontend → evals
```

## Stack

| Area | Decision |
|---|---|
| Backend | .NET 10, Minimal APIs, vertical slices |
| AI layer | `Microsoft.Extensions.AI` (`IChatClient`), no provider lock-in |
| Provider | Azure OpenAI by default; fallback to any OpenAI-compatible endpoint via config |
| Frontend | React 19 + Vite + TypeScript + Tailwind 4 |
| Persistence | PostgreSQL + EF Core, migrations and seed on startup |
| Auth | Demo JWT with 3 roles: Admin, Accountant, Viewer |
| Observability | OpenTelemetry (traces + metrics) |
| CI | GitHub Actions: build + tests + evals |
| License | MIT |

## Quick start

```bash
cp .env.example .env    # optional: add an AI provider key
docker compose up       # http://localhost:8080
```

One container serves both the API and the SPA. Pick one of the three demo users; the shared password
is `demo1234`. Migrations and around 40 seeded invoices are applied on startup, so there is no
database step.

Developing rather than demoing — two terminals, with hot reload:

```bash
docker compose up -d postgres    # just the database (ADR 006)
dotnet run --project src/Api     # http://localhost:5080
npm install --prefix src/Web && npm run dev --prefix src/Web   # http://localhost:5173
```

**Without an AI key** the ledger, filters and API all work; only `/api/chat` answers 503 telling you
which variables it wants. That is deliberate — you can read and run the repo before signing up to a
provider.

All commands, including the quality gates, live in [`.agent/commands.md`](.agent/commands.md).

## Deploying it

The same image runs anywhere a container does; it takes its whole configuration from environment
variables. On **Coolify**, point a Docker Compose resource at
[`docker-compose.coolify.yml`](docker-compose.coolify.yml), set `JWT_SIGNING_KEY` and
`POSTGRES_PASSWORD`, and give the `app` service a domain written with the container port
(`https://invoices.example.com:8080`). On a VPS you manage, `docker compose up -d` is the whole
deploy.

Behind any TLS-terminating proxy there is one setting worth knowing about — the assistant calls our
own API over HTTP, so it needs to know where "itself" is — and one worth deciding before you hand
the URL out, the daily spend cap. Both, plus a managed-database variant and a troubleshooting
table, are in [`docs/deployment.md`](docs/deployment.md).

## What a conversation costs

Every call to the model is metered where it happens — inside the tool-calling loop, not once per
turn, because a turn that uses tools makes several calls. Each one is recorded with its tokens,
latency and cost in euros, priced from a table in configuration; the **Usage** page shows the total
per conversation and a timeline that interleaves each model call with the write gate's decisions.

The same table backs the **spend kill switch**: a global daily cap (`USAGE_DAILY_BUDGET_EUR`, 1€ by
default) checked before the turn starts *and* before every model call inside it. Once it is spent,
`/api/chat` answers `429` and the rest of the app carries on working. A per-user rate limit does
nothing about a scraper with many IPs — the euro cap is what makes a public demo safe to leave
running with a real key in it. The reasoning, including what happens to a model with no configured
price, is in [ADR 008](docs/adr/008-cost-accounting-and-the-spend-kill-switch.md).

Traces are the other half of that piece. The chat footer prints each turn's trace id, and
[`docs/deployment.md`](docs/deployment.md#seeing-the-traces) has the two ways to make that id lead
somewhere — a console exporter, or any OTLP collector — plus what the spans and the meter carry.

## Roadmap

- **Skeleton + reads** ✅ — domain with enforced transitions, seed, JWT auth, business
  endpoints, SSE chat with read tools and propagated identity, invoices + chat UI.
- **Write gate** ✅ — ToolPolicyEngine over `policies.json`, five gated write tools,
  PendingAction with approve/reject, AuditEvent, idempotency and per-turn limits.
- **Evals + CI** ✅ — xUnit harness against a real model, 36 fact-based cases, CI job with
  per-run token budget and markdown report; a one-line prompt regression turns the pipeline red.
- **Cost, traces and polish** ✅ — UsageCollector metering every model call, global daily spend
  kill switch, Usage page with per-conversation cost timelines, OpenTelemetry traces and metrics,
  single-container Docker build.
- **Durable agent actions** ✅ — decision, execution and delivery as three linked records;
  approve/reject settled by a database compare-and-set; the business change and its replay receipt
  in one transaction; approvals bound to the invoice revision they were proposed against; an outbox
  and a reconciler for the one effect that cannot be rolled back. [ADR 009](docs/adr/009-durable-agent-actions.md).

## The five-minute failure demo

The gate is easy to show working. What durable actions add is what happens when something breaks
halfway, and
that is worth five minutes of anyone's time. Everything below runs against `docker compose up` with
no provider key except step 1, which needs the assistant.

1. **Ask for a send that policy gates.** "Send invoice 2026-0007." The card shows the server's
   summary and the invoice revision it was proposed against.
2. **Approve it, with the demo provider set to accept and lose the answer.** The card does not say
   *Done*. It says **Approved · executing**, then **Outcome unknown · reconciling**, with the
   execution id underneath.
3. **Look at the evidence.** The invoice reads `Sent`; its delivery reads `unknown`; the provider has
   exactly one receipt. All three are true at once, which is precisely why they are three records.
4. **Wait for the reconciler.** The same execution becomes `succeeded` and the delivery
   `delivered` — from the provider's existing receipt. No second email.
5. **Approve again.** The stored outcome is replayed: no second invoice transition, no second
   delivery, the same execution id.
6. **Optionally, change a different draft after proposing something against it**, then approve. The
   write is refused with `resource_changed` and the card says the invoice moved on — because an
   approval is a decision about a situation, not about a sentence.

What the demo is designed to make obvious is that "approved" and "done" are different words, and
that a system which cannot tell you which one it means is a system guessing about your money.

Roles are visible in the chat. A Viewer's write is refused outright by policy; an
Accountant settles small invoices without being asked, is asked to confirm larger ones, and is
refused by the API above a configurable ceiling (€1,000 by default) even after a human approves —
the three bands are tabulated in [`docs/architecture.md`](docs/architecture.md).

**Out of scope for v1:** RAG, cross-session memory, multi-agent, real multi-tenancy, real legal invoicing (Verifactu, etc.), i18n. The invoicing app is a credible pretext, not a product.

## License

[MIT](LICENSE)
