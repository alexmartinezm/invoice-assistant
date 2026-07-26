# invoice-assistant · Agent contract

Canonical source of rules for development agents. Client-specific files (`CLAUDE.md`, `.github/copilot-instructions.md`) are thin adapters: they only add differences, they never duplicate this contract.

## Product

Production-grade AI assistant embedded in a demo invoicing app. Public repo: portfolio, teaching material and technical baseline for client proposals.

**Guiding principle: the LLM orchestrates but does not calculate.** The model decides which tool to call and writes responses; all calculations (totals, VAT, aging) and all security decisions live in deterministic server-side code.

Invariants that must never be broken:

- No write reaches the DB without an explicit policy `allow` or a human approval recorded in `AuditEvent`.
- The assistant can never do more than the logged-in user: tools call our own REST API over HTTP with the user's bearer token.
- Security rules live in `policies.json` and on the server, never in the prompt. The prompt is UX, the policy is security.
- Invoice state transitions are enforced in the domain: `Draft → Sent → Paid`; `Draft|Sent → Cancelled`; `Overdue` is derived from dates, never persisted.
- Model output is treated as untrusted input: structured validation on the server, sanitized rendering on the frontend.
- There are no delete, bulk or admin tools. The first line of defense is which tools you expose, not what you forbid in the prompt.

## Stack

- Backend: .NET 10, Minimal APIs, a single `Api` project with vertical slices.
- AI layer: `Microsoft.Extensions.AI` (`IChatClient`). Azure OpenAI as default provider, fallback to any OpenAI-compatible endpoint via config. Provider and model always in configuration, never hardcoded.
- Frontend: React 19 + Vite + TypeScript + Tailwind 4. SPA served by the Api in prod from `wwwroot`.
- Persistence: PostgreSQL + EF Core, migrations and seed applied on startup (ADR 006).
- Auth: own JWT with demo login and 3 seed users (Admin / Accountant / Viewer).
- Streaming: SSE on `POST /api/chat`.
- Observability: OpenTelemetry (traces + metrics).
- Evals: own xUnit harness (`evals/InvoiceAssistant.Evals`), YAML cases in `evals/cases/`.
- CI: GitHub Actions — build, tests and evals job.

## Architecture

- `src/Api/Domain/` — entities and their invariants. Invoice transitions and every monetary calculation live here, with no public setters for status or totals.
- `src/Api/Features/` — business vertical slices: Invoices, Customers, Reports, Auth.
- `src/Api/Assistant/` — ChatOrchestrator, Tools/, ChatEndpoints (ToolPolicyEngine in F2, UsageCollector in F4).
- `src/Api/Infrastructure/` — EF Core, migrations, seed, telemetry, configuration.
- `src/Web/` — React SPA.
- `tests/Api.Tests/` — domain unit tests plus integration tests booting the real app against a throwaway PostgreSQL database.
- `prompts/system.md` — versioned system prompt; its hash is recorded per conversation.
- `policies.json` — write gate rules, structured, no DSL.
- `evals/` — harness and cases.
- `docs/` — architecture and ADRs. Read the ADRs before changing an already-made decision.

Dependency direction:

```text
HTTP / Events / Chat
        ↓
Application services (slices)
        ↓
Deterministic domain logic
        ↑
AI adapter proposes structured data
```

AI is an adapter, not the center of the domain. Structural changes (new project, persistence change, new AI provider) require a new ADR.

## Commands

Verifiable commands live in `.agent/commands.md`. Keep that file updated whenever projects or scripts are added; do not duplicate commands here.

## Workflow

- Inspect sibling code before editing: follow the patterns of the closest slice.
- Write or update tests with every behavior change.
- Run the minimum relevant test set during development; full suite before delivering.
- Quality gates (format, build, tests, evals) green before considering work done.
- Watch CI checks after opening a PR; delivery is not finished until CI passes.
- PR titles follow Conventional Commits.
- Definition of done and delivery process in `.agent/delivery.md`.

## AI-specific rules

- Always use structured output with schemas for tool calls; never parse free-form model text.
- Treat all model output as untrusted input: validate IDs and args against real data on the server.
- All financial calculations happen in the API (`get_receivables_summary`, totals, VAT); never model arithmetic.
- Writes are proposed, not executed: `PendingAction` + human confirmation, unless an explicit `allow` policy rule applies.
- Keep provider/model/prices in configuration (`appsettings`, `.env`), never in code.
- Changes to `prompts/system.md` must pass the evals suite; a prompt regression must break CI.
- No real provider calls in the normal test suite: fake `IChatClient`. Real-model evals are a separate job with a limited budget.
- Respect cost limits: daily spend kill switch and per-run evals budget.

## Definition of done

- Functional requirements covered and tested.
- Tests and evals green.
- Formatting and static analysis green.
- Security invariants verified (no write without policy or approval).
- Documentation and ADRs updated if a decision changes.
