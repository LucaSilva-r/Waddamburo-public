# Downloads a release's native libraries (FFmpeg, vgmstream, FreeType, bgfx; built by eng/package.sh)
# into the folder dotnet build takes them from (Directory.Build.props), so the game builds and runs
# from source with only the .NET SDK. Native code changed since that release? Build the
# windows-package preset instead (BUILDING.md).
# usage: ./eng/fetch-native.ps1 [-Release vX.Y.Z]   (default: the latest release)
[CmdletBinding()]
param([string] $Release = 'latest')

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is very slow with its progress bar

$root = Split-Path -Parent $PSScriptRoot
$releases = 'https://github.com/LucaSilva-r/Waddamburo-public/releases'
$url = if ($Release -eq 'latest') { "$releases/latest/download/native-win-x64.zip" }
       else { "$releases/download/$Release/native-win-x64.zip" }
$dir = Join-Path $root 'out/build/native/win-x64/windows-package/stage/Release/bin'
$zip = Join-Path $root 'out/downloads/native-win-x64.zip'

New-Item -ItemType Directory -Force $dir, (Split-Path $zip) | Out-Null
Invoke-WebRequest $url -OutFile $zip
Expand-Archive $zip $dir -Force
Write-Host "Native libraries in $dir; now: dotnet run --project src/Waddamburo.App -- C:\path\to\USRDIR"
