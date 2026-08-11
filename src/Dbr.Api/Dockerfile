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

# Never run the API as root — a connector-adjacent process should hold as little
# ambient authority as the image can give it.
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dbr.Api.dll"]
