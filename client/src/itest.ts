/**
 * itest.ts — end-to-end gameplay integration test for TOMB RUSH.
 *
 * Connects two real SpacetimeDB clients and drives them through the full loop:
 *   move -> auto-near a relic -> pickup -> carry to gate -> bank (score++)
 *   then a second robber shoves the carrier and the loot drops.
 *
 * Movement is server-authoritative: to walk a bot toward a point we send
 * `forward` input with yaw = atan2(dx, dz) — the inverse of the server's
 * movement math in player_logic.rs.
 *
 *   Requires local server running + module published.
 *   Run:  cd client && npx tsx src/itest.ts
 */
import { DbConnection } from './generated/index.js';
import type { PlayerData, Relic, BankGate, Game } from './generated/types.js';
import { Identity } from 'spacetimedb';

const HOST = 'ws://localhost:3000';
const DB = 'vibe-multiplayer';

let pass = 0;
let fail = 0;
function check(name: string, cond: boolean) {
  if (cond) { pass++; console.log(`  ✅ ${name}`); }
  else { fail++; console.log(`  ❌ ${name}`); }
}
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

interface Bot {
  conn: DbConnection;
  id: Identity;
  seq: number;
}

function connectBot(name: string, klass: string): Promise<Bot> {
  return new Promise((resolve, reject) => {
    const to = setTimeout(() => reject(new Error(`${name} connect timeout`)), 15000);
    DbConnection.builder()
      .withUri(HOST)
      .withDatabaseName(DB)
      .withConfirmedReads(false)
      .onConnect((conn: DbConnection, id: Identity) => {
        conn.db.player.onInsert(() => {});
        conn.subscriptionBuilder()
          .onApplied(() => {
            conn.reducers.registerPlayer({ username: name, characterClass: klass });
            clearTimeout(to);
            resolve({ conn, id, seq: 0 });
          })
          .onError(reject)
          .subscribe(['SELECT * FROM player', 'SELECT * FROM relic', 'SELECT * FROM bank_gate', 'SELECT * FROM game']);
      })
      .onConnectError((_c: any, e: any) => { clearTimeout(to); reject(e); })
      .build();
  });
}

const me = (b: Bot): PlayerData | undefined =>
  [...b.conn.db.player.iter()].find(p => p.identity.toHexString() === b.id.toHexString());

function sendMove(b: Bot, yaw: number, sprint: boolean) {
  b.seq++;
  b.conn.reducers.updatePlayerInput({
    input: { forward: true, backward: false, left: false, right: false, sprint, jump: false, attack: false, castSpell: false, sequence: b.seq },
    clientPos: { x: 0, y: 0, z: 0 },
    clientRot: { x: 0, y: yaw, z: 0 },
    clientAnimation: sprint ? 'run-forward' : 'walk-forward',
  });
}

async function driveTo(b: Bot, tx: number, tz: number, stopDist: number, maxMs: number): Promise<number> {
  const start = Date.now();
  let dist = Infinity;
  while (Date.now() - start < maxMs) {
    const p = me(b);
    if (p) {
      const dx = tx - p.position.x;
      const dz = tz - p.position.z;
      dist = Math.hypot(dx, dz);
      if (dist <= stopDist) break;
      sendMove(b, Math.atan2(dx, dz), true);
    }
    await sleep(50);
  }
  return dist;
}

async function waitFor<T>(fn: () => T | undefined, ms: number): Promise<T | undefined> {
  const start = Date.now();
  while (Date.now() - start < ms) {
    const v = fn();
    if (v !== undefined && v !== null) return v;
    await sleep(80);
  }
  return undefined;
}

