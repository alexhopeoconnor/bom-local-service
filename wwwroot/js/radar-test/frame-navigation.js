// Frame navigation and display
import { formatDate, formatFrameTime, getMinutesAgo, preloadImages } from './utils.js';
import { state } from './state.js';

/**
 * Calculate jump amount for frame navigation
 * Returns 10% of total frames (min 1, max 50) for extended mode, 0 otherwise
 */
export function getJumpAmount() {
    if (!state.isExtendedMode || state.frames.length === 0) {
        return 0;
    }
    return Math.max(1, Math.min(50, Math.round(state.frames.length / 10)));
}

/**
 * Build frame controls HTML and attach event listeners
 */
export function buildFrameControls() {
    const controlsEl = document.getElementById('frame-controls');
    if (!controlsEl) return;
    
    // Calculate jump amount - 10% of total frames, minimum 1, maximum 50
    // Only used in extended mode (timeseries)
    const jumpAmount = getJumpAmount();
    
    // Build jump buttons HTML only for extended mode
    const prevJumpButtonHtml = state.isExtendedMode && jumpAmount > 0
        ? `<button class="frame-nav-btn" id="frame-prev-jump-btn" title="Go back ${jumpAmount} frames" aria-label="Go back ${jumpAmount} frames">-${jumpAmount}</button>`
        : '';
    const nextJumpButtonHtml = state.isExtendedMode && jumpAmount > 0
        ? `<button class="frame-nav-btn" id="frame-next-jump-btn" title="Go forward ${jumpAmount} frames" aria-label="Go forward ${jumpAmount} frames">+${jumpAmount}</button>`
        : '';
    
    controlsEl.innerHTML = `
        <div class="frame-slider-container">
            <div class="frame-slider-wrapper">
                <button class="frame-nav-btn" id="frame-first-btn" title="First frame" aria-label="First frame">⏮</button>
                ${prevJumpButtonHtml}
                <input type="range" class="frame-slider" id="frame-slider" min="0" max="${state.frames.length - 1}" value="${state.currentFrameIndex}" aria-label="Frame slider">
                ${nextJumpButtonHtml}
                <button class="frame-nav-btn" id="frame-last-btn" title="Last frame" aria-label="Last frame">⏭</button>
            </div>
            <div class="frame-info-display">
                <div class="frame-info-item">
                    <span class="frame-info-label">Frame:</span>
                    <input type="number" class="frame-jump-input" id="frame-jump-input" min="0" max="${state.frames.length - 1}" value="0">
                    <span class="frame-info-value">/ ${state.frames.length - 1}</span>
                </div>
                <div class="frame-info-item">
                    <span class="frame-info-label">Time:</span>
                    <span class="frame-info-value" id="current-frame-time">-</span>
                </div>
                <div class="frame-info-item">
                    <span class="frame-info-label">Progress:</span>
                    <span class="frame-info-value" id="frame-progress">0%</span>
                </div>
            </div>
        </div>
    `;
    
    // Attach event listeners
    const firstBtn = document.getElementById('frame-first-btn');
    const prevJumpBtn = document.getElementById('frame-prev-jump-btn');
    const nextJumpBtn = document.getElementById('frame-next-jump-btn');
    const lastBtn = document.getElementById('frame-last-btn');
    const slider = document.getElementById('frame-slider');
    const jumpInput = document.getElementById('frame-jump-input');
    
    if (firstBtn) firstBtn.addEventListener('click', () => showFrame(0));
    if (prevJumpBtn && jumpAmount > 0) {
        prevJumpBtn.addEventListener('click', () => jumpFrame(-jumpAmount));
    }
    if (nextJumpBtn && jumpAmount > 0) {
        nextJumpBtn.addEventListener('click', () => jumpFrame(jumpAmount));
    }
    if (lastBtn) lastBtn.addEventListener('click', () => showFrame(state.frames.length - 1));
    if (slider) {
        slider.addEventListener('input', (e) => updateFrameFromSlider(parseInt(e.target.value)));
    }
    if (jumpInput) {
        jumpInput.addEventListener('change', (e) => jumpToFrame(parseInt(e.target.value)));
        jumpInput.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') jumpToFrame(parseInt(e.target.value));
        });
    }
    
    // Update initial display
    updateFrameSliderInfo(state.currentFrameIndex);
    
    // Preload all frame images
    preloadImages(state.frames);
}

