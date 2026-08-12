# CLAUDE.md

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

# MoSimulator

## Scene & Prefab Inspection

Do NOT read or grep scene/prefab YAML directly. Use `Tools/unityscan.py` (accepts basename, relative path, or full path; supports `--json`).

* `python Tools/unityscan.py index` — Refresh cache
* `unityscan.py info <asset>` — Content summary
* `unityscan.py tree <asset> [--depth N]` — Hierarchy
* `unityscan.py find "<regex>"` — Search GameObjects
* `unityscan.py scripts <asset> --name <script>` — Inspector values
* `unityscan.py usage <script>` — Script usages
* `unityscan.py deps <asset>` / `refs <asset>` — Dependencies / References
* `unityscan.py mods <asset> --interesting` — Prefab overrides
* `unityscan.py obj <asset> <id>` — Raw object YAML
* `unityscan.py doctor` — Find broken references

## Editing Serialized Fields

Do NOT hand-edit prefab/scene YAML. Use `unityscan.py set`, the one write command.

* `unityscan.py set <asset> FIELD=VALUE [...] --name <script> [--on <path regex>] [--id <fileID>]`

Dry run unless `--write` is passed. It rewrites single lines in place, refuses
unknown field names (Unity drops them silently), and refuses arrays and object
references. Bools take `true`/`false` or `1`/`0`.

Close the asset in Unity first, or the editor will overwrite the change on its
next save. Add `--check-overrides` to catch prefab-instance overrides in scenes
that would mask the edit.

## C# Compilation

Do NOT run `dotnet build`. Use `Tools/unitybuild.py` (outputs errors only).

* `python Tools/unitybuild.py [--warnings]`

## Unity Logs

Never read `Editor.log` directly. Use `Tools/unitylog.py` to extract errors/exceptions.

* `python Tools/unitylog.py [--new|--player|--tail N|--grep RE]`
