# SPDX-FileCopyrightText: 2026 Max Veregge
# SPDX-License-Identifier: AGPL-3.0-or-later

# Build context is the repo root, so Directory.Build.props and the sibling
# projects Dbr.Api depends on are all reachable.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project graph alone first, so a source-only edit doesn't
# invalidate the restore layer.
COPY Directory.Build.props ./
COPY src/Dbr.Domain/Dbr.Domain.csproj          src/Dbr.Domain/
COPY src/Dbr.Infrastructure/Dbr.Infrastructure.csproj src/Dbr.Infrastructure/
COPY src/Dbr.Api/Dbr.Api.csproj                src/Dbr.Api/
RUN dotnet restore src/Dbr.Api/Dbr.Api.csproj

COPY src/ src/
RUN dotnet publish src/Dbr.Api/Dbr.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Never run the API as root. This process ends up adjacent to code that talks to
# untrusted third-party sites, so it should hold as little ambient authority as the
# image can give it.
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

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dbr.Api.dll"]
