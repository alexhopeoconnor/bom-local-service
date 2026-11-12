// API client for fetching radar data
import { API_BASE } from './config.js';
import { formatDate, isNetworkError, isNetworkErrorResponse, createCacheStatusData, triggerBackgroundRefresh } from './utils.js';
import { state } from './state.js';

/**
 * Fetch cache range information
 */
export async function fetchCacheRange() {
    try {
        const response = await fetch(`${API_BASE.replace('/api/radar', '/api/cache')}/range`, {
            signal: AbortSignal.timeout(10000)
        });
        
        if (!response.ok) {
            if (isNetworkErrorResponse(response)) {
                throw new Error('Network error');
            }
            console.warn('Failed to fetch cache range');
            return { data: null, error: null };
        }
        
        const data = await response.json();
        state.cacheRangeInfo = data;
        
        // Update UI
        const rangeTextEl = document.getElementById('cache-range-text');
        if (rangeTextEl) {
            if (data.totalCacheFolders > 0) {
                const spanHours = data.timeSpanMinutes ? Math.round(data.timeSpanMinutes / 60 * 10) / 10 : 0;
                rangeTextEl.innerHTML = `
                    <div>Oldest: ${formatDate(data.oldestCache.cacheTimestamp)}</div>
                    <div>Newest: ${formatDate(data.newestCache.cacheTimestamp)}</div>
                    <div>Total: ${data.totalCacheFolders} cache folders (${spanHours} hours)</div>
                `;
            } else {
                rangeTextEl.textContent = 'No cache data available';
            }
        }
        
        return { data: data, error: null };
    } catch (error) {
        console.error('Error fetching cache range:', error);
        const rangeTextEl = document.getElementById('cache-range-text');
        if (rangeTextEl) {
            rangeTextEl.textContent = 'Unable to load cache range';
        }
        
        if (isNetworkError(error)) {
            return { data: null, error: 'network' };
        }
        return { data: null, error: null };
    }
}

/**
 * Fetch historical radar data for extended timespan
 */
export async function fetchHistoricalRadar(startTime, endTime) {
    try {
        let url = `${API_BASE}/timeseries`;
        const params = new URLSearchParams();
        if (startTime) params.append('startTime', startTime.toISOString());
        if (endTime) params.append('endTime', endTime.toISOString());
        if (params.toString()) url += '?' + params.toString();
        
        const response = await fetch(url, {
            signal: AbortSignal.timeout(30000) // Longer timeout for potentially large responses
        });
        
        if (!response.ok) {
            const errorData = await response.json().catch(() => ({ message: `HTTP ${response.status}` }));
            
            // Handle 400 Bad Request
            if (response.status === 400) {
                const errorMessage = errorData.message || 'Invalid request';
                let details = '';
                if (errorData.errorCode === 'TIME_RANGE_ERROR' && errorData.details?.requestedRange) {
                    const requested = errorData.details.requestedRange;
                    if (requested.requestedHours && errorData.details.maxHours) {
                        details = ` Requested: ${requested.requestedHours.toFixed(1)} hours, Maximum: ${errorData.details.maxHours} hours.`;
                    }
                }
                throw new Error(errorMessage + details);
            }
            
            // Handle 404 Not Found
            if (response.status === 404) {
                if (errorData.errorCode === 'CACHE_NOT_FOUND') {
                    triggerBackgroundRefresh(errorData.suggestions?.refreshEndpoint);
                    return { 
                        frames: null, 
                        error: 'location_missing',
                        cacheStatus: errorData.details || errorData
                    };
                } else if (errorData.errorCode === 'TIME_RANGE_ERROR' && errorData.details?.availableRange) {
                    const availableRange = errorData.details.availableRange;
                    let rangeMessage = '';
                    
                    if (errorData.suggestions?.suggestedRange) {
                        const suggested = errorData.suggestions.suggestedRange;
                        rangeMessage = ` Suggested range: ${formatDate(suggested.start)} to ${formatDate(suggested.end)}.`;
                    } else if (availableRange.oldest && availableRange.newest) {
                        rangeMessage = ` Available data: ${formatDate(availableRange.oldest)} to ${formatDate(availableRange.newest)}.`;
                    } else if (availableRange.totalCacheFolders > 0) {
                        rangeMessage = ` ${availableRange.totalCacheFolders} cache folders available.`;
                    }
                    
                    if (errorData.suggestions?.suggestion) {
                        rangeMessage += ` ${errorData.suggestions.suggestion}`;
                    }
                    
                    throw new Error(errorData.message + rangeMessage);
                } else {
                    throw new Error(errorData.message || 'No historical data found');
                }
            }
            
            throw new Error(errorData.message || 'Failed to fetch historical radar');
        }
        
        const data = await response.json();
        state.historicalData = data;
        
        // Flatten all frames, preserve their origin snapshot, and sort explicitly.
        // Grouping by capture does not guarantee global order when captures overlap.
        const allFrames = data.cacheFolders
            .flatMap(cacheFolder => cacheFolder.frames.map(frame => ({
                ...frame,
                cacheTimestamp: cacheFolder.cacheTimestamp,
                observationTime: cacheFolder.observationTime,
                cacheFolderName: cacheFolder.cacheFolderName
            })))
            .sort((a, b) =>
                Date.parse(a.absoluteObservationTime) -
                Date.parse(b.absoluteObservationTime)
            );
        
        // Filter out frames without absoluteObservationTime
        const invalidFrames = allFrames.filter(frame => !frame.absoluteObservationTime);
        if (invalidFrames.length > 0) {
            console.error('Backend returned frames missing absoluteObservationTime:', {
                count: invalidFrames.length,
                frames: invalidFrames.map((f, idx) => ({
                    index: idx,
                    frameIndex: f.frameIndex,
                    cacheFolder: f.cacheFolderName,
                    imageUrl: f.imageUrl
                }))
            });
        }
        const validFrames = allFrames.filter(frame => frame.absoluteObservationTime);
        
        // Validate chronological order
        let previousTime = null;
        const outOfOrderFrames = [];
        validFrames.forEach((frame, idx) => {
            const currentTime = new Date(frame.absoluteObservationTime).getTime();
            if (previousTime !== null && currentTime < previousTime) {
                outOfOrderFrames.push({
                    index: idx,
                    frameIndex: frame.frameIndex,
                    cacheFolder: frame.cacheFolderName,
                    absoluteTime: frame.absoluteObservationTime,
                    previousTime: new Date(previousTime).toISOString(),
                    timeDiffMinutes: (currentTime - previousTime) / 1000 / 60
                });
            }
            previousTime = currentTime;
        });
        
        if (outOfOrderFrames.length > 0) {
            console.error('Backend returned frames out of chronological order:', {
                count: outOfOrderFrames.length,
                issues: outOfOrderFrames
            });
        }
        
        // Re-index frames sequentially for display
        validFrames.forEach((frame, idx) => {
            frame.sequentialIndex = idx;
        });
        
        return { frames: validFrames, error: null };
    } catch (error) {
        console.error('Error fetching historical radar:', error);
        if (isNetworkError(error)) {
            return { frames: null, error: 'network' };
        }
        return { frames: [], error: null };
    }
}

