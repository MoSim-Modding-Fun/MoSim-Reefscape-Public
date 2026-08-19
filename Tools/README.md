# Tools

Utility scripts for working with this Unity project without going through the
Unity Editor UI. See the root `CLAUDE.md` for when these are required
(scene/prefab inspection, editing serialized fields, C# compilation, and log
reading must go through these tools rather than direct file access).

## unityscan.py

Low-token inspection of Unity scene/prefab YAML. Parses the serialized YAML
directly (no Unity, no PyYAML) and prints compact summaries instead of
dumping raw asset files into context. Accepts a basename, relative path, or
full path for any asset argument. Every command supports `--json`.

```
python Tools/unityscan.py index                 # build guid -> asset map (refresh cache)
python Tools/unityscan.py info Reefscape.unity   # content summary
python Tools/unityscan.py tree Arena.prefab --depth 3   # hierarchy
python Tools/unityscan.py find "Turret.*"        # search GameObjects by regex
python Tools/unityscan.py scripts Robot.prefab --name Drivetrain   # inspector values
python Tools/unityscan.py usage InteractionRoller  # script usages
python Tools/unityscan.py deps Reefscape.unity   # dependencies
python Tools/unityscan.py refs Assets/Prefabs/Robot/Robot.prefab  # references
python Tools/unityscan.py obj Robot.prefab 17205343   # raw object YAML
python Tools/unityscan.py doctor                 # find broken references
python Tools/unityscan.py set Robot.prefab rpm=6000 --name InteractionRoller --write
```

`set` is the only command that writes, via `FIELD=VALUE` pairs plus
`--name <script>` (optionally `--on <path regex>` / `--id <fileID>`). It's a
dry run unless `--write` is passed, refuses unknown field names (Unity drops
them silently otherwise), refuses arrays and object references, and rewrites
single lines in place so diffs stay reviewable. Add `--check-overrides` to
catch prefab-instance overrides in scenes that would mask the edit. Close the
asset in Unity first, or the editor will overwrite the change on its next
save.

## unitybuild.py

Compile-checks the project's C# without opening Unity, by running `dotnet
build` against the Unity-generated `.sln` and printing only errors
(de-duplicated), instead of MSBuild's usual few hundred lines of noise.

```
python Tools/unitybuild.py                  # errors only
python Tools/unitybuild.py --warnings        # include warnings
python Tools/unitybuild.py --project Assembly-CSharp.csproj
python Tools/unitybuild.py --json
```

Also checks for staleness: Unity generates the `.csproj` files, so a `.cs`
file added since the last Unity refresh belongs to no project and won't be
compiled. The tool detects this and says so instead of reporting a
misleadingly clean build.

## unitylog.py

Extracts the signal from Unity's `Editor.log` / `Player.log` — compile
errors and exceptions, collapsed and pointed at the project source line —
instead of wading through tens of thousands of lines of shader compiles,
asset imports, and memory stats.

```
python Tools/unitylog.py                 # summary of the whole log
python Tools/unitylog.py --new           # only what appeared since last run
python Tools/unitylog.py --player        # the built game's log instead
python Tools/unitylog.py --grep Steam    # lines matching a regex
python Tools/unitylog.py --tail 40       # raw tail, for when parsing misses something
```

`--new` is meant for a change-test loop: run it, press Play in Unity, run it
again, and you get exactly the exceptions that run produced.

## build-mods-all-platforms.ps1 / .sh / .bat

Builds one or more addressable mod groups for Windows, macOS, and Linux by
launching a fresh headless Unity process per platform (switching build
target inside a running Editor session doesn't reliably re-import
platform-specific assets). Each process runs
`Editor.AddressablesModExporter.BuildFromCommandLine`, which builds the
groups via the default Addressables build script, copies the platform
catalog files and robot DLLs into `Mods/<GroupName>/`, and zips each one.

Three entry points, same underlying logic:

- **`build-mods-all-platforms.ps1`** — the original PowerShell implementation.
  Use directly on Windows (or via PowerShell Core on Linux/macOS).
- **`build-mods-all-platforms.bat`** — thin `cmd.exe` wrapper that forwards
  its arguments straight to the `.ps1` (PowerShell-style flags, comma-joined
  arrays).
- **`build-mods-all-platforms.sh`** — bash port for Linux/macOS, reimplementing
  the same logic without depending on PowerShell being installed. Flags are
  comma-separated instead of PowerShell arrays.

```powershell
# Windows (PowerShell or the .bat wrapper)
./Tools/build-mods-all-platforms.ps1 -Groups "NY Modpack"
Tools\build-mods-all-platforms.bat -Groups "NY Modpack","China Modpack" -Versions "v2.1.0","v1.0.0"
```

```bash
# Linux / macOS
./Tools/build-mods-all-platforms.sh --groups "NY Modpack"

./Tools/build-mods-all-platforms.sh \
    --groups "NY Modpack,China Modpack" \
    --versions "v2.1.0,v1.0.0" \
    --zipnames "NY Modpack,Lanternfly Release"
```

Versions/zip-name overrides, if given, are matched to groups by position
(same count and order; use an empty entry to skip a value for one group).
Close Unity before running — it refuses to open a project that's already
open elsewhere. Logs are written per platform to `Tools/build-logs/`; a
build is judged failed if the log shows a compiler/crash error or an
expected zip is missing, not by Unity's exit code alone (a licensing-client
warning can make Unity exit non-zero on an otherwise clean build).

The `.sh` script auto-detects a default Unity path (`/Applications/Unity/...`
on macOS, `$HOME/Unity/Hub/Editor/...` on Linux) — pass `--unity-path` if
Unity was installed elsewhere.
