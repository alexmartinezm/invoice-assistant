# ADR 003 · Evals as xUnit tests, no external tooling

**Status:** accepted · 2026-07

## Context

Prompt regressions are silent: a one-line change can break tool selection or open an injection vector without any unit test noticing. Frameworks like promptfoo cover this, but bring Node tooling into a .NET repo.

## Decision

Own xUnit harness (`evals/InvoiceAssistant.Evals`) with declarative YAML cases (`evals/cases/*.yaml`). Two levels:

1. **Fact-based asserts (CI, every PR):** chosen tool (with `any_of` for alternative valid combinations), gate decisions, `AuditEvent` and actual DB writes. Never the free-form response text. Cheap pinned model, temperature 0, 1 retry per case, per-run budget. No API key (forks): skip with warning.
2. **LLM-as-judge (manual/nightly):** response writing quality. Outside the PR pipeline due to cost and flakiness.

## Consequences

- The repo stays 100% .NET and "evals as regular tests" is part of the message.
- Trade-off: fewer features than a dedicated framework; enough for 30-50 cases.
- One red case = red build: changing `prompts/system.md` without passing evals breaks CI by design.
