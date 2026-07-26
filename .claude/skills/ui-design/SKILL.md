---
name: ui-design
description: The UI design language of invoice-assistant and the rules that keep it coherent. Use this skill whenever you touch anything the user sees in src/Web — building or restyling a page, a component, a card, a table, a form, a chart or an empty state; picking colours, type, spacing or motion; adding the F2 approval and block cards or the F4 usage page; or reviewing frontend work for quality. Use it even when the request sounds purely functional ("add a button to reject a pending action", "show the token cost"), because every visible change either holds this design language together or quietly erodes it.
---

# UI design · invoice-assistant

## Why this skill exists

This repo is a portfolio piece. People will read the code, but first they will look at it, and a
screenshot that looks like every other AI-generated dashboard undermines the argument the code is
making. The SPA therefore commits to a specific look — **ink on paper, a ledger you read rather than
a dashboard you browse** — and the job of anyone touching the UI is to extend that look, not to
restart it.

That is the one place this skill deliberately contradicts the general-purpose `frontend-design`
skill. That skill tells you to pick a bold new direction and never converge on the same choices
twice, which is right when you are designing something from nothing. Here the direction is already
chosen and committed. A second session that invents a fresh aesthetic does not produce a better
app; it produces a repo with two aesthetics and no point of view. So: keep the anti-slop instincts
from that skill, drop the novelty-seeking, and treat the existing language as the constraint you
design inside.

If you genuinely believe the design language itself is wrong, say so and change it deliberately,
everywhere, in one pass. Drifting away from it one component at a time is the failure mode.

## How this relates to hallmark

[`hallmark`](../hallmark/) is vendored into this repo at `.claude/skills/hallmark/` — an
anti-AI-slop design skill with a 58-gate slop test. The two are complementary, and the split is:

- **This file is the system.** Tokens, type, the aging ramp, the rules that carry domain meaning.
  It is the equivalent of the `design.md` that hallmark's pre-flight step looks for and treats as
  overriding everything else. Where the two disagree on *what this app looks like*, this file wins.
- **hallmark is the check.** Run `hallmark audit <files>` against UI work — its gates catch the
  concrete, measurable failures that taste alone misses: contrast ratios, horizontal scroll at
  320 px, clickable text wrapping, missing interaction states, layout-property animation.

Do **not** run hallmark's default Design flow here. It picks a macrostructure, a nav archetype, a
footer archetype and a hero enrichment, and rotates them for variety between builds — all of which
is aimed at marketing pages, and all of which would fight the committed system. hallmark says as
much itself: its Component-scope branch exists because "the page-level apparatus is wrong for"
day-to-day product work, and a `design.md`-managed project *inverts* its diversification rule so
pages share the system rather than differ. This app is a ledger and a chat drawer, not a landing
page.

The gates were last run against the whole SPA and it passes the measurable ones: contrast 19/19
token pairs in both themes, no horizontal scroll at 320/375/414/768 px, no wrapped clickable labels.
Re-run them after any visible change rather than trusting that they still hold.

## Read these first

The language is defined in code, not in prose, so read the source rather than trusting this summary:

- `src/Web/src/index.css` — tokens, fonts, base layer, the `.card` / `.numeric` / motion helpers.
- `src/Web/src/invoices/ReceivablesStrip.tsx` — the aging strip: the most opinionated component in
  the app, and the clearest example of the intended density and tone.
- `src/Web/src/components/Pills.tsx` — how status is encoded with borders and fill weight.
- `src/Web/src/format.ts` — money and dates. Never format either inline.

## The vocabulary

**Type is one superfamily, three roles.** IBM Plex Serif (`font-display`) for headings, Plex Sans
(`font-sans`) for everything else, Plex Mono (`font-mono`) for anything that is a number or an
identifier. One family in three voices reads as deliberate; mixing in a fourth typeface reads as
someone who did not look at what was already there. Fonts are bundled via `@fontsource`, not fetched
from a CDN, because the demo has to run in one container with no network.

**Colour is paper, ink, one accent, and a severity ramp.**

| Group | Tokens | Use for |
|---|---|---|
| Surfaces | `paper`, `raised`, `sunken` | Page, cards, insets and table headers |
| Text | `ink`, `ink-soft`, `ink-faint` | Primary, secondary, and metadata |
| Lines | `rule`, `rule-strong` | Hairline borders; `rule-strong` only when a border must be read as an edge |
| Accent | `accent`, `accent-soft`, `accent-ink` | The single brand colour: primary actions, selection, collected money |
| Aging | `aging-current`, `aging-30`, `aging-60`, `aging-over` | **Only** how late money is |
| Danger | `danger`, `danger-soft` | Failures and refusals: an error, and from F2 a policy-denied action |

The aging ramp is a data scale, not a palette. Reaching for `aging-over` because you want "a red" —
on a delete button, a validation message, an unrelated warning — breaks the one visual rule the app
actually teaches: that amber-through-red means overdue and nothing else. `danger` exists so you
never have to. This distinction is about to earn its keep: F2's block card is a refusal, and if it
borrowed the ramp then red in this app would mean both "very overdue" and "blocked", which is
exactly the ambiguity the separation prevents.

