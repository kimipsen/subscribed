---
name: agentic-flow
description: 'Use when coordinating Claude and Copilot through design, UI previews, human approval, isolated Git worktrees, implementation reviews, draft GitHub PRs, and post-publication Actions and SonarQube/SonarCloud follow-up.'
---

# Agentic flow

Use the repository's [workflow guide](../../../docs/agentic-flow.md) and
[orchestrator](../../../scripts/agentic_flow.py). Run commands from the repository root.

1. Clarify requirements, acceptance criteria, UI scope, and constraints. Save the
   requirements to a local text file. Never include credentials or private user data.
2. Check that `claude`, `copilot`, and `gh` are installed and authenticated.
3. Initialize a named run with `init`, choosing frontend, backend, or full-stack.
   The orchestrator creates a design worktree from a committed base.
4. Run `advance`. Claude creates the design and, for UI work, a self-contained
   HTML mockup. Copilot reviews it. Claude addresses feedback, with at most three
   review rounds total. Unresolved or malformed reviews stop the workflow. If a
   UI design remains unresolved after three rounds, preserve the blocked run and
   feedback, then present the working HTML preview to the owner. Use a screenshot
   only if the HTML cannot be rendered or the owner asks for one. This handoff is
   not approval and does not reset the review counter.
5. Present the actual mockup or screenshot to the user, not just its description.
   An HTML mockup is not an implemented or tested UI. Open it in a browser when
   available; otherwise provide its path and wait for the user to view it. A
   working HTML preview is sufficient; do not require a separate screenshot
   unless the preview cannot be rendered or the user requests one.
6. Configure real project checks with `checks`. Obtain explicit user approval of
   the reviewed design and preview before running `approve`; never self-approve.
7. Run `advance` again. Claude implements frontend and backend work. Copilot
   reviews each role's changes. Checks must pass before review.
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
After three unsuccessful attempts, preserve feedback and wait for owner
direction; revise/split requirements before a genuinely new run. Do not reset
review counters to get around the three-round cap, and do not treat viewing the
preview as approval.
