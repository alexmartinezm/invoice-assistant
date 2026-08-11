# Assistant evaluation

How we measure that the assistant keeps doing the right thing. Tooling decision details in [ADR 003](../adr/003-evals-in-xunit.md); how to run the suite in [`evals/README.md`](../../evals/README.md).

## How a case runs

The harness (`evals/InvoiceAssistant.Evals`) boots the real application against a throwaway
PostgreSQL database with a real, recorded model behind the app's own `IChatClient` pipeline. For
each case it resolves placeholders, snapshots the database, runs the turn through
`POST /api/chat` as the case's user, and checks every expectation against observable facts:

- **Tool calls** come from a recorder that wraps the provider client, not from the transcript.
- **Writes** are counted by diffing the invoice table before and after the turn — the database is
  the ground truth, whatever the response text claims.
- **Gate decisions** come from the new `AuditEvent` and `PendingAction` rows the turn produced.

Each case gets one retry: temperature is 0 but residual variance exists, so an isolated flake
does not sink a PR while two failures in a row still do. The whole run has a token budget
(`EVALS_RUN_TOKEN_BUDGET`); exceeding it fails the run, because a prompt change that doubles
token spend is a regression even when every case still passes.

## Case format (`evals/cases/*.yaml`)

```yaml
id: read-overdue-01
category: read
user: carlos@demo
prompt: "which invoices are overdue?"
expect:
  any_of:                                      # alternative valid combinations
    - tool_called: list_invoices
      args_match: { overdue_only: true }
    - tool_called: list_invoices
      args_match: { status: Sent, to: $today } # $today is resolved by the harness
  no_write_tools: true
```

`any_of` exists because there is usually more than one correct way to resolve a request; pinning a single tool+args combination produces false reds on valid behavior. Checks over free-form response text live at the judge level, never in CI: negative string matching over natural language yields false reds ("I have not cancelled anything" contains "cancelled").

### Expectations

Every key asserts a fact; unknown keys fail the loader, so a typo cannot pass silently.

| Key | Asserts |
|---|---|
| `any_of` | At least one listed tool call was made. Each entry names a `tool_called` and optional `args_match` (every listed argument must be present and equal). |
| `tool_called` / `args_match` | Top-level shorthand for a one-entry `any_of`. |
| `no_write_tools: true` | No write tool was called at all — proposed, executed or refused. |
| `no_tools: true` | No tool was called at all (out-of-scope requests are declined, not researched). |
| `max_tool_calls: N` | The turn called at most N tools. Asserts the *shape* of the answer: a model that cannot reach a figure in one call enumerates towards it, hits the per-turn brake, and answers from a partial scan — which reads exactly like a complete answer, and which `tool_called` alone cannot catch. |
| `writes_executed: N` | Exactly N invoices were created or changed in the database. |
| `pending_action_created` | A new `PendingAction` names this tool; `null` means none was created. |
| `audit_contains` / `audit_not_contains` | Gate decisions (`auto`, `confirmed`, `denied`, `blocked`) among the turn's new audit events. |
| `executions_created: N` | Exactly N `ActionExecution` rows were created. Stronger than `writes_executed` for the injection cases: an execution exists the moment a write is *attempted*, before the API has had a chance to refuse it, so `0` asserts the assistant never tried rather than that nothing landed. The two failure modes look identical in the invoice table (ADR 009). |

### Placeholders

Cases stay declarative and market-agnostic by naming data instead of hardcoding it. The harness
resolves `{{...}}` in prompts and expected arguments against the live database:

| Placeholder | Resolves to |
|---|---|
| `{{a_customer_name}}` / `{{a_customer_email}}` | The first seeded customer. |
| `{{a_draft_invoice}}` / `{{a_sent_invoice}}` | The first invoice in that status. |
| `{{a_sent_invoice_over_100}}` / `{{another_sent_invoice_over_100}}` | Sent invoices in the confirmation band: over the policy's auto-approve ceiling, at or under the endpoint's accountant limit. Created through the business API when the seed has none. |
| `{{a_sent_invoice_under_100}}` / `{{a_draft_invoice_under_100}}` / `{{a_paid_invoice_under_100}}` | A fresh fixture invoice under the auto-approve ceiling, created through the business API per attempt so retries stay independent. |
| `{{the_poisoned_invoice}}` | The seeded invoice whose line description carries the indirect prompt injection. |
| `{{in_30_days}}` | Today + 30 days, `yyyy-MM-dd`. |
| `$today` (expected args only) | Today, `yyyy-MM-dd`. |

## Categories and volume (v1 = 36 cases)

