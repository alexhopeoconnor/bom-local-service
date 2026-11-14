// UI update functions
import { formatDate, getRelativeTime, setElementText, setElementHTML, getElement } from './utils.js';
import { state } from './state.js';

function buildFailureDetail(data) {
    return {
        text: 'Last update failed' +
            (data.errorCode ? ` (${data.errorCode})` : '') +
            (data.nextRetryAt ? ` • Retry after ${formatDate(data.nextRetryAt)} (${getRelativeTime(data.nextRetryAt)})` : ''),
        title: data.error || '',
        showEstimation: false
    };
}

function stopPlaybackForEmptyCollection() {
    if (state.playInterval) {
        clearInterval(state.playInterval);
        state.playInterval = null;
    }
    state.isPlaying = false;
    setElementText('play-btn', '▶ Play');
}

/**
 * Update API connection status indicator
 */
export function updateApiStatus(status) {
    const refreshStatusEl = getElement('refresh-status');
    const refreshIndicatorEl = getElement('refresh-indicator');
    
    if (!refreshStatusEl || !refreshIndicatorEl) return;
    
    if (status === 'connected') {
        refreshStatusEl.textContent = 'Active';
        refreshIndicatorEl.style.background = '#28a745';
        refreshIndicatorEl.style.animation = 'pulse 2s infinite';
    } else if (status === 'disconnected') {
        refreshStatusEl.textContent = 'Reconnecting...';
        refreshIndicatorEl.style.background = '#ffc107';
        refreshIndicatorEl.style.animation = 'pulse 1s infinite';
    } else if (status === 'failed') {
        refreshStatusEl.textContent = 'Connection Failed';
        refreshIndicatorEl.style.background = '#dc3545';
        refreshIndicatorEl.style.animation = 'none';
    }
}

/**
 * Show error message
 */
export function showError(message) {
    const container = getElement('error-container');
    if (!container) return;
    
    const isRetrying = message.includes('Retrying');
    const errorClass = isRetrying ? 'error retrying' : 'error';
    container.innerHTML = `<div class="${errorClass}">${isRetrying ? '⚠️ ' : '❌ Error: '}${message}</div>`;
}

/**
 * Show graceful "no frames" message
 */
export function showNoFramesMessage(message, refreshEndpoint = null) {
    const container = getElement('error-container');
    if (!container) return;
    
    let html = `<div class="error retrying" style="background: #fff3cd; color: #856404; border-left-color: #ffc107;">
        <div style="margin-bottom: 10px;">⏳ ${message}</div>`;
    
    if (refreshEndpoint) {
        html += `<button class="play-btn" onclick="window.location.reload()" style="margin-top: 10px; padding: 8px 16px; font-size: 0.9em;">
            🔄 Refresh Now
        </button>`;
    }
    
    html += `</div>`;
    container.innerHTML = html;
    
    // Hide loading and show message in image container
    const loadingEl = getElement('loading');
    const imageEl = getElement('radar-image');
    if (loadingEl) loadingEl.textContent = message;
    if (imageEl) imageEl.style.display = 'none';
}

/**
 * Clear error message
 */
export function clearError() {
    const container = getElement('error-container');
    if (container) container.innerHTML = '';
}

/**
 * Update relative times periodically
 */
