# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later

# Build context is the repo root — see the note in src/Dbr.Api/Dockerfile.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Dbr.Domain/Dbr.Domain.csproj          src/Dbr.Domain/
COPY src/Dbr.Infrastructure/Dbr.Infrastructure.csproj src/Dbr.Infrastructure/
COPY src/Dbr.Worker/Dbr.Worker.csproj          src/Dbr.Worker/
RUN dotnet restore src/Dbr.Worker/Dbr.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Dbr.Worker/Dbr.Worker.csproj -c Release -o /app --no-restore

# The worker holds no listening port; the runtime-deps-free aspnet image is still
# the right base once Playwright lands, since a headless browser needs more than
# runtime-deps provides.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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

ENTRYPOINT ["dotnet", "Dbr.Worker.dll"]
