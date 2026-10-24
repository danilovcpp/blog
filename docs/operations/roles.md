# Local roles and Operator

The API accepts one installation's verified Identity access JWT. A first successful
`GET /api/v1/me` provisions only a local UUID and implicit Reader. Its response has
only `id` and ordered `roles`: Reader, Author, Moderator, Admin. Email, profiles and
session/token secrets are not stored. HTTP has no user or role administration routes,
including for Admin.

Operator runs from a protected operator environment with its own PostgreSQL login.
Configure `Identity:Issuer` and `ConnectionStrings:Blog` through the environment's
secret provider; local Development may use this project's separate user-secrets.
No audience, JWKS URL, Identity credential or visitor key is needed. Configuration
cannot be overridden with command arguments. Optional `Operator:TimeoutSeconds`
defaults to 30 and accepts 1–300 seconds. Database commands and the final commit
exchange are independently bounded to five seconds.

Apply AddRoleChangeAudit and then [permissions.sql](../../ops/database/permissions.sql)
with provisioner credentials before using Operator. See [database.md](database.md)
for owner, migration, runtime and operator separation. Never use the API credential
for Operator or supply connection strings on its command line.

First have the intended user call `/api/v1/me` with their own valid Bearer. In a
protected shell, locate the existing mapping and use its **local** UUID:

```powershell
$issuer = Read-Host 'Exact configured issuer'
$subject = Read-Host 'Verified external sub UUID'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- users find --issuer $issuer --subject $subject
$localId = Read-Host 'Returned local userId'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles list --user-id $localId
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles grant --user-id $localId --role Admin --reason 'Initial administrator approved by installation owner'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles grant --user-id $localId --role Author --reason 'Editorial responsibilities'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles grant --user-id $localId --role Moderator --reason 'Discussion moderation responsibilities'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles revoke --user-id $localId --role Author --reason 'Editorial responsibilities ended'
```

Author and Moderator are independent and may coexist. Admin provides all rights
through policy evaluation without implicitly storing Author/Moderator assignments.
The first Admin uses the same grant command; there is no automatic or last-Admin rule.
Reader cannot be granted or revoked. Unknown users are not created by Operator.
`--issuer` must match exactly, including case and trailing slash; UUIDs use the
standard hyphenated form. Unknown/duplicate arguments, `--actor` and numeric roles
are rejected before SQL. Reasons are required, trimmed using the specified Unicode
White_Space set and limited to 1–512 Unicode scalar values. The actor comes from the
OS process identity; a command argument cannot impersonate another operator.

Every grant/revoke, including an idempotent repeat, takes the same transaction-scoped
advisory lock for the target local UUID. The role mutation and immutable audit record
commit together. A repeated grant/revoke succeeds with `changed=false` and a new audit
entry. Audit failure or cancellation before commit rolls the whole transaction back.
Timestamps come from the database UTC clock. Operator cannot modify content or
update/delete/truncate audit; runtime cannot access audit or write elevated roles.

Success writes exactly one JSON object to stdout and leaves stderr empty:

| Command | Fields |
| --- | --- |
| users find | userId, issuer, subject, roles |
| roles list | userId, roles |
| roles grant/revoke | userId, roles, changed, auditId, correlationId |

Errors leave stdout empty and write one safe `{code,message,correlationId}` object
to stderr. Exit codes: 0 success/no-op, 2 invalid arguments, 3 user not found,
4 database/permissions/audit failure or unknown commit outcome, 5 invalid
configuration, 130 confirmed cancellation before commit (Ctrl+C or timeout).
Neither output stream includes SQL, connection strings or stack traces.

Once commit is sent, cancellation cannot prove rollback. A confirmed commit returns
success even if cancellation arrived meanwhile. Losing its acknowledgement returns
exit 4 / `outcome_unknown`; **do not assume rollback**. Using the operator credential,
inspect `accounts.role_change_audit` by the returned `command_correlation_id`, using a
parameterized query through the protected database client. For example, prepare a
statement with a UUID parameter in that client:

```sql
PREPARE inspect_role_command(uuid) AS
SELECT id, command_correlation_id, target_user_id, role, action, changed, occurred_at
FROM accounts.role_change_audit WHERE command_correlation_id = $1;
```

An audit row proves the atomic command committed. Reconnect and establish database
availability and completion of the original backend before treating an absent row
as a rolled-back result. Retrying grant/revoke is idempotent for the assignment;
the retry is a new execution with its own correlation ID and no-op audit when needed.
Avoid exposing audit actor/reason outside the protected operator environment.

Roles are reread at the beginning of every operation. A request begun after the
operator's commit applies the change with the same still-valid JWT; an in-flight
request keeps its initial authorization snapshot. Identity session revocation or
deactivation may remain unobserved until `exp + 30 s`; instant external revocation
or a callback to Identity is not provided.
