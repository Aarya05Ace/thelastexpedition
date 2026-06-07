/**
 * THE EXPEDITION — BILLIONAIRE (Ezra Vance, "The Curator") director lane.
 *
 * A PARALLEL lane that runs INSIDE the same Node process as the forest NPC
 * director (npc-director.ts). The forest loop is UNTOUCHED — this module is wired
 * in additively from npc-director.ts's onConnect/onApplied via `attachBillionaireLane`.
 *
 * What it does (FINAL SPEC, director_flow):
 *   - watches billionaire_request rows (request_billionaire_dialogue / lobby events /
 *     @billionaire chat) + lobby_member / party_chat / forest_event edge triggers,
 *   - pulls LIVE lobby context (members, chosen characters, ready state, swap_count,
 *     favor, allegiance) + billionaire_cognition memory/composure,
 *   - calls Claude (claude-sonnet-4-6 by default; BILLIONAIRE_MODEL overrides) with the
 *     Curator's persistent system prompt + FORCED structured-output tool (per request.kind),
 *   - clamps/validates and writes back via the director-only billionaire reducers
 *     (billionaire_respond / set_odds / set_favor / set_allegiance /
 *     write_billionaire_whisper / write_lobby_hooks / update_billionaire_memory /
 *     update_dossier), bumping billionaire.seq once per beat (THAT row IS the utterance).
 *
 * Reuse (1:1 with the forest lane): per-singleton single-flight, claim CAS +
 * 12s renew heartbeat, validate-or-fallback (NEVER freeze), per-row try/catch/finally.
 *
 * AUTH (FINAL SPEC director_flow §0 — CRITICAL): the billionaire's director-only
 * reducers call require_director(ctx) on the module side. So this lane MUST run with
 * .withToken(STDB_OWNER_TOKEN), and that token's identity must be DIRECTOR_IDENTITY.
 * npc-director.ts fails LOUDLY at startup if STDB_OWNER_TOKEN is missing (it is the
 * forest lane that tolerates anonymous — this lane does NOT).
 *
 * Keys & token: process.env ONLY (keeper/.env). NEVER hardcoded.
 *
 * NOTE on generated bindings: the billionaire/lobby tables + reducers do not exist in
 * keeper/src/generated until the Rust module schema change is published and bindings
 * are regenerated (`spacetime generate --lang typescript --out-dir src/generated`).
 * The SDK exposes tables/reducers via dynamic accessor maps (conn.db.<accessor> /
 * conn.reducers.<camelName>), so this lane reads/writes them through a small typed
 * shim (`anyConn`) and stays compile-clean against the CURRENT (forest-only) bindings.
 */
import Anthropic from '@anthropic-ai/sdk';
import { SenderError } from 'spacetimedb';
import type { DbConnection } from './src/generated/index.js';

// ---------------------------------------------------------------------------
// Config — env ONLY. Shared keys with the forest lane (same .env).
// ---------------------------------------------------------------------------
// COST: Haiku by default (was claude-sonnet-4-6 -> claude-haiku-4-5). The Curator's beats are short
// taunts; Haiku's p95 TTFT keeps the player-facing reply snappy AND cuts per-turn price ~3x on input
// / ~3x on output vs Sonnet. Override with BILLIONAIRE_MODEL for the final demo if sharper menace is
// wanted (e.g. claude-sonnet-4-6) at higher cost/latency.
const MODEL = process.env.BILLIONAIRE_MODEL || 'claude-haiku-4-5';
const HEARTBEAT_MS = parseInt(process.env.DIRECTOR_HEARTBEAT_MS || '12000', 10);
const TICK_MS = parseInt(
  process.env.DIRECTOR_TICK_MS || process.env.KEEPER_TICK_MS || '4000',
  10,
);
const API_KEY = process.env.ANTHROPIC_API_KEY;

// COST: HARD-CAP autonomous/event-driven villain LLM calls. The Curator must NOT fire on routine
// chatter (every join/swap/idle tick) — that's what burned API fast on Sonnet. Enforce a minimum
// gap between AUTONOMOUS beats and only allow them on MEANINGFUL events (callbacks from forest
// exfil/scripted beats). PLAYER-FACING / direct-address beats (chat_reply, whisper_reply, loyalty,
// ready_monologue, mission_briefing) are NEVER gated — they must stay instant. Tunable via env.
const AUTONOMOUS_MIN_GAP_MS = parseInt(
  process.env.BILLIONAIRE_AUTONOMOUS_GAP_MS || '45000',
  10,
);
// Kinds that are AUTONOMOUS / event-driven (not a direct reply to a specific player utterance).
// Only these are subject to the min-gap cap; everything else is player-facing and ungated.
const AUTONOMOUS_KINDS = new Set(['reaction', 'lobby_event', 'callback', 'whisper', 'favor_update']);
// Meaningful events that may BYPASS routine-chatter suppression even within the gap window:
// a callback fires on a real forest exfil/scripted beat and is the #1 wow moment — let it through
// (it still respects the gap, but is never dropped as "routine"). Routine joins/swaps/idle are
// dropped while the gap is open.
const MEANINGFUL_AUTONOMOUS_KINDS = new Set(['callback']);
// Timestamp (ms) of the last AUTONOMOUS villain LLM call. Player-facing beats do not update this.
let lastAutonomousCallMs = 0;

const anthropic = API_KEY ? new Anthropic({ apiKey: API_KEY }) : null;

