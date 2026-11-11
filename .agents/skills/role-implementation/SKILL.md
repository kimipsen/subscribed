---
name: role-implementation
description: 'Use when Codex implements frontend tasks or Claude implements backend tasks from an approved design in isolated worktrees, with tests and Copilot review.'
---

# Role implementation

Follow [the workflow](../../../docs/agentic-flow.md). Confirm human approval
before editing application code.

- **Codex owns frontend implementation:** UI, client state, accessibility,
  responsive behavior, and frontend tests. Match the approved preview. Capture
  actual UI screenshots when tooling is available; label them as implemented UI,
  distinct from the design mockup.
- **Claude owns backend implementation:** APIs, persistence, authorization,
  validation, migrations, and backend tests.
- Follow the design's shared-file ownership and API contracts. Do not overwrite
  the other role's changes. If a contract must change, stop for a revised design
  and renewed approval rather than silently diverging.
- Work only in the assigned role worktree. Do not switch branches or manage
  commits, pushes, PRs, review counters, or approvals; the orchestrator owns those.
- Run existing targeted tests, lint, and type/build checks. Add focused regression
  coverage. Report missing dependencies and permission blockers explicitly.
- Copilot reviews each iteration. Address its findings and rerun checks. Stop
  after three review rounds if still unresolved.
- Hand off changed files, validation results, limitations, and any actual UI
  screenshots. Integration checks must pass before publication.
