# Claude + Codex + Copilot workflow

This repository provides four reusable skills and a resumable Python CLI:

| Stage | Author | Reviewer / gate |
| --- | --- | --- |
| Architecture, task ownership, API contracts | Claude Code | Copilot, at most 3 rounds |
| UI preview | Claude, self-contained HTML mockup | Human views and approves design + preview |
| Frontend | Codex CLI, separate worktree | Project checks + Copilot, at most 3 rounds |
| Backend | Claude Code, separate worktree | Project checks + Copilot, at most 3 rounds |
| Combined integration | Local integration worktree | Both roles' configured checks |
| Publication | Explicit `publish` command | Draft GitHub PRs, never auto-merged |
| Post-publication verification | Coordinator, with role authors fixing findings | Latest-commit Actions/checks complete; PR feedback and Sonar findings addressed |

Codex implements frontend work. The owner may direct a distinct retry run to use
Claude as the frontend implementation provider. Codex remains the default.

The three-round limit includes the first review. Exhaustion, malformed feedback,
failed checks, missing artifacts, and CLI errors block progress rather than imply
success. Attempts interrupted by errors conservatively consume a round.

## Setup

Requires Linux/macOS, Python 3.10+, Git with worktree support, authenticated
`claude`, `codex`, `copilot`, and `gh` CLIs, and Git author/committer identity.
Backend-only runs do not require Codex. There are no Python package dependencies.
Agent subscriptions, availability, permissions, and charges remain those of your
installed CLIs; this is not a provider SDK or an included subscription.

Skills live in [`.agents/skills`](../.agents/skills/). Claude discovers the same
skills through the [`.claude/skills`](../.claude/skills) symlink. Copilot and Codex
can discover `.agents/skills` directly. Restart the relevant session after setup
if it has already cached skill discovery.

Commit this setup before starting real work: worktrees see **committed** content,
not uncommitted changes in the main checkout. The orchestrator never commits or
stashes that checkout. No agent calls or GitHub publication occur during setup.

Review the installed CLIs' permissions and repository hooks/MCP configuration
before use. Claude uses `--permission-mode acceptEdits` without permission bypass;
`WebFetch` and `WebSearch` are explicitly allowed so headless design sessions can
verify public documentation without waiting for an interactive permission prompt.
Never send private repository content, credentials, or personal data to these
tools. Shell commands still need narrowly scoped permissions in your Claude
configuration; no blanket shell access is granted. Report permission denials
rather than trying alternate commands to evade them.
Codex uses `exec --sandbox workspace-write`, not unrestricted
execution. Copilot has only `view`, `glob`, and `grep` tools and cannot use shell
or write tools. Model selection comes from each CLI's configuration.
If a CLI rejects a new worktree as untrusted, inspect its repository configuration
and explicitly trust that directory using the CLI's interactive mode before
resuming. The orchestrator does not automatically bypass workspace trust.

These are trusted local agents, **not a security sandbox**. Git worktrees isolate
files and branches, not processes, network access, Git metadata, or credentials.
Do not run untrusted requirements or repository hooks. Review content before
publication; never include secrets in requirements, mockups, prompts, or logs.

## Run a feature

Save requirements and measurable acceptance criteria in a local text file.
Use a unique slug for each feature. Run these commands from the repository root:

```sh
python3 scripts/agentic_flow.py init billing-page \
  --requirements /absolute/path/to/requirements.txt --scope full-stack
python3 scripts/agentic_flow.py advance billing-page
```

`--base` defaults to the current branch; it must name a local branch also present
on `origin`. Use `--scope frontend` or `--scope backend` for single-role work.
The first `advance` runs Claude/Copilot design rounds and then **stops**.

Open the reported `design.md` and `preview.html` in the design worktree. The
preview is a visual HTML mockup, not implemented application code. When browser
tools are available, render it and capture screenshots; otherwise view the HTML
directly. A backend-only task has no UI preview requirement.