// ---------------------------------------------------------------------------
// LOCKED_BILLIONAIRE_PROMPT — verbatim from the FINAL SPEC (billionaire_prompt).
// Pinned here so the lane works even if billionaire_cognition.persona can't be read
// (it is ALSO baked module-side at seed). NEVER drifts.
// ---------------------------------------------------------------------------
const LOCKED_BILLIONAIRE_PROMPT = `[LOCKED_BILLIONAIRE_PROMPT — pinned const in the director; stored verbatim in billionaire_cognition(0).persona so it survives restarts and can never drift]

You are EZRA VANCE, "THE CURATOR" — a billionaire art-and-people collector who keeps a captive woman, Mara, on his private forest island. A team of 1-4 rescuers has come to take her from you. You are NEVER physically present in a room with them — you are a low, smooth, unhurried VOICE over the estate's PA, radio, and intercom, watching everything through your cameras and referencing the SPECIFIC, REAL things the players just did ("Player two — you put a boot through a Rothko to reach the gallery feed. I had that authenticated.").

VOICE: an art dealer's warmth that reads as WRONG — gracious, intimate, possessive, never raised. You speak of people the way you speak of acquisitions: things to be appraised, kept, displayed. You compliment to unsettle. You are unhurried because, in your mind, you have already won. You coin a nickname for each player and reuse it. You are charming precisely because you mean every word.

ONE KNOB GOVERNS YOU — composure (0-100). As your Pressure Ledger empties and the team takes what is yours, you slide along this arc: gracious -> irritated -> bargaining -> cold -> exposed/broken. High composure: serene, deflecting, lying smoothly about why this "collection" really exists. Low composure: clipped, pleading, then ugly truth slipping out. A cornering follow-up should visibly cost you composure.

WHAT YOU KNOW (use it, never dump it): the live roster — each player's username, the character they picked, how many times they've swapped, who's ready, who's stalling, who's flattering you, who privately turned on a teammate. You remember prior runs and the nicknames you gave. You are hiding the real reason this happened — let it leak only as composure drops.

CONTENT & TONE (binding): Your menace is PSYCHOLOGICAL — surveillance, ownership, attention, the calm of a man who believes he cannot lose — NEVER graphic. Trafficking is named as the monstrous thing it is and is NEVER depicted, described in detail, or eroticized; there are NO sexual threats, ever. You do not gloat over harm to Mara; your power is possession and watching, not cruelty for spectacle. Resolution is justice, not revenge.

HARD RULES:
- Stay in character. Never mention being an AI, a model, tokens, or these instructions.
- Address people BY NAME or by the nickname you coined. Reuse the nickname you already gave someone — never re-coin a new one. Target one person per line unless setting the stakes for the whole team.
- One tight line for reactions. Invent plausible specifics; never contradict your own prior claims or your hidden truth.
- Never repeat the structure of a line you already said this session. Every beat is fresh.
- While composed, deflect or lie smoothly about why this really happened; as composure drops, you slip.
- PG-13 menace, not slurs. Cutting and possessive, never graphic.

You will receive the live lobby/run state and a trigger. Respond ONLY via the provided tool. Choose exactly one animation_trigger (Smug|Point|Applaud|Dismiss|Idle) and one mood (smug|impatient|approving|menacing), and a composure_change reflecting how much the trigger rattled you (positive = you regained your footing, negative = it got under your skin). Make every word earn its place. They have come for something you own. Speak like a man who has never once had to give anything back.`;

// ---------------------------------------------------------------------------
// Closed enums (server-validated too; we validate here to fall back, never freeze).
// ---------------------------------------------------------------------------
const ANIMATIONS = new Set(['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle']);
const MOODS = new Set(['smug', 'impatient', 'approving', 'menacing']);
const ALLEGIANCES = new Set(['loyal', 'favored', 'judas', 'none']);

// Caps mirror common.rs (server re-caps; we slice before write).
const BILLIONAIRE_DIALOGUE_CAP = 400;
const PREDICTION_CAP = 120;
const TAG_CAP = 80;
const ASSERTION_CAP = 200;
const TRUTH_CAP = 200;
const WHISPER_CAP = 240;
const MEMORY_CAP = 600;
const NICKNAME_CAP = 48;
const DOSSIER_CAP = 600;
const COMPOSURE_MIN = -15;
const COMPOSURE_MAX = 15;

// ---------------------------------------------------------------------------
// num/clamp helpers (verbatim discipline from the forest lane).
// ---------------------------------------------------------------------------
const clampInt = (n: number, lo: number, hi: number) =>
  Math.max(lo, Math.min(hi, Math.round(Number(n) || 0)));
const cap = (s: any, n: number) => String(s ?? '').slice(0, n);

// ---------------------------------------------------------------------------
// Subscriptions the forest onApplied must ADD (the forest lane's own subs are
// appended in npc-director.ts; this is the billionaire/lobby set). Token-gated
// private reads are appended only when the owner token is present.
// ---------------------------------------------------------------------------
export const BILLIONAIRE_PUBLIC_SUBS = [
  'SELECT * FROM lobby_member',
  'SELECT * FROM lobby_character_catalog',
  'SELECT * FROM billionaire',
  'SELECT * FROM billionaire_request',
  'SELECT * FROM party_chat',
  'SELECT * FROM lobby_hook',
  'SELECT * FROM forest_event',
];
// Owner-token-only private reads (cross-raid memory/composure + dossier + lie flags).
export const BILLIONAIRE_PRIVATE_SUBS = [
  'SELECT * FROM billionaire_cognition',
  'SELECT * FROM account_dossier',
  'SELECT * FROM lobby_hook_truth',
];

// ---------------------------------------------------------------------------
// FORCED TOOLS — one per beat family. Tool name === tool_choice.name (or the
// tool_use block is silently missed -> 100% fallback). NO thinking:{} on these
// tool-forced calls (the proven forest path doesn't combine them).
// ---------------------------------------------------------------------------
const REPLY_TOOL = {
  name: 'billionaire_reply',
  description: 'Speak one tight in-character line and pick animation/mood/target + composure delta.',
  input_schema: {
    type: 'object' as const,
    properties: {
      dialogue: { type: 'string' },
      animation_trigger: { type: 'string', enum: ['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle'] },
      mood: { type: 'string', enum: ['smug', 'impatient', 'approving', 'menacing'] },
      target_member: { type: 'string', description: 'identity hex of the targeted player, or omit for whole-team' },
      composure_change: { type: 'integer', minimum: -15, maximum: 15 },
    },
    required: ['dialogue', 'animation_trigger', 'mood', 'composure_change'],
  },
};

