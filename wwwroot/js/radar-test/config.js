// Configuration constants
export const MAX_RETRY_ATTEMPTS = 10;
export const RETRY_DELAY_MS = 5000; // base delay; retries back off exponentially
export const MAX_RETRY_DELAY_MS = 30000; // cap so retries keep probing a recovering service

// Get API base URL from global variable set by Razor view
export const API_BASE = window.API_BASE || '';

// Default settings
export const DEFAULT_SETTINGS = {
    frameInterval: 2.0,      // seconds between frames
    refreshInterval: 30,     // seconds between API refreshes
    autoPlay: true,          // auto-play on load
    timespan: 'latest'       // 'latest', '1h', '3h', '6h', '12h', '24h', 'custom'
};

