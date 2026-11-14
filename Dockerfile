# Context audit stage: inspect only immutable build inputs.  Keeping this
# separate from /src means dotnet restore's generated /src/obj can never be
# mistaken for a context input or make the guard reject its own output.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS context
WORKDIR /context
COPY . .
# Packaging guard: .dockerignore is the first line of defence.  Fail the build
# if a local override, credential or runtime-state sentinel still reaches the
# build context, so a regressed ignore rule cannot ship it silently.
RUN set -eu; \
    offenders="$(find /context -type f \( \
        -name 'appsettings.Local.json' -o -name 'appsettings.local.json' \
        -o -name 'appsettings.Overrides.json' -o -name 'appsettings.*.local.json' \
        -o -name '.env' -o \( -name '.env.*' ! -name '*.example' \) \
        -o -name '.git-credentials' -o -name '.netrc' -o -name '_netrc' \
        -o -name '.pypirc' -o -name '.mcp.json' -o -name '*.trx' \
        -o -name '*.pyc' -o -name 'test_*.py' -o -name '*.tmp' -o -name '*.pfx' -o -name '*.pem' \
    \) -print -quit)"; \
    if [ -n "$offenders" ]; then \
        echo "forbidden build-context file: $offenders" >&2; exit 1; \
    fi; \
    for name in cache artifacts tests bin obj __pycache__ TestResults \
        .git .aws .azure .ssh .kube .config .agents .codex .claude .cursor \
        .vs .vscode .idea .pytest_cache .mypy_cache node_modules secrets; do \
        found="$(find /context -type d -name "$name" -print -quit)"; \
        if [ -n "$found" ]; then \
            echo "forbidden build-context directory: $found" >&2; exit 1; \
        fi; \
    done

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project, committed lockfile and SDK pin before restore.
COPY --from=context /context/BomLocalService.csproj /context/packages.lock.json /context/global.json ./
RUN MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
    dotnet restore "BomLocalService.csproj" --locked-mode

# Copy the audited source after restore.  The context stage contains no
# restore output, so this cannot overwrite the generated /src/obj.
COPY --from=context /context/ ./
RUN MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
    dotnet build "BomLocalService.csproj" -c Release -o /app/build --no-restore \
    --disable-build-servers -p:UseSharedCompilation=false

# Publish stage
FROM build AS publish
RUN MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
    dotnet publish "BomLocalService.csproj" -c Release -o /app/publish \
    /p:UseAppHost=false --no-restore \
    --disable-build-servers -p:UseSharedCompilation=false
# Publish guard: the image receives only published application content.  Local
# overrides, credentials, test results and restore inputs must not be present.
RUN set -eu; \
    offenders="$(find /app/publish -type f \( \
        -name 'appsettings.Local.json' -o -name 'appsettings.local.json' \
        -o -name 'appsettings.Overrides.json' -o -name 'appsettings.*.local.json' \
        -o -name '.env' -o \( -name '.env.*' ! -name '*.example' \) \
        -o -name '.git-credentials' -o -name '.netrc' -o -name '_netrc' \
        -o -name '.pypirc' -o -name '.mcp.json' -o -name '*.trx' \
        -o -name '*.pyc' -o -name 'test_*.py' -o -name '*.tmp' -o -name '*.pfx' -o -name '*.pem' \
        -o -name 'packages.lock.json' -o -name 'global.json' \
        -o -name '*Tests.dll' -o -name '*.csproj' -o -name '*.sln' \
    \) -print -quit)"; \
    if [ -n "$offenders" ]; then \
        echo "forbidden publish file: $offenders" >&2; exit 1; \
    fi; \
    for name in cache artifacts tests bin obj TestResults testresults test-results verification-output; do \
        found="$(find /app/publish -maxdepth 1 -type d -name "$name" -print -quit)"; \
        if [ -n "$found" ]; then \
            echo "forbidden publish directory: $found" >&2; exit 1; \
        fi; \
    done; \
    for name in .git .aws .azure .ssh .kube .config .agents .codex .claude .cursor \
        .vs .vscode .idea .pytest_cache .mypy_cache __pycache__ node_modules secrets; do \
        found="$(find /app/publish -type d -name "$name" -print -quit)"; \
        if [ -n "$found" ]; then \
            echo "forbidden publish directory: $found" >&2; exit 1; \
        fi; \
    done

# Runtime stage - official Playwright .NET image with version-matched Chromium.
# The Playwright package stays at 1.57.0 so package and browser image remain aligned.
FROM mcr.microsoft.com/playwright/dotnet:v1.57.0-noble AS final

# Build identity is supplied by candidate builds. Local builds keep explicit
# unknown values rather than claiming a released version or revision.
ARG VERSION=dev
ARG SOURCE_REVISION=unknown
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

# The published Playwright driver's bundled Node is observed at 0744
# root:root, so a non-root runtime user (compose runs uid 1000) cannot
# execute it and Playwright.CreateAsync fails with EACCES.  Normalise the
# bundled Node binaries so the driver can be launched by that user.
RUN chmod a+rx /app/.playwright/node/*/node

# Create cache directory for screenshots
RUN mkdir -p /app/cache && chmod 777 /app/cache

# Copy startup script
COPY scripts/docker-start.sh /app/start.sh
RUN chmod +x /app/start.sh

# OCI labels for GitHub Container Registry metadata
LABEL org.opencontainers.image.source="https://github.com/alexhopeoconnor/bom-local-service"
LABEL org.opencontainers.image.description="BOM Local Service - Australian Bureau of Meteorology radar data caching service for Home Assistant and local services"
LABEL org.opencontainers.image.licenses=MIT
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.revision="${SOURCE_REVISION}"

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
  CMD curl --fail http://localhost:8080/api/health || exit 1

ENTRYPOINT ["/app/start.sh"]
