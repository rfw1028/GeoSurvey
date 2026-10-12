#!/bin/bash
# Serial bridge: exposes serial port as a PTY via socat
# Usage: serial_bridge.sh /dev/ttyS0 115200

PORT="${1:-/dev/ttyS0}"
BAUD="${2:-115200}"
PTY_LINK="/tmp/gps_pty"

rm -f "$PTY_LINK"

# Configure serial port
stty -F "$PORT" "$BAUD" raw -echo cs8 -cstopb -parenb

# Use socat with explicit serial type (not OPEN:)
exec socat "${PORT},rawer,b${BAUD}" "PTY,rawer,link=${PTY_LINK},mode=666"
