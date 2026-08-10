# ADR 009 · Decision, execution and delivery are three facts

**Status:** accepted · 2026-08 · supersedes the execution wording of [ADR 001](001-server-side-write-gate.md) and [ADR 007](007-confirmation-does-not-end-the-turn.md) · preserves [ADR 002](002-tools-via-http.md)

## Context

The write gate is a good authorization boundary. It freezes the model's arguments, re-evaluates
policy under the approver's identity, calls the business API with that identity and records the
decision. What it has never had is an answer for the window *after* a decision and *before* the
effect is known.

Four concrete holes, all reachable in the shipped code:

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

Ten rules follow, and are decided here rather than left implicit:

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

4. **A stale approval fails closed, and so does an altered one.** The proposal captures the target's
   `Invoice.Revision`; the approval sends it as `If-Match`; a mismatch is `412 resource_changed` and
   the server does *not* refresh and execute against state the user never saw. The command hash
   covers tool, argument bytes and revision precisely so that a changed invoice is a different
   command — and it is **recomputed and compared at approval time**, not merely carried forward. A
   fingerprint nobody verifies records the tampering rather than preventing it.

5. **`Unknown` is a state, and it is not `Failed`.** A request that crossed a non-transactional
   boundary without an answer is recorded as `Unknown`, reconciled from an authoritative receipt,
   and never blindly resent. `AuditDecision` stays an authorization vocabulary: an approved write
   the API refuses is `Confirmed` plus a failed execution. A human refusal is `Rejected`, which is a
   different fact from policy's `Denied` — one of them is a decision somebody can be asked about.

6. **Every in-flight state has an owner and a way out.** An attempt holds a lease; when it lapses
   the execution is reclaimable *under the same idempotency key*, so a resume is a replay rather
   than a second write. A background pass settles what the evidence allows and releases what it
   cannot, and **it never re-executes**: it holds no bearer token and must not acquire one. Where no
   evidence exists the execution stays unsettled and a person decides, which is the honest outcome
   rather than a gap.

   The same rule binds the outbox. An ambiguous send is parked in its own status, out of the
   dispatcher's reach, and what may happen next is read from the provider's declared capabilities:
   ask when it can answer, resend only when it deduplicates, and otherwise wait for a person. A
   worker that defers an ambiguous send back to the queue is one that sends a second email on the
   strength of a shrug.

7. **A "not yet" is not a "no".** The idempotency filter's own `409 request_in_progress` — a twin
   still holding the key — is answered by a live attempt exactly the way a lost transport answer is:
   `Unknown`, not `Failed`. Reading it as a deterministic refusal would settle an execution as failed
   for an effect that had not actually failed, on the strength of a race the effect itself did not
   lose. The reclaim guard this implies — an `Unknown` execution is not retried before its own
   reconcile deadline — has one deliberate exception: a background pass that already checked for a
   receipt and a delivery and found neither *is* the "somebody looked and there is nothing" decision
   the deadline exists to wait for, so it releases the attempt immediately rather than making the
   next caller wait out a delay nothing further will resolve.

8. **A settler that finds no evidence defers; it does not spin.** A reconcile pass that cannot
   settle an `Unknown` execution pushes its own next look forward rather than leaving it "due"
   forever — otherwise the same unresolvable row is reselected first on every pass, ahead of
   whatever a batch limit is actually there to make room for.

9. **The database is the backstop against two writers, not just one.** Every row this feature settles
   outside a compare-and-set — `ActionExecution`, `InvoiceDelivery`, `OutboxMessage` — carries
   Postgres's own row version (`xmin`) as an EF concurrency token. A lease says who is *supposed* to
   be working a row; it does not stop a worker whose lease already lapsed from finishing a stale
   write anyway, and a live retry reconciling from a receipt can land at the same moment the outbox
   settles the same execution's delivery. The token is what turns whichever of the two writes second
   into a caught `DbUpdateConcurrencyException` — reloaded and answered from, never silently
   overwritten. The same token closes a gap one level up: a genuine revision race on `Invoice`,
   discovered only at save time rather than at the door, now answers the same `412 resource_changed`
   an `If-Match` mismatch does, not an unhandled 500.

   Referential integrity is the same idea applied to *which* rows may exist at all: foreign keys
   from `OutboxMessage` to the `InvoiceDelivery` it transports, from `InvoiceDelivery` to its
   `Invoice` and optionally to the `ActionExecution` waiting on it, and back — an invariant this code
   already maintained by construction, now one the database enforces rather than assumes.

