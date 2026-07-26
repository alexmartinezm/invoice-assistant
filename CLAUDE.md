# CLAUDE.md

Thin adapter for Claude Code. The canonical source of rules is **[AGENTS.md](AGENTS.md)** — read it first and follow it; this file only adds what is specific to this client.

## Claude Code specifics

- Verifiable setup, build and test commands: `.agent/commands.md`.
- Definition of done and delivery process: `.agent/delivery.md`.
- Skills live in `.claude/skills/`:
  - `ui-design` — this project's design system, and the authority on what the SPA looks like. Triggers on any change to `src/Web`. It deliberately overrides skills that optimise for a fresh aesthetic each time (`frontend-design`, and hallmark's default Design flow), because reinventing the look per session drifts the repo apart.
  - `hallmark` — vendored from [nutlope/hallmark](https://github.com/nutlope/hallmark) (MIT, see `VENDORED.md`). Use its `audit` verb and slop-test gates to check UI work; do not run its default Design flow, which is built for marketing pages. Never hand-edit the vendored files.
- Before modifying `prompts/system.md`, review `evals/cases/` and run the evals job: a prompt regression must be caught before the PR.
- Do not add security rules to the system prompt: they belong in `policies.json` and on the server (see `docs/adr/001-server-side-write-gate.md`).
