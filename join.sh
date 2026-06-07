#!/usr/bin/env bash
# THE LAST EXPEDITION - join your host's co-op session.
# Usage: ./join.sh <host-ip>     (the host's window prints the exact command)
HOST="${1:-}"
if [ -z "$HOST" ]; then
  echo "Usage: ./join.sh <host-ip>   (example: ./join.sh 192.168.1.50)"
  exit 1
fi

case "$HOST" in
  ws://*) URI="$HOST" ;;
  *)      URI="ws://$HOST:3000" ;;
esac

DIR="$HOME/Library/Application Support/DefaultCompany/My project"
mkdir -p "$DIR"
printf '%s\n' "$URI" > "$DIR/server.txt"

echo "Pointed at host: $URI"
echo "Now open the game. You will spawn into your host's world."
