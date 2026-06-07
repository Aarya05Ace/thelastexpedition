#!/bin/bash
# TOMB RUSH — one-shot local (re)deploy: start the local SpacetimeDB server if needed,
# publish the module, and regenerate the client + keeper bindings.
set -e
source "$HOME/.cargo/env" 2>/dev/null || true
ROOT="$(cd "$(dirname "$0")" && pwd)"

echo "→ checking local SpacetimeDB server..."
if ! curl -s -m 2 http://127.0.0.1:3000/v1/ping >/dev/null 2>&1; then
  echo "  starting spacetime (logs → /tmp/spacetime-server.log)"
  (cd "$ROOT/server" && nohup spacetime start >/tmp/spacetime-server.log 2>&1 &)
  for i in $(seq 1 15); do
    curl -s -m 2 http://127.0.0.1:3000/v1/ping >/dev/null 2>&1 && break
    sleep 1
  done
fi
echo "  server up."

echo "→ publishing module to local..."
cd "$ROOT/server"
spacetime publish --server local vibe-multiplayer -y

echo "→ regenerating bindings (client + keeper)..."
spacetime generate --lang typescript --out-dir ../client/src/generated --module-path . -y
spacetime generate --lang typescript --out-dir ../keeper/src/generated --module-path . -y

cat <<EOF

✅ Module published & bindings generated.

Next, in separate terminals:
  cd client && npm install && npm run dev     # → http://localhost:5173 (open 2+ tabs)
  cd keeper && npm install && npm start       # the Tomb Keeper (set ANTHROPIC_API_KEY in keeper/.env)

Tip: if you changed table columns, re-run with a wipe:
  (cd server && spacetime publish --server local vibe-multiplayer --delete-data -y)
EOF
