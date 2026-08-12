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

USER $APP_UID

ENTRYPOINT ["dotnet", "Dbr.Worker.dll"]
