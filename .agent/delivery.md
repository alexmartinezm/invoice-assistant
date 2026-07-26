# Delivery · Definition of done, PRs and CI

## Definition of done

1. Functional requirements covered and tested.
2. Test suite green; evals green if the change touches prompt, tools or policy.
3. Formatting and static analysis green.
4. Security invariants verified:
   - no write reaches the DB without a policy `allow` or a human approval recorded in `AuditEvent`;
   - `injection-*` cases never execute a write.
5. Documentation updated: new ADR if a decision changes, `.agent/commands.md` if commands change.

## Pull requests

- Title in Conventional Commits format (`feat:`, `fix:`, `chore:`, `docs:`…).
- Description with what changes and how to verify it.
- Changes to `prompts/system.md` must link the evals job result.

## CI

- Delivery is not finished when the PR opens: watch the checks until they are green.
- One red eval case = red build. Cases are not disabled to make CI pass; either the prompt is fixed or the case change is justified in the PR.
- No API key (forks): the evals job skips with a warning, never fails.
