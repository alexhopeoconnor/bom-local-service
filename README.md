# BOM Local Service

A local caching service for Australian Bureau of Meteorology (BOM) radar data, designed to provide a reliable API for Home Assistant integrations and other local services.

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

Built on ASP.NET Core 9.0, the service uses a service-oriented architecture with clear separation of concerns:

### Core Services
- **BomRadarService**: Main orchestrator that coordinates cache operations, browser automation, and data retrieval
- **CacheService**: Manages file-based storage of radar screenshots and metadata in organized directory structures
- **BrowserService**: Handles Playwright browser automation for headless browser sessions
- **ScrapingService**: Performs web scraping operations using the browser service to navigate and capture radar data from the BOM website
- **TimeParsingService**: Parses and converts time formats from BOM data
- **DebugService**: Provides debug functionality for troubleshooting

### Background Services
- **CacheManagementService**: Periodically checks cache validity for all cached locations and triggers updates when data expires
- **CacheCleanupService**: Removes cache files older than the configured retention period

### API Layer
- **RadarController**: REST endpoints for accessing radar data (`/api/radar/{suburb}/{state}`)
- **CacheController**: REST endpoints for cache management operations (`/api/cache/{suburb}/{state}`)
- **RadarTestController**: MVC controller serving the demo SPA at `/radar/{suburb}/{state}`

## Installation

### Docker Image

Pre-built Docker images are available on GitHub Container Registry:

**Image:** `ghcr.io/alexhopeoconnor/bom-local-service`

**Multi-architecture support:** Images are built for both `linux/amd64` and `linux/arm64` platforms

**Pull the latest version:**
```bash
docker pull ghcr.io/alexhopeoconnor/bom-local-service:latest
```

**Pull a specific version:**
```bash
docker pull ghcr.io/alexhopeoconnor/bom-local-service:v0.0.1
```

