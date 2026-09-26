# convert-ogg-to-mp3.ps1
# ---------------------------------------------------------------------------
# Batch-convert Ogg Vorbis sound files under Sounds/ into MP3.
#
# WHY THIS EXISTS
#   In the pet's sound.cs, ".ogg" is routed to the WPF MediaPlayer path.
#   MediaPlayer sits on Windows Media Foundation, which does NOT support
#   Ogg Vorbis. Worse, Open()/Play() only kick off an async load: on failure
#   nothing throws, so PlayMedia() returns true. The log then says
#   "double-click played: xxx.ogg" while in fact nothing is audible
#   (silent failure). Converting to MP3 uses the same path and just works.
#
#   This script is a WORKAROUND, not a fix. sound.cs still lists ".ogg"
#   in MediaExt, so an untranslated ogg will still fail silently.
#
# USAGE (double-click is not enough because of the parameters; use a shell)
#   powershell -NoProfile -ExecutionPolicy Bypass -File convert-ogg-to-mp3.ps1
#
# PARAMETERS
#   -Root <path>     Root folder to scan. Defaults to the script's own folder.
#   -Bitrate 192k    Output bitrate. Default 192k.
#   -Force           Re-encode even when the target mp3 already exists.
#   -DeleteSource    Delete the source .ogg after a SUCCESSFUL conversion.
#                    Off by default: sources are always kept unless asked.
#
# BEHAVIOUR
#   - Recurses into subfolders (each subfolder is a sound group).
#   - Existing same-named .mp3 files are skipped, so re-running is safe.
#   - Failures are summarised at the end; source files are never touched.
#   - This file must stay pure ASCII: Windows PowerShell 5.1 reads BOM-less
#     files as ANSI, and non-ASCII would corrupt it.
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$Bitrate = '192k',
    [string]$FfmpegPath = '',
    [switch]$Force,
    [switch]$DeleteSource
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 0. Work out the default Root.
#    NOTE: $PSScriptRoot is NOT reliable as a param() default - it can be an
#    empty string at binding time (observed when launched via
#    "powershell -File ...", which then fails with
#    "Cannot bind argument to parameter 'LiteralPath' because it is an empty
#    string"). So it is resolved here, after binding, and the working
#    directory is used as a second fallback.
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Root)) {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $Root = $PSScriptRoot
    } elseif ($MyInvocation.MyCommand.Path) {
        $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
    } else {
        $Root = (Get-Location).Path
    }
}

# ---------------------------------------------------------------------------
# 1. Locate ffmpeg.
#    Order: an explicit -FfmpegPath, then PATH, then a few common install
#    locations, then any ffmpeg.exe sitting next to this script.
#    Nothing is downloaded or installed.
# ---------------------------------------------------------------------------
function Find-Ffmpeg {
    if ($FfmpegPath -and (Test-Path -LiteralPath $FfmpegPath)) { return $FfmpegPath }

    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Common places ffmpeg ends up on Windows. Kept short and generic; the
    # reliable options are PATH or dropping ffmpeg.exe next to this script.
    $dirs = @(
        'C:\ffmpeg\bin',
        'C:\Program Files\ffmpeg\bin',
        "$env:LOCALAPPDATA\Microsoft\WinGet\Links"
    )
    foreach ($d in $dirs) {
        if ([string]::IsNullOrWhiteSpace($d)) { continue }
        $exe = Join-Path $d 'ffmpeg.exe'
        if (Test-Path -LiteralPath $exe) { return $exe }
    }

    # ffmpeg.exe shipped alongside this script.
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $local = Join-Path $PSScriptRoot 'ffmpeg.exe'
        if (Test-Path -LiteralPath $local) { return $local }
    }
    return $null
}

$ffmpeg = Find-Ffmpeg
if (-not $ffmpeg) {
    Write-Host ''
    Write-Host '  [X] ffmpeg.exe not found - cannot convert.' -ForegroundColor Red
    Write-Host ''
    Write-Host '  This script carries no decoder of its own; conversion needs ffmpeg.' -ForegroundColor Gray
    Write-Host '  Options:' -ForegroundColor Gray
    Write-Host '    1) Install ffmpeg and put it on PATH.' -ForegroundColor Gray
    Write-Host '    2) Drop ffmpeg.exe next to this script and re-run.' -ForegroundColor Gray
    Write-Host ''
    exit 1
}

