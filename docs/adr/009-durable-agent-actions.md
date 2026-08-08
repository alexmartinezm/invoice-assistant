# ADR 009 · Decision, execution and delivery are three facts

**Status:** accepted · 2026-08 · supersedes the execution wording of [ADR 001](001-server-side-write-gate.md) and [ADR 007](007-confirmation-does-not-end-the-turn.md) · preserves [ADR 002](002-tools-via-http.md)

## Context

The write gate is a good authorization boundary. It freezes the model's arguments, re-evaluates
policy under the approver's identity, calls the business API with that identity and records the
decision. What it has never had is an answer for the window *after* a decision and *before* the
effect is known.

Four concrete holes, all reachable in the F2/F4 code:

1. `PendingAction.TryApprove` mutated a tracked entity. Two requests could both read `Pending`
   before either `SaveChanges` became visible, so "single use" was timing rather than a constraint.
2. The idempotency filter recorded its receipt in a *second* `SaveChanges`, after the handler had
   already committed the business change. A crash in between left an effect with no replay receipt —
   and the next retry would perform it again.
3. `PendingActionStatus.Approved` was being read as "the work happened". There was no record of the
   attempt, so a crash, a timeout or a refusal could not be told apart, and none of them could be
   resumed.
4. Frozen arguments were replayed exactly, which is right, but the *state they were proposed
   against* was not frozen. An approval could land on an invoice somebody else had changed during
   the five-minute window.

A fifth was a vocabulary problem rather than a bug: an approved write the API then refused was
audited `Denied`, which erases the human from the record of a decision they demonstrably made.

## Decision

**Three facts, recorded separately, linked by id, never collapsed into one status.**

| Fact | Question it answers | Where it lives |
|---|---|---|
| Decision | Who authorized this exact command? | `AuditEvent` + `PendingAction` |
| Execution | What was attempted, and how did it end? | `ActionExecution` |
| External effect | Did the provider take it? | `InvoiceDelivery`, driven by `OutboxMessage` |

Five rules follow, and are decided here rather than left implicit:

1. **Authorization is a database compare-and-set, not tracked-entity timing.** Approve, reject and
   expire are one conditional `UPDATE … WHERE status = 'Pending' AND expires_at > now`; the
   affected-row count is the decision. A unique filtered index on `ActionExecution.PendingActionId`
   is the backstop. A caller that loses the race is shown the recorded outcome, never given a second
   one.

2. **No assistant write is sent before its decision and its execution identity are durably
   stored.** That includes a policy-allowed write: `auto` gets an execution too, so "what did the
   assistant attempt today" is one query rather than an inference from audit rows.

3. **The local effect and its idempotency receipt commit together.** One PostgreSQL transaction,
   opened by the endpoint filter and joined by the handler's own `SaveChanges`. The corollary is a
   hard constraint: **no handler under that filter may call an external service.** External work is
   an outbox row, committed with the effect and dispatched afterwards.

4. **A stale approval fails closed.** The proposal captures the target's `Invoice.Revision`; the
   approval sends it as `If-Match`; a mismatch is `412 resource_changed` and the server does *not*
   refresh and execute against state the user never saw. The command hash covers tool, argument
   bytes and revision precisely so that a changed invoice is a different command.

5. **`Unknown` is a state, and it is not `Failed`.** A request that crossed a non-transactional
   boundary without an answer is recorded as `Unknown`, reconciled from an authoritative receipt,
   and never blindly resent. `AuditDecision` stays an authorization vocabulary: an approved write
   the API refuses is `Confirmed` plus a failed execution.

### What this is not

Not a workflow engine, a saga framework or a reusable package. No distributed transaction across
PostgreSQL and a provider. The outbox is **at-least-once dispatch**; the end-to-end property is
*effectively once*, and only when the provider honours a stable key or exposes receipt lookup. With
neither capability an ambiguous result stays `Unknown` and waits for a person. No document in this
repository may claim exactly-once delivery without naming the provider capability that makes it so.

Deduplication is per `ActionExecution`, not per intention. Two similar tool calls are two
executions: guessing that they meant the same thing would suppress a second business action somebody
deliberately asked for.

## Consequences

- Three more persistence concepts. They are kept narrow and the vocabulary is defined once, here.
- Every invoice write endpoint now **requires** an `Idempotency-Key`. Missing is `400`, a reused key
  with a different fingerprint is `422` — never a plausible-looking replay of the wrong result.
- `POST /api/invoices/{number}/send` answers `202` with a delivery record. An invoice reading `Sent`
  no longer doubles as a claim that the customer received it, and the UI shows the two separately.
- Approving no longer means "done". The approval card shows execution state — *executing*,
  *completed*, *failed*, *outcome unknown* — written by the server, with no extra model call to
  narrate it (ADR 007's third point, unchanged and now load-bearing).
- ADR 002 is untouched: tools still call our own REST API over HTTP with the caller's bearer token.
  Bridging the transaction boundary that creates is exactly what the stable key plus the
  transactional receipt is for. No token, refresh token or provider secret is ever persisted, so
  nothing in this design can re-execute a command as a user who has gone away.
