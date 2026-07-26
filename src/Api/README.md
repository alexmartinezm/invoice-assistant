# src/Api

.NET 10 backend: Minimal APIs, one project, vertical slices.

```text
Api/
├── Program.cs          # the whole composition, top to bottom
├── Domain/             # entities and their invariants: transitions and money live here
├── Features/           # vertical slices: Auth/, Customers/, Invoices/, Reports/
├── Assistant/          # ChatOrchestrator, Tools/, ChatEndpoints  (+ ToolPolicyEngine in F2)
└── Infrastructure/     # EF Core, migrations, seed, telemetry, configuration
```

Start reading at `Assistant/ChatOrchestrator.cs`: one chat turn, start to finish, in one file.

Rules in [`AGENTS.md`](../../AGENTS.md); anatomy of a turn in [`docs/architecture.md`](../../docs/architecture.md).

## Notes for whoever changes this

- **The domain owns the rules.** `Invoice` exposes no setter for status or totals. A transition that
  would break the state machine throws `DomainException`, which becomes a `409` carrying a
  machine-readable `code`. Endpoints load, call an aggregate method, and save.
- **`Overdue` is not a status.** It is derived from the due date on every read, so it cannot go stale
  in the database.
- **Tools reach the API over HTTP** (ADR 002), carrying the caller's `Authorization` header via
  `ForwardCallerIdentityHandler`. That one handler is the whole "the assistant can never do more than
  the logged-in user" guarantee — remove it and every tool result becomes a 401.
- **Migrations and seed run at startup.** The wrong default for a real production service and the
  right one here: the promise is "clone, compose up, working demo with data", with nobody to run a
  migration step in between.
- **The app boots without an AI provider.** `NotConfiguredChatClient` stands in, and `/api/chat`
  answers 503 naming the variables it needs.
