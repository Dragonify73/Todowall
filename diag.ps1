# Reports where TodoWall's window actually ended up, and what Explorer's
# desktop windows look like on this machine.
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Diag {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string w);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Cls(IntPtr h){ var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString(); }
    public static string Txt(IntPtr h){ var sb=new StringBuilder(256); GetWindowText(h,sb,256); return sb.ToString(); }
}
'@

function Show-Win($h, $label) {
    $r = New-Object Diag+RECT
    [void][Diag]::GetWindowRect($h, [ref]$r)
    $pid2 = 0
    [void][Diag]::GetWindowThreadProcessId($h, [ref]$pid2)
    $style = [Diag]::GetWindowLongPtr($h, -16).ToInt64()
    $ex = [Diag]::GetWindowLongPtr($h, -20).ToInt64()
    $parent = [Diag]::GetParent($h)
    "{0,-14} hwnd=0x{1:X}  class={2,-22} pid={3}" -f $label, $h.ToInt64(), [Diag]::Cls($h), $pid2
    "               rect=({0},{1})-({2},{3})  {4}x{5}  visible={6}" -f $r.Left,$r.Top,$r.Right,$r.Bottom,($r.Right-$r.Left),($r.Bottom-$r.Top),[Diag]::IsWindowVisible($h)
    "               style=0x{0:X8}  exstyle=0x{1:X8}  parent=0x{2:X}" -f $style, $ex, $parent.ToInt64()
    "               WS_CHILD={0} WS_VISIBLE={1} WS_POPUP={2} EX_LAYERED={3} EX_TOOLWINDOW={4}" -f `
        (($style -band 0x40000000) -ne 0), (($style -band 0x10000000) -ne 0), (($style -band 0x80000000) -ne 0), `
        (($ex -band 0x00080000) -ne 0), (($ex -band 0x00000080) -ne 0)
    ""
}

"=== TodoWall process ==="
$proc = Get-Process TodoWall -ErrorAction SilentlyContinue
if (-not $proc) { "NOT RUNNING"; exit }
"pid = $($proc.Id)"
""

"=== Windows owned by TodoWall ==="
$targetPid = $proc.Id
$found = @()
$cb = [Diag+EnumProc]{
    param($h, $p)
    $wpid = 0
    [void][Diag]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -eq $targetPid) { $script:found += $h }
    return $true
}
[void][Diag]::EnumWindows($cb, [IntPtr]::Zero)

# top-level enumeration misses windows re-parented into Progman, so walk Progman too
$progman = [Diag]::FindWindow("Progman", $null)
$cb2 = [Diag+EnumProc]{
    param($h, $p)
    $wpid = 0
    [void][Diag]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -eq $targetPid -and $script:found -notcontains $h) { $script:found += $h }
    return $true
}
if ($progman -ne [IntPtr]::Zero) { [void][Diag]::EnumChildWindows($progman, $cb2, [IntPtr]::Zero) }

if ($found.Count -eq 0) { "none found" }
foreach ($h in $found) { Show-Win $h "TodoWall" }

"=== Explorer desktop layout ==="
if ($progman -ne [IntPtr]::Zero) { Show-Win $progman "Progman" }
$defview = [Diag]::FindWindowEx($progman, [IntPtr]::Zero, "SHELLDLL_DefView", $null)
if ($defview -ne [IntPtr]::Zero) { Show-Win $defview "DefView(prog)" }

$workers = @()
$cb3 = [Diag+EnumProc]{
    param($h, $p)
    if ([Diag]::Cls($h) -eq "WorkerW") { $script:workers += $h }
    return $true
}
[void][Diag]::EnumWindows($cb3, [IntPtr]::Zero)
foreach ($w in $workers) {
    $dv = [Diag]::FindWindowEx($w, [IntPtr]::Zero, "SHELLDLL_DefView", $null)
    Show-Win $w ("WorkerW" + $(if ($dv -ne [IntPtr]::Zero) { "*icons" } else { "" }))
}

"=== Screens ==="
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    "bounds={0}  working={1}  primary={2}" -f $_.Bounds, $_.WorkingArea, $_.Primary
}

"=== settings.json ==="
$cfg = Join-Path $env:APPDATA 'TodoWall\settings.json'
if (Test-Path $cfg) { Get-Content $cfg -Raw } else { "(none)" }

"=== todowall.log (last 40 lines) ==="
$log = Join-Path $env:APPDATA 'TodoWall\todowall.log'
if (Test-Path $log) { Get-Content $log -Tail 40 } else { "(no log yet)" }

"=== error.log (last 40 lines) ==="
$err = Join-Path $env:APPDATA 'TodoWall\error.log'
if (Test-Path $err) { Get-Content $err -Tail 40 } else { "(no errors logged)" }
