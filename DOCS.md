# BOM Local Service - Home Assistant Add-on

A local caching service for Australian Bureau of Meteorology (BOM) radar data, providing a reliable API for Home Assistant integrations and other local services.

## About

The Australian Bureau of Meteorology's radar API endpoint stopped working in December 2024, breaking integrations like the popular [bom-radar-card](https://github.com/Makin-Things/bom-radar-card) for Home Assistant. This add-on bridges that gap by:

- Caching radar data from the BOM website
- Storing it locally in a structured format
- Providing a simple REST API for local services to consume
- Automatically managing cache updates and cleanup

## Installation

1. **Add this repository to Home Assistant:**
   - Navigate to **Settings** → **Add-ons** → **Add-on Store**
   - Click the three dots (⋮) in the top right corner
   - Select **Repositories**
   - Add this URL: `https://github.com/alexhopeoconnor/bom-local-service`
   - Click **Add**

2. **Install the add-on:**
   - Find "BOM Local Service" in the add-on store
   - Click on it and click **Install**
   - Wait for the installation to complete

3. **Configure the add-on:**
   - Go to the **Configuration** tab
   - Adjust settings as needed (see Configuration section below)
   - Click **Save**

4. **Start the add-on:**
   - Go to the **Info** tab
   - Click **Start**
   - Enable **Start on boot** if you want it to start automatically

## Configuration

The add-on can be configured through the Home Assistant UI. Available options:

### Cache Settings

| Option | Description | Default |
|--------|-------------|---------|
| `cache_retention_hours` | Hours to retain cached data before cleanup (1-168) | `24` |
| `cache_expiration_minutes` | Minutes before cache is considered expired | `12.5` |
| `cache_check_interval_minutes` | Interval between cache validity checks (1-60) | `5` |
| `cache_cleanup_interval_hours` | Interval between cleanup runs (1-24) | `1` |

### General Settings

| Option | Description | Default |
|--------|-------------|---------|
| `timezone` | Timezone for time parsing (IANA format) | `Australia/Brisbane` |
| `debug_enabled` | Enable debug mode (saves debug screenshots) | `false` |

### Example Configuration

```yaml
cache_retention_hours: 48
cache_expiration_minutes: 15
timezone: "Australia/Sydney"
cache_check_interval_minutes: 10
cache_cleanup_interval_hours: 2
debug_enabled: false
```

## Usage

Once the add-on is running, the API will be available at `http://homeassistant.local:8082` (or your Home Assistant IP address).

### API Endpoints

#### Get Radar Data

Get the latest radar frames for a location:

```
GET http://homeassistant.local:8082/api/radar/{suburb}/{state}
```

**Example:**
```
GET http://homeassistant.local:8082/api/radar/Brisbane/QLD
```

**Response:**
```json
{
  "frames": [
    {
      "frameIndex": 0,
      "imageUrl": "/api/radar/Brisbane/QLD/frame/0",
      "absoluteObservationTime": "2025-01-15T10:00:00Z"
    }
  ],
  "observationTime": "2025-01-15T10:00:00Z",
  "weatherStation": "Brisbane",
  "cacheIsValid": true
}
```

#### Get Frame Image

Get a specific radar frame image:

```
GET http://homeassistant.local:8082/api/radar/{suburb}/{state}/frame/{frameIndex}
```

**Example:**
```
GET http://homeassistant.local:8082/api/radar/Brisbane/QLD/frame/0
```

Returns a PNG image.

#### Get Time Series

Get historical radar data:

```
GET http://homeassistant.local:8082/api/radar/{suburb}/{state}/timeseries?startTime={iso8601}&endTime={iso8601}
```

**Example:**
```
GET http://homeassistant.local:8082/api/radar/Brisbane/QLD/timeseries?startTime=2025-01-15T00:00:00Z&endTime=2025-01-15T12:00:00Z
```

### Integration with BOM Radar Card

To use with the [bom-radar-card](https://github.com/Makin-Things/bom-radar-card):

1. Install the BOM Radar Card in HACS
2. Configure it to use your local API endpoint:

```yaml
type: custom:bom-radar-card
suburb: Brisbane
state: QLD
api_endpoint: "http://homeassistant.local:8082"
```

## Data Storage

Cache data is stored in `/share/bom-cache` within Home Assistant's shared storage. This ensures:
- Data persists across add-on restarts
- Data is included in Home Assistant backups
- You can access the cache from other add-ons if needed

## Support

For issues, feature requests, or questions:
- GitHub Issues: https://github.com/alexhopeoconnor/bom-local-service/issues
- Full Documentation: https://github.com/alexhopeoconnor/bom-local-service

## License

MIT License - see the [LICENSE](https://github.com/alexhopeoconnor/bom-local-service/blob/main/LICENSE) file for details.
