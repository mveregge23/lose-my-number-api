# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later

# Build context is the repo root, so Directory.Build.props, src/Dbr.Domain and catalog/
# are all reachable. The catalog files are compiled into the assembly, so they have to
# be present at build time — deliberately, since it means the image carries its own
# content and there is no mounted path that could disagree with the binary. A sync that
# retracts what its files no longer describe must never be able to read an empty
# directory by accident.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Dbr.Domain/Dbr.Domain.csproj src/Dbr.Domain/
COPY src/Dbr.CatalogSync/Dbr.CatalogSync.csproj src/Dbr.CatalogSync/
RUN dotnet restore src/Dbr.CatalogSync/Dbr.CatalogSync.csproj

COPY src/Dbr.Domain/ src/Dbr.Domain/
COPY src/Dbr.CatalogSync/ src/Dbr.CatalogSync/
COPY catalog/ catalog/
RUN dotnet publish src/Dbr.CatalogSync/Dbr.CatalogSync.csproj -c Release -o /app --no-restore

# runtime, not aspnet — this is a console app that exits, not a service.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Npgsql probes for the Kerberos library while negotiating how to authenticate, and the
# runtime images do not ship it, so every start writes a loader failure to stderr that
# reads as a failed database connection. Installing it makes the probe answerable rather
# than noisy; how this authenticates is settled by the connection string either way.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

USER $APP_UID

ENTRYPOINT ["dotnet", "Dbr.CatalogSync.dll"]
