# src/Web

React 19 + Vite + TypeScript + Tailwind 4. Served by the Api from `wwwroot` in production; in
development `npm run dev` proxies `/api` to `http://localhost:5080` (override with `API_URL`). Either
way the browser talks to a single origin, which is why there is no CORS configuration anywhere.

```text
src/
├── api/            # fetch client, SSE parsing, response types
├── auth/           # session context (token in localStorage, deliberately inspectable)
├── invoices/       # receivables strip, filters, table, detail panel
├── chat/           # useChatStream + the drawer
├── pages/          # Login, Invoices
└── format.ts       # money and dates, in the formats the system prompt asks the assistant for
```

Pages: **Login** (the three demo users, one click each), **Invoices** (aging strip + filters +
detail) and a persistent **Chat** drawer. Approval and block cards arrive with the write gate in F2;
**Usage** in F4.

## Notes for whoever changes this

- **The model's output is untrusted.** It renders through `react-markdown`, which produces React
  elements and never passes raw HTML through. The same applies to invoice line descriptions, which are
  customer data — one seeded invoice carries a prompt injection in a line on purpose.
- **`remark-gfm` is not optional.** The system prompt asks for a table whenever a listing runs past
  three rows, and tables are a GFM extension rather than plain CommonMark. Without the plugin the
  assistant's tables render as rows of pipe characters.
- **`EventSource` cannot POST**, so a chat turn is a `fetch` whose body is parsed as SSE by hand. The
  server's event names and the payload's `type` discriminator match, so a client can switch on either.
- **Amounts use tabular figures** (`.numeric`, IBM Plex Mono). In a ledger, columns that do not line up
  are a bug.
- **The JWT is on display**, decoded, behind the user menu. It is what the assistant's tools carry, and
  seeing the `role` claim inside it is what makes propagated identity concrete.

## Two pinned versions, and why

- **TypeScript is held at 5.9** rather than 7.x: `typescript-eslint` declares a peer range of
  `>=4.8.4 <6.1.0`, so moving to TypeScript 7 would quietly drop linting from the toolchain. React and
  Vite are both on latest.
- **`npm audit` reports one advisory** against `react-router` (RSC-mode CSRF bypass,
  GHSA-qwww-vcr4-c8h2), with no fixed release yet. It does not reach this app: a client-only SPA on
  `BrowserRouter`, with no RSC mode and no server actions. Downgrading is worse — the versions below
  the advisory's range are missing a dozen fixes that 7.18 has.