If a UI design is unresolved after three review rounds, the run stops. Keep the
run state and all feedback. Show the owner the working `preview.html` and the
open feedback. Use a screenshot only if the HTML cannot render or the owner asks
for one. Do not infer approval, and do not reset or bypass the limit. Only
explicit owner approval, or a genuinely revised new run, moves the work forward.

Set checks appropriate to the approved design. The following commands are
examples, not assumed project tooling; this repository does not yet contain an
application:

```sh
python3 scripts/agentic_flow.py checks billing-page \
  --frontend 'npm run test:frontend -- --run' \
  --frontend 'npm run typecheck' \
  --backend 'dotnet test'
```

Each active role needs at least one real check covering its acceptance criteria.
Checks run from that role's worktree and again from the combined integration
worktree. Repeat flags to add build/lint/test commands. Each command is parsed
into an argument list, **not executed through a shell**. For `cd`, pipelines,
environment assignments, or compound commands, use a reviewed project script.
Checks must leave no non-ignored artifacts or modified files. Dependency restore
and environment setup must be available in each worktree; a worktree does not
share ignored dependency directories with another checkout.

Only after the human has viewed and approved the design and mockup:

```sh
python3 scripts/agentic_flow.py approve billing-page --design-and-preview
python3 scripts/agentic_flow.py advance billing-page
python3 scripts/agentic_flow.py status billing-page
```

`approve` records your approval; it does not solicit or infer it. Agents must
never call it on the user's behalf without explicit approval.

Frontend and backend run sequentially in separate worktrees from the reviewed
design commit. This keeps orchestration simple while isolating ownership.
Copilot reviews the full role diff, approved design, and check output and can
read surrounding source. The orchestrator commits only after approval. Checks
are frozen after human approval. A local integration worktree merges the
reviewed role heads and runs all configured checks together.

To publish, explicitly authorize and run:

```sh
python3 scripts/agentic_flow.py publish billing-page
```

This pushes the design/frontend/backend branches without force and creates draft
PRs. The design PR targets the original base; implementation PRs target the
design branch. Review/merge the design PR first, retarget implementation PRs to
the original base, and rerun CI. Keep the design branch until dependents are
retargeted. Do not merge the local integration branch: it exists only for tests.
Publication can resume after partial failure; open PRs are reused.

## Post-publication gate

Creating a PR or pushing a fix is not completion. The coordinator owns this gate for **every**
published PR, including manually created PRs, and repeats it after every push, rebase or
retargeting that changes the checked source/base. The current CLI `publish` command only pushes
branches and creates PRs: it does **not** automate this gate. A published CLI state is not evidence
that CI, PR review or Sonar analysis succeeded.

1. Record the PR's current head SHA and enumerate its Actions runs, check runs and commit statuses.
   Wait for **all** applicable workflows/checks to finish, not just the first build or required
   check. Include tests, image/release jobs triggered by this update and asynchronous
   SonarQube/SonarCloud analysis. Verify results belong to the latest head SHA; if it changes,
   restart verification. Do not start a release or create a tag merely to satisfy this gate.
2. Inspect unsuccessful job logs and annotations. Also read PR reviews, general comments, inline
   comments and unresolved review threads, including automated reviewer feedback. An all-green
   build alone does not mean the PR has no outstanding issues.
3. Inspect the SonarQube/SonarCloud quality gate **and** findings for the PR analysis, following
   the analysis/check link when necessary. Check new-code issues and relevant security hotspots;
   do not assume a passed quality gate means there are no actionable findings. If analysis is
   expected but not yet reported, continue waiting. If it is unconfigured, inaccessible or fails
   for an external reason, report that explicitly rather than claiming Sonar is clean.
