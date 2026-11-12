# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project and its committed lockfile first for deterministic restore.
COPY ["BomLocalService.csproj", "packages.lock.json", "./"]
RUN dotnet restore "BomLocalService.csproj" --locked-mode

# Copy everything else and build
COPY . .
RUN dotnet build "BomLocalService.csproj" -c Release -o /app/build --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish "BomLocalService.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

# Runtime stage - official Playwright .NET image with version-matched Chromium.
# The Playwright package stays at 1.57.0 so package and browser image remain aligned.
FROM mcr.microsoft.com/playwright/dotnet:v1.57.0-noble AS final
WORKDIR /app

# Install ASP.NET Core 10 (not merely the base .NET runtime) and the display tools.
# Version 10.0.15 is the tested runtime matching the .NET 10 SDK used to build.
RUN apt-get update && \
    wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh && \
    chmod +x dotnet-install.sh && \
    ./dotnet-install.sh --runtime aspnetcore --version 10.0.15 --install-dir /usr/share/dotnet && \
    rm dotnet-install.sh && \
    apt-get install -y xvfb x11vnc fluxbox curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Fail the build if the expected ASP.NET Core runtime is absent.
RUN dotnet --list-runtimes | grep -E '^Microsoft\.AspNetCore\.App 10\.'

# Copy published app
COPY --from=publish /app/publish .

# Create cache directory for screenshots
RUN mkdir -p /app/cache && chmod 777 /app/cache

# Copy startup script
COPY scripts/docker-start.sh /app/start.sh
RUN chmod +x /app/start.sh

# OCI labels for GitHub Container Registry metadata
LABEL org.opencontainers.image.source="https://github.com/alexhopeoconnor/bom-local-service"
LABEL org.opencontainers.image.description="BOM Local Service - Australian Bureau of Meteorology radar data caching service for Home Assistant and local services"
LABEL org.opencontainers.image.licenses=MIT

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
  CMD curl --fail http://localhost:8080/api/health || exit 1

ENTRYPOINT ["/app/start.sh"]
