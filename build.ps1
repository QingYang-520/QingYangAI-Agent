# ============================================================
#  青阳AI 一键构建脚本
#  用法：在【原生 PowerShell 窗口】里执行
#        cd D:\project\qingyang
#        .\build.ps1
# ============================================================

$ErrorActionPreference = "Stop"

# ---- 路径配置 ----
$ProjectDir  = "D:\project\qingyang"
$ProjectFile = "青阳AI.csproj"
$SdkDir      = "D:\android-sdk"                          # Android SDK（ASCII 路径 junction）
$JdkDir      = "C:\Program Files\Java\jdk-21.0.12"       # JDK 21
$ApkOutDir   = "D:\project\青阳AI\青阳AI\apk"             # APK 输出目录
$ZDrive      = "Z:\"                                     # 同步盘（手机存储）

Write-Host "==== 青阳AI 构建开始 ====" -ForegroundColor Cyan

# ---- 0. 从 csproj 读出本次版本号（避免手改脚本与工程不一致）----
Set-Location $ProjectDir
$csprojText = Get-Content $ProjectFile -Raw
if ($csprojText -match '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') {
    $AppVersion = $Matches[1].Trim()
} else {
    Write-Host "读不到 ApplicationDisplayVersion，请检查 csproj。" -ForegroundColor Red
    exit 1
}
Write-Host "[0/3] 版本：青阳AI-$AppVersion" -ForegroundColor Green

# ---- 1. 环境变量（确保原生 Windows 格式）----
$env:APPDATA      = "C:\Users\QingYang\AppData\Roaming"
$env:LOCALAPPDATA = "C:\Users\QingYang\AppData\Local"
$env:ProgramData  = "C:\ProgramData"
$env:USERPROFILE  = "C:\Users\QingYang"
$env:SystemDrive  = "C:"
$env:JAVA_HOME    = $JdkDir
$env:ANDROID_HOME     = $SdkDir
$env:ANDROID_SDK_ROOT = $SdkDir
if ($env:HOME -like "/*") { Remove-Item Env:\HOME -ErrorAction SilentlyContinue }

Write-Host "[1/3] 环境变量已设置" -ForegroundColor Green
Write-Host "      JAVA_HOME      = $env:JAVA_HOME"
Write-Host "      ANDROID_SDK_ROOT = $env:ANDROID_SDK_ROOT"

# ---- 2. 还原 + 构建 ----
Set-Location $ProjectDir

Write-Host "[2/3] 正在清理旧产物..." -ForegroundColor Yellow
if (Test-Path ".\obj\Release") { Remove-Item ".\obj\Release" -Recurse -Force }
if (Test-Path ".\bin\Release") { Remove-Item ".\bin\Release" -Recurse -Force }

Write-Host "[2/3] 正在还原 NuGet 包..." -ForegroundColor Yellow
dotnet restore $ProjectFile
if ($LASTEXITCODE -ne 0) { Write-Host "还原失败！" -ForegroundColor Red; exit 1 }

Write-Host "[2/3] 正在编译 Android Release..." -ForegroundColor Yellow
dotnet build $ProjectFile -f net10.0-android -c Release --no-restore -v m
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败！" -ForegroundColor Red; exit 1 }

# ---- 3. 收集 APK ----
Write-Host "[3/3] 正在收集 APK..." -ForegroundColor Yellow
$apk = Get-ChildItem -Path ".\bin\Release\net10.0-android" -Filter "*.apk" -Recurse -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -like "*Signed*" } |
       Select-Object -First 1

if (-not $apk) {
    $apk = Get-ChildItem -Path ".\bin\Release\net10.0-android" -Filter "*.apk" -Recurse -ErrorAction SilentlyContinue |
           Select-Object -First 1
}

if ($apk) {
    if (-not (Test-Path $ApkOutDir)) { New-Item -ItemType Directory -Path $ApkOutDir -Force | Out-Null }
    $target = Join-Path $ApkOutDir "青阳AI-$AppVersion.apk"
    Copy-Item $apk.FullName $target -Force
    Write-Host "构建成功！" -ForegroundColor Green
    Write-Host "APK: $target" -ForegroundColor Green
    Write-Host "大小: $([math]::Round((Get-Item $target).Length / 1MB, 1)) MB" -ForegroundColor Green

    # 同步一份到 Z 盘（手机存储），失败不影响构建结果
    if (Test-Path $ZDrive) {
        try {
            Copy-Item $target (Join-Path $ZDrive "青阳AI-$AppVersion.apk") -Force
            Write-Host "已同步到 $ZDrive" -ForegroundColor Green
        } catch {
            Write-Host "Z 盘同步失败（不影响构建）：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Z 盘不可用，跳过同步。" -ForegroundColor Yellow
    }
} else {
    Write-Host "未找到 APK，请检查编译输出。" -ForegroundColor Red
    exit 1
}
