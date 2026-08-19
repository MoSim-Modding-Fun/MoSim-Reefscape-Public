#!/usr/bin/env bash
# build-mods-all-platforms.sh
#
# Builds selected addressable mod group(s) for Windows, macOS, and Linux.
# Bash port of build-mods-all-platforms.ps1, for running on Linux/macOS.
#
# Switching build target inside a running Unity Editor session doesn't reliably
# re-import platform-specific assets, so this launches a fresh headless Unity
# process per platform (-buildTarget win64/osx/linux64). Each process runs
# Editor.AddressablesModExporter.BuildFromCommandLine, which builds the given
# groups via the default Addressables build script and copies the platform
# catalog files + robot DLLs into Mods/<GroupName>/, then zips each one.
#
# --versions and --zipnames, if given, must have the same number of
# comma-separated entries as --groups, matched by position. Use an empty
# entry (",,") to skip a value for one group.
#
# Examples:
#   ./Tools/build-mods-all-platforms.sh --groups "NY Modpack"
#
#   ./Tools/build-mods-all-platforms.sh \
#       --groups "NY Modpack,China Modpack" \
#       --versions "v2.1.0,v1.0.0" \
#       --zipnames "NY Modpack,Lanternfly Release"
#
# Groups/versions/zipnames are comma-separated (not pipe- or space-separated
# like the PowerShell version's array params), since bash has no native
# array-typed CLI flags. A group name may not contain a comma.

set -u

UNITY_VERSION="2023.2.22f1"
UNITY_PATH=""
PROJECT_PATH=""
GROUPS_CSV=""
VERSIONS_CSV=""
ZIPNAMES_CSV=""

usage() {
    echo "Usage: $0 --groups \"Group1,Group2\" [--versions \"v1,v2\"] [--zipnames \"Name1,Name2\"]"
    echo "          [--unity-version VERSION] [--unity-path PATH] [--project-path PATH]"
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        --groups) GROUPS_CSV="$2"; shift 2 ;;
        --versions) VERSIONS_CSV="$2"; shift 2 ;;
        --zipnames) ZIPNAMES_CSV="$2"; shift 2 ;;
        --unity-version) UNITY_VERSION="$2"; shift 2 ;;
        --unity-path) UNITY_PATH="$2"; shift 2 ;;
        --project-path) PROJECT_PATH="$2"; shift 2 ;;
        -h|--help) usage ;;
        *) echo "Unknown argument: $1" >&2; usage ;;
    esac
done

[ -z "$GROUPS_CSV" ] && { echo "error: --groups is required" >&2; usage; }

IFS=',' read -r -a GROUPS <<< "$GROUPS_CSV"
VERSIONS=()
ZIPNAMES=()
[ -n "$VERSIONS_CSV" ] && IFS=',' read -r -a VERSIONS <<< "$VERSIONS_CSV"
[ -n "$ZIPNAMES_CSV" ] && IFS=',' read -r -a ZIPNAMES <<< "$ZIPNAMES_CSV"

if [ -z "$PROJECT_PATH" ]; then
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    PROJECT_PATH="$(cd "$SCRIPT_DIR/.." && pwd)"
fi

if [ ! -d "$PROJECT_PATH/Assets" ]; then
    echo "error: ProjectPath '$PROJECT_PATH' doesn't look like the Unity project root (no Assets folder found). Pass --project-path explicitly." >&2
    exit 1
fi

if [ -z "$UNITY_PATH" ]; then
    case "$(uname -s)" in
        Darwin) UNITY_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity" ;;
        *) UNITY_PATH="$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity" ;;
    esac
fi

if [ ! -x "$UNITY_PATH" ]; then
    echo "error: Unity executable not found at: $UNITY_PATH" >&2
    exit 1
fi

if [ "${#VERSIONS[@]}" -gt 0 ] && [ "${#VERSIONS[@]}" -ne "${#GROUPS[@]}" ]; then
    echo "error: --versions must have the same number of entries as --groups (${#GROUPS[@]}), or be omitted." >&2
    exit 1
fi
if [ "${#ZIPNAMES[@]}" -gt 0 ] && [ "${#ZIPNAMES[@]}" -ne "${#GROUPS[@]}" ]; then
    echo "error: --zipnames must have the same number of entries as --groups (${#GROUPS[@]}), or be omitted." >&2
    exit 1
fi

# Unity refuses to open a project that's already open elsewhere (crashes instantly with
# "Project already open in another instance"). Warn up front instead of burning a build.
if pgrep -f "Unity" > /dev/null 2>&1; then
    echo "warning: Unity appears to already be running. If it has this project open, close it first or every platform here will crash on launch." >&2
