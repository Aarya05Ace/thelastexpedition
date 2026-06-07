#!/usr/bin/env bash
# THE LAST EXPEDITION - host a co-op session on your LAN.
# Run this, then open the game. Keep this window open during the demo.
set -u

echo "Starting the co-op server..."

# Clean restart so it binds the LAN and the module is fresh.
# (World data lives in the local data dir, so this is safe.)
pkill -f "spacetime start" 2>/dev/null
sleep 1

# spacetime start defaults to 0.0.0.0:3000, so it is reachable on your LAN.
spacetime start > /tmp/lastexpedition-server.log 2>&1 &

# Wait until it accepts connections.
for i in $(seq 1 40); do
  if nc -z 127.0.0.1 3000 2>/dev/null; then break; fi
  sleep 0.5
done

# Publish the game module to the local server.
spacetime publish --server local lost-expedition >> /tmp/lastexpedition-server.log 2>&1

# Make sure the host's own client connects to its own server.
HOSTDIR="$HOME/Library/Application Support/DefaultCompany/My project"
mkdir -p "$HOSTDIR"
printf 'ws://localhost:3000\n' > "$HOSTDIR/server.txt"

IP=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null)

echo ""
echo "============================================================"
echo "  SERVER IS UP. You are the host."
echo ""
echo "  Your teammate runs ONE command:"
echo ""
echo "      ./join.sh $IP"
echo ""
echo "  Then you BOTH open the game. Keep this window open."
echo "  (If your friend cannot connect: System Settings >"
echo "   Network > Firewall, turn it off for the demo.)"
echo "============================================================"
echo ""

# Keep the server alive while this window stays open.
wait
