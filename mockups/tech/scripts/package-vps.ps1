[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$techRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $techRoot '../..')).Path
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$packageName = 'retrotech-workshop-vps-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$packageRoot = Join-Path $artifactsRoot $packageName
$siteRoot = Join-Path $packageRoot 'site'
$archivePath = Join-Path $artifactsRoot ($packageName + '.zip')
$previousTheme = $env:VITE_THEME
$previousBrand = $env:VITE_BRAND

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $packageRoot | Out-Null

Push-Location $techRoot
try {
    # Explicit values also override any local Vite env files.
    $env:VITE_THEME = 'workshop'
    $env:VITE_BRAND = 'retrotech'
    # Invoke the local CLIs directly: npm.ps1 can consume forwarded --flags.
    & node (Join-Path $techRoot 'node_modules/typescript/bin/tsc') -b
    if ($LASTEXITCODE -ne 0) {
        throw "TypeScript check failed (exit $LASTEXITCODE). No archive was created."
    }
    & node (Join-Path $techRoot 'node_modules/vite/bin/vite.js') build --base / --outDir $siteRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Production build failed (exit $LASTEXITCODE). No archive was created."
    }

    Copy-Item -LiteralPath (Join-Path $techRoot 'deploy/nginx.conf') -Destination (Join-Path $packageRoot 'nginx.conf')
    Copy-Item -LiteralPath (Join-Path $techRoot 'deploy/README.md') -Destination (Join-Path $packageRoot 'README.md')
    $buildInfo = [ordered]@{
        brand = 'retrotech'
        theme = 'workshop'
        accent = '#276451'
        basePath = '/'
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        nodeVersion = (& node --version)
        demoData = $true
    }
    $buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'build-info.json') -Encoding utf8NoBOM

    # ZipFile includes all static files and uses portable ZIP entry paths.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $packageRoot,
        $archivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding ascii
    Write-Host "Package: $archivePath"
    Write-Host "Static site: $siteRoot"
    Write-Host "SHA256: $hash"
}
finally {
    $env:VITE_THEME = $previousTheme
    $env:VITE_BRAND = $previousBrand
    Pop-Location
}
