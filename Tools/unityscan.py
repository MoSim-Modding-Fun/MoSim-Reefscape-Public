#!/usr/bin/env python3
"""unityscan - low-token inspection of Unity scene/prefab YAML.

Parses Unity's serialized YAML directly (no Unity, no PyYAML) and prints
compact summaries instead of dumping megabytes of text into a context window.

Quick start:
    python Tools/unityscan.py index                 # build guid -> asset map
    python Tools/unityscan.py info Reefscape.unity
    python Tools/unityscan.py tree Arena.prefab --depth 3
    python Tools/unityscan.py find "Turret.*"
    python Tools/unityscan.py scripts Robot.prefab --name Drivetrain
    python Tools/unityscan.py usage InteractionRoller
    python Tools/unityscan.py deps Reefscape.unity
    python Tools/unityscan.py refs Assets/Prefabs/Robot/Robot.prefab
    python Tools/unityscan.py obj Robot.prefab 17205343
    python Tools/unityscan.py set Robot.prefab rpm=6000 --name InteractionRoller --write

Every command takes --json for machine-readable output.

`set` is the only command that writes. It is a dry run unless --write is
passed, it refuses unknown fields rather than adding them, and it rewrites
single lines in place so the diff stays reviewable.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import struct
import sys
from dataclasses import dataclass, field
from typing import Iterable, Iterator

# ---------------------------------------------------------------- constants

BLOCK_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)(?:\s+(stripped))?\s*$")
REF_RE = re.compile(r"\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-f]{32}))?(?:,\s*type:\s*(\d+))?\s*\}")
GUID_RE = re.compile(r"guid:\s*([0-9a-f]{32})")
MOD_RE = re.compile(
    r"-\s+target:\s*\{(?P<target>[^}]*)\}\s+"
    r"propertyPath:\s*(?P<path>.*?)\s+"
    r"value:\s*(?P<value>.*?)\s+"
    r"objectReference:\s*\{(?P<objref>[^}]*)\}",
    re.S,
)
CLASS_RE = re.compile(r"^\s*(?:public|internal|sealed|abstract|partial|static|\s)*\s"
                      r"(?:class|struct)\s+(\w+)", re.M)

TRANSFORM_CLASSES = {"4", "224"}          # Transform, RectTransform
GAMEOBJECT_CLASS = "1"
PREFAB_INSTANCE_CLASS = "1001"
MONOBEHAVIOUR_CLASS = "114"

ASSET_EXTS = (".unity", ".prefab", ".asset", ".controller", ".mat")
CACHE_REL = os.path.join("Library", "unityscan-index.json")

# Unity's built-in resources (default resources, built-in extra) use guids of
# 16 zeros + a marker; they have no asset file and are never broken.
BUILTIN_GUID_RE = re.compile(r"^0{16}[0-9a-f]{16}$")
# Unity itself ignores these when generating .meta files.
META_EXEMPT = (".", "~")
META_EXEMPT_EXT = (".tmp", ".bak")


# ------------------------------------------------------------------ project

def find_project(start: str | None = None) -> str:
    """Walk up from `start` until a directory containing Assets/ is found."""
    cur = os.path.abspath(start or os.getcwd())
    while True:
        if os.path.isdir(os.path.join(cur, "Assets")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            raise SystemExit("error: no Unity project (Assets/ dir) found above " + (start or os.getcwd()))
        cur = parent


class Index:
    """guid -> asset path, plus script guid -> class name."""

    def __init__(self, project: str):
        self.project = project
        self.guid_to_path: dict[str, str] = {}
        self.path_to_guid: dict[str, str] = {}
        self.guid_to_class: dict[str, str] = {}
        self.class_to_guid: dict[str, str] = {}

    # -- persistence ------------------------------------------------------
    @property
    def cache_path(self) -> str:
        return os.path.join(self.project, CACHE_REL)

    def load(self) -> bool:
        try:
            with open(self.cache_path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
        except (OSError, ValueError):
            return False
        self.guid_to_path = data.get("guid_to_path", {})
        self.guid_to_class = data.get("guid_to_class", {})
        self._derive()
        return bool(self.guid_to_path)

    def save(self) -> None:
        os.makedirs(os.path.dirname(self.cache_path), exist_ok=True)
        with open(self.cache_path, "w", encoding="utf-8") as fh:
            json.dump({"guid_to_path": self.guid_to_path,
                       "guid_to_class": self.guid_to_class}, fh)

    def _derive(self) -> None:
        self.path_to_guid = {p: g for g, p in self.guid_to_path.items()}
        self.class_to_guid = {c: g for g, c in self.guid_to_class.items()}

    # -- build ------------------------------------------------------------
    def build(self) -> "Index":
        self.guid_to_path.clear()
        self.guid_to_class.clear()
        # PackageCache holds the sources of registry packages (TMP, uGUI, ...);
        # without it their script guids resolve to nothing.
        roots = [os.path.join(self.project, d) for d in
                 ("Assets", "Packages", os.path.join("Library", "PackageCache"))]
        for root in filter(os.path.isdir, roots):
            for dirpath, dirnames, filenames in os.walk(root):
                dirnames[:] = [d for d in dirnames if d not in ("Temp", "obj", ".git")]
                for name in filenames:
                    if not name.endswith(".meta"):
                        continue
                    meta = os.path.join(dirpath, name)
                    guid = self._read_guid(meta)
                    if not guid:
                        continue
                    asset = meta[:-5]
                    rel = os.path.relpath(asset, self.project).replace("\\", "/")
                    self.guid_to_path[guid] = rel
                    if asset.endswith(".cs"):
                        cls = self._read_class(asset, name[:-8])
                        if cls:
                            self.guid_to_class[guid] = cls
        self._derive()
        return self

    @staticmethod
    def _read_guid(meta: str) -> str | None:
        try:
            with open(meta, "r", encoding="utf-8", errors="ignore") as fh:
                for _ in range(6):
                    line = fh.readline()
                    if not line:
                        break
                    m = GUID_RE.match(line.strip())
                    if m:
                        return m.group(1)
        except OSError:
            pass
        return None

    @staticmethod
    def _read_class(cs_path: str, stem: str) -> str | None:
        """Prefer the class matching the file name (Unity's own requirement)."""
        try:
            with open(cs_path, "r", encoding="utf-8", errors="ignore") as fh:
                text = fh.read(200_000)
        except OSError:
            return stem
        names = CLASS_RE.findall(text)
        if stem in names:
            return stem
        return names[0] if names else stem

    # -- lookups ----------------------------------------------------------
    def path_of(self, guid: str) -> str:
        return self.guid_to_path.get(guid, f"<guid {guid[:8]}…>")

    def script_name(self, guid: str) -> str:
        if guid in self.guid_to_class:
            return self.guid_to_class[guid]
        p = self.guid_to_path.get(guid)
        if p:
            return os.path.splitext(os.path.basename(p))[0]
        return f"<script {guid[:8]}…>"

    def resolve_asset(self, needle: str) -> str:
        """Accept a full path, a project-relative path, a suffix, or a basename."""
        if os.path.isfile(needle):
            return os.path.abspath(needle)
        direct = os.path.join(self.project, needle)
        if os.path.isfile(direct):
            return os.path.abspath(direct)
        n = needle.replace("\\", "/").lower()
        hits = [p for p in self.guid_to_path.values()
                if p.lower().endswith(n) or os.path.basename(p).lower() == n
                or fnmatch.fnmatch(p.lower(), n)]
        hits = [h for h in hits if h.endswith(ASSET_EXTS)] or hits
        if not hits:
            raise SystemExit(f"error: no asset matching {needle!r} (try `index --rebuild`)")
        if len(hits) > 1:
            exact = [h for h in hits if os.path.basename(h).lower() == n]
            if len(exact) == 1:
                hits = exact
            else:
                msg = "\n  ".join(sorted(hits)[:15])
                raise SystemExit(f"error: {needle!r} is ambiguous:\n  {msg}")
        return os.path.join(self.project, hits[0])


def get_index(project: str, rebuild: bool = False) -> Index:
    idx = Index(project)
    if rebuild or not idx.load():
        idx.build().save()
    return idx


# ------------------------------------------------------------------- parser

@dataclass
class Block:
    class_id: str
    file_id: str
    stripped: bool
    type_name: str
    lines: list[str] = field(default_factory=list)

    @property
    def text(self) -> str:
        return "".join(self.lines)

    def value(self, key: str) -> str | None:
        prefix = f"  {key}:"
        for line in self.lines:
            if line.startswith(prefix):
                return line[len(prefix):].strip()
        return None

    def ref(self, key: str) -> tuple[str, str | None] | None:
        raw = self.value(key)
        if not raw:
            return None
        m = REF_RE.search(raw)
        return (m.group(1), m.group(2)) if m else None

    def ref_list(self, key: str) -> list[str]:
        """fileIDs from a YAML sequence like m_Component / m_Children."""
        out: list[str] = []
        collecting = False
        for line in self.lines:
            if line.startswith(f"  {key}:"):
                collecting = True
                out += [m.group(1) for m in REF_RE.finditer(line)]
                continue
            if collecting:
                if line.startswith("  ") and not line.startswith("   ") and line.strip().endswith(":"):
                    break
                if re.match(r"^  \w", line):
                    break
                out += [m.group(1) for m in REF_RE.finditer(line)]
        return out


def iter_blocks(path: str) -> Iterator[Block]:
    with open(path, "r", encoding="utf-8", errors="ignore") as fh:
        cur: Block | None = None
        for line in fh:
            m = BLOCK_RE.match(line)
            if m:
                if cur:
                    yield cur
                cur = Block(m.group(1), m.group(2), bool(m.group(3)), "")
                continue
            if cur is None:
                continue
            if not cur.type_name and line and not line.startswith((" ", "\t")) and line.rstrip().endswith(":"):
                cur.type_name = line.rstrip().rstrip(":")
                continue
            cur.lines.append(line)
        if cur:
            yield cur


SECURE_FLOAT_RE = re.compile(r"_obfuscationKey:\s*(-?\d+)\s+_encryptedValue:\s*(-?\d+)")


def decode_secure_float(val: str) -> str:
    """MoSimLib.SecureFloat stores XOR-obfuscated float bits; show the real value.

    See Assets/Scripts/MoSimLib/SecureFloat.cs — value = bits(encrypted ^ key).
    """
    m = SECURE_FLOAT_RE.search(val)
    if not m:
        return val
    key, enc = int(m.group(1)), int(m.group(2))
    bits = (enc ^ key) & 0xFFFFFFFF
    real = struct.unpack("<f", struct.pack("<I", bits))[0]
    return f"{real:g}  (SecureFloat key={key} enc={enc})"


@dataclass
class Modification:
    target: str
    guid: str | None
    path: str
    value: str
    objref: str | None


@dataclass
class Doc:
    """A parsed scene or prefab."""
    path: str
    blocks: dict[str, Block]
    idx: Index

    # ---- constructors ---------------------------------------------------
    @classmethod
    def load(cls, path: str, idx: Index) -> "Doc":
        return cls(path, {b.file_id: b for b in iter_blocks(path)}, idx)

    # ---- categorised views ---------------------------------------------
    def of_class(self, class_id: str) -> list[Block]:
        return [b for b in self.blocks.values() if b.class_id == class_id]

    def game_objects(self) -> list[Block]:
        return self.of_class(GAMEOBJECT_CLASS)

    def prefab_instances(self) -> list[Block]:
        return self.of_class(PREFAB_INSTANCE_CLASS)

    def component_label(self, b: Block) -> str:
        if b.class_id == MONOBEHAVIOUR_CLASS:
            r = b.ref("m_Script")
            return self.idx.script_name(r[1]) + "*" if r and r[1] else "MonoBehaviour*"
        return b.type_name or f"class{b.class_id}"

    def go_name(self, b: Block) -> str:
        return (b.value("m_Name") or "").strip('"') or "<unnamed>"

    def components_of(self, go: Block) -> list[Block]:
        return [self.blocks[fid] for fid in go.ref_list("m_Component") if fid in self.blocks]

    # ---- prefab instances ------------------------------------------------
    def modifications(self, pi: Block) -> list[Modification]:
        out = []
        for m in MOD_RE.finditer(pi.text):
            t = REF_RE.search("{" + m.group("target") + "}")
            o = REF_RE.search("{" + m.group("objref") + "}")
            out.append(Modification(
                target=t.group(1) if t else "0",
                guid=t.group(2) if t else None,
                path=m.group("path").strip(),
                value=m.group("value").strip(),
                objref=(self.idx.path_of(o.group(2)) if o and o.group(2) else None),
            ))
        return out

    def pi_source(self, pi: Block) -> str:
        r = pi.ref("m_SourcePrefab")
        if not r:
            m = re.search(r"m_SourcePrefab:.*?guid:\s*([0-9a-f]{32})", pi.text, re.S)
            return self.idx.path_of(m.group(1)) if m else "<unknown prefab>"
        return self.idx.path_of(r[1]) if r[1] else "<unknown prefab>"

    def pi_name(self, pi: Block) -> str:
        for mod in self.modifications(pi):
            if mod.path == "m_Name":
                return mod.value.strip('"')
        return os.path.splitext(os.path.basename(self.pi_source(pi)))[0]

    def pi_parent(self, pi: Block) -> str:
        m = re.search(r"m_TransformParent:\s*\{fileID:\s*(-?\d+)", pi.text)
        return m.group(1) if m else "0"

    # ---- hierarchy -------------------------------------------------------
    def hierarchy(self) -> tuple[list[tuple[str, str]], dict[str, list[tuple[str, str]]]]:
        """Return (roots, children) as lists of (kind, id); kind is 'go' or 'pi'.

        Transform fileIDs are the graph edges; each transform is mapped to the
        GameObject it belongs to, and stripped transforms to their PrefabInstance.
        """
        tf_owner: dict[str, tuple[str, str]] = {}
        for b in self.blocks.values():
            if b.class_id not in TRANSFORM_CLASSES:
                continue
            if b.stripped:
                pi = b.ref("m_PrefabInstance")
                if pi:
                    tf_owner[b.file_id] = ("pi", pi[0])
            else:
                go = b.ref("m_GameObject")
                if go:
                    tf_owner[b.file_id] = ("go", go[0])

        children: dict[str, list[tuple[str, str]]] = {}
        roots: list[tuple[str, str]] = []
        seen: set[tuple[str, str]] = set()

        def attach(parent_tf: str, node: tuple[str, str]) -> None:
            if node in seen:
                return
            seen.add(node)
            if parent_tf == "0" or parent_tf not in tf_owner:
                roots.append(node)
            else:
                children.setdefault(tf_owner[parent_tf][1], []).append(node)

        # ordered walk: parents list their children, so use m_Children for order
        for b in self.blocks.values():
            if b.class_id in TRANSFORM_CLASSES and not b.stripped:
                owner = tf_owner.get(b.file_id)
                if not owner:
                    continue
                for child_tf in b.ref_list("m_Children"):
                    node = tf_owner.get(child_tf)
                    if node and node not in seen:
                        seen.add(node)
                        children.setdefault(owner[1], []).append(node)

        for b in self.blocks.values():
            if b.class_id in TRANSFORM_CLASSES and not b.stripped:
                owner = tf_owner.get(b.file_id)
                if owner:
                    father = b.ref("m_Father")
                    attach(father[0] if father else "0", owner)
            elif b.class_id == PREFAB_INSTANCE_CLASS:
                attach(self.pi_parent(b), ("pi", b.file_id))
        return roots, children

    def label(self, node: tuple[str, str], show_components: bool, short: bool = False) -> str:
        kind, fid = node
        b = self.blocks.get(fid)
        if b is None:
            return f"<missing {fid}>"
        if kind == "pi":
            if short:
                return f"[prefab] {self.pi_name(b)}"
            mods = self.modifications(b)
            src = self.pi_source(b)
            return f"[prefab] {self.pi_name(b)}  <- {src}  ({len(mods)} mods)"
        name = self.go_name(b)
        active = "" if (b.value("m_IsActive") or "1") == "1" else "  (inactive)"
        if not show_components:
            return name + active
        comps = [self.component_label(c) for c in self.components_of(b)]
        comps = [c for c in comps if c != "Transform" and c != "RectTransform"]
        suffix = f"  [{', '.join(comps)}]" if comps else ""
        return name + suffix + active

    def go_path(self, go_file_id: str) -> str:
        """Slow-ish reverse path lookup, used only for reporting matches."""
        if not hasattr(self, "_parent_map"):
            roots, children = self.hierarchy()
            pmap: dict[str, str] = {}
            for parent, kids in children.items():
                for _, cid in kids:
                    pmap[cid] = parent
            self._parent_map = pmap  # type: ignore[attr-defined]
        parts, cur, guard = [], go_file_id, 0
        while cur and guard < 200:
            b = self.blocks.get(cur)
            if b is None:
                break
            parts.append(self.pi_name(b) if b.class_id == PREFAB_INSTANCE_CLASS else self.go_name(b))
            cur = self._parent_map.get(cur)  # type: ignore[attr-defined]
            guard += 1
        return "/".join(reversed(parts))


# ------------------------------------------------------------------ walking

def iter_assets(project: str, patterns: Iterable[str] | None, exts=(".unity", ".prefab")) -> Iterator[str]:
    pats = [p.replace("\\", "/").lower() for p in (patterns or [])]
    for root in (os.path.join(project, "Assets"), os.path.join(project, "Packages")):
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in ("Library", "Temp", "obj", ".git")]
            for name in filenames:
                if not name.endswith(exts):
                    continue
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, project).replace("\\", "/").lower()
                if pats and not any(fnmatch.fnmatch(rel, p) or p in rel for p in pats):
                    continue
                yield full


