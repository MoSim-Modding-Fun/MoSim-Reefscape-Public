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
