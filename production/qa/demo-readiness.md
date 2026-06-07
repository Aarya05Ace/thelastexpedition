# Gate-Check: Demo Readiness — TOMB RUSH

> Applied the kit's `/gate-check` + `/launch-checklist` methodology, adapted from a
> commercial "Release" gate to a **hackathon demo** gate. Grounded in the actual
> codebase, not assumptions. Deadline: **SpacetimeDB Launchpad Hackathon, Sun Jun 7 2026 2pm.**

## VERDICT: ⚠️ CONCERNS — playable & impressive locally, but two P0s gate the *judged* demo

The core game is in great shape: server loop verified (`itest` 14/14), client builds clean,
runs in a real browser, and the Tomb Keeper's effects are now visible (curse/bless/stun/seal/
carry auras + HUD badges + screen tint). What stands between "works on my machine" and
"wins the prize" is **(1) the Keeper isn't actually running on Claude** and **(2) the
no-install judges-join path (maincloud) is down.** Both are fixable fast.

---

## P0 — Demo-breakers (must fix before judging)

### P0-1 · The Tomb Keeper is rule-based, not the LLM
- **Finding:** No `ANTHROPIC_API_KEY` set, so `keeper/` runs its rule-based fallback. The
  entire differentiator — and the **"Best Use of LLMs"** prize target — is the *LLM* Keeper.
- **Impact:** You'd be demoing the one thing that makes this special with the LLM turned off.
- **Fix:** `cp keeper/.env.example keeper/.env`, add `ANTHROPIC_API_KEY=...`, restart keeper.
  Then do **one real-key match end-to-end** (this was the open item from THRESHOLD too).
  Confirm taunts are dynamic/contextual (reference player names, standings) vs. the canned list.
- **Est:** 10 min · **Owner:** Dhruv (Keeper)

### P0-2 · maincloud deploy is down (no judges-join-from-any-laptop)
- **Finding:** `spacetime publish --server maincloud` fails `401 Invalid token: InvalidSignature`
  (stale token; local CLI 2.3.0 vs maincloud 2.4.1).
- **Impact:** Kills the "open a URL, no install, two judges join instantly" flow — a core part
  of the pitch and a big differentiator at a SpacetimeDB hackathon.
- **Fix:** `spacetime logout && spacetime login` (interactive), then
  `spacetime publish --server maincloud tombrush-<unique>`; run client/keeper with
  `VITE_STDB_HOST`/`STDB_HOST` = `wss://maincloud.spacetimedb.com` + the DB name.
- **Est:** 15 min · **Owner:** Aarya / Dhruv · **Status:** blocked on user re-auth

---

## P1 — Should-fix (impression & reliability)

### P1-1 · Keeper "wow moment" may not fire on cue in a short demo
- **Finding:** Keeper holds all disruptive actions for the first **20s**, then ~1 per 18s; the
  rule-based path only curses a leader at **score ≥ 2**. In a 2–3 min demo with few players,
  the dramatic "AI turns on the leader" beat can arrive late or not at all.
- **Fix options:** (a) for the demo, lower warmup to ~8s and cadence to ~10s in `keeper.ts`
  (`elapsed >= 20`, `>= 18`); (b) with a real key, the LLM escalates better — prompt it to act
  sooner; (c) scripted demo: have a primed 3-tab match where someone banks 2 fast.
- **Est:** 15 min · **Owner:** Dhruv

### P1-2 · Caught FBX `traverse of undefined` error spams the console
- **Finding:** Pre-existing **starter** bug in `client/src/components/Player.tsx` (~line 152)
  during embedded-light removal; caught, non-fatal, but noisy if a judge opens devtools.
- **Fix:** guard the `fbx.traverse(...)` call (null-check the group before traversing).
- **Est:** 5 min · **Owner:** Aarya/Sriyan

### P1-3 · ~~Dev DebugPanel visible in-game~~ ✅ FIXED this pass
- Gated behind `?debug` (App.tsx). Clean for judges; append `?debug` to the URL to bring it back.

---

## P2 — Nice-to-have (only if time)

- **Audio juice:** TTS Keeper voice is the only sound. A few SFX (pickup chime, bank success,
  shove thud, collapse rumble) would dramatically raise perceived polish. *(Owner: Mounish)*
- **`wasm-opt` not installed** → module published unoptimized. `brew install binaryen` then
  republish for a smaller/faster WASM. Minor.
- **Client bundle is one 1.46 MB chunk** → first-load delay on a cold cloud demo. Fine for a
  controlled demo; could code-split FBX/postprocessing later.
- **Round restart / ghost players:** disconnect cleanup works; just restart the server (or
  `--delete-data` publish) before the live demo to start from a pristine board.

---

## ✅ Already solid (do not re-touch before demo)
- Server loop server-authoritative + `itest` 14/14 · `tsc -b` + `vite build` clean.
- Keeper legibility shipped (auras + badges + tint) — verified rendering in a real browser.
- Cinematic title screen + themed join + dark tomb (fog/bloom/vignette) + winner banner.

---

## 🎬 3-minute demo runbook (the safe path to the wow moment)
1. **Before judges arrive:** fix P0-1 + P0-2; restart server clean; start client + keeper
   (real key); open the maincloud URL in 2–3 tabs/laptops; confirm a round is live.
2. **0:00–0:30** — "2–6 grave robbers, one collapsing tomb, and an AI Tomb Keeper that
   *watches the match and turns on whoever's winning*." Open the URL on a judge's laptop — **no install.**
3. **0:30–1:30** — Play: grab a relic (it **glows — you're a target**), bank it, **F** to shove
   a rival's loot loose. Get one player to bank 2.
4. **1:30–2:30** — The payoff: the **Keeper curses/seals the leader** — point at the purple
   aura + **CURSED / GATE SEALED** badges + screen tint + the spoken taunt. "That decision was
   made by Claude, live, from the match state — written back through SpacetimeDB reducers."
5. **2:30–3:00** — Collapse timer hits 0 → winner banner. Pitch the stack: everything shared —
   players, relics, the Keeper's moves — is real-time synced rows in SpacetimeDB; the LLM can't
   run in WASM, so it's a privileged external client. **R** to raid again.
