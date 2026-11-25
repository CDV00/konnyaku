-- Read-only statistics role of postgres-exporter: pg_monitor (pg_stat_* views, settings), no access to any module's
-- tables. Run by the postgres-monitor-role service on every `up` (idempotent; also sets the password from .env).
SELECT 'CREATE ROLE kk_monitor LOGIN'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'kk_monitor') \gexec

ALTER ROLE kk_monitor WITH LOGIN NOSUPERUSER NOBYPASSRLS CONNECTION LIMIT 3 PASSWORD :'pw';
GRANT pg_monitor TO kk_monitor;
