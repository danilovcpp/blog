\set ON_ERROR_STOP on
-- Run with psql as the database provisioner after InitialAccounts. All four principals must already exist.
-- Pass identifier variables owner_role, migration_role, runtime_role, operator_role. Never pass passwords here.
BEGIN;
SELECT format('REVOKE CREATE, TEMP ON DATABASE %I FROM PUBLIC', current_database()) \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT CONNECT, CREATE ON DATABASE %I TO %I', current_database(), :'owner_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I, %I, %I', current_database(), :'migration_role', :'runtime_role', :'operator_role') \gexec
GRANT :"owner_role" TO :"migration_role";
ALTER SCHEMA accounts OWNER TO :"owner_role";
ALTER TABLE accounts.local_users OWNER TO :"owner_role";
ALTER TABLE accounts.role_assignments OWNER TO :"owner_role";
REVOKE ALL ON SCHEMA accounts FROM PUBLIC, :"runtime_role", :"operator_role";
REVOKE ALL ON ALL TABLES IN SCHEMA accounts FROM PUBLIC, :"runtime_role", :"operator_role";
GRANT USAGE ON SCHEMA accounts TO :"runtime_role", :"operator_role";
GRANT SELECT, INSERT ON accounts.local_users TO :"runtime_role";
GRANT SELECT ON accounts.role_assignments TO :"runtime_role";
GRANT SELECT ON accounts.local_users, accounts.role_assignments TO :"operator_role";
-- New objects receive no implicit runtime/operator grants. Extend this file with the owning migration.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner_role" IN SCHEMA accounts REVOKE ALL ON TABLES FROM PUBLIC;
COMMIT;
