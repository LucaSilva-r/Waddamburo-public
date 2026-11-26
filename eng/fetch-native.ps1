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
# The release keeps native-win-x64.zip inside Waddamburo.sources-and-licenses.zip (with the licenses and FFmpeg sources).
$bundle = 'Waddamburo.sources-and-licenses.zip'
$url = if ($Release -eq 'latest') { "$releases/latest/download/$bundle" }
       else { "$releases/download/$Release/$bundle" }
$dir = Join-Path $root 'out/build/native/win-x64/windows-package/stage/Release/bin'
$downloads = Join-Path $root 'out/downloads'
$zip = Join-Path $downloads 'native-win-x64.zip'

New-Item -ItemType Directory -Force $dir, $downloads | Out-Null
try {
    Invoke-WebRequest $url -OutFile (Join-Path $downloads $bundle)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $downloads $bundle))
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('native-win-x64.zip'), $zip, $true) }
    finally { $archive.Dispose() }
} catch {
    # Releases up to v0.32.x attached native-win-x64.zip on its own.
    Invoke-WebRequest ($url -replace [regex]::Escape($bundle), 'native-win-x64.zip') -OutFile $zip
}
Expand-Archive $zip $dir -Force
Write-Host "Native libraries in $dir; now: dotnet run --project src/Waddamburo.App -- C:\path\to\USRDIR"
