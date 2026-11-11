#Requires -Version 7.0
param([uri] $AdminEndpoint, [uri] $S3Endpoint)
$ErrorActionPreference = 'Stop'
# Test-only provider: credentials arrive through the inherited anonymous stdin pipe.
# Production binds the same callbacks to SecretManagement or its deployment vault.
$provider = [Console]::In.ReadToEnd() | ConvertFrom-Json -AsHashtable
$read = { param($name) $provider[$name] }.GetNewClosure()
$write = { param($name, $value) $provider[$name] = $value }.GetNewClosure()
$script = Join-Path $PSScriptRoot 'Provision-MediaStorage.ps1'
$first = & $script -AdminEndpoint $AdminEndpoint -S3Endpoint $S3Endpoint -ReadSecret $read -WriteSecret $write -AllowLocalHttp
$second = & $script -AdminEndpoint $AdminEndpoint -S3Endpoint $S3Endpoint -ReadSecret $read -WriteSecret $write -AllowLocalHttp
if (($first | ConvertTo-Json -Compress) -ne ($second | ConvertTo-Json -Compress)) { throw 'Provisioning descriptor changed on replay.' }
$second | ConvertTo-Json -Compress
