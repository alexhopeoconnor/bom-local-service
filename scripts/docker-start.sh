#!/bin/bash
set -euo pipefail

# Function to cleanup stale Xvfb processes and lock files
cleanup_xvfb() {
    # Kill any stale Xvfb processes for display :99
    pkill -f "Xvfb :99" 2>/dev/null || true
    sleep 0.5
    
    # Remove stale lock files
    rm -f /tmp/.X99-lock /tmp/.X11-unix/X99 2>/dev/null || true
}

# Function to check if Xvfb is actually functional
check_xvfb_working() {
    # Check if process exists and is running (not zombie)
    local pid=$(pgrep -f "Xvfb :99" | head -n1)
    if [ -z "$pid" ]; then
        return 1
    fi
    
    # Check if process is actually running (not a zombie)
    if ! kill -0 "$pid" 2>/dev/null; then
        return 1
    fi
    
    return 0
}

# Function to start Xvfb
start_xvfb() {
    # Clean up any stale processes/locks first
    cleanup_xvfb
    
    if ! check_xvfb_working; then
        echo "Starting Xvfb on display :99..."
        Xvfb :99 -screen 0 1920x1080x24 -ac +extension GLX +render -noreset > /dev/null 2>&1 &
        sleep 1.5
        
        if check_xvfb_working; then
            echo "Xvfb started successfully"
            return 0
        else
            echo "ERROR: Xvfb failed to start" >&2
            return 1
        fi
    fi
    
    return 0
}

# Function to monitor and restart Xvfb if it dies (runs as background process)
monitor_xvfb() {
    local consecutive_failures=0
    local max_backoff=60  # Maximum backoff in seconds
    local backoff_seconds=5
    
    # Monitor while PID 1 (dotnet after exec) is still running
    while kill -0 1 2>/dev/null; do
        sleep $backoff_seconds
        
        if ! check_xvfb_working; then
            consecutive_failures=$((consecutive_failures + 1))
            
            if [ $consecutive_failures -eq 1 ]; then
                echo "WARNING: Xvfb not working, attempting restart..." >&2
            elif [ $consecutive_failures -le 3 ]; then
                echo "WARNING: Xvfb restart attempt $consecutive_failures..." >&2
            elif [ $((consecutive_failures % 10)) -eq 0 ]; then
                echo "ERROR: Xvfb has failed to start $consecutive_failures times. Display may be unavailable." >&2
            fi
            
            if start_xvfb; then
                consecutive_failures=0
                backoff_seconds=5
            else
                # Exponential backoff, capped at max_backoff
                backoff_seconds=$((backoff_seconds * 2))
                if [ $backoff_seconds -gt $max_backoff ]; then
                    backoff_seconds=$max_backoff
                fi
            fi
        else
            # Xvfb is working, reset failure counter
            if [ $consecutive_failures -gt 0 ]; then
                echo "Xvfb is now working" >&2
                consecutive_failures=0
                backoff_seconds=5
            fi
        fi
    done
}

# Cleanup function
cleanup() {
    echo "Shutting down Xvfb..."
    cleanup_xvfb
}

# Set up signal handlers for cleanup
trap cleanup SIGTERM SIGINT EXIT

# Start Xvfb
start_xvfb || exit 1

# Export DISPLAY
export DISPLAY=:99

# Start monitoring in background (will check if PID 1 is still running)
monitor_xvfb &

# Validate the runtime before starting; the image must include ASP.NET Core 10.
if ! dotnet --list-runtimes | grep -qE '^Microsoft\.AspNetCore\.App 10\.'; then
    echo "ERROR: Microsoft.AspNetCore.App 10.x is not installed" >&2
    dotnet --list-runtimes >&2 || true
    exit 1
fi

# Start the .NET application (replaces shell as PID 1). dotnet handles SIGTERM.
exec dotnet BomLocalService.dll "$@"
