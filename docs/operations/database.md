# Database principals and migrations

Final validation: run `pwsh -File scripts/validation/Invoke-MigrationValidation.ps1`
from the repository root. It creates isolated PostgreSQL 18.6 fixtures, upgrades
every real baseline with representative rows, replays idempotent SQL, checks
constraints/privileges/model drift, and writes reviewed SQL/TRX under
`artifacts/validation/migrations/`. It never targets a configured production DB.
Full dataset SQL/EXPLAIN and taxonomy-copy checks use
`scripts/validation/Invoke-QueryInspection.ps1`. Rollout, backups, locks and recovery
are described in [deployment.md](deployment.md).

Use four separately provisioned PostgreSQL principals per installation:

| Principal | Purpose |
| --- | --- |
| owner | NOLOGIN owner of schemas/tables; no application credential |
| migration | Protected deployment login, member of owner; run migrations with `SET ROLE owner` |
| runtime | API login: SELECT/INSERT LocalUser, SELECT assignments, Publishing DML, comments SELECT/INSERT/UPDATE and likes SELECT/INSERT/DELETE; no role writes or DDL |
| operator | Separate console login: SELECT LocalUser/assignments/audit, INSERT/DELETE assignments, INSERT audit |

Provision logins through the installation's protected secret mechanism. They must be
NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOREPLICATION, NOBYPASSRLS and must not inherit
other privileged memberships. Runtime and operator cannot be members of owner/migration
or one another. Credentials never belong in Git, shell arguments or reports.

`ops/database/permissions.sql` uses psql identifier variables `owner_role`,
`migration_role`, `runtime_role`, `operator_role`. Run it as the database provisioner
after applying the complete reviewed chain (currently AddViewDeduplication). It changes ownership and grants only on existing
Accounts/Publishing/Engagement/Discussions objects, revokes PUBLIC schema/database creation and temporary-table privileges,
and gives no elevated application roles. Use a dedicated database; pre-existing grants
or other memberships must be removed during provisioning. Run the script after each
migration whose tables require new grants. Operator has no content, DDL or audit
UPDATE/DELETE/TRUNCATE privileges; runtime has no audit privileges or role writes.

Apply EF migrations as a separate deployment step using migration credentials and
the owner role (for Npgsql, protected connection option `Options=-c role=<owner>`).
`BlogDbContextFactory` reads only `ConnectionStrings__Blog`; it neither starts the API
nor requests JWKS. Use `dotnet ef migrations script --idempotent` and review SQL before
`dotnet ef database update`, with Infrastructure as project and Api as startup project.
The API and Operator never migrate on startup. Keep `__EFMigrationsHistory` owned by
the migration principal/owner; runtime and operator require no access to it.

InitialAccounts creates only a new schema, two tables, their indexes and integrity
constraints. It takes DDL locks on new objects; it does not rewrite existing business
tables. Validate against an empty PostgreSQL 18.6 database, inspect generated SQL and
check `has-pending-model-changes`. There is no preceding production migration for an
upgrade test for InitialAccounts. Down drops Accounts data: use only for disposable tests;
production recovery requires a tested backup or a reviewed corrective migration.
Back up and restore with protected credentials outside request-serving processes.

AddPublishing creates publications, categories, tags, two classification tables and
the early read dependency engagement.likes. The migration adds constraints and indexes
on new tables, with brief DDL/FK locks on referenced Accounts objects. It does not
rewrite Accounts data. Empty-database application and upgrade from InitialAccounts,
preserving an existing LocalUser, are covered by real PostgreSQL tests. Deploy the
migration and then apply permissions.sql before starting the new API. The previous
API remains compatible with the additive schema during rollout.

Runtime can SELECT/INSERT/UPDATE publications (terminal deletion uses a tombstone),
and SELECT/INSERT/UPDATE/DELETE taxonomy and classification. Likes use only
SELECT/INSERT/DELETE; UPDATE is deliberately absent. Operator has no access to Publishing/Engagement. New schemas/tables
receive no implicit grants. Local role administration is described in [roles.md](roles.md).

Review the generated AddPublishing SQL and take a protected backup before deployment.
Down removes all new content/likes tables and is destructive once writes occur:
use Down/reapply only on disposable databases. Recover production by keeping the
additive schema with the previous API or by a reviewed corrective migration; verify
backup restoration separately. SQL, constraint/index evidence and verification results
for this phase are written to ignored artifacts/validation/us1-migrations.md.

AddRoleChangeAudit is additive: a new table with NOT NULL, role/action/actor/normalized
reason CHECKs, RESTRICT FK to LocalUser, UNIQUE correlation ID and a target/time/ID
index. It briefly locks referenced Accounts objects for FK validation and creates
indexes only on the new table; existing users, roles and content are preserved.
Apply it with migration credentials, then rerun permissions.sql before deploying
Operator. The previous API remains compatible; rollback of the executable does not
require schema rollback. Down drops all role audit history and is destructive once
commands have run. Production recovery keeps the additive schema or uses a reviewed
corrective migration and verified protected backup. Use Down/reapply only on
disposable test databases. Runtime and Operator never apply migrations themselves.

AddDiscussions adds the comments table and its integrity constraints/indexes. The
composite parent FK prevents cross-publication links; parent depth and immutable
links are checked under the existing publication row lock. New-table DDL and FK
validation briefly lock the referenced Accounts/Publishing objects; no existing
content is rewritten. Empty-schema application and upgrade from AddRoleChangeAudit
preserving an existing user are covered by PostgreSQL tests. Apply the migration,
then permissions.sql, before deploying the API. Runtime can SELECT/INSERT/UPDATE
comments, without physical DELETE; Operator has no Discussions privileges.

The previous API remains compatible with the additive schema. Down destroys all
discussion content and is only for disposable databases. Production rollback keeps
the additive schema with the previous executable, or uses a reviewed corrective
migration and a verified protected backup. Review the generated SQL in
`artifacts/validation/us4-migration.sql` before rollout. Constraint, index, query-plan
and acceptance evidence is stored in `artifacts/validation/us4.json` and
`artifacts/validation/queries/us4-tree.json`.

US6 adds no schema objects: the existing likes primary key, foreign keys, bigint
counter CHECK and popularity index already support desired-state writes. Rerun
permissions.sql with deployment credentials before deploying the API to grant
runtime INSERT/DELETE on engagement.likes. The script's REVOKE/GRANT transaction
briefly takes privilege/catalog locks; it does not rewrite content. Deploy the
executable after this step. Older API versions remain compatible with the schema
and grants. Executable rollback preserves existing likes/counters; no destructive
migration or data cleanup is required. Operator still has no Engagement access.

US7 adds AddViewDeduplication: a new Engagement table with composite visitor key,
RESTRICT publication FK, NOT NULL/kind/key-length constraints and an expiry index.
It briefly locks the referenced Publishing table for the new FK and does not rewrite
existing content or counters. Apply the migration with DDL credentials, then rerun
permissions.sql before deploying the new API. Runtime needs SELECT/INSERT/UPDATE/DELETE
on this table; Operator has no access. Every API replica also needs the same protected
visitor key ring. Startup never applies DDL. Down removes active dedup windows while
preserving counters; keep the additive schema when rolling back the executable to
preserve those windows. See [anonymous-visitors.md](anonymous-visitors.md) for staged
key rotation, cleanup timing, limits and the consequences of destructive rollback.
