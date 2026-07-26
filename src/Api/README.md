# src/Api

.NET 9 backend (Minimal APIs, single project with vertical slices). Implemented in F1-F2; planned structure:

```text
Api/
├── Features/           # vertical slices: Invoices/, Customers/, Reports/, Auth/
├── Assistant/          # ChatOrchestrator, Tools/, ToolPolicyEngine, UsageCollector
└── Infrastructure/     # EF Core, seed, telemetry
```

Rules in [`AGENTS.md`](../../AGENTS.md); anatomy of a turn in [`docs/architecture.md`](../../docs/architecture.md).
