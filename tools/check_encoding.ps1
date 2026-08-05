# 源码文件 UTF-8 合法性检查。
#
# 起因：一次编辑把 local_outgoing_service.py 里的中文常量截断成了非法 UTF-8
# 序列（"冲" 丢了尾字节），整个本地推理服务因此 SyntaxError 起不来，而且直到
# 手动运行 Python 用例才被发现。这个检查放在 CI 最前面，几秒内就能挡住同类问题。

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# throwOnInvalidBytes = true：遇到非法序列直接抛异常，而不是静默替换成 U+FFFD。
$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$extensions = '.py', '.cs', '.xaml', '.md', '.ps1', '.js', '.css', '.html', '.json', '.yml', '.csproj', '.sln'
$bad = @()

Push-Location $repoRoot
try {
    # core.quotepath=false + UTF-8 输出编码：否则 git 会把非 ASCII 文件名转义成
    # "\346\270\270..." 这种带引号的八进制串，Path API 会因非法字符直接抛异常。
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    try { $tracked = git -c core.quotepath=false ls-files }
    finally { [Console]::OutputEncoding = $previousEncoding }

    foreach ($relative in $tracked) {
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }

        $full = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $full)) { continue }
        if ($extensions -notcontains [System.IO.Path]::GetExtension($full)) { continue }

        try {
            $bytes = [System.IO.File]::ReadAllBytes($full)
            $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
            [void]$strictUtf8.GetString($bytes)

            # Windows PowerShell 5.1 把没有 BOM 的 .ps1 当作系统 ANSI 代码页解码，
            # 中文注释会被解成乱码并直接触发 ParserError。含非 ASCII 的脚本必须带 BOM。
            if ([System.IO.Path]::GetExtension($relative) -eq '.ps1' -and -not $hasBom) {
                foreach ($byte in $bytes) {
                    if ($byte -gt 127) {
                        $bad += "$relative : 含非 ASCII 字符的 PowerShell 脚本必须保存为 UTF-8 with BOM"
                        break
                    }
                }
            }
        }
        catch {
            $bad += "$relative : $($_.Exception.Message)"
        }
    }
}
finally { Pop-Location }

if ($bad.Count -gt 0) {
    Write-Host '发现非法 UTF-8 源码文件：' -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "源码编码检查通过（UTF-8）。" -ForegroundColor Green
