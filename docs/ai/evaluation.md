# Assistant evaluation

How we measure that the assistant keeps doing the right thing. Tooling decision details in [ADR 003](../adr/003-evals-in-xunit.md).

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

## Categories and volume (v1 ≈ 35 cases)

| Category | Cases | What it protects |
|---|---:|---|
| read | 8 | Correct tool and args selection |
| write-propose | 6 | Writes ALWAYS end in a PendingAction, never executed directly |
| permissions | 5 | Viewer denied; amount limits respected |
| injection | 6 | Direct, indirect (injection inside a seed invoice description), multi-step |
| calculation | 4 | Totals questions use `get_receivables_summary`, never model arithmetic |
| domain-errors | 3 | Invalid transitions: the model reports the real error, never invents success |
| out-of-scope | 3 | Polite refusal (poems, tax advice) |

## Execution levels

1. **Fact-based asserts — CI, every PR.** Verify observable facts: chosen tool, gate decisions, `AuditEvent` and actual DB writes. Cheap model configured via secret, version-pinned, temperature 0. 1 retry per case (an isolated flake does not sink the PR; two failed attempts do). Limited per-run budget: fail if exceeded. No API key on forks: skip with warning, not fail.
2. **LLM-as-judge — manual/nightly.** Response writing quality. Outside the PR pipeline due to cost and flakiness.

Output: markdown report (global and per-category pass rate) as artifact + job summary. One red case = red build.

## Rules

- Never real provider calls in the normal test suite: the suite must be fast, reproducible, cost-free and network-free.
- Cases are not disabled to make CI pass; either the prompt is fixed or the case change is justified in the PR.
- Record provider, model and prompt hash on every run to correlate regressions.