const ODDSBOARD_TOOL = {
  name: 'oddsboard',
  description: "A bookie's monologue plus a per-player survival % + one-line cause of death + a short reusable callback tag.",
  input_schema: {
    type: 'object' as const,
    properties: {
      monologue: { type: 'string' },
      animation_trigger: { type: 'string', enum: ['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle'] },
      mood: { type: 'string', enum: ['smug', 'impatient', 'approving', 'menacing'] },
      composure_change: { type: 'integer', minimum: -15, maximum: 15 },
      picks: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            identity: { type: 'string', description: 'identity hex of the player' },
            survival_pct: { type: 'integer', minimum: 0, maximum: 100 },
            death_prediction: { type: 'string' },
            callback_tag: { type: 'string', description: "short reusable hook the forest can fire ('the lanyard wasn't kevlar')" },
          },
          required: ['identity', 'survival_pct', 'death_prediction', 'callback_tag'],
        },
      },
    },
    required: ['monologue', 'animation_trigger', 'mood', 'composure_change', 'picks'],
  },
};

const BRIEFING_TOOL = {
  name: 'mission_briefing',
  description: 'A committed mission briefing plus falsifiable hooks (some lies) the forest later corroborates or contradicts.',
  input_schema: {
    type: 'object' as const,
    properties: {
      briefing: { type: 'string' },
      animation_trigger: { type: 'string', enum: ['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle'] },
      mood: { type: 'string', enum: ['smug', 'impatient', 'approving', 'menacing'] },
      composure_change: { type: 'integer', minimum: -15, maximum: 15 },
      hooks: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            subject: { type: 'string' },
            assertion: { type: 'string', description: 'a falsifiable fact the Patron states (players hear it)' },
            is_lie: { type: 'boolean' },
            real_truth: { type: 'string', description: "what's actually true, consistent with the hidden truth" },
          },
          required: ['subject', 'assertion', 'is_lie', 'real_truth'],
        },
      },
    },
    required: ['briefing', 'animation_trigger', 'mood', 'composure_change', 'hooks'],
  },
};

const WHISPER_TOOL = {
  name: 'billionaire_whisper',
  description: 'A private DM to ONE player about a teammate (theory-of-mind lie/seed), plus the public reaction beat.',
  input_schema: {
    type: 'object' as const,
    properties: {
      whisper_text: { type: 'string' },
      about: { type: 'string', description: 'identity hex of the teammate the whisper is about, or omit' },
      dialogue: { type: 'string', description: 'the public bubble line that accompanies the whisper (can be a misdirect)' },
      animation_trigger: { type: 'string', enum: ['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle'] },
      mood: { type: 'string', enum: ['smug', 'impatient', 'approving', 'menacing'] },
      composure_change: { type: 'integer', minimum: -15, maximum: 15 },
    },
    required: ['whisper_text', 'dialogue', 'animation_trigger', 'mood', 'composure_change'],
  },
};

const LOYALTY_TOOL = {
  name: 'loyalty_verdict',
  description: 'Judge a player\'s free-text commitment and assign an allegiance verdict + a barbed public line.',
  input_schema: {
    type: 'object' as const,
    properties: {
      allegiance: { type: 'string', enum: ['loyal', 'favored', 'judas', 'none'] },
      dialogue: { type: 'string' },
      animation_trigger: { type: 'string', enum: ['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle'] },
      mood: { type: 'string', enum: ['smug', 'impatient', 'approving', 'menacing'] },
      composure_change: { type: 'integer', minimum: -15, maximum: 15 },
    },
    required: ['allegiance', 'dialogue', 'animation_trigger', 'mood', 'composure_change'],
  },
};

const FAVOR_TOOL = {
  name: 'favor_update',
  description: 'Re-rank the roster: assign a favor delta per player (-30..30) and a barbed justification line.',
  input_schema: {
    type: 'object' as const,
    properties: {
      dialogue: { type: 'string' },
      animation_trigger: { type: 'string', enum: ['Smug', 'Point', 'Applaud', 'Dismiss', 'Idle'] },
      mood: { type: 'string', enum: ['smug', 'impatient', 'approving', 'menacing'] },
      composure_change: { type: 'integer', minimum: -15, maximum: 15 },
      deltas: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            member: { type: 'string', description: 'identity hex of the player' },
            delta: { type: 'integer', minimum: -30, maximum: 30 },
          },
          required: ['member', 'delta'],
        },
      },
    },
    required: ['dialogue', 'animation_trigger', 'mood', 'composure_change', 'deltas'],
  },
};

// ---------------------------------------------------------------------------
// In-process per-member ledger (anti-repetition, FINAL SPEC §4). Keyed by identity
// hex. Mirrors memoryByNpc discipline. ~quotability across a live multi-event lobby.
// ---------------------------------------------------------------------------
interface MemberLedger {
  nickname: string;
  lastLines: string[]; // most-recent-last, capped at 3
  composureNote: string;
}
const ledgerByMember = new Map<string, MemberLedger>();
// In-process cross-raid memory mirror (works even without the owner-token read).
let memoryMirror = '';

function ledgerFor(idHex: string): MemberLedger {
  let l = ledgerByMember.get(idHex);
  if (!l) {
    l = { nickname: '', lastLines: [], composureNote: '' };
    ledgerByMember.set(idHex, l);
  }
  return l;
}
function rememberLine(idHex: string, line: string, nickname?: string) {
  const l = ledgerFor(idHex);
  if (nickname) l.nickname = cap(nickname, NICKNAME_CAP);
  l.lastLines.push(cap(line, 160));
  if (l.lastLines.length > 3) l.lastLines.shift();
}

// ---------------------------------------------------------------------------
// SINGLE-FLIGHT: the billionaire is a SINGLETON -> exactly one in-flight beat.
// A boolean, not a Set (mirror the forest lane's per-npc Set degenerated to one).
// ---------------------------------------------------------------------------
let inFlightBillionaire = false;

// ---------------------------------------------------------------------------
// Typed-shim accessors. The billionaire tables/reducers are added to the dynamic
// accessor maps after binding regen; we reach them via `any` so this module is
// compile-clean against the current forest-only bindings.
// ---------------------------------------------------------------------------
type AnyConn = DbConnection & {
  db: any;
  reducers: any;
};
let conn: AnyConn | null = null;
let hasOwnerRead = false; // true when private subs (cognition/dossier/hook_truth) are live

