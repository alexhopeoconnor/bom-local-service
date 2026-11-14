// Single-owner refresh scheduler.
//
// Every data fetch goes through refreshRadar(): there is one timer, one
// in-flight request and one success path. Keeping this in a leaf module avoids
// the app/error-handler cycle that previously let normal polling, retry polling
// and settings changes fetch concurrently.
import { state } from './state.js';
import { fetchRadarData } from './api-client.js';
import { updateUI, showError, showNoFramesMessage, clearError } from './ui-updater.js';
import { buildFrameControls, showFrame, findFrameToShowAfterRefresh } from './frame-navigation.js';
import { play } from './slideshow.js';
import { handleApiDown, resetApiRetryState } from './error-handler.js';

function normalDelayMs() {
    const seconds = Number(state.settings?.refreshInterval);
    return Math.max(1, Number.isFinite(seconds) ? seconds : 30) * 1000;
}

function clearPollTimer() {
    if (state.refreshTimeout) {
        clearTimeout(state.refreshTimeout);
        state.refreshTimeout = null;
    }
}

function clearNextCheckLabel() {
    state.nextClientCheckTime = null;
    const nextCheckEl = document.getElementById('next-client-check');
    if (nextCheckEl) nextCheckEl.textContent = '';
}

export function stopPolling({ clearNextCheck = true } = {}) {
    state.pollingEnabled = false;
    state.refreshQueued = false;
    clearPollTimer();

    // Legacy interval field: never let an old script instance keep polling.
    if (state.refreshInterval) {
        clearInterval(state.refreshInterval);
        state.refreshInterval = null;
    }

    if (clearNextCheck) clearNextCheckLabel();
}

function scheduleNextPoll(delayMs) {
    clearPollTimer();
    if (!state.pollingEnabled) return;

    const delay = Math.max(0, delayMs);
    state.nextClientCheckTime = new Date(Date.now() + delay);
    state.refreshTimeout = setTimeout(() => {
        state.refreshTimeout = null;
        void refreshRadar();
    }, delay);
}

function buildRetryableError(result) {
    if (result.originalError) return result.originalError;
    const error = new Error(result.message || 'Network error');
    error.name = 'TypeError';
    return error;
}

function applySuccess(data) {
    const wasPlaying = state.isPlaying;

    state.lastRefreshTime = new Date();
    resetApiRetryState();
    clearError();

    updateUI(data);

    if (state.frames.length === 0) {
        // updateUI paused playback and cleared controls for the empty replacement.
        scheduleNextPoll(normalDelayMs());
        return;
    }

    buildFrameControls();
    showFrame(findFrameToShowAfterRefresh());

    if (wasPlaying) {
        play();
    } else if (!state.autoPlayAttempted) {
        // Auto-play once, including after a cold-start retry. A user pause is
        // never overridden by later refreshes.
        state.autoPlayAttempted = true;
        if (state.settings.autoPlay && state.frames.length > 1) play();
    }

    scheduleNextPoll(normalDelayMs());
}

function applyResult(result) {
    if (result.error) {
        if (result.error === 'network') {
            const outcome = handleApiDown(buildRetryableError(result));
            if (outcome.exhausted) {
                stopPolling();
            } else {
                scheduleNextPoll(outcome.retryDelayMs);
            }
            return;
        }

        // The service answered, so any connection outage is over.
        resetApiRetryState();

        if (result.error === 'cache_generating') {
            updateUI(result.cacheStatus);
            showNoFramesMessage(result.message, result.refreshEndpoint);
        } else if (result.error === 'location_missing' ||
                   result.error === 'no_cache_data' ||
                   result.error === 'no_frames') {
            if (result.cacheStatus) updateUI(result.cacheStatus);
            showNoFramesMessage(result.message, result.refreshEndpoint);
        } else {
            showError(result.message || 'An error occurred');
        }

        scheduleNextPoll(normalDelayMs());
        return;
    }

    if (result.data) {
        applySuccess(result.data);
        return;
    }

    scheduleNextPoll(normalDelayMs());
}

/**
 * Fetch and apply radar data. Concurrent callers (settings saves, polling,
 * retries) coalesce into one request and at most one queued follow-up.
 */
export function refreshRadar() {
    if (!state.pollingEnabled) return Promise.resolve();
    if (state.isRefreshInFlight) {
        state.refreshQueued = true;
        return state.inFlightRefresh;
    }

    // A manual/timer refresh supersedes any pending poll.
    clearPollTimer();

    state.isRefreshInFlight = true;
    state.inFlightRefresh = (async () => {
        try {
            const result = await fetchRadarData();
            if (state.pollingEnabled) applyResult(result);
        } catch (error) {
            if (state.pollingEnabled) applyResult({ error: 'network', originalError: error });
        } finally {
            state.isRefreshInFlight = false;
            state.inFlightRefresh = null;

            if (state.refreshQueued && state.pollingEnabled) {
                state.refreshQueued = false;
                // A queued settings refresh must respect an outage's retry delay.
                if (!state.isApiDown) scheduleNextPoll(0);
            }
        }
    })();

    return state.inFlightRefresh;
}

/**
 * Start the single poll loop. Used once during initial load.
 */
export function startPolling() {
    stopPolling();
    state.pollingEnabled = true;
    return refreshRadar();
}

/**
 * Restart after a settings change without duplicating timer owners.
 */
export function restartPolling() {
    stopPolling();
    state.pollingEnabled = true;
    return refreshRadar();
}
