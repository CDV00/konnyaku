-- Dev/test bootstrap. Runs as the schema owner (POSTGRES_USER = konnyaku).
-- Each module gets its own schema and a login role with DML rights on that schema only,
-- so cross-module SQL fails with a permission error. Roles cannot bypass RLS.
-- Add one block per module as modules are introduced.

CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Module: Organization (schema org)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kk_organization') THEN
        CREATE ROLE kk_organization LOGIN PASSWORD 'kk_organization_dev' NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;
CREATE SCHEMA IF NOT EXISTS org AUTHORIZATION konnyaku;
GRANT USAGE ON SCHEMA org TO kk_organization;
ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA org GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO kk_organization;
ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA org GRANT USAGE, SELECT ON SEQUENCES TO kk_organization;