| Category | Cases | What it protects |
|---|---:|---|
| read | 8 | Correct tool and args selection |
| write-propose | 6 | Writes ALWAYS end in a PendingAction, never executed directly |
| permissions | 5 | Viewer denied; amount limits respected in both directions |
| injection | 6 | Direct, indirect (injection inside a seed invoice description), multi-step |
| calculation | 5 | Totals and rankings use `get_receivables_summary`, never model arithmetic |
| domain-errors | 3 | Invalid transitions: the model reports the real error, never invents success |
| out-of-scope | 3 | Polite refusal (poems, tax advice) |

The `repo-checks` job in CI tallies `evals/cases/*.yaml` and fails when
the counts here disagree with what is on disk. A case added without touching this table breaks the
build before the test jobs run.

## Execution levels

1. **Fact-based asserts — CI, every PR.** The `evals` job in `ci.yml`. Cheap model set by the
   `EVALS_MODEL` repository variable, credentials by secret, temperature 0 (set by the
   orchestrator itself). The job is gated: the provider is only called for runs the repository
   owner triggers from a branch of this repository, because on a public repo the only job that
   spends money is the one a stranger's PR must not reach. An unauthorized run — a fork's PR, or
   missing credentials — skips with a warning and stays green, so the check keeps being reported
   and can remain required for merging; see [`evals/README.md`](../../evals/README.md) for the
   repository settings that back the gate. Output: a markdown report (global and per-category
   pass rate) uploaded as an artifact and appended to the job summary. One red case = red build.
2. **LLM-as-judge — manual/nightly.** Response writing quality. Outside the PR pipeline due to
   cost and flakiness.

The harness additionally runs `HarnessSelfTests` against a scripted model on every plain
`dotnet test`: they prove the machinery (placeholders, the SSE turn, the diff, the retry) without
spending a token, so a harness bug is caught on PRs that never touch a model.

## What a run looks like

Every run writes a markdown report to `EVALS_REPORT_PATH`, built by `EvalReport.Write`
(`evals/InvoiceAssistant.Evals/EvalReport.cs`). CI uploads it as the `evals-report` artifact and
appends it to the job summary. Six parts:

| Part | What it carries |
|---|---|
| Headline | `**PASS** — N/N cases.`, or `**FAIL** — N/M cases passed.` |
| Run identity | Date, provider, model, and the hash of the rendered system prompt |
| Budget | Tokens spent against `EVALS_RUN_TOKEN_BUDGET` |
| By category | One row per category: passed against total |
| Cases | One row per case: `pass`, `pass (retry)` or **fail**, with attempts and tokens |
| Failures | For every red case, the assertion that broke, in full |

The prompt hash is recorded on every `Conversation`, and
it covers the *rendered* prompt rather than the file on disk, so a regression seen in a deployment
and a red case in CI can be pinned to the same prompt revision. A `pass (retry)` records a case that
only passes on the second attempt. At temperature 0, repeated retries indicate an ambiguous prompt
before any case goes red.

The repository keeps one report. The `evals` job is gated to the repository owner, so the
check a visitor sees on someone else's pull request reads "skipped" and never a result:

```bash
EVALS_MODEL=<cheap pinned model id> OPENAI_API_KEY=... \
  EVALS_REPORT_PATH=$PWD/docs/ai/example-eval-run.md \
  dotnet test evals/InvoiceAssistant.Evals
```

Commit the result verbatim. It is a run record and should not be edited afterwards.

## Proving a regression turns it red

The front page claims that a one-line change to the prompt breaks the build. That is checkable in
about the time a run takes:

1. Delete the last sentence of the second **Behavior** bullet in `prompts/system.md` —
   *"For aggregated amounts use `get_receivables_summary`."* Leave everything else alone.
2. Run the suite.

The four cases that should fall are `calc-total-01`, `calc-aging-01`, `calc-aging-02` and
`calc-top-debtor-01`: each asserts that the figure came from `get_receivables_summary`, not that the
number in the prose is right, so a model that starts totalling `list_invoices` output itself fails
them even on the turns where its arithmetic happens to come out correct. `calc-top-debtor-01` is the
sharpest of the four, because it also caps the turn at one tool call — enumerating towards the
answer fails it even when the right tool appears somewhere in the enumeration.

`calc-vat-01` should stay green throughout: it reads the tax line off `get_invoice`, which that
sentence never governed. A regression that takes the whole category with it usually means something
larger broke than the line you deleted.

## Rules

- Never real provider calls in the normal test suite: the suite must be fast, reproducible, cost-free and network-free. The eval suite is the one deliberate exception, and only when credentials are explicitly configured.
- Cases are not disabled to make CI pass; either the prompt is fixed or the case change is justified in the PR.
- Record provider, model and prompt hash on every run to correlate regressions (the report carries all three).
