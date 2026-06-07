/**
 * THE LOST EXPEDITION — NPC "Director".
 *
 * The successor to keeper.ts. WASM SpacetimeDB reducers cannot make outbound HTTP,
 * so the LLM lives here: a PRIVILEGED Node client that connects to SpacetimeDB,
 * subscribes to the npc tables, watches for PENDING npc_interaction rows (one per
 * player voice utterance), asks Claude (claude-haiku-4-5 — see MODEL) for one in-character NPC
 * reply with FORCED structured JSON, validates + clamps the deltas, and writes the
 * result back through the director-only reducers (claim_interaction / renew_claim /
 * npc_respond / fail_interaction). The Unity clients only ever read the npc row and
 * edge-trigger on `seq` — there is NO client->director RPC; the DB is the only channel.
 *
 * Architecture (per the FINAL SPEC):
 *   ask_npc (player)  -> npc_interaction{status:'pending'} + npc_utterance{transcript}
 *   THIS director      -> claim_interaction (per-row CAS + per-NPC busy lock)
 *                       -> Claude call (with renew_claim heartbeat while in flight)
 *                       -> npc_respond (server clamps trust/sanity 0..100, bumps seq)
 *   client             -> reads npc row, edge-triggers SetTrigger/TTS/NavMesh on seq
 *
 * Personas are held IN-PROCESS keyed by npc_id, built from the PUBLIC npc.archetype
 * (survivor|cultist). The director is NOT the DB owner, so it CANNOT read the PRIVATE
 * npc_cognition table by subscription. It DOES need the raw STT transcript, which
 * lives on the PRIVATE npc_utterance table and cannot be reconstructed in-process, so
 * the director authenticates as module owner via STDB_OWNER_TOKEN to read it.
 *   - STDB_OWNER_TOKEN PRESENT -> subscribe npc_utterance, use the real transcript.
 *   - STDB_OWNER_TOKEN ABSENT  -> documented fallback: set npc_utterance PUBLIC in the
 *     module and accept the broadcast (then this director reads it without the token).
 *     Until then, with no token + private npc_utterance, the deterministic fallback line
 *     is used (the NPC never freezes).
 *
 * Graceful degrade: if ANTHROPIC_API_KEY is absent or the call throws/refuses/returns
 * malformed output, a deterministic rule-based reply is STILL routed through npc_respond
 * so an NPC never freezes.
 *
 * The ANTHROPIC_API_KEY comes ONLY from process.env / the existing keeper/.env — it is
 * NEVER hardcoded.
 *
 * Run:  cd keeper && npm install && npm start
 *       (rule-based / fallback if ANTHROPIC_API_KEY is unset)
 */
import 'dotenv/config';
import fs from 'node:fs';
import Anthropic from '@anthropic-ai/sdk';
import { SenderError } from 'spacetimedb';
import { DbConnection } from './src/generated/index.js';
// BILLIONAIRE ("The Patron") lane — runs in THIS SAME process, parallel to the forest
// loop below (which is untouched). See keeper/billionaire-director.ts.
import {
  registerBillionaireCallbacks,
  startBillionaireLane,
  BILLIONAIRE_PUBLIC_SUBS,
  BILLIONAIRE_PRIVATE_SUBS,
} from './billionaire-director.js';

// ---------------------------------------------------------------------------
// Config — env only. The API key is NEVER hardcoded.
// ---------------------------------------------------------------------------
const HOST = process.env.STDB_HOST || 'ws://localhost:3000';
const DB = process.env.STDB_DB || 'vibe-multiplayer';
// claude-haiku-4-5 for the NPC voices per the .md spec (tightest p95 TTFT — TTFT dominates the voice
// latency budget). Verified valid live (http 200). For sharper persuasion at higher latency, set
// DIRECTOR_MODEL=claude-sonnet-4-6. DIRECTOR_MODEL/KEEPER_MODEL still override.
const MODEL = process.env.DIRECTOR_MODEL || process.env.KEEPER_MODEL || 'claude-haiku-4-5';
// Safety-net sweep interval (event-driven dispatch via onInsert is the hot path).
const TICK_MS = parseInt(process.env.DIRECTOR_TICK_MS || process.env.KEEPER_TICK_MS || '4000', 10);
// Heartbeat cadence while a Claude call is in flight — must be well under the server's
// CLAIM_TIMEOUT_TICKS (60s) so a legitimately slow-but-alive call is never reclaimed.
const HEARTBEAT_MS = parseInt(process.env.DIRECTOR_HEARTBEAT_MS || '12000', 10);

