#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$IdentityMigrationSql,
    [string]$BlogMigrationSql = 'artifacts/validation/migrations/reviewed-chain.sql',
    [string]$IdentityImage = 'danilovcpp/identity:latest',
    [string]$BlogImage = 'danilovcpp/blog:phase10',
    [string]$OperatorImage = 'blog-engine-operator:phase10',
    [string]$OutputDirectory = 'artifacts/validation/live-identity'
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runId = [Guid]::NewGuid().ToString('N')
$prefix = "blog-identity-$runId"
$network = "$prefix-net"
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$secretDirectory = Join-Path $temporaryRoot $prefix
$containers = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[object]]::new()
$sensitiveValues = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ status = 'failed'; startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); checks = $checks }
$failed = $false
$cleanupPassed = $true

function Invoke-Docker {
    param([string[]]$Arguments, [string]$InputText)
    if ($PSBoundParameters.ContainsKey('InputText')) { $lines = $InputText | & docker @Arguments 2>&1 }
    else { $lines = & docker @Arguments 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "Docker $($Arguments[0]) failed; output suppressed to protect fixture secrets." }
    return ($lines -join "`n")
}
function New-Secret {
    $value = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $sensitiveValues.Add($value)
    return $value
}
function Write-EnvironmentFile([string]$Name, [hashtable]$Values) {
    $path = Join-Path $secretDirectory "$Name.env"
    [IO.File]::WriteAllLines($path, @($Values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }))
    return $path
}
function Start-FixtureContainer([string]$Name, [string[]]$ContainerArguments) {
    $containers.Add($Name)
    $null = Invoke-Docker -Arguments (@('run', '--detach', '--name', $Name, '--label', "blog.identity-smoke=$runId") + $ContainerArguments)
}
function Get-HttpBase([string]$Name, [int]$Port) {
    $binding = Invoke-Docker -Arguments @('port', $Name, "$Port/tcp")
    if ($binding -notmatch '^127\.0\.0\.1:\d+$') { throw 'Fixture port is not restricted to loopback.' }
    return "http://$binding"
}
function Request {
    param([string]$Base, [string]$Path, [string]$Method = 'GET', [object]$Body,
        [string]$Token, [hashtable]$ExtraHeaders = @{}, [int]$Expected = 200, [string]$Check)
    $headers = @{} + $ExtraHeaders
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $parameters = @{ Uri = "$Base$Path"; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 10 }
    if ($PSBoundParameters.ContainsKey('Body')) { $parameters.ContentType = 'application/json'; $parameters.Body = $Body | ConvertTo-Json -Compress -Depth 8 }
    try { $response = Invoke-WebRequest @parameters }
    catch { throw "HTTP transport failed for $Method $Path; response suppressed." }
    if ([int]$response.StatusCode -ne $Expected) { throw "Unexpected HTTP status for $Method $Path`: $($response.StatusCode), expected $Expected." }
    if ($Check) { Add-Check $Check }
    if ($response.Content) { return ($response.Content | ConvertFrom-Json -AsHashtable) }
}
function Add-Check([string]$Name) {
    $checks.Add(@{ name = $Name; status = 'passed' })
    Write-Output "PASS $Name" | Out-Host
}
function Wait-Ready([string]$Base) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest "$Base/health/ready" -SkipHttpErrorCheck -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Fixture readiness did not become healthy.'
}
function Register-VerifiedUser([string]$Email) {
    $password = 'Disposable account ' + (New-Secret)
    $null = Request -Base $identityBase -Path '/api/v1/auth/register' -Method POST -Body @{ email = $Email; password = $password; clientId = 'blog' } -Expected 202
    $verificationToken = $null
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $messages = Request -Base $mailBase -Path '/api/v1/messages'
        foreach ($message in $messages.messages) {
            if (@($message.To | Where-Object { $_.Address -eq $Email }).Count -eq 0) { continue }
            $mail = Request -Base $mailBase -Path "/api/v1/message/$($message.ID)"
            $match = [regex]::Match($mail.Text, '#token=([A-Za-z0-9_-]{43})')
            if ($match.Success) { $verificationToken = $match.Groups[1].Value; break }
        }
        if ($verificationToken) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $verificationToken) { throw 'Verification email was not received in disposable Mailpit.' }
    $sensitiveValues.Add($verificationToken)
    $null = Request -Base $identityBase -Path '/api/v1/auth/email/verify' -Method POST -Body @{ token = $verificationToken } -Expected 204
    return @{ email = $Email; password = $password; clientId = 'blog' }
}
function Remember-Pair([hashtable]$Pair) {
    if ($Pair.tokenType -ne 'Bearer' -or -not $Pair.accessToken -or -not $Pair.refreshToken) { throw 'Identity token-pair contract mismatch.' }
    $sensitiveValues.Add($Pair.accessToken)
    $sensitiveValues.Add($Pair.refreshToken)
}

