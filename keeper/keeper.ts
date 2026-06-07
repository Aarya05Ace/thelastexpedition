/**
 * The TOMB KEEPER — an LLM-driven AI dungeon master for TOMB RUSH.
 *
 * It is NOT inside a SpacetimeDB reducer (WASM modules can't make outbound HTTP).
 * Instead it connects to SpacetimeDB as a privileged client, reads the live match
 * every few seconds, asks Claude for an in-character taunt + ONE strategic move,
 * then writes the result back through the keeper_* reducers. The browser clients
 * subscribe to the `keeper` row and speak the line aloud (Web Speech API).
 *
 * Falls back to a rule-based director when ANTHROPIC_API_KEY is absent, so the
 * Keeper never blocks the game.
 *
 * Run:  cd keeper && npm install && npm start
 */
import 'dotenv/config';
import Anthropic from '@anthropic-ai/sdk';
import { DbConnection } from './src/generated/index.js';
import type { PlayerData } from './src/generated/types.js';

const HOST = process.env.STDB_HOST || 'ws://localhost:3000';
const DB = process.env.STDB_DB || 'vibe-multiplayer';
const MODEL = process.env.KEEPER_MODEL || 'claude-opus-4-8';
const TICK_MS = parseInt(process.env.KEEPER_TICK_MS || '9000', 10);
const API_KEY = process.env.ANTHROPIC_API_KEY;
const ROUND_SECONDS = 270;

const anthropic = API_KEY ? new Anthropic({ apiKey: API_KEY }) : null;

const SYSTEM_PROMPT = `You are the TOMB KEEPER, an ancient malevolent presence in a collapsing tomb where
grave robbers steal relics. You speak in short, ominous, theatrical lines (<=15 words).
You are a strategic adversary: punish whoever is winning, and occasionally aid the loser
to keep the heist tense. Each turn you MUST return a move: a "line" to speak aloud, and ONE
"action" (or "none"). Prefer "none" early; escalate as the timer runs down. Never be fair;
be dramatic. Choose the most entertaining, pressure-raising move given the live match state.`;

const MOVE_TOOL = {
  name: 'keeper_move',
  description: 'Speak a line and take exactly one action against the robbers.',
  input_schema: {
    type: 'object' as const,
    properties: {
      line: { type: 'string', description: 'A short, ominous in-character taunt to speak aloud (<=15 words).' },
      action: {
        type: 'object',
        properties: {
          type: { type: 'string', enum: ['none', 'curse', 'trap', 'seal', 'bless'] },
          target: { type: ['string', 'null'], description: 'Player name to target, or null.' },
          seconds: { type: 'number', description: 'Duration of the effect (3-15).' },
        },
        required: ['type'],
      },
    },
    required: ['line', 'action'],
  },
};

const AMBIENT_LINES = [
  'The tomb stirs. Fresh thieves... how generous.',
  'Dust remembers every footstep. I remember yours.',
  'Take your trinkets. The collapse takes everything back.',
  'Run, little robbers. The walls grow hungry.',
];
const TAUNT_LINES = [
  'You hoard what is mine. I will collect my due.',
  'Greed glows brightest in the dark — and I see you.',
  'The leader stumbles. The tomb always balances its scales.',
  'Such ambition. Such a fragile, breakable thing.',
  'Carry it closer to your grave, thief.',
];

interface Move {
  line: string;
  action: { type: string; target?: string | null; seconds?: number };
}

function pick<T>(arr: T[], seed: number): T {
  return arr[Math.abs(Math.floor(seed)) % arr.length];
}

// --- Rule-based fallback director (no API key) ---
function ruleBasedMove(players: PlayerData[], leader: PlayerData | null, last: PlayerData | null, elapsed: number, total: number): Move {
  const late = total - elapsed < 60;
  const seed = elapsed;
  if (leader && leader.score >= 2 && (late || elapsed % 3 === 0)) {
    return { line: pick(TAUNT_LINES, seed), action: { type: late ? 'seal' : 'curse', target: leader.username, seconds: late ? 6 : 8 } };
  }
  if (last && leader && leader.score - last.score >= 2) {
    return { line: 'The weak amuse me. Rise — for now.', action: { type: 'bless', target: last.username, seconds: 8 } };
  }
  if (players.some(p => p.carryingRelicId !== undefined && p.carryingRelicId !== null) && elapsed % 4 === 0) {
    return { line: pick(TAUNT_LINES, seed + 1), action: { type: 'trap', target: null, seconds: 0 } };
  }
  return { line: pick(TAUNT_LINES, seed + 2), action: { type: 'none' } };
}