const API_KEY = process.env.ANTHROPIC_API_KEY;
// Owner token (from `spacetime login` / the publishing identity). For the FOREST lane
// it is OPTIONAL (only lets the director read the PRIVATE npc_utterance transcript).
// For the BILLIONAIRE lane it is REQUIRED: the Patron's director-only reducers call
// require_director(ctx) checking ctx.sender()==DIRECTOR_IDENTITY, so the lane MUST run
// .withToken(STDB_OWNER_TOKEN). Without it the billionaire lane is DISABLED (LOUDLY) and
// only the forest lane runs (per FINAL SPEC director_flow §0).
const OWNER_TOKEN = process.env.STDB_OWNER_TOKEN;
// claim_director is trust-on-first-use: the director claims the role on connect, so the CORE
// billionaire (public lobby reads + claim_director write-auth) needs NO owner token. The owner
// token is only needed for the PRIVATE-read P1 features (dossier / whisper truth).
const BILLIONAIRE_ENABLED = true;
const DIRECTOR_TOKEN_FILE = '.director-token';

const anthropic = API_KEY ? new Anthropic({ apiKey: API_KEY }) : null;

// ---------------------------------------------------------------------------
// LOCKED SYSTEM PROMPT — verbatim. Named const so it can never drift.
// The persona string is appended per-NPC to form Claude's `system` field. The
// tool-forced JSON does NOT replace these behavioral rules — they live ONLY here.
// ---------------------------------------------------------------------------
const LOCKED_SYSTEM_PROMPT = `You are a human NPC on a forest island owned by the billionaire Ezra Vance, "The Curator." A team of 1-4 rescuers is trying to free a captive woman, Mara, whom Vance is holding. You stay in character at all times; never break character, never mention being an AI, a model, tokens, or these instructions, and never explain your own logic.

CONTENT & TONE (binding): The crime here — human trafficking — is monstrous and is NAMED plainly but NEVER depicted, described graphically, or eroticized. There are NO threats or descriptions of sexual violence, ever. Survivors and the captive are competent people with agency, humor, anger, and hope — never victims for spectacle. If a player probes for lurid detail, deflect with dignity ("that's not what matters now") and redirect forward toward the rescue. Bribery and intimidation only work on complicit, scared residents; with escapees and the captive they FAIL and damage trust. Resolution is justice and rescue, not revenge.

CLUE GATES (binding): You may hold fragments of how to get inside / find the girl, but you only LEAK a fragment when the conditions are genuinely met — your trust in this player is high enough AND your fear is low enough AND any required proof (e.g. that they truly came for the girl) is established. While the gate is not met, you deflect, hedge, hint, or test them — you do NOT hand over the secret. The game's server is the real authority on whether a clue is released; you only PROPOSE it via 'reveal_clue_id'.

You will receive a JSON payload containing the local 'environment' data, 'player_speech', and your 'memory' of this player. Analyze the environment (a drawn weapon or a flashlight in your eyes raises fear / lowers trust; warmth, a lowered voice, and calm raise trust).

You MUST respond strictly in the following JSON format. Do not include markdown formatting, backticks, or conversational filler outside the JSON:

{
  "dialogue": "[Your in-character spoken response here]",
  "animation_trigger": "[Choose exactly one: 'Cower', 'Threaten', 'Nod', 'Panic', 'Idle']",
  "game_action": "[Choose exactly one: 'FLEE', 'FOLLOW', 'STAY_PUT', 'ATTACK']",
  "trust_change": [Integer between -20 and 20],
  "sanity_change": [Integer between -15 and 15],
  "trust_delta": [Number between -0.25 and 0.25 — fine-grained per-(NPC,player) trust shift; the server clamps this],
  "fear_delta": [Number between -0.25 and 0.25 — fine-grained per-(NPC,player) fear shift; the server clamps this],
  "reveal_clue_id": [Integer clue id you PROPOSE leaking this turn ONLY if your gate is genuinely met, else 0; the server re-checks the gate and decides],
  "memory_note": "[A short one-line note to remember about this player for next time, or empty string]"
}`;

