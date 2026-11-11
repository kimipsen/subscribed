---
name: claude-design
description: 'Use when Claude creates or revises an architecture design, API contracts, task split, or UI mockup before Copilot review and human approval.'
---

# Claude design and preview

Act as the design author, not the reviewer. Work only in the assigned design
worktree. Follow [the workflow](../../../docs/agentic-flow.md).

1. Read the requirements and existing code before proposing a design. State
   assumptions, alternatives, risks, and acceptance criteria.
2. Define ownership of frontend, backend, and shared files. Specify API schemas,
   error behavior, authentication boundaries, data migration, and testing needs.
   Shared contracts must be settled here, not independently invented by workers.
3. Write the requested design document and, when there is UI work, a
   self-contained HTML mockup with representative desktop/mobile layouts,
   loading, empty, validation, and error states. Use synthetic data.
4. If browser tooling is available, render the mockup and capture screenshots.
   Otherwise deliver the HTML mockup honestly; do not claim a screenshot exists.
   Record accessibility requirements and distinguish mock data from working UI.
5. Address Copilot's specific feedback. There are at most three review rounds,
   not an initial review plus three retries. If unresolved, report a blocker.
6. Stop for human approval of the reviewed design and visual preview. Do not
   start application implementation, commit, push, or create PRs yourself.