/**
 * Fetch metadata separately
 */
export async function fetchMetadata() {
    try {
        const response = await fetch(`${API_BASE}/metadata`, {
            signal: AbortSignal.timeout(5000)
        });
        
        if (response.ok) {
            return await response.json();
        }
    } catch (error) {
        console.debug('Could not fetch metadata:', error);
    }
    return null;
}

/**
 * Fetch operational location status (stale data, failure and retry guidance).
 */
export async function fetchLocationStatus() {
    try {
        const response = await fetch(`${API_BASE.replace('/api/radar', '/api/cache')}/status`, {
            signal: AbortSignal.timeout(5000)
        });
        if (!response.ok) return null;
        return await response.json();
    } catch (error) {
        console.debug('Could not fetch location status:', error);
        return null;
    }
}

/**
 * Fetch latest radar data (for latest mode)
 */
export async function fetchLatestRadar() {
    const response = await fetch(API_BASE, {
        signal: AbortSignal.timeout(10000)
    });
    
    if (!response.ok) {
        // Read the response body exactly once. Reading it twice loses the
        // service's structured error details on the 404 path.
        const errorData = await response.json().catch(() => ({
            message: `HTTP ${response.status}: ${response.statusText}`
        }));

        if (response.status === 404) {
            state.lastRefreshTime = new Date();

            const refreshEndpoint = errorData.suggestions?.refreshEndpoint || errorData.refreshEndpoint;
            triggerBackgroundRefresh(refreshEndpoint);

            const cacheStatusData = createCacheStatusData(errorData);
            const retryAfter = errorData.details?.retryAfterSeconds ||
                errorData.suggestions?.retryAfter ||
                errorData.retryAfter || 30;
            const message = errorData.message ||
                `Cache is being generated. Please wait ${retryAfter} seconds and refresh.`;

            return {
                error: 'cache_generating',
                cacheStatus: cacheStatusData,
                message: message,
                refreshEndpoint: refreshEndpoint,
                retryAfter: retryAfter
            };
        }

        throw new Error(errorData.message || errorData.error || 'Failed to fetch radar data');
    }
    
    const data = await response.json();
    data.isExtendedMode = false;
    return { data: data, error: null };
}

/**
 * Main function to fetch radar data (handles both latest and extended modes)
 */