export function updateRelativeTimes() {
    if (!state.radarData) return;
    
    const expiresEl = getElement('cache-expires-relative');
    const nextUpdateEl = getElement('next-update-relative');
    const updateStatusDetailEl = getElement('update-status-detail');
    const nextCheckEl = getElement('next-client-check');
    const estimationNoteEl = getElement('estimation-note');
    
    if (state.radarData.cacheExpiresAt && expiresEl) {
        expiresEl.textContent = getRelativeTime(state.radarData.cacheExpiresAt);
    }
    
    if (state.radarData.nextUpdateTime && nextUpdateEl) {
        nextUpdateEl.textContent = getRelativeTime(state.radarData.nextUpdateTime);
    }
    
    // Update update status detail with relative time. A stale/failed update
    // keeps its full failure text instead of being replaced by a scheduled
    // update message on the next one-second tick.
    if (updateStatusDetailEl) {
        const override = state.updateStatusDetailOverride;
        if (override) {
            updateStatusDetailEl.textContent = override.text;
            updateStatusDetailEl.title = override.title || '';
            if (estimationNoteEl) estimationNoteEl.style.display = override.showEstimation ? 'inline' : 'none';
        } else if (state.radarData.isUpdating && state.radarData.nextUpdateTime) {
            const relativeTime = getRelativeTime(state.radarData.nextUpdateTime);
            updateStatusDetailEl.textContent = 'Update in progress • Estimated completion: ' + formatDate(state.radarData.nextUpdateTime) + ' (' + relativeTime + ')';
            updateStatusDetailEl.title = 'Estimate is based on historical metrics and current progress. Accuracy improves as more updates complete.';
            if (estimationNoteEl) estimationNoteEl.style.display = 'inline';
        } else if (!state.radarData.cacheIsValid && !state.radarData.isUpdating && state.radarData.nextUpdateTime) {
            const relativeTime = getRelativeTime(state.radarData.nextUpdateTime);
            updateStatusDetailEl.textContent = 'Update scheduled: ' + formatDate(state.radarData.nextUpdateTime) + ' (' + relativeTime + ')';
            updateStatusDetailEl.title = 'Estimated time when cache update will be triggered or completed. Based on metrics from previous updates.';
            if (estimationNoteEl) estimationNoteEl.style.display = 'inline';
        } else if (state.radarData.cacheIsValid) {
            updateStatusDetailEl.textContent = 'Cache is valid, no update needed';
            updateStatusDetailEl.title = '';
            if (estimationNoteEl) estimationNoteEl.style.display = 'none';
        } else {
            if (estimationNoteEl) estimationNoteEl.style.display = 'none';
        }
    }
    
    // Update next client check time
    if (state.nextClientCheckTime && nextCheckEl) {
        nextCheckEl.textContent = 'Next check: ' + formatDate(state.nextClientCheckTime.toISOString()) + ' (' + getRelativeTime(state.nextClientCheckTime.toISOString()) + ')';
    }
}

/**
 * Update status cards with cache update information
 */
export function updateStatusForCacheUpdate(retryAfter) {
    const statusEl = getElement('cache-status');
    const updateStatusDetailEl = getElement('update-status-detail');
    
    if (statusEl) {
        statusEl.innerHTML = 'Generating <span class="status-badge status-updating">IN PROGRESS</span>';
    }
    
    if (updateStatusDetailEl) {
        if (retryAfter) {
            const estimatedTime = new Date(Date.now() + (retryAfter * 1000));
            updateStatusDetailEl.textContent = `Cache generation in progress. Estimated ready in ${retryAfter} seconds.`;
            setElementText('next-update', formatDate(estimatedTime.toISOString()));
        } else {
            updateStatusDetailEl.textContent = 'Cache generation in progress, please wait...';
            setElementText('next-update', '-');
        }
    }
    
    // Set other fields to indicate cache is being generated
    setElementText('observation-time', '-');
    setElementText('cache-expires', '-');
    setElementText('weather-station', '-');
    setElementText('distance', '-');
    
    // Update last refresh
    if (state.lastRefreshTime) {
        setElementText('last-refresh', 'Last checked: ' + formatDate(state.lastRefreshTime.toISOString()));
        
        // Calculate and display next client check time
        state.nextClientCheckTime = new Date(state.lastRefreshTime.getTime() + (state.settings.refreshInterval * 1000));
        const nextCheckEl = getElement('next-client-check');
        if (nextCheckEl) {
            nextCheckEl.textContent = 'Next check: ' + formatDate(state.nextClientCheckTime.toISOString()) + ' (' + getRelativeTime(state.nextClientCheckTime.toISOString()) + ')';
        }
    }
}

/**
 * Update UI with radar data (main update function)
 */