10. **Settling the external effect and settling what it means for the execution waiting on it are
    two saves, not one.** The outbox dispatcher and the delivery reconciler each commit the
    delivery's and outbox row's outcome first — the record that a provider *actually did something* —
    and only then attempt the execution's own projection of that fact, in a save of its own. A
    conflict on the second must never roll back the first: an `ActionExecution` row losing a
    concurrency race is not a reason to leave a provider's acceptance unrecorded and the outbox row
    eligible to be sent again once its lease lapses. The corollary is a rule about what that loss
    means: a settler that loses this second save has not learned the execution failed, or that no
    evidence exists — it has learned someone else already recorded the truth, terminal or still in
    flight — so it must not invent a delay this pass already spent, demote a healthy hand-off to
    `Unknown`, or re-decide an outcome that is not its to decide. Where the loss leaves nothing
    watching the row at all — no lease of its own to expire, because it was never abandoned, only
    outrun — the execution reconciler's own sweep picks it up once the delivery has had a fair
    chance to settle, deriving the identical verdict from the record the first save already made
    durable. And a batch worker's `DbContext` carries no memory of a conflict it already lost: every
    entry a failed save left tracked is detached before the next row in the same pass is touched,
    the same discipline every per-row save in this feature now shares.

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
  narrate it (ADR 007's third point, unchanged and now load-bearing). The sentence travels **with**
  the execution, so a polling client renders the server's words rather than deriving copy from a
  status: `failed` on a refused delivery sits on an invoice that was issued, and only the server
  knows to say so.
- A body too large to fingerprint is refused with `413` rather than hashed by its prefix. Two
  requests that agree for 256 KiB are not the same request, and replaying one as the other would be
  a wrong answer that looks exactly like a right one.
- An `If-Match` the caller sent and got wrong answers `400 invalid_precondition` rather than being
  read as no header at all — a malformed value must not silently downgrade a conditional write into
  an unconditional one.
- A replayed response restores the allowlisted headers (`Location`, `ETag`) the first response
  carried. The idempotency contract is that a replay is indistinguishable from the original; a
  caller that created a resource and lost the reply still needs to be told where it landed.
- A `412 resource_changed` discovered at save time is a durable decision, not a transient one: the
  filter records a completed receipt for it the moment the losing transaction rolls back, so a retry
  under that same key replays the identical refusal instead of re-running the handler against
  whatever the resource has since become. `409 request_in_progress` stays the one exception — a
  "not yet", never worth a receipt of its own.
- An execution left `Unknown` with no evidence is not a dead end for the person who has to act on it:
  the approval card's own "Check again" is the same authorized approve action replayed under the same
  execution, not a new capability — resuming under one's own identity is what the reclaim guard in
  rule 7 was already built to allow.
- The sentence for a rejected delivery states that the invoice was issued and names nothing else: the
  provider's own error text is never interpolated into it. That text can hold anything a provider
  chooses to send back, and this sentence is both returned to a client and recorded as an
  assistant-authored line in the conversation — not a place for untrusted words with no closed
  vocabulary.
- ADR 002 is untouched: tools still call our own REST API over HTTP with the caller's bearer token.
  Bridging the transaction boundary that creates is exactly what the stable key plus the
  transactional receipt is for. No token, refresh token or provider secret is ever persisted, so
  nothing in this design can re-execute a command as a user who has gone away.