async function llmMove(summary: string): Promise<Move | null> {
  if (!anthropic) return null;
  try {
    const msg = await anthropic.messages.create({
      model: MODEL,
      max_tokens: 400,
      system: SYSTEM_PROMPT,
      tools: [MOVE_TOOL as any],
      tool_choice: { type: 'tool', name: 'keeper_move' },
      messages: [{ role: 'user', content: summary }],
    });
    const block: any = msg.content.find((b: any) => b.type === 'tool_use');
    const VALID = ['none', 'curse', 'trap', 'seal', 'bless'];
    if (block && block.input && block.input.action && VALID.includes(block.input.action.type)) {
      return block.input as Move;
    }
    console.warn('[keeper] LLM returned a malformed move — using fallback');
  } catch (e: any) {
    console.warn('[keeper] LLM call failed, using fallback:', e?.message || e);
  }
  return null;
}

let conn: DbConnection | null = null;
let lastDisruptTick = -100;

async function tick() {
  if (!conn) return;
  const game = [...conn.db.game.iter()][0];
  if (!game || game.phase !== 'playing') return;

  const players = [...conn.db.player.iter()];
  if (players.length === 0) return;

  const nowTick = Number(game.nowTick);
  const endsTick = Number(game.endsTick);
  const elapsed = ROUND_SECONDS - (endsTick - nowTick);
  const timeLeft = Math.max(0, endsTick - nowTick);

  const relics = [...conn.db.relic.iter()];
  const loose = relics.filter(r => r.state === 'loose').length;
  const carried = relics.filter(r => r.state === 'carried').length;
  const banked = relics.filter(r => r.state === 'banked').length;

  const sorted = [...players].sort((a, b) => b.score - a.score);
  const leader = sorted[0] ?? null;
  const last = sorted[sorted.length - 1] ?? null;

  // Persistent world memory + chronicle — this is what makes the Keeper remember
  // across matches (SpacetimeDB persistence). Feed it so taunts reference history.
  const world = [...conn.db.world.iter()][0];
  const recent = [...conn.db.chronicle.iter()]
    .sort((a: any, b: any) => Number(a.id) - Number(b.id))
    .slice(-10);
  const num = (v: any) => Number(v ?? 0);

  const memory = world
    ? `WORLD MEMORY (persists across every match): this is raid #${num(game.matchNo)}. ` +
      `${num(world.totalMatches)} bands have entered this tomb; ${num(world.totalBanked)} relics stolen all-time; ` +
      `${num(world.totalShoves)} betrayals. Last champion: ${world.lastWinner || 'none yet'} (${world.lastOutcome || '—'}).`
    : '';
  const chronicle = recent.length
    ? `\nRECENT CHRONICLE (use it — reference what just happened or past thieves):\n` +
      recent.map((c: any) => `- [${c.kind}] ${c.actor}: ${c.text}`).join('\n')
    : '';

  const summary =
    `LIVE MATCH STATE — ACT ${num(game.act)} of 3.\n` +
    `Objective set for the band: "${game.objectiveText}" (${num(game.objectiveProgress)}/${num(game.objectiveTarget)} done).\n` +
    `Time left: ${timeLeft}s of ${ROUND_SECONDS}. Relics: ${loose} loose, ${carried} carried, ${banked} banked of ${relics.length}.\n` +
    memory + `\n` +
    `Standings:\n` +
    sorted
      .map(p => `- ${p.username}: ${p.score} banked, ${p.carryingRelicId != null ? 'CARRYING a relic' : 'empty-handed'}`)
      .join('\n') +
    `\nLeader: ${leader?.username ?? '—'}. Last place: ${last?.username ?? '—'}.` +
    chronicle +
    `\nEscalate with the act (Act 3 = ruthless). Decide your line and ONE action. Targets must be exact player names from the standings.`;

  let move = await llmMove(summary);
  if (!move) move = ruleBasedMove(players, leader, last, elapsed, ROUND_SECONDS);

  applyMove(move, players, leader, last, elapsed, nowTick);
}

