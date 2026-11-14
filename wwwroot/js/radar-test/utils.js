// Utility functions

// Cookie management
export function setCookie(name, value, days = 365) {
    const expires = new Date();
    expires.setTime(expires.getTime() + (days * 24 * 60 * 60 * 1000));
    document.cookie = `${name}=${encodeURIComponent(JSON.stringify(value))};expires=${expires.toUTCString()};path=/`;
}

export function getCookie(name) {
    const nameEQ = name + "=";
    const ca = document.cookie.split(';');
    for (let i = 0; i < ca.length; i++) {
        let c = ca[i];
        while (c.charAt(0) === ' ') c = c.substring(1, c.length);
        if (c.indexOf(nameEQ) === 0) {
            try {
                return JSON.parse(decodeURIComponent(c.substring(nameEQ.length, c.length)));
            } catch (e) {
                return null;
            }
        }
    }
    return null;
}

// Date formatting utilities
export function formatDate(dateString) {
    if (!dateString) return '-';
    const date = new Date(dateString);
    return date.toLocaleString('en-AU', {
        timeZone: 'Australia/Brisbane',
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
    });
}

export function getRelativeTime(dateString) {
    if (!dateString) return '';
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = date - now;
    const diffMins = Math.floor(diffMs / 60000);
    const diffSecs = Math.floor((diffMs % 60000) / 1000);
    
    if (diffMs < 0) {
        return Math.abs(diffMins) + ' minute(s) ago';
    } else if (diffMins > 0) {
        return 'in ' + diffMins + ' minute(s)';
    } else {
        return 'in ' + diffSecs + ' second(s)';
    }
}

export function getMinutesAgo(dateString) {
    if (!dateString) return null;
    
    // Handle both string and Date object inputs
    let date;
    if (typeof dateString === 'string') {
        date = new Date(dateString);
    } else if (dateString instanceof Date) {
        date = dateString;
    } else {
        console.warn('getMinutesAgo: Expected string or Date, got:', typeof dateString, dateString);
        return null;
    }
    
    // Check if date is valid
    if (isNaN(date.getTime())) {
        console.warn('getMinutesAgo: Invalid date:', dateString, 'parsed as:', date);
        return null;
    }
    
    // Calculate difference in milliseconds
    // Date.now() is UTC-based, date.getTime() is UTC-based, so this should be correct
    const now = Date.now();
    const dateTime = date.getTime();
    const diffMs = now - dateTime;
    
    // Convert to minutes and round
    const minutes = Math.round(diffMs / 60000);
    
    // If negative (future date), return null
    if (minutes < 0) {
        console.warn('getMinutesAgo: Future date detected:', dateString, 'parsed as:', date.toISOString());
        return null;
    }
    
    // Safeguard against unreasonably large values (likely parsing errors)
    // But don't cap at 60 - radar frames can legitimately be older in some cases
    if (minutes > 100000) { // e.g., more than ~69 days ago
        console.warn('getMinutesAgo: Unreasonably large minutes value:', minutes, 'for date:', dateString, 'parsed as:', date.toISOString());
        return null;
    }
    
    return minutes;
}

// Format frame time for display (consolidates duplicate logic)
export function formatFrameTime(frame, isExtendedMode) {
    if (!frame.absoluteObservationTime) return '-';
    
    if (isExtendedMode) {
        return formatDate(frame.absoluteObservationTime);
    } else {
        const minutesAgo = getMinutesAgo(frame.absoluteObservationTime);
        // If minutesAgo is null (invalid date or edge case), fall back to formatted date
        if (minutesAgo === null) {
            return formatDate(frame.absoluteObservationTime);
        }
        return `${minutesAgo} min ago`;
    }
}

// Network error detection (consolidates duplicate checks)
export function isNetworkError(error) {
    return error.name === 'TypeError' || 
           error.name === 'AbortError' || 
           error.name === 'TimeoutError' ||
           error.message.includes('fetch') ||
           error.message.includes('network') ||
           error.message.includes('Failed to fetch') ||
           error.message === 'Network error';
}

export function isNetworkErrorResponse(response) {
    return response.status === 0 || response.status >= 500;
}

// DOM helpers
export function getElement(id) {
    return document.getElementById(id);
}

export function setElementText(id, text) {
    const el = getElement(id);
    if (el) el.textContent = text;
}

export function setElementHTML(id, html) {
    const el = getElement(id);
    if (el) el.innerHTML = html;
}

// Create cache status data object (consolidates duplicate creation)
export function createCacheStatusData(errorData) {
    const details = errorData.details || errorData;
    const isUpdating = details.updateTriggered === true || 
                      (errorData.message && errorData.message.includes('in progress')) ||
                      (details.statusMessage && details.statusMessage.includes('in progress'));
    
    return {
        frames: [],
        isUpdating: isUpdating,
        cacheIsValid: details.cacheIsValid || false,
        cacheExpiresAt: details.cacheExpiresAt || null,
        nextUpdateTime: details.nextUpdateTime || null,
        updateFailed: details.previousUpdateFailed === true || details.updateFailed === true,
        errorCode: details.previousErrorCode || details.errorCode || details.refreshRejected || null,
        error: details.previousError || details.error || null,
        nextRetryAt: details.nextRetryAt || null,
        consecutiveFailures: details.consecutiveFailures || 0,
        retryAfterSeconds: details.retryAfterSeconds || null
    };
}

// Trigger background cache refresh (consolidates duplicate calls)
export function triggerBackgroundRefresh(refreshEndpoint) {
    if (refreshEndpoint) {
        fetch(refreshEndpoint, {
            method: 'POST',
            signal: AbortSignal.timeout(5000)
        }).catch(err => {
            console.debug('Background cache refresh trigger failed (non-critical):', err);
        });
    }
}

// Preload all frame images to prevent jiggle
export function preloadImages(frames) {
    frames.forEach(frame => {
        const img = new Image();
        img.src = frame.imageUrl;
    });
}

