# CLAUDE.md

Thin adapter for Claude Code. The canonical source of rules is **[AGENTS.md](AGENTS.md)** — read it first and follow it; this file only adds what is specific to this client.

## Claude Code specifics

- Verifiable setup, build and test commands: `.agent/commands.md`.
- Definition of done and delivery process: `.agent/delivery.md`.
- Skills live in `.claude/skills/`. `ui-design` carries the SPA's design language and triggers on any change to `src/Web`; it deliberately overrides the general-purpose `frontend-design` skill, which optimises for a fresh aesthetic each time and would drift this one apart.
- Before modifying `prompts/system.md`, review `evals/cases/` and run the evals job: a prompt regression must be caught before the PR.
- Do not add security rules to the system prompt: they belong in `policies.json` and on the server (see `docs/adr/001-server-side-write-gate.md`).
