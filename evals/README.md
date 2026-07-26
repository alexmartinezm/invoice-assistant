# Evals

- `InvoiceAssistant.Evals/` — the xUnit harness. It boots the real application against a
  throwaway PostgreSQL database, lets a real model take the turn over `POST /api/chat`, and then
  asserts on facts: recorded tool calls, gate decisions, `AuditEvent` rows and actual database
  writes. Never on the free-form response text.
- `cases/` — declarative YAML cases, one file per case. Format, categories, placeholders and
  execution levels are documented in [`docs/ai/evaluation.md`](../docs/ai/evaluation.md).

## Running

```bash
export EVALS_MODEL=<cheap pinned model id>
export OPENAI_API_KEY=...        # or AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY
dotnet test evals/InvoiceAssistant.Evals
```

PostgreSQL must be reachable (same `TEST_POSTGRES_CONNECTION_STRING` convention as the
integration tests). Without `EVALS_MODEL` and provider credentials every case is skipped with a
warning — a plain `dotnet test` never spends money by accident, and a fork's PR never fails for
lacking secrets.

Each run writes a markdown report (pass rate globally and per category) to `EVALS_REPORT_PATH`,
and stops with a failure if the run exceeds its token budget (`EVALS_RUN_TOKEN_BUDGET`, default
250,000).

The harness also tests itself: `HarnessSelfTests` runs the whole machinery against a scripted
model on every plain `dotnet test`, credentials or not, so the first real run in CI is never the
first run ever.