fi

declare -A PLATFORM_LABELS=( ["win64"]="Windows" ["osx"]="MacOS" ["linux64"]="Linux" )
FAILURE_MARKERS=(
    "error CS"
    "Aborting batchmode due to failure"
    "Scripts have compiler errors"
    "crash has been intercepted"
    "Multiple Unity instances cannot open the same project"
)

log_failed() {
    local log_file="$1"
    [ -f "$log_file" ] || return 0
    for marker in "${FAILURE_MARKERS[@]}"; do
        grep -qF "$marker" "$log_file" && return 0
    done
    return 1
}

expected_zip_path() {
    local index="$1" target="$2"
    local group="${GROUPS[$index]}"
    local zip_name="$group"
    [ "${#ZIPNAMES[@]}" -gt 0 ] && [ -n "${ZIPNAMES[$index]}" ] && zip_name="${ZIPNAMES[$index]}"
    local version=""
    [ "${#VERSIONS[@]}" -gt 0 ] && version="${VERSIONS[$index]}"
    local label="${PLATFORM_LABELS[$target]}"
    if [ -n "$version" ]; then
        echo "$PROJECT_PATH/Mods/$zip_name $version $label.zip"
    else
        echo "$PROJECT_PATH/Mods/$zip_name $label.zip"
    fi
}

TARGETS=("win64" "osx" "linux64")
join_by() { local IFS="$1"; shift; echo "$*"; }
GROUPS_ARG="$(join_by '|' "${GROUPS[@]}")"
VERSIONS_ARG=""
[ "${#VERSIONS[@]}" -gt 0 ] && VERSIONS_ARG="$(join_by '|' "${VERSIONS[@]}")"
ZIPNAMES_ARG=""
[ "${#ZIPNAMES[@]}" -gt 0 ] && ZIPNAMES_ARG="$(join_by '|' "${ZIPNAMES[@]}")"

LOG_DIR="$PROJECT_PATH/Tools/build-logs"
mkdir -p "$LOG_DIR"

for target in "${TARGETS[@]}"; do
    LOG_FILE="$LOG_DIR/build-$target.log"
    echo "=== Building [$GROUPS_ARG] for $target ==="

    UNITY_ARGS=(
        -batchmode -quit -nographics
        -projectPath "$PROJECT_PATH"
        -buildTarget "$target"
        -executeMethod Editor.AddressablesModExporter.BuildFromCommandLine
        -groups "$GROUPS_ARG"
        -logFile "$LOG_FILE"
    )
    [ -n "$VERSIONS_ARG" ] && UNITY_ARGS+=(-versions "$VERSIONS_ARG")
    [ -n "$ZIPNAMES_ARG" ] && UNITY_ARGS+=(-zipNames "$ZIPNAMES_ARG")

    "$UNITY_PATH" "${UNITY_ARGS[@]}"
    EXIT_CODE=$?

    MISSING_ZIPS=()
    for i in "${!GROUPS[@]}"; do
        zip_path="$(expected_zip_path "$i" "$target")"
        [ -f "$zip_path" ] || MISSING_ZIPS+=("$zip_path")
    done

    # Unity's own exit code isn't fully trustworthy here (e.g. a licensing-client
    # warning can make it return non-zero even after a build that completed and wrote
    # its output fine) so the real signal is: did every expected zip land, and does the
    # log show an actual compiler/crash error. A non-zero exit with clean output is just
    # a warning.
    if log_failed "$LOG_FILE" || [ "${#MISSING_ZIPS[@]}" -gt 0 ]; then
        echo "Build FAILED for $target (exit $EXIT_CODE). Tail of $LOG_FILE :"
        tail -n 50 "$LOG_FILE" 2>/dev/null
        if [ "${#MISSING_ZIPS[@]}" -gt 0 ]; then
            echo "Missing expected output:"
            for z in "${MISSING_ZIPS[@]}"; do echo "  $z"; done
        fi
        exit 1
    fi
    if [ "$EXIT_CODE" -ne 0 ]; then
        echo "warning: $target : Unity exited with code $EXIT_CODE, but all expected zips were produced and no compiler/crash errors were found in the log - treating as success. Check $LOG_FILE if that seems wrong." >&2
    fi
    echo "OK: $target complete. ${#GROUPS[@]} zip(s) verified."
done

echo "All platforms built for: $GROUPS_ARG"
