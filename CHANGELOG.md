# Changelog

All notable changes to this add-on will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-01-22

### Added
- Initial Home Assistant add-on release
- Configuration options for cache retention, expiration, and timezone
- Support for amd64, aarch64, and armv7 architectures
- REST API endpoints for radar data access
- Automatic cache management and cleanup
- Time series support for historical radar data
- Integration with existing GitHub Container Registry images

### Notes
- This is the first official release as a Home Assistant add-on
- The underlying service has been tested and used as a standalone Docker container
- Cache data is stored in `/share/bom-cache` for persistence across restarts
