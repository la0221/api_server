# ============================================================
# AIVision 可攜式版本打包腳本
# ============================================================
# 用途：產生無需安裝的獨立部署版本
# 使用方式：在 PowerShell 中執行 .\build-portable.ps1
# ============================================================

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = ".\publish\AIVision-Portable"
)

$ErrorActionPreference = "Stop"

# 取得腳本所在目錄
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $ScriptDir "AIVision.Presentation.Wpf"
$PublishDir = Join-Path $ScriptDir $OutputDir

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  AIVision 可攜式版本打包工具" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: 清理舊的發布目錄
Write-Host "[1/5] 清理舊的發布目錄..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

# Step 2: 還原 NuGet 套件
Write-Host "[2/5] 還原 NuGet 套件..." -ForegroundColor Yellow
dotnet restore "$ProjectDir\AIVision.Presentation.Wpf.csproj" --runtime win-x64
if ($LASTEXITCODE -ne 0) {
    Write-Host "還原失敗！" -ForegroundColor Red
    exit 1
}

# Step 3: 發布專案（獨立部署）
Write-Host "[3/5] 發布專案（獨立部署模式）..." -ForegroundColor Yellow
dotnet publish "$ProjectDir\AIVision.Presentation.Wpf.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "發布失敗！" -ForegroundColor Red
    exit 1
}

# Step 4: 複製額外必要檔案
Write-Host "[4/5] 複製額外必要檔案..." -ForegroundColor Yellow

# 建立必要子目錄
$DataDir = Join-Path $PublishDir "data"
$LogsDir = Join-Path $PublishDir "logs"
$ConfigsDir = Join-Path $PublishDir "configs"
$ProjectsDir = Join-Path $PublishDir "Projects"

New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
New-Item -ItemType Directory -Path $ConfigsDir -Force | Out-Null
New-Item -ItemType Directory -Path $ProjectsDir -Force | Out-Null

# 複製範例專案（如果有）
$SourceProjectsDir = Join-Path $ProjectDir "Projects"
if (Test-Path $SourceProjectsDir) {
    Write-Host "  - 複製範例專案..." -ForegroundColor Gray
    Copy-Item -Path "$SourceProjectsDir\*" -Destination $ProjectsDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 複製設定檔（從 Presentation.Wpf 專案的 configs 目錄）
$SourceConfigsDir = Join-Path $ProjectDir "configs"
if (Test-Path $SourceConfigsDir) {
    Write-Host "  - 複製設定檔..." -ForegroundColor Gray
    Copy-Item -Path "$SourceConfigsDir\*" -Destination $ConfigsDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 移除開發環境設定檔（不應發布）
$DevConfig = Join-Path $PublishDir "appsettings.Development.json"
if (Test-Path $DevConfig) {
    Write-Host "  - 移除開發環境設定檔..." -ForegroundColor Gray
    Remove-Item -Path $DevConfig -Force
}

# 注意：IDS Peak SDK 與 LTS 光源控制器 DLL 已在 csproj 中設定自動複製到輸出目錄
# 注意：AI 模型檔案較大，不打包進可攜版，透過 appsettings.json 設定本地路徑即可

# Step 5: 建立啟動批次檔
Write-Host "[5/5] 建立啟動檔案..." -ForegroundColor Yellow

$LauncherContent = @"
@echo off
cd /d "%~dp0"
start "" "AIVision.exe"
"@
Set-Content -Path (Join-Path $PublishDir "啟動AIVision.bat") -Value $LauncherContent -Encoding Default

# 建立 README
$ReadmeContent = @"
============================================================
  AIVision 智慧視覺檢測系統 - 可攜式版本
============================================================

【系統需求】
- 作業系統：Windows 10/11 (64-bit)
- 無需安裝 .NET Runtime（已內含）

【使用方式】
1. 解壓縮整個資料夾到目標電腦
2. 雙擊「啟動AIVision.bat」或直接執行「AIVision.exe」

【目錄結構】
├── AIVision.exe          主程式
├── appsettings.json      系統設定檔
├── configs/              設定檔目錄（相機設定等）
├── data/                 資料庫目錄
├── logs/                 日誌目錄
└── Projects/             專案資料夾（一鍵載入專案設定）

【注意事項】
1. 首次執行可能需要允許 Windows 防火牆
2. 相機/PLC 連線需確保網路設定正確
3. AI 模型路徑請在 appsettings.json 中設定本地路徑
4. 如遇問題請查看 logs 目錄下的日誌檔案
5. 可透過「載入專案」按鈕快速切換不同專案設定

【版本資訊】
建置日期：$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
============================================================
"@
Set-Content -Path (Join-Path $PublishDir "README.txt") -Value $ReadmeContent -Encoding UTF8

# 完成
Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  打包完成！" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "發布目錄: $PublishDir" -ForegroundColor Cyan
Write-Host ""

# 計算資料夾大小
$Size = (Get-ChildItem -Path $PublishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "總大小: $([math]::Round($Size, 2)) MB" -ForegroundColor Cyan
Write-Host ""
Write-Host "可將整個 AIVision-Portable 資料夾複製到其他電腦使用" -ForegroundColor Yellow
Write-Host ""
