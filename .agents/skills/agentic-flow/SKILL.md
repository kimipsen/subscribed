---
name: agentic-flow
description: 'Use when coordinating Claude, Codex, and Copilot through design, UI previews, human approval, isolated Git worktrees, implementation reviews, and draft GitHub PRs.'
---

# Agentic flow

Use the repository's [workflow guide](../../../docs/agentic-flow.md) and
[orchestrator](../../../scripts/agentic_flow.py). Run commands from the repository root.

1. Clarify requirements, acceptance criteria, UI scope, and constraints. Save the
   requirements to a local text file. Never include credentials or private user data.
2. Check that `claude`, `codex` (when frontend work is needed), `copilot`, and `gh`
   are installed and authenticated. Do not silently substitute providers.
3. Initialize a named run with `init`, choosing frontend, backend, or full-stack.
   The orchestrator creates a design worktree from a committed base.
4. Run `advance`. Claude creates the design and, for UI work, a self-contained
   HTML mockup. Copilot reviews it. Claude addresses feedback, with at most three
   review rounds total. Unresolved or malformed reviews stop the workflow.
5. Present the actual mockup or screenshot to the user, not just its description.
   An HTML mockup is not an implemented or tested UI. Open it in a browser when
   available; otherwise provide its path and wait for the user to view it.
6. Configure real project checks with `checks`. Obtain explicit user approval of
   the reviewed design and preview before running `approve`; never self-approve.
7. Run `advance` again. Codex implements frontend work; Claude implements backend
   work. Copilot reviews each role's changes. Checks must pass before review.
   Each role has a separate worktree and a three-round review limit.
8. The orchestrator merges both roles into a local integration worktree and runs
   the configured checks together. Resolve any conflicts explicitly; never
   discard another agent's work or waive failed checks.
9. Report results and obtain permission to run `publish`. It pushes only the
   reviewed branches and creates draft, stacked GitHub PRs. Never auto-merge.

Use `status` to resume a run. Keep feedback and run state out of source control.
Do not reset review counters to get around the three-round cap.
