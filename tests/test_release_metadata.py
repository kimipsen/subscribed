import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/release_metadata.py"
SPEC = importlib.util.spec_from_file_location("release_metadata", SCRIPT)
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.options = {
            "tag": "v1.2.3", "commit": "a" * 40,
            "image": "ghcr.io/kimipsen/subscribed", "digest": "sha256:" + "b" * 64,
            "repository": "kimipsen/subscribed",
            "run_url": "https://github.com/kimipsen/subscribed/actions/runs/123",
            "output_dir": self.folder,
        }
        for name in ("image.sbom.spdx.json", "frontend.sbom.spdx.json"):
            (self.folder / name).write_text(json.dumps({
                "spdxVersion": "SPDX-2.3", "SPDXID": "SPDXRef-DOCUMENT",
                "packages": [{"name": "synthetic-package", "SPDXID": "SPDXRef-package"}],
            }))

    def test_assets_record_exact_source_digest_and_checksums(self):
        release.generate_assets(**self.options)
        metadata = json.loads((self.folder / "release.json").read_text())
        self.assertEqual(metadata["sourceCommit"], self.options["commit"])
        self.assertEqual(metadata["imageDigest"], self.options["digest"])
        self.assertEqual(metadata["platforms"], ["linux/amd64"])
        self.assertEqual(metadata["imageReference"], self.options["image"] + "@" + self.options["digest"])
        self.assertEqual((self.folder / "SOURCE_COMMIT.txt").read_text(), self.options["commit"] + "\n")
        lines = (self.folder / "SHA256SUMS").read_text().splitlines()
        self.assertEqual(len(lines), 5)
        for line in lines:
            checksum, name = line.split("  ")
            self.assertEqual(checksum, hashlib.sha256((self.folder / name).read_bytes()).hexdigest())
        notes = (self.folder / "release-notes.md").read_text()
        self.assertIn(metadata["imageReference"], notes)
        self.assertIn(f"{self.options['image']}:1.2.3", notes)
        self.assertIn(self.options["commit"], notes)
        self.assertIn("development dependencies", notes)

    def test_supported_version_tags(self):
        for tag in ("v0.0.0", "v1.2.3", "v11.22.333-rc.1", "v1.0.0-alpha-beta"):
            with self.subTest(tag=tag):
                release.validate_tag(tag)

    def test_invalid_version_tags(self):
        for tag in ("latest", "1.2.3", "v1", "v01.2.3", "v1.2.3-01", "v1.2.3+build", "v1.2.3\n"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.validate_tag(tag)

    def test_invalid_identity_is_rejected_before_outputs(self):
        for field, value in (
            ("commit", "abc123"), ("digest", "sha256:abc123"),
            ("image", "ghcr.io/another/project"),
            ("repository", "owner/repo/extra"),
            ("run_url", "https://example.invalid/actions/runs/123"),
        ):
            with self.subTest(field=field), self.assertRaises(ValueError):
                release.generate_assets(**(self.options | {field: value}))
        self.assertFalse((self.folder / "release.json").exists())

    def test_invalid_sbom_is_not_a_successful_release(self):
        for payload in ("not json", "[]", "{}", '{"spdxVersion":null}',
                        '{"spdxVersion":"SPDX-2.3","SPDXID":"SPDXRef-DOCUMENT","packages":[]}'):
            (self.folder / "image.sbom.spdx.json").write_text(payload)
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                release.generate_assets(**self.options)
        self.assertFalse((self.folder / "SHA256SUMS").exists())

    def test_missing_sbom_fails(self):
        (self.folder / "frontend.sbom.spdx.json").unlink()
        with self.assertRaises(FileNotFoundError):
            release.generate_assets(**self.options)

    def test_cli_reports_failure_and_prerelease_validation(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "validate-tag", "v1.2.3-rc.1"],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "validate-tag", "v1.2.3-01"],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("Release metadata error:", result.stderr)

    def test_assets_cli_generates_the_downloadable_metadata(self):
        arguments = [sys.executable, str(SCRIPT), "assets"]
        for key, value in self.options.items():
            arguments.extend(("--" + key.replace("_", "-"), str(value)))
        result = subprocess.run(arguments, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        metadata = json.loads((self.folder / "release.json").read_text())
        self.assertEqual(metadata["tag"], self.options["tag"])
        self.assertEqual(metadata["buildRun"], self.options["run_url"])
        self.assertTrue((self.folder / "SHA256SUMS").is_file())


if __name__ == "__main__":
    unittest.main()
