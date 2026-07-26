# ADR 008 · Cost accounting is middleware, and the kill switch counts in the database

**Status:** accepted · 2026-07

## Context

F4 has to answer two questions with the same data. *What did this conversation cost?* — the number
the Usage page shows, and the reason anyone believes the "<1€/day" claim. And *may this call
happen at all?* — the kill switch that makes a public demo safe to leave running with a real key in
it.

The obvious shape is a service the orchestrator calls once per turn: start a timer, ask the model,
record what came back. It does not survive contact with how a turn actually runs. A turn that uses
tools is not one model call; `FunctionInvokingChatClient` owns a loop that calls the model, runs the
tool, and calls the model again — up to `maxToolCallsPerTurn` times. `ChatOrchestrator` watches
that loop stream past and never sees an individual call. Metering per turn would therefore price a
five-call turn as one, and — worse — a budget checked once per turn would let a single long turn
spend arbitrarily past the cap before anyone looked again.

## Decision

**`UsageCollector` is an `IChatClient` in the pipeline, underneath the function-invoking client.**
Every model call passes through it, so it meters each one and checks the budget before each one.
Registration lives in `ChatClientRegistration.AddAssistantChatPipeline`, which the app, the
integration tests and the evals all call — the metering under test is the metering that ships.

Four things follow.

**1. Prices are configuration; an unknown model costs zero and says so loudly.** `Usage:Prices` is a
list of euros per million tokens, matched against the model id by longest prefix, so
`gpt-4o-mini-2024-07-18` finds the `gpt-4o-mini` entry rather than the `gpt-4o` one. A model with no
entry is recorded at zero with a warning and its own metric (`assistant.unpriced_model_calls`)
rather than priced by a guessed default. A wrong number here is worse than a missing one: it would
be spent silently, and the kill switch would be counting fiction.

The consequence is stated plainly because it is a real hole: **spend on an unpriced model is
invisible to the kill switch.** The metric exists so that hole is observable, and configuring a
price closes it.

**2. Cost is in euros, whatever the ledger bills in.** `Invoicing:Currency` is what the demo
invoices its customers in; the budget is `Usage:DailyBudgetEur`. Rendering model spend in the
invoicing currency would imply a conversion nobody performed. The Usage page formats to four
decimals for the same reason: a single small call costs fractions of a cent, and two decimals would
round every row to €0.00.

**3. Today's spend is a query, not a counter.** `UsageBudget` sums `usage_records` for the current
UTC day. An in-memory counter would be faster and would be wrong in the three ways that matter: it
resets on deploy, it disagrees with the figure the Usage page shows, and it is per-process. The
budget day is the UTC day — a demo does not need a billing timezone, it needs the same boundary
everywhere it is checked.

**4. The cap is global, not per-user, and enforced at two points.** `/api/chat` checks it before
the stream starts, while a status code is still available, and answers `429` with
`code: daily_budget_exhausted`. `UsageCollector` checks it again before every model call, so a turn
that crosses the line mid-flight stops there and the failure reaches the browser as an `error`
event naming the budget. The per-user rate limit already in place protects against one impatient
user; it does nothing about a scraper with many IPs, which is what the euro cap is for.

## Consequences

- The Usage page, `GET /api/usage/summary` and the kill switch all read one table, so they cannot
  disagree. Everyone sees their own conversations; an Admin sees everyone's. The budget figures are
  global on any of those views, because a per-user slice of one shared wallet would misstate how
  close the demo is to pausing.
- Recording happens in its own DI scope and with `CancellationToken.None`: the request's
  `DbContext` may be mid-operation in the orchestrator, and a user who closes the tab has still
  consumed the tokens. Metering must not fail a turn, and must not be skippable by disconnecting.
- The check is read-then-act, so two turns starting in the same instant can both pass and overshoot
  the cap by one turn's spend. With a 1€ cap and turns costing fractions of a cent that is not worth
  a lock or a reservation table; the honest statement is "a daily cap, not a hard ledger limit".
- A call the model makes is metered even when the gate then refuses every tool it asked for.
  Refusing costs tokens too, and a cost report that hid them would understate what injection
  attempts cost to defend against.
- Each model call is also a span (`assistant.model_call`) carrying model, tokens, latency and cost,
  under the turn's activity — so the same numbers are readable in a trace, not only in the table.
