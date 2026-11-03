<#
.SYNOPSIS
    FocusLedger 备份：生成 git bundle 并复制到移动硬盘。

.DESCRIPTION
    git bundle 是一个单文件，包含完整提交历史（几百 KB），可以随时 clone 回来：
        git clone FocusLedger-2026-11-03.bundle D:\恢复目录

    本脚本做三件事：
      1. 在 dist\backup\ 生成带日期的 bundle
      2. 如果移动硬盘在线（默认 G:\000_开发软件\FocusLedger-backup），复制一份过去
      3. 打印 SHA256，两边比对

.PARAMETER Destination
    移动硬盘上的备份目录，默认 G:\000_开发软件\FocusLedger-backup

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\backup.ps1
    powershell -ExecutionPolicy Bypass -File .\tools\backup.ps1 -Destination "H:\backup"
#>
[CmdletBinding()]
param(
    [string]$Destination = 'G:\000_开发软件\FocusLedger-backup'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$local = Join-Path $repo 'dist\backup'
New-Item -ItemType Directory -Force -Path $local | Out-Null

$name = "FocusLedger-$(Get-Date -Format 'yyyy-MM-dd').bundle"
$bundle = Join-Path $local $name

Push-Location $repo
try {
    & git bundle create $bundle --all | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "git bundle 失败（退出码 $LASTEXITCODE）"
    }
}
finally {
    Pop-Location
}

Write-Host "本机: $bundle"
Write-Host ("大小: {0:N0} KB" -f ((Get-Item $bundle).Length / 1KB))

# 复制到移动硬盘（不在线就跳过，不算失败）
$copied = $false
if (Test-Path (Split-Path -Parent $Destination)) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $target = Join-Path $Destination $name
    Copy-Item $bundle $target -Force
    $copied = $true
    Write-Host "移动硬盘: $target"
}
else {
    Write-Host "移动硬盘未接入（$Destination），只做了本机备份。" -ForegroundColor Yellow
}

# 校验
$h1 = (Get-FileHash $bundle -Algorithm SHA256).Hash
Write-Host "SHA256: $h1"

if ($copied) {
    $h2 = (Get-FileHash (Join-Path $Destination $name) -Algorithm SHA256).Hash
    if ($h1 -eq $h2) {
        Write-Host "两边一致，备份完成。" -ForegroundColor Green
    }
    else {
        Write-Host "两边哈希不一致，请重新复制！" -ForegroundColor Red
        exit 1
    }
}

Write-Host ''
Write-Host '恢复方法（需要时）:' -ForegroundColor DarkGray
Write-Host "  git clone `"$bundle`" D:\恢复目录" -ForegroundColor DarkGray
