<#
.SYNOPSIS
  Build Mixion and emit a single .exe to ./output.

.DESCRIPTION
  Builds the Angular SPA for production, copies the bundle into
  BE/Mixion.Host/wwwroot/ (where it gets embedded into the assembly),
  publishes the .NET 8 host as a single-file .exe, and lands the result
  in ./output/Mixion.exe.

  The driver-presence check (VB-CABLE) runs at app launch from inside the
  packaged binary, not here.

.PARAMETER Mode
  Which kind of .exe to produce.
    portable - self-contained single .exe (~70 MB). Runs anywhere on Windows
               10+ with no .NET install required. Default.
    minimal  - framework-dependent single .exe (~5 MB). Requires .NET 8
               Desktop Runtime to be installed on the target machine.

.PARAMETER Clean
  Delete ./output and BE/Mixion.Host/wwwroot before building.

.PARAMETER Configuration
  .NET build configuration. Release (default) or Debug.

.PARAMETER Version
  Version to stamp into Mixion.exe (file, product and assembly version, and
  what the UI shows), e.g. 1.2.0 or 1.3.0-beta.1. The release workflow passes
  the version from the tag. Left out, it comes from the git tags: exactly at a
  v* tag with no local changes, that tag's version (1.2.0); otherwise
  `git describe` style, e.g. 1.2.0-3-gabc1234 for three commits after v1.2.0,
  with -dirty appended when there are uncommitted changes.

.EXAMPLE
  .\build.ps1
  Self-contained portable Mixion.exe in ./output.

.EXAMPLE
  .\build.ps1 -Version 1.2.0
  The same, stamped as version 1.2.0 — what the release workflow runs.

.EXAMPLE
  .\build.ps1 -Mode minimal -Clean
  Wipes previous output, then produces a small framework-dependent .exe.
#>

[CmdletBinding()]
param(
    [ValidateSet('portable', 'minimal')]
    [string]$Mode = 'portable',

    [switch]$Clean,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root      = $PSScriptRoot
$bePath    = Join-Path $root 'BE\Mixion.Host'
$beWwwroot = Join-Path $bePath 'wwwroot'
$ngPath    = Join-Path $root 'FE\angular'
$ngDist    = Join-Path $ngPath 'dist\mixion\browser'
$outPath   = Join-Path $root 'output'
$publishDir = Join-Path $outPath '.publish'

function Step([string]$msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Require-Tool([string]$name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$name' not found in PATH."
    }
}

# "v1.2.0-0-ga4994b2" is exactly the tagged commit → "1.2.0". Anything else —
# commits after the tag, or "-dirty" for uncommitted changes — keeps the
# describe form without the v, e.g. "1.2.0-3-gabc1234" (a valid SemVer).
function ConvertFrom-GitDescribe([string]$described) {
    if ($described -notmatch '^v(?<tag>\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)-(?<height>\d+)-(?<rest>g[0-9a-f]+(-dirty)?)$') {
        return $null
    }
    if ($Matches['height'] -eq '0' -and $Matches['rest'] -notlike '*-dirty') { return $Matches['tag'] }
    return "$($Matches['tag'])-$($Matches['height'])-$($Matches['rest'])"
}

function Get-VersionFromGit {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
    # git reports "no names found" on stderr when there is no tag; that isn't an error here.
    $ErrorActionPreference = 'Continue'
    $described = & git -C $root describe --tags --match 'v[0-9]*' --long --dirty 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $described) { return $null }
    $text = ([string]$described).Trim()
    return ConvertFrom-GitDescribe $text
}

# -----------------------------------------------------------------------------
# 1. Prerequisites
# -----------------------------------------------------------------------------
Step 'Checking prerequisites'
Require-Tool dotnet
Require-Tool node
Require-Tool npm
Write-Host "    dotnet $((dotnet --version).Trim())"
Write-Host "    node   $((node --version).Trim())"
Write-Host "    npm    $((npm --version).Trim())"