See all available versions on the [releases page](https://github.com/alexhopeoconnor/bom-local-service/releases).

### Prerequisites

- Docker Engine 20.10+ or Docker Desktop
- Docker Compose v2.0+ (optional, for easier management)

### Quick Start

#### Option 1: Using Pre-built Docker Image (Recommended)

Pull the latest image from GitHub Container Registry:

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

**Available tags:**
- `latest` - Latest release
- `v0.0.1` - Specific version (see [releases](https://github.com/alexhopeoconnor/bom-local-service/releases) for all versions)

#### Option 2: Build from Source

1. **Clone the repository**:
   ```bash
   git clone https://github.com/alexhopeoconnor/bom-local-service.git
   cd bom-local-service
   ```

2. **Build the Docker image**:
   ```bash
   docker build -t bom-local-service .
   ```

   The build process uses a multi-stage Dockerfile:
   - **Build stage**: Compiles the .NET application
   - **Runtime stage**: Uses the official Playwright .NET image with browsers pre-installed
   - Sets up a virtual display (Xvfb) for headless browser operation

3. **Run the container**:
   ```bash
   docker run -d \
     --name bom-local-service \
     -p 8082:8080 \
     -v $(pwd)/cache:/app/cache \
     --shm-size=1gb \
     --ipc=host \
     bom-local-service
   ```

   **Note**: The `--shm-size=1gb` and `--ipc=host` flags are important for Playwright to function correctly in Docker.

### Using Docker Compose

The included `docker-compose.yml` provides a convenient way to run the service with all configuration options.

**Using pre-built image (recommended):**

Update `docker-compose.yml` to use the GitHub Container Registry image:
```yaml
services:
  bom-local-service:
    image: ghcr.io/alexhopeoconnor/bom-local-service:latest
    # Remove or comment out the 'build:' section
```

Then run:
```bash
docker-compose up -d
```

**Building from source:**

If you want to build locally, keep the `build:` section in `docker-compose.yml` and run:
```bash
docker-compose up -d
```

This will:
- Pull/build the image as configured
- Start the service on port 8082 (configurable via `HOST_PORT`)
- Mount the `./cache` directory for persistent storage
- Apply all environment variable configurations

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
| `ENABLE_HTTPS_REDIRECTION` | Enable HTTPS redirection | `false` |

#### Application Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CACHEDIRECTORY` | Directory path for cache storage | `/app/cache` | `/data/bom-cache` |
| `CACHERETENTIONHOURS` | Hours to retain cached data before cleanup | `24` | `48` |
| `CACHEEXPIRATIONMINUTES` | Minutes before cache is considered expired | `12.5` | `15` |
| `TIMEZONE` | Timezone for time parsing (IANA format) | `Australia/Brisbane` | `Australia/Sydney` |

#### Cache Management

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CACHEMANAGEMENT__CHECKINTERVALMINUTES` | Interval between cache validity checks | `5` | `10` |
| `CACHEMANAGEMENT__INITIALDELAYSECONDS` | Delay before first cache check on startup | `10` | `30` |
| `CACHEMANAGEMENT__UPDATESTAGGERSECONDS` | Delay between triggering cache updates | `2` | `5` |
| `CACHEMANAGEMENT__LOCATIONSTAGGERSECONDS` | Delay between processing different locations | `1` | `2` |

#### Cache Cleanup

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `CACHECLEANUP__INTERVALHOURS` | Interval between cleanup runs | `1` | `2` |

#### Screenshot Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `SCREENSHOT__DYNAMICCONTENTWAITMS` | Milliseconds to wait for dynamic content to load | `2000` | `3000` |
| `SCREENSHOT__TILERENDERWAITMS` | Milliseconds to wait for map tiles to render | `5000` | `7000` |

#### Debug Configuration

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `DEBUG__ENABLED` | Enable debug mode (saves debug screenshots) | `false` | `true` |
| `DEBUG__WAITMS` | Additional wait time in debug mode | `2000` | `5000` |

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

## API Documentation

The service provides RESTful API endpoints for accessing radar data and managing the cache.

### Base URL

When running locally with default settings: `http://localhost:8082`

### Endpoints

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
      "imageUrl": "/api/radar/Brisbane/QLD/frame/0",
      "minutesAgo": 0,
      "observationTime": "2025-01-15T10:00:00Z"
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

**Status Codes:**
- `200 OK`: Radar data available
- `404 Not Found`: Cache is being generated (check response for retry information)
- `400 Bad Request`: Invalid location parameters

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

**Response:**
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
          "absoluteObservationTime": "2025-01-15T10:00:00Z",
          "minutesAgo": 0
        }
      ]
    }
  ],
  "totalFrames": 7,
  "timeSpanMinutes": 60
}
```

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

### OpenAPI Documentation

When running in Development mode, OpenAPI documentation is available at:
```
http://localhost:8082/openapi/v1.json
```

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
- **Extended Timespans**: View historical data from 1 hour to 24 hours
- **Custom Time Ranges**: Select specific start and end times for historical viewing
- **Auto-Refresh**: Automatically checks for new data at configurable intervals
- **Cache Status**: Real-time display of cache validity, expiration, and update status
- **Settings Panel**: Configure frame intervals, refresh rates, and playback options

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
        if (error.refreshEndpoint) {
            // Trigger cache update in background
            fetch(error.refreshEndpoint, { method: 'POST' }).catch(() => {});
        }
        return { frames: [], message: `Cache being generated. Retry in ${error.retryAfter} seconds.` };
    }
    
    if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
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
        console.log(`Frame ${index}: ${frame.imageUrl} (${frame.minutesAgo} min ago)`);
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
curl "http://localhost:8082/api/radar/Brisbane/QLD/timeseries?startTime=2025-01-15T07:00:00Z"
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

## Development

### Building Locally

```bash
dotnet restore
dotnet build
dotnet run
```

### Debug Mode

Enable debug mode to save intermediate screenshots during data capture:

```bash
docker run -e DEBUG__ENABLED=true -e DEBUG__WAITMS=5000 ...
```

Debug screenshots are saved in `{CACHEDIRECTORY}/debug/`.

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
