# process-art.ps1
# ---------------------------------------------------------------------------
# Prepare the Phoebe sprite set for the desktop pet.
#
# Does four things to each source image, in order:
#   1. Erase the "AI generated" watermark in the top-left corner.
#   2. Remove a baked-in checkerboard background (fake transparency).
#   3. Crop away fully transparent margins.
#   4. Align each open-eye/closed-eye pair onto a common aspect ratio so the
#      sprite does not jump when privacy mode toggles the eyes.
#
# Output goes to Art/ as:
#   <pose>.png              open eyes   (normal state)
#   <pose>-<BIYAN>.png      closed eyes (privacy mode)
#
# ASCII-ONLY RULE
#   Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so any non-ASCII
#   byte would corrupt the script. That includes Chinese in COMMENTS and in
#   VARIABLE NAMES - both broke this file on the first attempt. Therefore
#   every Chinese string below is assembled from [char] code points.
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [string]$Src = '',
    [string]$Dst = '',
    [int]$TargetW = 0    # 0 = keep the natural cropped size
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---- Chinese fragments, built from code points (keeps this file ASCII) ----
function U([int[]]$cp) { -join ($cp | ForEach-Object { [char]$_ }) }

$C_SIT    = U @(0x5750)                       # zuo  (sitting)
$C_STAND  = U @(0x7AD9)                       # zhan (standing)
$C_LIE    = U @(0x8DB4)                       # pa   (lying down)
$C_TRANS  = U @(0x900F, 0x660E)               # tou ming
$C_CLOSED = U @(0x95ED, 0x773C)               # bi yan
$C_PHOEBE = U @(0x83F2, 0x6BD4)               # fei bi
$C_DASH   = '-'
$C_SRCDIR = U @(0x83F2, 0x6BD4, 0x7ACB, 0x7ED8)   # fei bi li hui

# ---- defaults resolved after binding ($PSScriptRoot is unreliable in param) ----
# Source defaults to a sibling folder of the script's parent; pass -Src to
# point anywhere else.
$scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
             elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }
             else { (Get-Location).Path }

if ([string]::IsNullOrWhiteSpace($Src)) { $Src = Join-Path (Split-Path -Parent $scriptDir) $C_SRCDIR }
if ([string]::IsNullOrWhiteSpace($Dst)) { $Dst = Join-Path $scriptDir 'Art' }
if (!(Test-Path -LiteralPath $Dst)) { New-Item -ItemType Directory -Path $Dst -Force | Out-Null }

# ---- pose table: output pose name -> source file names ----
# NOTE: every expansion uses ${...} braces. Without them PowerShell reads
# "$C_PHOEBE$C_STAND$C_TRANS.png" greedily and mangles the name (observed:
# the sitting file was looked up instead of the standing one).
$poses = @(
    @{ Out = "${C_PHOEBE}${C_DASH}${C_STAND}"; Open = "${C_PHOEBE}${C_STAND}${C_TRANS}.png"; Closed = "${C_PHOEBE}${C_STAND}${C_CLOSED}${C_TRANS}.png" },
    @{ Out = "${C_PHOEBE}${C_DASH}${C_SIT}";   Open = "${C_PHOEBE}${C_SIT}${C_TRANS}.png";   Closed = "${C_PHOEBE}${C_SIT}${C_CLOSED}${C_TRANS}.png" },
    @{ Out = "${C_PHOEBE}${C_DASH}${C_LIE}";   Open = "${C_PHOEBE}${C_LIE}${C_TRANS}.png";   Closed = "${C_PHOEBE}${C_LIE}${C_CLOSED}${C_TRANS}.png" }
)

function New-Canvas([int]$w, [int]$h) {
    $b = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.Dispose()
    return $b
}

