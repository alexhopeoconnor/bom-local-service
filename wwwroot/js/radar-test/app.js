// Main application entry point
import { loadSettings, saveSettings, updateSettingsUI, applySettings } from './settings.js';
import { updateRelativeTimes } from './ui-updater.js';
import { startPolling, restartPolling } from './polling.js';
import { togglePlay, previousFrame, nextFrame } from './slideshow.js';
import { setupKeyboardNavigation } from './keyboard.js';
import { state } from './state.js';

/**
 * Settings modal management
 */
function showSettings() {
    const modal = document.getElementById('settings-modal');
    if (modal) modal.style.display = 'flex';
}

function hideSettings() {
    const modal = document.getElementById('settings-modal');
    if (modal) modal.style.display = 'none';
}

/**
 * Initialize application
 */
async function init() {
    // Load settings from cookies
    loadSettings();

    // Set up event listeners
    const playBtn = document.getElementById('play-btn');
    const prevBtn = document.getElementById('prev-btn');
    const nextBtn = document.getElementById('next-btn');

    if (playBtn) playBtn.addEventListener('click', togglePlay);
    if (prevBtn) prevBtn.addEventListener('click', previousFrame);
    if (nextBtn) nextBtn.addEventListener('click', nextFrame);

    // Settings button
    const settingsBtn = document.getElementById('settings-btn-header');
    if (settingsBtn) {
        settingsBtn.addEventListener('click', showSettings);
    }

    // Settings modal handlers
    const saveSettingsBtn = document.getElementById('save-settings-btn');
    if (saveSettingsBtn) {
        saveSettingsBtn.addEventListener('click', () => {
            const frameIntervalValue = parseFloat(document.getElementById('frame-interval-input').value);
            state.settings.frameInterval = (frameIntervalValue > 0) ? frameIntervalValue : 2.0;

            const refreshIntervalValue = parseInt(document.getElementById('refresh-interval-input').value);
            state.settings.refreshInterval = (refreshIntervalValue >= 5) ? refreshIntervalValue : 30;
            state.settings.autoPlay = document.getElementById('auto-play-input').checked;
            state.settings.timespan = document.getElementById('timespan-select').value;

            if (state.settings.timespan === 'custom') {
                const startInput = document.getElementById('start-time-input');
                const endInput = document.getElementById('end-time-input');
                if (startInput && startInput.value) {
                    state.settings.customStartTime = startInput.value;
                }
                if (endInput && endInput.value) {
                    state.settings.customEndTime = endInput.value;
                }
            }

            saveSettings();
            hideSettings();

            // Apply settings and reload through the single refresh scheduler.
            applySettings();
            void restartPolling();
        });
    }

    // Timespan select change handler
    const timespanSelect = document.getElementById('timespan-select');
    if (timespanSelect) {
        timespanSelect.addEventListener('change', (e) => {
            state.settings.timespan = e.target.value;
            const customSection = document.getElementById('custom-range-section');
            if (customSection) {
                customSection.style.display = e.target.value === 'custom' ? 'block' : 'none';
            }
        });
    }

    // Cancel settings button
    const cancelSettingsBtn = document.getElementById('cancel-settings-btn');
    if (cancelSettingsBtn) {
        cancelSettingsBtn.addEventListener('click', () => {
            updateSettingsUI(); // Reset to current settings
            hideSettings();
        });
    }

    // Close button handler
    const closeSettingsBtn = document.getElementById('close-settings-btn');
    if (closeSettingsBtn) {
        closeSettingsBtn.addEventListener('click', hideSettings);
    }

    // Close modal on background click
    const settingsModal = document.getElementById('settings-modal');
    if (settingsModal) {
        settingsModal.addEventListener('click', (e) => {
            if (e.target.id === 'settings-modal') {
                hideSettings();
            }
        });
    }

    // Setup keyboard navigation
    setupKeyboardNavigation();

    // Initial load starts the one and only poll loop.
    await startPolling();

    // Update relative times every second
    setInterval(updateRelativeTimes, 1000);
}

// Start when page loads
init();
