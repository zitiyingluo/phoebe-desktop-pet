# Build the desktop pet.
#
# Why csc directly instead of `dotnet build` / `dotnet run pet.cs`:
#   This machine's sandbox denies writes to %APPDATA%\NuGet, ~\.dotnet and blocks
#   api.nuget.org, so MSBuild's restore step fails. The program needs zero NuGet
#   packages (WinForms ships with the framework), so calling the SDK's own Roslyn
#   compiler is both simpler and offline.
#
# Why a .dll + runtimeconfig.json instead of a bare .exe:
#   An exe produced by raw csc has no valid apphost and dies with 0xE0434352.
#   Producing a .dll and launching it with `dotnet` is the reliable path; the
#   launcher (start-pet.cmd) takes care of that.
#
# NOTE: keep this file ASCII-only -- Windows PowerShell 5.1 reads BOM-less files
# as ANSI, which mangles non-ASCII characters and breaks parsing.

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$dotnetRoot = 'C:\Program Files\dotnet'

$sdk = Get-ChildItem "$dotnetRoot\sdk" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$csc = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "csc not found: $csc" }

# Pick the reference pack version that has BOTH NETCore and WindowsDesktop refs
$picked = $null
foreach ($ver in '8.0.10', '10.0.0', '9.0.0') {
    $coreRoot = "$dotnetRoot\packs\Microsoft.NETCore.App.Ref\$ver\ref"
    $deskRoot = "$dotnetRoot\packs\Microsoft.WindowsDesktop.App.Ref\$ver\ref"
    if ((Test-Path $coreRoot) -and (Test-Path $deskRoot)) {
        $picked = @{
            ver  = $ver
            core = (Get-ChildItem $coreRoot -Directory | Select-Object -First 1).FullName
            desk = (Get-ChildItem $deskRoot -Directory | Select-Object -First 1).FullName
        }
        break
    }
}
if (-not $picked) { throw 'No matching reference packs found.' }

Write-Host "SDK      : $($sdk.Name)"
Write-Host "Ref pack : $($picked.ver)"

$outDll = Join-Path $here 'PhoebePet.dll'
$rsp = Join-Path $here 'build.rsp'

# Reference resolution.
#
# IMPORTANT: the WindowsDesktop ref pack wins over the NETCore ref pack for any
# name they both ship. Microsoft.NETCore.App.Ref contains small STUB copies of
# a few WindowsBase/PresentationCore types (WindowsBase.dll there is only 16KB
# and has no Freezable/MediaPlayer), and csc uses the FIRST matching /r for a
# given assembly identity. Listing the NETCore one first makes WPF types
# unresolvable with a confusing "Freezable claims to be in WindowsBase but
# could not be found" error. So: skip NETCore assemblies whose name also
# exists in the Desktop pack.
$deskNames = @{}
Get-ChildItem $picked.desk -Filter *.dll | ForEach-Object { $deskNames[$_.Name] = $true }

$refs = New-Object System.Collections.Generic.List[string]
foreach ($dir in @($picked.core, $picked.desk)) {
    Get-ChildItem $dir -Filter *.dll | ForEach-Object {
        if ($dir -eq $picked.core -and $deskNames.ContainsKey($_.Name)) { return }  # let Desktop win
        $refs.Add("/r:`"$($_.FullName)`"")
    }
}
Get-ChildItem $picked.desk -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    Get-ChildItem $_.FullName -Filter *.dll | ForEach-Object { $refs.Add("/r:`"$($_.FullName)`"") }
}

$lines = @(
    '/nologo'
    '/target:exe'
    '/codepage:65001'          # source is UTF-8: without this csc reads it as ANSI
    '/optimize+'
    '/nullable:enable'
    '/langversion:latest'
    '/platform:anycpu'
    "/out:`"$outDll`""
) + $refs + @(
    "`"$(Join-Path $here 'pet.cs')`""
    "`"$(Join-Path $here 'bubble.cs')`""
    "`"$(Join-Path $here 'inputbox.cs')`""
    "`"$(Join-Path $here 'chat.cs')`""
    "`"$(Join-Path $here 'activity.cs')`""
    "`"$(Join-Path $here 'sound.cs')`""
    "`"$(Join-Path $here 'usage.cs')`""
    "`"$(Join-Path $here 'balance.cs')`""
    "`"$(Join-Path $here 'art.cs')`""
)

# UTF8Encoding($false) => no BOM. A BOM here makes csc mis-read the response file,
# which silently drops /codepage:65001 and turns every Chinese literal into mojibake.
[System.IO.File]::WriteAllLines($rsp, $lines, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host 'Compiling...' -ForegroundColor Cyan
Push-Location $here
try {
    & dotnet $csc "@$rsp"
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($code -ne 0) { throw "Compile failed (exit $code)" }

# Host configuration: which runtime to load. Without this the dll cannot start.
$runtimeCfg = @'
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": {
      "name": "Microsoft.WindowsDesktop.App",
      "version": "8.0.0"
    },
    "configProperties": {
      "System.GC.Server": false,
      "System.Runtime.TieredPGO": true
    }
  }
}
'@
Set-Content -Path (Join-Path $here 'PhoebePet.runtimeconfig.json') -Value $runtimeCfg -Encoding UTF8

$dll = Get-Item $outDll
Write-Host ''
Write-Host "OK: $($dll.FullName)" -ForegroundColor Green
Write-Host "Size: $([math]::Round($dll.Length / 1KB, 1)) KB"
Write-Host 'Now run start-pet.cmd'
