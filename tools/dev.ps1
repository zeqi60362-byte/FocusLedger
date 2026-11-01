<#
.SYNOPSIS
    FocusLedger 统一开发入口

.DESCRIPTION
    本机存在两个 dotnet 根目录：
      - C:\Program Files\dotnet      （只有运行时，无 SDK，且在 PATH 中抢先）
      - %USERPROFILE%\.dotnet        （用户级安装的 SDK 8.0.x）
    因此所有构建命令必须走本脚本，不能直接敲 dotnet。

.PARAMETER Action
    build | test | run | probe | clean | publish

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 build
#>
[CmdletBinding()]
param(
    [ValidateSet('build', 'test', 'run', 'probe', 'clean', 'publish', 'info')]
    [string]$Action = 'build'
)

$ErrorActionPreference = 'Stop'
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path (Join-Path $PSScriptRoot 'Probe')) { $repo = Split-Path -Parent $PSScriptRoot }

# 优先使用用户级 SDK，其次才回退到 PATH 上的 dotnet
$candidates = @(
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    'C:\Program Files\dotnet\dotnet.exe'
)

$dotnet = $null
foreach ($c in $candidates) {
    if (Test-Path $c) {
        $sdks = & $c --list-sdks 2>$null
        if (-not [string]::IsNullOrWhiteSpace($sdks)) { $dotnet = $c; break }
    }
}

if (-not $dotnet) {
    Write-Host '找不到可用的 .NET SDK。' -ForegroundColor Red
    Write-Host '请运行：' -ForegroundColor Yellow
    Write-Host '  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1'
    Write-Host '  & $env:TEMP\dotnet-install.ps1 -Channel 8.0 -NoPath'
    exit 1
}

Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray
Write-Host "sdk   : $((& $dotnet --version))" -ForegroundColor DarkGray

$probe = Join-Path $PSScriptRoot 'Probe\Probe.csproj'
$sln = Join-Path $repo 'FocusLedger.sln'

switch ($Action) {
    'info' {
        & $dotnet --info
    }
    'probe' {
        Write-Host '构建并运行 M0.2 信号探针...' -ForegroundColor Cyan
        & $dotnet run --project $probe -c Release -- 20 2000
    }
    'build' {
        if (Test-Path $sln) { & $dotnet build $sln -c Debug }
        else { Write-Host 'FocusLedger.sln 尚未创建（M1 阶段）。当前只有探针工程。' -ForegroundColor Yellow; & $dotnet build $probe -c Debug }
    }
    'test' {
        if (Test-Path $sln) { & $dotnet test $sln -c Debug --nologo }
        else { Write-Host '还没有测试工程（M1 阶段）。' -ForegroundColor Yellow }
    }
    'run' {
        $app = Join-Path $repo 'src\FocusLedger.App\FocusLedger.App.csproj'
        if (Test-Path $app) { & $dotnet run --project $app -c Debug }
        else { Write-Host '桌面应用尚未创建（M2 阶段）。' -ForegroundColor Yellow }
    }
    'publish' {
        $app = Join-Path $repo 'src\FocusLedger.App\FocusLedger.App.csproj'
        if (-not (Test-Path $app)) { Write-Host '桌面应用尚未创建（M2 阶段）。' -ForegroundColor Yellow; exit 1 }
        & $dotnet publish $app -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -o (Join-Path $repo 'dist')
    }
    'clean' {
        Get-ChildItem -Recurse $repo -Directory -Include bin, obj |
            Where-Object { $_.FullName -notmatch '\\\.git\\' } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '已清理 bin / obj' -ForegroundColor Green
    }
}

exit $LASTEXITCODE