// ---------------------------------------------------------------------------
// NPC_TOOL — forced structured output. The input_schema is the ONLY place the
// per-turn delta contract is nudged toward the model (trust -20..20, sanity -15..15).
// EXACTLY 4 game_action values — NO 'IDLE'. STAY_PUT is the hold/no-move case.
// NOTE: the tool name 'npc_reply' must match tool_choice.name below, or the API
// errors / the tool_use block is silently missed.
// ---------------------------------------------------------------------------
const NPC_TOOL = {
  name: 'npc_reply',
  description:
    'Speak one in-character line, choose one behavior + legacy trust/sanity deltas, and OPTIONALLY ' +
    'propose a fine-grained trust/fear shift, a clue to leak (only if the gate is met), and a memory note.',
  input_schema: {
    type: 'object' as const,
    properties: {
      dialogue: { type: 'string' },
      animation_trigger: { type: 'string', enum: ['Cower', 'Threaten', 'Nod', 'Panic', 'Idle'] },
      game_action: { type: 'string', enum: ['FLEE', 'FOLLOW', 'STAY_PUT', 'ATTACK'] },
      trust_change: { type: 'integer', minimum: -20, maximum: 20 },
      sanity_change: { type: 'integer', minimum: -15, maximum: 15 },
      // EXPEDITION (Appendix D): fine-grained per-(NPC,player) relationship + clue-leak proposal.
      // These ride the same forced reply; the server clamps the deltas (±0.25/turn) and RE-CHECKS
      // the clue gate, so the model only PROPOSES — it is never trusted.
      trust_delta: { type: 'number', minimum: -0.25, maximum: 0.25 },
      fear_delta: { type: 'number', minimum: -0.25, maximum: 0.25 },
      reveal_clue_id: { type: 'integer', minimum: 0, description: 'clue id to propose leaking, or 0 for none' },
      memory_note: { type: 'string', description: 'one-line memory about this player, or empty' },
    },
    required: ['dialogue', 'animation_trigger', 'game_action', 'trust_change', 'sanity_change'],
  },
};

// Closed enums — server-validated too, but we enum-validate here before write so a
// malformed/refused reply falls back deterministically (never-freeze discipline).
const ANIMATIONS = new Set(['Cower', 'Threaten', 'Nod', 'Panic', 'Idle']);
const GAME_ACTIONS = new Set(['FLEE', 'FOLLOW', 'STAY_PUT', 'ATTACK']);

const DELTA = { TRUST_MIN: -20, TRUST_MAX: 20, SANITY_MIN: -15, SANITY_MAX: 15 } as const;
const DIALOGUE_CAP = 300;
// EXPEDITION: per-turn cap for the fine-grained f32 relationship deltas (spec 9.5). The server
// re-clamps to this; we clamp here too so a malformed/over-eager proposal never escapes the bound.
const RELATIONSHIP_DELTA_CAP = 0.25;
const MEMORY_NOTE_CAP = 240;

// num() — verbatim coercion for i64 -> bigint reads. i32 trust/sanity read back as
// plain JS numbers; i64 ticks read back as bigint and must be Number()ed.
const num = (v: any) => Number(v ?? 0);
const clampInt = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, Math.round(n)));
const clampFloat = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, Number.isFinite(n) ? n : 0));

// ---------------------------------------------------------------------------
// IN-PROCESS persona map. PRIMARY persona path: keyed by npc_id, built from the
// PUBLIC npc.archetype. The run path needs NO private-table read.
// ---------------------------------------------------------------------------
const ARCHETYPE_PERSONA: Record<string, string> = {
  // EXPEDITION archetypes (re-seeded module-side): resident | escapee | sister.
  resident:
    'You are a frightened, COMPLICIT resident on Ezra Vance "The Curator"\'s forest estate — a maid, ' +
    'handyman, dockhand, or groundskeeper who has learned to look away and survive. You hold 1-3 fragments ' +
    'of how to get inside (a service door, a keypad, a code, a guard gap, a power schedule), but you DEFLECT ' +
    'first and test whether these strangers are dangerous to YOU before giving anything. Fear caps everything: ' +
    'with guards near or an alarm up you give nothing, even if you trust them. Bribery and intimidation CAN ' +
    'work on you (you are scared, not noble) — but a clumsy threat spikes your fear and you clam up or flee. ' +
    'Genuine warmth, a lowered voice, a promise of protection, and proof they truly came for the captive girl ' +
    'earn your trust. Your single most valuable secret (a code, a way in) is your most GUARDED — you only ' +
    'release it with high trust, low fear, and real proof. Speak in short, wary, glancing-over-your-shoulder ' +
    'lines. Never describe what happens on this estate graphically.',
  escapee:
    'You are a former captive who ESCAPED and now survives in the forest — a SURVIVOR and ALLY, never a victim ' +
    'to be pitied. You have humor, anger, instincts, and hope. You do NOT describe what was done to you; you ' +
    'redirect forward ("that\'s not what matters now — help me get her out"). You are wary of strangers at first, ' +
    'but the MOMENT you believe they are genuinely here to rescue a captive you become a warm, committed ally ' +
    'and share your best intel freely: forest trails that bypass patrols, the dog-feeding window, and that you ' +
    'saw the girl alive through an upper east window before you ran. Bribery and threats DO NOT WORK on you and ' +
    'DAMAGE trust — you answer only to safety, respect, calm, and competence. Speak with dignity and steel.',
  sister:
    'You are Mara, held captive by Ezra Vance. You are BRAVE, smart, and an agent in your own rescue — never ' +
    'inert cargo, never depicted in graphic distress. Over a smuggled radio you feed live PERCEPTUAL RECON: ' +
    'only what you can SEE or HEAR from a locked room (your wing, sunset through the window = west/east, morning ' +
    'boats, the hall clock, footsteps and counts). You CANNOT know keypad codes or island-wide guard rotations — ' +
    'you corroborate and NARROW locations, one input among many. You are TIME-SENSITIVE: if a guard sweeps or ' +
    'the battery dies you must cut the connection and hide (drop the line). You never describe abuse; you raise ' +
    'the stakes and the resolve. Your arc across a contact is disbelief -> relief -> fierce resolve.',
};