Push-Location $repoRoot
try {
    $identitySqlPath = (Resolve-Path -LiteralPath $IdentityMigrationSql).Path
    $blogSqlPath = (Resolve-Path -LiteralPath $BlogMigrationSql).Path
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    New-Item -ItemType Directory -Path $secretDirectory | Out-Null
    $identityReference = Invoke-Docker -Arguments @('image', 'inspect', $IdentityImage, '--format', '{{.Id}}')
    $blogReference = Invoke-Docker -Arguments @('image', 'inspect', $BlogImage, '--format', '{{.Id}}')
    $operatorReference = Invoke-Docker -Arguments @('image', 'inspect', $OperatorImage, '--format', '{{.Id}}')
    $report.identity = @{ image = $IdentityImage; id = $identityReference; repoDigests = ((Invoke-Docker -Arguments @('image', 'inspect', $IdentityImage, '--format', '{{json .RepoDigests}}')) | ConvertFrom-Json) }
    $report.blog = @{ image = $BlogImage; id = $blogReference }
    $report.operator = @{ image = $OperatorImage; id = $operatorReference }
    $report.schemaSqlSha256 = @{ identity = (Get-FileHash -LiteralPath $identitySqlPath -Algorithm SHA256).Hash; blog = (Get-FileHash -LiteralPath $blogSqlPath -Algorithm SHA256).Hash }
    $report.environment = 'Disposable Docker network; real Identity/Blog images; PostgreSQL 17/18.6; Mailpit; Development loopback HTTP JWKS; no production deployment.'
    $null = Invoke-Docker -Arguments @('network', 'create', '--label', "blog.identity-smoke=$runId", $network)
    $databasePassword = New-Secret
    $runtimePassword = New-Secret
    $operatorPassword = New-Secret
    $migrationPassword = New-Secret
    $databaseEnvironment = Write-EnvironmentFile 'postgres' @{ POSTGRES_USER = 'fixture_admin'; POSTGRES_PASSWORD = $databasePassword; POSTGRES_DB = 'app'; POSTGRES_INITDB_ARGS = '--encoding=UTF8 --locale=C'; PGDATA = '/var/lib/postgresql/data'; TZ = 'UTC'; PGTZ = 'UTC' }
    $identityDatabase = "$prefix-identity-db"
    $blogDatabase = "$prefix-blog-db"
    Start-FixtureContainer $identityDatabase @('--network', $network, '--network-alias', 'identity-db', '--env-file', $databaseEnvironment, '--tmpfs', '/var/lib/postgresql/data:rw,nosuid,size=512m', 'postgres:17-alpine')
    Start-FixtureContainer $blogDatabase @('--network', $network, '--network-alias', 'blog-db', '--env-file', $databaseEnvironment, '--tmpfs', '/var/lib/postgresql/data:rw,nosuid,size=512m', 'postgres:18.6')
    foreach ($database in @($identityDatabase, $blogDatabase)) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $null = & docker exec $database pg_isready -U fixture_admin -d app 2>&1
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }
    }
    $null = Invoke-Docker -Arguments @('exec', '-i', $identityDatabase, 'psql', '-Xq', '-v', 'ON_ERROR_STOP=1', '-U', 'fixture_admin', '-d', 'app') -InputText ([IO.File]::ReadAllText($identitySqlPath))
    $null = Invoke-Docker -Arguments @('exec', '-i', $blogDatabase, 'psql', '-Xq', '-v', 'ON_ERROR_STOP=1', '-U', 'fixture_admin', '-d', 'app') -InputText ([IO.File]::ReadAllText($blogSqlPath))
    $rolesSql = "CREATE ROLE blog_owner NOLOGIN; CREATE ROLE blog_migration LOGIN PASSWORD '$migrationPassword'; CREATE ROLE blog_runtime LOGIN PASSWORD '$runtimePassword'; CREATE ROLE blog_operator LOGIN PASSWORD '$operatorPassword';"
    $null = Invoke-Docker -Arguments @('exec', '-i', $blogDatabase, 'psql', '-Xq', '-v', 'ON_ERROR_STOP=1', '-U', 'fixture_admin', '-d', 'app') -InputText $rolesSql
    $null = Invoke-Docker -Arguments @('exec', '-i', $blogDatabase, 'psql', '-Xq', '-v', 'ON_ERROR_STOP=1', '-v', 'owner_role=blog_owner', '-v', 'migration_role=blog_migration', '-v', 'runtime_role=blog_runtime', '-v', 'operator_role=blog_operator', '-U', 'fixture_admin', '-d', 'app') -InputText ([IO.File]::ReadAllText((Join-Path $repoRoot 'ops/database/permissions.sql')))
    Add-Check 'Real migrations and separate Blog runtime/operator database grants'
    $mailContainer = "$prefix-mail"
    Start-FixtureContainer $mailContainer @('--network', $network, '--network-alias', 'mail', '-p', '127.0.0.1::8025', 'axllent/mailpit:v1')
    $mailBase = Get-HttpBase $mailContainer 8025
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try { [IO.File]::WriteAllText((Join-Path $secretDirectory 'jwt.pem'), $rsa.ExportRSAPrivateKeyPem()) }
    finally { $rsa.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $secretDirectory 'outbox.key'), [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $issuer = "https://$runId.identity.invalid"
    $identityEnvironment = Write-EnvironmentFile 'identity' @{
        ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_HTTP_PORTS = '8080'; AllowedHosts = 'localhost;127.0.0.1'
        Database__ConnectionString = "Host=identity-db;Database=app;Username=fixture_admin;Password=$databasePassword;Include Error Detail=false;Log Parameters=false"
        Jwt__Enabled = 'true'; Jwt__Issuer = $issuer; Jwt__ActiveKeyId = 'integration'; Jwt__PrivateKeyPath = '/run/fixture/jwt.pem'
        Identity__Clients__0__ClientId = 'blog'; Identity__Clients__0__Audience = 'blog-api'; Identity__Clients__0__Enabled = 'true'
        Identity__Clients__1__ClientId = 'other'; Identity__Clients__1__Audience = 'other-api'; Identity__Clients__1__Enabled = 'true'
        Registration__Enabled = 'true'; Outbox__Enabled = 'true'; Outbox__PollSeconds = '0.1'
        OutboxEncryption__ActiveKeyId = 'integration'; OutboxEncryption__Keys__integration = '/run/fixture/outbox.key'
        Email__FromAddress = 'no-reply@identity.example'; Email__VerificationUrlTemplate = 'http://localhost/verify#token={token}'
        Email__Smtp__Host = 'mail'; Email__Smtp__Port = '1025'; Email__Smtp__Tls = 'None'
        SecurityMaintenance__Enabled = 'false'; SessionCleanup__Enabled = 'false'
        AuthRateLimits__IpPermitLimit = '1000'; AuthRateLimits__AccountPermitLimit = '100'
    }
    $identityContainer = "$prefix-identity"
    Start-FixtureContainer $identityContainer @('--network', $network, '--env-file', $identityEnvironment, '--mount', "type=bind,source=$secretDirectory,target=/run/fixture,readonly", '-p', '127.0.0.1::8080', '-p', '127.0.0.1::8081', '-p', '127.0.0.1::8082', $identityReference)
    $identityBase = Get-HttpBase $identityContainer 8080
    $blogBase = Get-HttpBase $identityContainer 8081
    $wrongIssuerBase = Get-HttpBase $identityContainer 8082
    Wait-Ready $identityBase
    $blogSettings = @{
        ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_HTTP_PORTS = '8081'; AllowedHosts = 'localhost;127.0.0.1'
        ConnectionStrings__Blog = "Host=blog-db;Database=app;Username=blog_runtime;Password=$runtimePassword;Timezone=UTC"
        Identity__Issuer = $issuer; Identity__Audience = 'blog-api'; Identity__JwksUrl = 'http://127.0.0.1:8080/.well-known/jwks.json'; Identity__AllowLocalHttp = 'true'
        AnonymousVisitors__ActiveKeyId = 'integration'; AnonymousVisitors__Keys__integration = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    }
    $blogEnvironment = Write-EnvironmentFile 'blog' $blogSettings
    Start-FixtureContainer "$prefix-blog" @('--network', "container:$identityContainer", '--env-file', $blogEnvironment, $blogReference)
    Wait-Ready $blogBase
    $jwks = Request -Base $identityBase -Path '/.well-known/jwks.json' -Check 'Real Identity public JWKS reachable'
    if (@($jwks.keys).Count -ne 1 -or $jwks.keys[0].d) { throw 'Invalid public JWKS contract.' }
    $null = Request -Base $blogBase -Path '/api/v1/me' -Expected 401 -Check 'Anonymous Blog /me rejected'
    $account = Register-VerifiedUser 'author@identity.example'
    Add-Check 'Real registration and email verification through SMTP/Mailpit'
    $pair = Request -Base $identityBase -Path '/api/v1/auth/login' -Method POST -Body $account -Check 'Real Identity login'
    Remember-Pair $pair
    $profile = Request -Base $identityBase -Path '/api/v1/users/me' -Token $pair.accessToken -Check 'Real Identity authenticated profile'
    $me = Request -Base $blogBase -Path '/api/v1/me' -Token $pair.accessToken -Check 'Blog accepts real Identity token'
    if (@($me.roles).Count -ne 1 -or $me.roles[0] -ne 'Reader') { throw 'Unexpected initial Blog roles.' }
    $again = Request -Base $blogBase -Path '/api/v1/me' -Token $pair.accessToken
    if ($me.id -ne $again.id) { throw 'Blog bootstrap is not idempotent.' }
    $mappingCount = Invoke-Docker -Arguments @('exec', '-i', $blogDatabase, 'psql', '-XAt', '-v', 'ON_ERROR_STOP=1', '-U', 'fixture_admin', '-d', 'app') -InputText "SELECT count(*) FROM accounts.local_users WHERE issuer = '$issuer' AND subject = '$($profile.id)'::uuid AND id = '$($me.id)'::uuid;"
    if ($mappingCount.Trim() -ne '1') { throw 'Verified issuer/sub mapping mismatch.' }
    Add-Check 'Stable Blog user mapped to verified Identity issuer/sub with Reader role'
    $content = @{ title = 'Live Identity smoke'; slug = "identity-$runId"; markdown = 'Disposable interoperability check'; categoryIds = @(); tagIds = @() }
    $null = Request -Base $blogBase -Path '/api/v1/publications' -Method POST -Body $content -Token $pair.accessToken -Expected 403 -Check 'Reader cannot create publications'
    $operatorEnvironment = Write-EnvironmentFile 'operator' @{ Identity__Issuer = $issuer; ConnectionStrings__Blog = "Host=blog-db;Database=app;Username=blog_operator;Password=$operatorPassword;Timezone=UTC" }
    $null = Invoke-Docker -Arguments @('run', '--rm', '--network', $network, '--env-file', $operatorEnvironment, $operatorReference, 'roles', 'grant', '--user-id', $me.id, '--role', 'Author', '--reason', 'Disposable Identity interoperability check')
    $author = Request -Base $blogBase -Path '/api/v1/me' -Token $pair.accessToken
    if ($author.roles -notcontains 'Author') { throw 'Local role change was not visible with the existing Identity JWT.' }
    Add-Check 'Blog Operator grants Author locally; same Identity JWT observes new role'
    $draft = Request -Base $blogBase -Path '/api/v1/publications' -Method POST -Body $content -Token $pair.accessToken -Expected 201 -Check 'Author creates publication with real Identity JWT'
    $otherAccount = Register-VerifiedUser 'reader@identity.example'
    $otherPair = Request -Base $identityBase -Path '/api/v1/auth/login' -Method POST -Body $otherAccount
    Remember-Pair $otherPair
    $otherMe = Request -Base $blogBase -Path '/api/v1/me' -Token $otherPair.accessToken
    if ($otherMe.id -eq $me.id -or @($otherMe.roles).Count -ne 1 -or $otherMe.roles[0] -ne 'Reader') { throw 'Cross-user Blog role isolation failed.' }
    $null = Request -Base $blogBase -Path "/api/v1/publications/$($draft.id)" -Token $otherPair.accessToken -Expected 404 -Check 'Second Identity user has isolated Reader role and cannot read foreign draft'
    $null = Request -Base $blogBase -Path "/api/v1/publications/$($draft.id)/publish" -Method POST -Token $pair.accessToken -ExtraHeaders @{ 'X-Resource-Version' = $draft.version } -Check 'Author publishes with real Identity JWT'
    $null = Request -Base $blogBase -Path "/api/v1/publications/$($draft.id)" -Check 'Published material is readable anonymously'
    $otherClientLogin = @{} + $account
    $otherClientLogin.clientId = 'other'
    $wrongAudience = Request -Base $identityBase -Path '/api/v1/auth/login' -Method POST -Body $otherClientLogin
    Remember-Pair $wrongAudience
    $null = Request -Base $blogBase -Path '/api/v1/me' -Token $wrongAudience.accessToken -Expected 401 -Check 'Real Identity token for another audience rejected'
    $segments = $pair.accessToken.Split('.')
    $segments[2] = $(if ($segments[2][0] -eq 'A') { 'B' } else { 'A' }) + $segments[2].Substring(1)
    $null = Request -Base $blogBase -Path '/api/v1/me' -Token ($segments -join '.') -Expected 401 -Check 'Tampered real JWT signature rejected'
    $blogSettings.Identity__Issuer = 'https://wrong-issuer.invalid'; $blogSettings.ASPNETCORE_HTTP_PORTS = '8082'
    $wrongIssuerEnvironment = Write-EnvironmentFile 'wrong-issuer' $blogSettings
    Start-FixtureContainer "$prefix-wrong-issuer" @('--network', "container:$identityContainer", '--env-file', $wrongIssuerEnvironment, $blogReference)
    Wait-Ready $wrongIssuerBase
    $null = Request -Base $wrongIssuerBase -Path '/api/v1/me' -Token $pair.accessToken -Expected 401 -Check 'Real signed JWT rejected when trusted issuer differs'
    $replacement = Request -Base $identityBase -Path '/api/v1/auth/refresh' -Method POST -Body @{ refreshToken = $pair.refreshToken } -Check 'Real Identity refresh rotation'
    Remember-Pair $replacement
    $refreshedMe = Request -Base $blogBase -Path '/api/v1/me' -Token $replacement.accessToken
    if ($refreshedMe.id -ne $me.id -or $refreshedMe.roles -notcontains 'Author') { throw 'Refreshed Identity JWT lost Blog identity or local roles.' }
    Add-Check 'Refreshed real JWT preserves Blog user and local roles'
    $null = Request -Base $identityBase -Path '/api/v1/auth/refresh' -Method POST -Body @{ refreshToken = $pair.refreshToken } -Expected 401 -Check 'Consumed refresh replay rejected'
    $null = Request -Base $identityBase -Path '/api/v1/auth/refresh' -Method POST -Body @{ refreshToken = $replacement.refreshToken } -Expected 401 -Check 'Refresh replay revokes replacement family'
    $logoutPair = Request -Base $identityBase -Path '/api/v1/auth/login' -Method POST -Body $account
    Remember-Pair $logoutPair
    $null = Request -Base $identityBase -Path '/api/v1/auth/logout' -Method POST -Body @{ refreshToken = $logoutPair.refreshToken } -Expected 204 -Check 'Real Identity logout'
    $null = Request -Base $identityBase -Path '/api/v1/users/me' -Token $logoutPair.accessToken -Expected 401 -Check 'Identity profile rejects logged-out session'
    $null = Request -Base $blogBase -Path '/api/v1/me' -Token $logoutPair.accessToken -Check 'Documented local JWT validation remains valid until expiry after Identity logout'
    foreach ($container in $containers) {
        $logs = Invoke-Docker -Arguments @('logs', $container)
        foreach ($secret in $sensitiveValues) { if ($logs.Contains($secret)) { throw 'A fixture log contains a credential or token; output suppressed.' } }
    }
    Add-Check 'Container logs contain none of the generated account passwords or received tokens'
    $report.status = 'passed'
}
catch {
    $failed = $true
    $report.failure = $_.Exception.Message
    Write-Output "FAIL $($report.failure)" | Out-Host
}
finally {
    for ($index = $containers.Count - 1; $index -ge 0; $index--) {
        try {
            $name = $containers[$index]
            $owner = Invoke-Docker -Arguments @('inspect', $name, '--format', '{{index .Config.Labels "blog.identity-smoke"}}')
            if ($owner.Trim() -ne $runId) { throw 'Unexpected fixture owner.' }
            $null = Invoke-Docker -Arguments @('rm', '--force', '--volumes', $name)
        } catch { $cleanupPassed = $false }
    }
    try { $null = Invoke-Docker -Arguments @('network', 'rm', $network) } catch { $cleanupPassed = $false }
    try {
        $resolvedSecrets = [IO.Path]::GetFullPath($secretDirectory)
        if (-not $resolvedSecrets.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolvedSecrets -Leaf) -ne $prefix) { throw 'Unexpected temporary path.' }
        if (Test-Path -LiteralPath $resolvedSecrets) { Remove-Item -LiteralPath $resolvedSecrets -Recurse -Force }
    } catch { $cleanupPassed = $false }
    $report.cleanupPassed = $cleanupPassed
    $report.completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    if (-not $cleanupPassed) { $report.status = 'failed'; $failed = $true }
    if ($destination) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $destination 'result.json') -Encoding utf8 }
    Pop-Location
}
if ($failed) { exit 1 }
