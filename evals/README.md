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

PostgreSQL must be reachable, using the same `TEST_POSTGRES_CONNECTION_STRING` convention as the
integration tests). Without `EVALS_MODEL` and provider credentials every case is skipped with a
warning — a plain `dotnet test` never spends money by accident, and a fork's PR never fails for
lacking secrets.

Each run writes a markdown report (pass rate globally and per category) to `EVALS_REPORT_PATH`,
and stops with a failure if the run exceeds its token budget (`EVALS_RUN_TOKEN_BUDGET`, default
250,000).

`HarnessSelfTests` also runs the whole machinery against a scripted model on every plain
`dotnet test`, with or without credentials. CI therefore exercises the evaluator even when it does
not call a real model.

## Who can run the suite in CI

This is a public repository and the evals job is the only job that costs money, so it is gated:
the provider is called only when the run was triggered by the repository owner from a branch of
this repository. Any other pull request still runs the job — its check has to be reported for
`evals` to work as a required check — but the gate closes and the run ends green with a warning
in the job summary explaining why.

The gate is a convenience, not the security boundary. GitHub never hands secrets to a workflow
run from a fork, which is what actually keeps eval credentials out of a stranger's pull request;
the explicit check adds an owner-only rule on top, for accounts that are later given write
access. Two settings back it up, and neither lives in this repository:

- **Settings → Actions → General → Fork pull request workflows**: require approval for all
  external contributors, so a fork's PR does not even burn runner minutes unreviewed.
- **Settings → Environments**: an `evals` environment holding the provider secrets, with the
  owner as a required reviewer. Environment protection is repository configuration rather than
  workflow code, so it survives a pull request that edits `ci.yml` — the one control the gate
  in the workflow cannot give you. The cost is that the check waits on a manual approval.

Because evals are skipped on outside contributions, a green `evals` check on such a pull request
means "not run", not "passed". Before merging one that touches `prompts/`, `policies.json`,
`evals/` or the assistant slice, push the branch into this repository and let the suite run.