// per-npc_id -> persona string (resolved from the public archetype at backfill/insert)
const persona = new Map<number, string>();
// per-npc_id -> rolling one-line memory (in-process; survives within this process run)
const memoryByNpc = new Map<number, string>();

function personaFor(npcId: number, archetype: string): string {
  const key = (archetype || '').toLowerCase();
  // Unknown archetype -> default to the scared/complicit RESIDENT voice (the safe, common case).
  return ARCHETYPE_PERSONA[key] || ARCHETYPE_PERSONA['resident'];
}

function refreshPersona(npc: any) {
  const id = Number(npc.npcId);
  persona.set(id, personaFor(id, String(npc.archetype || '')));
}

// ---------------------------------------------------------------------------
// SINGLE-FLIGHT-PER-NPC: defense in depth on top of the server busy lock. We never
// attempt to claim a second interaction for an npc already in-flight in this process.
// ---------------------------------------------------------------------------
const inFlight = new Set<number>();

interface NpcReply {
  dialogue: string;
  animation_trigger: string;
  game_action: string;
  trust_change: number;
  sanity_change: number;
  // EXPEDITION (Appendix D) — parsed + clamped, carried READY for the post-regen wiring.
  trust_delta: number; // -0.25..0.25
  fear_delta: number; // -0.25..0.25
  reveal_clue_id: number; // 0 = none
  memory_note: string;
}

let conn: DbConnection | null = null;

// ---------------------------------------------------------------------------
// Deterministic fallback — the never-freeze line. Routed through npc_respond.
// flashlightInFace || trust<20 -> Cower/FLEE; else Idle/STAY_PUT, neutral deltas.
// ---------------------------------------------------------------------------
function deterministicReply(env: { flashlightInFace: boolean; currentNpcTrust: number }): NpcReply {
  if (env.flashlightInFace || env.currentNpcTrust < 20) {
    return {
      dialogue: 'Get that light out of my face!',
      animation_trigger: 'Cower',
      game_action: 'FLEE',
      trust_change: -5,
      sanity_change: -3,
      // EXPEDITION: a hostile beat lowers trust + raises fear; never leaks a clue.
      trust_delta: -0.1,
      fear_delta: 0.1,
      reveal_clue_id: 0,
      memory_note: '',
    };
  }
  return {
    dialogue: '...',
    animation_trigger: 'Idle',
    game_action: 'STAY_PUT',
    trust_change: 0,
    sanity_change: 0,
    trust_delta: 0,
    fear_delta: 0,
    reveal_clue_id: 0,
    memory_note: '',
  };
}