4. Fix defects introduced by or tightly coupled to the PR in its implementation branch/worktree.
   Do not suppress quality gates, weaken tests, dismiss valid feedback or make unrelated baseline
   repairs to obtain green checks. Delegate fixes to the assigned role when appropriate; rerun
   local checks and required implementation review, then commit and push to the **same PR**.
   Preserve existing review counters and caps. Explain addressed feedback on the PR; resolve a
   thread only after verifying its issue is actually fixed.
5. After pushing, repeat from step 1 for the new head SHA. Finish only when all applicable checks
   have completed successfully (or intentional non-applicable skips are explained), relevant PR
   feedback is addressed and Sonar's quality gate/findings have been checked. Report the verified
   SHA, check results, Sonar result and any remaining blockers. Pending, missing, cancelled,
   unexpectedly skipped or inaccessible verification is **not** a pass. If permissions, service
   outages, baseline issues or the review cap block progress, report the exact limitation and do
   not present the PR as fully verified.

Useful starting points with the authenticated GitHub CLI:

```sh
gh pr view <PR> --json headRefOid,statusCheckRollup,reviews,comments
gh pr checks <PR> --watch
gh run list --commit <head-SHA>
gh run view <run-ID> --log-failed
```

`gh pr checks --watch` covers checks GitHub associates with the PR, not necessarily every Actions
run or Sonar finding. Enumerate runs separately and inspect inline review threads through the
GitHub API or PR UI. A draft PR may have intentionally disabled analysis; do not mark it ready
without permission or count missing analysis as successful verification.

## State and recovery

- Branches: `agentic/<task>/design`, `frontend`, `backend`, and `integration`.
- Worktrees: sibling directory `<repository>-worktrees/<task>/<role>`.
- State, prompts, output, review feedback, and check logs:
  `<git-common-dir>/agentic-flow/<task>/`. These are local, not PR content.
- Design artifacts: `docs/agentic/<task>/design.md` and `preview.html`, versioned
  in the design branch and inherited by each implementation branch.
- `status` shows phase, worktrees, rounds, feedback, and PR URLs. `advance`
  resumes unfinished work. A lock prevents concurrent commands for the same run.
- A failed check blocks before review; inspect logs and fix the underlying
  problem in that role worktree before retrying. Do not fabricate passing checks.
- Review inputs are never silently truncated. Diffs larger than 60 KB must be
  split into smaller tasks. Copilot's full prompt is subject to OS argument limits;
  an oversized prompt fails explicitly rather than producing a partial review.
  Design prompts ask for less than 40 KB of combined artifacts to leave revision
  headroom. This is guidance, not a replacement for the enforced 60 KB diff limit.
  Review prompts include the measured diff size. Designs must record source URLs
  and observed version fields, distinguishing declared support from tested behavior.
- A changed approved HEAD or dirty approved worktree blocks further progress.
  A moved remote base blocks publication. Resolve/rebase and create a fresh,
  fully reviewed run; do not edit approval state to bypass validation.
- Integration conflicts remain in the integration worktree for inspection.
  Correct the ownership/contracts and create a new reviewed run. Manually
  committing a conflict resolution in the integration worktree is not accepted:
  that would publish code not reviewed by Copilot. Integration checks are not a
  replacement for review.
- Three unsuccessful attempts require human intervention. Preserve the blocked
  run's feedback; revise/split requirements before creating a genuinely new run.
  Do not simply rename the task to evade the cap. For UI designs, follow the
  escalation in *Run a feature*.
- No cleanup is automatic. After PRs are merged, use `git worktree remove` on
  specific clean worktrees, then delete only the corresponding obsolete branches.
  Never recursively remove the repository or Git state.

## Offline validation

```sh
python3 -m unittest discover -s tests -p 'test_agentic_flow.py' -v
```

Tests use temporary Git repositories and stub agents. They do not consume
subscriptions, launch real model calls, push branches, or create real PRs.

CLI references:
[Claude Code](https://code.claude.com/docs/en/cli-reference),
[Codex non-interactive mode](https://developers.openai.com/codex/noninteractive),
[Copilot programmatic reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference).
