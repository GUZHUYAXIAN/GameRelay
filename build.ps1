param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows .NET Framework 4.8 开发环境或系统 C# 编译器。' }
New-Item -ItemType Directory -Path 'build' -Force | Out-Null
$sources = @(Get-ChildItem 'src\Core\*.cs','src\Infrastructure\*.cs' | ForEach-Object FullName)
& $compiler /nologo /target:library /optimize+ /out:build/GameRelay.Core.dll /r:System.Web.Extensions.dll /r:System.Core.dll @sources
if ($LASTEXITCODE -ne 0) { throw '核心编译失败' }
if (Test-Path 'src\App\Program.cs') {
    $appSources = @(Get-ChildItem 'src\App\*.cs' | ForEach-Object FullName)
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:src/App/app.manifest /out:build/GameRelay.exe /r:build/GameRelay.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll @appSources
    if ($LASTEXITCODE -ne 0) { throw 'GUI 编译失败' }
}
if ($Test) {
    & $compiler /nologo /target:exe /platform:x64 /out:build/Simulation.exe (Join-Path $PSScriptRoot 'tests\Simulation.cs')
    if ($LASTEXITCODE -ne 0) { throw '模拟器编译失败' }
    & $compiler /nologo /target:exe /platform:x64 /out:build/MfaFixture.exe (Join-Path $PSScriptRoot 'tests\Simulation.cs') (Join-Path $PSScriptRoot 'tests\MfaMetadata.cs')
    if ($LASTEXITCODE -ne 0) { throw '客户端元信息夹具编译失败' }
    & $compiler /nologo /target:exe /platform:x64 /out:build/GameRelay.Tests.exe /r:build/GameRelay.Core.dll /r:System.Core.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
    & '.\build\GameRelay.Tests.exe'
    if ($LASTEXITCODE -ne 0) { throw '测试失败' }
    & $compiler /nologo /target:exe /platform:x64 /out:build/Diagnostics.exe /r:build/GameRelay.Core.dll /r:System.Core.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'tools\Diagnostics.cs')
    if ($LASTEXITCODE -ne 0) { throw '诊断工具编译失败' }
}
