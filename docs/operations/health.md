# Health probes

`GET /health/live` returns `200` and `{"status":"healthy"}` for the live process.
It performs no database or Identity requests.

`GET /health/ready` returns the same success body only when PostgreSQL responds and
the shared JWKS loader has a fresh usable snapshot. Database checks and JWKS loading
have five-second bounds. Errors are safe ProblemDetails `503 dependency_unavailable`
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