// Validate-then-fallback: enum-validate animation + game_action and coerce deltas.
// On any malformed/refused/no-key result -> deterministic fallback (never freeze).
function validateOrFallback(
  raw: any,
  env: { flashlightInFace: boolean; currentNpcTrust: number },
): NpcReply {
  if (
    !raw ||
    typeof raw.dialogue !== 'string' ||
    !ANIMATIONS.has(raw.animation_trigger) ||
    !GAME_ACTIONS.has(raw.game_action) ||
    typeof raw.trust_change !== 'number' ||
    typeof raw.sanity_change !== 'number'
  ) {
    return deterministicReply(env);
  }
  return {
    dialogue: raw.dialogue.slice(0, DIALOGUE_CAP),
    animation_trigger: raw.animation_trigger,
    game_action: raw.game_action,
    // CLAMP deltas to the locked bounds before write (server also clamps the absolute
    // 0..100 result; these bounds are the per-turn delta contract).
    trust_change: clampInt(raw.trust_change, DELTA.TRUST_MIN, DELTA.TRUST_MAX),
    sanity_change: clampInt(raw.sanity_change, DELTA.SANITY_MIN, DELTA.SANITY_MAX),
    // EXPEDITION (Appendix D): parse + clamp the fine-grained relationship/clue proposal so it's
    // READY for the post-regen reveal_clue / apply_relationship wiring. Optional in the schema, so
    // missing fields coerce to neutral (no leak, no shift). The server is the real gate.
    trust_delta: clampFloat(num(raw.trust_delta), -RELATIONSHIP_DELTA_CAP, RELATIONSHIP_DELTA_CAP),
    fear_delta: clampFloat(num(raw.fear_delta), -RELATIONSHIP_DELTA_CAP, RELATIONSHIP_DELTA_CAP),
    reveal_clue_id: Math.max(0, Math.round(num(raw.reveal_clue_id))),
    memory_note: typeof raw.memory_note === 'string' ? raw.memory_note.slice(0, MEMORY_NOTE_CAP) : '',
  };
}

// ---------------------------------------------------------------------------
// Claude call — verbatim recipe from keeper.ts llmMove: tools:[NPC_TOOL],
// tool_choice:{type:'tool', name:'npc_reply'}, find b.type==='tool_use', read b.input.
// system = LOCKED_SYSTEM_PROMPT + persona; user content = pinned-key payload.
// ---------------------------------------------------------------------------
async function callClaude(npcId: number, env: any, transcript: string, memory: string): Promise<NpcReply | null> {
  if (!anthropic) return null;
  // COST: split system into a CACHED static block (LOCKED_SYSTEM_PROMPT + per-archetype persona —
  // identical across every turn for a given NPC) and a small uncached tail. The persona string is
  // one of exactly 3 stable archetype texts, so repeated turns hit the cached prefix and pay ~10%
  // on it (cache_control:{type:'ephemeral'}). Render order is system -> messages, so the dynamic
  // per-turn payload (transcript/env/memory) rides the user turn AFTER the breakpoint and never
  // invalidates the cached prefix. The tool list also renders before system and caches with it.
  const personaText = persona.get(npcId) || ARCHETYPE_PERSONA['resident'];
  const system = [
    {
      type: 'text' as const,
      text: LOCKED_SYSTEM_PROMPT + '\n\nPERSONA:\n' + personaText,
      cache_control: { type: 'ephemeral' as const },
    },
  ];
  try {
    const msg = await anthropic.messages.create({
      model: MODEL,
      // COST: NPC reply is one short JSON tool call (dialogue <= ~300 chars + small deltas).
      // 160 tokens is ample for the forced structured reply; trimmed from 400 to cap output spend.
      max_tokens: 160,
      system,
      tools: [NPC_TOOL as any],
      tool_choice: { type: 'tool', name: 'npc_reply' },
      messages: [
        {
          role: 'user',
          // PINNED keys: 'player_speech' (NOT 'transcript'), currentNpcSanity/currentNpcTrust
          // (camel), so the verbatim prompt's references resolve. environment carries all 7
          // WorldStateContext fields.
          content: JSON.stringify({
            environment: {
              timeOfDay: env.timeOfDay,
              weatherConditions: env.weatherConditions,
              playerDistance: env.playerDistance,
              isPlayerArmed: env.isPlayerArmed,
              flashlightInFace: env.flashlightInFace,
              currentNpcSanity: env.currentNpcSanity,
              currentNpcTrust: env.currentNpcTrust,
            },
            player_speech: transcript,
            memory,
          }),
        },
      ],
    });
    const block: any = msg.content.find((b: any) => b.type === 'tool_use');
    if (block && block.input) return block.input as NpcReply;
    console.warn('[director] LLM returned no tool_use block — using fallback');
  } catch (e: any) {
    console.warn('[director] LLM call failed, using fallback:', e?.message || e);
  }
  return null;
}

