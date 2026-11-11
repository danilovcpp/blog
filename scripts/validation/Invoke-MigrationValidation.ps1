[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/validation/migrations', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoRoot
$previousOutput = $env:BLOG_MIGRATION_VALIDATION_DIRECTORY
try {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $env:BLOG_MIGRATION_VALIDATION_DIRECTORY = $destination
    $filter = 'FullyQualifiedName~MigrationValidationTests|FullyQualifiedName~DatabasePermissionsTests|FullyQualifiedName~DatabasePrivilegeTests|FullyQualifiedName~ViewMigrationTests|FullyQualifiedName~PublishingConstraintTests|FullyQualifiedName~CommentConstraintTests|FullyQualifiedName~MediaMigrationTests|FullyQualifiedName~MediaConstraintTests'
    $testArguments = @('test', 'tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj', '-c', 'Release', '-p:RestoreLockedMode=true', '--filter', $filter, '--logger', 'trx;LogFileName=migrations.trx', '--results-directory', $destination)
    if ($NoBuild) { $testArguments += '--no-build' }
    $started = [DateTime]::UtcNow
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    [xml]$results = Get-Content -LiteralPath (Join-Path $destination 'migrations.trx') -Raw
    $counts = $results.TestRun.ResultSummary.Counters
    if ([int]$counts.total -eq 0 -or [int]$counts.total -ne [int]$counts.passed) { throw 'Missing, skipped or failed migration checks.' }
    $mediaUpgrade = Get-Item -LiteralPath (Join-Path $destination 'media-upgrade.json')
    if ($mediaUpgrade.LastWriteTimeUtc -lt $started) { throw 'No Media upgrade evidence was produced by this run.' }
    $previousConnection = $env:ConnectionStrings__Blog
    try {
        $env:ConnectionStrings__Blog = 'Host=127.0.0.1;Database=design_time_only;Username=unused'
        dotnet ef migrations has-pending-model-changes --no-build --configuration Release --project src/BlogEngine.Infrastructure --startup-project src/BlogEngine.Api
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet ef migrations script --idempotent --no-build --configuration Release --project src/BlogEngine.Infrastructure --startup-project src/BlogEngine.Api --output (Join-Path $destination 'reviewed-chain.sql')
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally { $env:ConnectionStrings__Blog = $previousConnection }
}
finally {
    $env:BLOG_MIGRATION_VALIDATION_DIRECTORY = $previousOutput
    Pop-Location
}
