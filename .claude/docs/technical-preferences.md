# Technical Preferences — TOMB RUSH

<!-- Hand-populated for TOMB RUSH's web stack (the kit's /setup-engine assumes a
     native engine; this project does not use one). All agents read this file. -->

## Engine & Language

- **"Engine"**: There is **no Unity / Unreal / Godot**. The stack is a web game:
  - **Backend (source of truth):** SpacetimeDB 2.3 **Rust** module (compiled to WASM) — `server/`
  - **Client:** React 19 + **React Three Fiber** (Three.js) + Vite — `client/`
  - **AI director:** Node "Tomb Keeper" service (privileged STDB client + Claude) — `keeper/`
- **Languages**: TypeScript (client + keeper, `strict`), Rust (server module).
- **Rendering**: Three.js via R3F. Postprocessing `EffectComposer` (Bloom + Vignette).
  Glow is done with **emissive materials + the Bloom pass**, NOT per-entity lights (perf).
- **Physics**: None. No rapier/ecctrl. All interactions are **server-authoritative
  proximity checks** (pickup/shove/bank/trap) using squared-distance vs. range constants.

## Input & Platform

- **Target Platforms**: Web (desktop Chrome/Edge/Firefox). Demo: local dev + SpacetimeDB maincloud.
- **Input Methods**: Keyboard + Mouse only. WASD move, Shift sprint, mouse-look (pointer lock),
  **F** = shove, **M** = mute Keeper, **R** = raid again. Grab/bank are automatic (proximity).
- **Gamepad / Touch**: None.

## Naming Conventions

- **TS**: `camelCase` vars/functions, `PascalCase` React components (`.tsx`), `UPPER_SNAKE` consts.
- **Rust**: `snake_case` fns/fields, `PascalCase` types.
- **SpacetimeDB bridge (critical):** reducers are `snake_case` **on the wire** but the client
  binding is a **camelCase single-object arg** — `register_player` ⇒ `conn.reducers.registerPlayer({ username, characterClass })`.
  Tables are property access by `snake_case` accessor (`conn.db.bank_gate`). A no-arg reducer
  still needs `({})`. `u64`/`i64` args are **bigint** (`{ seconds: 12n }`); `i64` columns read back as bigint.

## Performance Budgets

- **Target Framerate**: 60 fps in-browser with 2–6 players.
- **Render**: `dpr={[1, 1.5]}`, `antialias: false`, `powerPreference: high-performance`,
  ACES tone mapping. Bloom uses `mipmapBlur`. **No per-relic / per-gate real lights** — emissive only.
  Carrier glow is the one allowed `pointLight` (one per carrier, `castShadow={false}`).
- **Netcode**: client sends input at **20 Hz**; server advances a **1 Hz** tick clock (`game.now_tick`).
  All durations/effects are expressed in integer **ticks (seconds)**, never wall-clock micros.

## Build / Test / Deploy

```bash
# local: server + bindings + client + keeper
./run-local.sh                                  # publish module to local, regen TS bindings
cd client && npm run dev                        # http://localhost:5173+ (open 2+ tabs)
cd keeper && npm start                          # rule-based unless ANTHROPIC_API_KEY set

# tests
cd client && npx tsx src/itest.ts               # end-to-end heist loop (14 assertions)
cd client && npx tsc -b && npx vite build       # typecheck + prod build gate
cd client && npm run simulate -- 5 12           # netcode load (bots)

# schema change → must wipe
cd server && spacetime publish --server local vibe-multiplayer --delete-data -y

# maincloud (judges, no install): publish then point client/keeper via env
cd server && spacetime publish --server maincloud tombrush-<unique>
# client: VITE_STDB_HOST=wss://maincloud.spacetimedb.com VITE_STDB_DB=tombrush-<unique>
# keeper: STDB_HOST / STDB_DB env to match
```

## SpacetimeDB 2.3 gotchas (so agents give correct advice)

- **`--server local` is NOT the default** — maincloud is; always pass `--server local` locally.
- **No `ORDER BY`** in `spacetime sql`. **`position` is a reserved word** — quote it or `SELECT *`.
- Private tables are the default (omit `public`); public tables sync **whole rows** — never put
  secrets on them. RLS join columns need **btree indexes**. DB owner **bypasses RLS**.
- Generated bindings use extensionless imports → run scripts with **`tsx`**, not `node`.
- WASM reducers **cannot make outbound HTTP** — that's why the Keeper is an external Node client.

## Testing posture for this project (hackathon)

- The established test is `client/src/itest.ts` (real STDB clients driving the loop). Keep it green.
- Logic (server reducers) → assert via itest. Visual/feel (auras, bloom) → screenshot + eyeball.
- Verification-driven: typecheck + `vite build` + itest must pass before calling work done.
