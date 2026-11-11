[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RecoverySetDirectory,
    [string]$OutputDirectory = 'artifacts/validation/media'
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoRoot
$previousDirectory = $env:BLOG_MEDIA_RECOVERY_DIRECTORY
$previousInput = $env:BLOG_MEDIA_RECOVERY_INPUT
$previousKey = $env:BLOG_MEDIA_RECOVERY_KEY_BASE64
$previousEvidence = $env:BLOG_MEDIA_VALIDATION_DIRECTORY
try {
    $directory = [IO.Path]::GetFullPath($RecoverySetDirectory)
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $directory,$destination -Force | Out-Null
    $manifestPath = Join-Path $directory 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        if (!$previousKey) { throw 'An existing encrypted set requires BLOG_MEDIA_RECOVERY_KEY_BASE64 from a protected provider.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if (!$manifest.RecoverySetId -or !$manifest.Postgres -or !$manifest.Garage -or !$manifest.Schema) { throw 'Incomplete recovery manifest.' }
        foreach ($dependency in @('Postgres','Garage')) {
            $source = Get-Content -LiteralPath (Join-Path $repoRoot "tests/BlogEngine.IntegrationTests/Fixtures/${dependency}Image.cs") -Raw
            $reference = [regex]::Match($source, 'Reference\s*=\s*"([^"]+)"').Groups[1].Value
            if (!$reference -or $manifest.$dependency -cne $reference) { throw "Recovery image pin mismatch: $dependency" }
        }
        $parts = @('database.bin','metadata.bin','data.bin','keys.bin','expected.bin')
        if (@($manifest.Checksums.PSObject.Properties).Count -ne $parts.Count) { throw 'Unexpected recovery set parts.' }
        foreach ($part in $parts) {
            $path = Join-Path $directory $part
            if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $manifest.Checksums.$part) { throw "Recovery checksum mismatch: $part" }
        }
        $env:BLOG_MEDIA_RECOVERY_INPUT = '1'
    }
    else {
        if (@(Get-ChildItem -LiteralPath $directory -Force).Count -ne 0) { throw 'Refusing to overwrite an incomplete recovery set.' }
        if (!$previousKey) { $env:BLOG_MEDIA_RECOVERY_KEY_BASE64 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) }
        $env:BLOG_MEDIA_RECOVERY_INPUT = '0'
    }
    $env:BLOG_MEDIA_RECOVERY_DIRECTORY = $directory
    $env:BLOG_MEDIA_VALIDATION_DIRECTORY = $destination
    $started = [DateTime]::UtcNow
    dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release -m:1 -p:RestoreLockedMode=true --output (Join-Path $destination 'build') --filter 'FullyQualifiedName~MediaRecoveryTests' --logger 'trx;LogFileName=media-recovery.trx' --results-directory $destination
    if ($LASTEXITCODE -ne 0) { throw 'Recovery rehearsal failed.' }
    $resultFile = Get-Item -LiteralPath (Join-Path $destination 'media-recovery.trx')
    if ($resultFile.LastWriteTimeUtc -lt $started) { throw 'No recovery results were produced by this run.' }
    [xml]$results = Get-Content -LiteralPath $resultFile.FullName -Raw
    $counts = $results.TestRun.ResultSummary.Counters
    if ([int]$counts.total -eq 0 -or [int]$counts.total -ne [int]$counts.passed) { throw 'Missing, failed or skipped recovery checks.' }
    $report = Get-Item -LiteralPath (Join-Path $destination 'recovery.md')
    if ($report.LastWriteTimeUtc -lt $started) { throw 'No recovery evidence was produced by this run.' }
    [ordered]@{ scenario='Restore'; total=[int]$counts.total; passed=[int]$counts.passed; skipped=0 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'counts-restore.json')
    # The tests validate exact pinned images, authenticated decryption, SQL/grants and every control object.
    # An ephemeral key is intentionally discarded; use a protected provider to retain a reusable rehearsal set.
}
finally {
    $env:BLOG_MEDIA_RECOVERY_DIRECTORY = $previousDirectory
    $env:BLOG_MEDIA_RECOVERY_INPUT = $previousInput
    $env:BLOG_MEDIA_RECOVERY_KEY_BASE64 = $previousKey
    $env:BLOG_MEDIA_VALIDATION_DIRECTORY = $previousEvidence
    Pop-Location
}