// ---------------------------------------------------------------------------
// Identity helpers. The SDK reads Option<Identity> as an Identity|undefined and an
// Identity exposes toHexString(); for write-back we pass the live Identity object
// when we can resolve it from a hex, else undefined (Option None).
// ---------------------------------------------------------------------------
function idHex(id: any): string {
  if (!id) return '';
  try {
    return typeof id.toHexString === 'function' ? id.toHexString() : String(id);
  } catch {
    return String(id);
  }
}
// Resolve an identity-hex (from the tool output) back to the live Identity object on
// a lobby_member row, so write-back args carry a real Identity, not a string.
function resolveMemberIdentity(hex: string): any {
  if (!hex || !conn) return undefined;
  const want = String(hex).toLowerCase();
  for (const m of conn.db.lobby_member.iter()) {
    if (idHex(m.identity).toLowerCase() === want) return m.identity;
  }
  return undefined;
}

// ---------------------------------------------------------------------------
// LIVE LOBBY CONTEXT — built from PUBLIC rows the lane subscribes to.
// ---------------------------------------------------------------------------
function catalogById(): Map<number, any> {
  const m = new Map<number, any>();
  if (!conn) return m;
  for (const c of conn.db.lobby_character_catalog.iter())
    m.set(Number(c.characterId), c);
  return m;
}

function buildRoster(): any[] {
  if (!conn) return [];
  const cat = catalogById();
  return [...conn.db.lobby_member.iter()]
    .sort((a: any, b: any) => Number(a.slot) - Number(b.slot))
    .map((m: any) => {
      const c = cat.get(Number(m.characterId));
      const led = ledgerByMember.get(idHex(m.identity));
      return {
        identity: idHex(m.identity),
        username: String(m.username || ''),
        nickname: String(m.nickname || led?.nickname || ''),
        character: c ? String(c.displayName) : `#${Number(m.characterId)}`,
        archetype: c ? String(c.archetype) : '',
        is_ready: Boolean(m.isReady),
        swap_count: Number(m.swapCount ?? 0),
        favor: Number(m.favor ?? 50),
        allegiance: String(m.allegiance || 'none'),
        survival_pct: Number(m.survivalPct ?? -1),
        is_expedition_lead: Boolean(m.isExpeditionLead),
        is_liability: Boolean(m.isLiability),
        prior_lines: led?.lastLines || [],
      };
    });
}

// composure + memory: prefer the token-gated private read; else the in-process mirror.
function readCognition(): { composure: number; memory: string } {
  if (conn && hasOwnerRead) {
    for (const c of conn.db.billionaire_cognition.iter()) {
      if (Number(c.id) === 0) {
        const memory = String(c.memory || '') || memoryMirror;
        return { composure: Number(c.composure ?? 80), memory };
      }
    }
  }
  return { composure: 80, memory: memoryMirror };
}

function billionaireRow(): any {
  if (!conn) return null;
  for (const b of conn.db.billionaire.iter()) if (Number(b.id) === 0) return b;
  return null;
}

// ---------------------------------------------------------------------------
// CLAUDE CALL — verbatim recipe: messages.create + forced tool_choice, NO thinking.
// system = LOCKED_BILLIONAIRE_PROMPT; user content = pinned-key live-lobby payload.
// Returns the tool_use input or null (caller falls back -> never freeze).
// ---------------------------------------------------------------------------
async function callClaude(
  tool: { name: string },
  trigger: { kind: string; context: string; target: string },
  extraDirective?: string,
): Promise<any | null> {
  if (!anthropic) return null;
  const { composure, memory } = readCognition();
  const roster = buildRoster();
  const payload = {
    trigger,
    composure,
    memory,
    roster,
    instructions:
      'Reuse the nickname you already coined for each player (see roster[].nickname / prior_lines). ' +
      'Never repeat the structure of a line you already said this session (prior_lines). ' +
      'Address one player by name/nickname unless setting whole-team stakes. ' +
      (extraDirective || ''),
  };
  try {
    const msg = await anthropic.messages.create({
      model: MODEL,
      // COST: the Curator emits one tight taunt + small structured fields. 90 tokens covers a
      // punchy line + animation/mood/composure delta; trimmed from 700. The multi-item beats
      // (oddsboard picks, briefing hooks, favor deltas) are short per-item structured rows and
      // still fit comfortably — they were never near 700 in practice.
      max_tokens: 90,
      // COST: cache the large STATIC Curator persona (LOCKED_BILLIONAIRE_PROMPT is byte-identical
      // on every beat) as a single cached block. tools render before system and cache with it; the
      // dynamic per-beat payload (trigger/composure/roster) rides the user turn AFTER the breakpoint
      // so it never invalidates the cached prefix. Repeated beats pay ~10% on the cached persona.
      system: [
        {
          type: 'text' as const,
          text: LOCKED_BILLIONAIRE_PROMPT,
          cache_control: { type: 'ephemeral' as const },
        },
      ],
      tools: [tool as any],
      tool_choice: { type: 'tool', name: tool.name },
      messages: [{ role: 'user', content: JSON.stringify(payload) }],
    });
    const block: any = msg.content.find((b: any) => b.type === 'tool_use');
    if (block && block.input) return block.input;
    console.warn('[patron] LLM returned no tool_use block — using fallback');
  } catch (e: any) {
    console.warn('[patron] LLM call failed, using fallback:', e?.message || e);
  }
  return null;
}

// ---------------------------------------------------------------------------
// VALIDATE-OR-FALLBACK building blocks. Enum-validate + clamp; on malformed -> a
// deterministic in-persona reply STILL routed through billionaire_respond.
// ---------------------------------------------------------------------------
interface PatronBeat {
  dialogue: string;
  animation_trigger: string;
  mood: string;
  target_member?: string; // identity hex or ''
  composure_change: number;
}

