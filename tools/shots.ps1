<#
.SYNOPSIS
    Capture the running FocusLedger main window to docs/shots/*.png.

.DESCRIPTION
    The capture uses the Win32 PrintWindow API, so it does not depend on the
    window being unobstructed. The window is restored and brought to the
    foreground first unless -NoActivate is given. Visible DWM frame bounds are
    used to crop away the invisible resize border.

    UI language:
    FocusLedger stores its language in
        %LOCALAPPDATA%\FocusLedger\settings.json
    and only the running app can change it (Settings > Language). This script
    reads that file first and refuses to label a screenshot with a language the
    app is not currently using. Files are named <Name>-<lang>.png.

    To capture both languages in one run:
        powershell -ExecutionPolicy Bypass -File .\tools\shots.ps1 -Lang both
    The script captures the current language, then pauses and asks you to
    switch the app to the other language and press Enter.

.PARAMETER Lang
    auto (default) captures the language the app is currently using.
    zh or en captures that language only; if the app uses the other one, the
    script stops without writing a mislabeled file.
    both captures the current language, then prompts you to switch and
    captures the other one. Use -NoPrompt to skip the second capture.

.PARAMETER Name
    Base file name. Use the page you are looking at, for example today, report,
    plan, rules, or settings. Default: main.

.PARAMETER OutDir
    Output directory, relative to the repository root unless absolute.
    Default: docs\shots.

.PARAMETER DelayMs
    Milliseconds to wait after activating the window (and after a language
    switch) before capturing. Default: 500.

.PARAMETER NoActivate
    Do not restore or activate the window. PrintWindow may still work, but the
    screen-capture fallback can pick up whatever is on top.

.PARAMETER NoPrompt
    With -Lang both, skip the interactive second capture.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\shots.ps1

    Capture the current page in the current UI language as main-zh.png or
    main-en.png.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\shots.ps1 -Lang en -Name report

    Capture the report page. The app must already be in English.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\shots.ps1 -Lang both -Name today

    Capture today-zh.png and today-en.png, switching language in between.

.NOTES
    ASCII only. Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI, so Chinese
    comments or strings in this file would be garbled. Keep it ASCII.
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'zh', 'en', 'both')]
    [string]$Lang = 'auto',

    [string]$Name = 'main',

    [string]$OutDir = 'docs\shots',

    [int]$DelayMs = 500,

    [switch]$NoActivate,

    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repo 'FocusLedger.sln'))) {
    throw "Cannot find FocusLedger.sln under '$repo'. Keep this script in the repository's tools folder."
}

# Windows PowerShell 5.1 does not auto-load System.Drawing, so the
# [System.Drawing.Imaging.PixelFormat] literals used further down would fail
# with "Unable to find type" without these two lines.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace FocusLedgerShots
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int SW_RESTORE = 9;
        public const uint PW_RENDERFULLCONTENT = 2;
    }
}
'@

# Per-monitor DPI awareness keeps GetWindowRect and PrintWindow in physical
# pixels on scaled displays. Fall back to system awareness on older builds.
try {
    [FocusLedgerShots.Native]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
}
catch {
    try { [FocusLedgerShots.Native]::SetProcessDPIAware() | Out-Null } catch { }
}

function Get-FocusLedgerWindow {
    $candidates = @(Get-Process -Name FocusLedger -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 })

    if ($candidates.Count -eq 0) {
        $candidates = @(Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq 'FocusLedger' })
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    if ($candidates.Count -gt 1) {
        Write-Warning ("Found {0} candidate windows; using PID {1}." -f $candidates.Count, $candidates[0].Id)
    }

    return $candidates[0]
}

function Get-UiLanguage {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'FocusLedger\settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return 'zh'
    }

    try {
        $json = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Cannot read '$settingsPath': $($_.Exception.Message). Close and reopen FocusLedger, then retry."
    }

    if ($json.Language -eq 'en') {
        return 'en'
    }

    return 'zh'
}

function Test-BitmapBlank {
    param([System.Drawing.Bitmap]$Bitmap)

    $first = $Bitmap.GetPixel(0, 0).ToArgb()
    $stepX = [Math]::Max(1, [int]($Bitmap.Width / 16))
    $stepY = [Math]::Max(1, [int]($Bitmap.Height / 16))

    for ($y = 0; $y -lt $Bitmap.Height; $y += $stepY) {
        for ($x = 0; $x -lt $Bitmap.Width; $x += $stepX) {
            if ($Bitmap.GetPixel($x, $y).ToArgb() -ne $first) {
                return $false
            }
        }
    }

    return $true
}

function Save-Shot {
    param([string]$Language)

    $proc = Get-FocusLedgerWindow
    if ($null -eq $proc) {
        throw 'FocusLedger window not found. Start the app and open its panel.'
    }

    $hwnd = [IntPtr]$proc.MainWindowHandle

    if (-not $NoActivate) {
        [FocusLedgerShots.Native]::ShowWindow($hwnd, [FocusLedgerShots.Native]::SW_RESTORE) | Out-Null
        [FocusLedgerShots.Native]::SetForegroundWindow($hwnd) | Out-Null
        if ($DelayMs -gt 0) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    $wr = New-Object 'FocusLedgerShots.Native+RECT'
    if (-not [FocusLedgerShots.Native]::GetWindowRect($hwnd, [ref]$wr)) {
        throw 'GetWindowRect failed.'
    }

    $fullWidth = $wr.Right - $wr.Left
    $fullHeight = $wr.Bottom - $wr.Top
    if ($fullWidth -le 0 -or $fullHeight -le 0) {
        throw "Unexpected window size: ${fullWidth}x${fullHeight}."
    }

    # Crop to the visible frame so the PNG has no invisible resize border.
    $cropX = 0
    $cropY = 0
    $outWidth = $fullWidth
    $outHeight = $fullHeight
    $ext = New-Object 'FocusLedgerShots.Native+RECT'
    $hr = [FocusLedgerShots.Native]::DwmGetWindowAttribute(
        $hwnd,
        [FocusLedgerShots.Native]::DWMWA_EXTENDED_FRAME_BOUNDS,
        [ref]$ext,
        [System.Runtime.InteropServices.Marshal]::SizeOf($ext))

    if ($hr -eq 0) {
        $ex = $ext.Left - $wr.Left
        $ey = $ext.Top - $wr.Top
        $ew = $ext.Right - $ext.Left
        $eh = $ext.Bottom - $ext.Top

        if ($ew -gt 0 -and $eh -gt 0 -and $ex -ge 0 -and $ey -ge 0 -and
            ($ex + $ew) -le $fullWidth -and ($ey + $eh) -le $fullHeight) {
            $cropX = $ex
            $cropY = $ey
            $outWidth = $ew
            $outHeight = $eh
        }
    }

    $bmp = $null
    $graphics = $null
    $out = $null

    try {
        $bmp = New-Object System.Drawing.Bitmap($fullWidth, $fullHeight, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bmp)
        $graphics.Clear([System.Drawing.Color]::White)

        $printed = $false
        $usedScreen = $false

        $hdc = $graphics.GetHdc()
        try {
            $printed = [FocusLedgerShots.Native]::PrintWindow(
                $hwnd, $hdc, [FocusLedgerShots.Native]::PW_RENDERFULLCONTENT)
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }

        if (-not $printed) {
            $hdc = $graphics.GetHdc()
            try {
                $printed = [FocusLedgerShots.Native]::PrintWindow($hwnd, $hdc, 0)
            }
            finally {
                $graphics.ReleaseHdc($hdc)
            }
        }

        if (-not $printed) {
            Write-Warning 'PrintWindow failed; falling back to a screen capture of the window area.'
            $graphics.CopyFromScreen(
                $wr.Left, $wr.Top, 0, 0,
                (New-Object System.Drawing.Size($fullWidth, $fullHeight)))
            $usedScreen = $true
        }

        $cropRect = New-Object System.Drawing.Rectangle($cropX, $cropY, $outWidth, $outHeight)
        $out = $bmp.Clone($cropRect, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)

        if (Test-BitmapBlank $out) {
            if (-not $usedScreen) {
                Write-Warning 'PrintWindow produced a blank image; falling back to a screen capture.'
                $graphics.CopyFromScreen(
                    $wr.Left, $wr.Top, 0, 0,
                    (New-Object System.Drawing.Size($fullWidth, $fullHeight)))
                $usedScreen = $true

                $out.Dispose()
                $out = $bmp.Clone($cropRect, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            }

            if (Test-BitmapBlank $out) {
                Write-Warning 'The capture still looks blank. The window may be minimized or fully covered.'
            }
        }

        $path = Join-Path $OutDir ("{0}-{1}.png" -f $Name, $Language)
        $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

        Write-Host ("Captured {0} {1}x{2} -> {3}" -f $Language, $outWidth, $outHeight, $path) -ForegroundColor Green
        return $path
    }
    finally {
        if ($null -ne $out) { $out.Dispose() }
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $bmp) { $bmp.Dispose() }
    }
}

# ---------------------------------------------------------------------------

$currentLang = Get-UiLanguage
Write-Host ("Current UI language: {0}" -f $currentLang) -ForegroundColor DarkGray

if ($Lang -ne 'auto' -and $Lang -ne 'both' -and $Lang -ne $currentLang) {
    Write-Host ("Requested '{0}', but the running UI is '{1}'." -f $Lang, $currentLang) -ForegroundColor Yellow
    Write-Host "Switch it in Settings > Language, then run this script again. No file was written."
    exit 1
}

if (-not (Get-FocusLedgerWindow)) {
    $running = Get-Process -Name FocusLedger -ErrorAction SilentlyContinue
    if ($null -eq $running) {
        Write-Host 'FocusLedger is not running.' -ForegroundColor Yellow
        Write-Host 'Start it with: powershell -ExecutionPolicy Bypass -File .\tools\dev.ps1 run'
    }
    else {
        Write-Host 'FocusLedger is running, but its main window is hidden or minimized to the tray.' -ForegroundColor Yellow
        Write-Host 'Open the panel (double-click the tray icon), then run this script again.'
    }
    exit 1
}

$safeName = ($Name -replace '[^A-Za-z0-9._-]', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($safeName)) {
    $safeName = 'main'
}
$Name = $safeName

if (-not [System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir = Join-Path $repo $OutDir
}
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}

$targets = @()
if ($Lang -eq 'auto' -or $Lang -eq 'both') {
    $targets = @($currentLang)
}
else {
    $targets = @($Lang)
}

foreach ($language in $targets) {
    Save-Shot -Language $language | Out-Null
}

if ($Lang -eq 'both') {
    $other = if ($currentLang -eq 'zh') { 'en' } else { 'zh' }

    if ($NoPrompt) {
        Write-Host ("Skipped '{0}'. Switch language and run again with -Lang {0}." -f $other) -ForegroundColor Yellow
    }
    else {
        Write-Host ''
        Write-Host ("Captured the '{0}' set. To capture '{1}':" -f $currentLang, $other)
        Write-Host ("  1. Open Settings > Language and switch the app to '{0}'." -f $other)
        Write-Host '  2. Press Enter here when the UI has changed.'
        Write-Host "Type 'skip' and press Enter to stop."
        $answer = Read-Host

        if ($answer -ne 'skip') {
            $nowLang = Get-UiLanguage
            if ($nowLang -eq $other) {
                Save-Shot -Language $other | Out-Null
            }
            else {
                Write-Warning ("UI is still '{0}'; skipped '{1}'." -f $nowLang, $other)
            }
        }
    }
}

Write-Host 'Done.' -ForegroundColor Green
