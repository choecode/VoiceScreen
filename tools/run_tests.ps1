# VoiceScreen 全量测试入口。
#
# 两套测试的工作目录要求不同：Python 用例通过 sys.path 直接引入 LocalService 里的
# 模块，unittest discover 必须以 tests/python 同时作为 start 和 top-level 目录，
# 否则会报 "Start directory is not importable"。这个脚本把两边都封装好，
# 本地和 CI 走同一条命令，避免出现"测试存在但从没被执行过"的情况。

[CmdletBinding()]
param(
    [switch]$DotnetOnly,
    [switch]$PythonOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failed = @()

if (-not $PythonOnly) {
    Write-Host '=== dotnet test ===' -ForegroundColor Cyan
    Push-Location $repoRoot
    try {
        dotnet test VoiceScreen.sln --nologo
        if ($LASTEXITCODE -ne 0) { $failed += 'dotnet test' }
    }
    finally { Pop-Location }
}

if (-not $DotnetOnly) {
    Write-Host '=== python unittest ===' -ForegroundColor Cyan
    $pythonTests = Join-Path $repoRoot 'tests/python'
    Push-Location $pythonTests
    try {
        python -m unittest discover -s . -t . -v
        if ($LASTEXITCODE -ne 0) { $failed += 'python unittest' }
    }
    finally { Pop-Location }
}

if ($failed.Count -gt 0) {
    Write-Host ("失败：{0}" -f ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

Write-Host '全部测试通过。' -ForegroundColor Green
