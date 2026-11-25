# PostgreSQL 17 with pgBackRest (continuous backup to S3-compatible object storage, docs/trien-khai.md#backup).
# Built on the server by `docker compose build postgres` (scripts/deploy.sh does it when the image is missing) and used
# by two services: `postgres` (archive_command pushes WAL) and `backup` (scheduled backups, drills, restores).
# The scripts are bind-mounted (postgres/backup), not copied, so changing them never restarts the database.
FROM postgres:17

# Empty: the newest pgBackRest of the PGDG repository (already configured in the base image).
ARG PGBACKREST_VERSION=
RUN apt-get update \
 && apt-get install -y --no-install-recommends "pgbackrest${PGBACKREST_VERSION:+=$PGBACKREST_VERSION}" ca-certificates jq \
 && rm -rf /var/lib/apt/lists/* \
 && install -d -o postgres -g postgres -m 750 /var/spool/pgbackrest /var/lib/pgbackrest /var/log/pgbackrest /tmp/pgbackrest
