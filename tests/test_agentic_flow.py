import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("agentic_flow", ROOT / "scripts/agentic_flow.py")
flow = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(flow)


class FlowTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.repo = self.folder / "repo"
        self.repo.mkdir()
        self.git("init", "--initial-branch=master")
        self.git("config", "user.name", "Workflow Test")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "core.hooksPath", "/dev/null")
        (self.repo / "base.txt").write_text("baseline\n")
        self.git("add", "base.txt")
        self.git("commit", "-m", "baseline")
        self.base = self.git("rev-parse", "HEAD")
        self.requirements = self.folder / "requirements.txt"
        self.requirements.write_text("Build a frontend and backend with agreed contracts.")
        self.original_cwd = Path.cwd()
        os.chdir(self.repo)
        self.addCleanup(os.chdir, self.original_cwd)
        self.calls = []
        self.feedback = {}
        self.tools = mock.patch.object(flow, "require_tools")
        self.tools.start()
        self.addCleanup(self.tools.stop)
        self.agents = mock.patch.object(flow, "run_agent", side_effect=self.agent)
        self.agents.start()
        self.addCleanup(self.agents.stop)

    def git(self, *args):
        result = subprocess.run(
            ["git", "--no-pager", *args], cwd=self.repo,
            text=True, capture_output=True, check=True,
        )
        return result.stdout.strip()

    def invoke(self, *args, expected=0):
        output = io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            code = flow.main(list(args))
        self.assertEqual(code, expected, output.getvalue())
        return output.getvalue()

    def state(self):
        return json.loads(self.state_path.read_text())

    @property
    def state_path(self):
        return self.repo / ".git/agentic-flow/example/state.json"

    def init(self, scope="full-stack"):
        self.invoke("init", "example", "--requirements", str(self.requirements), "--scope", scope)

    def checks(self, roles=("frontend", "backend"), failure=False):
        argv = ["checks", "example"]
        for role in roles:
            program = "raise SystemExit(4)" if failure else "import sys; print('checks passed', file=sys.stderr)"
            check = shlex.join([sys.executable, "-c", program])
            argv += [f"--{role}", check]
        self.invoke(*argv)

    def approve(self, roles=("frontend", "backend")):
        self.checks(roles)
        self.invoke("approve", "example", "--design-and-preview")

    def ready(self):
        self.init()
        self.invoke("advance", "example")
        self.approve()
        self.invoke("advance", "example")
        self.assertEqual(self.state()["phase"], "ready")

    def agent(self, provider, prompt, stage, directory, label):
        self.calls.append((provider, label, Path(stage["worktree"])))
        if provider == "copilot":
            role = label.split("-")[0]
            queue = self.feedback.get(role, [])
            return queue.pop(0) if queue else '{"verdict":"approve","feedback":[]}'
        role = label.split("-")[0]
        worktree = Path(stage["worktree"])
        if role == "design":
            artifacts = worktree / "docs/agentic/example"
            artifacts.mkdir(parents=True, exist_ok=True)
            (artifacts / "design.md").write_text("Approved design: frontend.txt and backend.txt are separate.\n")
            if "preview.html" in prompt:
                (artifacts / "preview.html").write_text("<html><body>Visual mockup</body></html>\n")
        else:
            (worktree / f"{role}.txt").write_text(f"{role} implementation\n")
        return "Completed requested artifacts."

    def test_end_to_end_and_human_gate(self):
        self.init()
        self.invoke("approve", "example", "--design-and-preview", expected=1)
        output = self.invoke("advance", "example")
        state = self.state()
        self.assertEqual(state["phase"], "awaiting-approval")
        self.assertIn("preview.html", output)
        self.assertEqual([call[0] for call in self.calls], ["claude", "copilot"])
        self.invoke("advance", "example")
        self.assertEqual(len(self.calls), 2)
        self.invoke("approve", "example", "--design-and-preview", expected=1)
        self.approve()
        self.invoke("advance", "example")
        state = self.state()
        self.assertEqual(state["phase"], "ready")
        self.assertEqual(
            [(provider, label) for provider, label, _ in self.calls],
            [
                ("claude", "design-1-author"), ("copilot", "design-1-review"),
                ("codex", "frontend-1-author"), ("copilot", "frontend-1-review"),
                ("claude", "backend-1-author"), ("copilot", "backend-1-review"),
            ],
        )
        frontend = Path(state["stages"]["frontend"]["worktree"])
        backend = Path(state["stages"]["backend"]["worktree"])
        integration = Path(state["integration"]["worktree"])
        self.assertNotEqual(frontend, backend)
        self.assertFalse((frontend / "backend.txt").exists())
        self.assertFalse((backend / "frontend.txt").exists())
        self.assertTrue((integration / "frontend.txt").is_file())
        self.assertTrue((integration / "backend.txt").is_file())
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)
        self.assertFalse((self.repo / "frontend.txt").exists())
        log = self.repo / ".git/agentic-flow/example/frontend-1-frontend-check-1.txt"
        self.assertIn("checks passed", log.read_text())
        for stage in state["stages"].values():
            message = flow.git(stage["worktree"], "log", "-1", "--format=%B")
            self.assertIn(flow.TRAILER, message)

    def test_design_never_exceeds_three_reviews(self):
        self.init()
        self.feedback["design"] = [
            '{"verdict":"revise","feedback":["Clarify contract."]}'
        ] * 3
        self.invoke("advance", "example", expected=1)
        self.assertEqual(self.state()["stages"]["design"]["rounds"], 3)
        count = len(self.calls)
        self.invoke("advance", "example", expected=1)
        self.assertEqual(len(self.calls), count)
        self.assertEqual(self.state()["phase"], "design")

    def test_feedback_revision_succeeds_on_third_round(self):
        self.init()
        self.feedback["design"] = [
            '{"verdict":"revise","feedback":["Clarify contract."]}',
            '{"verdict":"revise","feedback":["Add error states."]}',
            '{"verdict":"approve","feedback":[]}',
        ]
        self.invoke("advance", "example")
        self.assertEqual(self.state()["stages"]["design"]["rounds"], 3)
        self.assertEqual(self.state()["phase"], "awaiting-approval")

    def test_implementation_review_cap(self):
        self.init()
        self.invoke("advance", "example")
        self.approve()
        self.feedback["frontend"] = ['{"verdict":"revise","feedback":["Fix regression."]}'] * 3
        self.invoke("advance", "example", expected=1)
        self.invoke("advance", "example", expected=1)
        self.assertEqual(self.state()["stages"]["frontend"]["rounds"], 3)
        self.assertNotIn("backend", self.state()["stages"])
        self.invoke("publish", "example", expected=1)

    def test_review_schema_rejects_success_shaped_fallbacks(self):
        for output in (
            "", "approved", "```json\n{}\n```", "[]", "null",
            '{"verdict":"approve","feedback":["Unresolved bug"]}',
            '{"verdict":"approve","feedback":[],"extra":true}',
            '{"verdict":"revise","feedback":[]}',
            '{"verdict":"revise","feedback":[1]}',
        ):
            with self.subTest(output=output), self.assertRaises(flow.FlowError):
                flow.parse_review(output)

    def test_malformed_review_blocks(self):
        self.init()
        self.feedback["design"] = ["Not JSON"]
        self.invoke("advance", "example", expected=1)
        self.assertFalse(self.state()["stages"]["design"]["approved"])
        self.invoke("approve", "example", "--design-and-preview", expected=1)

    def test_backend_only_has_no_ui_or_codex(self):
        self.init("backend")
        self.invoke("advance", "example")
        self.approve(("backend",))
        self.invoke("advance", "example")
        self.assertEqual(self.state()["phase"], "ready")
        self.assertFalse(any(provider == "codex" for provider, _, _ in self.calls))
        self.assertFalse((Path(self.state()["stages"]["design"]["worktree"]) / "docs/agentic/example/preview.html").exists())

    def test_failed_checks_block_review_and_publication(self):
        self.init()
        self.invoke("advance", "example")
        self.checks(failure=True)
        self.invoke("approve", "example", "--design-and-preview")
        self.invoke("advance", "example", expected=1)
        self.assertFalse(any(label == "frontend-1-review" for _, label, _ in self.calls))
        self.invoke("publish", "example", expected=1)
        self.invoke("checks", "example", "--frontend", "echo waived", expected=1)

    def test_design_preview_required(self):
        self.init()
        original = self.agent

        def no_preview(*args):
            result = original(*args)
            preview = Path(args[2]["worktree"]) / "docs/agentic/example/preview.html"
            if preview.exists():
                preview.unlink()
            return result

        with mock.patch.object(flow, "run_agent", side_effect=no_preview):
            self.invoke("advance", "example", expected=1)
        self.assertFalse(self.state()["stages"]["design"]["approved"])

    def test_staged_out_of_scope_design_edit_is_rejected(self):
        self.init()
        original = self.agent

        def stray_edit(*args):
            result = original(*args)
            worktree = Path(args[2]["worktree"])
            (worktree / "rogue.txt").write_text("not design")
            flow.git(worktree, "add", "rogue.txt")
            return result

        with mock.patch.object(flow, "run_agent", side_effect=stray_edit):
            self.invoke("advance", "example", expected=1)
        self.assertFalse(self.state()["stages"]["design"]["approved"])

    def test_reviewer_edits_are_rejected(self):
        self.init()
        original = self.agent

        def editing_reviewer(*args):
            result = original(*args)
            if args[0] == "copilot":
                (Path(args[2]["worktree"]) / "rogue.txt").write_text("reviewer edit")
            return result

        with mock.patch.object(flow, "run_agent", side_effect=editing_reviewer):
            self.invoke("advance", "example", expected=1)
        self.assertFalse(self.state()["stages"]["design"]["approved"])

    def test_approved_artifacts_cannot_change(self):
        self.init()
        self.invoke("advance", "example")
        self.approve()
        original = self.agent

        def changed_design(*args):
            result = original(*args)
            if args[0] == "codex":
                (Path(args[2]["worktree"]) / "docs/agentic/example/design.md").write_text("changed")
            return result

        with mock.patch.object(flow, "run_agent", side_effect=changed_design):
            self.invoke("advance", "example", expected=1)
        self.assertFalse(self.state()["stages"]["frontend"]["approved"])

    def test_dirty_reviewed_worktree_blocks_publication(self):
        self.ready()
        worktree = Path(self.state()["stages"]["frontend"]["worktree"])
        (worktree / "frontend.txt").write_text("unreviewed")
        self.invoke("publish", "example", expected=1)

    def test_agent_commit_cannot_be_accepted_on_retry(self):
        self.init()
        original = self.agent

        def committing_author(*args):
            result = original(*args)
            worktree = args[2]["worktree"]
            flow.git(worktree, "add", "--all")
            flow.git(worktree, "commit", "-m", "Unauthorized author commit")
            return result

        with mock.patch.object(flow, "run_agent", side_effect=committing_author):
            self.invoke("advance", "example", expected=1)
        count = len(self.calls)
        self.invoke("advance", "example", expected=1)
        self.assertEqual(len(self.calls), count)
        self.assertFalse(self.state()["stages"]["design"]["approved"])

    def test_integration_checks_are_required_even_after_role_checks_pass(self):
        self.init()
        self.invoke("advance", "example")
        program = (
            "from pathlib import Path; "
            "raise SystemExit(4 if Path('frontend.txt').exists() "
            "and Path('backend.txt').exists() else 0)"
        )
        self.invoke(
            "checks", "example",
            "--frontend", shlex.join([sys.executable, "-c", program]),
            "--backend", shlex.join([sys.executable, "-c", "print('passed')"]),
        )
        self.invoke("approve", "example", "--design-and-preview")
        self.invoke("advance", "example", expected=1)
        self.assertTrue(self.state()["stages"]["frontend"]["approved"])
        self.assertTrue(self.state()["stages"]["backend"]["approved"])
        self.assertNotEqual(self.state()["phase"], "ready")
        self.invoke("publish", "example", expected=1)

    def test_changed_integration_head_cannot_be_accepted_on_retry(self):
        self.ready()
        stage = self.state()["integration"]
        worktree = Path(stage["worktree"])
        (worktree / "unreviewed.txt").write_text("manual edit")
        flow.git(worktree, "add", "--all")
        flow.git(worktree, "commit", "-m", "Unreviewed integration change")
        self.invoke("publish", "example", expected=1)

    def test_lock_blocks_concurrent_commands(self):
        self.init()
        with flow.locked(self.state_path.parent):
            self.invoke("advance", "example", expected=1)
        self.assertFalse(self.calls)

    def test_cli_errors_are_not_success_and_are_logged(self):
        self.agents.stop()
        with mock.patch.object(flow, "command", side_effect=flow.FlowError("provider failed")):
            with self.assertRaisesRegex(flow.FlowError, "provider failed"):
                flow.run_agent("claude", "task", {"worktree": str(self.repo)}, self.folder, "failure")
        self.assertIn("provider failed", (self.folder / "failure.error.txt").read_text())

    def test_design_prompt_bounds_scope_and_artifact_size(self):
        self.init()
        self.invoke("advance", "example")
        author_prompt = flow.run_agent.call_args_list[0].args[1]
        self.assertIn("below 40 KB", author_prompt)
        self.assertIn("do not design later milestones", author_prompt)
        self.assertIn("Do not implement the application", author_prompt)
        self.assertIn("Do not send private repository content", author_prompt)
        self.assertIn("source URL and observed version fields", author_prompt)

    def test_review_prompt_reports_measured_diff_size(self):
        self.init()
        self.invoke("advance", "example")
        stage = self.state()["stages"]["design"]
        diff = flow.git(stage["worktree"], "diff", "--binary", stage["base"], stage["head"])
        review_prompt = flow.run_agent.call_args_list[1].args[1]
        self.assertIn(f"Complete diff size: {len(diff.encode())} bytes", review_prompt)

    def test_conflicting_roles_block_integration(self):
        self.init()
        self.invoke("advance", "example")
        self.approve()
        original = self.agent

        def conflict(*args):
            result = original(*args)
            if args[0] in ("claude", "codex"):
                (Path(args[2]["worktree"]) / "shared.txt").write_text(args[0] + "\n")
            return result

        with mock.patch.object(flow, "run_agent", side_effect=conflict):
            self.invoke("advance", "example", expected=1)
        self.assertNotEqual(self.state()["phase"], "ready")
        self.invoke("publish", "example", expected=1)

    def test_publication_is_explicit_stacked_draft_and_resumable(self):
        self.ready()
        calls = []
        original_git = flow.git
        original_command = flow.command

        def publishing_git(cwd, *args):
            if args[0] == "push":
                calls.append(("push", args))
                return ""
            if args[0] == "ls-remote":
                return self.base + "\trefs/heads/master"
            return original_git(cwd, *args)

        def gh_command(argv, cwd, **kwargs):
            if argv[0] != "gh":
                return original_command(argv, cwd, **kwargs)
            calls.append(("gh", argv))
            if argv[1:3] == ["pr", "list"]:
                return "[]"
            if argv[1:3] == ["pr", "create"]:
                self.assertIn("--draft", argv)
                base = argv[argv.index("--base") + 1]
                head = argv[argv.index("--head") + 1]
                self.assertEqual(base, "master" if head.endswith("/design") else "agentic/example/design")
                return "https://github.com/example/repo/pull/" + str(len(calls))
            return ""

        with mock.patch.object(flow, "git", side_effect=publishing_git), mock.patch.object(flow, "command", side_effect=gh_command):
            self.invoke("publish", "example")
            self.invoke("publish", "example")
        self.assertEqual(self.state()["phase"], "published")
        self.assertEqual(len(self.state()["prs"]), 3)
        creates = [args for kind, args in calls if kind == "gh" and args[1:3] == ["pr", "create"]]
        self.assertEqual(len(creates), 3)
        self.assertFalse(any("--force" in args for _, args in calls))

    def test_moved_remote_base_blocks_publication(self):
        self.ready()
        original = flow.git
        original_command = flow.command

        def moved_base(cwd, *args):
            if args[0] == "ls-remote":
                return "0" * 40 + "\trefs/heads/master"
            return original(cwd, *args)

        def execute(argv, cwd, **kwargs):
            if argv[0] == "gh":
                return ""
            return original_command(argv, cwd, **kwargs)

        with mock.patch.object(flow, "git", side_effect=moved_base), mock.patch.object(flow, "command", side_effect=execute):
            self.invoke("publish", "example", expected=1)
        self.assertFalse(self.state()["prs"])

    def test_partial_publication_resumes_without_duplicate_design_pr(self):
        self.ready()
        original_git = flow.git
        original_command = flow.command
        creates = []
        failed_once = False

        def publishing_git(cwd, *args):
            if args[0] == "push":
                return ""
            if args[0] == "ls-remote":
                return self.base + "\trefs/heads/master"
            return original_git(cwd, *args)

        def gh_command(argv, cwd, **kwargs):
            nonlocal failed_once
            if argv[0] != "gh":
                return original_command(argv, cwd, **kwargs)
            if argv[1:3] == ["pr", "list"]:
                return "[]"
            if argv[1:3] == ["pr", "create"]:
                head = argv[argv.index("--head") + 1]
                if head.endswith("/frontend") and not failed_once:
                    failed_once = True
                    raise flow.FlowError("Temporary GitHub error")
                creates.append(head)
                return f"https://github.com/example/repo/pull/{len(creates)}"
            return ""

        with mock.patch.object(flow, "git", side_effect=publishing_git), mock.patch.object(flow, "command", side_effect=gh_command):
            self.invoke("publish", "example", expected=1)
            self.assertEqual(list(self.state()["prs"]), ["design"])
            self.assertEqual(self.state()["phase"], "ready")
            self.invoke("publish", "example")
        self.assertEqual(self.state()["phase"], "published")
        self.assertEqual(creates, [
            "agentic/example/design",
            "agentic/example/frontend",
            "agentic/example/backend",
        ])

    def test_agents_use_expected_cli_permissions(self):
        stage = {"worktree": str(self.repo)}
        with mock.patch.object(flow, "command", return_value="result") as execute:
            self.agents.stop()
            for provider in ("claude", "codex", "copilot"):
                flow.run_agent(provider, "task", stage, self.folder, provider)
            args = [call.args[0] for call in execute.call_args_list]
            self.assertIn("acceptEdits", args[0])
            self.assertEqual(args[0][args[0].index("--allowedTools") + 1], "WebFetch,WebSearch")
            self.assertFalse(any("Bash" in item for item in args[0]))
            self.assertIn("workspace-write", args[1])
            self.assertIn("--available-tools=view,glob,grep", args[2])
            self.assertIn("--deny-tool=write,shell", args[2])
            self.assertFalse(any("dangerously" in item for argv in args for item in argv))

    def test_skill_discovery_and_frontmatter(self):
        skills = ROOT / ".agents/skills"
        self.assertEqual((ROOT / ".claude/skills").resolve(), skills)
        files = list(skills.glob("*/SKILL.md"))
        self.assertEqual(len(files), 4)
        for file in files:
            content = file.read_text()
            frontmatter = content.split("---", 2)[1]
            self.assertIn(f"name: {file.parent.name}\n", frontmatter)
            self.assertIn("description: 'Use when ", frontmatter)
            self.assertTrue((file.parent / "../../../docs/agentic-flow.md").resolve().is_file())


if __name__ == "__main__":
    unittest.main()
