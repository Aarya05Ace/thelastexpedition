# Credits & Licenses

TOMB RUSH converges free, professional, pre-made pieces. Original work here is the
SpacetimeDB game logic, the React/R3F game layer, and the LLM Tomb Keeper service.

## Code / engines
- **vibe-coding-starter-pack-3d-multiplayer** — Majid Manzarpour — **MIT**. The
  SpacetimeDB multiplayer netcode core (player sync, client prediction, FBX hero
  rendering) this project is built on. License retained in `LICENSE`.
- **SpacetimeDB** (CLI + Rust module SDK + TS client SDK) — Clockwork Labs.
- **React Three Fiber**, **@react-three/drei**, **@react-three/postprocessing** — pmndrs — **MIT**.
- **three.js** — **MIT**.
- **@anthropic-ai/sdk** — Anthropic — the Tomb Keeper's brain (`claude-opus-4-8`).

## Assets actually shipped
- **Kenney "Nature Kit"** — Kenney.nl — **CC0** (no attribution required). The forest
  environment: low-poly trees (pines/oak), bushes, grass, mushrooms, wildflowers, logs,
  rocks (`client/public/models/forest/kenney/`). Vertex-colored GLBs, GPU-instanced.
- **Poly Haven** — **CC0**: `forest_grove` HDRI (2k) for sky + image-based lighting,
  and `forest_ground_04` PBR texture for the forest floor (`client/public/hdri/`,
  `client/public/textures/forest/`).
- **Character models** (`paladin`, `wizard` FBX + animations) — bundled with the
  majid starter pack.
- Relics, bank gates, traps, the cursed-sun, and Keeper-effect auras — **procedural**
  (emissive Three.js geometry + bloom / god-rays), no external assets.

> The previous Egyptian tomb-temple environment (Polygonal Mind "Tomb Chaser" CC0 GLBs
> in `client/public/models/tomb/`, the `dikhololo_night` HDRI, and `TombRuins.tsx` /
> `HauntedForest.tsx`) is no longer rendered. Those files can be deleted to slim the repo.

## Voice
- **Web Speech API** (`window.speechSynthesis`) — browser built-in, free. Speaks the
  Tomb Keeper's lines.

## Noted but NOT shipped
- **KayKit** (Kay Lousberg, CC0) dungeon + adventurer GLBs were the intended art swap
  but are itch.io download-gated; the build uses the starter's FBX heroes + procedural
  props instead. Drop KayKit GLBs into `client/public/` to upgrade — no license
  obligation (CC0).

No CC-BY assets are used; nothing here requires attribution beyond the MIT license
files, which are retained.
