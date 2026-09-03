#Requires -Version 7.0
<#
.SYNOPSIS
    Checks whether the mcp-server / streamdeck-plugin source trees are newer than their bundled
    copies under OnAirNative/Assets/ — i.e. whether someone edited the source and forgot to
    rebuild + recopy the bundled asset before committing/releasing.

.DESCRIPTION
    This exact mistake happened twice for real this session (once for the Stream Deck plugin's
    onair-client.ts, once for the MCP server), each time caught only by a developer manually
    eyeballing file timestamps before publishing a release. This script automates that same
    manual check.

    IMPORTANT CAVEAT (documented deliberately, not hidden): git does not preserve original
    modification timestamps on checkout — a fresh `git clone`/`actions/checkout` resets every
    file's mtime to the checkout moment, regardless of which commit last touched it. That makes
    this script's real teeth a LOCAL working-tree check: it reliably catches "I just edited a
    source file in my own checkout and haven't rebuilt yet" (exactly the scenario that caused
    both real bugs this session), run before a commit/release. In CI's fresh-checkout context it
    is a much weaker signal (most files land within the same checkout instant) — CI additionally
    rebuilds both projects from source as its own regression check (see ci.yml), which is the
    more reliable CI-side guard against a genuinely broken bundle.

.PARAMETER RepoRoot
    Root of the onair-native repo. Defaults to two levels up from this script's own location
    (scripts/check-bundled-assets.ps1 -> repo root).

.EXAMPLE
    pwsh scripts/check-bundled-assets.ps1
    Run before committing/releasing. Exits 0 if both bundles are at least as new as their
    sources, exits 1 (with guidance) if either is stale.
#>
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$stale = $false

function Get-NewestWriteTime {
    param([string[]]$Paths)
    $files = foreach ($p in $Paths) {
        if (Test-Path $p) { Get-ChildItem -Path $p -File -Recurse -ErrorAction SilentlyContinue }
    }
    if (-not $files) { return $null }
    ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
}

Write-Host "Checking bundled-asset freshness in: $RepoRoot`n"

# --- mcp-server -------------------------------------------------------------------------------
# Compare against the actual OUR-code build output (OnAirMcp.dll) — NOT the whole bundled folder,
# which also contains third-party NuGet DLLs whose own file dates are irrelevant noise here and
# would otherwise produce constant false positives.
$mcpSourcePaths = @(
    Join-Path $RepoRoot 'mcp-server\Program.cs'
    Join-Path $RepoRoot 'mcp-server\OnAirClient.cs'
    Join-Path $RepoRoot 'mcp-server\OnAirTools.cs'
    Join-Path $RepoRoot 'mcp-server\RemoteState.cs'
    Join-Path $RepoRoot 'mcp-server\ToolGate.cs'
    Join-Path $RepoRoot 'mcp-server\OnAirMcp.csproj'
)
$mcpBundledDll = Join-Path $RepoRoot 'OnAirNative\Assets\mcp-server\OnAirMcp.dll'

$mcpSourceNewest = Get-NewestWriteTime -Paths $mcpSourcePaths
if (-not (Test-Path $mcpBundledDll)) {
    Write-Host "[MCP SERVER] ✗ STALE — bundled OnAirMcp.dll not found at $mcpBundledDll" -ForegroundColor Red
    Write-Host "  Run: cd mcp-server; dotnet publish -c Release -o publish --self-contained false" -ForegroundColor Yellow
    Write-Host "  Then copy publish\* (minus .pdb) -> OnAirNative\Assets\mcp-server\" -ForegroundColor Yellow
    $stale = $true
}
else {
    $mcpBundledTime = (Get-Item $mcpBundledDll).LastWriteTimeUtc
    if ($mcpSourceNewest -and $mcpSourceNewest -gt $mcpBundledTime) {
        Write-Host "[MCP SERVER] ✗ STALE — source changed $((New-TimeSpan -Start $mcpBundledTime -End $mcpSourceNewest).ToString('c')) after the bundled build" -ForegroundColor Red
        Write-Host "  Newest source: $mcpSourceNewest UTC   Bundled OnAirMcp.dll: $mcpBundledTime UTC" -ForegroundColor Red
        Write-Host "  Run: cd mcp-server; dotnet publish -c Release -o publish --self-contained false" -ForegroundColor Yellow
        Write-Host "  Then copy publish\* (minus .pdb) -> OnAirNative\Assets\mcp-server\" -ForegroundColor Yellow
        $stale = $true
    }
    else {
        Write-Host "[MCP SERVER] ✓ up to date (bundled build is newer than every tracked source file)" -ForegroundColor Green
    }
}

# --- streamdeck-plugin -------------------------------------------------------------------------
# The shipped artifact is the single packed .streamDeckPlugin file (built via `streamdeck pack`),
# not the raw dist/ or bin/ folders, so that's what actually matters here.
$sdSourcePaths = @(
    Join-Path $RepoRoot 'streamdeck-plugin\src'
    Join-Path $RepoRoot 'streamdeck-plugin\package.json'
    Join-Path $RepoRoot 'streamdeck-plugin\rollup.config.mjs'
    Join-Path $RepoRoot 'streamdeck-plugin\tsconfig.json'
    Join-Path $RepoRoot 'streamdeck-plugin\com.souz4rafael.onair.sdPlugin\manifest.json'
    Join-Path $RepoRoot 'streamdeck-plugin\com.souz4rafael.onair.sdPlugin\dial-layout.json'
)
$sdBundledFile = Join-Path $RepoRoot 'OnAirNative\Assets\onair-remote.streamDeckPlugin'

$sdSourceNewest = Get-NewestWriteTime -Paths $sdSourcePaths
if (-not (Test-Path $sdBundledFile)) {
    Write-Host "[STREAM DECK PLUGIN] ✗ STALE — bundled .streamDeckPlugin not found at $sdBundledFile" -ForegroundColor Red
    Write-Host "  Run: cd streamdeck-plugin; npm run build; streamdeck pack com.souz4rafael.onair.sdPlugin -o dist -f" -ForegroundColor Yellow
    Write-Host "  Then copy dist\*.streamDeckPlugin -> OnAirNative\Assets\onair-remote.streamDeckPlugin" -ForegroundColor Yellow
    $stale = $true
}
else {
    $sdBundledTime = (Get-Item $sdBundledFile).LastWriteTimeUtc
    if ($sdSourceNewest -and $sdSourceNewest -gt $sdBundledTime) {
        Write-Host "[STREAM DECK PLUGIN] ✗ STALE — source changed $((New-TimeSpan -Start $sdBundledTime -End $sdSourceNewest).ToString('c')) after the bundled pack" -ForegroundColor Red
        Write-Host "  Newest source: $sdSourceNewest UTC   Bundled .streamDeckPlugin: $sdBundledTime UTC" -ForegroundColor Red
        Write-Host "  Run: cd streamdeck-plugin; npm run build; streamdeck pack com.souz4rafael.onair.sdPlugin -o dist -f" -ForegroundColor Yellow
        Write-Host "  Then copy dist\*.streamDeckPlugin -> OnAirNative\Assets\onair-remote.streamDeckPlugin" -ForegroundColor Yellow
        $stale = $true
    }
    else {
        Write-Host "[STREAM DECK PLUGIN] ✓ up to date (bundled pack is newer than every tracked source file)" -ForegroundColor Green
    }
}

Write-Host ""
if ($stale) {
    Write-Host "Bundled-asset staleness check FAILED. See guidance above." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "Bundled-asset staleness check passed." -ForegroundColor Green
    exit 0
}
