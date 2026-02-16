#!/usr/bin/env python3
"""
Validate local markdown links/images and in-file heading anchors.

Checks:
- Relative file links exist.
- Relative image paths exist.
- Markdown fragment links map to existing headings.

Skips external links (http/https/mailto/tel/data).
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from urllib.parse import unquote


LINK_PATTERN = re.compile(r"(?<!!)\[[^\]]*\]\(([^)]+)\)")
IMAGE_PATTERN = re.compile(r"!\[[^\]]*\]\(([^)]+)\)")
HEADING_PATTERN = re.compile(r"^(#{1,6})\s+(.+?)\s*$")

EXTERNAL_PREFIXES = ("http://", "https://", "mailto:", "tel:", "data:")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Check local markdown links and image references.")
    parser.add_argument(
        "targets",
        nargs="*",
        default=["README.md", "docs"],
        help="Markdown files and/or directories to scan (default: README.md docs)",
    )
    return parser.parse_args()


def collect_markdown_files(targets: list[str]) -> list[Path]:
    files: list[Path] = []
    for raw in targets:
        path = Path(raw)
        if path.is_file() and path.suffix.lower() == ".md":
            files.append(path)
            continue
        if path.is_dir():
            files.extend(sorted(path.rglob("*.md")))
    deduped = sorted({f.resolve() for f in files})
    return [Path(p) for p in deduped]


def is_external_link(target: str) -> bool:
    target = target.strip().lower()
    return target.startswith(EXTERNAL_PREFIXES)


def clean_link_target(raw_target: str) -> str:
    target = raw_target.strip()
    if target.startswith("<") and target.endswith(">"):
        target = target[1:-1]

    # Drop optional markdown title: path "title"
    if ' "' in target:
        candidate, maybe_title = target.split(' "', 1)
        if maybe_title.endswith('"'):
            target = candidate

    return target


def build_anchor_map(md_file: Path) -> set[str]:
    anchors: set[str] = set()
    anchor_counts: dict[str, int] = {}
    in_fenced_code = False

    for line in md_file.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if stripped.startswith("```"):
            in_fenced_code = not in_fenced_code
            continue
        if in_fenced_code:
            continue

        match = HEADING_PATTERN.match(line)
        if not match:
            continue

        heading = match.group(2).strip().lower()
        heading = re.sub(r"[^\w\s-]", "", heading, flags=re.UNICODE)
        heading = heading.replace("_", "")
        anchor = re.sub(r"\s+", "-", heading).strip("-")
        anchor = re.sub(r"-{2,}", "-", anchor)
        if not anchor:
            continue

        count = anchor_counts.get(anchor, 0)
        if count == 0:
            anchors.add(anchor)
        else:
            anchors.add(f"{anchor}-{count}")
        anchor_counts[anchor] = count + 1

    return anchors


def extract_targets(pattern: re.Pattern[str], content: str) -> list[str]:
    return [clean_link_target(match.group(1)) for match in pattern.finditer(content)]


def strip_fenced_code_blocks(content: str) -> str:
    lines = []
    in_fenced_code = False
    for line in content.splitlines():
        stripped = line.strip()
        if stripped.startswith("```"):
            in_fenced_code = not in_fenced_code
            continue
        if not in_fenced_code:
            lines.append(line)
    return "\n".join(lines)


def split_fragment(target: str) -> tuple[str, str | None]:
    if "#" not in target:
        return target, None
    path_part, fragment = target.split("#", 1)
    return path_part, fragment.strip()


def resolve_target(current_file: Path, path_part: str) -> Path:
    decoded = unquote(path_part)
    return (current_file.parent / decoded).resolve()


def validate_markdown_file(md_file: Path, anchor_cache: dict[Path, set[str]]) -> list[str]:
    errors: list[str] = []
    content = strip_fenced_code_blocks(md_file.read_text(encoding="utf-8"))
    refs = extract_targets(LINK_PATTERN, content) + extract_targets(IMAGE_PATTERN, content)

    for ref in refs:
        if not ref or is_external_link(ref):
            continue

        path_part, fragment = split_fragment(ref)

        if path_part == "":
            target_path = md_file.resolve()
        else:
            target_path = resolve_target(md_file, path_part)
            if not target_path.exists():
                errors.append(f"{md_file}: missing path '{ref}'")
                continue

        if fragment:
            if target_path.suffix.lower() != ".md":
                continue

            anchors = anchor_cache.get(target_path)
            if anchors is None:
                anchors = build_anchor_map(target_path)
                anchor_cache[target_path] = anchors

            fragment_key = unquote(fragment).strip().lower()
            if fragment_key not in anchors:
                errors.append(f"{md_file}: missing anchor '#{fragment}' in '{target_path}'")

    return errors


def main() -> int:
    args = parse_args()
    md_files = collect_markdown_files(args.targets)
    if not md_files:
        print("No markdown files found.")
        return 0

    anchor_cache: dict[Path, set[str]] = {}
    all_errors: list[str] = []

    for md_file in md_files:
        anchor_cache[md_file.resolve()] = build_anchor_map(md_file.resolve())

    for md_file in md_files:
        all_errors.extend(validate_markdown_file(md_file.resolve(), anchor_cache))

    if all_errors:
        print("Markdown link check failed:")
        for error in all_errors:
            print(f"- {error}")
        return 1

    print(f"Markdown link check passed ({len(md_files)} files).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