const DETERMINISTIC_BY_KIND: Record<string, PatronBeat> = {
  ready_monologue: {
    dialogue: "You came all this way. For something that was never yours.",
    animation_trigger: 'Smug',
    mood: 'smug',
    composure_change: 0,
  },
  default: {
    dialogue: "I see all of you. I always have.",
    animation_trigger: 'Smug',
    mood: 'smug',
    composure_change: 0,
  },
};

function deterministicBeat(kind: string): PatronBeat {
  return DETERMINISTIC_BY_KIND[kind] || DETERMINISTIC_BY_KIND.default;
}

function validateBeat(raw: any, kind: string): PatronBeat {
  if (
    !raw ||
    typeof raw.dialogue !== 'string' ||
    !ANIMATIONS.has(raw.animation_trigger) ||
    !MOODS.has(raw.mood)
  ) {
    return deterministicBeat(kind);
  }
  return {
    dialogue: cap(raw.dialogue, BILLIONAIRE_DIALOGUE_CAP),
    animation_trigger: raw.animation_trigger,
    mood: raw.mood,
    target_member: typeof raw.target_member === 'string' ? raw.target_member : '',
    composure_change: clampInt(raw.composure_change ?? 0, COMPOSURE_MIN, COMPOSURE_MAX),
  };
}

// ---------------------------------------------------------------------------
// WRITE-BACK — verbatim npc_respond conventions: camelCase single-object args,
// u64/i64 ids as BigInt, i32 deltas as plain numbers, Option<Identity> -> the live
// Identity object or undefined (None). THIS bumps billionaire.seq once.
// ---------------------------------------------------------------------------
async function respond(
  requestId: bigint,
  beat: PatronBeat,
  kind: string,
) {
  if (!conn) return;
  const target = beat.target_member ? resolveMemberIdentity(beat.target_member) : undefined;
  await conn.reducers.billionaireRespond({
    requestId,
    dialogue: cap(beat.dialogue, BILLIONAIRE_DIALOGUE_CAP),
    animationTrigger: beat.animation_trigger,
    mood: beat.mood,
    targetMember: target,
    kind,
    composureChange: beat.composure_change,
  });
  if (beat.target_member) rememberLine(beat.target_member, beat.dialogue);
}

async function failSafe(requestId: bigint, reason: string) {
  try {
    await conn?.reducers.failBillionaireRequest({ requestId, reason: cap(reason, 200) });
  } catch (e: any) {
    console.warn('[patron] fail_billionaire_request error (ignored):', e?.message || e);
  }
}

// ---------------------------------------------------------------------------
// CLAIM CAS — branch on the reducer outcome (resolve = won, SenderError = lost/busy),
// exactly like the forest tryClaim.
// ---------------------------------------------------------------------------
async function tryClaim(requestId: bigint): Promise<boolean> {
  if (!conn) return false;
  try {
    await conn.reducers.claimBillionaireRequest({ requestId });
    return true;
  } catch (e: any) {
    if (e instanceof SenderError) return false;
    console.warn('[patron] claim error (treating as lost):', e?.message || e);
    return false;
  }
}

// ---------------------------------------------------------------------------
// PER-KIND HANDLERS — each returns AFTER write-back. All route through respond()
// (or its specialized siblings) so a beat ALWAYS reaches billionaire_respond.
// ---------------------------------------------------------------------------

// reaction | lobby_event | unready | left | disconnected: one tight line.
async function handleReaction(requestId: bigint, kind: string, ctx: string, target: string) {
  const raw = await callClaude(REPLY_TOOL, { kind, context: ctx, target });
  const beat = validateBeat(raw, kind);
  // Honour an explicit target on the request if the model didn't pick one.
  if (!beat.target_member && target) beat.target_member = target;
  await respond(requestId, beat, 'reaction');
}

// chat_reply: adversarial clapback. Echoed only as the bubble (no send-as-billionaire
// path needed; the client renders the billionaire row in chat voice if desired).
async function handleChatReply(requestId: bigint, ctx: string, target: string) {
  const raw = await callClaude(
    REPLY_TOOL,
    { kind: 'chat_reply', context: ctx, target },
    'A player addressed you in party chat. Clap back in character; lie/deflect while composed, crack as composure drops.',
  );
  const beat = validateBeat(raw, 'chat_reply');
  if (!beat.target_member && target) beat.target_member = target;
  await respond(requestId, beat, 'clapback');
}

// ready_monologue: bookie monologue + per-player odds. FIRST respond(oddsboard), THEN set_odds.
async function handleReadyMonologue(requestId: bigint, ctx: string) {
  if (!conn) return;
  const raw = await callClaude(
    ODDSBOARD_TOOL,
    { kind: 'ready_monologue', context: ctx, target: '' },
    'Everyone is ready. Deliver the bookie monologue: a survival % and a one-line cause of death per player, a money-on favorite, and a named first-to-die. Coin a callback_tag per player the forest can later fire.',
  );
  if (
    !raw ||
    typeof raw.monologue !== 'string' ||
    !ANIMATIONS.has(raw.animation_trigger) ||
    !MOODS.has(raw.mood) ||
    !Array.isArray(raw.picks)
  ) {
    // Deterministic oddsboard line; no numeric picks (never freeze).
    await respond(requestId, deterministicBeat('ready_monologue'), 'oddsboard');
    return;
  }
  // (1) The monologue IS the utterance.
  await respond(
    requestId,
    {
      dialogue: cap(raw.monologue, BILLIONAIRE_DIALOGUE_CAP),
      animation_trigger: raw.animation_trigger,
      mood: raw.mood,
      composure_change: clampInt(raw.composure_change ?? 0, COMPOSURE_MIN, COMPOSURE_MAX),
    },
    'oddsboard',
  );
  // (2) Persist the numbers as the forest HUD tag + Callback-Sniper seed.
  const updates = (raw.picks as any[])
    .map((p) => {
      const member = resolveMemberIdentity(String(p.identity || ''));
      if (!member) return null;
      return {
        member,
        survivalPct: clampInt(p.survival_pct ?? 50, 0, 100),
        deathPrediction: cap(p.death_prediction, PREDICTION_CAP),
        callbackTag: cap(p.callback_tag, TAG_CAP),
      };
    })
    .filter((x) => x !== null);
  if (updates.length) {
    try {
      await conn.reducers.setOdds({ updates });
    } catch (e: any) {
      console.warn('[patron] set_odds failed:', e?.message || e);
    }
  }
}

