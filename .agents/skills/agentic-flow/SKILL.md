---
name: agentic-flow
description: 'Use when coordinating Claude, Codex, and Copilot through design, UI previews, human approval, isolated Git worktrees, implementation reviews, draft GitHub PRs, and post-publication Actions and SonarQube/SonarCloud follow-up.'
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
   After three unresolved UI-design rounds, keep the run and feedback, show the
   owner the working HTML preview (a screenshot only if it cannot render or the
   owner asks), and never infer approval or reset the limit.
5. Present the actual mockup or screenshot to the user, not just its description.
   An HTML mockup is not an implemented or tested UI. Open it in a browser when
   available; otherwise provide its path and wait for the user to view it.
6. Configure real project checks with `checks`. Obtain explicit user approval of
   the reviewed design and preview before running `approve`; never self-approve.
7. Run `advance` again. Codex implements frontend work; Claude implements backend
   work. The owner may direct a distinct retry run to use Claude as the frontend
   implementation provider. Codex remains the default. Copilot reviews each role's changes. Checks must pass before review.
   Each role has a separate worktree and a three-round review limit.
8. The orchestrator merges both roles into a local integration worktree and runs
   the configured checks together. Resolve any conflicts explicitly; never
   discard another agent's work or waive failed checks.
9. Report results and obtain permission to run `publish`. It pushes only the
   reviewed branches and creates draft, stacked GitHub PRs. Never auto-merge.
10. Publication is not completion. Follow the workflow guide's
    [post-publication gate](../../../docs/agentic-flow.md#post-publication-gate)
    for every PR and every subsequent push. Wait for all Actions runs and PR
    checks for the latest head SHA to finish, including builds, tests, release
    jobs and asynchronous SonarQube/SonarCloud analysis. Inspect failed logs,
    PR reviews/comments/unresolved threads, and Sonar quality-gate results and
    actionable findings. Fix issues caused by or relevant to the PR, run local
    checks, obtain required implementation review, and push to the same PR.
    Repeat the wait-and-inspect cycle on the new SHA. Never treat missing,
    pending, cancelled, inaccessible or unexpectedly skipped checks as passing.
    Report unavailable Sonar analysis, permissions, external failures and
    unrelated baseline issues explicitly; do not claim a clean result or
    mark the task complete while required verification remains unresolved.

Use `status` to resume a run. Keep feedback and run state out of source control.
Do not reset review counters to get around the three-round cap.
