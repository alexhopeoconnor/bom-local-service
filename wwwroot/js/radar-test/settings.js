// Settings management
import { getCookie, setCookie } from './utils.js';
import { state } from './state.js';
import { fetchCacheRange } from './api-client.js';
import { pause, play } from './slideshow.js';

/**
 * Load settings from cookies
 */
export function loadSettings() {
    const saved = getCookie('radarTestSettings');
    if (saved) {
        state.settings = { ...state.settings, ...saved };
    }
    updateSettingsUI();
}

/**
 * Save settings to cookies
 */
export function saveSettings() {
    setCookie('radarTestSettings', state.settings);
    updateSettingsUI();
    applySettings();
}

/**
 * Update settings UI elements
 */
export function updateSettingsUI() {
    document.getElementById('frame-interval-input').value = state.settings.frameInterval;
    document.getElementById('refresh-interval-input').value = state.settings.refreshInterval;
    document.getElementById('auto-play-input').checked = state.settings.autoPlay;
    document.getElementById('timespan-select').value = state.settings.timespan || 'latest';
    
    // Show/hide custom range section
    const customSection = document.getElementById('custom-range-section');
    if (customSection) {
        customSection.style.display = state.settings.timespan === 'custom' ? 'block' : 'none';
    }
    
    // Load cache range info when settings modal opens
    fetchCacheRange();
}

/**
 * Apply settings (restart playback; polling is restarted by the caller)
 */
export function applySettings() {
    // Restart play interval if playing
    if (state.isPlaying) {
        pause();
        play();
    }
}