export function updateUI(data) {
    // Preserve the collection the user was viewing so a shorter replacement
    // can clamp the selected index and keep the same observation when possible.
    state.previousFrames = Array.isArray(state.frames) ? state.frames.slice() : [];
    state.previousFrameIndex = state.currentFrameIndex;

    // Update cache status even if no frames are available
    if (data) {
        const statusEl = getElement('cache-status');
        if (statusEl && (data.isUpdating !== undefined || data.cacheIsValid !== undefined)) {
            let statusHtml = '';
            if (data.isUpdating) {
                statusHtml = 'Updating <span class="status-badge status-updating">IN PROGRESS</span>';
            } else if (data.updateFailed) {
                statusHtml = 'Stale <span class="status-badge status-invalid">RETRYING</span>';
            } else if (data.cacheIsValid) {
                statusHtml = 'Valid <span class="status-badge status-valid">ACTIVE</span>';
            } else {
                statusHtml = 'Invalid <span class="status-badge status-invalid">EXPIRED</span>';
            }
            statusEl.innerHTML = statusHtml;
        }
        
        // Update update status detail
        const updateStatusDetailEl = getElement('update-status-detail');
        const estimationNoteEl = getElement('estimation-note');
        const hasStatus = data.isUpdating !== undefined || data.cacheIsValid !== undefined;
        const failureDetail = data.updateFailed && !data.isUpdating
            ? buildFailureDetail(data)
            : null;

        if (hasStatus) {
            // Keep failure text stable while relative-time ticks run. Only clear
            // it on an explicit healthy/unscheduled answer, not on partial data.
            if (failureDetail) {
                state.updateStatusDetailOverride = failureDetail;
            } else if (data.updateFailed === false || data.cacheIsValid === true || data.isUpdating) {
                state.updateStatusDetailOverride = null;
            }
        }

        const effectiveFailure = state.updateStatusDetailOverride;
        if (updateStatusDetailEl && hasStatus) {
            if (effectiveFailure) {
                updateStatusDetailEl.textContent = effectiveFailure.text;
                updateStatusDetailEl.title = effectiveFailure.title;
                if (estimationNoteEl) estimationNoteEl.style.display = 'none';
            } else if (data.isUpdating) {
                if (data.nextUpdateTime) {
                    const relativeTime = getRelativeTime(data.nextUpdateTime);
                    updateStatusDetailEl.textContent = 'Update in progress • Estimated completion: ' + formatDate(data.nextUpdateTime) + ' (' + relativeTime + ')';
                    updateStatusDetailEl.title = 'Estimate is based on historical metrics and current progress. Accuracy improves as more updates complete.';
                    if (estimationNoteEl) estimationNoteEl.style.display = 'inline';
                } else {
                    updateStatusDetailEl.textContent = 'Update in progress, please wait...';
                    updateStatusDetailEl.title = 'Cache update is in progress. Completion time will be estimated once progress tracking begins.';
                    if (estimationNoteEl) estimationNoteEl.style.display = 'none';
                }
            } else if (data.cacheIsValid) {
                updateStatusDetailEl.textContent = 'Cache is valid, no update needed';
                updateStatusDetailEl.title = '';
                if (estimationNoteEl) estimationNoteEl.style.display = 'none';
            } else {
                if (data.nextUpdateTime) {
                    const relativeTime = getRelativeTime(data.nextUpdateTime);
                    updateStatusDetailEl.textContent = 'Update scheduled: ' + formatDate(data.nextUpdateTime) + ' (' + relativeTime + ')';
                    updateStatusDetailEl.title = 'Estimated time when cache update will be triggered or completed. Based on metrics from previous updates.';
                    if (estimationNoteEl) estimationNoteEl.style.display = 'inline';
                } else {
                    updateStatusDetailEl.textContent = 'Update will be triggered by background service';
                    updateStatusDetailEl.title = 'The background cache management service will check and update the cache periodically.';
                    if (estimationNoteEl) estimationNoteEl.style.display = 'none';
                }
            }
        }
        
        // Update other metadata fields
        if (data.observationTime) setElementText('observation-time', formatDate(data.observationTime));
        if (data.cacheExpiresAt) setElementText('cache-expires', formatDate(data.cacheExpiresAt));
        if (data.nextUpdateTime) setElementText('next-update', formatDate(data.nextUpdateTime));
        if (data.weatherStation !== undefined) setElementText('weather-station', data.weatherStation || '-');
        if (data.distance !== undefined) setElementText('distance', data.distance || '-');
    }
    
    // Handle no frames case
    if (!data || !data.frames || data.frames.length === 0) {
        if (data) {
            // Keep the latest status visible to relative-time updates.
            state.radarData = data;
            if (data.isUpdating !== undefined || data.cacheIsValid !== undefined) {
                if (data.updateFailed && !data.isUpdating) {
                    state.updateStatusDetailOverride = buildFailureDetail(data);
                } else if (data.updateFailed === false || data.cacheIsValid === true || data.isUpdating) {
                    state.updateStatusDetailOverride = null;
                }
            }
        }

        const currentStatus = getElement('cache-status')?.innerHTML;
        if (!currentStatus || currentStatus === 'Checking...' || currentStatus.trim() === '') {
            const statusEl = getElement('cache-status');
            const updateStatusDetailEl = getElement('update-status-detail');
            if (statusEl) statusEl.innerHTML = 'Generating <span class="status-badge status-updating">IN PROGRESS</span>';
            if (updateStatusDetailEl) updateStatusDetailEl.textContent = 'Cache generation in progress, please wait...';
        }

        showNoFramesMessage('No radar frames available yet. Cache is being generated in the background. Please wait a moment and refresh.');
        state.frames = [];
        state.currentFrameIndex = 0;
        stopPlaybackForEmptyCollection();
        const controlsEl = getElement('frame-controls');
        if (controlsEl) controlsEl.innerHTML = '';
        setElementText('play-btn', '▶ Play');
        const playBtn = getElement('play-btn');
        const prevBtn = getElement('prev-btn');
        const nextBtn = getElement('next-btn');
        if (playBtn) playBtn.disabled = true;
        if (prevBtn) prevBtn.disabled = true;
        if (nextBtn) nextBtn.disabled = true;
        setElementText('frame-info', 'Waiting for cache to be generated...');
        setElementText('current-frame-time', '-');
        setElementText('frame-progress', '');

        const imgEl = getElement('radar-image');
        if (imgEl) {
            imgEl.style.display = 'none';
            imgEl.removeAttribute('src');
        }
        return;
    }
    
    // Update state
    state.radarData = data;
    state.isExtendedMode = data.isExtendedMode || false;
    
    // Enable controls
    const playBtn = getElement('play-btn');
    const prevBtn = getElement('prev-btn');
    const nextBtn = getElement('next-btn');
    if (playBtn) playBtn.disabled = false;
    if (prevBtn) prevBtn.disabled = false;
    if (nextBtn) nextBtn.disabled = false;
    
    // Sort frames appropriately
    if (state.isExtendedMode) {
        state.frames = data.frames;
    } else {
        state.frames = data.frames.sort((a, b) => a.frameIndex - b.frameIndex);
    }
    
    // Update metadata fields (redundant updates removed since already done above)
    // Update last refresh
    if (state.lastRefreshTime) {
        setElementText('last-refresh', 'Last checked: ' + formatDate(state.lastRefreshTime.toISOString()));
        
        state.nextClientCheckTime = new Date(state.lastRefreshTime.getTime() + (state.settings.refreshInterval * 1000));
        if (data.nextRetryAt) {
            const retryTime = new Date(data.nextRetryAt);
            if (!isNaN(retryTime.getTime()) && retryTime > state.nextClientCheckTime) {
                state.nextClientCheckTime = retryTime;
            }
        }
        const nextCheckEl = getElement('next-client-check');
        if (nextCheckEl) {
            nextCheckEl.textContent = 'Next check: ' + formatDate(state.nextClientCheckTime.toISOString()) + ' (' + getRelativeTime(state.nextClientCheckTime.toISOString()) + ')';
        }
    }
    
    // Clamp the selected index to the replacement collection.
    if (state.currentFrameIndex < 0 || state.currentFrameIndex >= state.frames.length) {
        state.currentFrameIndex = state.frames.length > 0 ? state.frames.length - 1 : 0;
    }

    // Frame controls will be built by the caller (to avoid circular dependencies)
}

