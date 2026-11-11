# Media storage setup

Phase 1 supplies storage/provisioning and the integration harness. Phase 2
registers Media options, spool, S3/codec and transaction/transport primitives.
Upload routes are scheduled for Phase 3. Provisioning remains an operator step,
never an API startup action. Apply `AddMedia` and rerun database permissions
using the separate provisioner; the API and Operator receive no DDL rights.

API startup now requires the runtime S3 credential pair, trusted endpoint and
an absolute `Media__Spool__Directory` outside webroot. Each API host needs its
own private persistent spool volume. Defaults reserve a 1 GiB disk budget and
share four transfer slots. The process exclusively owns and reclaims its spool
before accepting transfers; ownership/cleanup failure leaves readiness false.
Local/private HTTP needs `Media__Storage__AllowLocalHttp=true` in Development;
Production requires HTTPS. See [the Phase 2 report](../../specs/002-files-avatars/phase2-report.md)
for verification and remaining feature work.

## Development

Load `BLOG_GARAGE_RPC_SECRET` (32 random bytes as 64 lowercase hex characters)
and `BLOG_GARAGE_ADMIN_TOKEN` (a random administrative token) from the protected
environment/secret provider. The Compose file has no default credentials.
It also requires the existing protected PostgreSQL variables. Start from the
repository root:

```powershell
docker compose --profile media up -d garage
docker compose --profile media exec garage /garage status
docker compose --profile media exec garage /garage layout assign -z development -c 1G <NODE_ID_FROM_STATUS>
docker compose --profile media exec garage /garage layout apply --version 1
```

Assign/apply the layout once for a new metadata volume. Restart uses the same
metadata/data volumes and layout. Do not run `down --volumes` against retained
data. S3 and Admin ports bind only loopback; RPC is not published. Website access
is not configured. The single-node layout provides no redundancy.

## Provisioning and secrets

`Provision-MediaStorage.ps1` uses Garage Admin API v2 over a trusted root HTTPS
endpoint. Development HTTP requires `-AllowLocalHttp` and loopback endpoints.
The provisioner receives callbacks to an already configured protected provider;
credentials are not command-line arguments, returned objects or log messages.
For example, with a registered, unlocked Microsoft.PowerShell.SecretManagement
vault containing `Blog.Media.AdminToken`:

```powershell
$read = {
  param($name)
  if (Get-SecretInfo -Vault Deployment -ErrorAction Stop | Where-Object Name -CEQ $name) {
    Get-Secret -Name $name -Vault Deployment -AsPlainText -ErrorAction Stop
  }
}
$write = { param($name, $value) Set-Secret -Name $name -Vault Deployment -Secret (ConvertTo-SecureString $value -AsPlainText -Force) }
& ./ops/media/Provision-MediaStorage.ps1 `
  -AdminEndpoint https://garage-admin.internal.example `
  -S3Endpoint https://garage-s3.internal.example `
  -ReadSecret $read -WriteSecret $write
```

Use only this vault's missing-secret result for absent runtime credentials;
provider failures must throw. Provisioning writes `Blog.Media.AccessKey` and
`Blog.Media.SecretKey` before granting access and reuses them on subsequent runs.
Protect these as one credential pair; interrupted partial provider writes fail
closed and require operator recovery of the pair. Serialize provisioning jobs.
An interruption before provider persistence can leave an unprivileged orphan
key, which an operator can remove after checking the protected provider.

Use a separate scoped Admin token allowing `CreateKey`, `UpdateKey`, `GetKeyInfo`,
`ListBuckets`, `CreateBucket`, `GetBucketInfo`, `UpdateBucket`, `AllowBucketKey`
and `DenyBucketKey`. Keep it out of the API environment. Runtime receives only
the two saved S3 values through standard .NET protected configuration providers
(`Media__Storage__AccessKey` / `Media__Storage__SecretKey`). It cannot create
buckets or administer their configuration. Garage's bucket write permission
includes object deletion. The provisioner refuses buckets granted to other keys
and a runtime key granted unrelated buckets, disables website access, and removes
owner/create-bucket privileges. It does not modify unrelated keys or buckets.

Each bucket contains `_blog/health-v1`, exactly 29 UTF-8 bytes:
`BlogEngine media sentinel v1` followed by LF. SHA-256:
`e85f6fe5e6589eee2f0cf83bc9356352a1a94ef01ff75dc925b6487fe34bcf75`.
The provisioner signs the fixed PUT with SigV4 (`garage`), SHA-256 and Content-MD5.
Its only output is a non-secret readiness descriptor. Probes must only GET this
object; they must not grant permissions or create storage.

## Production layout

Complete `production-inventory.example.yaml` using three actual hosts in three
independent failure domains. Use the same pinned multi-platform manifest digest
as development/tests. Install `garage.production.toml` per node, replace its
private RPC address, connect the peers using their node IDs and private RPC
addresses, and retain separate metadata/data volumes. Terminate HTTPS on the
private storage ingress. Firewall S3 to Blog/provisioning and RPC/Admin to
storage/operator networks. Supply the shared RPC secret and separate Admin token
through the protected provider; neither belongs in TOML/inventory.

Inspect `garage status`, assign the three real node IDs with their measured
capacity (`garage layout assign -z <FAILURE_DOMAIN> -c <CAPACITY> <NODE_ID>`),
then inspect the staged layout and apply its next version. Each node must have
a distinct zone matching the independent inventory domain. Configuration fixes
`replication_factor=3` and `consistency_mode="consistent"`. Three containers on
one host do not satisfy this gate. Before activation, record a successful
single-node loss/recovery check. Phase 1 does not perform production rollout,
HA acceptance, Media schema migration or backup/restore acceptance.

## Verification

```powershell
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --filter FullyQualifiedName~MediaFixtureTests
```

The fixture uses the same digest and TOML, independent disposable volumes,
loopback random ports, cryptographically random credentials and an in-memory
test provider over a child process's stdin pipe. It executes the real provisioner
twice, exercises private S3 and restricted grants, stops/restarts Garage and
checks persistent bytes. Two API test hosts reuse PostgreSQL, local JWT/JWKS,
controlled clock and the runtime storage configuration. Existing barrier helpers
remain available to the later lifecycle/concurrency scenarios.
Docker may reassign the fixture's ephemeral ports after `RestartAsync`; create
fresh storage clients/API factories from its updated configuration after restart.

References: [Garage quick start](https://garagehq.deuxfleurs.fr/documentation/quick-start/),
[configuration](https://garagehq.deuxfleurs.fr/documentation/reference-manual/configuration/),
[Admin API v2](https://garagehq.deuxfleurs.fr/api/garage-admin-v2.html),
[S3 permissions](https://garagehq.deuxfleurs.fr/documentation/reference-manual/s3-compatibility/).
