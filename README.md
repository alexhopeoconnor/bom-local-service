<p align="center">
  <img width="256" height="auto" alt="icon" src="https://github.com/user-attachments/assets/6f712fbd-4f42-49d4-9ddb-1767bb43551a" />
</p>

# BOM Local Service

A local caching service for Australian Bureau of Meteorology (BOM) radar data, designed to provide a reliable API for Home Assistant integrations and other local services.

**Development status (2026-11-15):** This checkout contains the **unreleased
v0.1.1 candidate**. The architecture and API changes below describe that
candidate; they are not yet available in a published v0.1.1 image. The latest
GitHub release is [v0.0.6](https://github.com/alexhopeoconnor/bom-local-service/releases/tag/v0.0.6).
See [candidate validation and limitations](#candidate-validation-and-limitations)
before treating the candidate as release-qualified. The active service-only
implementation and evidence requirements are tracked in the
[service qualification register](docs/service-qualification.md). Test instances
must use isolated resources, a finite lifetime and verified teardown; a completed
short smoke run does not qualify the planned 30-hour trial.

## Background

The Australian Bureau of Meteorology's radar API endpoint (`https://api.weather.bom.gov.au/v1/radar/capabilities`) stopped working in December 2024, returning errors. This broke integrations like the popular [bom-radar-card](https://github.com/Makin-Things/bom-radar-card) for Home Assistant, which can no longer render due to the Cross-Origin Request Blocked error. 

The issue was reported in [GitHub issue #40](https://github.com/Makin-Things/bom-radar-card/issues/40), where multiple users confirmed the card completely fails to render.

This project was created to bridge that gap by:
- Caching radar data from the BOM website
- Storing it locally in a structured format
- Providing a simple REST API for local services to consume
- Automatically managing cache updates and cleanup

## Features

- 🌧️ **Automatic Cache Management**: Background service automatically updates radar data for all cached locations
- 📊 **Historical Data**: Access historical radar frames across multiple time periods
- 🎯 **Location-Based**: Support for any Australian suburb/state combination
- 🖼️ **Image API**: Direct access to individual radar frame images
- 🔄 **Auto-Refresh**: Configurable cache expiration and refresh intervals
- 🧹 **Automatic Cleanup**: Old cache data is automatically purged based on retention settings
- 🎨 **Demo SPA**: Built-in web interface for testing and demonstration
- 🐳 **Easy Docker Deployment**: Quick setup with Docker

## Architecture

Built on ASP.NET Core 10.0 (LTS), the service uses a service-oriented architecture with clear separation of concerns:

### Core Services
- **BomRadarService**: Read orchestration, historical selection and refresh requests
- **CacheService**: Snapshot validation, safe folder resolution, publication and metadata
- **CacheRefreshService**: Single owner of the bounded refresh queue, deduplication, deadlines, retry backoff and status
- **RadarCaptureService**: The browser/scraping capture boundary; writes only into a staging directory
- **BrowserService**: Playwright browser lifecycle with disconnection detection and recreation
- **ScrapingService**: Coordinates web scraping workflows (simplified orchestrator)
- **SelectorService**: Finds page elements using configurable CSS selectors with fallback support
- **TimeParsingService**: Parses and converts time formats from BOM data
- **DebugService**: Provides debug functionality for troubleshooting

### Scraping Architecture
The scraping system uses a **workflow-based architecture** with configurable steps:

- **Workflows**: Define fixed sequences of steps for different data types (e.g., `RadarScrapingWorkflow`, `TemperatureMapWorkflow`). Each workflow specifies its response type via generics (`IWorkflow<TResponse>`)
- **Steps**: Individual, testable units that perform specific actions (navigation, search, map interaction, capture). Steps declare prerequisites and validate page state before execution
- **Step Registry**: Manages and discovers available scraping steps
- **Workflow Factory**: Creates typed workflow instances based on configuration

**Configuration-Driven Design:**
- **Selectors**: All CSS selectors are configurable via `appsettings.json` with fallback options, allowing adaptation to website changes without code modifications
- **JavaScript Templates**: JavaScript code for page evaluation is externalized in configuration, making it easy to update logic as the website evolves
- **Text Patterns**: Regex patterns for parsing page content are configurable, enabling quick adjustments to parsing logic
- **Workflow Steps**: Individual steps within workflows can be enabled/disabled via configuration, providing flexibility for testing and troubleshooting

### Background Services
- **CacheRefreshService**: Hosted worker that owns refresh work independently of HTTP requests. The worker restores durable state before admission and coordinates queueing with shutdown; focused regression checks pass.
- **CacheManagementService**: Periodically requests refreshes for known locations through the same queue.
- **CacheCleanupService**: Removes recognised expired snapshots while preserving the newest complete fallback per location and leaving unrecognised top-level directories alone.

### Cache Publication
A capture writes into `cache/.staging/` before a same-filesystem rename into a final snapshot folder. The current source validates bounded metadata, frame indices, PNG structure and decoded pixels before publication. Readers select metadata and frames from one snapshot. Supported complete legacy folders without `.complete` remain readable. Containment, corruption, identity and legacy compatibility regressions pass.

### API Layer
- **RadarController**: REST endpoints for accessing radar data (`/api/radar/{suburb}/{state}`)
- **CacheController**: REST endpoints for cache management operations (`/api/cache/{suburb}/{state}`)
- **RadarTestController**: MVC controller serving the demo SPA at `/radar/{suburb}/{state}`

## Installation

### Docker Image

Pre-built Docker images are available on GitHub Container Registry:

**Image:** `ghcr.io/alexhopeoconnor/bom-local-service`

The candidate workflow targets `linux/amd64` and `linux/arm64`. Native
qualification of the final candidate images is still outstanding.

**Pull the published image:**
```bash
docker pull ghcr.io/alexhopeoconnor/bom-local-service:latest
```

`latest` is mutable and does not identify this local candidate. There is no
published `v0.1.1` image yet. To test these changes, build the candidate locally
as shown below. See published versions on the [releases page](https://github.com/alexhopeoconnor/bom-local-service/releases).

### Prerequisites

- Docker Engine 20.10+ or Docker Desktop
- Docker Compose v2.0+ (optional, for easier management)

### Quick Start

#### Option 1: Using the Published Docker Image

Use this path for the published service, whose behavior may differ from the
candidate described in this README:

```bash
docker pull ghcr.io/alexhopeoconnor/bom-local-service:latest
```

Then run the container:
```bash
docker run -d \
  --name bom-local-service \
  -p 8082:8080 \
  -v $(pwd)/cache:/app/cache \
  --shm-size=1gb \
  --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service:latest
```

Record the resolved image digest when deploying so the same image can be
selected again. Confirm version tags in the registry before using them.

#### Option 2: Build the Candidate from Source

1. **Use a checkout containing the candidate changes**. A fresh clone starts
   from the repository's published default branch; it does not include local,
   unpushed candidate work. Run the following commands from the candidate
   checkout's repository root.

2. **Build the Docker image**:
   ```bash
   docker build -t bom-local-service:candidate .
   ```

   The build process uses a multi-stage Dockerfile:
   - **Build stage**: .NET 10 SDK with locked package restore
   - **Runtime stage**: Official Playwright .NET image with version-matched Chromium, plus ASP.NET Core 10
   - Sets up a virtual display (Xvfb) for browser operation

   Docker supplies the build SDK. Building directly with `dotnet` requires the
   .NET 10 SDK selected by `global.json` (`10.0.100`, latest feature band).

3. **Run the container**:
   ```bash
   docker run -d \
     --name bom-local-service \
     -p 8082:8080 \
     -v $(pwd)/cache:/app/cache \
     --shm-size=1gb \
     --ipc=host \
     bom-local-service:candidate
   ```

   **Note**: The `--shm-size=1gb` and `--ipc=host` flags are important for Playwright to function correctly in Docker.

### Using Docker Compose

The included `docker-compose.yml` provides a convenient way to run the service with all configuration options.

**Using the published image:**

Override the candidate image name and explicitly disable the local build:
```bash
BOM_IMAGE=ghcr.io/alexhopeoconnor/bom-local-service:latest docker compose up -d --no-build --pull always
```

**Building the local candidate:**

The checked-in Compose default names `v0.1.1`, which is not published yet.
Use a local tag and build it explicitly:
```bash
BOM_IMAGE=bom-local-service:candidate docker compose up -d --build
```

**Mounting custom appsettings.json:**

To use a custom configuration file, add it to the volumes section in `docker-compose.yml`:
```yaml
services:
  bom-local-service:
    volumes:
      - ./cache:/app/cache
      - ./appsettings.json:/app/appsettings.json:ro  # Custom config
```

This will:
- Pull/build the image as configured
- Start the service on port 8082 (configurable via `HOST_PORT`)
- Mount the `./cache` directory for persistent storage
- Mount custom `appsettings.json` if specified
- Apply all environment variable configurations (which override appsettings.json)

To view logs:
```bash
docker-compose logs -f
```

To stop the service:
```bash
docker-compose down
```

## Configuration

All configuration can be done via environment variables, which override the default values in `appsettings.json`. The service uses ASP.NET Core's configuration system, which supports nested configuration via double underscores (`__`).

### Environment Variables

#### ASP.NET Core Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | Runtime environment (Development/Production) | `Production` |
| `ASPNETCORE_URLS` | URLs the service listens on | `http://+:8080` |
| `ENABLEHTTPSREDIRECTION` | Enable HTTPS redirection | `false` |

#### Application Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CACHEDIRECTORY` | Directory path for cache storage | `/app/cache` | `/data/bom-cache` |
| `CACHERETENTIONHOURS` | Hours to retain cached data before cleanup (can be any positive integer) | `24` | `48`, `72`, `168` (1 week) |
| `CACHEEXPIRATIONMINUTES` | Minutes before cache is considered expired | `12.5` | `15` |
| `TIMEZONE` | Timezone for time parsing (IANA format) | `Australia/Brisbane` | `Australia/Sydney` |

#### Cache Management

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CACHEMANAGEMENT__CHECKINTERVALMINUTES` | Interval between cache validity checks | `5` | `10` |
| `CACHEMANAGEMENT__INITIALDELAYSECONDS` | Delay before first cache check on startup | `10` | `30` |
| `CACHEMANAGEMENT__LOCATIONSTAGGERSECONDS` | Delay between processing different locations (used for both initial and periodic updates) | `1` | `2` |

#### Cache Cleanup

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CACHECLEANUP__INTERVALHOURS` | Interval between cleanup runs | `1` | `2` |

#### Screenshot Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `SCREENSHOT__DYNAMICCONTENTWAITMS` | Milliseconds to wait for dynamic content to load | `1500` | `2000` |
| `SCREENSHOT__TILERENDERWAITMS` | Milliseconds to wait for map tiles to render | `3000` | `5000` |
| `SCREENSHOT__CROP__X` | X offset in pixels for screenshot cropping | `250` | `300` |
| `SCREENSHOT__CROP__Y` | Y offset in pixels for screenshot cropping | `0` | `50` |
| `SCREENSHOT__CROP__RIGHTOFFSET` | Right offset in pixels for screenshot cropping | `250` | `300` |
| `SCREENSHOT__CROP__HEIGHT` | Height in pixels for screenshot cropping (null = full height) | `null` | `800` |

#### Scraping Configuration

The scraping system is highly configurable through `appsettings.json`. Most scraping settings (selectors, JavaScript templates, text patterns, workflow steps) are configured in `appsettings.json`, but can be overridden in Docker deployments.

**Option 1: Mount Custom appsettings.json (Recommended for Docker)**

Mount a custom `appsettings.json` file as a volume:

```bash
docker run -d \
  --name bom-local-service \
  -p 8082:8080 \
  -v $(pwd)/cache:/app/cache \
  -v $(pwd)/appsettings.json:/app/appsettings.json:ro \
  --shm-size=1gb \
  --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service:latest
```

Or in `docker-compose.yml`:
```yaml
services:
  bom-local-service:
    volumes:
      - ./cache:/app/cache
      - ./appsettings.json:/app/appsettings.json:ro  # Add this line
```

**Option 2: Environment Variables (Simple Overrides)**

For quick overrides of commonly needed settings:

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `SCRAPING__BASEURL` | Base URL for the BOM website | `https://www.bom.gov.au/` | `https://www.bom.gov.au/` |

**Note**: Complex configurations (selectors, JavaScript templates, text patterns, workflow steps) are best managed via a mounted `appsettings.json` file. See the [Configuration File](#configuration-file) section below for the complete structure.

#### Debug Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `DEBUG__ENABLED` | Enable debug mode (saves debug screenshots) | `false` | `true` |
| `DEBUG__WAITMS` | Additional wait time in debug mode | `2000` | `5000` |

#### Time Series Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `TIMESERIES__WARNINGFOLDERCOUNT` | Number of cache folders that triggers a warning log when processing time series requests | `200` | `300` |
| `TIMESERIES__MAXTIMERANGEHOURS` | Maximum time range allowed for time series queries (null = use CacheRetentionHours) | `null` (uses CacheRetentionHours) | `72` |

#### Docker Compose Port Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `HOST_PORT` | Port on host machine to map to container | `8082` |
| `CONTAINER_PORT` | Port inside container (usually 8080) | `8080` |

### Configuration Examples

#### Basic Configuration

```bash
docker run -d \
  --name bom-local-service \
  -p 8082:8080 \
  -v $(pwd)/cache:/app/cache \
  -e CACHERETENTIONHOURS=48 \
  -e CACHEEXPIRATIONMINUTES=15 \
  --shm-size=1gb \
  --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service:latest
```

#### Custom Cache Directory

```bash
docker run -d \
  --name bom-local-service \
  -p 8082:8080 \
  -v /data/bom-cache:/app/cache \
  -e CACHEDIRECTORY=/app/cache \
  --shm-size=1gb \
  --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service:latest
```

#### Development Mode with Debug

```bash
docker run -d \
  --name bom-local-service \
  -p 8082:8080 \
  -v $(pwd)/cache:/app/cache \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e DEBUG__ENABLED=true \
  -e DEBUG__WAITMS=5000 \
  --shm-size=1gb \
  --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service:latest
```

#### Using docker-compose with Custom Settings

Create a `.env` file in the project root:

```env
HOST_PORT=8082
CACHERETENTIONHOURS=48
CACHEEXPIRATIONMINUTES=15
CACHEMANAGEMENT__CHECKINTERVALMINUTES=10
TIMEZONE=Australia/Sydney
```

Then run:
```bash
docker-compose up -d
```

#### Custom appsettings.json (For Selector/Scraping Configuration)

If you need to customize selectors, JavaScript templates, or workflow steps:

1. **Copy the default appsettings.json** from the repository
2. **Edit the sections you need** (e.g., `Scraping:Selectors`)
3. **Mount it as a volume**:

```bash
docker run -d \
  --name bom-local-service \
  -p 8082:8080 \
  -v $(pwd)/cache:/app/cache \
  -v $(pwd)/appsettings.json:/app/appsettings.json:ro \
  --shm-size=1gb \
  --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service:latest
```

Or in `docker-compose.yml`:
```yaml
services:
  bom-local-service:
    volumes:
      - ./cache:/app/cache
      - ./appsettings.json:/app/appsettings.json:ro
```

**Note**: Environment variables still override values in the mounted `appsettings.json`, so you can use env vars for simple overrides and the mounted file for complex configurations.

### Configuration File

For advanced scraping configuration, edit `appsettings.json` directly. When using Docker, you can mount a custom `appsettings.json` file as a volume (see [Scraping Configuration](#scraping-configuration) above).

The scraping system supports extensive configuration:

#### Scraping Selectors

All CSS selectors used to find page elements are configurable with fallback options:

```json
{
  "Scraping": {
    "Selectors": {
      "SearchButton": {
        "Name": "Search Button",
        "Selectors": [
          "button[data-testid='searchLabel']",
          "button[aria-label='Search for a location']",
          "button.search-location__trigger-button"
        ],
        "TimeoutMs": 10000,
        "Required": true,
        "ErrorMessage": "Could not find search button"
      }
    }
  }
}
```

#### JavaScript Templates

JavaScript code used for page evaluation is externalized and configurable:

```json
{
  "Scraping": {
    "JavaScriptTemplates": {
      "WaitForSearchResults": "() => { /* template code */ }",
      "ExtractSearchResults": "() => { /* template code */ }"
    }
  }
}
```

#### Text Patterns

Regex patterns for parsing page content are configurable:

```json
{
  "Scraping": {
    "TextPatterns": {
      "ResultsCountPattern": "(\\d+)\\s+of\\s+(\\d+)",
      "TimestampPattern": "(?:[A-Za-z]+\\s+)?\\d{1,2}\\s+[A-Za-z]{3},?\\s+\\d{1,2}:\\d{2}\\s+(?:am|pm)",
      "TimestampPattern": "(?:[A-Za-z]+\\s+)?\\d{1,2}\\s+[A-Za-z]{3},?\\s+\\d{1,2}:\\d{2}\\s+(?:am|pm)"
    }
  }
}
```

#### Workflow Steps

Individual workflow steps can be enabled/disabled and configured:

```json
{
  "Scraping": {
    "Workflows": {
      "RadarScraping": {
        "Description": "Scrapes radar images for a location",
        "Steps": {
          "NavigateHomepage": { "Enabled": true },
          "ClickSearchButton": { "Enabled": true },
          "CaptureFrames": {
            "Enabled": true,
            "Parameters": {
              "FrameCount": 7,
              "WaitBetweenFrames": 5000
            }
          }
        }
      }
    }
  }
}
```

**Note**: Step order is fixed within workflows due to dependencies. Steps can be disabled but not reordered. See `appsettings.json` for the complete configuration structure.

## API Documentation

The service provides RESTful API endpoints for accessing radar data and managing the cache.

### Base URL

When running locally with default settings: `http://localhost:8082`

### API and cache compatibility contract

This table describes the intended compatibility contract. Follow-up source and
regression cases are implemented; execution status is listed under
[candidate validation and limitations](#candidate-validation-and-limitations).

| Behaviour | Intended contract |
| :-- | :-- |
| Existing routes and JSON fields | Remain compatible |
| Existing cache folders | Remain readable without renaming |
| Location identity | Consistent casing/whitespace normalization |
| State | Valid documented abbreviation, normalized consistently |
| Historical bounds | Inclusive observation times, compared in UTC |
| Missing historical bounds | Preserve existing optional-bound behaviour |
| Invalid location, folder syntax, or range | Structured HTTP 400 |
| Well-formed missing or wrong-location folder | HTTP 404 |
| Empty historical result | Preserve the existing structured 404 behaviour |
| Deletion during queued/running capture | Explicit HTTP 409 conflict, rather than racing the writer |
| Frame indices | Validated against the relevant snapshot/configuration, without hard-coded `0..6` assumptions |

Ordinary Australian place-name punctuation is retained, including apostrophes, hyphens, periods, spaces and Unicode letters. Lossless names keep `Suburb_State_YYYYMMDD_HHMMSS` (spaces become underscores). Literal underscores, commas and leading-dot names use `v2~<base64url-UTF8-suburb>~STATE~YYYYMMDD~HHMMSS`. Identity comparison normalizes case and whitespace. Both final and staging basenames must fit 255 UTF-8 bytes. Legacy underscores continue to mean spaces; they are never guessed to mean literal underscores or omitted commas.

`cacheFolders` grouping is preserved in historical responses for compatibility. Captures can overlap, so grouping by capture cannot guarantee global playback order. Consumers should flatten and sort by `absoluteObservationTime`:

```javascript
const frames = data.cacheFolders
  .flatMap(folder =>
    folder.frames.map(frame => ({
      ...frame,
      cacheFolderName: folder.cacheFolderName,
    })))
  .sort((a, b) =>
    Date.parse(a.absoluteObservationTime) -
    Date.parse(b.absoluteObservationTime)
  );
```

Each frame URL includes `cacheFolder` so its origin snapshot is unambiguous.

### Endpoints

#### Liveness and readiness

- `GET /api/health` is process liveness only. It never contacts BOM and remains healthy during an upstream outage.
- `GET /api/ready` checks local prerequisites only: cache writability and refresh-worker availability. An empty cache is ready; readiness never starts a scrape.

```json
{
  "status": "ready",
  "checks": { "cacheWritable": true, "refreshWorker": true },
  "queueDepth": 0
}
```

#### Get Radar Data

Get the latest radar frames for a location.

```http
GET /api/radar/{suburb}/{state}
```

**Parameters:**
- `suburb` (path): Suburb name (e.g., "Brisbane")
- `state` (path): State abbreviation (e.g., "QLD")

**Response:**
```json
{
  "frames": [
    {
      "frameIndex": 0,
      "imageUrl": "/api/radar/Brisbane/QLD/frame/0?cacheFolder=Brisbane_QLD_20250115_100000",
      "absoluteObservationTime": "2025-01-15T10:00:00Z"
    }
  ],
  "observationTime": "2025-01-15T10:00:00Z",
  "forecastTime": "2025-01-15T10:00:00Z",
  "weatherStation": "Brisbane",
  "distance": "5.2 km",
  "cacheIsValid": true,
  "cacheExpiresAt": "2025-01-15T10:12:30Z",
  "isUpdating": false,
  "nextUpdateTime": "2025-01-15T10:12:30Z"
}
```

**Response Fields:**
- `frames`: Array of radar frame objects with image URLs. Each frame contains `absoluteObservationTime` (UTC timestamp). Client should calculate "minutes ago" dynamically from this timestamp.
- `observationTime`: UTC timestamp when the observation was made
- `forecastTime`: UTC timestamp for the forecast
- `weatherStation`: Name of the weather station
- `distance`: Distance from location to weather station
- `cacheIsValid`: Whether the cache is still valid (not expired)
- `cacheExpiresAt`: UTC timestamp when the cache expires
- `isUpdating`: Whether a cache update is currently in progress
- `nextUpdateTime`: **Estimated** UTC timestamp for when the cache will be updated or when an in-progress update will complete. This value is calculated using:
  - **Metrics-based estimation** (preferred): When historical data is available, uses median durations from previous cache updates to provide hardware-adaptive estimates
  - **Calculated estimation** (fallback): When no metrics are available yet (e.g., first update), calculates based on configured wait times and frame count
  - **Progress-aware**: During active updates, estimates improve as progress is tracked through phases (Initializing → CapturingFrames → Saving)

**Status Codes:**
- `200 OK`: Radar data available
- `404 Not Found`: Cache is being generated (check response for retry information)
  ```json
  {
    "errorCode": "CACHE_NOT_FOUND",
    "errorType": "CacheError",
    "message": "No cached data found for this location (fresh start). Cache update has been triggered in background.",
    "details": {
      "location": { "suburb": "Brisbane", "state": "QLD" },
      "cacheExists": false,
      "cacheIsValid": false,
      "updateTriggered": true,
      "nextUpdateTime": "2025-01-15T10:12:30Z"
    },
    "suggestions": {
      "action": "retry_after_seconds",
      "retryAfter": 30,
      "refreshEndpoint": "/api/cache/Brisbane/QLD/refresh"
    },
    "note": "The retryAfter value is dynamically calculated based on the estimated cache update duration. On first startup with no cache, it uses a calculated estimate. After metrics are collected from completed updates, it uses hardware-adaptive estimates based on actual performance."
    "timestamp": "2025-01-15T10:00:00Z"
  }
  ```
- `400 Bad Request`: Invalid location parameters
  ```json
  {
    "errorCode": "VALIDATION_ERROR",
    "errorType": "ValidationError",
    "message": "Invalid state abbreviation. Use: NSW, VIC, QLD, SA, WA, TAS, NT, ACT",
    "details": {
      "field": "state"
    },
    "timestamp": "2025-01-15T10:00:00Z"
  }
  ```

#### Get Frame Image

Get a specific radar frame image.

```http
GET /api/radar/{suburb}/{state}/frame/{frameIndex}
```

**Parameters:**
- `suburb` (path): Suburb name
- `state` (path): State abbreviation
- `frameIndex` (path): Frame index (0-6 for default 7 frames)
- `cacheFolder` (query, optional): Specific cache folder name for historical data

**Response:**
- `200 OK`: PNG image
- `404 Not Found`: Frame not found
  ```json
  {
    "errorCode": "NOT_FOUND",
    "errorType": "NotFoundError",
    "message": "Frame 3 not found for Brisbane, QLD",
    "details": {
      "resourceType": "Frame",
      "identifier": "Frame 3 for Brisbane, QLD",
      "frameIndex": 3,
      "location": { "suburb": "Brisbane", "state": "QLD" }
    },
    "suggestions": {
      "suggestion": "The frame may not exist yet. Try refreshing the cache or checking if cache update is in progress."
    },
    "timestamp": "2025-01-15T10:00:00Z"
  }
  ```

#### Get Metadata

Get metadata about cached radar data.

```http
GET /api/radar/{suburb}/{state}/metadata
```

**Response:**
```json
{
  "lastUpdated": "2025-01-15T10:00:00Z",
  "observationTime": "2025-01-15T10:00:00Z",
  "forecastTime": "2025-01-15T10:00:00Z",
  "weatherStation": "Brisbane",
  "distance": "5.2 km"
}
```

#### Get Time Series

Get historical radar data across multiple cache folders.

```http
GET /api/radar/{suburb}/{state}/timeseries?startTime={iso8601}&endTime={iso8601}
```

**Parameters:**
- `suburb` (path): Suburb name
- `state` (path): State abbreviation
- `startTime` (query, optional): ISO 8601 start time (e.g., `2025-01-15T00:00:00Z`)
- `endTime` (query, optional): ISO 8601 end time (defaults to now)

**Response (200 OK):**
```json
{
  "cacheFolders": [
    {
      "cacheFolderName": "Brisbane_QLD_20250115_100000",
      "cacheTimestamp": "2025-01-15T10:00:00Z",
      "observationTime": "2025-01-15T10:00:00Z",
      "frames": [
        {
          "frameIndex": 0,
          "imageUrl": "/api/radar/Brisbane/QLD/frame/0?cacheFolder=Brisbane_QLD_20250115_100000",
          "absoluteObservationTime": "2025-01-15T10:00:00Z"
        }
      ]
    }
  ],
  "startTime": "2025-01-15T07:00:00Z",
  "endTime": "2025-01-15T10:00:00Z",
  "totalFrames": 7
}
```

**Status Codes:**
- `200 OK`: Historical data available
- `400 Bad Request`: Invalid request (e.g., time range exceeds maximum allowed duration, invalid time format, startTime after endTime)
  - **Invalid time format**:
    ```json
    {
      "errorCode": "VALIDATION_ERROR",
      "errorType": "ValidationError",
      "message": "Invalid startTime format. Use ISO 8601 format (e.g., 2025-12-07T00:00:00Z)",
      "details": {
        "field": "startTime"
      },
      "timestamp": "2025-01-15T10:00:00Z"
    }
    ```
  - **Time range exceeds maximum**:
    ```json
    {
      "errorCode": "TIME_RANGE_ERROR",
      "errorType": "ValidationError",
      "message": "Time range exceeds maximum allowed duration of 24 hours (based on cache retention: 24 hours). Please specify a smaller range.",
      "details": {
        "requestedRange": {
          "start": "2025-01-15T00:00:00Z",
          "end": "2025-01-15T25:00:00Z",
          "requestedHours": 25.0
        }
      },
      "suggestions": {
        "action": "adjust_time_range"
      },
      "timestamp": "2025-01-15T10:00:00Z"
    }
    ```
- `404 Not Found`: 
  - **Location not cached**: No cache exists for this location. Cache update is triggered in background:
    ```json
    {
      "errorCode": "CACHE_NOT_FOUND",
      "errorType": "CacheError",
      "message": "No cached data found for this location. Cache update has been triggered in background.",
      "details": {
        "location": { "suburb": "Brisbane", "state": "QLD" },
        "cacheExists": false,
        "cacheIsValid": false,
        "updateTriggered": true,
        "cacheExpiresAt": null,
        "nextUpdateTime": "2025-01-15T10:12:30Z",
        "statusMessage": "No cache exists, update triggered"
      },
      "suggestions": {
        "action": "retry_after_seconds",
        "retryAfter": 30,
        "refreshEndpoint": "/api/cache/Brisbane/QLD/refresh",
        "statusEndpoint": "/api/cache/Brisbane/QLD/range"
      },
      "timestamp": "2025-01-15T10:00:00Z"
    }
    ```
  - **No data in range**: Cache exists but no data in the requested time range:
    ```json
    {
      "errorCode": "TIME_RANGE_ERROR",
      "errorType": "ValidationError",
      "message": "No historical data found for the specified time range.",
      "details": {
        "availableRange": {
          "oldest": "2025-01-15T08:00:00Z",
          "newest": "2025-01-15T10:00:00Z",
          "totalCacheFolders": 10,
          "timeSpanMinutes": 120
        },
        "requestedRange": {
          "start": "2025-01-15T00:00:00Z",
          "end": "2025-01-15T10:00:00Z"
        }
      },
      "suggestions": {
        "action": "adjust_time_range",
        "suggestedRange": {
          "start": "2025-01-15T08:00:00Z",
          "end": "2025-01-15T10:00:00Z"
        },
        "suggestion": "Try querying data between 2025-01-15T08:00:00Z and 2025-01-15T10:00:00Z"
      },
      "timestamp": "2025-01-15T10:00:00Z"
    }
    ```

**Time Range Limits:**
- Maximum time range is configurable via `TimeSeries:MaxTimeRangeHours` (defaults to `CacheRetentionHours` or minimum 24 hours)
- If `TimeSeries:MaxTimeRangeHours` is not set, the limit automatically matches your `CacheRetentionHours` setting
- This ensures you can always query all available cached data (e.g., if retention is 72 hours, you can query up to 72 hours)

### Error Response Format

All API endpoints return standardized error responses using the `ApiErrorResponse` format:

```json
{
  "errorCode": "CACHE_NOT_FOUND",
  "errorType": "CacheError",
  "message": "Human-readable error message",
  "details": {
    "location": { "suburb": "Brisbane", "state": "QLD" },
    "cacheExists": false,
    "cacheIsValid": false
  },
  "suggestions": {
    "action": "retry_after_seconds",
    "retryAfter": 30,
    "refreshEndpoint": "/api/cache/Brisbane/QLD/refresh"
  },
  "timestamp": "2025-01-15T10:00:00Z"
}
```

**Error Response Fields:**
- `errorCode`: Machine-readable error code (e.g., `CACHE_NOT_FOUND`, `VALIDATION_ERROR`, `TIME_RANGE_ERROR`)
- `errorType`: Error category (`CacheError`, `ValidationError`, `ServiceError`, `NotFoundError`)
- `message`: Human-readable error description
- `details`: Additional context (varies by error type)
- `suggestions`: Actionable guidance (retry times, endpoints, etc.)
- `timestamp`: UTC timestamp when error occurred

**Common Error Codes:**
- `CACHE_NOT_FOUND`: No cached data exists for the location (fresh start scenario)
- `VALIDATION_ERROR`: Invalid request parameters
- `TIME_RANGE_ERROR`: Time range validation failed or no data in range
- `NOT_FOUND`: Specific resource not found (e.g., frame, metadata)
- `CACHE_UPDATE_FAILED`: Cache update operation failed
- `INTERNAL_ERROR`: Server-side error occurred
- If no time range is specified, returns all available historical data
- **Note**: `CacheRetentionHours` can be set to any positive integer value (24, 48, 72, 168, etc.)

#### Historical selection semantics

Historical requests filter by **observation time**, not by the capture folder timestamp. Bounds are inclusive and compared in UTC. A capture is included when any of its frame observations fall inside the requested interval.

Duplicate observation times are resolved deterministically in favour of the newest complete capture (then folder name, ordinal). Exact observation deduplication replaces the previous unconditional four-minute thinning. `cacheRange` keeps its existing capture-folder fields and adds `oldestObservationTime`/`newestObservationTime`.

#### Get Cache Range

Get information about available historical cache data.

```http
GET /api/cache/{suburb}/{state}/range
```

**Response:**
```json
{
  "oldestCache": {
    "cacheFolderName": "Brisbane_QLD_20250115_000000",
    "cacheTimestamp": "2025-01-15T00:00:00Z"
  },
  "newestCache": {
    "cacheFolderName": "Brisbane_QLD_20250115_100000",
    "cacheTimestamp": "2025-01-15T10:00:00Z"
  },
  "totalCacheFolders": 10,
  "timeSpanMinutes": 600
}
```

#### Get Location Status

```http
GET /api/cache/{suburb}/{state}/status
```

Returns `200 OK` even when the cache is empty. Operational state is persisted outside the capture-folder namespace.

```json
{
  "cacheExists": true,
  "cacheIsValid": false,
  "isUpdating": false,
  "lastSuccessfulCaptureAt": "2026-11-12T01:20:00Z",
  "latestObservationAt": "2026-11-12T01:15:00Z",
  "updateFailed": true,
  "errorCode": "BROWSER_ERROR",
  "consecutiveFailures": 1,
  "nextRetryAt": "2026-11-12T01:27:00Z"
}
```

`updateFailed`, `errorCode`, `error`, `lastUpdateAttempt`, `consecutiveFailures` and `nextRetryAt` are populated from actual outcomes. Error messages are bounded; exception dumps are not exposed.

#### Refresh Cache

Manually trigger a cache update for a location.

```http
POST /api/cache/{suburb}/{state}/refresh
```

**Response:**
```json
{
  "updateTriggered": true,
  "cacheIsValid": false,
  "cacheExpiresAt": null,
  "nextUpdateTime": "2025-01-15T10:12:30Z",
  "message": "Cache update triggered"
}
```

The rejection paths for queue saturation, shutdown, deletion conflict and active failure backoff return `429`, `503`, `409` and `429` respectively with a `rejectionCode`. Accepted work is independent of client disconnection; startup/shutdown admission races still need regression coverage and fixes.

**Note on `nextUpdateTime`**: The estimated completion time is calculated using:
- **Metrics-based estimation**: Uses historical median durations from previous cache updates (more accurate, hardware-adaptive)
- **Calculated estimation**: Falls back to calculated estimates based on configuration when no metrics are available yet
- Estimates improve in real-time as the update progresses through phases (Initializing → CapturingFrames → Saving)

#### Delete Cache

Delete cached data for a location.

```http
DELETE /api/cache/{suburb}/{state}
```

**Response:**
```json
{
  "message": "Cache deleted for Brisbane, QLD"
}
```

Returns `409 Conflict` when a capture is queued or running for the location. Only recognised cache folders are deleted; unknown directories and outside sentinel files are left untouched.

### Retention exception

Ordinary retention removes complete snapshots older than `CacheRetentionHours`. The newest complete snapshot per location is always preserved as a stale fallback, even when it is older than the retention window, and active captures are skipped. Unknown directories are never deleted.

## Cache Update Estimation

The service uses a **metrics-based estimation system** to provide accurate estimates of cache update completion times. This ensures clients receive meaningful `nextUpdateTime` values that adapt to the actual hardware performance.

### How It Works

1. **Progress Tracking**: During cache updates, the service tracks progress through three phases:
   - **Initializing**: Browser setup, navigation, and page loading (~0-20% of total time)
   - **CapturingFrames**: Frame capture loop (~20-95% of total time)
   - **Saving**: Metadata and cleanup operations (~95-100% of total time)

2. **Metrics Collection**: After each successful cache update, the service records:
   - Total duration of the update
   - Duration of each phase (Initializing, CapturingFrames, Saving)
   - Duration of each individual scraping step (NavigateHomepage, ClickSearchButton, etc.)
   - Frame-level progress during capture

3. **Estimation Strategy**:
   - **Metrics-based** (preferred): Uses median durations from the last 20 completed updates to provide hardware-adaptive estimates
   - **Progress-aware**: During active updates, estimates improve in real-time based on current phase and frame progress
   - **Calculated fallback**: When no metrics are available (e.g., first update), falls back to calculated estimates based on configuration values

4. **Benefits**:
   - **Hardware-adaptive**: Estimates automatically adjust to slower/faster hardware
   - **Improves over time**: More accurate estimates as more updates complete
   - **Real-time refinement**: Estimates become more precise as updates progress
   - **Works from clean start**: Provides reasonable estimates even on first run

### Example Scenarios

**First Update (No Metrics)**:
- Uses calculated estimate based on `Screenshot:DynamicContentWaitMs`, `Screenshot:TileRenderWaitMs`, and frame count
- Example: ~100 seconds for 7 frames with default settings (optimized wait times)

**Subsequent Updates (With Metrics)**:
- Uses median duration from historical data
- Example: If previous updates averaged 95 seconds, estimates will use ~95 seconds (with buffer)

**In-Progress Update**:
- If capturing frame 3 of 7, estimates remaining time based on:
  - Average frame duration from historical data
  - Remaining frames (4 frames × avg frame duration)
  - Plus estimated time for saving phase

## Demo SPA

The service includes a built-in Single Page Application (SPA) for testing and demonstration purposes. This provides a visual interface to:

- View radar frames in a slideshow
- Test API endpoints
- Configure playback settings
- View historical data across extended time periods
- Monitor cache status and update information

### Accessing the Demo

Navigate to:
```
http://localhost:8082/radar/{suburb}/{state}
```

**Example:**
```
http://localhost:8082/radar/Brisbane/QLD
```

### Features

- **Slideshow Playback**: Play, pause, and navigate through radar frames
- **Frame Navigation**: Use slider, buttons, or keyboard shortcuts (arrow keys, spacebar)
- **Extended Timespans**: View historical data with configurable time ranges (based on cache retention settings)
- **Custom Time Ranges**: Select specific start and end times for historical viewing
- **Auto-Refresh**: Automatically checks for new data at configurable intervals (minimum 5 seconds, no maximum)
- **Cache Status**: Real-time display of cache validity, expiration, and update status
- **Settings Panel**: Configure frame intervals (minimum 0.1 seconds, no maximum), refresh rates, and playback options

### Keyboard Shortcuts

- `←` / `→`: Navigate to previous/next frame
- `Shift + ←` / `Shift + →`: Jump back/forward 10 frames
- `Home` / `End`: Jump to first/last frame
- `Space`: Play/pause slideshow

### Using the Demo for Integration Development

The demo SPA serves as a reference implementation demonstrating best practices for consuming the API. Key implementation patterns:

**Basic Radar Data Fetching**: The simplest pattern - fetch data and handle the case where cache is being generated:

```javascript
async function getRadarData(suburb, state) {
    const response = await fetch(`http://localhost:8082/api/radar/${suburb}/${state}`);
    
    if (response.status === 404) {
        // Cache is being generated - trigger refresh and show message
        const error = await response.json();
        
        // Use standardized error response format
        if (error.errorCode === 'CACHE_NOT_FOUND' && error.suggestions?.refreshEndpoint) {
            // Trigger cache update in background
            fetch(error.suggestions.refreshEndpoint, { method: 'POST' }).catch(() => {});
        }
        
        const retryAfter = error.suggestions?.retryAfter || 30;
        return { 
            frames: [], 
            message: error.message || `Cache being generated. Retry in ${retryAfter} seconds.` 
        };
    }
    
    if (!response.ok) {
        const error = await response.json().catch(() => ({ message: `HTTP ${response.status}` }));
        throw new Error(error.message || `HTTP ${response.status}`);
    }
    
    return await response.json();
}
```

**Displaying Frames**: Frame objects include ready-to-use `imageUrl` properties:

```javascript
const radarData = await getRadarData('Brisbane', 'QLD');
if (radarData.frames && radarData.frames.length > 0) {
    // Display first frame
    document.getElementById('radar-image').src = radarData.frames[0].imageUrl;
    
    // Or loop through all frames for animation
    radarData.frames.forEach((frame, index) => {
        const minutesAgo = frame.absoluteObservationTime 
            ? Math.round((Date.now() - new Date(frame.absoluteObservationTime).getTime()) / 60000)
            : null;
        console.log(`Frame ${index}: ${frame.imageUrl}${minutesAgo !== null ? ` (${minutesAgo} min ago)` : ''}`);
    });
}
```

**Historical Data**: Fetch extended time series by specifying a time range:

```javascript
async function getHistoricalRadar(suburb, state, hoursBack = 3) {
    const endTime = new Date();
    const startTime = new Date(endTime.getTime() - (hoursBack * 60 * 60 * 1000));
    
    const response = await fetch(
        `/api/radar/${suburb}/${state}/timeseries?startTime=${startTime.toISOString()}&endTime=${endTime.toISOString()}`
    );
    
    if (response.status === 400) {
        const error = await response.json();
        // Use standardized error format
        throw new Error(error.message || 'Invalid time range request');
    }
    
    if (response.status === 404) {
        const error = await response.json();
        
        // Check error code to determine type
        if (error.errorCode === 'CACHE_NOT_FOUND') {
            // Location doesn't exist - trigger cache update if endpoint provided
            if (error.suggestions?.refreshEndpoint) {
                fetch(error.suggestions.refreshEndpoint, { method: 'POST' }).catch(() => {});
            }
            throw new Error(error.message || 'Cache update triggered, please retry in a few moments.');
        }
        
        // Cache exists but no data in range
        if (error.errorCode === 'TIME_RANGE_ERROR' && error.details?.availableRange) {
            const range = error.details.availableRange;
            const rangeMsg = range.oldest && range.newest 
                ? ` Available data: ${new Date(range.oldest).toLocaleString()} to ${new Date(range.newest).toLocaleString()}.`
                : '';
            throw new Error(error.message + rangeMsg);
        }
        
        throw new Error(error.message || 'No historical data found');
    }
    
    if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
    }
    
    const data = await response.json();
    // Flatten frames from all cache folders
    const allFrames = data.cacheFolders.flatMap(folder => folder.frames);
    return allFrames;
}
```

**Auto-Refresh with Error Handling**: Implement periodic updates that handle API downtime:

```javascript
let refreshInterval;

function startAutoRefresh(suburb, state, intervalSeconds = 30) {
    refreshInterval = setInterval(async () => {
        try {
            const data = await getRadarData(suburb, state);
            if (data.frames && data.frames.length > 0) {
                updateDisplay(data);
            }
        } catch (error) {
            console.error('Failed to refresh:', error);
            // Optionally stop auto-refresh on persistent errors
        }
    }, intervalSeconds * 1000);
}

// Start auto-refresh
startAutoRefresh('Brisbane', 'QLD', 30);

// Stop auto-refresh
if (refreshInterval) {
    clearInterval(refreshInterval);
}
```

The complete implementation with all patterns is available in `Views/RadarTest/Index.cshtml` for reference.

## Usage Examples

### Home Assistant Integration

To use this service with Home Assistant, you'll need to create a custom integration or use a REST sensor. Here's a basic example:

```yaml
# configuration.yaml
rest:
  - sensor:
      name: "BOM Radar Brisbane"
      resource: "http://localhost:8082/api/radar/Brisbane/QLD"
      value_template: "{{ value_json.frames[0].imageUrl }}"
      scan_interval: 300
```

### cURL Examples

**Get latest radar data:**
```bash
curl http://localhost:8082/api/radar/Brisbane/QLD
```

**Get specific frame image:**
```bash
curl http://localhost:8082/api/radar/Brisbane/QLD/frame/0 -o frame.png
```

**Trigger cache refresh:**
```bash
curl -X POST http://localhost:8082/api/cache/Brisbane/QLD/refresh
```

**Get historical data (last 3 hours):**
```bash
curl "http://localhost:8082/api/radar/Brisbane/QLD/timeseries?startTime=2025-01-15T07:00:00Z&endTime=2025-01-15T10:00:00Z"
```

**Get historical data with error handling:**
```bash
# Check response status
response=$(curl -s -w "\n%{http_code}" "http://localhost:8082/api/radar/Brisbane/QLD/timeseries?startTime=2025-01-15T07:00:00Z&endTime=2025-01-15T10:00:00Z")
http_code=$(echo "$response" | tail -n1)
body=$(echo "$response" | sed '$d')

if [ "$http_code" = "200" ]; then
    echo "Success: $body"
elif [ "$http_code" = "400" ]; then
    echo "Bad Request: $body"
elif [ "$http_code" = "404" ]; then
    echo "Not Found: $body"
    # Check if cache update was triggered
    if echo "$body" | grep -q "updateTriggered"; then
        echo "Cache update triggered, retry in a few moments"
    fi
else
    echo "Error ($http_code): $body"
fi
```

## Troubleshooting

### Service Won't Start

- **Check Docker logs**: `docker logs bom-local-service`
- **Verify port availability**: Ensure port 8082 (or your configured port) is not in use
- **Shared memory configuration**: The docker-compose.yml includes `shm_size: '1gb'` and `ipc: host` which are required for Playwright. If running with `docker run`, ensure you include:
  ```bash
  docker run --shm-size=1gb --ipc=host ...
  ```
- **Browser launch failures**: If Playwright browsers fail to launch, check for seccomp security restrictions. You may need to add `--security-opt seccomp:unconfined` (though this is generally not needed with the included configuration)

### No Radar Data Available

- **Wait for initial cache**: First request triggers cache generation, which takes 30-60 seconds
- **Check location format**: Ensure suburb and state are correctly formatted (e.g., "Brisbane", "QLD")
- **Verify cache directory**: Check that the cache volume is mounted and writable
- **Check logs**: Look for errors in browser automation or data capture

### Images Not Loading

- **Verify frame URLs**: Check that frame image URLs are correctly formatted
- **Check cache status**: Use `/api/cache/{suburb}/{state}/range` to verify cache exists
- **Browser automation issues**: Check logs for Playwright errors

### Scraping Failures

If scraping fails (e.g., "Could not find element"), the BOM website structure may have changed:

- **Check debug screenshots**: Enable `DEBUG__ENABLED=true` to see what the browser sees at each step
- **Update selectors**: 
  - **Docker**: Mount a custom `appsettings.json` with updated selectors (see [Scraping Configuration](#scraping-configuration))
  - **Local**: Edit `appsettings.json` under `Scraping:Selectors` to add new CSS selectors as fallbacks
- **Check step logs**: Each step logs its execution - look for which step failed
- **Selector fallbacks**: The system tries multiple selectors in order, so add new selectors to the existing arrays
- **Workflow steps**: Individual steps can be disabled via `Scraping:Workflows:RadarScraping:Steps:{StepName}:Enabled: false` if needed temporarily

**Quick Fix for Docker Users:**

1. Copy the default `appsettings.json` from the repository
2. Edit the selectors that are failing
3. Mount it as a volume: `-v $(pwd)/appsettings.json:/app/appsettings.json:ro`
4. Restart the container

### Playwright Resource Usage

Playwright browsers (Chromium) can consume significant CPU and memory:

**Limit CPU Cores**: Add CPU limits to your docker-compose.yml:
```yaml
services:
  bom-local-service:
    deploy:
      resources:
        limits:
          cpus: '1.0'  # Limit to 1 CPU core
        reservations:
          cpus: '0.5'  # Reserve 0.5 cores
```

Or with `docker run`:
```bash
docker run --cpus="1.0" ...
```

**Common Playwright + Docker Issues**:

- **Browser crashes with "out of memory"**: Ensure `shm_size: '1gb'` is set. Chromium uses `/dev/shm` for shared memory, and the default 64MB is insufficient.
- **High CPU usage during idle**: This is normal - Playwright browsers can consume CPU even when idle. Consider reducing `CACHEMANAGEMENT__CHECKINTERVALMINUTES` to check less frequently.
- **Browser processes not terminating**: Check logs for stuck browser processes. The service includes cleanup logic, but you may need to restart the container if processes hang.
- **"Protocol error" or connection failures**: Usually indicates insufficient shared memory or IPC namespace issues. Verify `ipc: host` is set in docker-compose.yml.

**Optimize Cache Settings**:
- **Reduce retention**: Lower `CACHERETENTIONHOURS` to keep less data on disk
- **Increase cleanup frequency**: Lower `CACHECLEANUP__INTERVALHOURS` to clean up more often
- **Limit locations**: The service automatically manages all cached locations; reduce the number of locations being cached to lower resource usage

### Performance Monitoring

The service logs detailed performance metrics for each scraping workflow:

**Step-Level Timing**:
- Each step logs its duration and compares it to historical averages
- Example: `Step WaitForMapReady completed in 45.30s (avg: 43.76s)`
- Steps that are >50% slower than average trigger warnings: `⚠️ Step WaitForMapReady took significantly longer than average: 75.45s (avg: 50.30s, +25.15s, +50.0% slower)`

**Workflow-Level Timing**:
- Complete workflow duration is logged with step breakdown
- Example: `Workflow RadarScraping completed in 144.40s. Step breakdown: NavigateHomepage=4.96s, ClickSearchButton=2.70s, ...`
- Workflows that are >30% slower than average trigger warnings: `⚠️ Workflow RadarScraping took significantly longer than average: 189.45s (avg: 145.67s, +43.78s, +30.0% slower)`

**Metrics Storage**:
- Step and phase durations are stored in memory (last 20 samples)
- Used for performance estimation and identifying bottlenecks
- Metrics improve over time as more updates complete

## Development

### Building Locally

Requires the .NET 10 SDK selected by `global.json`.

```bash
dotnet restore --locked-mode
dotnet restore tests/BomLocalService.Tests/BomLocalService.Tests.csproj --locked-mode
dotnet build --configuration Release
dotnet test tests/BomLocalService.Tests/BomLocalService.Tests.csproj --configuration Release --filter 'Category!=Browser'
```

The `packages.lock.json` files make restores reproducible. Tests use real HTTP controllers, cache lookup, serialization, historical selection and a controllable capture boundary. Live BOM checks are separate from required PR checks.

Browser tests require an owned disposable service and an explicit
`BOM_DEMO_BASE_URL`. The dedicated browser run fails when prerequisites are
missing; deterministic runs explicitly exclude `Category=Browser`. Browser
scenarios cover recovery, stale failure details, history ordering, shorter/empty
collections and polling ownership. Six demo cases and four production Chromium lifecycle cases have executed successfully across the initial and focused runs.

The dedicated-unit runner passed independent source review, 66 pure checks and
11 native/container lifecycle fixtures. Use `scripts/run-service-test.sh` for
service and browser work; it bounds lifetime and verifies teardown. It requires
a systemd user manager and cgroup v2; long runs also require linger. A present
`artifacts/execution/runner/SAFETY_HOLD` blocks execution. See the
[qualification register](docs/service-qualification.md).

### Verification and soak checks

```bash
./scripts/verify-service.sh \
  --base-url http://127.0.0.1:18082 \
  --suburb Pomona \
  --state QLD \
  --output artifacts/verification

./scripts/soak-service.sh \
  --base-url http://127.0.0.1:18082 \
  --duration-hours 30 \
  --output artifacts/soak \
  --container bom-local-service
```

These entry points validate an already-running test instance; its owning runner
must supervise its lifetime and teardown. Qualification also requires the frozen
candidate manifest and policy, actual inspected image identity, and reports tied
to those inputs. The verifier and soak enforce complete image
validation, freshness, exact ownership, bounded requests, monotonic progression
and honest duration/exit accounting; 50 helper and simulated-loop checks pass. Short or interrupted runs cannot qualify.
Their current source is not yet qualified by end-to-end execution.

### Candidate validation and limitations

Evidence available as of 2026-11-15:

- Service implementation commit `18f565599f814215e42f5ee47a79294834229e4d` is
  SSH-signed and verified locally. This is an implementation checkpoint, not the
  final frozen candidate.
- Locked restores, Release build/publish and published-content audit pass.
  All 124 deterministic cases passed across an initial run (122 passed) and
  focused reruns after repairing an invalid synthetic PNG fixture and a test
  that observed failure before cleanup completed.
- All ten Chromium/demo cases executed: eight initially passed, then two demo
  assertions were corrected to respect disabled navigation and relative-time
  display. Focused reruns passed. The production cases exercise concurrent
  context leases, cancellation, abrupt Chromium crash and recreation.
- The dependency vulnerability audit completed and reported no vulnerable
  packages using the configured sources.
- The replacement runner has 66 pure checks and 11 live native/container
  fixtures. Success, failure, interruption, abrupt supervisor loss and outer
  timeout have verified cleanup evidence. No inherited process group or cgroup
  is treated as task-owned.
- Verifier/soak helper checks pass, including simulated 30-hour completion.
  Simulated time is not a real trial. Earlier Pomona/two-location reports and
  the interrupted two-minute soak do not qualify the current candidate.

Native amd64 image qualification, fresh two-location checks, fault/restart/rollback
exercises and the full 30-hour trial remain outstanding. No candidate image has
been frozen or published. ARM64 testing is explicitly deferred. Card/HA
implementation remains deferred; its [handoff](docs/ha-handoff/README.md) is
unchanged by this service work.

### Release and rollback

The candidate workflow is intended to build one image per architecture,
smoke-test it on a native runner, publish the tested images and combine them
into a multi-architecture index. That process has not yet qualified or published
v0.1.1. Before release, record the final tested commit, per-architecture evidence
and image digests in the release notes.

To roll back, deploy the previously recorded digest explicitly:

```bash
docker pull ghcr.io/alexhopeoconnor/bom-local-service@sha256:<previous-digest>
docker run -d --name bom-local-service \
  -p 8082:8080 -v $(pwd)/cache:/app/cache \
  --shm-size=1gb --ipc=host \
  ghcr.io/alexhopeoconnor/bom-local-service@sha256:<previous-digest>
```

Automated fixtures cover reading legacy cache folders without renaming. A
rollback of the final candidate has not been qualified; verify it against a copy
of the cache before relying on the same volume across versions.

### Debug Mode

Enable debug mode to save intermediate screenshots during data capture:

```bash
docker run -e DEBUG__ENABLED=true -e DEBUG__WAITMS=5000 ...
```

Debug screenshots are saved in `{CACHEDIRECTORY}/debug/`. Each scraping step saves a screenshot, HTML snapshot, and logs, making it easy to diagnose issues.

### Extending the Scraping System

The workflow-based architecture makes it easy to extend the scraping system:

**Adding a New Workflow**:

1. Create a new workflow class in `Services/Scraping/Workflows/` implementing `IWorkflow<TResponse>` where `TResponse` is your response type
2. Define the step sequence (can reuse existing steps)
3. Register the workflow in `WorkflowFactory`
4. Add workflow configuration to `appsettings.json`

**Adding a New Step**:

1. Create a step class inheriting from `BaseScrapingStep`
2. Implement `Name`, `Prerequisites`, `CanExecute`, and `ExecuteAsync`
3. The step will be auto-registered on startup
4. Add the step to a workflow's `StepNames` array

**Updating Selectors**:

1. Edit `appsettings.json` under `Scraping:Selectors`
2. Add new CSS selectors to the `Selectors` array (tried in order)
3. Adjust `TimeoutMs` if needed
4. No code changes required

**Updating JavaScript Templates**:

1. Edit `appsettings.json` under `Scraping:JavaScriptTemplates`
2. Update the template code as needed
3. No code changes required

**Updating Text Patterns**:

1. Edit `appsettings.json` under `Scraping:TextPatterns`
2. Update regex patterns as needed (e.g., `TimestampPattern`)
3. The `TimestampPattern` supports parsing timestamps like "Wednesday 17 Dec, 11:05 pm" when the BOM website changes format
4. No code changes required

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Contributing

This is a hobby project, but contributions are welcome! Feel free to open issues or submit pull requests.

## Acknowledgments

- Inspired by the [bom-radar-card](https://github.com/Makin-Things/bom-radar-card) project
- Built with [Playwright](https://playwright.dev/) for browser automation
- Uses [ASP.NET Core](https://dotnet.microsoft.com/) for the web framework

## Disclaimer

This project is intended for **local, personal use only**. The radar data and images cached by this service are the property of the Australian Bureau of Meteorology (BOM) and are subject to copyright. 

**Important Notes:**
- This service is designed to run on your local network for personal use
- Do not redistribute or republish BOM radar data or images
- Respect BOM's terms of service and data usage policies
- The service caches publicly available data for local consumption only
- This project is not affiliated with or endorsed by the Australian Bureau of Meteorology

For official BOM data and services, visit [bom.gov.au](https://www.bom.gov.au/).
