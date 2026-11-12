// Application state management
import { DEFAULT_SETTINGS } from './config.js';

// Export a mutable state object - properties can be mutated even though the object binding is read-only
export const state = {
    currentFrameIndex: 0,
    frames: [],
    previousFrames: [],
    previousFrameIndex: 0,
    radarData: null,
    playInterval: null,
    isPlaying: false,
    lastRefreshTime: null,
    refreshInterval: null,
    nextClientCheckTime: null,
    retryInterval: null,
    isApiDown: false,
    retryAttempts: 0,
    cacheRangeInfo: null,
    historicalData: null,
    isExtendedMode: false,
    settings: { ...DEFAULT_SETTINGS }
};

// Reset state (for testing/debugging)
export function resetState() {
    state.currentFrameIndex = 0;
    state.frames = [];
    state.previousFrames = [];
    state.previousFrameIndex = 0;
    state.radarData = null;
    state.playInterval = null;
    state.isPlaying = false;
    state.lastRefreshTime = null;
    state.refreshInterval = null;
    state.nextClientCheckTime = null;
    state.retryInterval = null;
    state.isApiDown = false;
    state.retryAttempts = 0;
    state.cacheRangeInfo = null;
    state.historicalData = null;
    state.isExtendedMode = false;
    state.settings = { ...DEFAULT_SETTINGS };
}

