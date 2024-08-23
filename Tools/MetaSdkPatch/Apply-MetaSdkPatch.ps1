<#
.SYNOPSIS
    Reapplies the Meta XR Core SDK fix needed to compile on Unity 6000.5.

.DESCRIPTION
    Meta XR Core SDK 205.0.0 does not compile on Unity 6000.5. In

        Editor/BuildingBlocks/BlockData/MultiplayerBlocks/NGO/SceneListenerNGO.cs

    the version guard reads:

        #if UNITY_6000_3_OR_NEWER
            EditorUtility.EntityIdToObject(evt.instanceId)
        #else
            EditorUtility.InstanceIDToObject(evt.instanceId)
        #endif

    Unity 6000.5 made ObjectChangeEventStream's `instanceId` an error-level obsolete
    (CS0619, "use entityId instead"), so BOTH branches fail and the project drops into
    Safe Mode. Meta already fixed the identical code in the sibling file
    Editor/OVRTelemetry/OVRSceneChangeListener.cs by adding a UNITY_6000_5_OR_NEWER
    branch that uses `.entityId` — they just missed this one. This script applies the
    same branch here.

    The patch lives in Library/PackageCache, which is gitignored and is regenerated
    whenever the package re-resolves (fresh clone, cleared cache, version bump), so it
    has to be reapplied after any of those. Run this script; it is idempotent.

    Delete this whole folder once Meta ships a release that compiles on 6000.5 —
    the script will tell you when that has happened.

.EXAMPLE
    pwsh -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
#>

[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = "Stop"

if (-not $ProjectRoot) {
    # $PSScriptRoot is not bound during param default evaluation in Windows PowerShell 5.1.
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ProjectRoot = (Resolve-Path (Join-Path $here "..\..")).Path
}

$cache = Join-Path $ProjectRoot "Library\PackageCache"
if (-not (Test-Path $cache)) {
    Write-Host "No Library/PackageCache yet. Open the project in Unity once, then re-run." -ForegroundColor Yellow
    exit 0
}

$pkgs = @(Get-ChildItem $cache -Directory | Where-Object { $_.Name -like "com.meta.xr.sdk.core@*" })
if ($pkgs.Count -eq 0) {
    Write-Host "Meta XR Core SDK is not in the package cache. Nothing to patch." -ForegroundColor Yellow
    exit 0
}

$relative = "Editor\BuildingBlocks\BlockData\MultiplayerBlocks\NGO\SceneListenerNGO.cs"

# Each fix: the exact broken guard, and the same guard with a UNITY_6000_5_OR_NEWER branch.
$fixes = @(
    @{
        Broken = @"
#if UNITY_6000_3_OR_NEWER
                    ProcessGameObject(
                        EditorUtility.EntityIdToObject(createGameObjectHierarchyEvent.instanceId) as GameObject);
#else
"@
        Fixed  = @"
#if UNITY_6000_5_OR_NEWER
                    ProcessGameObject(
                        EditorUtility.EntityIdToObject(createGameObjectHierarchyEvent.entityId) as GameObject);
#elif UNITY_6000_3_OR_NEWER
                    ProcessGameObject(
                        EditorUtility.EntityIdToObject(createGameObjectHierarchyEvent.instanceId) as GameObject);
#else
"@
    },
    @{
        Broken = @"
#if UNITY_6000_3_OR_NEWER
                    ProcessGameObject(
                        EditorUtility.EntityIdToObject(changeGameObjectStructure.instanceId) as GameObject);
#else
"@
        Fixed  = @"
#if UNITY_6000_5_OR_NEWER
                    ProcessGameObject(
                        EditorUtility.EntityIdToObject(changeGameObjectStructure.entityId) as GameObject);
#elif UNITY_6000_3_OR_NEWER
                    ProcessGameObject(
                        EditorUtility.EntityIdToObject(changeGameObjectStructure.instanceId) as GameObject);
#else
"@
    }
)

$exit = 0

foreach ($pkg in $pkgs) {
    $target = Join-Path $pkg.FullName $relative
    Write-Host "`n$($pkg.Name)"

    if (-not (Test-Path $target)) {
        Write-Host "  SceneListenerNGO.cs not found - package layout changed. Check whether the patch is still needed." -ForegroundColor Yellow
        $exit = 1
        continue
    }

    # Normalise to LF so the here-strings above match regardless of checkout settings.
    $text = [System.IO.File]::ReadAllText($target).Replace("`r`n", "`n")
    $applied = 0
    $already = 0

    foreach ($fix in $fixes) {
        $broken = $fix.Broken.Replace("`r`n", "`n")
        $good   = $fix.Fixed.Replace("`r`n", "`n")

        if ($text.Contains($good)) { $already++; continue }

        if ($text.Contains($broken)) {
            $text = $text.Replace($broken, $good)
            $applied++
        }
    }

    if ($applied -gt 0) {
        [System.IO.File]::WriteAllText($target, $text)
        Write-Host "  Patched $applied site(s)." -ForegroundColor Green
    }

    if ($already -eq $fixes.Count -and $applied -eq 0) {
        Write-Host "  Already patched - nothing to do." -ForegroundColor Green
    }
    elseif ($applied + $already -ne $fixes.Count) {
        Write-Host "  Only matched $($applied + $already) of $($fixes.Count) sites." -ForegroundColor Yellow
        Write-Host "  Meta may have fixed this upstream. If the project compiles, delete Tools/MetaSdkPatch." -ForegroundColor Yellow
        $exit = 1
    }
}

Write-Host ""
exit $exit