export async function fetchRadarData() {
    try {
        // Extended mode
        if (state.settings.timespan !== 'latest') {
            // Fetch cache range first if needed
            if (!state.cacheRangeInfo) {
                const rangeResult = await fetchCacheRange();
                if (rangeResult.error === 'network') {
                    throw new Error('Network error - API unavailable');
                }
                state.cacheRangeInfo = rangeResult.data;
            }
            
            if (!state.cacheRangeInfo || state.cacheRangeInfo.totalCacheFolders === 0) {
                const refreshEndpoint = `${API_BASE.replace('/api/radar', '/api/cache')}/refresh`;
                triggerBackgroundRefresh(refreshEndpoint);
                
                // Try to fetch cache status
                try {
                    const statusResponse = await fetch(`${API_BASE}`, {
                        signal: AbortSignal.timeout(5000)
                    });
                    if (statusResponse.status === 404) {
                        const errorData = await statusResponse.json().catch(() => ({}));
                        return {
                            error: 'no_cache_data',
                            cacheStatus: createCacheStatusData(errorData),
                            message: 'No historical cache data available yet. Cache update has been triggered in the background.'
                        };
                    }
                } catch (err) {
                    console.debug('Could not fetch cache status:', err);
                }
                
                return {
                    error: 'no_cache_data',
                    message: 'No historical cache data available yet. Cache update has been triggered in the background.'
                };
            }
            
            // Calculate time range
            let startTime = null;
            let endTime = new Date();
            
            if (state.settings.timespan === 'custom') {
                const startInput = document.getElementById('start-time-input');
                const endInput = document.getElementById('end-time-input');
                if (startInput && startInput.value) startTime = new Date(startInput.value);
                if (endInput && endInput.value) endTime = new Date(endInput.value);
            } else {
                const hours = parseInt(state.settings.timespan.replace('h', '')) || 1;
                startTime = new Date(endTime.getTime() - (hours * 60 * 60 * 1000));
            }
            
            // Fetch historical data
            const result = await fetchHistoricalRadar(startTime, endTime);
            if (result.error === 'network') {
                throw new Error('Network error - API unavailable');
            }
            
            if (result.error === 'location_missing') {
                return {
                    error: 'location_missing',
                    cacheStatus: createCacheStatusData(result.cacheStatus || {}),
                    message: 'No cached data found for this location. Cache update has been triggered in background.'
                };
            }
            
            if (!result.frames || result.frames.length === 0) {
                return {
                    error: 'no_frames',
                    message: 'No frames found for selected timespan. Try a different range or wait for more cache data.'
                };
            }
            
            // Fetch metadata
            let metadata = null;
            try {
                const metadataResponse = await fetch(API_BASE, {
                    signal: AbortSignal.timeout(5000)
                });
                if (metadataResponse.ok) {
                    const latestData = await metadataResponse.json();
                    metadata = {
                        weatherStation: latestData.weatherStation,
                        distance: latestData.distance,
                        observationTime: latestData.observationTime,
                        cacheExpiresAt: latestData.cacheExpiresAt,
                        nextUpdateTime: latestData.nextUpdateTime,
                        cacheIsValid: latestData.cacheIsValid,
                        isUpdating: latestData.isUpdating
                    };
                }
            } catch (err) {
                console.debug('Could not fetch metadata for extended mode:', err);
            }
            
            // Create response object
            const newestCacheFolder = state.historicalData.cacheFolders[state.historicalData.cacheFolders.length - 1];
            return {
                data: {
                    frames: result.frames,
                    lastUpdated: endTime.toISOString(),
                    observationTime: metadata?.observationTime || newestCacheFolder?.observationTime || endTime.toISOString(),
                    forecastTime: endTime.toISOString(),
                    weatherStation: metadata?.weatherStation || null,
                    distance: metadata?.distance || null,
                    cacheIsValid: metadata?.cacheIsValid ?? true,
                    cacheExpiresAt: metadata?.cacheExpiresAt || endTime.toISOString(),
                    isUpdating: metadata?.isUpdating || false,
                    nextUpdateTime: metadata?.nextUpdateTime || endTime.toISOString(),
                    totalFrames: result.frames.length,
                    isExtendedMode: true
                },
                error: null
            };
        } else {
            // Latest mode
            const result = await fetchLatestRadar();
            if (result.error) {
                return result;
            }

            if (result.data && result.data.cacheIsValid === false) {
                const status = await fetchLocationStatus();
                if (status) {
                    result.data.updateFailed = status.updateFailed === true;
                    result.data.errorCode = status.errorCode || null;
                    result.data.error = status.error || null;
                    result.data.nextRetryAt = status.nextRetryAt || null;
                    result.data.consecutiveFailures = status.consecutiveFailures || 0;
                    result.data.isUpdating = result.data.isUpdating || status.isUpdating === true;
                    result.data.nextUpdateTime = result.data.nextUpdateTime || status.nextUpdateTime || null;
                }
            }

            return result;
        }
    } catch (error) {
        console.error('Error fetching radar data:', error);
        if (isNetworkError(error)) {
            return { error: 'network', originalError: error };
        }
        return { error: 'unknown', message: error.message };
    }
}

