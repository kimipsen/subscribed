"""deploy/Dockerfile must copy every source project and its lock file before the first restore.

CI never builds the image, so a missing line would only surface in the release workflow.
"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCKERFILE = ROOT / "deploy" / "Dockerfile"
SOURCES = ROOT / "backend" / "src"


def lines_before_first_restore() -> list[str]:
    text = DOCKERFILE.read_text(encoding="utf-8").replace("\\\n", " ")
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if re.match(r"\s*RUN\s+dotnet restore\b", line):
            return lines[:index]
    raise AssertionError("deploy/Dockerfile has no 'RUN dotnet restore' line")


class DockerfileRestoreTests(unittest.TestCase):
    def test_every_source_project_and_lock_file_is_copied_before_restore(self) -> None:
        copied = "\n".join(line for line in lines_before_first_restore() if line.lstrip().startswith("COPY"))
        projects = sorted(SOURCES.glob("*/*.csproj"))
        self.assertTrue(projects, "no source projects found")
        for project in projects:
            relative = project.relative_to(ROOT).as_posix()
            lock = project.with_name("packages.lock.json").relative_to(ROOT).as_posix()
            self.assertIn(relative, copied, f"{relative} is not copied before dotnet restore")
            self.assertTrue(project.with_name("packages.lock.json").exists(), f"{lock} is missing")
            self.assertIn(lock, copied, f"{lock} is not copied before dotnet restore")


if __name__ == "__main__":
    unittest.main()
