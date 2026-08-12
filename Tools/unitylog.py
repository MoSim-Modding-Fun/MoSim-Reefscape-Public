#!/usr/bin/env python3
"""unitylog - pull the signal out of Unity's Editor.log / Player.log.

The logs are tens of thousands of lines of shader compilation, asset imports
and memory statistics. This extracts compile errors and exceptions, collapses
repeats, and points at the project source line.

    python Tools/unitylog.py                 # summary of the whole log
    python Tools/unitylog.py --new           # only what appeared since last run
    python Tools/unitylog.py --player        # the built game's log instead
    python Tools/unitylog.py --grep Steam    # lines matching a regex
    python Tools/unitylog.py --tail 40       # raw tail, for when parsing misses

`--new` is the one to use in a change-test loop: run it, press Play in Unity,
run it again, and you get exactly the exceptions that run produced.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import sys

EXC_RE = re.compile(r"^(?P<type>[A-Za-z_][\w.`+]*(?:Exception|Error))\s*:\s*(?P<msg>.*)$")
FRAME_RE = re.compile(r"^\s+at\s+(?P<sym>.+?)\s*(?:\[0x[0-9a-f]+\]\s*)?in\s+(?P<file>.+?):(?P<line>\d+)\s*$")
BARE_FRAME_RE = re.compile(r"^\s+at\s+(?P<sym>.+)$")
FILENAME_RE = re.compile(r"^\(Filename:\s*(?P<file>.*?)\s+Line:\s*(?P<line>-?\d+)\)")
CS_ERR_RE = re.compile(r"^(?P<file>[^(]+\.cs)\((?P<line>\d+),(?P<col>\d+)\):\s*error\s+(?P<code>\w+):\s*(?P<msg>.*)$")
COMPILE_FAIL_RE = re.compile(r"(Compilation failed|Scripts have compiler errors|error CS\d+)")

STATE_REL = os.path.join("Library", "unitylog-state.json")


def find_project(start: str | None = None) -> str:
    cur = os.path.abspath(start or os.getcwd())
    while True:
        if os.path.isdir(os.path.join(cur, "Assets")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            raise SystemExit("error: no Unity project (Assets/ dir) found")
        cur = parent


def project_names(project: str) -> tuple[str, str]:
    company = product = ""
    path = os.path.join(project, "ProjectSettings", "ProjectSettings.asset")
    try:
        with open(path, "r", encoding="utf-8", errors="ignore") as fh:
            for line in fh:
                s = line.strip()
                if s.startswith("companyName:"):
                    company = s.split(":", 1)[1].strip()
                elif s.startswith("productName:"):
                    product = s.split(":", 1)[1].strip()
                if company and product:
                    break
    except OSError:
        pass
    return company, product


def editor_log() -> str | None:
    system = platform.system()
    if system == "Windows":
        p = os.path.expandvars(r"%LOCALAPPDATA%\Unity\Editor\Editor.log")
    elif system == "Darwin":
        p = os.path.expanduser("~/Library/Logs/Unity/Editor.log")
    else:
        p = os.path.expanduser("~/.config/unity3d/Editor.log")
    return p if os.path.isfile(p) else None


def player_log(project: str) -> str | None:
    company, product = project_names(project)
    system = platform.system()
    if system == "Windows":
        p = os.path.expandvars(rf"%USERPROFILE%\AppData\LocalLow\{company}\{product}\Player.log")
    elif system == "Darwin":
        p = os.path.expanduser(f"~/Library/Logs/{company}/{product}/Player.log")
    else:
        p = os.path.expanduser(f"~/.config/unity3d/{company}/{product}/Player.log")
    return p if os.path.isfile(p) else None


# --------------------------------------------------------------- reading

def read_from(path: str, offset: int) -> tuple[list[str], int]:
    size = os.path.getsize(path)
    if offset > size:          # log was rotated / truncated by a new Unity session
        offset = 0
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        fh.seek(offset)
        text = fh.read()
    return text.splitlines(), size


def load_state(project: str) -> dict:
    try:
        with open(os.path.join(project, STATE_REL), encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


def save_state(project: str, state: dict) -> None:
    path = os.path.join(project, STATE_REL)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(state, fh)


# --------------------------------------------------------------- parsing

def in_project(path: str) -> bool:
    p = path.replace("\\", "/").lower()
    return "/assets/" in p or p.startswith("assets/")


def parse(lines: list[str], project: str, max_frames: int) -> dict:
    exceptions: dict[tuple, dict] = {}
    compile_errors: dict[tuple, dict] = {}
    i, n = 0, len(lines)
    while i < n:
        line = lines[i]

        m = CS_ERR_RE.match(line.strip())
        if m:
            key = (m.group("file"), m.group("line"), m.group("code"))
            rec = compile_errors.setdefault(key, {
                "file": m.group("file").replace("\\", "/"), "line": int(m.group("line")),
                "code": m.group("code"), "message": m.group("msg"), "count": 0})
            rec["count"] += 1
            i += 1
            continue

        m = EXC_RE.match(line)
        if m:
            frames, site = [], None
            j = i + 1
            while j < n:
                nxt = lines[j]
                f = FRAME_RE.match(nxt)
                if f:
                    frames.append({"symbol": f.group("sym").strip(),
                                   "file": f.group("file").replace("\\", "/"),
                                   "line": int(f.group("line"))})
                elif BARE_FRAME_RE.match(nxt):
                    frames.append({"symbol": BARE_FRAME_RE.match(nxt).group("sym").strip(),
                                   "file": "", "line": 0})
                elif FILENAME_RE.match(nxt.strip()):
                    fm = FILENAME_RE.match(nxt.strip())
                    if fm.group("file"):
                        site = f"{fm.group('file')}:{fm.group('line')}"
                    break
                elif nxt.strip() == "":
                    j += 1
                    continue
                else:
                    break
                j += 1

            own = next((f for f in frames if f["file"] and in_project(f["file"])), None)
            if own and not site:
                try:
                    site = f"{os.path.relpath(own['file'], project)}:{own['line']}".replace("\\", "/")
                except ValueError:
                    site = f"{own['file']}:{own['line']}"
            key = (m.group("type"), m.group("msg"), site or "")
            rec = exceptions.setdefault(key, {
                "type": m.group("type"), "message": m.group("msg"),
                "site": site or "<no project frame>", "count": 0,
                "stack": [f"{f['symbol']}"
                          + (f"  ({os.path.basename(f['file'])}:{f['line']})" if f["file"] else "")
                          for f in frames[:max_frames]]})
            rec["count"] += 1
            i = max(j, i + 1)
            continue
        i += 1

    return {"exceptions": sorted(exceptions.values(), key=lambda r: -r["count"]),
            "compile_errors": sorted(compile_errors.values(), key=lambda r: (r["file"], r["line"]))}


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="unitylog", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", help="Unity project root")
    p.add_argument("--player", action="store_true", help="read the built game's Player.log")
    p.add_argument("--file", help="an explicit log file to read")
    p.add_argument("--new", action="store_true",
                   help="only content appended since the last --new run")
    p.add_argument("--reset", action="store_true", help="forget the --new position")
    p.add_argument("--tail", type=int, help="print the last N raw lines instead of parsing")
    p.add_argument("--grep", help="print lines matching a regex (with 1 line of context)")
    p.add_argument("--frames", type=int, default=4, help="stack frames per exception")
    p.add_argument("--max", type=int, default=15, help="max distinct issues printed")
    p.add_argument("--json", action="store_true")
    args = p.parse_args(argv)

    project = os.path.abspath(args.root) if args.root else find_project()
    path = args.file or (player_log(project) if args.player else editor_log())
    if not path:
        what = "Player.log" if args.player else "Editor.log"
        print(f"no {what} found"
              + ("  (has the game been built and run on this machine?)" if args.player else ""))
        return 2

    state = load_state(project)
    key = "player" if args.player else "editor"
    if args.reset:
        state.pop(key, None)
        save_state(project, state)
    offset = state.get(key, 0) if args.new else 0
    lines, size = read_from(path, offset)
    if args.new:
        state[key] = size
        save_state(project, state)

    header = (f"{path}\n{len(lines)} line(s)"
              + (f" since last check (offset {offset})" if args.new else " total"))

    if args.tail:
        out = lines[-args.tail:]
        if args.json:
            json.dump({"log": path, "lines": out}, sys.stdout, indent=1)
            print()
        else:
            print(header)
            print("\n".join(out))
        return 0

    if args.grep:
        rx = re.compile(args.grep, re.I)
        hits = [f"{i+1}: {l}" for i, l in enumerate(lines) if rx.search(l)]
        if args.json:
            json.dump({"log": path, "matches": hits}, sys.stdout, indent=1)
            print()
        else:
            print(header)
            print("\n".join(hits[-args.max * 4:]) or "no match")
        return 0

    data = parse(lines, project, args.frames)
    data["log"] = path
    data["lines_scanned"] = len(lines)

    if args.json:
        json.dump(data, sys.stdout, indent=1)
        print()
        return 1 if (data["exceptions"] or data["compile_errors"]) else 0

    print(header)
    if data["compile_errors"]:
        print(f"\ncompile errors ({len(data['compile_errors'])}):")
        for e in data["compile_errors"][: args.max]:
            print(f"  {e['file']}:{e['line']}  {e['code']}: {e['message']}")
    if data["exceptions"]:
        print(f"\nexceptions ({len(data['exceptions'])} distinct):")
        for e in data["exceptions"][: args.max]:
            print(f"  x{e['count']}  {e['type']}: {e['message']}")
            print(f"        at {e['site']}")
            for f in e["stack"]:
                print(f"           {f}")
    if not data["compile_errors"] and not data["exceptions"]:
        print("\nno compile errors or exceptions")
    return 1 if (data["exceptions"] or data["compile_errors"]) else 0


if __name__ == "__main__":
    sys.exit(main())