def rel(project: str, path: str) -> str:
    return os.path.relpath(path, project).replace("\\", "/")


# ----------------------------------------------------------------- commands

def cmd_index(args, project: str) -> None:
    idx = get_index(project, rebuild=True)
    out = {"assets": len(idx.guid_to_path), "scripts": len(idx.guid_to_class),
           "cache": rel(project, idx.cache_path)}
    emit(args, out, lambda: print(f"indexed {out['assets']} assets, {out['scripts']} scripts "
                                  f"-> {out['cache']}"))


def cmd_info(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    doc = Doc.load(path, idx)
    by_type: dict[str, int] = {}
    scripts: dict[str, int] = {}
    for b in doc.blocks.values():
        by_type[doc.component_label(b) if b.class_id == MONOBEHAVIOUR_CLASS else (b.type_name or b.class_id)] = \
            by_type.get(doc.component_label(b) if b.class_id == MONOBEHAVIOUR_CLASS else (b.type_name or b.class_id), 0) + 1
        if b.class_id == MONOBEHAVIOUR_CLASS:
            n = doc.component_label(b).rstrip("*")
            scripts[n] = scripts.get(n, 0) + 1
    pis: dict[str, int] = {}
    for pi in doc.prefab_instances():
        src = doc.pi_source(pi)
        pis[src] = pis.get(src, 0) + 1
    roots, _ = doc.hierarchy()
    data = {
        "path": rel(project, path),
        "size_mb": round(os.path.getsize(path) / 1e6, 2),
        "objects": len(doc.blocks),
        "game_objects": len(doc.game_objects()),
        "prefab_instances": len(doc.prefab_instances()),
        "roots": [doc.label(r, False, short=True) for r in roots],
        "types": dict(sorted(by_type.items(), key=lambda kv: -kv[1])[: args.top]),
        "scripts": dict(sorted(scripts.items(), key=lambda kv: -kv[1])[: args.top]),
        "prefabs_used": dict(sorted(pis.items(), key=lambda kv: -kv[1])[: args.top]),
    }

    def text():
        print(f"{data['path']}  ({data['size_mb']} MB)")
        print(f"  objects={data['objects']}  gameObjects={data['game_objects']}  "
              f"prefabInstances={data['prefab_instances']}")
        print(f"  roots ({len(data['roots'])}): " + ", ".join(data["roots"][:40]))
        print("  top types:   " + ", ".join(f"{k}={v}" for k, v in data["types"].items()))
        if data["scripts"]:
            print("  top scripts: " + ", ".join(f"{k}={v}" for k, v in data["scripts"].items()))
        if data["prefabs_used"]:
            print("  prefabs used:")
            for k, v in data["prefabs_used"].items():
                print(f"    {v:4d}x {k}")
    emit(args, data, text)


def cmd_tree(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    doc = Doc.load(path, idx)
    roots, children = doc.hierarchy()
    pat = re.compile(args.filter, re.I) if args.filter else None
    lines: list[str] = []
    count = 0

    def walk(node, depth):
        nonlocal count
        if count >= args.max:
            return
        label = doc.label(node, not args.no_components)
        if pat is None or pat.search(label):
            lines.append("  " * depth + ("- " if depth else "") + label)
            count += 1
        if depth >= args.depth:
            kids = children.get(node[1], [])
            if kids and pat is None:
                lines.append("  " * (depth + 1) + f"... {len(kids)} more children")
            return
        for kid in children.get(node[1], []):
            walk(kid, depth + 1)

    for r in roots:
        walk(r, 0)
    emit(args, {"path": rel(project, path), "lines": lines},
         lambda: print(f"{rel(project, path)}\n" + "\n".join(lines) +
                       (f"\n... truncated at --max {args.max}" if count >= args.max else "")))


def cmd_find(args, project: str) -> None:
    idx = get_index(project)
    pat = re.compile(args.pattern, re.I)
    results = []
    for asset in iter_assets(project, args.into):
        doc = Doc.load(asset, idx)
        for go in doc.game_objects():
            name = doc.go_name(go)
            if pat.search(name):
                comps = [doc.component_label(c) for c in doc.components_of(go)]
                results.append({"asset": rel(project, asset), "id": go.file_id,
                                "path": doc.go_path(go.file_id), "components": comps})
        for pi in doc.prefab_instances():
            if pat.search(doc.pi_name(pi)):
                results.append({"asset": rel(project, asset), "id": pi.file_id,
                                "path": doc.go_path(pi.file_id),
                                "components": [f"[prefab] {doc.pi_source(pi)}"]})
        if len(results) >= args.max:
            break

    def text():
        shown = results[: args.max]
        for r in shown:
            print(f"{r['asset']}  &{r['id']}\n    {r['path']}\n    {', '.join(r['components'])}")
        more = " (search stopped early; raise --max)" if len(results) > args.max else ""
        print(f"showing {len(shown)} of {len(results)} match(es){more}")
    emit(args, results, text)


def cmd_scripts(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    doc = Doc.load(path, idx)
    pat = re.compile(args.name, re.I) if args.name else None
    results = []
    skip = {"m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance",
            "m_PrefabAsset", "m_GameObject", "m_Enabled", "m_EditorHideFlags",
            "m_Script", "m_Name", "m_EditorClassIdentifier"}
    for b in doc.of_class(MONOBEHAVIOUR_CLASS):
        cls = doc.component_label(b).rstrip("*")
        if pat and not pat.search(cls):
            continue
        go = b.ref("m_GameObject")
        fields: list[str] = []
        key, buf = None, ""

        def flush():
            if key is None:
                return
            val = " ".join(buf.split())
            val = decode_secure_float(val)
            for ref in REF_RE.finditer(val):
                if ref.group(2):
                    val += f"  -> {idx.path_of(ref.group(2))}"
            fields.append(f"{key}: {val}" if val else key + ":")

        for line in b.lines:
            # keys can be auto-property backing fields: <Foo>k__BackingField
            m = re.match(r"^  ([\w<>]+):(.*)$", line.rstrip("\n"))
            if m:                       # new top-level field
                flush()
                key = m.group(1) if m.group(1) not in skip else None
                buf = m.group(2)
            elif key is not None:       # continuation / nested value
                buf += " " + line.strip()
        flush()
        results.append({"class": cls, "id": b.file_id,
                        "gameObject": doc.go_path(go[0]) if go else "?",
                        "fields": fields[: args.fields]})

    def text():
        for r in results:
            print(f"{r['class']}  &{r['id']}  on {r['gameObject']}")
            for f in r["fields"]:
                print("    " + f)
        print(f"{len(results)} MonoBehaviour(s)")
    emit(args, results, text)


def cmd_usage(args, project: str) -> None:
    idx = get_index(project)
    target = args.target
    guid = target if re.fullmatch(r"[0-9a-f]{32}", target) else idx.class_to_guid.get(target)
    if not guid:
        cands = [c for c in idx.class_to_guid if c.lower() == target.lower()]
        guid = idx.class_to_guid.get(cands[0]) if cands else None
    if not guid:
        raise SystemExit(f"error: unknown script {target!r} (try `index --rebuild`)")
    results = []
    for asset in iter_assets(project, args.into):
        with open(asset, "r", encoding="utf-8", errors="ignore") as fh:
            if guid not in fh.read():
                continue
        doc = Doc.load(asset, idx)
        hits = []
        for b in doc.of_class(MONOBEHAVIOUR_CLASS):
            r = b.ref("m_Script")
            if r and r[1] == guid:
                go = b.ref("m_GameObject")
                hits.append(doc.go_path(go[0]) if go else b.file_id)
        if hits:
            results.append({"asset": rel(project, asset), "count": len(hits), "on": hits[: args.max]})

    def text():
        total = sum(r["count"] for r in results)
        print(f"{idx.script_name(guid)}  guid={guid}  ({total} instance(s) in {len(results)} asset(s))")
        for r in results:
            print(f"  {r['asset']}  x{r['count']}")
            for o in r["on"]:
                print(f"      {o}")
    emit(args, results, text)


def cmd_deps(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    counts: dict[str, int] = {}
    with open(path, "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            for m in GUID_RE.finditer(line):
                p = idx.path_of(m.group(1))
                counts[p] = counts.get(p, 0) + 1
    items = sorted(counts.items(), key=lambda kv: -kv[1])
    if args.ext:
        items = [(p, c) for p, c in items if p.endswith(tuple(args.ext))]
    items = items[: args.max]

    def text():
        print(f"{rel(project, path)} references {len(counts)} asset(s):")
        for p, c in items:
            print(f"  {c:5d}x {p}")
    emit(args, dict(items), text)


def cmd_refs(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    guid = idx.path_to_guid.get(rel(project, path))
    if not guid:
        raise SystemExit(f"error: no guid for {rel(project, path)} (missing .meta?)")
    exts = tuple(args.ext) if args.ext else (".unity", ".prefab", ".asset", ".controller", ".mat")
    results = []
    for asset in iter_assets(project, args.into, exts=exts):
        if os.path.abspath(asset) == os.path.abspath(path):
            continue
        with open(asset, "r", encoding="utf-8", errors="ignore") as fh:
            n = fh.read().count(guid)
        if n:
            results.append({"asset": rel(project, asset), "count": n})
    results.sort(key=lambda r: -r["count"])

    def text():
        print(f"{rel(project, path)}  guid={guid}\nreferenced by {len(results)} asset(s):")
        for r in results[: args.max]:
            print(f"  {r['count']:5d}x {r['asset']}")
    emit(args, results, text)


def cmd_doctor(args, project: str) -> None:
    """Broken guid references and .meta hygiene across the whole project."""
    idx = get_index(project)
    known = idx.guid_to_path
    broken: dict[str, dict] = {}
    exts = (".unity", ".prefab", ".asset", ".controller", ".mat", ".playable", ".anim")
    scanned = 0
    for asset in iter_assets(project, args.into, exts=exts):
        scanned += 1
        try:
            with open(asset, "r", encoding="utf-8", errors="ignore") as fh:
                text = fh.read()
        except OSError:
            continue
        missing = {g for g in set(GUID_RE.findall(text))
                   if g not in known and not BUILTIN_GUID_RE.match(g)}
        if not missing:
            continue
        # a guid on an m_Script line is a "missing script" - the actionable case
        script_guids = set(re.findall(r"m_Script:\s*\{fileID:[^}]*?guid:\s*([0-9a-f]{32})", text))
        for g in missing:
            rec = broken.setdefault(g, {"guid": g, "kind": "asset", "referenced_by": []})
            if g in script_guids:
                rec["kind"] = "script"
            rec["referenced_by"].append(rel(project, asset))

    # .meta hygiene
    orphan_meta: list[str] = []
    missing_meta: list[str] = []
    for root_dir in ("Assets",):
        base = os.path.join(project, root_dir)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in ("Library", "Temp", "obj", ".git")]
            dirnames[:] = [d for d in dirnames if not d.startswith(META_EXEMPT)]
            names = set(filenames)

            def exempt(n: str) -> bool:
                return n.startswith(META_EXEMPT) or n.endswith(META_EXEMPT)or n.endswith(META_EXEMPT_EXT)

            for name in filenames:
                full = os.path.join(dirpath, name)
                if name.endswith(".meta"):
                    target = name[:-5]
                    if target not in names and not os.path.isdir(os.path.join(dirpath, target)):
                        orphan_meta.append(rel(project, full))
                elif not exempt(name) and name + ".meta" not in names:
                    missing_meta.append(rel(project, full))
            for d in dirnames:
                if d + ".meta" not in names:
                    missing_meta.append(rel(project, os.path.join(dirpath, d)))

    scripts = [b for b in broken.values() if b["kind"] == "script"]
    assets_ = [b for b in broken.values() if b["kind"] == "asset"]
    data = {"scanned": scanned, "missing_scripts": scripts, "broken_asset_refs": assets_,
            "orphan_meta": orphan_meta, "missing_meta": missing_meta}

    def text():
        print(f"scanned {scanned} asset(s)")
        for label, items in (("MISSING SCRIPTS (guid has no .cs - shows as 'Missing (Mono Script)')", scripts),
                             ("broken asset references (guid has no asset)", assets_)):
            print(f"\n{label}: {len(items)}")
            for b in items[: args.max]:
                users = b["referenced_by"]
                print(f"  {b['guid']}  in {len(users)} asset(s): {', '.join(users[:4])}"
                      + (" ..." if len(users) > 4 else ""))
            if len(items) > args.max:
                print(f"  ... {len(items) - args.max} more")
        print(f"\nfiles with no .meta: {len(missing_meta)}")
        for f in missing_meta[: args.max]:
            print(f"  {f}")
        print(f"orphan .meta (asset deleted): {len(orphan_meta)}")
        for f in orphan_meta[: args.max]:
            print(f"  {f}")
    emit(args, data, text)


def cmd_obj(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    doc = Doc.load(path, idx)
    wanted: list[Block] = []
    if re.fullmatch(r"-?\d+", args.target):
        b = doc.blocks.get(args.target)
        if not b:
            raise SystemExit(f"error: no object &{args.target} in {rel(project, path)}")
        wanted.append(b)
        if b.class_id == GAMEOBJECT_CLASS and args.components:
            wanted += doc.components_of(b)
    else:
        pat = re.compile(args.target, re.I)
        for go in doc.game_objects():
            if pat.search(doc.go_name(go)):
                wanted.append(go)
                if args.components:
                    wanted += doc.components_of(go)
    out = []
    for b in wanted[: args.max]:
        out.append(f"--- !u!{b.class_id} &{b.file_id}{' stripped' if b.stripped else ''}\n"
                   f"{b.type_name}:\n{b.text}")
    emit(args, out, lambda: print("\n".join(out) or "no match"))


def cmd_mods(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    doc = Doc.load(path, idx)
    pat = re.compile(args.name, re.I) if args.name else None
    results = []
    for pi in doc.prefab_instances():
        name = doc.pi_name(pi)
        if pat and not pat.search(name):
            continue
        mods = [m for m in doc.modifications(pi)
                if not args.interesting or not m.path.startswith(("m_Anchor", "m_Pivot", "m_SizeDelta",
                                                                 "m_LocalEulerAnglesHint", "m_RootOrder"))]
        results.append({"name": name, "id": pi.file_id, "source": doc.pi_source(pi),
                        "parent": doc.go_path(pi.file_id),
                        "mods": [{"path": m.path, "value": m.value or m.objref or "",
                                  "target": m.target} for m in mods[: args.max]]})

    def text():
        for r in results:
            print(f"[prefab] {r['name']}  &{r['id']}  <- {r['source']}")
            print(f"    at {r['parent']}")
            for m in r["mods"]:
                print(f"    {m['path']} = {m['value']}")
        print(f"{len(results)} prefab instance(s)")
    emit(args, results, text)


# -------------------------------------------------------------------- write

def block_spans(path: str) -> tuple[list[str], dict[str, tuple[int, int]]]:
    """Raw lines, plus file_id -> [start, end) body line range.

    iter_blocks() discards physical line positions, which is fine for reading
    and useless for writing. Keeping the offsets lets an edit rewrite exactly
    one line and leave every other byte of the file identical — Unity is picky
    about its own formatting, and a reserialised file produces a diff that is
    impossible to review.
    """
    with open(path, "r", encoding="utf-8", errors="ignore", newline="") as fh:
        lines = fh.readlines()
    spans: dict[str, tuple[int, int]] = {}
    cur, start = None, 0
    for i, line in enumerate(lines):
        m = BLOCK_RE.match(line.rstrip("\r\n"))
        if not m:
            continue
        if cur:
            spans[cur] = (start, i)
        cur, start = m.group(2), i + 1
    if cur:
        spans[cur] = (start, len(lines))
    return lines, spans


def parse_assignment(arg: str) -> tuple[str, str]:
    if "=" not in arg:
        raise SystemExit(f"error: {arg!r} is not field=value")
    key, val = (s.strip() for s in arg.split("=", 1))
    if not key:
        raise SystemExit(f"error: {arg!r} has an empty field name")
    # Unity serialises bools as 0/1. Writing `true` yields a value Unity reads
    # back as 0, so the edit looks applied in the file and does nothing in game.
    if val.lower() in ("true", "false"):
        val = "1" if val.lower() == "true" else "0"
    # Structural characters would splice new YAML into the block rather than
    # replace a value. Only plain scalars are writable here by design.
    if not val or any(c in val for c in "{}[]\r\n#"):
        raise SystemExit(f"error: {val!r} is not a plain scalar; `set` writes "
                         "numbers, enums and bools, not references or lists")
    return key, val


def find_overrides(project: str, idx: Index, asset_path: str,
                   fields: Iterable[str]) -> list[dict]:
    """Prefab-instance overrides that would mask an edit to `asset_path`.

    A scene that instantiates this prefab can pin any field in its
    m_Modifications block. That value wins at runtime, so editing the prefab
    changes nothing for that instance — a silent no-op worth surfacing.
    """
    guid = idx.path_to_guid.get(rel(project, asset_path))
    if not guid:
        return []
    wanted = set(fields)
    out: list[dict] = []
    for other in iter_assets(project, None):
        if os.path.abspath(other) == os.path.abspath(asset_path):
            continue
        try:
            with open(other, "r", encoding="utf-8", errors="ignore") as fh:
                text = fh.read()
        except OSError:
            continue
        if guid not in text:
            continue
        for m in MOD_RE.finditer(text):
            path_ = m.group("path").strip()
            if path_ in wanted and guid in m.group("target"):
                out.append({"asset": rel(project, other), "field": path_,
                            "value": m.group("value").strip()})
    return out


def cmd_set(args, project: str) -> None:
    idx = get_index(project)
    path = idx.resolve_asset(args.asset)
    doc = Doc.load(path, idx)
    assigns = [parse_assignment(a) for a in args.assignment]

    pat = re.compile(args.name, re.I) if args.name else None
    on = re.compile(args.on, re.I) if args.on else None
    want_id = args.id.lstrip("&") if args.id else None

    targets = []
    for b in doc.of_class(MONOBEHAVIOUR_CLASS):
        cls = doc.component_label(b).rstrip("*")
        if pat and not pat.search(cls):
            continue
        if want_id and b.file_id != want_id:
            continue
        go = b.ref("m_GameObject")
        gopath = doc.go_path(go[0]) if go else "?"
        if on and not on.search(gopath):
            continue
        targets.append((b, cls, gopath))

    if not targets:
        raise SystemExit("error: no MonoBehaviour matched (check --name / --on / --id)")

    lines, spans = block_spans(path)
    edits: list[tuple[int, str]] = []
    results = []

    for b, cls, gopath in targets:
        start, end = spans[b.file_id]
        changes = []
        for key, new in assigns:
            hit = None
            for i in range(start, end):
                m = re.match(rf"^(  {re.escape(key)}:)([^\r\n]*)([\r\n]*)$", lines[i])
                if m:
                    hit = (i, m)
                    break
            # Unity drops keys it does not recognise without complaint, so a
            # typo would write a field that silently never takes effect.
            # Refusing is the only way the caller finds out.
            if hit is None:
                raise SystemExit(
                    f"error: {cls} &{b.file_id} has no serialized field {key!r}; "
                    "run `scripts` to see the exact names")
            i, m = hit
            old = m.group(2).strip()
            if not old:
                raise SystemExit(
                    f"error: {key!r} on {cls} &{b.file_id} is a list or nested "
                    "block, not a scalar; refusing to edit")
            if "{" in old:
                raise SystemExit(
                    f"error: {key!r} on {cls} &{b.file_id} is an object reference "
                    f"({old}); refusing to edit")
            # An empty array serialises inline as `[]`, which passes the
            # non-empty test above while being just as structural as a
            # multi-line list.
            if old.startswith("["):
                raise SystemExit(
                    f"error: {key!r} on {cls} &{b.file_id} is an array ({old}); "
                    "refusing to edit")
            if old == new:
                continue
            edits.append((i, f"{m.group(1)} {new}{m.group(3)}"))
            changes.append({"field": key, "old": old, "new": new})
        if changes:
            results.append({"class": cls, "id": b.file_id, "gameObject": gopath,
                            "changes": changes})

    overrides = (find_overrides(project, idx, path, [k for k, _ in assigns])
                 if args.check_overrides else [])

    if args.write and edits:
        for i, text in edits:
            lines[i] = text
        with open(path, "w", encoding="utf-8", newline="") as fh:
            fh.writelines(lines)

    payload = {"asset": rel(project, path), "written": bool(args.write and edits),
               "matched": len(targets), "components": results, "overrides": overrides}

    def text():
        for r in results:
            print(f"{r['class']}  &{r['id']}  on {r['gameObject']}")
            for c in r["changes"]:
                print(f"    {c['field']}: {c['old']} -> {c['new']}")
        if not results:
            print(f"{len(targets)} component(s) matched; all values already set")
        elif args.write:
            print(f"wrote {len(edits)} field(s) across {len(results)} component(s) "
                  f"in {rel(project, path)}")
        else:
            print(f"DRY RUN - {len(edits)} field(s) across {len(results)} "
                  "component(s). Re-run with --write to apply.")
        for o in overrides:
            print(f"  warning: {o['asset']} overrides {o['field']} = {o['value']} "
                  "on an instance of this prefab; that value wins there")

    emit(args, payload, text)


# -------------------------------------------------------------------- shell

def emit(args, data, text_fn) -> None:
    if args.json:
        json.dump(data, sys.stdout, indent=1, default=str)
        print()
    else:
        text_fn()


def main(argv: list[str] | None = None) -> None:
    p = argparse.ArgumentParser(prog="unityscan", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--project", help="Unity project root (default: walk up for Assets/)")
    p.add_argument("--json", action="store_true", help="machine-readable output")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("index", help="rebuild the guid -> asset / script cache")
    s.set_defaults(fn=cmd_index)

    s = sub.add_parser("info", help="one-screen summary of a scene or prefab")
    s.add_argument("asset")
    s.add_argument("--top", type=int, default=20)
    s.set_defaults(fn=cmd_info)

    s = sub.add_parser("tree", help="GameObject hierarchy with components")
    s.add_argument("asset")
    s.add_argument("--depth", type=int, default=2, help="levels to expand (default 2)")
    s.add_argument("--filter", help="regex; print only matching lines, at full depth")
    s.add_argument("--no-components", action="store_true")
    s.add_argument("--max", type=int, default=400, help="max lines")
    s.set_defaults(fn=cmd_tree)

    s = sub.add_parser("find", help="find GameObjects/prefab instances by name across the project")
    s.add_argument("pattern", help="regex, case-insensitive")
    s.add_argument("--into", nargs="*", help="limit to path globs, e.g. Assets/Prefabs/*")
    s.add_argument("--max", type=int, default=100)
    s.set_defaults(fn=cmd_find)

    s = sub.add_parser("scripts", help="list MonoBehaviours and their serialized field values")
    s.add_argument("asset")
    s.add_argument("--name", help="regex on the script class name")
    s.add_argument("--fields", type=int, default=40, help="max fields per component")
    s.set_defaults(fn=cmd_scripts)

    s = sub.add_parser("usage", help="where a script is attached (class name or guid)")
    s.add_argument("target")
    s.add_argument("--into", nargs="*")
    s.add_argument("--max", type=int, default=25, help="max GameObject paths per asset")
    s.set_defaults(fn=cmd_usage)

    s = sub.add_parser("deps", help="assets referenced by a scene/prefab")
    s.add_argument("asset")
    s.add_argument("--ext", nargs="*", help="filter by extension, e.g. .prefab .mat")
    s.add_argument("--max", type=int, default=60)
    s.set_defaults(fn=cmd_deps)

    s = sub.add_parser("refs", help="assets that reference this asset (reverse deps)")
    s.add_argument("asset")
    s.add_argument("--into", nargs="*")
    s.add_argument("--ext", nargs="*")
    s.add_argument("--max", type=int, default=60)
    s.set_defaults(fn=cmd_refs)

    s = sub.add_parser("doctor", help="find broken guid references and .meta problems")
    s.add_argument("--into", nargs="*")
    s.add_argument("--max", type=int, default=20)
    s.set_defaults(fn=cmd_doctor)

    s = sub.add_parser("obj", help="dump raw YAML for an object by fileID or GameObject name")
    s.add_argument("asset")
    s.add_argument("target", help="fileID or name regex")
    s.add_argument("--components", action="store_true", help="also dump the GameObject's components")
    s.add_argument("--max", type=int, default=10, help="max blocks")
    s.set_defaults(fn=cmd_obj)

    s = sub.add_parser("set", help="write serialized field values on MonoBehaviours",
                       description="Set scalar serialized fields in place. Dry run unless "
                                   "--write is given. Unity must not have the asset open, or "
                                   "the editor will overwrite the change on next save.")
    s.add_argument("asset")
    s.add_argument("assignment", nargs="+", metavar="FIELD=VALUE",
                   help="e.g. compressionStiffness=5000 frictionCoupledToNormal=true")
    s.add_argument("--name", help="regex on the script class name")
    s.add_argument("--on", help="regex on the GameObject path")
    s.add_argument("--id", help="exact component fileID")
    s.add_argument("--write", action="store_true", help="apply the change (default: dry run)")
    s.add_argument("--check-overrides", action="store_true",
                   help="scan for prefab-instance overrides that would mask the edit")
    s.set_defaults(fn=cmd_set)

    s = sub.add_parser("mods", help="prefab instances and their property overrides")
    s.add_argument("asset")
    s.add_argument("--name", help="regex on the instance name")
    s.add_argument("--interesting", action="store_true", help="hide RectTransform/layout noise")
    s.add_argument("--max", type=int, default=40, help="max mods per instance")
    s.set_defaults(fn=cmd_mods)

    args = p.parse_args(argv)
    project = os.path.abspath(args.project) if args.project else find_project()
    args.fn(args, project)


if __name__ == "__main__":
    main()
