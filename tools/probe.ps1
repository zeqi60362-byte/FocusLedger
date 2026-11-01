<#
.SYNOPSIS
    FocusLedger M0 信号探针

.DESCRIPTION
    在写任何正式代码之前，先证明这台电脑能采到关键信号、且成本可接受。
    只读、不落盘、不联网。运行期间不会记录任何按键内容或画面。

    采集：
      1. 前景窗口的进程名与窗口标题
      2. 系统空闲秒数（GetLastInputInfo）
      3. 前景窗口的画面变化（9x8 灰度 dHash，64 位汉明距离）

    未覆盖（需在 .NET 版探针中验证）：
      4. 媒体播放状态（SMTC / WinRT）
      5. 锁屏与休眠事件（WTS / PowerModeChanged）

.PARAMETER Seconds
    运行时长，默认 20 秒。

.PARAMETER IntervalMs
    采样间隔，默认 1000 毫秒。

.PARAMETER OutFile
    可选，把每个 tick 的结果写成 CSV。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\probe.ps1 -Seconds 60
#>
[CmdletBinding()]
param(
    [int]$Seconds = 20,
    [int]$IntervalMs = 1000,
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$script:LastVisualError = ''

if (-not ('FNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static int GetIdleSeconds()
    {
        LASTINPUTINFO li = new LASTINPUTINFO();
        li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref li)) return -1;
        uint tick = (uint)Environment.TickCount;
        return (int)((tick - li.dwTime) / 1000u);
    }

    public static string GetForegroundTitle()
    {
        IntPtr h = GetForegroundWindow();
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
'@
}

function Get-ForegroundInfo {
    $h = [FNative]::GetForegroundWindow()
    [uint32]$procId = 0
    [void][FNative]::GetWindowThreadProcessId($h, [ref]$procId)
    $name = 'unknown'
    if ($procId -gt 0) {
        try { $name = (Get-Process -Id $procId -ErrorAction Stop).ProcessName + '.exe' } catch { $name = "pid:$procId" }
    }
    $title = [FNative]::GetForegroundTitle()
    if ($title.Length -gt 70) { $title = $title.Substring(0, 70) + '…' }
    [pscustomobject]@{
        Hwnd  = $h
        Pid   = $procId
        App   = $name
        Title = $title
    }
}

function Get-VisualHash {
    param([IntPtr]$Hwnd)

    if ([FNative]::IsIconic($Hwnd)) {
        $script:LastVisualError = 'window minimized'
        return $null
    }

    $rect = New-Object 'FNative+RECT'
    if (-not [FNative]::GetWindowRect($Hwnd, [ref]$rect)) {
        $script:LastVisualError = 'GetWindowRect failed'
        return $null
    }

    # 窗口可能最小化到 -32000、位于屏幕外或横跨多屏，必须裁剪到虚拟桌面范围内
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $left   = [math]::Max($rect.Left, $vs.Left)
    $top    = [math]::Max($rect.Top, $vs.Top)
    $right  = [math]::Min($rect.Right, $vs.Right)
    $bottom = [math]::Min($rect.Bottom, $vs.Bottom)
    $w = $right - $left
    $h = $bottom - $top
    if ($w -le 8 -or $h -le 8) {
        $script:LastVisualError = "offscreen rect ($($rect.Left),$($rect.Top))-($($rect.Right),$($rect.Bottom))"
        return $null
    }

    $bmp = $null; $g = $null; $small = $null; $g2 = $null
    try {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size($w, $h)))

        $small = New-Object System.Drawing.Bitmap(9, 8)
        $g2 = [System.Drawing.Graphics]::FromImage($small)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        $g2.DrawImage($bmp, 0, 0, 9, 8)

        # 用一维数组模拟 8x9 网格：PowerShell 5.1 对多维数组的表达式下标支持有坑
        $lum = New-Object 'double[]' 72
        $min = [double]::MaxValue
        $max = [double]::MinValue
        for ($y = 0; $y -lt 8; $y++) {
            for ($x = 0; $x -lt 9; $x++) {
                $c = $small.GetPixel($x, $y)
                $v = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                $lum[$y * 9 + $x] = $v
                if ($v -lt $min) { $min = $v }
                if ($v -gt $max) { $max = $v }
            }
        }

        [uint64]$hash = 0
        $bit = 0
        for ($y = 0; $y -lt 8; $y++) {
            for ($x = 0; $x -lt 8; $x++) {
                if ($lum[$y * 9 + $x] -gt $lum[$y * 9 + $x + 1]) {
                    $hash = $hash -bor ([uint64]1 -shl $bit)
                }
                $bit++
            }
        }

        [pscustomobject]@{
            Hash    = $hash
            Range   = $max - $min
            IsBlack = (($max - $min) -lt 1.0)
        }
    }
    catch {
        $script:LastVisualError = $_.Exception.Message
        $null
    }
    finally {
        if ($g2) { $g2.Dispose() }
        if ($small) { $small.Dispose() }
        if ($g) { $g.Dispose() }
        if ($bmp) { $bmp.Dispose() }
    }
}