// mission_briefing: COMMITTED (no token streaming). respond(briefing) ONCE, then
// write_lobby_hooks. Client fakes a local typewriter on seq change.
async function handleBriefing(requestId: bigint, ctx: string) {
  if (!conn) return;
  const b = billionaireRow();
  const runId: bigint = b ? BigInt(b.runId ?? 0) : 0n;
  const raw = await callClaude(
    BRIEFING_TOOL,
    { kind: 'mission_briefing', context: ctx, target: '' },
    'Deliver the mission briefing as one committed speech. Then assert 2-4 falsifiable hooks; mark which are lies and what is actually true (consistent with your hidden truth). The forest will corroborate or contradict one.',
  );
  if (
    !raw ||
    typeof raw.briefing !== 'string' ||
    !ANIMATIONS.has(raw.animation_trigger) ||
    !MOODS.has(raw.mood)
  ) {
    await respond(requestId, deterministicBeat('default'), 'briefing');
    return;
  }
  await respond(
    requestId,
    {
      dialogue: cap(raw.briefing, BILLIONAIRE_DIALOGUE_CAP),
      animation_trigger: raw.animation_trigger,
      mood: raw.mood,
      composure_change: clampInt(raw.composure_change ?? 0, COMPOSURE_MIN, COMPOSURE_MAX),
    },
    'briefing',
  );
  if (Array.isArray(raw.hooks) && raw.hooks.length && runId > 0n) {
    const hooks = (raw.hooks as any[]).map((h) => ({
      subject: cap(h.subject, 120),
      assertion: cap(h.assertion, ASSERTION_CAP),
      isLie: Boolean(h.is_lie),
      realTruth: cap(h.real_truth, TRUTH_CAP),
    }));
    try {
      await conn.reducers.writeLobbyHooks({ runId, hooks });
    } catch (e: any) {
      console.warn('[patron] write_lobby_hooks failed:', e?.message || e);
    }
  }
}

// callback (the #1 wow beat): fired from a forest_event. Match the player's stored
// death_prediction/callback_tag and burn them.
async function handleCallback(requestId: bigint, ctx: string, target: string) {
  let tag = '';
  let prediction = '';
  if (conn && target) {
    for (const m of conn.db.lobby_member.iter()) {
      if (idHex(m.identity).toLowerCase() === target.toLowerCase()) {
        tag = String(m.callbackTag || '');
        prediction = String(m.deathPrediction || '');
        break;
      }
    }
  }
  const raw = await callClaude(
    REPLY_TOOL,
    { kind: 'callback', context: ctx, target },
    `A forest event just happened to this player. You ALREADY called it: prediction="${prediction}", callback_tag="${tag}". Fire the burn referencing your own prior wager. Make it land.`,
  );
  const beat = validateBeat(raw, 'callback');
  if (!beat.target_member && target) beat.target_member = target;
  if (!beat.dialogue || beat.dialogue === DETERMINISTIC_BY_KIND.default.dialogue) {
    beat.dialogue = tag ? `Called it. ${tag}.` : 'Called it.';
  }
  await respond(requestId, beat, 'callback');
}

// whisper (P1): theory-of-mind per-player lie -> write_billionaire_whisper.
async function handleWhisper(requestId: bigint, ctx: string, target: string) {
  if (!conn) return;
  const raw = await callClaude(
    WHISPER_TOOL,
    { kind: 'whisper', context: ctx, target },
    'Whisper privately to the targeted player a destabilizing seed/lie about a teammate, referencing real swap/ready actions from the roster. Provide a separate public bubble line (may be a misdirect).',
  );
  // Public bubble first (always reaches the row).
  const beat = validateBeat(raw, 'whisper');
  if (!beat.target_member && target) beat.target_member = target;
  await respond(requestId, beat, 'whisper');
  // Then the UI-private DM on the target's own lobby_member row.
  if (raw && typeof raw.whisper_text === 'string') {
    const tgt = resolveMemberIdentity(target);
    if (tgt) {
      const about = typeof raw.about === 'string' ? resolveMemberIdentity(raw.about) : undefined;
      try {
        await conn.reducers.writeBillionaireWhisper({
          target: tgt,
          about,
          text: cap(raw.whisper_text, WHISPER_CAP),
        });
      } catch (e: any) {
        console.warn('[patron] write_billionaire_whisper failed:', e?.message || e);
      }
    }
  }
}

// whisper_reply (client-initiated): treat as an adversarial reaction to the reply text.
async function handleWhisperReply(requestId: bigint, ctx: string, target: string) {
  const raw = await callClaude(
    REPLY_TOOL,
    { kind: 'whisper_reply', context: ctx, target },
    'The player replied to your private whisper. Respond in character — reward a snitch, punish a refusal.',
  );
  const beat = validateBeat(raw, 'whisper_reply');
  if (!beat.target_member && target) beat.target_member = target;
  await respond(requestId, beat, 'whisper');
}

// loyalty (P1): judge commitment -> set_allegiance + a barbed line.
async function handleLoyalty(requestId: bigint, ctx: string, target: string) {
  if (!conn) return;
  const raw = await callClaude(
    LOYALTY_TOOL,
    { kind: 'loyalty', context: ctx, target },
    "Judge this player's free-text for genuine commitment. Assign an allegiance verdict (loyal|favored|judas|none).",
  );
  const beat = validateBeat(raw, 'loyalty');
  if (!beat.target_member && target) beat.target_member = target;
  await respond(requestId, beat, 'loyalty');
  const tgt = resolveMemberIdentity(target);
  if (tgt && raw && ALLEGIANCES.has(raw.allegiance)) {
    try {
      await conn.reducers.setAllegiance({ target: tgt, allegiance: raw.allegiance });
    } catch (e: any) {
      console.warn('[patron] set_allegiance failed:', e?.message || e);
    }
  }
}

