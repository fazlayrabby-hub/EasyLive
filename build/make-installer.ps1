<#
.SYNOPSIS  Builds artifacts\EasyLive-Setup-<version>.exe with Inno Setup 6 (runs build\publish.ps1 first if needed).
.PARAMETER Version  Default 1.0.0.
.PARAMETER Rebuild  Re-run publish.ps1 even if the published folder exists.
.PARAMETER Iscc     Path to ISCC.exe. Default: searched in the usual per-user and machine-wide locations.
#>
[CmdletBinding()]
param([string]$Version = '1.0.0', [switch]$Rebuild, [string]$Iscc)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pkg  = Join-Path $repo "artifacts\EasyLive-$Version"

if ($Rebuild -or -not (Test-Path (Join-Path $pkg 'EasyLive.exe'))) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version
}
if (-not (Test-Path (Join-Path $pkg 'EasyLive.exe'))) { throw "Published package not found: $pkg" }

if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php (per-user install is fine).' }

& $Iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'installer\EasyLive.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$exe = Join-Path $repo "artifacts\EasyLive-Setup-$Version.exe"
'{0}  {1:N1} MB' -f $exe, ((Get-Item $exe).Length / 1MB)