function Get-PopCount {
    param([uint64]$x)
    $n = 0
    while ($x -ne 0) { $n++; $x = $x -band ($x - 1) }
    $n
}

# ---------- 运行 ----------

$proc = Get-Process -Id $PID
$cpuStart = $proc.TotalProcessorTime
$sw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ''
Write-Host 'FocusLedger · M0 信号探针' -ForegroundColor Cyan
Write-Host ("运行 {0}s，间隔 {1}ms。请在运行期间切换几个窗口、静止一会儿、再滚动页面。" -f $Seconds, $IntervalMs) -ForegroundColor DarkGray
Write-Host ''

$rows = @()
$prevHash = $null
$prevApp = $null
$switchCount = 0
$blackFrames = 0
$visualFailures = 0

try {
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        $info = Get-ForegroundInfo
        $idle = [FNative]::GetIdleSeconds()
        $vis = Get-VisualHash -Hwnd $info.Hwnd

        $delta = -1
        if ($null -eq $vis) {
            $visualFailures++
        }
        elseif ($prevHash -ne $null) {
            $delta = Get-PopCount ($vis.Hash -bxor $prevHash)
        }

        if ($vis -ne $null -and $vis.IsBlack) { $blackFrames++ }
        if ($prevApp -and $prevApp -ne $info.App) { $switchCount++ }
        $prevApp = $info.App
        if ($vis -ne $null) { $prevHash = $vis.Hash }

        $t = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        Write-Host ("[{0,5}s] {1,-22} idle={2,4}s  dhash_delta={3,3}  | {4}" -f `
                $t, $info.App, $idle, $delta, $info.Title)

        $isBlack = $null
        if ($null -ne $vis) { $isBlack = $vis.IsBlack }

        $rows += [pscustomobject]@{
            t_sec        = $t
            app          = $info.App
            title        = $info.Title
            idle_s       = $idle
            visual_delta = $delta
            visual_black = $isBlack
        }

        Start-Sleep -Milliseconds $IntervalMs
    }
}
finally {
    $sw.Stop()
    $proc.Refresh()
    $cpu = $proc.TotalProcessorTime - $cpuStart
    $elapsed = $sw.Elapsed
    $cores = [Environment]::ProcessorCount
    $cpuPct = if ($elapsed.TotalSeconds -gt 0) {
        [math]::Round(($cpu.TotalMilliseconds / ($elapsed.TotalMilliseconds * $cores)) * 100, 2)
    } else { 0 }

    Write-Host ''
    Write-Host '---------- 汇总 ----------' -ForegroundColor Cyan
    Write-Host ("采样点数        : {0}" -f $rows.Count)
    Write-Host ("窗口切换次数    : {0}" -f $switchCount)
    Write-Host ("画面采样失败    : {0}" -f $visualFailures)
    if ($visualFailures -gt 0 -and $script:LastVisualError) {
        Write-Host ("  └ 失败原因      : {0}" -f $script:LastVisualError) -ForegroundColor Yellow
    }
    Write-Host ("疑似黑帧(DRM)   : {0}" -f $blackFrames)
    Write-Host ("平均 CPU 占用   : {0}%  ({1} 核)" -f $cpuPct, $cores)
    Write-Host ("工作集内存      : {0} MB" -f [math]::Round($proc.WorkingSet64 / 1MB, 1))

    $valid = $rows | Where-Object { $_.visual_delta -ge 0 }
    if ($valid.Count -gt 0) {
        $stats = $valid | Measure-Object -Property visual_delta -Average -Maximum -Minimum
        Write-Host ("画面变化 delta  : min={0} max={1} avg={2:N1}" -f $stats.Minimum, $stats.Maximum, $stats.Average)
        $still = ($valid | Where-Object { $_.visual_delta -eq 0 }).Count
        Write-Host ("完全静止占比    : {0:P0}" -f ($still / $valid.Count))
    }

    $maxIdle = ($rows | Measure-Object -Property idle_s -Maximum).Maximum
    Write-Host ("最大空闲时长    : {0}s" -f $maxIdle)
    Write-Host ''

    if ($OutFile) {
        $rows | Export-Csv -Path $OutFile -NoTypeInformation -Encoding UTF8
        Write-Host ("已写出 CSV: {0}" -f $OutFile) -ForegroundColor Green
    }
}
