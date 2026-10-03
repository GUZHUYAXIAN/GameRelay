param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw '版本格式无效' }
& (Join-Path $PSScriptRoot 'build.ps1')
$name = "GameRelay-$Version-win-x64"
$stage = Join-Path $PSScriptRoot ("artifacts\package-" + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage $name
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -LiteralPath 'build\GameRelay.exe','build\GameRelay.Core.dll' -Destination $payload
Copy-Item -LiteralPath 'src\App\GameRelay.exe.config' -Destination $payload
Copy-Item -LiteralPath 'README.md','CHANGELOG.md','THIRD_PARTY.md' -Destination $payload
if (Test-Path -LiteralPath 'LICENSE') { Copy-Item -LiteralPath 'LICENSE' -Destination $payload }
else { '项目许可证待所有者确认。此包为本地开发验收候选，尚未公开发布。' | Set-Content -LiteralPath (Join-Path $payload 'LICENSE-PENDING.txt') -Encoding UTF8 }
Copy-Item -LiteralPath 'docs' -Destination $payload -Recurse
New-Item -ItemType Directory -Path 'dist' -Force | Out-Null
$zip = Join-Path $PSScriptRoot "dist\$name.zip"
Compress-Archive -LiteralPath $payload -DestinationPath $zip -Force
$target = Join-Path $PSScriptRoot "dist\$name"
if (Test-Path -LiteralPath $target) {
    if (Test-Path -LiteralPath (Join-Path $target 'data')) { throw '已有便携目录含用户数据；ZIP 已生成，目录不覆盖。' }
    $target = Join-Path $PSScriptRoot ("dist\$name-" + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
}
Copy-Item -LiteralPath $payload -Destination $target -Recurse
Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Select-Object Algorithm,Hash,Path
Write-Output "Portable directory: $target"