# ---------------------------------------------------------------------------
# 1. Erase the watermark.
#
#    Measured signature of the mark (this took three attempts - the wrong
#    guesses are recorded so they are not repeated):
#      - it is PURE WHITE at alpha ~127 (a half-transparent white overlay),
#        roughly x=40..340, y=45..110, sitting in empty space above the hat;
#      - the hat's soft drop shadow is a low-alpha gradient (alpha 1..40),
#        so an alpha>=100 & R=G=B=255 test separates the two cleanly;
#      - "transparent gap below the mark" does NOT work (shadow shares rows);
#      - an A>=150 threshold finds NOTHING, because the mark is alpha 127.
#
#    Erasing is confined to the rows above the artwork, and the function
#    refuses to run if the detected block looks like artwork instead.
# ---------------------------------------------------------------------------
function Remove-Watermark([System.Drawing.Bitmap]$img) {
    $scanW = [Math]::Min(500, $img.Width)
    $scanH = [Math]::Min(140, [int]($img.Height * 0.09))

    $minX = 999999; $minY = 999999; $maxX = -1; $maxY = -1; $hits = 0
    for ($y = 0; $y -lt $scanH; $y++) {
        for ($x = 0; $x -lt $scanW; $x++) {
            $p = $img.GetPixel($x, $y)
            if ($p.A -lt 100) { continue }                       # skip soft shadow
            if ($p.R -ne 255 -or $p.G -ne 255 -or $p.B -ne 255) { continue }  # pure white only
            $hits++
            if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($hits -lt 50) { return "no watermark found (white-opaque hits=$hits)" }

    # The artwork can also contain opaque white (the hat). The watermark is a
    # DENSE compact cluster in the top-left; the hat is sparse and sits further
    # right. So cluster the hits by column and keep only the left-most dense
    # run - that is the mark. Measured example (lying pose): the mark spans
    # x=40..181 densely while the hat contributes only a few pixels at x>340.
    $colHits = @{}
    for ($y = 0; $y -lt $scanH; $y++) {
        for ($x = 0; $x -lt $scanW; $x++) {
            $p = $img.GetPixel($x, $y)
            if ($p.A -lt 100) { continue }
            if ($p.R -ne 255 -or $p.G -ne 255 -or $p.B -ne 255) { continue }
            if ($colHits.ContainsKey($x)) { $colHits[$x]++ } else { $colHits[$x] = 1 }
        }
    }
    $denseCols = @($colHits.Keys | Where-Object { $colHits[$_] -ge 2 } | Sort-Object)
    if ($denseCols.Count -eq 0) { return 'no dense white column found - skipped' }

    # Split the dense columns into runs separated by a wide gap; the watermark
    # is the first (left-most) run.
    $runs = @()
    $start = $denseCols[0]; $prev = $denseCols[0]
    foreach ($c in $denseCols) {
        if (($c - $prev) -gt 40) { $runs += ,@($start, $prev); $start = $c }
        $prev = $c
    }
    $runs += ,@($start, $prev)
    $runX0 = $runs[0][0]; $runX1 = $runs[0][1]

    # Restrict the vertical extent to rows that belong to that left run.
    $minY = 999999; $maxY = -1
    for ($y = 0; $y -lt $scanH; $y++) {
        for ($x = $runX0; $x -le $runX1; $x++) {
            $p = $img.GetPixel($x, $y)
            if ($p.A -ge 100 -and $p.R -eq 255 -and $p.G -eq 255 -and $p.B -eq 255) {
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
                break
            }
        }
    }
    if ($maxY -lt 0) { return 'no watermark rows found - skipped' }

    # The mark must sit near the top; a block starting low in the window is art.
    if ($minY -gt ($scanH * 0.75)) {
        return "SKIPPED: white block starts at y=$minY - too low, looks like artwork"
    }
    # A corner watermark is small in width.
    if (($runX1 - $runX0) -gt ($img.Width * 0.35)) {
        return "SKIPPED: white run is $($runX1 - $runX0)px wide - looks like artwork"
    }

    $x0 = [Math]::Max(0, $runX0 - 10); $y0 = [Math]::Max(0, $minY - 10)
    $x1 = [Math]::Min($img.Width - 1,  $runX1 + 10)
    $y1 = [Math]::Min($img.Height - 1, $maxY + 10)
    for ($y = $y0; $y -le $y1; $y++) {
        for ($x = $x0; $x -le $x1; $x++) {
            $img.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
        }
    }
    return "erased ${x0},${y0}..${x1},${y1} (run x=$runX0..$runX1, $hits white px)"
}

# ---------------------------------------------------------------------------
# 2. Remove a baked-in checkerboard (fake transparency drawn as real pixels).
#    Detects the two dominant greys and converts only those; an image that
#    already has real alpha is left alone.
# ---------------------------------------------------------------------------
function Remove-Checkerboard([System.Drawing.Bitmap]$img) {
    $trans = 0; $tot = 0
    for ($y = 0; $y -lt $img.Height; $y += 16) {
        for ($x = 0; $x -lt $img.Width; $x += 16) { $tot++; if ($img.GetPixel($x, $y).A -lt 10) { $trans++ } }
    }
    if ($tot -gt 0 -and ($trans / $tot) -gt 0.05) { return 'already transparent - skipped' }

    $hist = @{}
    for ($y = 0; $y -lt $img.Height; $y += 3) {
        for ($x = 0; $x -lt $img.Width; $x += 3) {
            $p = $img.GetPixel($x, $y)
            if ($p.A -lt 250) { continue }
            if ($p.R -ne $p.G -or $p.G -ne $p.B) { continue }
            $k = [string]$p.R
            if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
        }
    }
    if ($hist.Count -eq 0) { return 'no checkerboard detected - skipped' }

    $top = @($hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 2)
    $c1 = [int]$top[0].Key
    $c2 = if ($top.Count -gt 1) { [int]$top[1].Key } else { -1 }
    $n1 = $top[0].Value

    $sampleTot = 0
    foreach ($v in $hist.Values) { $sampleTot += $v }
    if ($n1 -lt ($sampleTot * 0.10)) {
        return "no checkerboard detected (top grey $([Math]::Round(100 * $n1 / $sampleTot))%) - skipped"
    }

    $removed = 0
    for ($y = 0; $y -lt $img.Height; $y++) {
        for ($x = 0; $x -lt $img.Width; $x++) {
            $p = $img.GetPixel($x, $y)
            if ($p.A -lt 250) { continue }
            if ($p.R -ne $p.G -or $p.G -ne $p.B) { continue }
            $v = [int]$p.R
            if ($v -eq $c1 -or ($c2 -ge 0 -and $v -eq $c2)) {
                $img.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
                $removed++
            }
        }
    }
    return "removed checkerboard greys ($c1,$c2): $removed px"
}

# ---------------------------------------------------------------------------
# 3. Crop to the non-transparent bounding box.
# ---------------------------------------------------------------------------
function Get-ContentBounds([System.Drawing.Bitmap]$img) {
    $minX = $img.Width; $minY = $img.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $img.Height; $y++) {
        for ($x = 0; $x -lt $img.Width; $x++) {
            if ($img.GetPixel($x, $y).A -gt 16) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return $null }
    return @{ X = $minX; Y = $minY; W = ($maxX - $minX + 1); H = ($maxY - $minY + 1) }
}

function Crop-To([System.Drawing.Bitmap]$img, $b) {
    $rect = New-Object System.Drawing.Rectangle($b.X, $b.Y, $b.W, $b.H)
    return $img.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

# Scale to fit inside a canvas and centre it, so a pair shares one geometry.
function Fit-Centered([System.Drawing.Bitmap]$src, [int]$cw, [int]$ch) {
    $k = [Math]::Min($cw / [double]$src.Width, $ch / [double]$src.Height)
    $nw = [Math]::Max(1, [int][Math]::Round($src.Width * $k))
    $nh = [Math]::Max(1, [int][Math]::Round($src.Height * $k))
    $canvas = New-Canvas $cw $ch
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, [int](($cw - $nw) / 2), [int](($ch - $nh) / 2), $nw, $nh)
    $g.Dispose()
    return $canvas
}

function Resize-If([System.Drawing.Bitmap]$src, [int]$tw) {
    if ($tw -le 0 -or $src.Width -le $tw) { return $src }
    $th = [int][Math]::Round($src.Height * ($tw / [double]$src.Width))
    $b = New-Canvas $tw $th
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, 0, 0, $tw, $th)
    $g.Dispose()
    return $b
}

Write-Host ''
Write-Host '  Art processing' -ForegroundColor Cyan
Write-Host "  Source : $Src"
Write-Host "  Output : $Dst"
Write-Host ''

$results = @()

foreach ($p in $poses) {
    $openPath   = Join-Path $Src $p.Open
    $closedPath = Join-Path $Src $p.Closed

    if (!(Test-Path -LiteralPath $openPath))   { Write-Host "  [X] missing: $($p.Open)"   -ForegroundColor Red; continue }
    if (!(Test-Path -LiteralPath $closedPath)) { Write-Host "  [X] missing: $($p.Closed)" -ForegroundColor Red; continue }

    Write-Host "  === $($p.Out) ===" -ForegroundColor White

    $pair = @{}
    $abort = $false
    foreach ($which in @('open', 'closed')) {
        # NOTE: this variable must NOT be called $src - PowerShell variable
        # names are case-insensitive, so "$src" collides with the $Src
        # parameter and clobbers the source directory. That made every pose
        # after the first build its path from a previous image file and
        # report "missing" (observed before this was renamed).
        $filePath = if ($which -eq 'open') { $openPath } else { $closedPath }
        $img = New-Object System.Drawing.Bitmap($filePath)

        $wm  = Remove-Watermark   $img
        $chk = Remove-Checkerboard $img
        $b   = Get-ContentBounds  $img
        if ($null -eq $b) {
            Write-Host "      [X] $which became fully transparent - aborting this pose" -ForegroundColor Red
            $img.Dispose(); $abort = $true; break
        }

        $cropped = Crop-To $img $b
        $img.Dispose()

        Write-Host ("      {0,-6} watermark: {1}" -f $which, $wm)          -ForegroundColor DarkGray
        Write-Host ("      {0,-6} checker  : {1}" -f $which, $chk)         -ForegroundColor DarkGray
        Write-Host ("      {0,-6} cropped  : {1}x{2}" -f $which, $cropped.Width, $cropped.Height) -ForegroundColor DarkGray

        $pair[$which] = $cropped
    }
    if ($abort) { continue }

    # ---- align the pair: canvas ratio follows the open-eye image ----
    $o = $pair['open']; $c = $pair['closed']
    $ar = $o.Height / [double]$o.Width
    $canW = [Math]::Max($o.Width, $c.Width)
    $canH = [int][Math]::Round($canW * $ar)

    $o2 = Fit-Centered $o $canW $canH
    $c2 = Fit-Centered $c $canW $canH
    $o.Dispose(); $c.Dispose()

    $o3 = Resize-If $o2 $TargetW; if (-not [object]::ReferenceEquals($o3, $o2)) { $o2.Dispose() }
    $c3 = Resize-If $c2 $TargetW; if (-not [object]::ReferenceEquals($c3, $c2)) { $c2.Dispose() }

    $openOut   = Join-Path $Dst ("$($p.Out).png")
    $closedOut = Join-Path $Dst ("$($p.Out)$C_DASH$C_CLOSED.png")
    $o3.Save($openOut,   [System.Drawing.Imaging.ImageFormat]::Png)
    $c3.Save($closedOut, [System.Drawing.Imaging.ImageFormat]::Png)

    Write-Host ("      -> {0}x{1}  saved" -f $o3.Width, $o3.Height) -ForegroundColor Green
    Write-Host ''

    $results += [pscustomobject]@{
        Pose      = $p.Out
        W         = $o3.Width
        H         = $o3.Height
        Ratio     = [Math]::Round($o3.Height / [double]$o3.Width, 3)
        PairMatch = ($o3.Width -eq $c3.Width -and $o3.Height -eq $c3.Height)
    }
    $o3.Dispose(); $c3.Dispose()
}

Write-Host '  ' + ('-' * 52)
$results | ForEach-Object {
    Write-Host ("  {0,-16} {1,5}x{2,-5} ratio={3,-6} pair-aligned={4}" -f $_.Pose, $_.W, $_.H, $_.Ratio, $_.PairMatch)
}
Write-Host ''
