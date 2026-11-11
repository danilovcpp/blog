[CmdletBinding()]
param(
    [ValidateSet('All','Core','Concurrency','Faults','Cleanup','Restore')][string]$Scenario = 'All',
    [string]$OutputDirectory = 'artifacts/validation/media',
    [ValidateRange(60,900)][int]$CleanupIntervalSeconds = 300,
    [ValidateRange(1,1000)][int]$CleanupBatchSize = 100,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoRoot
$previousOutput = $env:BLOG_MEDIA_VALIDATION_DIRECTORY
$previousInterval = $env:BLOG_MEDIA_CLEANUP_INTERVAL_SECONDS
$previousBatch = $env:BLOG_MEDIA_CLEANUP_BATCH_SIZE
try {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $env:BLOG_MEDIA_VALIDATION_DIRECTORY = $destination
    $env:BLOG_MEDIA_CLEANUP_INTERVAL_SECONDS = [string]$CleanupIntervalSeconds
    $env:BLOG_MEDIA_CLEANUP_BATCH_SIZE = [string]$CleanupBatchSize
    if ($Scenario -eq 'Restore') {
        $set = Join-Path $destination ('recovery-set-' + [guid]::NewGuid().ToString('N'))
        & (Join-Path $PSScriptRoot 'Invoke-MediaRecoveryValidation.ps1') -RecoverySetDirectory $set -OutputDirectory $destination
        if ($LASTEXITCODE -ne 0) { throw 'Recovery validation failed.' }
        return
    }
    $filters = @{
        All = 'FullyQualifiedName~.Media.|FullyQualifiedName~MediaReadinessTests|FullyQualifiedName~MediaRecoveryTests|FullyQualifiedName~MediaMigrationTests'
        Core = '(FullyQualifiedName~.Media.&FullyQualifiedName!~FullBoundaryProfile&FullyQualifiedName!~MediaResourceContainerTests)|FullyQualifiedName~MediaReadinessTests'
        Concurrency = 'FullyQualifiedName~MediaAllowanceTests|FullyQualifiedName~AvatarConcurrencyTests|FullyQualifiedName~PublicationMediaWriteTests|FullyQualifiedName~UploadContractTests'
        Faults = 'FullyQualifiedName~MediaFaultTests|FullyQualifiedName~UploadRetryTests|FullyQualifiedName~MediaTransportTests|FullyQualifiedName~MediaBinaryTransportTests|FullyQualifiedName~LibraryContractTests|FullyQualifiedName~AvatarContractTests|FullyQualifiedName~PublicationMediaReadTests|FullyQualifiedName~MediaSpoolTests|FullyQualifiedName~MediaReadinessTests'
        Cleanup = 'FullyQualifiedName~MediaCleanupTests|FullyQualifiedName~MediaAllowanceTests|FullyQualifiedName~MediaFaultTests'
    }
    $trx = 'media-' + $Scenario.ToLowerInvariant() + '.trx'
    $arguments = @('test','tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj','-c','Release','-p:RestoreLockedMode=true','--filter',$filters[$Scenario],
        '--logger',"trx;LogFileName=$trx",'--results-directory',$destination)
    if ($NoBuild) { $arguments += '--no-build' }
    $started = [DateTime]::UtcNow
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Media $Scenario suite failed." }
    $resultFile = Get-Item -LiteralPath (Join-Path $destination $trx)
    if ($resultFile.LastWriteTimeUtc -lt $started) { throw 'No test results were produced by this run.' }
    [xml]$results = Get-Content -LiteralPath $resultFile.FullName -Raw
    $counts = $results.TestRun.ResultSummary.Counters
    if ([int]$counts.total -eq 0 -or [int]$counts.total -ne [int]$counts.passed) { throw 'Missing, empty, failed or skipped Media checks.' }
    [ordered]@{ scenario=$Scenario; total=[int]$counts.total; passed=[int]$counts.passed; skipped=0; utc=[DateTimeOffset]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination ('counts-' + $Scenario.ToLowerInvariant() + '.json'))
    if ($Scenario -in @('All','Cleanup')) {
        foreach ($file in @('queue-plans.json','cleanup-profile.json')) {
            $evidence = Get-Item -LiteralPath (Join-Path $destination $file)
            if ($evidence.LastWriteTimeUtc -lt $started) { throw "No cleanup evidence was produced by this run: $file" }
        }
    }
    if ($Scenario -in @('All','Faults')) {
        & (Join-Path $PSScriptRoot 'Invoke-MediaResourceValidation.ps1') -OutputDirectory (Join-Path $destination 'resources')
    }
}
finally {
    $env:BLOG_MEDIA_VALIDATION_DIRECTORY = $previousOutput
    $env:BLOG_MEDIA_CLEANUP_INTERVAL_SECONDS = $previousInterval
    $env:BLOG_MEDIA_CLEANUP_BATCH_SIZE = $previousBatch
    Pop-Location
}
