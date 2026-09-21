# ============================================================
#  青阳AI —— 提交快照脚本
#  规则：每次改完代码 / 打包前必做
#        在 Z:\git提交\年-月-日\ 下新建 rc{当日序号}\，把工程源码复制进去
#  用法：原生 PowerShell 窗口执行
#        powershell -ExecutionPolicy Bypass -File "D:\project\QIngYangAI\git提交.ps1"
# ============================================================

$ErrorActionPreference = "Stop"

# ---- 路径配置 ----
$SourceDir = "D:\project\QIngYangAI"
$RootDir   = "Z:\git" + [char]0x63D0 + [char]0x4EA4        # Z:\git提交

# ---- 不复制的东西（构建产物 / 工具目录 / SDK / 归档）----
# 注意：.git 里存着 GitHub 访问令牌，绝不进快照
$SkipDirs = @('bin','obj','.vs','android-sdk','apk','.workbuddy','.workbuddy-ai','.zcode','node_modules','dsh','.git','csproj')
$SkipFiles = @('_tok.txt')

# ---- 1. 当天目录 年-月-日 ----
$today  = Get-Date -Format "yyyy-MM-dd"
$dayDir = Join-Path $RootDir $today
if (-not (Test-Path -LiteralPath $dayDir)) {
    New-Item -ItemType Directory -Path $dayDir -Force | Out-Null
}

# ---- 2. 算当日下一个 rc 序号（rc1 起）----
$maxRc = 0
Get-ChildItem -LiteralPath $dayDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Name -match '^rc(\d+)$') {
        $n = [int]$Matches[1]
        if ($n -gt $maxRc) { $maxRc = $n }
    }
}
$rcName = "rc" + ($maxRc + 1)
$dstDir = Join-Path $dayDir $rcName

# ---- 3. 复制工程源码 ----
New-Item -ItemType Directory -Path $dstDir -Force | Out-Null

Get-ChildItem -LiteralPath $SourceDir -Force | Where-Object {
    if ($SkipDirs -contains $_.Name) { return $false }
    if (-not $_.PSIsContainer -and $SkipFiles -contains $_.Name) { return $false }
    if (-not $_.PSIsContainer -and $_.Extension -eq '.user') { return $false }
    return $true
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $dstDir -Recurse -Force
}

# ---- 4. 汇报 ----
$csproj = Join-Path $dstDir "青阳AI.csproj"
$ver = "未知"
if (Test-Path -LiteralPath $csproj) {
    $txt = Get-Content -LiteralPath $csproj -Raw
    if ($txt -match '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') { $ver = $Matches[1].Trim() }
}
$allFiles = Get-ChildItem -LiteralPath $dstDir -Recurse -File -Force
$sizeMb = [math]::Round(($allFiles | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "==== 提交快照完成 ====" -ForegroundColor Cyan
Write-Host "  位置   : $dstDir" -ForegroundColor Green
Write-Host "  版本   : 青阳AI-$ver" -ForegroundColor Green
Write-Host "  文件数 : $($allFiles.Count)   体积: $sizeMb MB" -ForegroundColor Green
Write-Host ""