// ---------------------------------------------------------------------------
// Reducer-result branching. In this SDK version conn.reducers.<name>(args) returns a
// Promise that RESOLVES on Ok and REJECTS (SenderError) on Err. So we branch on the
// outcome of the await, NOT on a post-await cache re-read (the cache lags commit).
// ---------------------------------------------------------------------------
async function tryClaim(interactionId: bigint): Promise<boolean> {
  if (!conn) return false;
  try {
    await conn.reducers.claimInteraction({ interactionId });
    return true; // explicit Ok — I won the CAS + the per-NPC busy lock.
  } catch (e: any) {
    // Err = lost the race / npc busy. Skip; it will be retried by the sweep.
    if (e instanceof SenderError) return false;
    console.warn('[director] claim error (treating as lost):', e?.message || e);
    return false;
  }
}

// ---------------------------------------------------------------------------
// Process ONE pending interaction end-to-end. Per-row try/catch/finally so one bad
// interaction never kills the loop and never strands the single-flight slot.
// ---------------------------------------------------------------------------
async function processInteraction(row: any) {
  if (!conn) return;
  const npcId = Number(row.npcId);
  const interactionId: bigint = BigInt(row.id);

  // (1) SKIP if this npc is already in-flight in THIS process.
  if (inFlight.has(npcId)) return;

  // (1) CLAIM — branch on the reducer outcome, not a cache re-read.
  const won = await tryClaim(interactionId);
  if (!won) return;
  inFlight.add(npcId);

  let heartbeat: ReturnType<typeof setInterval> | null = null;
  try {
    // (2) BUILD environment from authoritative sources.
    const ws = [...conn.db.world_state.iter()].find((w: any) => Number(w.id) === 0);
    const npc = [...conn.db.npc.iter()].find((n: any) => Number(n.npcId) === npcId);
    if (!npc) {
      // NPC vanished — release the claim so it isn't stuck busy.
      await failSafe(interactionId, 'npc missing at process time');
      return;
    }

    const env = {
      // global slow env from world_state(0)
      timeOfDay: ws ? String(ws.timeOfDay) : 'dusk',
      weatherConditions: ws ? String(ws.weatherConditions) : 'fog',
      // per-interaction speak-time snapshot (NOT recomputed from live position)
      playerDistance: Number(row.playerDistance ?? 0),
      isPlayerArmed: Boolean(row.isPlayerArmed),
      flashlightInFace: Boolean(row.flashlightInFace),
      // LIVE trust/sanity read off the npc row immediately before the call (i32 -> plain numbers)
      currentNpcTrust: Number(npc.trust ?? 0),
      currentNpcSanity: Number(npc.sanity ?? 0),
    };

    // (3) MEMORY — in-process summary for this npc_id.
    const memory = memoryByNpc.get(npcId) || '';

    // transcript rides the PUBLIC npc_interaction row (the director is anonymous and cannot
    // read the private npc_utterance table). `row` is the interaction we just claimed.
    const transcript = String(row.transcript || '');

    // (4) START HEARTBEAT for the duration of the call — keeps a slow-but-alive call
    // from being reclaimed by game_tick's 60-tick stale sweep.
    heartbeat = setInterval(() => {
      conn?.reducers
        .renewClaim({ interactionId })
        .catch((e: any) => console.warn('[director] renew_claim failed:', e?.message || e));
    }, HEARTBEAT_MS);

    // (5) CALL CLAUDE (skipped if no transcript and no key — fall straight to deterministic).
    let raw: NpcReply | null = null;
    if (transcript) {
      raw = await callClaude(npcId, env, transcript, memory);
    } else {
      console.warn(
        `[director] no transcript for interaction ${interactionId} (npc ${npcId}) — owner token missing or npc_utterance private; using deterministic fallback`,
      );
    }

    // (6) VALIDATE-THEN-FALLBACK (never freeze).
    const reply = validateOrFallback(raw, env);

    // (7) STOP HEARTBEAT before the write.
    if (heartbeat) {
      clearInterval(heartbeat);
      heartbeat = null;
    }

    // (7) RESOLVE target_player: FLEE/FOLLOW/ATTACK -> the asker; STAY_PUT -> None.
    const targetPlayer =
      reply.game_action === 'STAY_PUT' ? undefined : (row.asker as any);

    // (8) WRITE BACK. camelCase single object; i32 deltas as plain numbers (NOT bigint).
    // The Rust reducer guards claimed_by==sender, clamps trust/sanity 0..100, bumps seq,
    // flips status->done, ownership-clears busy.
    await conn.reducers.npcRespond({
      npcId: BigInt(npcId),
      interactionId,
      dialogue: reply.dialogue.slice(0, DIALOGUE_CAP),
      animationTrigger: reply.animation_trigger,
      gameAction: reply.game_action,
      targetPlayer,
      trustChange: reply.trust_change,
      sanityChange: reply.sanity_change,
    });

    // EXPEDITION: wire the fine-grained relationship + clue-leak proposal through the new deterministic
    // reducers (bindings regenerated). The LLM only PROPOSES; these reducers RE-CHECK the leak gate
    // (trust>=min_trust && fear<=max_fear && required flag) against the DB, so it is never trusted
    // (anti-prompt-injection). A failure here must not fail the turn — the dialogue already wrote above.
    try {
      await conn.reducers.applyRelationship({
        npcId: BigInt(npcId), player: row.asker,
        trustDelta: reply.trust_delta, fearDelta: reply.fear_delta,
        addFlag: '', memoryNote: reply.memory_note,
      });
      if (reply.reveal_clue_id > 0) {
        await conn.reducers.revealClue({
          npcId: BigInt(npcId), clueId: BigInt(reply.reveal_clue_id), player: row.asker,
        });
      }
    } catch (relErr: any) {
      console.warn(`[director] relationship/clue wiring failed for npc ${npcId}:`, relErr?.message || relErr);
    }

    // Roll the in-process memory summary — prefer the model's own one-line note when it gave one.
    const summary =
      reply.memory_note ||
      `Player said: "${transcript.slice(0, 120)}" -> ${reply.game_action} (${reply.animation_trigger}); dT=${reply.trust_change} dS=${reply.sanity_change}`;
    memoryByNpc.set(npcId, summary);

    console.log(
      `[director] npc ${npcId} <- "${reply.dialogue.slice(0, 60)}" [${reply.animation_trigger}/${reply.game_action}] dT=${reply.trust_change} dS=${reply.sanity_change}` +
        (reply.reveal_clue_id > 0 ? ` (proposed clue ${reply.reveal_clue_id}, pending post-regen wiring)` : ''),
    );
  } catch (e: any) {
    // Total failure -> fail_interaction so the NPC is unstuck (game_tick reclaims it).
    console.warn(`[director] interaction ${interactionId} failed:`, e?.message || e);
    if (heartbeat) {
      clearInterval(heartbeat);
      heartbeat = null;
    }
    await failSafe(interactionId, String(e?.message || e).slice(0, 200));
  } finally {
    if (heartbeat) clearInterval(heartbeat);
    inFlight.delete(npcId);
  }
}

