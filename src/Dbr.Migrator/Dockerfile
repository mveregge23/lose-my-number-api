# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later

# Build context is the repo root, so Directory.Build.props and db/migrations/ are
# both reachable. The migration scripts are compiled into the assembly, so they have
# to be present at build time — deliberately, since it means the image carries its own
# migrations and there is no mounted path that could disagree with the binary.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Dbr.Migrator/Dbr.Migrator.csproj src/Dbr.Migrator/
RUN dotnet restore src/Dbr.Migrator/Dbr.Migrator.csproj

COPY src/Dbr.Migrator/ src/Dbr.Migrator/
COPY db/ db/
RUN dotnet publish src/Dbr.Migrator/Dbr.Migrator.csproj -c Release -o /app --no-restore

# runtime, not aspnet — this is a console app that exits, not a service.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Npgsql probes for the Kerberos library while negotiating how to authenticate. The
# .NET runtime images do not ship it, so every start writes a loader failure to
# stderr — which is the first thing an operator reads, and it reads as the database
# connection having failed when in fact it succeeded by another method moments later.
# Installing the library makes the probe answerable rather than noisy. It changes
# nothing about how this connects: that is settled by the connection string, which
# uses a password.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

USER $APP_UID

ENTRYPOINT ["dotnet", "Dbr.Migrator.dll"]
