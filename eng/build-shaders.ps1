$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$slangVersion = "2026.18"
$slangSha256 = "6ffa4827b519fd0a85b38407049d87ab0c1f045fe2289cb1e6831f965169f8a1"
$dxcVersion = "1.9.2602.24"
$dxcSha256 = "cf658aacf070d3045e31b8f1f8a696c2945f37c1095019481ef7c513368db3b4"
$repoRoot = Split-Path -Parent $PSScriptRoot
$archive = Join-Path $repoRoot "out/downloads/slang-$slangVersion-windows-x86_64.zip"
$dxcArchive = Join-Path $repoRoot "out/downloads/dxc-$dxcVersion-windows-x86_64.zip"
$toolRoot = Join-Path $repoRoot "out/tools/slang-$slangVersion-windows-x86_64"
$dxcRoot = Join-Path $repoRoot "out/tools/dxc-$dxcVersion-windows-x86_64"
$shaderRoot = Join-Path $repoRoot "src/Waddamburo.Platform.Sdl/Shaders"
$outputRoot = Join-Path $shaderRoot "Compiled"

New-Item -ItemType Directory -Force (Split-Path -Parent $archive), $toolRoot, $dxcRoot, $outputRoot | Out-Null
if (-not (Test-Path $archive)) {
    Invoke-WebRequest `
        -Uri "https://github.com/shader-slang/slang/releases/download/v$slangVersion/slang-$slangVersion-windows-x86_64.zip" `
        -OutFile $archive
}
if ((Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant() -ne $slangSha256) {
    throw "Slang archive checksum mismatch: $archive"
}
if (-not (Test-Path $dxcArchive)) {
    Invoke-WebRequest `
        -Uri "https://github.com/microsoft/DirectXShaderCompiler/releases/download/v$dxcVersion/dxc_2026_05_27.zip" `
        -OutFile $dxcArchive
}
if ((Get-FileHash -Algorithm SHA256 $dxcArchive).Hash.ToLowerInvariant() -ne $dxcSha256) {
    throw "DXC archive checksum mismatch: $dxcArchive"
}

$slangc = Join-Path $toolRoot "bin/slangc.exe"
if (-not (Test-Path $slangc)) {
    Expand-Archive -Path $archive -DestinationPath $toolRoot -Force
}
$dxc = Join-Path $dxcRoot "bin/x64/dxc.exe"
if (-not (Test-Path $dxc)) {
    Expand-Archive -Path $dxcArchive -DestinationPath $dxcRoot -Force
}

& $slangc (Join-Path $shaderRoot "quad.vert.glsl") -entry main -stage vertex -target spirv -profile glsl_450 -warnings-as-errors all -o (Join-Path $outputRoot "quad.vert.spv")
if ($LASTEXITCODE -ne 0) { throw "Vertex SPIR-V compilation failed." }
& $slangc (Join-Path $shaderRoot "quad.frag.glsl") -entry main -stage fragment -target spirv -profile glsl_450 -warnings-as-errors all -o (Join-Path $outputRoot "quad.frag.spv")
if ($LASTEXITCODE -ne 0) { throw "Fragment SPIR-V compilation failed." }
& $dxc -WX -E main -T vs_6_0 -Fo (Join-Path $outputRoot "quad.vert.dxil") (Join-Path $shaderRoot "quad.vert.hlsl")
if ($LASTEXITCODE -ne 0) { throw "Vertex DXIL compilation failed." }
& $dxc -WX -E main -T ps_6_0 -Fo (Join-Path $outputRoot "quad.frag.dxil") (Join-Path $shaderRoot "quad.frag.hlsl")
if ($LASTEXITCODE -ne 0) { throw "Fragment DXIL compilation failed." }
foreach ($shader in @("don.vert", "don.frag", "don-post.vert", "don-post.frag")) {
    $profile = if ($shader.EndsWith(".vert")) { "vs_6_0" } else { "ps_6_0" }
    & $dxc -WX -E main -T $profile -Fo (Join-Path $outputRoot "$shader.dxil") (Join-Path $shaderRoot "$shader.hlsl")
    if ($LASTEXITCODE -ne 0) { throw "Don shader compilation failed: $shader" }
}

Write-Host "Built SPIR-V with Slang $slangVersion and DXIL with DXC $dxcVersion."
