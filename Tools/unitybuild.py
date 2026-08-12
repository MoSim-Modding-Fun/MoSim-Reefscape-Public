#!/usr/bin/env python3
"""unitybuild - compile-check a Unity project's C# without opening Unity.

Builds the Unity-generated .sln with `dotnet build` and prints only what
matters: errors, grouped and de-duplicated. MSBuild's raw output is a few
hundred lines of warnings and per-project banners; this is usually 1 line.

    python Tools/unitybuild.py                  # errors only
    python Tools/unitybuild.py --warnings       # include warnings
    python Tools/unitybuild.py --project Assembly-CSharp.csproj
    python Tools/unitybuild.py --json

Staleness: Unity generates the .csproj files, so a .cs file added since the
last Unity refresh is in no project and will NOT be compiled. This checks for
that and says so instead of reporting a misleading clean build.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

# path(line,col): error CS0103: The name 'x' does not exist [C:\...\Foo.csproj]
DIAG_RE = re.compile(
    r"^(?P<file>[^(]+)\((?P<line>\d+),(?P<col>\d+)\):\s+"
    r"(?P<sev>error|warning)\s+(?P<code>\w+):\s+(?P<msg>.*?)"
    r"(?:\s+\[(?P<proj>[^\]]+)\])?$"
)
# Errors without a file (missing reference, bad project) still matter.
BARE_RE = re.compile(r"^(?:MSBUILD|.*?)\s*:\s*(?P<sev>error)\s+(?P<code>\w+):\s+(?P<msg>.+)$")

SKIP_DIRS = {"Library", "Temp", "obj", "bin", ".git"}


def find_project(start: str | None = None) -> str:
    cur = os.path.abspath(start or os.getcwd())
    while True:
        if os.path.isdir(os.path.join(cur, "Assets")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            raise SystemExit("error: no Unity project (Assets/ dir) found")
        cur = parent


def find_solution(project: str) -> str | None:
    slns = [f for f in os.listdir(project) if f.endswith(".sln")]
    return os.path.join(project, slns[0]) if slns else None


# ------------------------------------------------------------- staleness

def compiled_files(project: str) -> set[str]:
    """Every .cs listed in any generated .csproj, normalised to lowercase rel paths."""
    out: set[str] = set()
    for name in os.listdir(project):
        if not name.endswith(".csproj"):
            continue
        try:
            root = ET.parse(os.path.join(project, name)).getroot()
        except ET.ParseError:
            continue
        for el in root.iter():
            if el.tag.endswith("Compile"):
                inc = el.get("Include")
                if inc and inc.lower().endswith(".cs"):
                    out.add(inc.replace("\\", "/").lower())
    return out


def disk_files(project: str) -> set[str]:
    out: set[str] = set()
    for root_dir in ("Assets", "Packages"):
        base = os.path.join(project, root_dir)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for f in filenames:
                if f.endswith(".cs"):
                    rel = os.path.relpath(os.path.join(dirpath, f), project)
                    out.add(rel.replace("\\", "/").lower())
    return out


def staleness(project: str) -> list[str]:
    """.cs files on disk that no generated project compiles."""
    return sorted(disk_files(project) - compiled_files(project))


# ----------------------------------------------------------------- build

def run_build(target: str, project: str, extra: list[str]) -> tuple[int, list[str]]:
    cmd = ["dotnet", "build", target, "-v", "q", "--nologo",
           "-consoleloggerparameters:NoSummary"] + extra
    proc = subprocess.run(cmd, cwd=project, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")
    return proc.returncode, (proc.stdout + proc.stderr).splitlines()


def parse(lines: list[str], project: str) -> tuple[list[dict], list[dict]]:
    errors: dict[tuple, dict] = {}
    warnings: dict[tuple, dict] = {}
    for raw in lines:
        line = raw.strip()
        if not line:
            continue
        m = DIAG_RE.match(line)
        if m:
            f = m.group("file").strip()
            try:
                f = os.path.relpath(f, project).replace("\\", "/")
            except ValueError:
                pass
            # the same file compiles into several assemblies; report once
            key = (f, m.group("line"), m.group("code"), m.group("msg"))
            rec = {"file": f, "line": int(m.group("line")), "col": int(m.group("col")),
                   "code": m.group("code"), "message": m.group("msg")}
            (errors if m.group("sev") == "error" else warnings)[key] = rec
            continue
        b = BARE_RE.match(line)
        if b and "error" in line:
            key = (b.group("code"), b.group("msg"))
            errors[key] = {"file": "", "line": 0, "col": 0,
                           "code": b.group("code"), "message": b.group("msg")}
    return list(errors.values()), list(warnings.values())


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="unitybuild", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--project", help="a single .csproj to build (default: the .sln)")
    p.add_argument("--root", help="Unity project root")
    p.add_argument("--warnings", action="store_true", help="also print warnings")
    p.add_argument("--max", type=int, default=40, help="max diagnostics printed")
    p.add_argument("--no-stale-check", action="store_true")
    p.add_argument("--json", action="store_true")
    p.add_argument("rest", nargs=argparse.REMAINDER, help="extra args passed to dotnet build")
    args = p.parse_args(argv)

    root = os.path.abspath(args.root) if args.root else find_project()
    target = args.project or find_solution(root)
    if not target:
        raise SystemExit("error: no .sln in the project root; pass --project Foo.csproj")

    code, lines = run_build(target, root, [a for a in args.rest if a != "--"])
    errors, warnings = parse(lines, root)
    errors.sort(key=lambda d: (d["file"], d["line"]))
    warnings.sort(key=lambda d: (d["file"], d["line"]))
    stale = [] if args.no_stale_check else staleness(root)

    result = {
        "target": os.path.basename(target),
        "ok": code == 0 and not errors,
        "errors": errors,
        "error_count": len(errors),
        "warning_count": len(warnings),
        "uncompiled_files": stale,
    }
    if args.warnings:
        result["warnings"] = warnings

    if args.json:
        json.dump(result, sys.stdout, indent=1)
        print()
        return 1 if errors else 0

    def show(items, label):
        for d in items[: args.max]:
            loc = f"{d['file']}:{d['line']}:{d['col']}" if d["file"] else "(build)"
            print(f"  {loc}  {d['code']}: {d['message']}")
        if len(items) > args.max:
            print(f"  ... {len(items) - args.max} more {label}")

    if errors:
        print(f"FAILED  {result['target']}  {len(errors)} error(s), {len(warnings)} warning(s)")
        show(errors, "errors")
    else:
        print(f"OK  {result['target']}  0 errors, {len(warnings)} warning(s)")
    if args.warnings and warnings:
        print("warnings:")
        show(warnings, "warnings")
    if stale:
        print(f"WARNING: {len(stale)} .cs file(s) are in no .csproj — Unity has not "
              f"regenerated project files, so these were NOT compiled:")
        for f in stale[:10]:
            print(f"  {f}")
        if len(stale) > 10:
            print(f"  ... {len(stale) - 10} more")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
