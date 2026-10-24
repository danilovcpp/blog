[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/validation/queries', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoRoot
try {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $testArguments = @('test', 'tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj', '-c', 'Release', '--filter', 'FullyQualifiedName~FullDatasetQueryTests|FullyQualifiedName~QueryInspectionTests|FullyQualifiedName~ModerationQueryTests', '--logger', 'trx;LogFileName=query-inspection.trx', '--results-directory', $destination)
    if ($NoBuild) { $testArguments += '--no-build' }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    [xml]$results = Get-Content -LiteralPath (Join-Path $destination 'query-inspection.trx') -Raw
    $counts = $results.TestRun.ResultSummary.Counters
    if ([int]$counts.total -eq 0 -or [int]$counts.total -ne [int]$counts.passed) { throw 'Missing, skipped or failed query checks.' }
}
finally { Pop-Location }
