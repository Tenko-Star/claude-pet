#!/bin/sh
# Forwards the hook JSON on stdin to StatusHub.Service.
# Usage: forward-hook.sh <EventName>
# Must never slow down or break Claude Code: bounded time, no output, always exit 0.

event="${1:-unknown}"
port="${CLAUDE_PLUGIN_OPTION_PORT:-47821}"

# 127.0.0.1 rather than localhost: the service binds IPv4 loopback only, and with WSL
# mirrored networking 127.0.0.1 inside WSL reaches the Windows host.
curl --silent --output /dev/null \
    --connect-timeout 0.2 --max-time 1 \
    --request POST \
    --header "Content-Type: application/json" \
    --data-binary @- \
    "http://127.0.0.1:${port}/hooks/${event}" >/dev/null 2>&1

exit 0