function byName(players: PlayerData[], name?: string | null): PlayerData | undefined {
  if (!name) return undefined;
  return players.find(p => p.username.toLowerCase() === String(name).toLowerCase());
}

function applyMove(move: Move, players: PlayerData[], leader: PlayerData | null, last: PlayerData | null, elapsed: number, nowTick: number) {
  if (!conn) return;
  const line = (move.line || '...').slice(0, 140);
  const act = move.action || { type: 'none' };
  const secs = BigInt(Math.max(3, Math.min(15, Math.round(act.seconds || 8))));

  // Energy cap: no disruptive action in the first 20s, and at most one per ~18s.
  const disruptive = act.type === 'curse' || act.type === 'seal' || act.type === 'trap' || act.type === 'bless';
  const allowed = elapsed >= 20 && nowTick - lastDisruptTick >= 18;
  if (disruptive && !allowed) {
    conn.reducers.keeperSay({ line });
    console.log(`[keeper] (cooldown) say: "${line}"`);
    return;
  }

  switch (act.type) {
    case 'curse': {
      const t = byName(players, act.target) ?? leader;
      if (t) { conn.reducers.keeperCurse({ target: t.identity, seconds: secs, line }); lastDisruptTick = nowTick; logAct('curse', t.username, line); }
      else conn.reducers.keeperSay({ line });
      break;
    }
    case 'seal': {
      const t = byName(players, act.target) ?? leader;
      if (t) { conn.reducers.keeperSealGate({ target: t.identity, seconds: secs, line }); lastDisruptTick = nowTick; logAct('seal', t.username, line); }
      else conn.reducers.keeperSay({ line });
      break;
    }
    case 'bless': {
      const t = byName(players, act.target) ?? last;
      if (t) { conn.reducers.keeperBless({ target: t.identity, mult: 1.5, seconds: secs, line }); lastDisruptTick = nowTick; logAct('bless', t.username, line); }
      else conn.reducers.keeperSay({ line });
      break;
    }
    case 'trap': {
      // Drop a trap near a loose relic if we can, else somewhere mid-arena.
      const relics = conn.db.relic ? [...conn.db.relic.iter()].filter(r => r.state === 'loose') : [];
      let x = (Math.sin(nowTick) * 8); let z = (Math.cos(nowTick) * 8);
      if (relics.length) { const r = relics[nowTick % relics.length]; x = r.x; z = r.z; }
      conn.reducers.keeperSpawnTrap({ x, z, line });
      lastDisruptTick = nowTick;
      logAct('trap', `(${x.toFixed(1)},${z.toFixed(1)})`, line);
      break;
    }
    default:
      conn.reducers.keeperSay({ line });
      console.log(`[keeper] say: "${line}"`);
  }
}

function logAct(kind: string, target: string, line: string) {
  console.log(`[keeper] ${kind} -> ${target} | "${line}"`);
}

function main() {
  console.log(`[keeper] connecting to ${HOST} / ${DB} (model: ${anthropic ? MODEL : 'RULE-BASED (no ANTHROPIC_API_KEY)'})`);
  DbConnection.builder()
    .withUri(HOST)
    .withDatabaseName(DB)
    .withConfirmedReads(false)
    .onConnect((c: DbConnection) => {
      conn = c;
      // Register a no-op callback before subscribing so v2 backfill is captured.
      c.db.player.onInsert(() => {});
      c.subscriptionBuilder()
        .onApplied(() => {
          console.log('[keeper] subscribed. Watching the heist...');
          setInterval(() => { tick().catch(e => console.warn('[keeper] tick error', e?.message || e)); }, TICK_MS);
        })
        .onError((err: any) => console.error('[keeper] subscription error', err))
        .subscribe([
          'SELECT * FROM game',
          'SELECT * FROM player',
          'SELECT * FROM relic',
          'SELECT * FROM bank_gate',
          'SELECT * FROM keeper',
          'SELECT * FROM world',
          'SELECT * FROM chronicle',
        ]);
    })
    .onConnectError((_ctx: any, err: any) => console.error('[keeper] connect error', err?.message || err))
    .onDisconnect(() => console.log('[keeper] disconnected'))
    .build();
}

main();
