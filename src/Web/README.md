# src/Web

React 19 + Vite + TypeScript + Tailwind SPA, served by the Api in prod. Implemented in F1; planned pages:

- **Login** — selector for the 3 demo users (JWT visible in a tooltip: didactic).
- **Invoices** — list + filters + detail.
- **Chat** — persistent side drawer: SSE streaming, approval cards (`PendingAction`) and block cards, trace id in the footer. Model output sanitized, never raw HTML.
- **Usage** — cost, tokens and tool calls per conversation.
