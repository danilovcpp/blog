# Database principals and migrations

Use four separately provisioned PostgreSQL principals per installation:

| Principal | Purpose |
| --- | --- |
| owner | NOLOGIN owner of schemas/tables; no application credential |
| migration | Protected deployment login, member of owner; run migrations with `SET ROLE owner` |
| runtime | API login: SELECT/INSERT LocalUser, SELECT assignments, Publishing DML and likes SELECT; no role writes or DDL |
| operator | Separate console login: SELECT LocalUser/assignments/audit, INSERT/DELETE assignments, INSERT audit |

Provision logins through the installation's protected secret mechanism. They must be
NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOREPLICATION, NOBYPASSRLS and must not inherit
other privileged memberships. Runtime and operator cannot be members of owner/migration
or one another. Credentials never belong in Git, shell arguments or reports.

`ops/database/permissions.sql` uses psql identifier variables `owner_role`,
`migration_role`, `runtime_role`, `operator_role`. Run it as the database provisioner
after applying AddRoleChangeAudit. It changes ownership and grants only on existing
Accounts/Publishing/Engagement objects, revokes PUBLIC schema/database creation and temporary-table privileges,
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
and SELECT/INSERT/UPDATE/DELETE taxonomy and classification. It only SELECTs likes
in this phase. Operator has no access to Publishing/Engagement. New schemas/tables
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
