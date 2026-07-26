# ADR 004 · The LLM orchestrates but does not calculate

**Status:** accepted · 2026-07

## Context

LLMs are unreliable at arithmetic and errors in amounts are unacceptable in a financial domain, even a demo one.

## Decision

The model never calculates or decides over data: it decides which tool to call and writes responses. All calculations (totals, VAT, aging buckets) live in deterministic server-side code and are exposed as read tools (`get_receivables_summary`, `get_invoice`, …).

## Consequences

- Questions about totals must be resolved via tools; an evals category (`calculation`) verifies the model does no arithmetic of its own.
- Domain errors (invalid Invoice transitions) are produced by the API and the model must report them as-is, never invent success (`domain-errors` category).
- Model output is always treated as an untrusted proposal that the server validates.
