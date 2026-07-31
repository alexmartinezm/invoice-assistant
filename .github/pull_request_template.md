<!--
Title in Conventional Commits form (feat:, fix:, docs:, chore:…). The full definition of done is
in .agent/delivery.md; this template is the short version of it.
-->

## What changes

## How to verify

<!-- The commands you actually ran, from .agent/commands.md. -->

## Checks

- [ ] Tests and quality gates green locally
- [ ] Security invariants hold: no write reaches the DB without a policy `allow` or a human
      approval recorded in `AuditEvent`
- [ ] Docs updated — a new ADR if a decision changed, `.agent/commands.md` if commands changed
- [ ] If this touches `prompts/system.md`, `policies.json` or the assistant slice: the evals ran on
      a branch of this repository, and the run is linked above

<!--
On a fork's pull request the evals job reports green without calling a model — that means "not
run", not "passed". See evals/README.md.
-->
