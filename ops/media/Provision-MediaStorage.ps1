#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri] $AdminEndpoint,
    [Parameter(Mandatory)][uri] $S3Endpoint,
    # Callbacks bind to a protected provider (e.g. SecretManagement), never CLI values/files.
    [Parameter(Mandatory)][scriptblock] $ReadSecret,
    [Parameter(Mandatory)][scriptblock] $WriteSecret,
    [string] $SecretPrefix = 'Blog.Media',
    [string] $OriginalsBucket = 'blog-originals',
    [string] $RenditionsBucket = 'blog-renditions',
    [switch] $AllowLocalHttp
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($endpoint in @($AdminEndpoint, $S3Endpoint)) {
    if (-not $endpoint.IsAbsoluteUri -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.AbsolutePath -ne '/' -or
        ($endpoint.Scheme -ne 'https' -and -not ($AllowLocalHttp -and $endpoint.Scheme -eq 'http' -and $endpoint.IsLoopback))) {
        throw 'Endpoints must be trusted HTTPS roots; HTTP requires explicit loopback opt-in.'
    }
}
if ($OriginalsBucket -eq $RenditionsBucket) { throw 'Media buckets must differ.' }
foreach ($bucket in @($OriginalsBucket, $RenditionsBucket)) {
    if ($bucket -notmatch '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$') { throw 'Invalid private bucket name.' }
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [timespan]::FromSeconds(60)
$stage = 'reading protected credentials'
try {
    $adminToken = & $ReadSecret "$SecretPrefix.AdminToken"
    if ([string]::IsNullOrWhiteSpace($adminToken)) { throw 'Missing provisioning token.' }
    function Invoke-GarageAdmin([string] $Operation, [object] $Body = $null, [string] $Query = '') {
        $method = if ($null -eq $Body) { [System.Net.Http.HttpMethod]::Get } else { [System.Net.Http.HttpMethod]::Post }
        $request = [System.Net.Http.HttpRequestMessage]::new($method, [uri]::new($AdminEndpoint, "v2/$Operation$Query"))
        try {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $adminToken)
            if ($null -ne $Body) { $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8 -Compress), [Text.Encoding]::UTF8, 'application/json') }
            $response = $client.Send($request)
            try {
                if (-not $response.IsSuccessStatusCode) { throw "Garage admin operation $Operation failed (HTTP $([int]$response.StatusCode))." }
                $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if ($json) { return ConvertFrom-Json -InputObject $json }
            } finally { $response.Dispose() }
        } finally { $request.Dispose() }
    }

    $stage = 'provisioning runtime key'
    $accessKey = & $ReadSecret "$SecretPrefix.AccessKey"
    $secretKey = & $ReadSecret "$SecretPrefix.SecretKey"
    if ([string]::IsNullOrWhiteSpace($accessKey) -ne [string]::IsNullOrWhiteSpace($secretKey)) { throw 'Incomplete runtime credentials in provider.' }
    if ([string]::IsNullOrWhiteSpace($accessKey)) {
        $key = Invoke-GarageAdmin 'CreateKey' @{ name = 'blog-media-runtime'; deny = @{ createBucket = $true } }
        $accessKey = $key.accessKeyId
        $secretKey = $key.secretAccessKey
        # Persist before granting permissions; interrupted provisioning can safely resume.
        $null = & $WriteSecret "$SecretPrefix.AccessKey" $accessKey
        $null = & $WriteSecret "$SecretPrefix.SecretKey" $secretKey
    }
    $null = Invoke-GarageAdmin 'UpdateKey' @{ deny = @{ createBucket = $true } } "?id=$([uri]::EscapeDataString($accessKey))"
    $runtimeInfo = Invoke-GarageAdmin 'GetKeyInfo' -Query "?id=$([uri]::EscapeDataString($accessKey))"
    if ($runtimeInfo.expired) { throw 'Runtime key has expired.' }
    foreach ($grant in $runtimeInfo.buckets) {
        if (@($grant.globalAliases | Where-Object { $_ -in @($OriginalsBucket, $RenditionsBucket) }).Count -eq 0) {
            throw 'Runtime key has unexpected bucket grants.'
        }
    }

    # Paginate rather than materializing an unbounded cluster-wide bucket list.
    $existing = @{}
    $offset = ''
    do {
        $page = @(Invoke-GarageAdmin 'ListBuckets' -Query "?limit=100$offset")
        foreach ($item in $page) {
            foreach ($alias in $item.globalAliases) {
                if ($alias -in @($OriginalsBucket, $RenditionsBucket)) { $existing[$alias] = $item.id }
            }
        }
        if ($page.Count -eq 100) { $offset = '&offset=' + [uri]::EscapeDataString($page[-1].id) }
    } while ($page.Count -eq 100)

    $sentinelKey = '_blog/health-v1'
    $bytes = [Text.Encoding]::UTF8.GetBytes("BlogEngine media sentinel v1`n")
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    foreach ($bucket in @($OriginalsBucket, $RenditionsBucket)) {
        $stage = 'provisioning private bucket'
        $id = $existing[$bucket]
        if (-not $id) { $id = (Invoke-GarageAdmin 'CreateBucket' @{ globalAlias = $bucket }).id }
        $info = Invoke-GarageAdmin 'GetBucketInfo' -Query "?id=$id"
        # Never silently adopt a bucket accessible to another key.
        if (@($info.keys | Where-Object { $_.accessKeyId -ne $accessKey }).Count -ne 0) { throw 'Bucket has unexpected key grants.' }
        $null = Invoke-GarageAdmin 'UpdateBucket' @{ websiteAccess = @{ enabled = $false } } "?id=$id"
        $null = Invoke-GarageAdmin 'DenyBucketKey' @{ bucketId = $id; accessKeyId = $accessKey; permissions = @{ owner = $true } }
        $null = Invoke-GarageAdmin 'AllowBucketKey' @{ bucketId = $id; accessKeyId = $accessKey; permissions = @{ read = $true; write = $true; owner = $false } }

        $stage = 'writing readiness sentinel'
        # Small fixed sentinel only. User content uses the production streaming SDK adapter.
        $now = [datetime]::UtcNow
        $timestamp = $now.ToString('yyyyMMddTHHmmssZ', [cultureinfo]::InvariantCulture)
        $date = $now.ToString('yyyyMMdd', [cultureinfo]::InvariantCulture)
        $path = "/$bucket/$sentinelKey"
        $authority = $S3Endpoint.Authority
        $headers = "host:$authority`nx-amz-content-sha256:$hash`nx-amz-date:$timestamp`n"
        $signedHeaders = 'host;x-amz-content-sha256;x-amz-date'
        $canonical = "PUT`n$path`n`n$headers`n$signedHeaders`n$hash"
        $canonicalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
        $scope = "$date/garage/s3/aws4_request"
        $stringToSign = "AWS4-HMAC-SHA256`n$timestamp`n$scope`n$canonicalHash"
        $signingKey = [Text.Encoding]::UTF8.GetBytes("AWS4$secretKey")
        foreach ($part in @($date, 'garage', 's3', 'aws4_request')) { $signingKey = [Security.Cryptography.HMACSHA256]::HashData($signingKey, [Text.Encoding]::UTF8.GetBytes($part)) }
        $signature = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData($signingKey, [Text.Encoding]::UTF8.GetBytes($stringToSign))).ToLowerInvariant()
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, [uri]::new($S3Endpoint, $path))
        try {
            $request.Content = [System.Net.Http.ByteArrayContent]::new($bytes)
            $request.Content.Headers.ContentMD5 = [Security.Cryptography.MD5]::HashData($bytes)
            $null = $request.Headers.TryAddWithoutValidation('x-amz-content-sha256', $hash)
            $null = $request.Headers.TryAddWithoutValidation('x-amz-date', $timestamp)
            $null = $request.Headers.TryAddWithoutValidation('Authorization', "AWS4-HMAC-SHA256 Credential=$accessKey/$scope, SignedHeaders=$signedHeaders, Signature=$signature")
            $response = $client.Send($request)
            try { if (-not $response.IsSuccessStatusCode) { throw "Sentinel PUT failed (HTTP $([int]$response.StatusCode))." } }
            finally { $response.Dispose() }
        } finally { $request.Dispose() }
    }
    # Non-secret descriptor for readiness configuration/evidence.
    [pscustomobject]@{ OriginalsBucket = $OriginalsBucket; RenditionsBucket = $RenditionsBucket; SentinelKey = $sentinelKey; SentinelLength = $bytes.Length; SentinelSha256 = $hash }
} catch {
    # Provider/HTTP exception text can contain credentials or response bodies.
    throw "Media provisioning failed during $stage. Check protected provider and Garage operator diagnostics."
} finally {
    $client.Dispose()
    $adminToken = $accessKey = $secretKey = $null
}
