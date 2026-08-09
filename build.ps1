<#
    Builds TodoWall into .\dist

    Usage:
        .\build.ps1              # release build into .\dist
        .\build.ps1 -Run         # build, then start it
        .\build.ps1 -SelfContained   # bundle the .NET runtime (no runtime install needed)
        .\build.ps1 -Package     # self-contained build + read-me, zipped, ready to send
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$SelfContained,
    [switch]$Package
)

# Packaging is for handing to someone else, who will not have the .NET runtime.
if ($Package) { $SelfContained = $true }

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# ---------------------------------------------------------------- app icon
function New-AppIcon {
    param([string]$Path)

    Add-Type -AssemblyName System.Drawing

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $blobs = @()

    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $accent = [System.Drawing.Color]::FromArgb(255, 111, 177, 255)
        $brush = New-Object System.Drawing.SolidBrush($accent)
        $pad = [double]$s * 0.03
        $g.FillEllipse($brush, $pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
        $brush.Dispose()

        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 20, 22, 28), [float]($s * 0.105))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $pts = @(
            (New-Object System.Drawing.PointF([float]($s * 0.28), [float]($s * 0.52))),
            (New-Object System.Drawing.PointF([float]($s * 0.44), [float]($s * 0.68))),
            (New-Object System.Drawing.PointF([float]($s * 0.73), [float]($s * 0.33)))
        )
        $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
        $pen.Dispose()
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $blobs += , @{ Size = $s; Data = $ms.ToArray() }
        $ms.Dispose()
    }

    # ICONDIR + ICONDIRENTRY[] + PNG payloads
    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0)                 # reserved
    $bw.Write([UInt16]1)                 # type: icon
    $bw.Write([UInt16]$blobs.Count)

    $offset = 6 + (16 * $blobs.Count)
    foreach ($b in $blobs) {
        $dim = if ($b.Size -ge 256) { 0 } else { $b.Size }
        $bw.Write([Byte]$dim)            # width
        $bw.Write([Byte]$dim)            # height
        $bw.Write([Byte]0)               # palette count
        $bw.Write([Byte]0)               # reserved
        $bw.Write([UInt16]1)             # colour planes
        $bw.Write([UInt16]32)            # bits per pixel
        $bw.Write([UInt32]$b.Data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $b.Data.Length
    }
    foreach ($b in $blobs) { $bw.Write($b.Data) }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

$icon = Join-Path $root 'app.ico'
if (-not (Test-Path $icon)) {
    Write-Host 'Generating app.ico...' -ForegroundColor DarkGray
    New-AppIcon -Path $icon
}

# ---------------------------------------------------------------- build
$sdkUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) { throw "dotnet was not found on PATH. Install the .NET 10 SDK: $sdkUrl" }

$sdks = & dotnet --list-sdks
if (-not $sdks) { throw "No .NET SDK found - only a runtime is installed. Install the .NET 10 SDK: $sdkUrl" }

# The project targets net10.0-windows, which an older SDK cannot build. It does say so
# on its own, but in NETSDK1045 terms that take a moment to decode - so check plainly.
$majors = @()
foreach ($line in $sdks) {
    $m = [regex]::Match($line, '^(\d+)\.')
    if ($m.Success) { $majors += [int]$m.Groups[1].Value }
}
if ($majors.Count -gt 0) {
    $newest = ($majors | Measure-Object -Maximum).Maximum
    if ($newest -lt 10) {
        throw "TodoWall targets net10.0-windows, but the newest SDK installed is $newest.x. Install the .NET 10 SDK: $sdkUrl"
    }
}

