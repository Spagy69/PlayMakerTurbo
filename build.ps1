# Builds the installer, the runtime, the profiler mod and the addon mod and puts a ready release into dist\.
#
#   .\build.ps1                                   finds the game through Steam
#   .\build.ps1 -GamePath "E:\Games\My Winter Car"
#
# Needs the .NET SDK, Visual Studio 2022 (MSBuild with the Unity Full v3.5 reference profile) and an
# installed copy of My Winter Car with MSCLoader. The game files are only read, never changed.

param([string]$GamePath)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-Game {
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $item = Get-ItemProperty $key -ErrorAction SilentlyContinue
        $steam = if ($item.SteamPath) { $item.SteamPath } else { $item.InstallPath }
        if (-not $steam) { continue }
        $libraries = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $libraries += Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
        }
        foreach ($lib in $libraries) {
            $game = Join-Path $lib 'steamapps\common\My Winter Car'
            if (Test-Path (Join-Path $game 'mywintercar_Data\Managed\PlayMaker.dll')) { return $game }
        }
    }
    return $null
}

if (-not $GamePath) { $GamePath = Find-Game }
if (-not $GamePath) { throw 'Could not find My Winter Car. Pass it with -GamePath.' }
$managed = Join-Path $GamePath 'mywintercar_Data\Managed'
if (-not (Test-Path (Join-Path $managed '0Harmony.dll'))) { throw "No 0Harmony.dll in $managed. Install MSCLoader first." }
Write-Host "Game: $GamePath"

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio 2022.' }

# 1. Installer
dotnet build (Join-Path $root 'Patcher\Patcher.csproj') -c Release -v q -nologo
if ($LASTEXITCODE) { throw 'Installer build failed.' }
$installerDir = Join-Path $root 'Patcher\bin\Release\net48'

# 2. Patch a copy of the original PlayMaker.dll. If Turbo is already installed, the original is the .orig backup.
$work = Join-Path $root 'obj\playmaker'
New-Item -ItemType Directory $work -Force | Out-Null
$original = Join-Path $managed 'PlayMaker.dll.orig'
if (-not (Test-Path $original)) { $original = Join-Path $managed 'PlayMaker.dll' }
Get-ChildItem $work -File | ForEach-Object { Remove-Item $_.FullName }
Copy-Item $original (Join-Path $work 'PlayMaker.dll')
& (Join-Path $installerDir 'PlayMakerTurbo Installer.exe') $work --patch-only | Write-Host
if ($LASTEXITCODE) { throw 'Patching the copy of PlayMaker.dll failed.' }

# 3. Runtime, compiled against the patched copy
& $msbuild (Join-Path $root 'Runtime\PlayMakerTurbo.csproj') -p:Configuration=Release "-p:ManagedPath=$managed" "-p:PlayMakerPath=$work\PlayMaker.dll" -v:m -nologo
if ($LASTEXITCODE) { throw 'Runtime build failed.' }

# 4. Profiler mod (optional tool, works with and without Turbo installed)
& $msbuild (Join-Path $root 'Profiler\MWCFsmProfiler.csproj') -p:Configuration=Release "-p:ManagedPath=$managed" -v:m -nologo
if ($LASTEXITCODE) { throw 'Profiler build failed.' }

# 5. Addon mod (fixes in the game's own scripts, independent of the PlayMaker patch)
& $msbuild (Join-Path $root 'Addon\PlayMakerTurboAddon.csproj') -p:Configuration=Release "-p:ManagedPath=$managed" -v:m -nologo
if ($LASTEXITCODE) { throw 'Addon build failed.' }

# 6. Release folder
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory $dist -Force | Out-Null
foreach ($name in 'PlayMakerTurbo Installer.exe', 'PlayMakerTurbo Installer.exe.config', 'Mono.Cecil.dll', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Pdb.dll', 'Mono.Cecil.Rocks.dll') {
    Copy-Item (Join-Path $installerDir $name) $dist -Force
}
Copy-Item (Join-Path $root 'Runtime\bin\Release\PlayMakerTurbo.dll') $dist -Force
foreach ($name in 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $root $name) $dist -Force }
$profilerDist = Join-Path $dist 'Profiler'
New-Item -ItemType Directory $profilerDist -Force | Out-Null
Copy-Item (Join-Path $root 'Profiler\bin\Release\MWCFsmProfiler.dll') $profilerDist -Force
Copy-Item (Join-Path $root 'Profiler\README.md') $profilerDist -Force
$addonDist = Join-Path $dist 'Addon'
New-Item -ItemType Directory $addonDist -Force | Out-Null
Copy-Item (Join-Path $root 'Addon\bin\Release\PlayMakerTurboAddon.dll') $addonDist -Force
Copy-Item (Join-Path $root 'Addon\README.md') $addonDist -Force

Write-Host "Done. The release is in $dist"
