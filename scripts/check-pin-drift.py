#!/usr/bin/env python3
"""Report how far each repo's NuGet pins have drifted behind what is published.

Repos in this workspace consume each other as published packages rather than project
references, which is deliberate -- but it means nothing tells you a newer version shipped.
Drift is invisible until someone needs a fix badly enough to go looking, and by then it can
be several minor versions deep.

This turns that into a table. It is READ-ONLY: it never edits a props file, never restores,
and never touches a repo's git state. Point it at a directory of sibling repos.

    ./scripts/check-pin-drift.py                 # scan the parent of this repo
    ./scripts/check-pin-drift.py ~/repos         # scan somewhere else
    ./scripts/check-pin-drift.py --only PhoenixmlDb   # first-party packages only
    ./scripts/check-pin-drift.py --strict        # exit 1 if anything is behind

Exit codes: 0 clean (or informational), 1 drift found under --strict, 2 bad usage.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

NUGET_INDEX = "https://api.nuget.org/v3-flatcontainer/{package}/index.json"
TIMEOUT_SECONDS = 15

# A NuGet version is major.minor.patch[.revision][-prerelease]. Compare the numeric parts as
# integers -- string comparison puts "1.10.0" before "1.9.0", which is the whole reason a
# drift check gets written by hand rather than eyeballed.
VERSION_PART = re.compile(r"^(\d+(?:\.\d+)*)(?:-(.+))?$")

# <PackageVersion Include="X" Version="Y" />, attributes in either order, possibly wrapped.
PACKAGE_VERSION_ELEMENT = re.compile(r"<PackageVersion\b([^>]*?)/?>", re.DOTALL)


def attribute(body: str, name: str) -> str | None:
    """Read one double- or single-quoted attribute out of an element's attribute text."""
    match = re.search(rf"""\b{name}\s*=\s*(["'])(.*?)\1""", body, re.DOTALL)
    return match.group(2) if match else None


def parse_version(text: str) -> tuple[tuple[int, ...], bool]:
    """Return (numeric parts, is_stable). Unparseable versions sort last."""
    match = VERSION_PART.match(text.strip())
    if not match:
        return ((-1,), False)
    numbers = tuple(int(part) for part in match.group(1).split("."))
    return (numbers, match.group(2) is None)


def latest_stable(package: str) -> str | None:
    """Highest non-prerelease version on nuget.org, or None if unpublished/unreachable."""
    url = NUGET_INDEX.format(package=package.lower())
    try:
        with urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:
            versions = json.load(response).get("versions", [])
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, OSError):
        return None

    stable = [v for v in versions if parse_version(v)[1]]
    if not stable:
        return None
    return max(stable, key=lambda v: parse_version(v)[0])


def read_pins(props: Path) -> dict[str, str]:
    """Extract PackageVersion entries from a Directory.Packages.props.

    Deliberately not an XML parser. Python's ElementTree is not vulnerable to XXE (it does
    not resolve external entities) but it is vulnerable to entity-expansion attacks, and the
    hardened alternative, defusedxml, is a third-party dependency this script is more useful
    without -- it should run anywhere python3 does, with nothing installed.

    Regex-scraping XML is normally a mistake. It is defensible here because the target is a
    single element with two attributes in a machine-written file, and because the failure
    mode is benign: an unmatched file reports no pins rather than doing something surprising.
    """
    try:
        text = props.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return {}

    pins: dict[str, str] = {}
    for element in PACKAGE_VERSION_ELEMENT.finditer(text):
        body = element.group(1)
        name = attribute(body, "Include")
        version = attribute(body, "Version")
        if name and version:
            pins[name] = version
    return pins


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("root", nargs="?", help="directory holding the repos (default: this repo's parent)")
    parser.add_argument("--only", metavar="PREFIX", help="only report packages whose name starts with PREFIX")
    parser.add_argument("--strict", action="store_true", help="exit 1 if any pin is behind")
    args = parser.parse_args()

    root = Path(args.root).expanduser() if args.root else Path(__file__).resolve().parents[2]
    if not root.is_dir():
        print(f"not a directory: {root}", file=sys.stderr)
        return 2

    found = sorted(
        (path for path in root.glob("*/Directory.Packages.props")),
        key=lambda p: p.parent.name,
    )
    if not found:
        print(f"no Directory.Packages.props under {root}", file=sys.stderr)
        return 2

    by_repo = {props.parent.name: read_pins(props) for props in found}
    packages = {
        name
        for pins in by_repo.values()
        for name in pins
        if not args.only or name.lower().startswith(args.only.lower())
    }
    if not packages:
        print("no packages matched", file=sys.stderr)
        return 2

    # One request per distinct package, not per pin.
    with ThreadPoolExecutor(max_workers=8) as pool:
        published = dict(zip(sorted(packages), pool.map(latest_stable, sorted(packages))))

    rows: list[tuple[str, str, str, str, str]] = []
    behind = 0
    for repo, pins in by_repo.items():
        for name in sorted(pins):
            if name not in packages:
                continue
            pinned, current = pins[name], published.get(name)
            if current is None:
                state = "unpublished"
            elif pinned == current:
                state = "current"
            else:
                state = "BEHIND" if parse_version(pinned)[0] < parse_version(current)[0] else "ahead"
                if state == "BEHIND":
                    behind += 1
            rows.append((repo, name, pinned, current or "-", state))

    widths = [max(len(row[i]) for row in rows) for i in range(5)]
    header = ("repo", "package", "pinned", "published", "state")
    widths = [max(w, len(h)) for w, h in zip(widths, header)]

    def line(cells: tuple[str, ...]) -> str:
        return "  ".join(cell.ljust(width) for cell, width in zip(cells, widths)).rstrip()

    print(line(header))
    print("  ".join("-" * width for width in widths))
    for row in rows:
        print(line(row))

    print()
    if behind:
        print(f"{behind} pin(s) behind. Drift is silent, so it is worth a decision rather than a shrug:")
        print("  bump it, or record why staying behind is deliberate.")
    else:
        print("No pins behind.")

    return 1 if behind and args.strict else 0


if __name__ == "__main__":
    sys.exit(main())