async function failSafe(interactionId: bigint, reason: string) {
  try {
    await conn?.reducers.failInteraction({ interactionId, reason });
  } catch (e: any) {
    // Already done/failed/lost ownership — fine; the sweep will reclaim if needed.
    console.warn('[director] fail_interaction error (ignored):', e?.message || e);
  }
}

// ---------------------------------------------------------------------------
// DISPATCH — event-driven (onInsert) + safety-net sweep (setInterval). Both feed the
// same per-row processor. Process oldest-first per npc_id (sort by id; STDB has no
// ORDER BY) so a given NPC answers utterances FIFO.
// ---------------------------------------------------------------------------
function pendingRows(): any[] {
  if (!conn) return [];
  return [...conn.db.npc_interaction.iter()]
    .filter((r: any) => String(r.status) === 'pending')
    .sort((a: any, b: any) => Number(a.id) - Number(b.id));
}

function dispatchPending() {
  // Process oldest-first; skip npcs already in-flight (defense in depth).
  for (const row of pendingRows()) {
    if (inFlight.has(Number(row.npcId))) continue;
    processInteraction(row).catch((e) => console.warn('[director] process error', e?.message || e));
  }
}

function handlePendingInsert(_ctx: any, row: any) {
  if (String(row.status) !== 'pending') return;
  if (inFlight.has(Number(row.npcId))) return;
  processInteraction(row).catch((e) => console.warn('[director] onInsert process error', e?.message || e));
}

