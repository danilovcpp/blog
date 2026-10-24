[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Seed', 'Run')][string]$Mode,
    [ValidateSet(20261024)][int]$Seed = 20261024,
    [string]$OutputDirectory = 'artifacts/performance/sc-005',
    [string]$QueryEvidenceDirectory = 'artifacts/validation/queries',
    [switch]$Diagnostic
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoRoot
try {
    if ($Mode -eq 'Run' -and (Test-Path -LiteralPath (Join-Path $QueryEvidenceDirectory 'sc005-summary.json'))) {
        $queryDestination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'queries'
        New-Item -ItemType Directory -Path $queryDestination -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $QueryEvidenceDirectory 'sc005-summary.json') -Destination $queryDestination
        foreach ($folder in @('sc005', 'taxonomy')) {
            Copy-Item -LiteralPath (Join-Path $QueryEvidenceDirectory $folder) -Destination $queryDestination -Recurse -Force
        }
    }
    $arguments = @('run', '--project', 'tests/BlogEngine.Performance', '-c', 'Release', '--no-launch-profile', '--', '--mode', $Mode, '--seed', "$Seed", '--output', [IO.Path]::GetFullPath($OutputDirectory))
    if ($Diagnostic) { $arguments += @('--diagnostic', 'true') }
    & dotnet @arguments
    exit $LASTEXITCODE
}
finally { Pop-Location }