/**
 * Update frame slider info display
 */
export function updateFrameSliderInfo(index) {
    if (index < 0 || index >= state.frames.length) return;
    
    const frame = state.frames[index];
    const progress = Math.round(((index + 1) / state.frames.length) * 100);
    
    // Update jump input
    const jumpInput = document.getElementById('frame-jump-input');
    if (jumpInput) jumpInput.value = index;
    
    // Update slider
    const slider = document.getElementById('frame-slider');
    if (slider) slider.value = index;
    
    // Update frame number display
    const frameNumDisplay = document.getElementById('current-frame-num-display');
    if (frameNumDisplay) frameNumDisplay.textContent = index + 1;
    
    // Update time display
    const timeDisplay = document.getElementById('current-frame-time');
    if (timeDisplay) {
        timeDisplay.textContent = formatFrameTime(frame, state.isExtendedMode);
    }
    
    // Update progress
    const progressDisplay = document.getElementById('frame-progress');
    if (progressDisplay) progressDisplay.textContent = `${progress}%`;
    
    // Update navigation buttons
    const firstBtn = document.getElementById('frame-first-btn');
    const prevJumpBtn = document.getElementById('frame-prev-jump-btn');
    const nextJumpBtn = document.getElementById('frame-next-jump-btn');
    const lastBtn = document.getElementById('frame-last-btn');
    
    if (firstBtn) firstBtn.disabled = index === 0;
    if (prevJumpBtn) prevJumpBtn.disabled = index === 0;
    if (nextJumpBtn) nextJumpBtn.disabled = index >= state.frames.length - 1;
    if (lastBtn) lastBtn.disabled = index >= state.frames.length - 1;
}

let sliderUpdateTimeout;
/**
 * Update frame from slider (with debouncing)
 */
export function updateFrameFromSlider(value) {
    clearTimeout(sliderUpdateTimeout);
    sliderUpdateTimeout = setTimeout(() => {
        showFrame(value);
    }, 50);
}

/**
 * Jump to specific frame number
 */
export function jumpToFrame(frameNum) {
    const index = Math.max(0, Math.min(state.frames.length - 1, frameNum));
    showFrame(index);
}

/**
 * Jump forward/backward by N frames
 */
export function jumpFrame(offset) {
    const newIndex = Math.max(0, Math.min(state.frames.length - 1, state.currentFrameIndex + offset));
    showFrame(newIndex);
}

/**
 * Build alt text for frame image
 */
function buildFrameAltText(frame, index) {
    if (state.isExtendedMode) {
        const frameNum = frame.sequentialIndex !== undefined ? frame.sequentialIndex : index;
        const timeInfo = frame.absoluteObservationTime 
            ? formatDate(frame.absoluteObservationTime)
            : (frame.cacheTimestamp ? formatDate(frame.cacheTimestamp) : '');
        return `Radar frame ${frameNum}${timeInfo ? ' (' + timeInfo + ')' : ''}`;
    } else {
        const minutesAgo = getMinutesAgo(frame.absoluteObservationTime);
        if (minutesAgo !== null) {
            return `Radar frame ${frame.frameIndex} (${minutesAgo} minutes ago)`;
        } else {
            // Fall back to formatted date if minutes ago calculation fails
            const timeInfo = frame.absoluteObservationTime ? formatDate(frame.absoluteObservationTime) : '';
            return `Radar frame ${frame.frameIndex}${timeInfo ? ' (' + timeInfo + ')' : ''}`;
        }
    }
}

/**
 * Build frame info text
 */
function buildFrameInfoText(frame, index) {
    if (state.isExtendedMode) {
        const frameNum = frame.sequentialIndex !== undefined ? frame.sequentialIndex : index;
        const timeInfo = frame.absoluteObservationTime 
            ? formatDate(frame.absoluteObservationTime)
            : (frame.cacheTimestamp ? formatDate(frame.cacheTimestamp) : '');
        return `Frame ${frameNum} of ${state.frames.length - 1}${timeInfo ? ' • ' + timeInfo : ''}`;
    } else {
        const minutesAgo = getMinutesAgo(frame.absoluteObservationTime);
        if (minutesAgo !== null) {
            return `Frame ${frame.frameIndex} of ${state.frames.length - 1} • ${minutesAgo} minutes ago`;
        } else {
            // Fall back to formatted date if minutes ago calculation fails
            const timeInfo = frame.absoluteObservationTime ? formatDate(frame.absoluteObservationTime) : '';
            return `Frame ${frame.frameIndex} of ${state.frames.length - 1}${timeInfo ? ' • ' + timeInfo : ''}`;
        }
    }
}

