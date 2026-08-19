<#
.SYNOPSIS
  Builds selected addressable mod group(s) for Windows, macOS, and Linux.

.DESCRIPTION
  Switching build target inside a running Unity Editor session doesn't reliably
  re-import platform-specific assets, so this launches a fresh headless Unity
  process per platform (-buildTarget win64/osx/linux64). Each process runs
  Editor.AddressablesModExporter.BuildFromCommandLine, which builds the given
  groups via the default Addressables build script and copies the platform
  catalog files + robot DLLs into Mods/<GroupName>/, then zips each one.

  -Versions and -ZipNames, if given, are matched to -Groups by position - same
  count and order. Use an empty string to skip a value for one group.

.EXAMPLE
  ./Tools/build-mods-all-platforms.ps1 -Groups "NY Modpack"

.EXAMPLE
  ./Tools/build-mods-all-platforms.ps1 `
      -Groups "NY Modpack","China Modpack" `
      -Versions "v2.1.0","v1.0.0" `
      -ZipNames "NY Modpack","Lanternfly Release"
#>
param(
    [Parameter(Mandatory = $true)][string[]]$Groups,
    [string[]]$Versions = @(),
    [string[]]$ZipNames = @(),
    [string]$UnityVersion = "2023.2.22f1",
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe",
    [string]$ProjectPath = ""
)

if (-not $ProjectPath) {
    $scriptRoot = $PSScriptRoot
    if (-not $scriptRoot) { $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ProjectPath = (Resolve-Path (Join-Path $scriptRoot "..")).Path
}

if (-not (Test-Path (Join-Path $ProjectPath "Assets"))) {
    Write-Error "ProjectPath '$ProjectPath' doesn't look like the Unity project root (no Assets folder found). Pass -ProjectPath explicitly."
    exit 1
}

if (-not (Test-Path $UnityPath)) {
    Write-Error "Unity executable not found at: $UnityPath"
    exit 1
}

if ($Versions.Count -gt 0 -and $Versions.Count -ne $Groups.Count) {
    Write-Error "-Versions must have the same number of entries as -Groups ($($Groups.Count)), or be omitted."
    exit 1
}
if ($ZipNames.Count -gt 0 -and $ZipNames.Count -ne $Groups.Count) {
    Write-Error "-ZipNames must have the same number of entries as -Groups ($($Groups.Count)), or be omitted."
    exit 1
}

# Unity refuses to open a project that's already open elsewhere (crashes instantly with
# "Project already open in another instance"). Warn up front instead of burning a build.
$runningUnity = Get-Process -Name "Unity" -ErrorAction SilentlyContinue
if ($runningUnity) {
    Write-Warning "Unity is already running (PID $($runningUnity.Id -join ', ')). If it has this project open, close it first or every platform here will crash on launch."
}

$platformLabels = @{ "win64" = "Windows"; "osx" = "MacOS"; "linux64" = "Linux" }
$failureMarkers = @(
    "error CS",
    "Aborting batchmode due to failure",
    "Scripts have compiler errors",
    "crash has been intercepted",
    "Multiple Unity instances cannot open the same project"
)

function Test-LogFailed($logFile) {
    if (-not (Test-Path $logFile)) { return $true }
    $content = Get-Content $logFile -Raw
    foreach ($marker in $failureMarkers) {
        if ($content -match [regex]::Escape($marker)) { return $true }
    }
    return $false
}

function Get-ExpectedZipPath($group, $index) {
    $zipName = if ($ZipNames.Count -gt 0 -and $ZipNames[$index]) { $ZipNames[$index] } else { $group }
    $version = if ($Versions.Count -gt 0) { $Versions[$index] } else { "" }
    $label = $platformLabels[$target]
    $archiveName = if ($version) { "$zipName $version $label.zip" } else { "$zipName $label.zip" }
    return Join-Path (Join-Path $ProjectPath "Mods") $archiveName
}

$targets = @("win64", "osx", "linux64")
$groupsArg = ($Groups -join "|")
$versionsArg = ($Versions -join "|")
$zipNamesArg = ($ZipNames -join "|")
$logDir = Join-Path $ProjectPath "Tools\build-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

foreach ($target in $targets) {
    $logFile = Join-Path $logDir "build-$target.log"
    Write-Host "=== Building [$groupsArg] for $target ==="

    # Start-Process -ArgumentList joins elements with a plain space and does NOT auto-quote
    # ones containing spaces (unlike the `&` call operator), so any value that might contain
    # a space (group names, paths) must be wrapped in literal quotes ourselves.
    $unityArgs = @(
        "-batchmode", "-quit", "-nographics",
        "-projectPath", "`"$ProjectPath`"",
        "-buildTarget", $target,
        "-executeMethod", "Editor.AddressablesModExporter.BuildFromCommandLine",
        "-groups", "`"$groupsArg`"",
        "-logFile", "`"$logFile`""
    )
    if ($Versions.Count -gt 0) { $unityArgs += @("-versions", "`"$versionsArg`"") }
    if ($ZipNames.Count -gt 0) { $unityArgs += @("-zipNames", "`"$zipNamesArg`"") }

    # Unity.exe is a GUI-subsystem app; the `&` call operator doesn't reliably block on it here
    # (observed: control returned to the script, and Unity kept building in the background for
    # minutes afterward). Start-Process -Wait genuinely waits on the process handle.
    $proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -Wait -PassThru
    $exitCode = $proc.ExitCode

    $expectedZips = for ($i = 0; $i -lt $Groups.Count; $i++) { Get-ExpectedZipPath $Groups[$i] $i }
    $missingZips = $expectedZips | Where-Object { -not (Test-Path $_) }
    $logFailed = Test-LogFailed $logFile

    # Unity's own exit code isn't fully trustworthy here (e.g. a licensing-client warning
    # can make it return non-zero even after a build that completed and wrote its output
    # fine) so the real signal is: did every expected zip land, and does the log show an
    # actual compiler/crash error. A non-zero exit with clean output is just a warning.
    if ($logFailed -or $missingZips.Count -gt 0) {
        Write-Host "Build FAILED for $target (exit $exitCode). Tail of $logFile :"
        Get-Content $logFile -Tail 50
        if ($missingZips.Count -gt 0) {
            Write-Host "Missing expected output:"
            $missingZips | ForEach-Object { Write-Host "  $_" }
        }
        exit 1
    }
    if ($exitCode -ne 0) {
        Write-Warning "$target : Unity exited with code $exitCode, but all expected zips were produced and no compiler/crash errors were found in the log - treating as success. Check $logFile if that seems wrong."
    }
    Write-Host "OK: $target complete. $($expectedZips.Count) zip(s) verified."
}

Write-Host "All platforms built for: $groupsArg"
