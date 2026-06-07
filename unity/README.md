# TOMB RUSH — Unity client (PHOTOREAL, HDRP desktop) + SpacetimeDB

The **Unity** front-end for TOMB RUSH. It connects to the **same Rust SpacetimeDB
server** (`../server`) and the **same Node LLM Keeper** (`../keeper`) as before — only the
client changes. Target: **photoreal HDRP**, shipped as a **desktop build (Win/Mac)**.

> Why desktop, not browser: **HDRP (photoreal) does not run in Unity WebGL.** WebGL is
> URP-only and can't do photoreal. So photoreal = a desktop build judges download & run.
> (If you ever want the no-install URL demo back, that's a separate URP build.)

> Honest status: these C# scripts were authored outside the editor and **need a compile
> pass in Unity** (see ⚠️ notes). The hard part — the server, persistence, the AI Keeper —
> is done and reused; this is the client shell + SpacetimeDB wiring + your photoreal scene.

## What's here
```
Packages/manifest.json     SpacetimeDB C# SDK + HDRP + Cinemachine + TMP
Assets/Scripts/
  GameManager.cs           connection, subscriptions, RegisterPlayer, FrameTick
  NetworkedWorld.cs        spawns/syncs GameObjects from Player/Relic/BankGate tables
  LocalPlayer.cs           WASD @20Hz, camera follow, F-shove, auto pickup/bank
  autogen/                 C# bindings generated from the server (9 tables, 15 reducers)
```

## Prerequisites
- **Unity 6 (6000.x)** via Unity Hub, with the **HDRP** + **Windows/Mac Build Support** modules.
- SpacetimeDB server running + published (from repo root): `./run-local.sh`
- Keeper running (optional, it's the magic): `cd keeper && npm start`

## The photoreal environment (the whole point) — pick ONE
1. **Book of the Dead: Environment** (Unity Technologies, **FREE**) — a complete photoreal
   forest scene (Megascans + photogrammetry, HDRP). Fastest path to "big + photoreal."
   Import it into an **empty HDRP project** (per its docs), then add our scripts on top.
   ⚠️ It's an older demo — Unity may prompt to upgrade HDRP/materials; let it, and fix any
   pink (missing-shader) materials by reassigning the HDRP/Lit shader.
2. **Quixel Megascans via Fab** (free with an Epic account) — photoreal trees/rocks/ground;
   build a big forest on a Unity Terrain. More assembly, but current + robust.
3. **NatureManufacture free packs** (e.g. Mountain Trees: Dynamic Nature) — photoreal
   conifers, scatter on a terrain.

Place the playable area at world origin; **gates ring out to r≈210**, so the terrain/scene
must cover ≥ ~250-unit radius. Set up an **HDRP Volume** (exposure, fog, bloom, GI, shadows)
for the cinematic grade — that's where the photoreal feel comes from.

## Setup
1. **Create a new HDRP project** in Unity Hub (or open this `unity/` folder — it pulls the
   SpacetimeDB SDK + HDRP from `manifest.json`). If you start from the Book of the Dead
   project, copy `Assets/Scripts/` (incl. `autogen/`) + the SDK package into it instead.
2. **Regenerate bindings** when the server schema changes (from repo root):
   ```bash
   cd server && spacetime generate --lang csharp --out-dir ../unity/Assets/Scripts/autogen --module-path . -y
   ```
3. **Scene**: in your photoreal forest scene, add an empty **"Game"** object →
   `GameManager` + `NetworkedWorld`.
   - `GameManager`: set **Server Uri** (`ws://localhost:3000` local, or
     `wss://maincloud.spacetimedb.com`) + **Module Name** (`vibe-multiplayer`).
   - `NetworkedWorld`: assign 3 prefabs — a robber (your character), a glowing relic, and a
     gate marker. Use **HDRP/Lit** materials with **Emission** on the relic/gate so they pop.
4. **Press Play** → connects, auto-joins, players/relics/gates spawn + sync on the terrain.

## Desktop build (the demo)
*File ▸ Build Settings ▸ Platform: Windows or macOS ▸ Build.* Ship the build folder /
installer. Point `GameManager.serverUri` at **maincloud** so two laptops join the same
world. (For a no-install URL fallback later, make a separate URP build — not photoreal.)

## ⚠️ VERIFY on first compile (SDK-version sensitive — 1-line fixes)
- `GameManager`: `WithModuleName(...)` + `Conn.FrameTick()`. If your SDK errors, they're
  `WithDatabaseName(...)` / `Conn.Update()`.
- `LocalPlayer`: `new InputState(...)` arg order must match `autogen/Types/InputState.g.cs`
  (forward, backward, left, right, sprint, jump, attack, castSpell, sequence). If it's an
  object-initializer type, switch to `new InputState { Forward = f, ... }`.
- `NetworkedWorld.StyleRelic` sets emission — HDRP/Lit uses `_EmissiveColor` (URP uses
  `_EmissionColor`); the script tries both.

## Credits
Server netcode core: Vibe Coding Starter (MIT). Engine: Unity (HDRP). Environment: Book of
the Dead / Megascans / NatureManufacture per their licenses — record them in `../CREDITS.md`.
