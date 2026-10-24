\set ON_ERROR_STOP on
-- Run with psql as the database provisioner after AddDiscussions. All four principals must already exist.
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
ALTER TABLE accounts.role_change_audit OWNER TO :"owner_role";
REVOKE ALL ON SCHEMA accounts FROM PUBLIC, :"runtime_role", :"operator_role";
REVOKE ALL ON ALL TABLES IN SCHEMA accounts FROM PUBLIC, :"runtime_role", :"operator_role";
GRANT USAGE ON SCHEMA accounts TO :"runtime_role", :"operator_role";
GRANT SELECT, INSERT ON accounts.local_users TO :"runtime_role";
GRANT SELECT ON accounts.role_assignments TO :"runtime_role";
GRANT SELECT ON accounts.local_users, accounts.role_assignments TO :"operator_role";
GRANT INSERT, DELETE ON accounts.role_assignments TO :"operator_role";
GRANT SELECT, INSERT ON accounts.role_change_audit TO :"operator_role";
ALTER SCHEMA publishing OWNER TO :"owner_role";
ALTER SCHEMA engagement OWNER TO :"owner_role";
ALTER SCHEMA discussions OWNER TO :"owner_role";
SELECT format('ALTER TABLE %I.%I OWNER TO %I', schemaname, tablename, :'owner_role')
FROM pg_tables WHERE schemaname IN ('publishing', 'engagement', 'discussions') \gexec
REVOKE ALL ON SCHEMA publishing, engagement, discussions FROM PUBLIC, :"runtime_role", :"operator_role";
REVOKE ALL ON ALL TABLES IN SCHEMA publishing, engagement, discussions FROM PUBLIC, :"runtime_role", :"operator_role";
GRANT USAGE ON SCHEMA publishing, engagement, discussions TO :"runtime_role";
GRANT SELECT, INSERT, UPDATE ON publishing.publications TO :"runtime_role";
GRANT SELECT, INSERT, UPDATE, DELETE ON publishing.categories, publishing.tags,
    publishing.publication_categories, publishing.publication_tags TO :"runtime_role";
-- Likes have only desired-state insert/delete operations; runtime cannot rewrite identity or creation time.
GRANT SELECT, INSERT, DELETE ON engagement.likes TO :"runtime_role";
-- Comments are terminal tombstones; runtime never physically deletes a node.
GRANT SELECT, INSERT, UPDATE ON discussions.comments TO :"runtime_role";
-- New objects receive no implicit runtime/operator grants. Extend this file with the owning migration.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner_role" IN SCHEMA accounts REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner_role" IN SCHEMA publishing, engagement, discussions REVOKE ALL ON TABLES FROM PUBLIC;
COMMIT;