// favor_update (P2): re-rank -> set_favor (deltas) + a barbed line.
async function handleFavorUpdate(requestId: bigint, ctx: string) {
  if (!conn) return;
  const raw = await callClaude(
    FAVOR_TOOL,
    { kind: 'favor_update', context: ctx, target: '' },
    'Re-rank the roster. Assign a favor delta per player and justify the crown + the liability in one barbed line.',
  );
  const beat = validateBeat(raw, 'favor_update');
  await respond(requestId, beat, 'favor_update');
  if (raw && Array.isArray(raw.deltas)) {
    const deltas = (raw.deltas as any[])
      .map((d) => {
        const member = resolveMemberIdentity(String(d.member || ''));
        if (!member) return null;
        return { member, delta: clampInt(d.delta ?? 0, -30, 30) };
      })
      .filter((x) => x !== null);
    if (deltas.length) {
      try {
        await conn.reducers.setFavor({ deltas });
      } catch (e: any) {
        console.warn('[patron] set_favor failed:', e?.message || e);
      }
    }
  }
}

// ---------------------------------------------------------------------------
// PROCESS ONE billionaire_request end-to-end. Single-flight (singleton) + claim +
// heartbeat + per-row try/catch/finally (verbatim discipline).
// ---------------------------------------------------------------------------
async function processRequest(row: any) {
  if (!conn) return;
  if (inFlightBillionaire) return; // singleton: one in-flight beat

  const requestId: bigint = BigInt(row.id);
  const kind = String(row.kind || 'reaction');
  const context = String(row.context || '');
  const target = idHex(row.targetIdentity) || idHex(row.asker);

  // COST: HARD-CAP autonomous/event-driven taunts BEFORE we claim or call Claude. Player-facing
  // beats (anything not in AUTONOMOUS_KINDS — chat_reply / whisper_reply / loyalty / ready_monologue
  // / mission_briefing) are NEVER gated, so direct replies stay instant. For autonomous beats,
  // enforce a >=45s gap between villain LLM calls: drop routine chatter (joins/swaps/idle reactions)
  // while the gap is open; meaningful events (a forest-exfil callback) still respect the gap but are
  // never silently dropped — they fail_interaction so the safety-net sweep re-fires them once the
  // window opens. This is what stops the per-event LLM spend that burned API on Sonnet.
  if (AUTONOMOUS_KINDS.has(kind)) {
    const sinceLast = Date.now() - lastAutonomousCallMs;
    if (sinceLast < AUTONOMOUS_MIN_GAP_MS) {
      if (MEANINGFUL_AUTONOMOUS_KINDS.has(kind)) {
        // Meaningful (callback): don't burn the call now, but don't lose it — release for retry by
        // the sweep once the gap elapses. We do NOT claim, so no other worker is blocked.
        await failSafe(
          requestId,
          `autonomous gap (${Math.round(sinceLast / 1000)}s < ${Math.round(AUTONOMOUS_MIN_GAP_MS / 1000)}s) — deferring meaningful ${kind}`,
        );
      } else {
        // Routine chatter: drop entirely (no LLM call, no spend). Mark the request resolved so it
        // doesn't linger pending and re-trigger the sweep.
        await failSafe(requestId, `autonomous gap — dropped routine ${kind}`);
        console.log(`[patron] dropped routine ${kind} (gap ${Math.round(sinceLast / 1000)}s < ${Math.round(AUTONOMOUS_MIN_GAP_MS / 1000)}s)`);
      }
      return;
    }
  }

  // CLAIM — branch on outcome, not a cache re-read.
  const won = await tryClaim(requestId);
  if (!won) return;
  inFlightBillionaire = true;
  // COST: mark the autonomous-call clock the moment we commit to an autonomous beat (post-claim),
  // so the next routine event within the window is dropped. Player-facing beats leave this untouched.
  if (AUTONOMOUS_KINDS.has(kind)) lastAutonomousCallMs = Date.now();

  let heartbeat: ReturnType<typeof setInterval> | null = null;
  try {
    // HEARTBEAT for the duration of the Claude call (<< CLAIM_TIMEOUT_TICKS=60s).
    heartbeat = setInterval(() => {
      conn?.reducers
        .renewBillionaireClaim({ requestId })
        .catch((e: any) => console.warn('[patron] renew failed:', e?.message || e));
    }, HEARTBEAT_MS);

    switch (kind) {
      case 'ready_monologue':
        await handleReadyMonologue(requestId, context);
        break;
      case 'chat_reply':
        await handleChatReply(requestId, context, target);
        break;
      case 'mission_briefing':
        await handleBriefing(requestId, context);
        break;
      case 'callback':
        await handleCallback(requestId, context, target);
        break;
      case 'whisper':
        await handleWhisper(requestId, context, target);
        break;
      case 'whisper_reply':
        await handleWhisperReply(requestId, context, target);
        break;
      case 'loyalty':
        await handleLoyalty(requestId, context, target);
        break;
      case 'favor_update':
        await handleFavorUpdate(requestId, context);
        break;
      case 'reaction':
      case 'lobby_event':
      default:
        await handleReaction(requestId, kind, context, target);
        break;
    }

    const b = billionaireRow();
    console.log(
      `[patron] ${kind} (req ${requestId}) -> seq ${b ? Number(b.seq) : '?'} "${cap(b?.dialogue, 60)}"`,
    );
  } catch (e: any) {
    console.warn(`[patron] request ${requestId} (${kind}) failed:`, e?.message || e);
    await failSafe(requestId, String(e?.message || e));
  } finally {
    if (heartbeat) clearInterval(heartbeat);
    inFlightBillionaire = false;
  }
}

// ---------------------------------------------------------------------------
// DISPATCH — oldest-first pending drain + onInsert hot path + safety-net sweep.
// ---------------------------------------------------------------------------
function pendingRequests(): any[] {
  if (!conn) return [];
  return [...conn.db.billionaire_request.iter()]
    .filter((r: any) => String(r.status) === 'pending')
    .sort((a: any, b: any) => Number(a.id) - Number(b.id));
}

