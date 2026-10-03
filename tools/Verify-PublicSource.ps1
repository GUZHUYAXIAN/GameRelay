param([switch]$RequireLicense)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$relativeFiles = @('.gitignore','README.md','CONTRIBUTING.md','CHANGELOG.md','THIRD_PARTY.md','build.ps1','package.ps1')
if (Test-Path -LiteralPath (Join-Path $project 'LICENSE')) { $relativeFiles += 'LICENSE' }
foreach ($folder in @('src','tests','tools','docs','examples')) {
    $relativeFiles += Get-ChildItem -LiteralPath (Join-Path $project $folder) -File -Recurse | ForEach-Object { $_.FullName.Substring($project.Length + 1) }
}
$findings = @()
foreach ($relative in $relativeFiles) {
    $file = Join-Path $project $relative
    $text = [IO.File]::ReadAllText($file)
    $patterns = @('gh[pousr]_[A-Za-z0-9]{30,}', 'github_pat_[A-Za-z0-9_]{30,}', 'sk-proj-[A-Za-z0-9_-]{20,}', '(?i)(password|api_key|token|cookie|cdk)\s*[=:]\s*["''][A-Za-z0-9_+/=-]{20,}')
    foreach ($pattern in $patterns) { if ($text -match $pattern) { $findings += "$relative : credential-shaped text" } }
    if ($text -match '(?i)[A-Z]:\\Users\\[^\\\s]+\\') { $findings += "$relative : absolute user profile path" }
    if ([IO.Path]::GetExtension($file) -match '^\.(exe|dll|zip|png|jpg|log|bak)$') { $findings += "$relative : binary or private artifact" }
}
if ($findings.Count) { $findings | ForEach-Object { Write-Output $_ }; throw '公开源文件检查未通过；仅报告文件名，不回显疑似凭据。' }
$relativeFiles | Sort-Object | ForEach-Object { Write-Output $_ }
Write-Output "Source allowlist checked: $($relativeFiles.Count) files."
if ($RequireLicense -and -not (Test-Path -LiteralPath (Join-Path $project 'LICENSE'))) { throw '项目许可证尚未确认，禁止公开上传。' }
Write-Output 'Before push, additionally inspect staged files, full pending Git history, verified account, remote and repository visibility.'
