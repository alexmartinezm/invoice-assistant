# Production-grade AI assistant in .NET: tool calling, write safety, evals & cost tracking

**invoice-assistant** is a production-grade AI assistant embedded in a demo invoicing app. It is a reference repo: it demonstrates in code the pieces that separate a demo from a production system.

> **Guiding principle: the LLM orchestrates but does not calculate.** The model decides which tool to call and writes responses; all calculations (totals, VAT, aging) and all security decisions live in deterministic server-side code.

## The 4 pieces

1. **Tool calling with propagated identity** — the assistant can never do more than the logged-in user: its tools call our own REST API over HTTP with the user's bearer token.
2. **Server-side write gate** — writes are proposed, not executed: human confirmation via `PendingAction`, structured policy in `policies.json`, prompt injection stopped by per-turn limits.
3. **Evals in CI** — prompt regressions break the pipeline. Own xUnit harness, declarative YAML cases.
4. **Cost and traces per conversation** — tokens, euros, latency and OpenTelemetry, with a daily spend kill switch.

## Anatomy of a turn

```mermaid
flowchart TD
    UI[Chat UI] -->|POST /api/chat SSE| ORCH[ChatOrchestrator]
    ORCH --> LLM[IChatClient + tools]
    LLM -->|tool call| GATE[ToolPolicyEngine]
    GATE -->|allow| EXEC[ToolExecutor → HTTP to our own API with the user's JWT]
    GATE -->|require_confirmation| PA[PendingAction → human approval]
    GATE -->|deny| BLOCK[AuditEvent blocked → the model explains it]
    EXEC --> API[REST API · role-based authZ · deterministic domain]
    ORCH --> USAGE[UsageCollector · tokens, €, latency]
```

The golden rule: **the system prompt can ask for good behavior; only the server can guarantee it.** The prompt is UX, the policy is security. Key decisions are documented as ADRs in [`docs/adr/`](docs/adr/).

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
├── docs/                         # Architecture + ADRs
├── docker-compose.yml            # PostgreSQL for development
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
cp .env.example .env             # optional: add an AI provider key
docker compose up -d postgres    # PostgreSQL (ADR 006)

dotnet run --project src/Api     # http://localhost:5080
npm install --prefix src/Web && npm run dev --prefix src/Web
```

Open http://localhost:5173 and pick one of the three demo users; the shared password is `demo1234`.
Migrations and around 40 seeded invoices are applied on startup, so there is no database step.

**Without an AI key** the ledger, filters and API all work; only `/api/chat` answers 503 telling you
which variables it wants. That is deliberate — you can read and run the repo before signing up to a
provider.

All commands, including the quality gates, live in [`.agent/commands.md`](.agent/commands.md).

## Roadmap

- **F1 · Skeleton + reads** ✅ — domain with enforced transitions, seed, JWT auth, business
  endpoints, SSE chat with read tools and propagated identity, invoices + chat UI.
- **F2 · Write gate** — ToolPolicyEngine + `policies.json`, PendingAction, approve/reject,
  AuditEvent, idempotency and per-turn limits.
- **F3 · Evals + CI** — xUnit harness, ~35 cases, evals job with per-run budget.
- **F4 · Cost, traces and polish** — UsageCollector, spend kill switch, Usage page, OpenTelemetry,
  Docker and deploy.

Until F2 lands the assistant is read-only: the tool catalog contains four read tools and nothing
else, which is also why the Viewer and the Admin see the same figures today. The roles already
differ at the API — a Viewer is refused every write, an Accountant is capped at a configurable
ceiling (€1,000 by default) — and that difference becomes visible in the chat as soon as write
tools exist.

**Out of scope for v1:** RAG, cross-session memory, multi-agent, real multi-tenancy, real legal invoicing (Verifactu, etc.), i18n. The invoicing app is a credible pretext, not a product.

## License

[MIT](LICENSE)
