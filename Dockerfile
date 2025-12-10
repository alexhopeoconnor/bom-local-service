# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["BomLocalService.csproj", "./"]
RUN dotnet restore "BomLocalService.csproj"

# Copy everything else and build
COPY . .
RUN dotnet build "BomLocalService.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "BomLocalService.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage - use official Playwright .NET image that has browsers and dependencies pre-installed
FROM mcr.microsoft.com/playwright/dotnet:v1.57.0-noble AS final
WORKDIR /app

# Install .NET 9 runtime and Xvfb for virtual display (Playwright image has browsers but may need .NET 9)
RUN apt-get update && \
    wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh && \
    chmod +x dotnet-install.sh && \
    ./dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet && \
    rm dotnet-install.sh && \
    apt-get install -y xvfb x11vnc fluxbox && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

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

ENTRYPOINT ["/app/start.sh"]
