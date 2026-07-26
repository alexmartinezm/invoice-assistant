# ADR 005 · Structured policies, no DSL

**Status:** accepted · 2026-07

## Context

Write gate rules need to express conditions like "an Accountant may mark invoices as paid up to a certain amount". A DSL such as `"amount <= 100 && role >= Accountant"` forces writing and securing a parser with precedence.

## Decision

Rules in `policies.json` as **structured matching**: each rule is a conjunction of optional typed fields (`tool`, `sideEffect`, `role`/`minRole`, `maxAmount`; role order: Viewer < Accountant < Admin), validatable with JSON schema.

- Rules are evaluated in order and the first match wins; if none matches, the `defaults` per `sideEffect` apply.
- Matching runs against the tool's `sideEffect` metadata, not against name globs.
- Evaluation is not pure over the args: for `maxAmount`, the engine resolves the real amount (fetches the invoice by `number`) before deciding. That context resolution is part of the contract and is recorded as its own span in the trace.

## Consequences

- Same expressive power as the DSL for this domain, with half the code and no parser to secure.
- Rules live outside the prompt and outside the code: reviewable in PRs and testable deterministically.
