# Renders TodoWall's bar window to a PNG using PrintWindow, so it can be inspected
# without minimising anything or capturing whatever is sitting on top of it.
param([string]$Out = "$env:TEMP\todowall-shot.png")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Shot {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString(); }
}
'@

$proc = Get-Process TodoWall -ErrorAction SilentlyContinue
if (-not $proc) { throw 'TodoWall is not running.' }
$targetPid = $proc.Id

$best = [IntPtr]::Zero
$bestArea = 0
$cb = [Shot+EnumProc]{
    param($h, $p)
    $wpid = 0
    [void][Shot]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -eq $targetPid -and [Shot]::Cls($h).StartsWith('HwndWrapper[TodoWall')) {
        # WPF keeps hidden helper windows around; the bar is the visible, layered one
        # (AllowsTransparency implies WS_EX_LAYERED).
        $ex = [Shot]::GetWindowLongPtr($h, -20).ToInt64()
        $layered = ($ex -band 0x00080000) -ne 0
        if ([Shot]::IsWindowVisible($h) -and $layered) {
            $r = New-Object Shot+RECT
            [void][Shot]::GetWindowRect($h, [ref]$r)
            $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
            if ($area -gt $script:bestArea) { $script:bestArea = $area; $script:best = $h }
        }
    }
    return $true
}
[void][Shot]::EnumWindows($cb, [IntPtr]::Zero)

if ($best -eq [IntPtr]::Zero) { throw 'Could not find the TodoWall bar window.' }

$r = New-Object Shot+RECT
[void][Shot]::GetWindowRect($best, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
"window 0x{0:X}  {1}x{2} at ({3},{4})" -f $best.ToInt64(), $w, $h, $r.Left, $r.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# 0x2 = PW_RENDERFULLCONTENT, required for DWM/WPF-composited windows
[void][Shot]::PrintWindow($best, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved: $Out"