# A running instance keeps a lock on TodoWall.dll, which would fail the clean.
# Packaging publishes to .\release, so a running instance (which runs from .\dist)
# is holding nothing that is in the way.
$running = if ($Package) { $null } else { Get-Process TodoWall -ErrorAction SilentlyContinue }
if ($running) {
    Write-Host 'Stopping the running TodoWall...' -ForegroundColor DarkGray
    try {
        $running | Stop-Process -Force -ErrorAction Stop
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
    catch {
        Write-Host ''
        Write-Host 'Could not stop the running TodoWall (access denied).' -ForegroundColor Yellow
        Write-Host 'It is almost certainly running elevated while this shell is not.' -ForegroundColor Yellow
        Write-Host 'Right-click the TodoWall tray icon and choose Exit, then run this again.' -ForegroundColor Yellow
        Write-Host '(TodoWall does not need admin - please start it normally from now on.)' -ForegroundColor DarkGray
        Write-Host ''
        throw 'Build stopped: a running TodoWall is holding dist\TodoWall.dll.'
    }
}

$dist = if ($Package) { Join-Path $root 'release\TodoWall' } else { Join-Path $root 'dist' }
if (Test-Path $dist) {
    # Windows can hold the file briefly after the process exits.
    for ($i = 0; $i -lt 10; $i++) {
        try { Remove-Item $dist -Recurse -Force -ErrorAction Stop; break }
        catch {
            if ($i -eq 9) { throw "Could not clean $dist - is TodoWall still running? $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 400
        }
    }
}

$publishArgs = @(
    'publish', 'TodoWall.csproj',
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $dist,
    '/p:DebugType=embedded'
)
if ($SelfContained) {
    $publishArgs += '--self-contained'; $publishArgs += 'true'
    $publishArgs += '/p:PublishSingleFile=true'
    $publishArgs += '/p:IncludeNativeLibrariesForSelfExtract=true'
    # The runtime bundle is most of the file; compressing it roughly halves the download.
    $publishArgs += '/p:EnableCompressionInSingleFile=true'
}
else {
    $publishArgs += '--self-contained'; $publishArgs += 'false'
}

Write-Host "Building TodoWall..." -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$exe = Join-Path $dist 'TodoWall.exe'
if (-not (Test-Path $exe)) { throw "Build reported success but $exe is missing." }

Write-Host ""
Write-Host "Built: $exe" -ForegroundColor Green

# ---------------------------------------------------------------- package
if ($Package) {
    $csproj = Get-Content (Join-Path $root 'TodoWall.csproj') -Raw
    $version = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
    if (-not $version) { $version = '1.0.0' }

    $readme = @"
TodoWall $version
A to-do board that lives on your desktop.


GETTING STARTED

  1. Put TodoWall.exe wherever you like - Documents, Desktop, anywhere.
     There is no installer; this one file is the whole program.
  2. Double-click it. The board appears on the desktop, and a tick-mark
     icon appears in the notification area (bottom right, under the ^).

  The first time, Windows may say "Windows protected your PC". That is
  SmartScreen noticing a file it has not seen before and that has not been
  code-signed - it is not a virus warning. Click "More info", then
  "Run anyway".

  If it came out of a downloaded zip and will not start, right-click the
  exe, choose Properties, tick "Unblock" at the bottom, then OK.


USING IT

  Add a task ............  click "+ add task" under any day
  Several in a row ......  type, press Enter, keep typing
  Tick one off ..........  click the circle next to it
  Edit ..................  click the task's text
  Delete ................  hover it and click the x, or middle-click it
  Move to another day ...  right-click the task
  Other weeks ...........  the arrows at the top; "Today" jumps back
  Settings ..............  the gear at the top right, or right-click the
                           board, or right-click the tray icon
  Quit ..................  right-click the tray icon, then Exit

  Everything saves itself. There is no save button.


WHAT IT NEEDS

  Windows 10 or 11, 64-bit. Nothing else - the .NET runtime is inside the
  exe. It never asks for administrator rights, and it only writes to its
  own folder below.


WHERE YOUR TASKS ARE KEPT

  %APPDATA%\TodoWall     (paste that into Explorer's address bar)


REMOVING IT

  Settings -> Remove -> "Uninstall TodoWall...". It stops starting with
  Windows, asks whether to delete your tasks, and then deletes itself.
"@
    Set-Content -Path (Join-Path $dist 'Read me first.txt') -Value $readme -Encoding utf8

    $zip = Join-Path $root "release\TodoWall-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $dist -DestinationPath $zip

    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Package: $zip  ($mb MB)" -ForegroundColor Green
    Write-Host 'Send that zip. Unzipping gives a TodoWall folder with the exe and a read-me.' -ForegroundColor DarkGray
}

if ($Run) {
    Start-Process $exe
    Write-Host 'TodoWall started - look for the tray icon.' -ForegroundColor Green
}
