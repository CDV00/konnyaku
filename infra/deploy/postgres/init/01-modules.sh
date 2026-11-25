#!/usr/bin/env bash
# Production bootstrap, run once by the postgres image on an empty volume (as POSTGRES_USER = konnyaku, the schema owner).
# Same layout as infra/docker/postgres/init/01-modules.sql (dev), but every module role gets its password from
# DB_PASSWORD_<MODULE> (see compose.yaml / .env). Keep both module lists in sync.
set -euo pipefail

modules=(
  "platform platform"
  "catalog catalog"
  "identity identity"
  "organization org"
  "crm crm"
  "academic academic"
  "scheduling scheduling"
  "billing billing"
  "workspace workspace"
  "learning learning"
  "hr hr"
  "notification notification"
  "assessment assessment"
  "content content"
)

psql=(psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB")

"${psql[@]}" <<'SQL'
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;
-- unaccent() is only STABLE; search columns and indexes need an IMMUTABLE wrapper ("Ân" -> "An").
CREATE OR REPLACE FUNCTION public.kk_unaccent(text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
AS $fn$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $fn$;
SQL

for entry in "${modules[@]}"; do
  read -r module schema <<<"$entry"
  var="DB_PASSWORD_${module^^}"
  password="${!var:-}"
  if [[ -z "$password" ]]; then
    echo "missing $var" >&2
    exit 1
  fi
  # Passwords travel as psql variables (:'pw' quotes them), never interpolated into SQL text.
  "${psql[@]}" -v role="kk_${module}" -v pw="$password" -v schema="$schema" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS', :'role')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role') \gexec
SELECT format('ALTER ROLE %I PASSWORD %L', :'role', :'pw') \gexec
SELECT format('CREATE SCHEMA IF NOT EXISTS %I AUTHORIZATION konnyaku', :'schema') \gexec
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', :'schema', :'role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', :'schema', :'role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE konnyaku IN SCHEMA %I GRANT USAGE, SELECT ON SEQUENCES TO %I', :'schema', :'role') \gexec
SQL
done

echo "konnyaku: module roles and schemas ready"
