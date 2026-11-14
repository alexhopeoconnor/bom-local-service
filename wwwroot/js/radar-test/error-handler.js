// Error handling and API retry state
import { MAX_RETRY_ATTEMPTS, RETRY_DELAY_MS, MAX_RETRY_DELAY_MS } from './config.js';
import { state } from './state.js';
import { updateApiStatus, showError, clearError } from './ui-updater.js';

/**
 * Reset API retry state when a fetch succeeds.
 * Timer ownership stays with the refresh scheduler; this only resets visible state.
 */
export function resetApiRetryState() {
    const wasDown = state.isApiDown || state.retryAttempts > 0;

    state.isApiDown = false;
    state.retryAttempts = 0;

    // Clear the legacy interval in case an older page instance left one behind.
    if (state.retryInterval) {
        clearInterval(state.retryInterval);
        state.retryInterval = null;
    }

    if (wasDown) {
        clearError();
        updateApiStatus('connected');
    }
}

/**
 * Bounded exponential backoff for connection retries.
 */
export function getRetryDelayMs(attempt) {
    const safeAttempt = Math.max(1, Math.min(MAX_RETRY_ATTEMPTS, attempt));
    return Math.min(RETRY_DELAY_MS * (2 ** (safeAttempt - 1)), MAX_RETRY_DELAY_MS);
}

/**
 * Record a failed API fetch and return the delay the scheduler should use.
 * The scheduler (polling.js) remains the only component that owns timers and
 * performs fetches.
 */
export function handleApiDown(error) {
    if (!state.isApiDown) {
        state.isApiDown = true;
        state.retryAttempts = 0;
        updateApiStatus('disconnected');
    }

    state.retryAttempts++;

    if (state.retryAttempts >= MAX_RETRY_ATTEMPTS) {
        showError('API connection failed after multiple attempts. Please check if the service is running.');
        updateApiStatus('failed');
        return {
            exhausted: true,
            attempts: state.retryAttempts,
            retryDelayMs: 0,
            error: error || null
        };
    }

    showError(`API connection lost. Retrying... (Attempt ${state.retryAttempts}/${MAX_RETRY_ATTEMPTS})`);

    return {
        exhausted: false,
        attempts: state.retryAttempts,
        retryDelayMs: getRetryDelayMs(state.retryAttempts),
        error: error || null
    };
}
