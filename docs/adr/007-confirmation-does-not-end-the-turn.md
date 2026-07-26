# ADR 007 · `require_confirmation` does not end the turn

**Status:** accepted · 2026-07 · supersedes one point of [ADR 001](001-server-side-write-gate.md)

## Context

ADR 001 says a `require_confirmation` decision "creates a `PendingAction` … and ends the turn". Implementing F2 showed that sentence describes an orchestration this codebase does not have.

Tool calls are executed by `Microsoft.Extensions.AI`'s `FunctionInvokingChatClient`, which owns the loop: it invokes the function, appends the result to the conversation and goes straight back to the model. `ChatOrchestrator` only *observes* `FunctionCallContent` and `FunctionResultContent` as they stream past. There is no seam where the orchestrator can decide "stop here" without either replacing that middleware or throwing out of a tool — and a tool that throws reaches the model as a failure, which is the opposite of what a proposal means.

Ending the turn would also leave the user with an approval card and no sentence next to it. ADR 001 already warns about exactly that failure for the post-approval turn: a silent assistant is a worse outcome than an extra sentence.

## Decision

**The turn continues.** A gated write returns a structured result — `status: "pending_approval"`, the action id, the server's summary and the deadline — the model writes one short line about it, and the `approval_required` SSE event puts the card beside that line. Nothing has been written to the database.

Three things follow, and are decided here rather than left implicit:

1. **The gate lives inside the tool delegates**, not in `ChatOrchestrator`. It is the one choke point every call must pass and the model has no route around it. A scoped `TurnJournal` carries approvals and blocks back out; the orchestrator drains it between streaming updates. This costs one of the "no more than two file jumps" hops that reading a turn is allowed (acceptance criterion 6), so the drain in `ChatOrchestrator` is explicit and points at `ToolGate`.

2. **The audit vocabulary is `auto | confirmed | denied | blocked`.** ADR 001 lists three and omits `confirmed`; `docs/architecture.md` and `evals/cases/injection-03.yaml` both assume four. Four it is: `auto` for a policy-allowed write, `confirmed` for one a human approved, `denied` for a policy refusal, `blocked` for a per-turn or per-conversation limit.

   `auto` therefore means one thing only — *a change happened and nobody approved it* — which is what `injection-03` asserts the absence of. That is why an allowed **read** is not audited: auditing reads would put `auto` rows in the trail for a turn in which nothing changed, and the assertion would stop meaning anything. A read the gate *refuses* is still audited; that one is a security event.

3. **The closing line is written by the server, not by the model.** Approving is its own HTTP request, so there is no model in the loop to narrate the outcome. Rather than spend a second model call on it, `POST /api/actions/{id}/approve|reject` returns the sentence and records it as an assistant message in the conversation. The user is told what happened to their money by the component that knows; the assistant cannot be wrong about it, or be talked into being wrong about it.

## Consequences

- The user sees a sentence and a card together, and the transcript still reads as a conversation.
- Approval re-evaluates policy under the approver's identity and replays the frozen arguments (ADR 001, unchanged). The action id doubles as the `Idempotency-Key`, so a retried approval cannot pay an invoice twice.
- **A proposal spends the per-turn write budget, exactly as an execution does.** This falls out of the decision and is the part that is easy to get wrong: if only executed writes counted, "cancel every invoice" would execute nothing and still produce eight approval cards. Nothing would have been written, and the user would be looking at a wall of pre-filled Approve buttons — which is how approval fatigue is manufactured, and a slower route to the same compromise. The budget is spent by the intent, not by the outcome; the second proposal in a turn is `blocked`.

- The model is told, in the tool result, to say one line and stop. That is a prompt-level nicety, not a guarantee: the guarantee is the budget above, so a model that keeps going gets blocked rather than obeyed.
- ADR 001 stays as written. Changing a decision here requires a new ADR that supersedes it, never a silent edit — this file is that ADR.