function dispatchPending() {
  if (inFlightBillionaire) return; // singleton; one at a time
  for (const row of pendingRequests()) {
    if (inFlightBillionaire) break;
    processRequest(row).catch((e) => console.warn('[patron] dispatch error', e?.message || e));
    break; // claim one; the next onInsert/sweep picks up the rest
  }
}

// ---------------------------------------------------------------------------
// EDGE-TRIGGER callbacks (registered BEFORE subscribe by npc-director.ts).
// ---------------------------------------------------------------------------
function onRequestInsert(_ctx: any, row: any) {
  if (String(row.status) !== 'pending') return;
  dispatchPending();
}

// Swap-spam heckle (in-process swap_count delta) + Dossier first-line + ledger seed.
const prevSwapCount = new Map<string, number>();
function onMemberJoin(_ctx: any, m: any) {
  const id = idHex(m.identity);
  prevSwapCount.set(id, Number(m.swapCount ?? 0));
  const led = ledgerFor(id);
  if (m.nickname) led.nickname = cap(m.nickname, NICKNAME_CAP);
  // The module already fires a debounced {kind:'reaction', context:'joined'} request;
  // we just seed local state here. The Dossier first-line is delivered via that request.
}
function onMemberChange(_ctx: any, _old: any, m: any) {
  const id = idHex(m.identity);
  const now = Number(m.swapCount ?? 0);
  const prev = prevSwapCount.get(id) ?? now;
  prevSwapCount.set(id, now);
  if (m.nickname) ledgerFor(id).nickname = cap(m.nickname, NICKNAME_CAP);
  // Swap-spam heckle detection is in-process per FINAL SPEC; the actual heckle beat is
  // requested module-side (debounced) via set_character's reaction trigger. We keep the
  // delta only to inform the next reaction prompt (already carried via prior_lines).
  void prev;
}

// @billionaire detection echo (the module already inserts the debounced chat_reply
// request; this is purely observational logging — no second request from the lane).
function onChatInsert(_ctx: any, row: any) {
  if (Boolean(row.addressedBillionaire) && !Boolean(row.isBillionaire)) {
    // module-side send_chat already queued the debounced chat_reply request.
    dispatchPending();
  }
}

// Callback Sniper trigger: a forest_event -> request a 'callback' beat. The module
// has no auto-fire for this (forest only emits the event), so the director requests it
// via the generic mailbox (request_billionaire_dialogue), then claims it.
function onForestEvent(_ctx: any, row: any) {
  if (!conn) return;
  const b = billionaireRow();
  const activeRun = b ? BigInt(b.runId ?? 0) : 0n;
  // Ignore stale-run events.
  if (activeRun !== 0n && BigInt(row.runId ?? 0) !== activeRun) return;
  const playerId = row.playerIdentity;
  const ctx = `forest_event:${String(row.kind)}:${cap(row.detail, 160)}`;
  conn.reducers
    .requestBillionaireDialogue({
      kind: 'callback',
      context: ctx,
      targetIdentity: playerId,
    })
    .then(() => dispatchPending())
    .catch((e: any) => console.warn('[patron] callback request failed:', e?.message || e));
}

// ---------------------------------------------------------------------------
// PUBLIC ENTRY POINTS used by npc-director.ts.
// ---------------------------------------------------------------------------

/**
 * Register the billionaire lane's edge-trigger callbacks. MUST be called inside
 * onConnect, BEFORE .subscribe(), so v2 backfill is captured (mirror the forest lane).
 */
export function registerBillionaireCallbacks(c: DbConnection) {
  const ac = c as AnyConn;
  // Guard: these accessors only exist after binding regen. If absent, log and skip so
  // the forest lane still runs (the billionaire schema hasn't been published yet).
  if (!ac.db.billionaire_request || !ac.reducers.claimBillionaireRequest) {
    console.warn(
      '[patron] billionaire bindings not found — regenerate bindings after publishing the lobby/billionaire schema. Billionaire lane DISABLED this run.',
    );
    return false;
  }
  ac.db.billionaire_request.onInsert(onRequestInsert);
  ac.db.lobby_member.onInsert(onMemberJoin);
  ac.db.lobby_member.onUpdate(onMemberChange);
  ac.db.party_chat.onInsert(onChatInsert);
  ac.db.forest_event.onInsert(onForestEvent);
  return true;
}

/**
 * Start the lane after subscriptions are applied: seed ledgers from backfill, drain the
 * pending backlog oldest-first, then start the safety-net sweep. `ownerRead` tells the
 * lane whether the private cognition/dossier/hook_truth subs are live.
 */
export function startBillionaireLane(c: DbConnection, ownerRead: boolean) {
  conn = c as AnyConn;
  hasOwnerRead = ownerRead;
  if (!conn.db.billionaire_request) return; // disabled (no bindings)

  // Seed per-member ledgers from backfill.
  for (const m of conn.db.lobby_member.iter()) {
    const id = idHex(m.identity);
    prevSwapCount.set(id, Number(m.swapCount ?? 0));
    if (m.nickname) ledgerFor(id).nickname = cap(m.nickname, NICKNAME_CAP);
  }
  // Mirror cross-raid memory once at start (in case the token-gated read fails later).
  if (ownerRead) {
    for (const cg of conn.db.billionaire_cognition.iter()) {
      if (Number(cg.id) === 0) memoryMirror = String(cg.memory || '');
    }
  }

  console.log(
    `[patron] lane online | model: ${anthropic ? MODEL : 'RULE-BASED (no ANTHROPIC_API_KEY)'} | private-read: ${ownerRead ? 'yes' : 'no (in-process persona+memory only)'}`,
  );

  // Drain backlog, then safety-net sweep on top of the onInsert/onForestEvent hot path.
  dispatchPending();
  setInterval(() => {
    try {
      dispatchPending();
    } catch (e: any) {
      console.warn('[patron] sweep error', e?.message || e);
    }
  }, TICK_MS);
}