async function main() {
  console.log('\n=== TOMB RUSH end-to-end integration test ===\n');

  console.log('[1] Connecting robber A (Heister)...');
  const a = await connectBot('Heister_IT', 'Wizard');
  await waitFor(() => me(a), 5000);
  check('robber A registered as a player row', !!me(a));

  const game = await waitFor<Game>(() => [...a.conn.db.game.iter()][0], 4000);
  check('game is in "playing" phase after join', game?.phase === 'playing');

  const relics = () => [...a.conn.db.relic.iter()] as Relic[];
  await waitFor(() => (relics().length >= 26 ? true : undefined), 4000);
  check('26 relics spawned', relics().length === 26);

  // [2] Drive A to the nearest loose relic and pick it up.
  const pa = me(a)!;
  const loose = relics().filter(r => r.state === 'loose');
  loose.sort((r1, r2) => Math.hypot(r1.x - pa.position.x, r1.z - pa.position.z) - Math.hypot(r2.x - pa.position.x, r2.z - pa.position.z));
  const targetRelic = loose[0];
  console.log(`[2] Driving A to relic #${targetRelic.id} at (${targetRelic.x.toFixed(1)}, ${targetRelic.z.toFixed(1)})...`);
  const d1 = await driveTo(a, targetRelic.x, targetRelic.z, 1.7, 20000);
  check(`A reached the relic (dist ${d1.toFixed(2)})`, d1 <= 1.9);

  a.conn.reducers.pickupRelic({ relicId: targetRelic.id });
  const carried = await waitFor(() => (me(a)?.carryingRelicId != null ? true : undefined), 3000);
  check('A is now carrying the relic', !!carried);
  const relNow = relics().find(r => r.id === targetRelic.id);
  check('relic state flipped to "carried"', relNow?.state === 'carried');

  // [3] Carry it to A's own gate and bank it.
  const gate = [...a.conn.db.bank_gate.iter()].find((g: BankGate) => g.owner.toHexString() === a.id.toHexString())!;
  console.log(`[3] Carrying to gate at (${gate.x.toFixed(1)}, ${gate.z.toFixed(1)})...`);
  const d2 = await driveTo(a, gate.x, gate.z, 2.8, 20000);
  check(`A reached its gate (dist ${d2.toFixed(2)})`, d2 <= 3.2);

  a.conn.reducers.bankRelic({});
  const scored = await waitFor(() => (me(a)?.score === 1 ? true : undefined), 3000);
  check('A score incremented to 1', !!scored);
  check('A is empty-handed after banking', me(a)?.carryingRelicId == null);
  check('relic state is "banked"', relics().find(r => r.id === targetRelic.id)?.state === 'banked');

  // [4] Shove test: A grabs a second relic, B shoves A, loot drops.
  console.log('[4] Connecting robber B (Bruiser) for the shove test...');
  const b = await connectBot('Bruiser_IT', 'Paladin');
  await waitFor(() => me(b), 5000);
  check('robber B registered', !!me(b));

  const loose2 = relics().filter(r => r.state === 'loose');
  const pa2 = me(a)!;
  loose2.sort((r1, r2) => Math.hypot(r1.x - pa2.position.x, r1.z - pa2.position.z) - Math.hypot(r2.x - pa2.position.x, r2.z - pa2.position.z));
  const relic2 = loose2[0];
  await driveTo(a, relic2.x, relic2.z, 1.7, 20000);
  a.conn.reducers.pickupRelic({ relicId: relic2.id });
  const carried2 = await waitFor(() => (me(a)?.carryingRelicId != null ? true : undefined), 3000);
  check('A grabbed a second relic', !!carried2);

  const pa3 = me(a)!;
  console.log('[4b] Driving B onto A to shove...');
  await driveTo(b, pa3.position.x, pa3.position.z, 2.2, 20000);
  b.conn.reducers.shove({ target: a.id });
  const dropped = await waitFor(() => (me(a)?.carryingRelicId == null ? true : undefined), 3000);
  check('shove forced A to drop the relic', !!dropped);
  check('dropped relic is loose again', relics().find(r => r.id === relic2.id)?.state === 'loose');

  console.log(`\n=== RESULT: ${pass} passed, ${fail} failed ===\n`);
  process.exit(fail === 0 ? 0 : 1);
}

main().catch(e => { console.error('itest crashed:', e); process.exit(2); });
