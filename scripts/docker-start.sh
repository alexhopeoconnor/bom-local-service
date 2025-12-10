#!/bin/bash
set -e

# Function to start Xvfb
start_xvfb() {
    if ! pgrep -f "Xvfb :99" > /dev/null; then
        echo "Starting Xvfb on display :99..."
        Xvfb :99 -screen 0 1920x1080x24 -ac +extension GLX +render -noreset > /dev/null 2>&1 &
        sleep 1
        if ! pgrep -f "Xvfb :99" > /dev/null; then
            echo "ERROR: Xvfb failed to start" >&2
            return 1
        fi
        echo "Xvfb started successfully"
    fi
    return 0
}

# Function to monitor and restart Xvfb if it dies (runs as background process)
monitor_xvfb() {
    # Monitor while PID 1 (dotnet after exec) is still running
    while kill -0 1 2>/dev/null; do
        sleep 5
        if ! pgrep -f "Xvfb :99" > /dev/null; then
            echo "WARNING: Xvfb process not found, restarting..." >&2
            start_xvfb || true
        fi
    done
}

# Cleanup function
cleanup() {
    echo "Shutting down Xvfb..."
    pkill -f "Xvfb :99" || true
}

# Set up signal handlers for cleanup
trap cleanup SIGTERM SIGINT EXIT

# Start Xvfb
start_xvfb || exit 1

# Export DISPLAY
export DISPLAY=:99

# Start monitoring in background (will check if PID 1 is still running)
monitor_xvfb &

# Start the .NET application (replaces shell as PID 1)
exec dotnet BomLocalService.dll
