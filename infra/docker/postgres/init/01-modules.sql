-- Dev/test bootstrap. Runs as the schema owner (POSTGRES_USER = konnyaku).
-- Each module gets its own schema and a login role kk_<module> with DML rights on that schema only,
-- so cross-module SQL fails with a permission error. Module roles cannot bypass RLS.
-- Add a row to the list when a module is introduced (and recreate the dev volume: docker compose down -v).

CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

DO $$
DECLARE
    m record;
    role_name text;
BEGIN
    FOR m IN SELECT * FROM (VALUES
        ('platform', 'platform'),
        ('catalog', 'catalog'),
        ('identity', 'identity'),
        ('organization', 'org'),
        ('crm', 'crm'),
        ('academic', 'academic'),
        ('scheduling', 'scheduling'),
        ('billing', 'billing'),
        ('workspace', 'workspace'),
        ('learning', 'learning')
    ) AS v(module, schema_name)
    LOOP
        role_name := 'kk_' || m.module;
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = role_name) THEN
            EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS', role_name, role_name || '_dev');
        END IF;
        EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I AUTHORIZATION konnyaku', m.schema_name);
        EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', m.schema_name, role_name);
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', m.schema_name, role_name);
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO %I', m.schema_name, role_name);
    END LOOP;
END $$;