Work in flight is neutral, not amber: the tool chips in the chat drawer pulse `ink-faint` while
running and settle to `accent` when done. Distinguishing two states by animation alone fails under
`prefers-reduced-motion`, so they differ in colour too.

Every token has a light and a dark value already. Use the semantic name (`bg-sunken`) and both
themes come out right; write a literal (`bg-gray-100`) and you have just made a component that only
works in one of them.

**Numbers line up.** Amounts, dates, invoice numbers, trace ids and token counts all take
`.numeric`, which is Plex Mono with tabular figures. In a ledger, a column of numbers that does not
align is a bug, not a preference. Amounts are right-aligned; text is not.

**Motion is confirmation, not decoration.** There are three effects in the app: `.ledger-row`
(staggered rise, capped so row 200 does not animate a second late), `.slide-in` (something arriving
— a tool chip, a panel), and `.caret` (the model is still thinking). All are CSS, all respect
`prefers-reduced-motion` through the global reset at the bottom of `index.css`. Adding a motion
library for a fourth effect is almost certainly the wrong trade.

## Rules that carry meaning, not taste

These look like styling choices and are not. Getting them wrong makes the UI say something false.

- **Overdue is not a status.** An invoice is Draft, Sent, Paid or Cancelled; overdue is derived from
  the due date. `StatusPill` renders the overdue reading *instead of* the status for that reason. Do
  not add a fifth pill colour and do not put "Overdue" in a status filter.
- **Only collected money gets a filled badge.** Paid is filled, Sent is outline, Draft is dashed,
  Cancelled is struck through. Weight encodes finality, so the eye finds settled invoices without
  reading a word.
- **Model output and customer data are untrusted.** Assistant text renders through `react-markdown`
  with `remark-gfm`; invoice line descriptions render as text. Neither ever goes through
  `dangerouslySetInnerHTML` — one seeded invoice carries a prompt injection in a line description
  precisely so that a mistake here is visible.
- **The server computed it; show it, do not recompute it.** `daysOverdue`, bucket totals and VAT all
  arrive from the API. Grouping already-computed values in the browser is fine; doing the arithmetic
  again is how the UI and the assistant start disagreeing about the same number.
- **Format money and dates through `format.ts`.** The system prompt tells the assistant to write
  €1,240.50 and 26/07/2026. If a table formats them differently, the chat and the page it sits next
  to contradict each other on screen.

## The failure modes to avoid

Generic AI-generated UI is not bad because it is ugly; it is bad because it is anonymous. Each of
these is a way to make this app look like every other one:

- **A purple or indigo gradient anywhere.** The accent is a deep teal-green and there are no
  gradients on surfaces, only the proportional aging bar and the page grain.
- **Emoji as iconography.** None are used. A coloured dot, a rule or a weight change carries the
  same signal without the cartoon register.
- **Evenly-spaced pastel cards in a three-up grid,** each with an icon, a big number and a caption.
  The aging strip does the same job with a proportional bar, because the *distribution* of the debt
  is the story, not four numbers side by side.
- **Rounded-everything and drop shadows to create hierarchy.** Hierarchy here comes from hairline
  rules and surface level. `shadow-card` is the only shadow, and it is nearly invisible on purpose.
- **A spinner in the middle of an empty panel.** Loading dims the existing content
  (`aria-busy` + reduced opacity) so the layout does not jump and the user keeps their place.
- **Explaining the interface in the interface.** Empty states say what to do next
  (the chat drawer offers three real questions to click); they do not narrate what the panel is.
- **Centred text and generous whitespace as a substitute for density.** This is a ledger. Forty rows
  on screen at once is correct; a page that shows eight invoices in a card carousel is not.

## When you are adding something genuinely new

F2 brings approval and block cards; F4 brings the usage page and a cost timeline. Neither exists
yet, so you will be designing, not copying. Work outward from what is there:

1. **Find the nearest existing thing** and start from its structure. An approval card is closer to
   `InvoiceDetailPanel` (a header with a status, a body of facts, a decision at the end) than to
   anything you would invent from scratch.
2. **Say what the new element means in the existing vocabulary.** A block card is a refusal, so it
   is bordered and text-coloured, not filled — filled is reserved for settled money. An approval card
   is pending, so it carries the accent, and its countdown is `.numeric` like every other figure.
3. **Add a token rather than a one-off colour** if you truly need a new semantic (a destructive
   action, a denied state). Put it in `index.css` next to its neighbours so the next person finds it
   and both themes get a value.
4. **Charts follow the same rules.** Categorical series use the accent plus neutrals; anything
   ordered by severity reuses the aging ramp. If a data-visualisation skill is available, use it for
   chart-type and accessibility guidance — but its palette advice is subordinate to these tokens.

## Before you call it done

Check the things that are cheap to verify and embarrassing to miss:

- Rendered in **both** colour schemes — the tokens make this free, so there is no excuse for a
  component that only works in light.
- Real data, not three tidy rows: forty invoices, a customer name that overflows, a €0.00 total, an
  empty filter result.
- Narrow viewport: the drawer overlays below `lg` and docks above it; a new page needs an answer for
  both.
- Keyboard reachable, with the focus ring intact (it is defined globally on `:focus-visible`), and
  semantic elements — a clickable row still contains a real `button`.
- `npm run lint`, `npm run format:check` and `npm run build` all clean, per `.agent/commands.md`.