$resolvedVersion = if ($Version) { $Version } else { Get-VersionFromGit }
$versionSource   = if ($Version) { '-Version' } else { 'git tags' }
if (-not $resolvedVersion) {
    $resolvedVersion = '0.0.0-dev'
    $versionSource   = 'no v* tag found; pass -Version to set one'
}
Write-Host "    Mixion $resolvedVersion ($versionSource)"

if (-not (Test-Path $bePath)) { throw "Backend project not found at $bePath" }
if (-not (Test-Path $ngPath)) { throw "Angular project not found at $ngPath" }

# -----------------------------------------------------------------------------
# 2. Clean
# -----------------------------------------------------------------------------
if ($Clean) {
    Step 'Cleaning previous output'
    if (Test-Path $outPath)   { Remove-Item $outPath -Recurse -Force }
    if (Test-Path $beWwwroot) { Remove-Item $beWwwroot -Recurse -Force }
}
New-Item -ItemType Directory -Path $outPath -Force | Out-Null

# -----------------------------------------------------------------------------
# 3. Build Angular (production)
# -----------------------------------------------------------------------------
Step 'Building Angular (production)'
Push-Location $ngPath
try {
    if (Test-Path (Join-Path $ngPath 'package-lock.json')) {
        & npm ci
    } else {
        & npm install
    }
    if ($LASTEXITCODE -ne 0) { throw 'npm install (Angular) failed.' }

    & npm run build -- --configuration=production
    if ($LASTEXITCODE -ne 0) { throw 'Angular build failed.' }
} finally {
    Pop-Location
}

if (-not (Test-Path $ngDist)) {
    throw "Expected Angular bundle at $ngDist after build. Check angular.json outputPath."
}

# -----------------------------------------------------------------------------
# 4. Copy Angular bundle into the host's wwwroot/ (will be embedded by csproj)
# -----------------------------------------------------------------------------
Step "Staging Angular bundle into BE/Mixion.Host/wwwroot/"
if (Test-Path $beWwwroot) { Remove-Item $beWwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $beWwwroot -Force | Out-Null
Copy-Item -Path (Join-Path $ngDist '*') -Destination $beWwwroot -Recurse -Force

$indexFile = Join-Path $beWwwroot 'index.html'
if (-not (Test-Path $indexFile)) {
    throw "Angular dist did not produce an index.html in $beWwwroot."
}
$bundleSize = [math]::Round(((Get-ChildItem $beWwwroot -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "    Embedded UI bundle: $bundleSize MB"

# -----------------------------------------------------------------------------
# 5. Publish .NET host
# -----------------------------------------------------------------------------
$versionLabel = ", version $resolvedVersion"
Step "Publishing .NET host (mode: $Mode, configuration: $Configuration$versionLabel)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$selfContained = if ($Mode -eq 'portable') { 'true' } else { 'false' }

# -p:Version sets the file, product, assembly and informational versions together.
& dotnet publish $bePath `
    -c $Configuration `
    -r win-x64 `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$resolvedVersion `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$producedExe = Join-Path $publishDir 'Mixion.exe'
if (-not (Test-Path $producedExe)) {
    throw "Expected Mixion.exe at $producedExe (check <AssemblyName> in csproj)."
}

# -----------------------------------------------------------------------------
# 6. Move the exe to ./output and clean the publish staging
# -----------------------------------------------------------------------------
Step 'Collecting artifact into ./output'
$finalExe = Join-Path $outPath 'Mixion.exe'
Move-Item -Path $producedExe -Destination $finalExe -Force

Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
$exeSize = [math]::Round((Get-Item $finalExe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "    $finalExe ($exeSize MB, $Mode$versionLabel)"
Write-Host ""
Write-Host "Run it:  $finalExe"
Write-Host "         (it will probe for VB-CABLE, reuse its last port or pick a free one, and open your browser)"