/**
 * Show specific frame
 */
export function showFrame(index) {
    if (index < 0 || index >= state.frames.length) {
        console.error(`showFrame: Invalid index ${index}, frames.length = ${state.frames.length}`);
        return;
    }
    
    if (!state.frames[index]) {
        console.error(`showFrame: Frame at index ${index} is undefined`);
        return;
    }
    
    state.currentFrameIndex = index;
    const frame = state.frames[index];
    
    // Update slider info
    updateFrameSliderInfo(index);
    
    // Update active button state
    document.querySelectorAll('.frame-btn').forEach(btn => {
        btn.classList.remove('active');
        if (parseInt(btn.getAttribute('data-frame')) === index) {
            btn.classList.add('active');
        }
    });
    
    // Update image
    const imgEl = document.getElementById('radar-image');
    const loadingEl = document.getElementById('loading');
    const altText = buildFrameAltText(frame, index);
    
    // Check if image is already loaded (from preload)
    const preloadedImg = new Image();
    preloadedImg.onload = () => {
        if (imgEl) {
            imgEl.src = frame.imageUrl;
            imgEl.alt = altText;
        }
        if (loadingEl) loadingEl.style.display = 'none';
        if (imgEl) imgEl.style.display = 'block';
    };
    preloadedImg.onerror = () => {
        if (imgEl) {
            imgEl.src = frame.imageUrl;
            imgEl.alt = altText;
        }
    };
    preloadedImg.src = frame.imageUrl;
    
    // Update frame info
    const frameInfoEl = document.getElementById('frame-info');
    if (frameInfoEl) {
        frameInfoEl.textContent = buildFrameInfoText(frame, index);
    }
}

/**
 * Find frame to show after refresh (preserves position)
 */
export function findFrameToShowAfterRefresh() {
    if (state.frames.length === 0) return 0;

    const previousFrames = Array.isArray(state.previousFrames) ? state.previousFrames : [];
    let previousFrameIndex = Number.isInteger(state.previousFrameIndex)
        ? state.previousFrameIndex
        : state.currentFrameIndex;

    if (previousFrames.length === 0) {
        // First load (or recovery after an empty replacement): show a valid index.
        const currentIndex = Number.isInteger(state.currentFrameIndex) ? state.currentFrameIndex : 0;
        return Math.max(0, Math.min(state.frames.length - 1, currentIndex));
    }

    // A shorter replacement must still resolve a frame from the previous collection.
    previousFrameIndex = Math.max(0, Math.min(previousFrames.length - 1, previousFrameIndex));
    const previousFrame = previousFrames[previousFrameIndex];
    if (!previousFrame) return 0;
    
    if (previousFrame.absoluteObservationTime) {
        // Find frame with same absolute observation time
        const matchingIndex = state.frames.findIndex(f => 
            f.absoluteObservationTime === previousFrame.absoluteObservationTime
        );
        if (matchingIndex >= 0) return matchingIndex;
        
        // Find closest frame by time. Start at the clamped previous index so a
        // replacement without usable timestamps still lands on a valid frame.
        const previousTime = new Date(previousFrame.absoluteObservationTime).getTime();
        let closestIndex = Math.max(0, Math.min(state.frames.length - 1, previousFrameIndex));
        let closestDiff = Infinity;
        state.frames.forEach((f, idx) => {
            if (f.absoluteObservationTime) {
                const diff = Math.abs(new Date(f.absoluteObservationTime).getTime() - previousTime);
                if (diff < closestDiff) {
                    closestDiff = diff;
                    closestIndex = idx;
                }
            }
        });
        return closestIndex;
    }
    
    // Fallback: clamp the previous index to the shorter replacement collection.
    return Math.max(0, Math.min(state.frames.length - 1, previousFrameIndex));
}

