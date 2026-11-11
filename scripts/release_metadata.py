#!/usr/bin/env python3
"""Generate release assets tied to the image digest, not a mutable image tag."""

import argparse
import hashlib
import json
from pathlib import Path
import re


TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")


def validate_tag(tag):
    match = TAG.fullmatch(tag)
    if not match or (match[4] and any(
        part.isdigit() and len(part) > 1 and part.startswith("0")
        for part in match[4].split(".")
    )):
        raise ValueError("Release tag must be vMAJOR.MINOR.PATCH with an optional SemVer prerelease.")


def generate_assets(*, tag, commit, image, digest, repository, run_url, output_dir):
    validate_tag(tag)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source commit must be a full Git SHA.")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise ValueError("Image digest must be SHA-256.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Repository must be owner/name.")
    if image != f"ghcr.io/{repository.lower()}":
        raise ValueError("Image must belong to this repository in GHCR.")
    if not re.fullmatch(rf"https://github\.com/{re.escape(repository)}/actions/runs/[0-9]+", run_url):
        raise ValueError("Run URL must identify this repository's GitHub Actions run.")

    folder = Path(output_dir)
    sboms = ("image.sbom.spdx.json", "frontend.sbom.spdx.json")
    for name in sboms:
        sbom = json.loads((folder / name).read_text())
        if not (isinstance(sbom, dict)
                and isinstance(sbom.get("spdxVersion"), str)
                and sbom["spdxVersion"].startswith("SPDX-2.")
                and sbom.get("SPDXID") == "SPDXRef-DOCUMENT"
                and isinstance(sbom.get("packages"), list) and sbom["packages"]):
            raise ValueError(f"{name} must be a nonempty SPDX 2.x package inventory.")

    reference = f"{image}@{digest}"
    metadata = {
        "tag": tag, "sourceCommit": commit, "image": image,
        "imageDigest": digest, "imageReference": reference,
        "platforms": ["linux/amd64"], "buildRun": run_url,
        "sboms": list(sboms),
    }
    (folder / "release.json").write_text(json.dumps(metadata, indent=2) + "\n")
    (folder / "SOURCE_COMMIT.txt").write_text(commit + "\n")
    (folder / "IMAGE_DIGEST.txt").write_text(reference + "\n")
    assets = (*sboms, "release.json", "SOURCE_COMMIT.txt", "IMAGE_DIGEST.txt")
    checksums = "".join(
        f"{hashlib.sha256((folder / name).read_bytes()).hexdigest()}  {name}\n"
        for name in assets
    )
    (folder / "SHA256SUMS").write_text(checksums)
    (folder / "release-notes.md").write_text(
        f"## Container image\n\n```sh\ndocker pull {reference}\n```\n\n"
        f"- Version tag: `{image}:{tag}`\n"
        f"- Plain version tag: `{image}:{tag[1:]}`\n"
        f"- Source commit: [`{commit}`](https://github.com/{repository}/commit/{commit})\n"
        f"- Image/index SHA-256: `{digest}`\n"
        f"- Platform: `linux/amd64`\n"
        f"- [Build run]({run_url})\n\n"
        "## Supply-chain information\n\n"
        "`image.sbom.spdx.json` inventories the published image scanned by digest. "
        "`frontend.sbom.spdx.json` inventories the frontend source/lockfile, including "
        "development dependencies; compiled JavaScript alone cannot expose its complete "
        "dependency inventory. BuildKit SBOM and maximum-mode provenance attestations "
        "are also attached to the image in GHCR.\n\n"
        "`release.json`, `SOURCE_COMMIT.txt` and `IMAGE_DIGEST.txt` record source and image "
        "identity. `SHA256SUMS` verifies all five attached inventory/metadata files "
        "(not itself):\n\n```sh\nsha256sum --check SHA256SUMS\n```\n"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("validate-tag").add_argument("tag")
    assets = commands.add_parser("assets")
    for field in ("tag", "commit", "image", "digest", "repository", "run-url", "output-dir"):
        assets.add_argument(f"--{field}", required=True)
    args = vars(parser.parse_args())
    command = args.pop("command")
    try:
        if command == "validate-tag":
            validate_tag(args["tag"])
        else:
            generate_assets(**args)
    except (ValueError, OSError) as exc:
        parser.exit(1, f"Release metadata error: {exc}\n")


if __name__ == "__main__":
    main()
