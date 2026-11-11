# Health probes

`GET /health/live` returns `200` and `{"status":"live"}` for the live process.
It performs no database or Identity requests.

`GET /health/ready` returns `200 {"status":"ready"}` when PostgreSQL responds,
the shared JWKS loader has a fresh usable snapshot, and Media is ready. Database
checks and JWKS loading have five-second bounds. Errors are safe ProblemDetails `503 dependency_unavailable`
or `503 identity_keys_unavailable`, without addresses or configuration values.

Readiness uses the same key cache, single-flight fetch and 30-second attempt cooldown
as Bearer validation. A fresh snapshot remains usable during an Identity outage.
After expiry it is never extended; probes can recover with a bounded fetch when the
cooldown permits. Configure probe timeouts to allow both dependency checks and a
failure threshold that tolerates the cooldown. Do not depend on a live Identity for
liveness or issue a JWKS fetch for every probe.

Expose readiness only through internal ingress/network policy, restricting access to
the orchestrator/operations network. The application does not infer trusted callers
from spoofable forwarding headers. Production ingress must enforce this restriction.
Development-only OpenAPI 3.1 and Swagger UI are available at `/openapi/v1.json` and
`/swagger`; they are absent outside Development.

Media readiness has a three-second budget for schema marker `version=1`, read
grants on all eight Media tables, spool ownership/reclaim/free writable capacity,
native codec and verified GET of both fixed sentinels. It neither creates objects
nor repairs schema/grants. Maintenance forces readiness false; liveness remains
process-only. Backup also requires stopping GET hosts after draining, as described
in [media-recovery.md](media-recovery.md).

The `BlogEngine.Media` meter reports active transfers/decodes, spool used/free and
captured sizes, request/processing duration, quota used/reserved, storage failures
by stable code (including integrity/missing), retry counts, deleted objects per
queue, backlog counts/age and reconciliation lag. No user/file names, bodies,
tokens, credentials, object keys or internal URLs are metric labels or Media log
properties. Logs carry stable codes, attempt and opaque IDs; request errors carry
traceId. Media processing starts a trace activity for background work.

The verified baseline is one worker, five-minute interval and **100 independent
items per queue** (due jobs, expired uploads, reconciliation), with 10,000 initial
Retired/Deleted records, 1,000 initial expired uploads and 1,000 additional arrivals
per queue per 24 hours. Expiries without objects still receive their own budget.
Completed jobs retain permanent object tombstones. Reconciling them removes late
PUT bytes even after a successful deletion; Ready library/current avatars are
never eligible just because of age or lack of usages.

Alert when oldest incomplete cleanup exceeds **12h**, reconciliation lag exceeds
**12h**, expired backlog exceeds **1,000**, daily expiries/retirements exceed
**1,000**, or retained objects exceed the baseline plus daily arrivals. These
thresholds warn before the 24h deletion deadline. Retained tombstones grow over
time: measure the actual profile and increase resources/revalidate before exceeding
it. Outages use bounded jittered retry (at most 15min) and do not discard work.
The accelerated profile test checks full scan <=12h and drain <=24h with continuing
arrivals; real-time throughput, operation latency and node-loss measurements for
the actual deployment must also be retained before activation. A changed interval,
batch size, replica topology or load requires revalidation. Never infer a deployment
capacity guarantee solely from the arithmetic of interval multiplied by batch.

Use `Invoke-MediaValidation.ps1 -Scenario Cleanup -CleanupIntervalSeconds 300
-CleanupBatchSize 100` with the proposed interval and batch size. The rehearsal uses
pinned PostgreSQL and Garage. Its clock advances one interval per cycle, and its
report includes actual wall-clock cycle durations, queue progress, SQL plans and
control hashes. Run it for the baseline and for the deployment settings.