# Verify this build can actually decode vorbis and encode mp3.
$caps = & $ffmpeg -hide_banner -decoders 2>&1
$capsE = & $ffmpeg -hide_banner -encoders 2>&1
$decOk = [bool]($caps  | Select-String 'vorbis')
$encOk = [bool]($capsE | Select-String 'libmp3lame|mp3_mf')
if (-not $decOk -or -not $encOk) {
    Write-Host ''
    Write-Host "  [X] $ffmpeg lacks required support: vorbis-decode=$decOk mp3-encode=$encOk" -ForegroundColor Red
    Write-Host ''
    exit 1
}
$encoder = if ($capsE | Select-String 'libmp3lame') { 'libmp3lame' } else { 'mp3_mf' }

Write-Host ''
Write-Host '  Ogg -> MP3 batch conversion' -ForegroundColor Cyan
Write-Host "  Root    : $Root"
Write-Host "  ffmpeg  : $ffmpeg"
Write-Host "  Encoder : $encoder @ $Bitrate"
Write-Host ''

if (!(Test-Path -LiteralPath $Root)) {
    Write-Host "  [X] Folder does not exist: $Root" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 2. Collect every .ogg
# ---------------------------------------------------------------------------
$oggs = @(Get-ChildItem -LiteralPath $Root -Filter '*.ogg' -Recurse -File -ErrorAction SilentlyContinue |
          Where-Object { $_.DirectoryName -notlike '*_backup*' })

if ($oggs.Count -eq 0) {
    Write-Host '  No .ogg files found - nothing to do.' -ForegroundColor Yellow
    Write-Host ''
    exit 0
}

Write-Host "  Found $($oggs.Count) .ogg file(s)" -ForegroundColor Gray
Write-Host ''

# ---------------------------------------------------------------------------
# 3. Convert one by one
# ---------------------------------------------------------------------------
$ok = 0; $skipped = 0; $failed = @()

foreach ($f in $oggs) {
    $target = [System.IO.Path]::ChangeExtension($f.FullName, '.mp3')
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\')

    if ((Test-Path -LiteralPath $target) -and -not $Force) {
        Write-Host "  skip  $rel  (mp3 already exists)" -ForegroundColor DarkGray
        $skipped++
        continue
    }

    # NOTE: the variable is $ffArgs, not $args - $args is an automatic
    # PowerShell variable and assigning to it breaks argument passing.
    $ffArgs = @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-i', $f.FullName,
        '-vn', '-ac', '2', '-ar', '44100',
        '-c:a', $encoder, '-b:a', $Bitrate,
        $target
    )

    $err = & $ffmpeg @ffArgs 2>&1
    $code = $LASTEXITCODE

    # ffmpeg can exit 0 yet leave an empty file, so check both.
    $size = if (Test-Path -LiteralPath $target) { (Get-Item -LiteralPath $target).Length } else { 0 }

    if ($code -eq 0 -and $size -gt 0) {
        Write-Host ("  done  {0}  ({1:N0} KB)" -f $rel, ($size / 1KB)) -ForegroundColor Green
        $ok++

        if ($DeleteSource) {
            try {
                Remove-Item -LiteralPath $f.FullName -Force
                Write-Host '        source ogg deleted' -ForegroundColor DarkGray
            } catch {
                Write-Host "        could not delete source: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host "  FAIL  $rel" -ForegroundColor Red
        if ($err) { $err | Select-Object -First 3 | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkGray } }
        $failed += $rel
        # Remove any partial output so a broken file cannot masquerade as success.
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------------------
# 4. Summary
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host ('  ' + ('-' * 46))
Write-Host "  ok $ok   skipped $skipped   failed $($failed.Count)"

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host '  These did not convert (their .ogg sources are untouched):' -ForegroundColor Yellow
    $failed | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
}

if (-not $DeleteSource -and $ok -gt 0) {
    Write-Host ''
    Write-Host '  All source .ogg files were kept. To clean them up later,' -ForegroundColor Gray
    Write-Host '  re-run with -DeleteSource once you are happy with the mp3s.' -ForegroundColor Gray
}

Write-Host ''
Write-Host '  Restart the pet afterwards so it rescans Sounds/.' -ForegroundColor Cyan
Write-Host ''
