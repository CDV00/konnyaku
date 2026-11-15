# syntax=docker/dockerfile:1.7
# One build, three runtime images: docker build --target api|worker|migrator .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 ... (arm64: e.g. Oracle Ampere A1).
# Build context: backend/ (see .dockerignore).

# The SDK always runs on the build machine's own architecture and cross-compiles for TARGETARCH,
# so an arm64 image is built at native speed instead of under QEMU emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Hosts/Konnyaku.Api -c Release -a "$TARGETARCH" --self-contained false -o /out/api \
 && dotnet publish src/Hosts/Konnyaku.Worker -c Release -a "$TARGETARCH" --self-contained false -o /out/worker \
 && dotnet publish src/Hosts/Konnyaku.Migrator -c Release -a "$TARGETARCH" --self-contained false -o /out/migrator

# ICU and tzdata ship with the Debian-based images: InvariantGlobalization is off and schedules use IANA time zones.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Npgsql probes for GSSAPI at connect time and logs an error when the library is missing (Kerberos is not used).
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
WORKDIR /app
USER $APP_UID

FROM runtime AS api
COPY --from=build /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Konnyaku.Api.dll"]

FROM runtime AS worker
COPY --from=build /out/worker .
ENTRYPOINT ["dotnet", "Konnyaku.Worker.dll"]

FROM runtime AS migrator
COPY --from=build /out/migrator .
ENTRYPOINT ["dotnet", "Konnyaku.Migrator.dll"]
