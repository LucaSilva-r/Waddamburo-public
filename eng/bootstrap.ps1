[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'Sanitize')]
    [string] $Configuration = 'Debug',

    [switch] $ManagedOnly,
    [switch] $NativeOnly,
    [switch] $WithFFmpeg
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ManagedOnly -and $NativeOnly) {
    throw '-ManagedOnly and -NativeOnly cannot be combined.'
}

if ($WithFFmpeg -and $ManagedOnly) {
    throw '-WithFFmpeg cannot be combined with -ManagedOnly.'
}

if (-not $IsWindows) {
    throw 'bootstrap.ps1 supports Windows; use bootstrap.sh on Linux.'
}

if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw "Phase 2 supports only Windows x64, found $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

function Assert-Command {
    param([Parameter(Mandatory)][string] $Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

try {
    if (-not $NativeOnly) {
        Assert-Command dotnet
        $dotnetVersion = (& dotnet --version).Trim()
        if (-not $dotnetVersion.StartsWith('10.', [StringComparison]::Ordinal)) {
            throw ".NET SDK 10 is required, found $dotnetVersion."
        }

        $managedConfiguration = $Configuration
        if ($Configuration -eq 'Sanitize') {
            $managedConfiguration = 'Debug'
        }

        & dotnet restore Waddamburo.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Managed restore failed.' }

        $env:CI = 'true'
        & dotnet build Waddamburo.slnx --configuration $managedConfiguration --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Managed build failed.' }

        & dotnet test Waddamburo.slnx --configuration $managedConfiguration --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed.' }
    }

    if (-not $ManagedOnly) {
        Assert-Command cmake
        Assert-Command ninja
        Assert-Command cl

        $nativePreset = "windows-$($Configuration.ToLowerInvariant())"
        Push-Location native
        try {
            & cmake --preset $nativePreset
            if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }

            & cmake --build --preset $nativePreset
            if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }

            & ctest --preset $nativePreset
            if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }

            if ($WithFFmpeg) {
                Assert-Command bash
                Assert-Command make
                & cmake --preset windows-ffmpeg
                if ($LASTEXITCODE -ne 0) { throw 'FFmpeg configure failed.' }

                & cmake --build --preset windows-ffmpeg
                if ($LASTEXITCODE -ne 0) { throw 'FFmpeg build or audit failed.' }
            }
        }
        finally {
            Pop-Location
        }
    }

    Write-Host "Bootstrap completed successfully ($Configuration)."
}
finally {
    Pop-Location
}
