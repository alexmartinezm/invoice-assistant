# The app, screen by screen

The running app against the seeded ledger — around 40 invoices, the three demo users, the real
policy file, `gpt-4o-mini` behind `/api/chat`. Nothing here is staged: the model picked its own
tools, the figures came back from the API, the approval summaries are written by the server, and
the refusals are the gate's own words.

Three of these appear in the [README](../README.md); this is the whole tour.

## The ledger

Sign in as one of three seeded users. The role you pick is the role the assistant inherits — the
same question gets different answers, and different refusals, depending on who is asking.

![The login screen listing the three demo users and what each may do](screenshots/login-pick-a-user.png)

The invoicing app itself: outstanding receivables and their aging on top, the ledger below, and the
assistant docked to the right on every page, so a conversation survives navigating between them.

![The invoices page with the receivables strip, filters and the ledger table, next to the assistant drawer](screenshots/invoice-ledger.png)

Selecting an invoice opens its lines, its VAT and its total. Every one of those figures is computed
in the domain — there is no path by which a model could produce them.

![Invoice 2026-0022 expanded, showing its two lines, subtotal, VAT and total](screenshots/invoice-detail.png)

Clicking an aging bucket narrows the ledger to the invoices in it, using the days-overdue figure the
server sent. The browser groups; it does not recalculate.

![The Over 60 days bucket selected, narrowing the table to its five overdue invoices](screenshots/receivables-aging.png)

## Asking the assistant

A read question becomes a tool call. The chip names the tool while it runs, and every number in the
answer came back from `get_receivables_summary` rather than from the model's arithmetic.

![The assistant answering "How much are we owed?" with a get_receivables_summary tool chip and an aging table](screenshots/assistant-reads-the-ledger.png)

Behind the role line in the drawer's footer sits the catalog itself, read from
`GET /api/assistant/tools` — the same one the model is handed. Nine tools, four of them read-only,
and nothing that deletes, operates in bulk, or touches more than the one invoice it names. This is
a Viewer's session, so the role floor on each row is the one their assistant would have to clear:
`cancel_invoice` says Admin, and no prompt changes that.

![The tool catalog panel open in the drawer, listing four read tools and five write tools with their role floors and risk levels](screenshots/assistant-tool-catalog.png)

## The write gate

Asking an Accountant's assistant to settle a €1,004.30 invoice does not settle it. Policy puts
`mark_invoice_paid` above the €100 auto-allow ceiling, so the gate turns the tool call into a
proposal with a five-minute window — and the sentence being approved is written by the server from
resolved data, not by the model that proposed it.

![An approval card asking to confirm marking invoice 2026-0025 as paid, with Approve and Reject](screenshots/assistant-approval-required.png)

Approving is not the last word either. Policy lets an Accountant confirm it; the API's own €1,000
settlement ceiling still refuses it, and nothing changes. Two independent limits, and the outer one
does not care that a human said yes.

![The approved action refused by the API, shown as a red resolved note in the transcript](screenshots/assistant-ceiling-refusal.png)

Under that ceiling the same flow goes through: approve, and the write executes under the approver's
identity, against the same REST API a person would have used.

![Invoice 2026-0039 sent after approval, confirmed with the resulting customer and amount](screenshots/assistant-approved-write.png)

A Viewer's write never becomes a proposal at all. The policy denies it outright, the audit log
records it, and the model is left to explain a decision it did not make and could not have avoided.

![A blocked card reading "cancel_invoice is not permitted for this user (write role=Viewer → deny)"](screenshots/assistant-policy-denied.png)

Asked for two cancellations at once, the model duly issued two tool calls. The per-turn write limit
stopped the second before it reached the ledger, and the first is still only a proposal — waiting on
an Admin, because an Accountant cannot cancel at all. This is the brake that stops a prompt
injection carried inside invoice data: it fires no matter how many times the model iterates.

![Two cancel_invoice calls, the second blocked by the one-change-per-turn limit](screenshots/assistant-write-limit-blocked.png)

## What it costs

Every model call is metered where it happens — inside the tool-calling loop, not once per turn,
because a turn that uses tools makes several calls. The model id is the pinned one the provider
reports, which is what the price table matches on.

![The usage page showing today's spend, the daily budget bar and one row per conversation](screenshots/usage-per-conversation.png)

Opening a conversation interleaves its model calls with the gate's decisions, so a refusal and the
tokens spent either side of it sit on one timeline.

![A conversation timeline showing two model calls either side of a denied cancel_invoice](screenshots/usage-conversation-timeline.png)
