# Database principals and migrations

Use four separately provisioned PostgreSQL principals per installation:

| Principal | Purpose |
| --- | --- |
| owner | NOLOGIN owner of schemas/tables; no application credential |
| migration | Protected deployment login, member of owner; run migrations with `SET ROLE owner` |
| runtime | API login: SELECT/INSERT LocalUser, SELECT assignments; no role writes or DDL |
| operator | Separate console login: SELECT LocalUser/assignments in Foundational |

Provision logins through the installation's protected secret mechanism. They must be
NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOREPLICATION, NOBYPASSRLS and must not inherit
other privileged memberships. Runtime and operator cannot be members of owner/migration
or one another. Credentials never belong in Git, shell arguments or reports.

`ops/database/permissions.sql` uses psql identifier variables `owner_role`,
`migration_role`, `runtime_role`, `operator_role`. Run it as the database provisioner
after applying InitialAccounts. It changes ownership and grants only on existing
Accounts objects, revokes PUBLIC schema/database creation and temporary-table privileges,
and gives no elevated application roles. Use a dedicated database; pre-existing grants
or other memberships must be removed during provisioning. Run the script after each
migration whose tables require new grants. Operator writes/audit are added with US3.

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
upgrade test at this phase. Down drops Accounts data: use only for disposable tests;
production recovery requires a tested backup or a reviewed corrective migration.
Back up and restore with protected credentials outside request-serving processes.
