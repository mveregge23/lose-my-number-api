# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later

# Build context is the repo root, so Directory.Build.props and db/migrations/ are
# both reachable. The migration scripts are compiled into the assembly, so they have
# to be present at build time — that is deliberate (§18.5): the image carries its own
# migrations, and there is no mounted path that could disagree with the binary.
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

USER $APP_UID

ENTRYPOINT ["dotnet", "Dbr.Migrator.dll"]
