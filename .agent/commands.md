# Verifiable commands

Single source of commands for humans and agents. Update this file in the same commit that adds or changes projects, scripts or tooling.

## Setup

```bash
cp .env.example .env           # add the provider API key (optional: the app runs without one)
docker compose up -d postgres  # PostgreSQL is required (ADR 006)
dotnet restore
npm install --prefix src/Web
```

The whole demo in one container, without a local toolchain:

```bash
docker compose up              # builds the SPA into wwwroot, then serves it on :8080
docker compose up --build      # after changing code: Compose reuses the image otherwise
```

Migrations and seed data are applied on startup, so there is no separate database step. The three demo users share the password `demo1234`.

## Development

Two terminals:

```bash
dotnet run --project src/Api           # http://localhost:5080
npm run dev --prefix src/Web           # http://localhost:5173
```

The dev server proxies `/api` to `http://localhost:5080`; override with `API_URL` if the Api runs elsewhere. Without an AI provider configured everything works except `/api/chat`, which answers 503 naming the variables it needs.

## Tests

```bash
dotnet test                            # domain + integration, against a real PostgreSQL
```

The integration tests create and drop a database per run, connecting with `TEST_POSTGRES_CONNECTION_STRING` (default `Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres`). No test calls a real model: `IChatClient` is scripted.

The write-gate tests load the repository's real `policies.json` rather than a fixture, so editing a rule or a limit is expected to move them.

```bash
dotnet test --filter FullyQualifiedName~ToolPolicyEngineTests   # the matching table, no host
dotnet test --filter FullyQualifiedName~WriteGateTests          # the gate end to end
dotnet test --filter FullyQualifiedName~UsageAccountingTests    # metering and the usage endpoints
dotnet test --filter FullyQualifiedName~DailyBudgetTests        # the spend kill switch
```

The scripted model reports fixed usage (120 prompt / 45 completion tokens) and the test host prices
it at 10€/20€ per million, so one scripted model call costs exactly 0.0021€. Changing either number
moves the cost assertions.

Evals against a real model (they spend tokens; the normal suite never does):

```bash
EVALS_MODEL=<cheap pinned model id> OPENAI_API_KEY=... dotnet test evals/InvoiceAssistant.Evals
```

Azure works too (`AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY`, with `EVALS_MODEL` as the
deployment name). Without credentials every case skips with a warning, so a plain `dotnet test`
never spends money by accident; the harness's own `HarnessSelfTests` still run, scripted. Report
path and token budget: `EVALS_REPORT_PATH`, `EVALS_RUN_TOKEN_BUDGET` (default 250,000). Details
in [`docs/ai/evaluation.md`](../docs/ai/evaluation.md).

## Quality gates

```bash
dotnet format --verify-no-changes      # backend formatting
dotnet build                           # backend build
npm run lint --prefix src/Web          # ESLint
npm run format:check --prefix src/Web  # Prettier
npm run build --prefix src/Web         # typecheck + Vite build into src/Api/wwwroot
jq empty policies.json                 # policy file is valid JSON
docker compose config --quiet          # compose file parses and every variable resolves
```

## Migrations

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name> --project src/Api --output-dir Infrastructure/Migrations
```