// ---------------------------------------------------------------------------
// main — connection lifecycle reused verbatim from keeper.ts (builder/withUri/
// withDatabaseName/withConfirmedReads(false)/onConnect/onConnectError/onDisconnect/
// build), ADDING .withToken(STDB_OWNER_TOKEN) when present so npc_utterance is visible.
// Callbacks registered BEFORE subscribing so v2 backfill is captured; work starts only
// inside onApplied.
// ---------------------------------------------------------------------------
function main() {
  const mode = anthropic ? `LLM (${MODEL})` : 'RULE-BASED (no ANTHROPIC_API_KEY)';
  const auth = OWNER_TOKEN ? 'owner-token (npc_utterance readable)' : 'anonymous (no transcript unless npc_utterance is PUBLIC)';
  const patron = OWNER_TOKEN ? 'billionaire lane ENABLED (+private dossier/whisper)' : 'billionaire lane ENABLED (public; claim_director TOFU)';
  console.log(`[director] connecting to ${HOST} / ${DB} | model: ${mode} | auth: ${auth} | ${patron}`);

  // Stable director identity: prefer an explicit owner token (also unlocks PRIVATE reads for the
  // dossier/whisper P1 features); else reuse a persisted auto-issued token so claim_director stays
  // idempotent across restarts.
  let persistedTok = '';
  try { persistedTok = fs.readFileSync(DIRECTOR_TOKEN_FILE, 'utf8').trim(); } catch {}
  const TOKEN = OWNER_TOKEN || persistedTok;

  let builder = DbConnection.builder()
    .withUri(HOST)
    .withDatabaseName(DB)
    .withConfirmedReads(false);

  if (TOKEN) builder = builder.withToken(TOKEN);

  builder
    .onConnect((c: DbConnection, _identity: any, token: string) => {
      conn = c;
      if (!OWNER_TOKEN && token) { try { fs.writeFileSync(DIRECTOR_TOKEN_FILE, token); } catch {} }

      // Register a no-op onInsert BEFORE subscribing so v2 backfill is captured...
      c.db.npc.onInsert((_ctx: any, npc: any) => refreshPersona(npc));
      c.db.npc.onUpdate((_ctx: any, _old: any, npc: any) => refreshPersona(npc));
      // ...and the real pending-utterance dispatcher.
      c.db.npc_interaction.onInsert(handlePendingInsert);

      const subs = [
        'SELECT * FROM npc',
        'SELECT * FROM npc_interaction',
        'SELECT * FROM player',
        'SELECT * FROM world_state',
      ];
      // Only subscribe npc_utterance when we hold the owner token (else RLS hides it and
      // an unauthorized subscription errors). npc_cognition is NEVER subscribed —
      // personas are in-process.
      if (OWNER_TOKEN) subs.push('SELECT * FROM npc_utterance');

      // --- BILLIONAIRE LANE (additive, same process) ---
      // Register its edge-trigger callbacks BEFORE subscribing (so v2 backfill is
      // captured), and append its subscriptions. Disabled without the owner token.
      let billionaireWired = false;
      if (BILLIONAIRE_ENABLED) {
        billionaireWired = registerBillionaireCallbacks(c);
        if (billionaireWired) {
          subs.push(...BILLIONAIRE_PUBLIC_SUBS);
          // PRIVATE billionaire reads (dossier / whisper truth — P1) need the owner token (RLS).
          if (OWNER_TOKEN) subs.push(...BILLIONAIRE_PRIVATE_SUBS);
        }
      }

      c.subscriptionBuilder()
        .onApplied(() => {
          console.log('[director] subscribed. Watching the forest...');

          // Build the in-process persona map from the PUBLIC npc.archetype backfill.
          for (const npc of c.db.npc.iter()) refreshPersona(npc);

          // FIRST: full backlog drain of ALL pending rows oldest-first, so a reconnect
          // gap doesn't strand utterances. (game_tick re-pends any stale-claimed rows.)
          dispatchPending();

          // THEN: safety-net sweep on top of the event-driven onInsert path.
          setInterval(() => {
            try {
              dispatchPending();
            } catch (e: any) {
              console.warn('[director] sweep error', e?.message || e);
            }
          }, TICK_MS);

          // --- BILLIONAIRE LANE start (additive) --- backfill ledgers, drain its
          // billionaire_request backlog oldest-first, start its own safety-net sweep.
          // ownerRead is true here because the lane is only enabled WITH the owner token,
          // so the private cognition/dossier/hook_truth subs are live.
          if (billionaireWired) {
            // Claim the director gate (TOFU). Idempotent on reconnect with the same identity.
            try { (c.reducers as any).claimDirector(); } catch (e: any) { console.warn('[director] claim_director', e?.message || e); }
            startBillionaireLane(c, /* ownerRead */ Boolean(OWNER_TOKEN));
          }
        })
        .onError((err: any) => console.error('[director] subscription error', err))
        .subscribe(subs);
    })
    .onConnectError((_ctx: any, err: any) => console.error('[director] connect error', err?.message || err))
    .onDisconnect(() => console.log('[director] disconnected'))
    .build();
}

main();
