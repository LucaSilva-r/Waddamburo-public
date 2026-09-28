# Compiles the bgfx .sc shaders with the pinned shaderc from the native bgfx build. On Windows this
# adds the D3D11 bytecode (s_5_0, via the Windows SDK compiler) to the backends build-shaders.sh
# produces on Linux; it rebuilds those too so either host can regenerate the full set.
param([string]$BgfxPrefix)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $BgfxPrefix) {
    $BgfxPrefix = Join-Path $repoRoot "out/build/native/win-x64/windows-debug/bgfx/prefix"
}
$shaderc = Join-Path $BgfxPrefix "bin/shaderc.exe"
$shaderRoot = Join-Path $repoRoot "src/Waddamburo.Platform.Sdl/Shaders/Bgfx"
$outputRoot = Join-Path $repoRoot "src/Waddamburo.Platform.Sdl/Shaders/Compiled"
if (-not (Test-Path $shaderc)) {
    throw "shaderc not found at $shaderc; build native with -DWADDAMBURO_BUILD_BGFX=ON first."
}
New-Item -ItemType Directory -Force $outputRoot | Out-Null

$shaders = @{
    "vs_quad" = "quad"; "fs_quad" = "quad"; "fs_mask" = "quad"
    "vs_don" = "don"; "fs_don" = "don"
    "vs_don_post" = "post"; "fs_don_post" = "post"
}
# token, profile, platform
$targets = @(
    @("dx11", "s_5_0", "windows"),
    @("spirv", "spirv", "linux"),
    @("glsl", "330", "linux"),
    @("essl", "300_es", "android"),
    @("metal", "metal", "osx")
)
foreach ($name in $shaders.Keys) {
    $type = if ($name.StartsWith("vs_")) { "vertex" } else { "fragment" }
    $varying = Join-Path $shaderRoot "$($shaders[$name]).varying.def.sc"
    foreach ($target in $targets) {
        & $shaderc -f (Join-Path $shaderRoot "$name.sc") -o (Join-Path $outputRoot "$name.$($target[0]).bin") `
            --type $type --varyingdef $varying -i (Join-Path $BgfxPrefix "include/bgfx") `
            --platform $target[2] -p $target[1] -O 3
        if ($LASTEXITCODE -ne 0) { throw "shaderc failed for $name ($($target[0]))." }
    }
}
Write-Output "Built bgfx shaders (dx11, spirv, glsl, essl, metal)."
