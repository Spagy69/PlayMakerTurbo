# Packs the output of build.ps1 (dist\) into the release zips in release\.
#
#   .\build.ps1
#   .\package.ps1 -Version 1.2.0-beta
#
# players   Turbo installer + addon + PLAYERS.md as README.md
# full      everything in dist\
# turbo     Turbo installer only
# addon     addon mod only
# profiler  profiler mod only

param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$out = Join-Path $root 'release'
if (-not (Test-Path (Join-Path $dist 'PlayMakerTurbo Installer.exe'))) { throw 'dist\ is empty. Run build.ps1 first.' }

$turboFiles = 'PlayMakerTurbo Installer.exe', 'PlayMakerTurbo Installer.exe.config', 'PlayMakerTurbo.dll',
    'Mono.Cecil.dll', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Pdb.dll', 'Mono.Cecil.Rocks.dll'
$legal = 'LICENSE', 'THIRD-PARTY-NOTICES.md'

New-Item -ItemType Directory $out -Force | Out-Null
$stage = Join-Path $root 'obj\package'

# Copies the listed files (paths relative to dist\, or absolute) into a fresh folder and zips it.
function New-Package([string]$name, [hashtable]$files) {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    foreach ($target in $files.Keys) {
        $source = $files[$target]
        if (-not [IO.Path]::IsPathRooted($source)) { $source = Join-Path $dist $source }
        $destination = Join-Path $stage $target
        New-Item -ItemType Directory (Split-Path $destination) -Force | Out-Null
        Copy-Item $source $destination
    }
    $zip = Join-Path $out "PlayMakerTurbo-$Version-$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "$zip"
}

function Map([string[]]$names) {
    $map = @{}
    foreach ($n in $names) { $map[$n] = $n }
    return $map
}

$players = Map ($turboFiles + $legal)
$players['README.md'] = Join-Path $root 'PLAYERS.md'
$players['Addon\PlayMakerTurboAddon.dll'] = 'Addon\PlayMakerTurboAddon.dll'
New-Package 'players' $players

$full = @{}
foreach ($file in Get-ChildItem $dist -Recurse -File) { $rel = $file.FullName.Substring($dist.Length + 1); $full[$rel] = $rel }
New-Package 'full' $full

$turbo = Map ($turboFiles + $legal)
$turbo['README.md'] = 'README.md'
New-Package 'turbo' $turbo

New-Package 'addon' @{ 'PlayMakerTurboAddon.dll' = 'Addon\PlayMakerTurboAddon.dll'; 'README.md' = 'Addon\README.md'; 'LICENSE' = 'LICENSE' }

New-Package 'profiler' @{ 'MWCFsmProfiler.dll' = 'Profiler\MWCFsmProfiler.dll'; 'README.md' = 'Profiler\README.md'; 'LICENSE' = 'LICENSE' }

Remove-Item $stage -Recurse -Force
