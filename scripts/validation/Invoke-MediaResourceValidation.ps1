[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/validation/media/resources')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$suffix = [guid]::NewGuid().ToString('N')
$volume = "blog-media-resource-$suffix"
$container = "blog-media-resource-$suffix"
$image = "blog-media-resource:$suffix"
Push-Location $repoRoot
try {
    docker build --target media-resource-validation -f src/BlogEngine.Api/Dockerfile -t $image .
    if ($LASTEXITCODE -ne 0) { throw 'Reference container build failed.' }
    docker volume create $volume | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Resource volume creation failed.' }
    # Only prepare the disposable volume as root; both measured processes run under the image's APP_UID.
    docker run --rm --user 0 --entrypoint /bin/sh --mount "type=volume,src=$volume,dst=/spool" $image -c 'chown "$APP_UID:$APP_UID" /spool'
    if ($LASTEXITCODE -ne 0) { throw 'Resource volume permissions failed.' }
    docker run --name $container -d --memory 1g --mount "type=volume,src=$volume,dst=/spool" -e BLOG_MEDIA_RESOURCE_SPOOL=/spool -e BLOG_MEDIA_CRASH=1 $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Crash fixture did not start.' }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        docker cp "${container}:/spool/crash.ready" (Join-Path $destination 'crash.ready') 2>$null
        if ($LASTEXITCODE -eq 0) { break }
        $running = docker inspect --format '{{.State.Running}}' $container
        if ($LASTEXITCODE -ne 0 -or $running -ne 'true' -or [DateTimeOffset]::UtcNow -ge $deadline) {
            docker logs --tail 30 $container
            throw 'Crash fixture never started a transfer.'
        }
        Start-Sleep -Milliseconds 250
    } while ($true)
    docker kill $container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Crash fixture could not be stopped.' }
    $crashed = docker inspect $container | ConvertFrom-Json
    if ($crashed[0].State.OOMKilled) { throw 'Crash fixture was OOM killed before the deliberate termination.' }
    docker rm $container | Out-Null
    docker run --name $container --memory 1g --mount "type=volume,src=$volume,dst=/spool" -e BLOG_MEDIA_RESOURCE_SPOOL=/spool $image
    if ($LASTEXITCODE -ne 0) { throw 'Reference pressure/restart checks failed.' }
    $state = docker inspect $container | ConvertFrom-Json
    if ($state[0].State.OOMKilled -or $state[0].RestartCount -ne 0) { throw 'Reference pressure run restarted or was OOM killed.' }
    docker cp "${container}:/spool/resources.json" (Join-Path $destination 'resources.json')
    if ($LASTEXITCODE -ne 0) { throw 'Missing measured memory evidence.' }
    $evidence = Get-Content -LiteralPath (Join-Path $destination 'resources.json') -Raw | ConvertFrom-Json
    if ($evidence.peakBytes -le 0 -or $evidence.peakBytes -ge 1073741824 -or !$evidence.reclaimed) { throw 'Invalid resource evidence.' }
}
finally {
    docker rm -f $container 2>$null | Out-Null
    docker volume rm $volume 2>$null | Out-Null
    docker image rm $image 2>$null | Out-Null
    Pop-Location
}
