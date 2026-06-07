/**
 * THE LOST EXPEDITION - lib.rs
 *
 * SpacetimeDB module: a co-op psychological survival game. Players are a
 * search-and-rescue team in a photoreal old-growth forest hunting a lost
 * billionaire. The NPCs (survivors / cultists) are LLM-driven via an external
 * privileged Node "director" process that reads pending interactions from the
 * DB and writes structured results back. WASM reducers CANNOT make outbound
 * HTTP — this module is a pure server-authoritative in/out mailbox.
 *
 * Shared state (all server-authoritative):
 *   player          - the rescue team: position, movement, status (core sync)
 *   npc             - PUBLIC fat row clients render: one per LLM-driven NPC
 *                     (dialogue / animation / action / target / trust / sanity
 *                     + per-NPC mutual-exclusion lock). NO secrets.
 *   npc_cognition   - PRIVATE: persona seed + rolling memory + last raw output
 *   npc_interaction - PUBLIC append-only player->director mailbox + per-row CAS
 *   npc_utterance   - PRIVATE: raw STT transcript (not broadcast)
 *   world_state     - PUBLIC singleton: the now_tick clock + global env inputs
 *
 * Time model: a monotonic `now_tick` (seconds) advanced by the 1Hz scheduled
 * game_tick, stored on world_state(0). All tick fields reference it.
 */

mod common;
mod player_logic;

use spacetimedb::{ReducerContext, Identity, Table, ScheduleAt, Timestamp};
use std::time::Duration;

use crate::common::*;

// ---------------------------------------------------------------------------
// Schema
// ---------------------------------------------------------------------------

#[spacetimedb::table(accessor = player, public)]
#[derive(Clone)]
pub struct PlayerData {
    #[primary_key]
    identity: Identity,
    username: String,
    character_class: String,
    position: Vector3,
    rotation: Vector3,
    health: i32,
    max_health: i32,
    mana: i32,
    max_mana: i32,
    current_animation: String,
    is_moving: bool,
    is_running: bool,
    is_attacking: bool,
    is_casting: bool,
    last_input_seq: u32,
    input: InputState,
    color: String,
}

#[spacetimedb::table(accessor = logged_out_player)]
#[derive(Clone)]
pub struct LoggedOutPlayerData {
    #[primary_key]
    identity: Identity,
    username: String,
    character_class: String,
    color: String,
    last_seen: Timestamp,
}

// PUBLIC. The single fat row clients render — one per LLM-driven survivor/cultist.
// Holds ONLY player-facing structured output + replicated trust/sanity + the
// per-NPC mutual-exclusion lock. NO prompts/secrets/transcripts (public tables
// sync whole rows). Clients subscribe to npc and edge-trigger on `seq`.
#[spacetimedb::table(accessor = npc, public)]
#[derive(Clone)]
pub struct Npc {
    #[primary_key]
    #[auto_inc]
    npc_id: u64,
    display_name: String,           // subtitle attribution + TTS voice selection
    archetype: String,              // survivor | cultist (public: director keys persona map on it)
    position: Vector3,              // spawn/teleport seed + coarse sync ONLY (client derives NavMesh dest)
    dialogue: String,               // subtitle text + TTS source (capped 300 in npc_respond)
    animation_trigger: String,      // Cower|Threaten|Nod|Panic|Idle -> animator.SetTrigger
    game_action: String,            // FLEE|FOLLOW|STAY_PUT|ATTACK -> NavMeshAgent behavior (EXACTLY 4)
    target_player: Option<Identity>,// concrete ref for FLEE/FOLLOW/ATTACK (NO index: Option not FilterableValue)
    trust: i32,                     // server-clamped 0..=100
    sanity: i32,                    // server-clamped 0..=100
    seq: u32,                       // monotonic edge-trigger; bumped once per successful npc_respond
    last_spoke_tick: i64,           // staleness / rate-limit stamp
    busy_until_tick: i64,           // PER-NPC LOCK window (authoritative)
    busy_interaction_id: u64,       // interaction that owns the busy window (0 = free)
}

// PRIVATE (omit `public`). Authoring-only persona seed + rolling memory + last
// raw output. Reducer-write only. The director's PRIMARY persona path is
// in-process keyed by npc_id, built from the PUBLIC npc.archetype; this table
// persists authored personas across module restarts (optional owner-token load).
#[spacetimedb::table(accessor = npc_cognition)]
#[derive(Clone)]
pub struct NpcCognition {
    #[primary_key]
    npc_id: u64,                    // 1:1 with npc; NOT auto_inc (seeded with the npc's assigned id)
    persona: String,                // full per-NPC persona seed (secret)
    memory: String,                 // rolling capped summary of past exchanges
    last_raw_output: String,        // last tool_use JSON for debugging (never synced)
}

// PUBLIC append-only inbox = player->director mailbox AND the per-row
// anti-double-processing CAS. ONE row per voice utterance. The raw STT
// transcript is NOT here (see npc_utterance) so interrogations aren't broadcast.
#[spacetimedb::table(accessor = npc_interaction, public)]
#[derive(Clone)]
pub struct NpcInteraction {
    #[primary_key]
    #[auto_inc]
    id: u64,
    #[index(btree)]
    npc_id: u64,                    // u64 IS FilterableValue
    #[index(btree)]
    asker: Identity,                // ctx.sender() of the speaker (from sender, not an arg — unforgeable)
    #[index(btree)]
    status: String,                 // pending | claimed | done | failed (the CAS guard column)
    claimed_by: Option<Identity>,   // director identity that won the CAS (None until claimed)
    player_distance: f32,           // env snapshot at speak-time
    is_player_armed: bool,          // env snapshot at speak-time
    flashlight_in_face: bool,       // env snapshot at speak-time
    transcript: String,             // the player's (STT) speech — PUBLIC so the anonymous director can read it
    trust_at_ask: i32,              // snapshot of npc.trust at ask-time (audit)
    sanity_at_ask: i32,             // snapshot of npc.sanity at ask-time (audit)
    created_tick: i64,              // FIFO ordering + stale sweep + client timeout cue
    claimed_tick: i64,              // set on claim, advanced by renew_claim heartbeat
}

// PRIVATE companion to npc_interaction holding the raw STT transcript so
// interrogation text is NOT broadcast on a public whole-row sync. Reducer-write
// (ask_npc) only. The director reads it via the owner-token path.
#[spacetimedb::table(accessor = npc_utterance)]
#[derive(Clone)]
pub struct NpcUtterance {
    #[primary_key]
    interaction_id: u64,            // 1:1 with npc_interaction.id
    transcript: String,             // STT result, capped 400 in ask_npc
}

// PUBLIC singleton (id:0). Absorbs now_tick relocated off the deleted Game table —
// without this, cur_tick() and every tick-based check break. Seeded in init BEFORE
// the first tick can fire; game_tick SELF-HEALS by inserting it if missing.
#[spacetimedb::table(accessor = world_state, public)]
#[derive(Clone)]
pub struct WorldState {
    #[primary_key]
    id: u32,                        // singleton, always 0
    now_tick: i64,                  // monotonic 1Hz clock (+1 each game_tick)
    time_of_day: String,            // dawn | day | dusk | night
    weather_conditions: String,     // clear | fog | rain | storm
    patron_dread: i32,              // FEATURE: Tells — composure crosses to forest. 0..=100. Set by set_patron_dread only.
    // EXPEDITION (ADDITIVE — existing C#/TS readers ignore unknown fields). The Acts/Breach/Exfil state.
    current_act: u32,               // 1.. ; the active act (advances on exfil). Default 1.
    breach_progress: f32,           // 0..1 ; one-way within an act; >= BREACH_GOAL -> exfil. Default 0.
    escape_cut_off: bool,           // finale lock: set true when the villain is trapped (no act past 4). Default false.
    act_goal_met: bool,             // run_combine sets true when an is_act_goal combo is satisfied (HUD breach-unlock).
}

// ===========================================================================
// EXPEDITION — CLUE KNOWLEDGE-GRAPH + ACTS/BREACH/EXFIL + PRESSURE LEDGER
// ---------------------------------------------------------------------------
// ADDITIVE: the distributed clue DAG. PUBLIC tables carry ONLY player-safe
// derived facts; the LEAK-GATE secret (who-knows-what + the gate thresholds)
// and the true locations live on PRIVATE tables that never sync to clients.
// The LLM director only PROPOSES (reveal_clue/apply_relationship); the reducer
// re-checks every gate against the DB and is the sole source of truth.
// ===========================================================================

// PUBLIC. Static clue node (DAG vertex). Seeded once (idempotent). `truth` is a
// String enum (validate_truth fallback, mirroring npc_respond's match style).
// overridden_by_clue_id / points_to use 0 / "" sentinels (mirror busy_interaction_id=0).
#[spacetimedb::table(accessor = clue, public)]
#[derive(Clone)]
pub struct Clue {
    #[primary_key]
    #[auto_inc]
    id: u64,
    #[index(btree)]
    code: String,                   // "CL_KEYCODE_DIGITS" (btree: combine/seed lookups by code)
    fact_text: String,              // player-safe derived fact string (capped at reveal-insert)
    truth: String,                  // TRUE | MISLEADING | PARTIAL  (validate_truth fallback)
    overridden_by_clue_id: u64,     // 0 = none; else the clue that DISPUTES this one
    act: u32,                       // 1.. ; the act this clue belongs to (STALE source on advance)
    points_to: String,             // free hint string ("sister"|"keypad"|...); "sister" marks the sister-spine
}

// PUBLIC. Deduction rule (AND-of-clue-ids -> yields a derived clue). Seeded once.
// OR-branches are modeled as TWO combos yielding the SAME yields_clue_id.
#[spacetimedb::table(accessor = clue_combo, public)]
#[derive(Clone)]
pub struct ClueCombo {
    #[primary_key]
    #[auto_inc]
    id: u64,
    required_clue_ids: Vec<u64>,    // ALL must be held (Vec<u64> column is valid in STDB 2.3)
    yields_clue_id: u64,            // the derived clue inserted on satisfy (0 = none)
    is_act_goal: bool,              // true -> run_combine sets world_state.act_goal_met (breach-unlock)
    act: u32,
}

// PRIVATE (omit `public`) — the LEAK-GATE secret; NEVER reaches clients/subscriptions.
// Per-(npc, clue). No composite index (STDB 2.3 has no multi-col index in this file) —
// two single btrees + an in-Rust pair match resolve (npc_id, clue_id).
#[spacetimedb::table(accessor = npc_knowledge)]
#[derive(Clone)]
pub struct NpcKnowledge {
    #[primary_key]
    #[auto_inc]
    id: u64,
    #[index(btree)]
    npc_id: u64,                    // which NPC holds this fragment (btree)
    clue_id: u64,
    min_trust: f32,                 // gate: npc_player_state.trust >= this
    max_fear: f32,                  // gate: npc_player_state.fear  <= this
    required_flag: String,          // "" = none, else must be present in the player's flags CSV
    reveal_if_surrendered: bool,    // bypass gate on surrender (parity with spec; unused in P1)
}

// PUBLIC. Team-owned earned progress (the run's held clue ids). run_id = billionaire.run_id.
#[spacetimedb::table(accessor = party_clue, public)]
#[derive(Clone)]
pub struct PartyClue {
    #[primary_key]
    #[auto_inc]
    id: u64,
    #[index(btree)]
    run_id: u64,                    // groups a run's clues (btree filter, mirrors lobby_hook.run_id)
    #[index(btree)]
    clue_id: u64,                   // btree: dedup "already held?" lookups
    revealed_by_npc: u64,           // 0 for combine-derived clues
    revealed_to: Identity,          // the player who triggered the reveal (Identity::ZERO for derived)
}

// PUBLIC. Player-safe DERIVED facts = the Evidence Board source. NO raw secret here —
// only the fact strings + DISPUTED:/STALE: markers the board renders.
#[spacetimedb::table(accessor = clue_reveal, public)]
#[derive(Clone)]
pub struct ClueReveal {
    #[primary_key]
    #[auto_inc]
    id: u64,
    #[index(btree)]
    run_id: u64,
    clue_code: String,              // "CL_KEYCODE_DIGITS" / "DF_SERVICE_DOOR_OPENABLE" / "DISPUTED:..." / "STALE:..."
    fact_text: String,              // the safe string the board renders (capped)
    act: u32,
}

// PUBLIC. Per-(NPC,player) relationship (the SHARED-clue / PRIVATE-relationship model:
// the relationship is non-secret; the GATE secret lives on npc_knowledge). Single PK +
// two btrees; the (npc_id, player) pair is resolved in Rust (no multi-col index in 2.3).
#[spacetimedb::table(accessor = npc_player_state, public)]
#[derive(Clone)]
pub struct NpcPlayerState {
    #[primary_key]
    #[auto_inc]
    id: u64,
    #[index(btree)]
    npc_id: u64,
    #[index(btree)]
    player: Identity,               // Identity IS Filterable (mirrors npc_interaction.asker)
    trust: f32,                     // 0..1 clamped
    fear: f32,                      // 0..1 clamped
    flags: String,                  // CSV e.g. "proved_sister_intent,protected_me"
    memory_note: String,            // 1-line rolling memory (capped)
    last_interaction: i64,          // cur_tick stamp
}

// PUBLIC. The Pressure Ledger (4 rows, one per act). CSV strings = display-only lists.
#[spacetimedb::table(accessor = villain_ledger, public)]
#[derive(Clone)]
pub struct VillainLedger {
    #[primary_key]
    act: u32,                       // 1..4, NOT auto_inc (act IS the key, mirrors billionaire id=0)
    kept: String,                   // CSV
    lost: String,                   // CSV
    players_carry: String,          // CSV
    composure_tone: String,         // gracious|thinning|bargaining|broken (validate_composure_tone)
}

// PRIVATE (omit `public`) — the TRUE locations. Triple-protected: lives ONLY here,
// unsubscribable by any client. id=0 singleton (mirrors world_state/billionaire).
#[spacetimedb::table(accessor = act_secret)]
#[derive(Clone)]
pub struct ActSecret {
    #[primary_key]
    id: u32,                        // singleton 0 (NOT auto_inc)
    sister_location: String,
    billionaire_location: String,
    sister_clue_act: u32,           // the act sister-clues are bound to (STALE source on advance)
}

#[spacetimedb::table(accessor = game_tick_schedule, public, scheduled(game_tick))]
pub struct GameTickSchedule {
    #[primary_key]
    #[auto_inc]
    scheduled_id: u64,
    scheduled_at: ScheduleAt,
}

// ===========================================================================
// LOBBY / BILLIONAIRE ("The Patron") layer — additive schema
// ---------------------------------------------------------------------------
// 1:1 reuse of the proven PUBLIC-fat-row + PRIVATE-cognition + PUBLIC-inbox-CAS
// + seq-edge-trigger architecture from the forest NPCs. Nothing the forest game
// depends on is modified except WorldState.patron_dread (above) + the additive
// emit_forest_event reducer below.
// ===========================================================================

// PRIVATE (omit `public`). AUTH store — pin_hash/salt/throttle NEVER sync. PK is
// the canonical lowercased username_key (the real uniqueness guarantee). Client
// can NEVER read this table; login success is observed via lobby_member appearing.
#[spacetimedb::table(accessor = account)]
#[derive(Clone)]
pub struct Account {
    #[primary_key]
    username_key: String,           // CANONICAL lowercased+trimmed login key (PK = uniqueness)
    display_name: String,           // as-typed username for render
    pin_hash: String,               // hex(SHA-256(PEPPER || salt || pin)). NEVER plaintext / logged.
    pin_salt: String,               // per-account salt (hex), random u128 mixed with sender+username
    identity: Identity,             // last identity that logged into this account (re-bound on login)
    failed_attempts: i32,           // brute-force throttle counter
    lockout_until_tick: i64,        // if cur_tick < this -> ambiguous error immediately
    created_tick: i64,              // audit / dossier seed
    last_login_tick: i64,           // recency / stale cleanup
}

// PRIVATE. The Dossier (P1 STRETCH) — cross-session memory the Suit USES. 1:1 with
// account (keyed by username_key, NOT auto_inc). Director needs the owner token to
// READ this; non-secret pieces (nickname/grudge) mirror to PUBLIC lobby_member.
#[spacetimedb::table(accessor = account_dossier)]
#[derive(Clone)]
pub struct AccountDossier {
    #[primary_key]
    username_key: String,           // 1:1 with account.username_key
    nickname: String,               // the Suit's sticky coined nickname (also mirrored public)
    dossier_text: String,           // rolling free-text memory (capped, stays private)
    grudge_score: i32,              // cross-run resentment, clamped 0..=100
    runs_seen: i32,                 // increments per run-end
}

// PUBLIC. PARTY = the scoping unit for chat + billionaire reactions + podium lineup.
// `code` PK is the shareable join token (the real uniqueness guarantee). host re-elects
// on leave; member_count drives the full/locked gates; is_open allows future locking.
#[spacetimedb::table(accessor = party, public)]
#[derive(Clone)]
pub struct Party {
    #[primary_key]
    code: String,                   // shareable lobby code (PK = uniqueness); 5 chars, canonical uppercase
    #[index(btree)]
    host: Identity,                 // current host; re-elected to the lowest-slot remaining member on leave
    created_tick: i64,              // audit / sort
    member_count: u32,              // authoritative; gates party full
    is_open: bool,                  // false locks new joins (party locked)
}

// PUBLIC. CHARACTER SELECT + the Suit's live render context. Every client subscribes
// and renders each member's chosen Survivalist. THE row the client watches to confirm
// login. ALSO the no-RLS whisper-delivery channel (each client reads only its OWN row).
#[spacetimedb::table(accessor = lobby_member, public)]
#[derive(Clone)]
pub struct LobbyMember {
    #[primary_key]
    identity: Identity,             // ctx.sender(); unforgeable owner key
    username: String,               // copied from account.display_name on login
    nickname: String,               // PUBLIC mirror of dossier nickname (default "")
    character_id: u32,              // 0..=3 chosen Survivalist; DEFAULT_CHARACTER_ID on join
    is_ready: bool,                 // locked: set_character rejects while ready
    ready_tick: i64,                // cur_tick at last set_ready
    last_activity_tick: i64,        // cur_tick at last action; AFK + auto-request debounce
    swap_count: u32,                // increments each set_character (director reads delta in-process)
    slot: u32,                      // podium position + color, assigned by count() on join
    seq: u32,                       // monotonic edge-trigger on every member mutation
    survival_pct: i32,              // Odds Board (P0). -1 = not priced; 0..=100 when set
    death_prediction: String,       // Odds Board — one-line cause-of-death (capped)
    callback_tag: String,           // Callback Sniper hook (capped); forest event lane reads it
    allegiance: String,             // Judas Offer — loyal|favored|judas|none (validated)
    favor: i32,                     // Playing Favorites — 0..=100, default 50
    is_expedition_lead: bool,       // Favorites — max favor = crown
    is_liability: bool,             // Favorites — min favor = 'Liability'
    whisper_text: String,           // Whisper Network DM on the player's OWN row (UI-private)
    whisper_about: Option<Identity>,// the teammate the whisper is about (NO btree — Option not Filterable)
    whisper_seq: u32,               // edge-trigger: target client pops the private DM
    #[index(btree)]
    party_id: String,               // "" = unpartied; else the Party.code this member is in (String IS Filterable)
}

// PUBLIC seeded set — the 4 selectable Survivalists. Idempotent count()==0 guard.
// Lets the Suit LLM read real names/archetypes + the client render the podium.
#[spacetimedb::table(accessor = lobby_character_catalog, public)]
#[derive(Clone)]
pub struct LobbyCharacterCatalog {
    #[primary_key]
    character_id: u32,              // 0..=3 closed set set_character validates against
    display_name: String,           // 'The Medic', 'The Guide'
    archetype: String,              // medic|scout|brute|tinkerer
    model_key: String,              // client FBX/model lookup key
    blurb: String,                  // one-line podium flavor + LLM context
}

// PUBLIC fat singleton (id=0) — the Suit NPC render row. 1:1 clone of the Npc shape
// (dialogue+seq+animation+busy-lock). Clients edge-trigger on seq. NO secrets.
#[spacetimedb::table(accessor = billionaire, public)]
#[derive(Clone)]
pub struct Billionaire {
    #[primary_key]
    id: u32,                        // singleton, always 0 (NOT auto_inc)
    display_name: String,           // 'The Patron'
    dialogue: String,               // current spoken line (capped) = subtitle + TTS source
    animation_trigger: String,      // Smug|Point|Applaud|Dismiss|Idle (validated, fallback Idle)
    mood: String,                   // smug|impatient|approving|menacing (validated, fallback smug)
    target_member: Option<Identity>,// who the line is aimed at (NO btree — Option not Filterable)
    kind: String,                   // last utterance kind: client routes render/VFX
    seq: u32,                       // monotonic edge-trigger; THE client TTS/bubble trigger
    last_spoke_tick: i64,           // staleness + MIN_GAP stamp + idle one-shot guard
    busy_until_tick: i64,           // PER-BILLIONAIRE LOCK window (authoritative single-flight)
    busy_interaction_id: u64,       // request that owns the busy window (0 = free)
    odds_episode_tick: i64,         // double-ready idempotency sentinel for the all-ready oddsboard
    launch_tick: i64,               // double-launch gate
    run_id: u64,                    // active run id (= launch cur_tick)
}

// PRIVATE persistent persona + composure. 1:1 with billionaire (id=0, NOT auto_inc).
// Persona ALSO pinned as a const in the director, so the lane works with NO token;
// composure crosses via world_state.patron_dread. Token-gated read = memory + hidden_truth.
#[spacetimedb::table(accessor = billionaire_cognition)]
#[derive(Clone)]
pub struct BillionaireCognition {
    #[primary_key]
    id: u32,                        // singleton 0, 1:1 with billionaire
    persona: String,                // persistent SYSTEM PROMPT persona seed (secret)
    memory: String,                 // rolling cross-raid memory (capped)
    voice_id: String,               // pinned ElevenLabs voice id, BAKED const at seed (NOT env)
    composure: i32,                 // Tells — hidden, clamped 0..=100, default 80
    hidden_truth: String,           // the truth the Suit conceals; never synced
    resentment: i32,                // Tells — slow-burn grudge feeding mood drift + Favor
    // AUTH: the director identity, first-claimed by claim_director on startup. require_director
    // checks ctx.sender() against this. ZERO Identity (unset) means the lane is open until claimed
    // (the director must call claim_director once with its owner-token identity to lock the gate).
    director_identity: Identity,
}

// PUBLIC append-only mailbox + per-row CAS — a DIRECT CLONE of npc_interaction. ONE
// extensible `kind` enum funnels EVERY lobby event + @billionaire + whisper through one
// pipeline. Creation is backpressured (has-open dedup + pending cap) in the producers.
#[spacetimedb::table(accessor = billionaire_request, public)]
#[derive(Clone)]
pub struct BillionaireRequest {
    #[primary_key]
    #[auto_inc]
    id: u64,                        // correlation id; client watches its own request
    #[index(btree)]
    kind: String,                   // reaction|ready_monologue|chat_reply|... (btree for kind sweeps)
    #[index(btree)]
    asker: Identity,                // ctx.sender() of triggering/targeted player (Identity = Filterable)
    #[index(btree)]
    status: String,                 // pending|claimed|done|failed — CAS guard
    claimed_by: Option<Identity>,   // director identity that won the CAS (NO btree — Option)
    context: String,                // serialized trigger snapshot OR chat line OR whisper-reply (capped)
    target_identity: Option<Identity>, // the player a beat is ABOUT/FOR (NO btree — Option)
    created_tick: i64,              // FIFO ordering + stale sweep
    claimed_tick: i64,              // set on claim, advanced by renew_billionaire_claim
}

// PUBLIC append-only PARTY CHAT (transcript public so the anonymous director can read it).
// '@billionaire' in body -> debounced chat_reply request. Pruned in game_tick.
#[spacetimedb::table(accessor = party_chat, public)]
#[derive(Clone)]
pub struct PartyChat {
    #[primary_key]
    #[auto_inc]
    id: u64,                        // FIFO id (sort in Rust — no ORDER BY)
    #[index(btree)]
    sender: Identity,               // ctx.sender(); btree for rate-limit lookups
    sender_name: String,            // copied from lobby_member.username for render
    body: String,                   // capped CHAT_BODY_CAP server-side
    #[index(btree)]
    created_tick: i64,              // FIFO + per-sender MIN_GAP rate-limit + prune key
    is_billionaire: bool,           // true if this line is the Suit's clapback echoed into chat
    addressed_billionaire: bool,    // true if body.contains('@billionaire')
    #[index(btree)]
    party_id: String,               // scoping: clients render only rows where party_id == their own party_id
}

// PUBLIC structured CLAIMs the Suit asserts on launch (The Briefing That Lies, P0).
// The forest director is fed ACTIVE hooks by run_id and corroborates/contradicts ONE.
#[spacetimedb::table(accessor = lobby_hook, public)]
#[derive(Clone)]
pub struct LobbyHook {
    #[primary_key]
    #[auto_inc]
    hook_id: u64,                   // id; forest director reads active hooks by run_id
    #[index(btree)]
    run_id: u64,                    // groups hooks for one launch (btree filter)
    subject: String,                // what the claim is about
    assertion: String,              // the falsifiable 'fact' the Suit states (capped). PUBLIC.
    seq: u32,                       // edge-trigger for the briefing overlay
}

// PRIVATE companion 1:1 with lobby_hook (keyed by hook_id, NOT auto_inc) — the HIDDEN
// truth flag. PRIVATE genuinely doesn't sync. Forest director reads it via owner token.
#[spacetimedb::table(accessor = lobby_hook_truth)]
#[derive(Clone)]
pub struct LobbyHookTruth {
    #[primary_key]
    hook_id: u64,                   // 1:1 with lobby_hook.hook_id
    is_lie: bool,                   // true if the assertion is false. NEVER public.
    real_truth: String,             // what's actually true (capped), consistent with hidden_truth
}

// PUBLIC append-only event log the forest reducers append to (died/fled/beat_odds) — the
// Callback Sniper trigger source. The billionaire lane subscribes onInsert -> matches the
// lobby_member's callback_tag -> billionaireRespond{kind:'callback'}.
#[spacetimedb::table(accessor = forest_event, public)]
#[derive(Clone)]
pub struct ForestEvent {
    #[primary_key]
    #[auto_inc]
    event_id: u64,                  // FIFO id; lane drains oldest-first + prunes by cap
    #[index(btree)]
    player_identity: Identity,      // who the event is about (btree to join to lobby_member)
    #[index(btree)]
    kind: String,                   // died|fled|beat_odds (btree + &str-literal filter)
    #[index(btree)]
    run_id: u64,                    // active run so stale-run events are ignored
    detail: String,                 // optional context fed into the callback prompt (capped)
    created_tick: i64,              // FIFO + prune key
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

#[spacetimedb::reducer(init)]
pub fn init(ctx: &ReducerContext) -> Result<(), String> {
    spacetimedb::log::info!("[INIT] THE LOST EXPEDITION module booting...");

    // (1) Seed world_state FIRST — before the schedule row — so cur_tick() is
    //     valid before any tick can fire.
    if ctx.db.world_state().id().find(0u32).is_none() {
        ctx.db.world_state().insert(WorldState {
            id: 0,
            now_tick: 0,
            time_of_day: "dusk".to_string(),
            weather_conditions: "fog".to_string(),
            patron_dread: 0,
            current_act: 1,
            breach_progress: 0.0,
            escape_cut_off: false,
            act_goal_met: false,
        });
    }

    // (2) KEPT VERBATIM: the 1Hz scheduled-reducer infra (count()==0 guard).
    if ctx.db.game_tick_schedule().count() == 0 {
        let schedule = GameTickSchedule {
            scheduled_id: 0,
            scheduled_at: ScheduleAt::Interval(Duration::from_secs(1).into()),
        };
        let _ = ctx.db.game_tick_schedule().try_insert(schedule);
    }

    // (4) Seed the forest roster.
    seed_npcs(ctx);

    // (5) APPEND lobby seeds LAST (order preserved — do NOT reorder above).
    seed_lobby_characters(ctx);
    seed_billionaire(ctx);

    // (6) EXPEDITION seeds — seed_clues MUST run AFTER seed_npcs (npc_knowledge
    //     resolves npc ids by display_name from the just-seeded roster).
    seed_clues(ctx);
    seed_ledger(ctx);
    seed_act_secret(ctx);

    Ok(())
}

#[spacetimedb::reducer(client_connected)]
pub fn identity_connected(ctx: &ReducerContext) {
    spacetimedb::log::info!("Client connected: {}", ctx.sender());
}

#[spacetimedb::reducer(client_disconnected)]
pub fn identity_disconnected(ctx: &ReducerContext) {
    let id = ctx.sender();
    // NPCs persist regardless of client connections; only the player row is
    // snapshotted + removed on disconnect.
    if let Some(player) = ctx.db.player().identity().find(id) {
        ctx.db.logged_out_player().insert(LoggedOutPlayerData {
            identity: player.identity,
            username: player.username.clone(),
            character_class: player.character_class.clone(),
            color: player.color.clone(),
            last_seen: ctx.timestamp,
        });
        ctx.db.player().identity().delete(id);
    }

    // ADD: lobby_member cleanup (account persists — do NOT touch it).
    if let Some(member) = ctx.db.lobby_member().identity().find(id) {
        let leaver_party = member.party_id.clone();

        // PARTY-SCOPED odds reset: only the leaver's OWN party can re-price (do not zero
        // another party's episode on this disconnect).
        let others_all_ready = {
            let mut any = false;
            let mut all = true;
            for m in ctx
                .db
                .lobby_member()
                .iter()
                .filter(|m| m.identity != id && m.party_id == leaver_party)
            {
                any = true;
                if !m.is_ready {
                    all = false;
                    break;
                }
            }
            any && all
        };
        if others_all_ready {
            if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
                b.odds_episode_tick = 0;
                ctx.db.billionaire().id().update(b);
            }
        }

        // MANDATORY party cleanup BEFORE deleting the member row.
        party_member_leave(ctx, id, &leaver_party);

        ctx.db.lobby_member().identity().delete(id);
        // Debounced takeover/exit heckle.
        request_reaction_debounced(ctx, id, "disconnected");
    }
}

// ---------------------------------------------------------------------------
// Join / movement
// ---------------------------------------------------------------------------

#[spacetimedb::reducer]
pub fn register_player(ctx: &ReducerContext, username: String, character_class: String) {
    let id = ctx.sender();
    if ctx.db.player().identity().find(id).is_some() {
        return;
    }

    // Color cycles through a fixed palette by join order.
    let slot = ctx.db.player().count() as usize;
    let colors = ["cyan", "magenta", "yellow", "lightgreen", "orange", "white"];
    let color = colors[slot % colors.len()].to_string();

    // Clear any prior logged-out snapshot for this identity.
    if ctx.db.logged_out_player().identity().find(id).is_some() {
        ctx.db.logged_out_player().identity().delete(id);
    }

    // Spawn at the fixed expedition trailhead.
    let spawn = Vector3 { x: 0.0, y: 1.0, z: 0.0 };

    ctx.db.player().insert(PlayerData {
        identity: id,
        username,
        character_class,
        position: spawn,
        rotation: Vector3 { x: 0.0, y: 0.0, z: 0.0 },
        health: 100,
        max_health: 100,
        mana: 100,
        max_mana: 100,
        current_animation: "idle".to_string(),
        is_moving: false,
        is_running: false,
        is_attacking: false,
        is_casting: false,
        last_input_seq: 0,
        input: default_input(),
        color,
    });
}

#[spacetimedb::reducer]
pub fn update_player_input(
    ctx: &ReducerContext,
    input: InputState,
    client_pos: Vector3,
    client_rot: Vector3,
    client_animation: String,
) {
    if let Some(mut player) = ctx.db.player().identity().find(ctx.sender()) {
        // CLIENT-AUTHORITATIVE movement: the Unity client drives a CharacterController
        // (real collision + gravity vs. the forest) and sends its resulting position.
        // We keep update_input_state for rotation/animation/flags, then trust the client's
        // collided position (overriding the input-derived one).
        player_logic::update_input_state(&mut player, input, client_rot, client_animation, 1.0, false);
        player.position = client_pos;
        ctx.db.player().identity().update(player);
    }
}

// ---------------------------------------------------------------------------
// NPC interaction reducers
// ---------------------------------------------------------------------------

// PLAYER-FACING. The voice/STT entry point. Inserts a pending interaction row
// (the mailbox) + the private transcript companion, then returns the assigned
// interaction id so the client can correlate the eventual seq bump. Does NOT
// call the LLM (WASM can't HTTP) and does NOT mutate dialogue/trust/target.
#[spacetimedb::reducer]
pub fn ask_npc(
    ctx: &ReducerContext,
    npc_id: u64,
    transcript: String,
    player_distance: f32,
    is_player_armed: bool,
    flashlight_in_face: bool,
) -> Result<(), String> {   // reducers cannot return values; client correlates via its own npc_interaction row
    let asker = ctx.sender();
    let now = cur_tick(ctx);

    // (1) Validate the NPC exists.
    let Some(npc) = ctx.db.npc().npc_id().find(npc_id) else {
        return Err("npc not found".to_string());
    };

    // (2) RATE-LIMIT / DEDUP: reject if this asker already has a pending|claimed
    //     interaction for this NPC, or if the NPC spoke too recently.
    let has_open = ctx
        .db
        .npc_interaction()
        .npc_id()
        .filter(npc_id)
        .any(|i| i.asker == asker && (i.status == "pending" || i.status == "claimed"));
    if has_open {
        return Err("you already have a pending question for this npc".to_string());
    }
    if now - npc.last_spoke_tick < MIN_GAP {
        return Err("npc just spoke — wait a moment".to_string());
    }

    // (3) Cap transcript.
    let transcript: String = transcript.chars().take(TRANSCRIPT_CAP).collect();

    // (4) Snapshot live trust/sanity for audit.
    let trust_at_ask = npc.trust;
    let sanity_at_ask = npc.sanity;

    // (5) Insert the mailbox row; read back the assigned id.
    let row = ctx.db.npc_interaction().insert(NpcInteraction {
        id: 0,
        npc_id,
        asker,
        status: "pending".to_string(),
        claimed_by: None,
        player_distance,
        is_player_armed,
        flashlight_in_face,
        transcript: transcript.clone(),
        trust_at_ask,
        sanity_at_ask,
        created_tick: now,
        claimed_tick: 0,
    });
    let interaction_id = row.id;

    // (6) Insert the private transcript companion.
    ctx.db.npc_utterance().insert(NpcUtterance {
        interaction_id,
        transcript,
    });

    // (7) Do NOT write npc.target_player here — resolved only in npc_respond.
    let _ = interaction_id; // (kept for the npc_utterance link above; reducer returns unit)
    Ok(())
}

// Director only. PER-ROW CAS + PER-NPC LOCK. STDB serializes each reducer as one
// transaction: two directors/retries racing one row -> exactly one wins.
#[spacetimedb::reducer]
pub fn claim_interaction(ctx: &ReducerContext, interaction_id: u64) -> Result<(), String> {
    let now = cur_tick(ctx);

    let Some(mut interaction) = ctx.db.npc_interaction().id().find(interaction_id) else {
        return Err("interaction not found".to_string());
    };
    if interaction.status != "pending" {
        return Err("interaction not pending (lost race / already handled)".to_string());
    }

    // PER-NPC MUTEX: the real two-players-one-NPC serialization.
    let Some(mut npc) = ctx.db.npc().npc_id().find(interaction.npc_id) else {
        return Err("npc not found".to_string());
    };
    if npc.busy_until_tick > now && npc.busy_interaction_id != interaction_id {
        return Err("npc busy".to_string());
    }

    // Win: claim the row + lock the NPC.
    interaction.status = "claimed".to_string();
    interaction.claimed_by = Some(ctx.sender());
    interaction.claimed_tick = now;
    ctx.db.npc_interaction().id().update(interaction);

    npc.busy_until_tick = now + CLAIM_TIMEOUT_TICKS;
    npc.busy_interaction_id = interaction_id;
    ctx.db.npc().npc_id().update(npc);

    Ok(())
}

// Director only. Heartbeat while an LLM call is in flight: advances claimed_tick
// (and the NPC busy window) so game_tick's stale sweep doesn't reclaim a slow
// but alive call. Director calls it every ~10-20s while awaiting Claude.
#[spacetimedb::reducer]
pub fn renew_claim(ctx: &ReducerContext, interaction_id: u64) -> Result<(), String> {
    let now = cur_tick(ctx);

    let Some(mut interaction) = ctx.db.npc_interaction().id().find(interaction_id) else {
        return Err("interaction not found".to_string());
    };
    if interaction.status != "claimed" || interaction.claimed_by != Some(ctx.sender()) {
        return Err("not your active claim".to_string());
    }

    let npc_id = interaction.npc_id;
    interaction.claimed_tick = now;
    ctx.db.npc_interaction().id().update(interaction);

    if let Some(mut npc) = ctx.db.npc().npc_id().find(npc_id) {
        if npc.busy_interaction_id == interaction_id {
            npc.busy_until_tick = now + CLAIM_TIMEOUT_TICKS;
            ctx.db.npc().npc_id().update(npc);
        }
    }

    Ok(())
}

// Director only. The write-back, in ONE transaction. STRICT GUARD: the row must
// be claimed by the caller (closes the reclaim double-apply race). Trust/sanity
// are clamped server-side on the LIVE row (deltas never trusted); enums are
// validated with safe fallbacks; target_player resolved HERE atomic with seq.
#[spacetimedb::reducer]
pub fn npc_respond(
    ctx: &ReducerContext,
    npc_id: u64,
    interaction_id: u64,
    dialogue: String,
    animation_trigger: String,
    game_action: String,
    target_player: Option<Identity>,
    trust_change: i32,
    sanity_change: i32,
) -> Result<(), String> {
    let now = cur_tick(ctx);

    // STRICT GUARD.
    let Some(mut interaction) = ctx.db.npc_interaction().id().find(interaction_id) else {
        return Err("interaction not found".to_string());
    };
    if interaction.status != "claimed" || interaction.claimed_by != Some(ctx.sender()) {
        return Err("not your active claim".to_string());
    }

    let Some(mut npc) = ctx.db.npc().npc_id().find(npc_id) else {
        return Err("npc not found".to_string());
    };

    // Validate the closed enums; fall back so a client never gets garbage.
    let animation_trigger = match animation_trigger.as_str() {
        "Cower" | "Threaten" | "Nod" | "Panic" | "Idle" => animation_trigger,
        _ => "Idle".to_string(),
    };
    let game_action = match game_action.as_str() {
        "FLEE" | "FOLLOW" | "STAY_PUT" | "ATTACK" => game_action,
        _ => "STAY_PUT".to_string(),
    };

    // Clamp the ABSOLUTE result on the LIVE row (not the delta).
    npc.trust = (npc.trust + trust_change).clamp(0, 100);
    npc.sanity = (npc.sanity + sanity_change).clamp(0, 100);

    npc.dialogue = dialogue.chars().take(DIALOGUE_CAP).collect();
    npc.animation_trigger = animation_trigger;
    npc.game_action = game_action;
    npc.target_player = target_player; // resolved HERE, atomic with seq
    npc.seq += 1;
    npc.last_spoke_tick = now;

    // OWNERSHIP-SCOPED busy clear: only free the window THIS interaction owns.
    if npc.busy_interaction_id == interaction_id {
        npc.busy_until_tick = 0;
        npc.busy_interaction_id = 0;
    }
    ctx.db.npc().npc_id().update(npc);

    interaction.status = "done".to_string();
    ctx.db.npc_interaction().id().update(interaction);

    Ok(())
}

// Director only. Graceful-degrade last resort (the normal path uses npc_respond
// even for the rule-based fallback line so an NPC never freezes). Marks the row
// failed + ownership-clears the busy window.
#[spacetimedb::reducer]
pub fn fail_interaction(ctx: &ReducerContext, interaction_id: u64, reason: String) -> Result<(), String> {
    let Some(mut interaction) = ctx.db.npc_interaction().id().find(interaction_id) else {
        return Err("interaction not found".to_string());
    };
    if interaction.status != "claimed" || interaction.claimed_by != Some(ctx.sender()) {
        return Err("not your active claim".to_string());
    }

    let npc_id = interaction.npc_id;
    interaction.status = "failed".to_string();
    ctx.db.npc_interaction().id().update(interaction);

    if let Some(mut npc) = ctx.db.npc().npc_id().find(npc_id) {
        if npc.busy_interaction_id == interaction_id {
            npc.busy_until_tick = 0;
            npc.busy_interaction_id = 0;
            ctx.db.npc().npc_id().update(npc);
        }
    }

    spacetimedb::log::info!("[fail_interaction] {} reason: {}", interaction_id, reason);
    Ok(())
}

// Director/admin. Setter for the slow global env on world_state(0). Read by the
// director into environment.timeOfDay / weatherConditions.
#[spacetimedb::reducer]
pub fn set_world_state(ctx: &ReducerContext, time_of_day: String, weather_conditions: String) {
    if let Some(mut ws) = ctx.db.world_state().id().find(0u32) {
        ws.time_of_day = time_of_day;
        ws.weather_conditions = weather_conditions;
        ctx.db.world_state().id().update(ws);
    }
}

// Director only. Writes a rolling one-line summary to the PRIVATE npc_cognition
// row (reducer write = full DB access; no owner-token needed for WRITES).
// Optional — the director may instead keep memory in-process keyed by npc_id.
#[spacetimedb::reducer]
pub fn update_npc_memory(ctx: &ReducerContext, npc_id: u64, memory: String) {
    if let Some(mut cog) = ctx.db.npc_cognition().npc_id().find(npc_id) {
        cog.memory = memory;
        ctx.db.npc_cognition().npc_id().update(cog);
    }
}

// ===========================================================================
// LOBBY / BILLIONAIRE ("The Patron") — helpers (plain fns, NOT #[reducer])
// ===========================================================================

// AUTH HARD GATE. Every director-only billionaire reducer calls this FIRST so a
// client can never forge favor/crown/allegiance/whispers/persona writes.
//
// IMPLEMENTATION NOTE (verified against spacetimedb 2.3.0): a reducer CANNOT read
// the publisher/owner client identity at runtime — `ctx.database_identity()` is the
// MODULE identity (never equal to ctx.sender()), and `sender_auth()` exposes no
// owner/bypass-RLS flag in the module SDK. So we use a RUNTIME-CLAIMED director
// identity stored on the PRIVATE billionaire_cognition row: the director calls
// `claim_director` once on startup (first-caller-wins) with its owner-token identity,
// and every director-only reducer checks ctx.sender() against it.
//
// Until claimed (director_identity == ZERO) the gate is OPEN (so the lane functions
// during bootstrap / before the director attaches). Once claimed it is LOCKED to that
// one identity. The director should call claim_director immediately on connect.
fn require_director(ctx: &ReducerContext) -> Result<(), String> {
    let claimed = ctx
        .db
        .billionaire_cognition()
        .id()
        .find(0u32)
        .map(|c| c.director_identity)
        .unwrap_or(Identity::ZERO);
    if claimed == Identity::ZERO || ctx.sender() == claimed {
        Ok(())
    } else {
        Err("director only".to_string())
    }
}

// Count of currently-open (pending|claimed) billionaire_request rows — global
// backpressure cap so a swap/@billionaire flood can't outrun the single-flight Suit.
fn open_request_count(ctx: &ReducerContext) -> usize {
    ctx.db
        .billionaire_request()
        .status()
        .filter("pending")
        .count() as usize
        + ctx
            .db
            .billionaire_request()
            .status()
            .filter("claimed")
            .count() as usize
}

// True if this asker already has a pending|claimed request of the given kind (has-open
// dedup mirroring ask_npc). PASS &str LITERALS to .filter() (never a String var).
fn asker_has_open_kind(ctx: &ReducerContext, asker: Identity, kind: &str) -> bool {
    ctx.db
        .billionaire_request()
        .asker()
        .filter(asker)
        .any(|r| r.kind == kind && (r.status == "pending" || r.status == "claimed"))
}

// Insert a debounced {kind:'reaction'} request: skip if this asker already has an
// open reaction OR the global pending cap is hit. The triggering mutation itself
// stays unconditional/snappy at the call site.
fn request_reaction_debounced(ctx: &ReducerContext, asker: Identity, context: &str) {
    if asker_has_open_kind(ctx, asker, "reaction") {
        return;
    }
    if open_request_count(ctx) >= REQUEST_PENDING_CAP {
        return;
    }
    let now = cur_tick(ctx);
    let ctx_str: String = context.chars().take(CONTEXT_CAP).collect();
    ctx.db.billionaire_request().insert(BillionaireRequest {
        id: 0,
        kind: "reaction".to_string(),
        asker,
        status: "pending".to_string(),
        claimed_by: None,
        context: ctx_str,
        target_identity: None,
        created_tick: now,
        claimed_tick: 0,
    });
}

// ===========================================================================
// PARTY — plain helpers (NOT #[reducer])
// ===========================================================================

// Allocate a fresh unused party code. Draws a FRESH ctx.random() u128 inside the loop
// each try (same StdbRng pattern as register_account's salt_n), then maps PARTY_CODE_LEN
// 5-bit chunks into PARTY_CODE_ALPHABET. The PK collision check (find().is_none()) is the
// real uniqueness guarantee; the retry loop just avoids the rare insert-conflict. Returns
// None only if every try collided (effectively never with a 32^5 space).
fn alloc_party_code(ctx: &ReducerContext) -> Option<String> {
    let alphabet = PARTY_CODE_ALPHABET.as_bytes();
    for _ in 0..PARTY_CODE_TRIES {
        let n: u128 = ctx.random();
        let code: String = (0..PARTY_CODE_LEN)
            .map(|i| {
                let idx = ((n >> (i * 5)) & 0x1F) as usize;
                alphabet[idx] as char
            })
            .collect();
        if ctx.db.party().code().find(&code).is_none() {
            return Some(code);
        }
    }
    None
}

// The caller's current party code ("" if unpartied / not in lobby).
#[allow(dead_code)]
fn caller_party(ctx: &ReducerContext, id: Identity) -> String {
    ctx.db
        .lobby_member()
        .identity()
        .find(id)
        .map(|m| m.party_id)
        .unwrap_or_default()
}

// Shared party-membership teardown for a member leaving a party (used by leave_party,
// leave_lobby, identity_disconnected). Decrements member_count; deletes the Party row when
// it empties; otherwise re-elects the host to the lowest-slot remaining same-party member
// if the leaver was host. `leaver_id` is the member departing; `code` is their party.
fn party_member_leave(ctx: &ReducerContext, leaver_id: Identity, code: &str) {
    if code.is_empty() {
        return;
    }
    let Some(mut p) = ctx.db.party().code().find(&code.to_string()) else {
        return;
    };
    p.member_count = p.member_count.saturating_sub(1);
    if p.member_count == 0 {
        ctx.db.party().code().delete(&code.to_string());
        return;
    }
    // Re-elect host to the lowest-slot remaining member of this party (excluding the leaver).
    if p.host == leaver_id {
        let new_host = ctx
            .db
            .lobby_member()
            .iter()
            .filter(|m| m.identity != leaver_id && m.party_id == code)
            .min_by_key(|m| m.slot)
            .map(|m| m.identity);
        if let Some(h) = new_host {
            p.host = h;
        }
    }
    ctx.db.party().code().update(p);
}

// Idempotent lobby join used by register_account/login (NOT a #[reducer]). Refreshes
// an existing member; else inserts a fresh row with the default character + debounced greet.
fn join_lobby(ctx: &ReducerContext, display_name: String, _key: String) {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    if let Some(mut m) = ctx.db.lobby_member().identity().find(id) {
        m.username = display_name;
        m.seq += 1;
        m.last_activity_tick = now;
        ctx.db.lobby_member().identity().update(m);
        return;
    }

    let slot = ctx.db.lobby_member().count() as u32;
    ctx.db.lobby_member().insert(LobbyMember {
        identity: id,
        username: display_name,
        nickname: String::new(),
        character_id: DEFAULT_CHARACTER_ID,
        is_ready: false,
        ready_tick: 0,
        last_activity_tick: now,
        swap_count: 0,
        slot,
        seq: 0,
        survival_pct: -1,
        death_prediction: String::new(),
        callback_tag: String::new(),
        allegiance: "none".to_string(),
        favor: 50,
        is_expedition_lead: false,
        is_liability: false,
        whisper_text: String::new(),
        whisper_about: None,
        whisper_seq: 0,
        party_id: String::new(),
    });

    // Mirror any persisted dossier nickname/grudge onto the PUBLIC row (so it crosses
    // without the owner token). grudge_score itself stays private; only nickname rides.
    if let Some(d) = ctx.db.account_dossier().username_key().find(&_key) {
        if !d.nickname.is_empty() {
            if let Some(mut m) = ctx.db.lobby_member().identity().find(id) {
                m.nickname = d.nickname;
                m.seq += 1;
                ctx.db.lobby_member().identity().update(m);
            }
        }
    }

    // Debounced auto-greet.
    request_reaction_debounced(ctx, id, "joined");
}

// Compute hex(SHA-256(PEPPER || salt_bytes || pin_bytes)). salt is hex-encoded.
fn compute_pin_hash(salt_hex: &str, pin: &str) -> String {
    use sha2::{Digest, Sha256};
    let mut hasher = Sha256::new();
    hasher.update(PEPPER.as_bytes());
    // Best-effort decode of the hex salt; if it ever fails (it won't — we encode it),
    // fall back to the raw salt bytes so a hash is still produced deterministically.
    match hex::decode(salt_hex) {
        Ok(bytes) => hasher.update(&bytes),
        Err(_) => hasher.update(salt_hex.as_bytes()),
    }
    hasher.update(pin.as_bytes());
    hex::encode(hasher.finalize())
}

// Server-side username normalization + validation. Returns the canonical key.
fn normalize_username(username: &str) -> Result<String, String> {
    let key = username.trim().to_lowercase();
    if key.is_empty() {
        return Err("username required".to_string());
    }
    if key.len() < USERNAME_MIN || key.len() > USERNAME_MAX {
        return Err(format!(
            "username must be {}-{} characters",
            USERNAME_MIN, USERNAME_MAX
        ));
    }
    if !key.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
        return Err("username may only contain letters, numbers, and underscore".to_string());
    }
    Ok(key)
}

fn validate_pin(pin: &str) -> Result<(), String> {
    if pin.chars().count() != PIN_LEN || !pin.chars().all(|c| c.is_ascii_digit()) {
        return Err(format!("pin must be exactly {} digits", PIN_LEN));
    }
    Ok(())
}

// ===========================================================================
// LOBBY / BILLIONAIRE — auth reducers
// ===========================================================================

// AUTH. First-time registration. Normalizes + validates server-side, salts with a
// random u128 mixed with the sender + key (defense-in-depth), PEPPERs + SHA-256s the
// pin into a PRIVATE account row, then joins the lobby. NEVER logs pin/hash/salt.
#[spacetimedb::reducer]
pub fn register_account(ctx: &ReducerContext, username: String, pin: String) -> Result<(), String> {
    let now = cur_tick(ctx);
    let key = normalize_username(&username)?;
    validate_pin(&pin)?;

    // Friendly dup message (the PK insert is the real uniqueness guarantee).
    if ctx.db.account().username_key().find(&key).is_some() {
        return Err("username taken".to_string());
    }

    // Salt: random u128 (Standard distribution; StdbRng is timestamp-seeded/non-crypto —
    // the PEPPER is what closes the offline hole) mixed with sender bytes + key.
    let salt_n: u128 = ctx.random();
    let mut salt_bytes = salt_n.to_le_bytes().to_vec(); // 16 bytes
    let n = salt_bytes.len();
    let sender_bytes = ctx.sender().to_byte_array();
    for (i, b) in sender_bytes.iter().enumerate() {
        salt_bytes[i % n] ^= *b;
    }
    for (i, b) in key.as_bytes().iter().enumerate() {
        salt_bytes[i % n] ^= *b;
    }
    let pin_salt = hex::encode(&salt_bytes);
    let pin_hash = compute_pin_hash(&pin_salt, &pin);

    let display_name: String = username.trim().chars().take(USERNAME_MAX).collect();

    ctx.db.account().insert(Account {
        username_key: key.clone(),
        display_name: display_name.clone(),
        pin_hash,
        pin_salt,
        identity: ctx.sender(),
        failed_attempts: 0,
        lockout_until_tick: 0,
        created_tick: now,
        last_login_tick: now,
    });

    // Seed the dossier 1:1 if absent.
    if ctx.db.account_dossier().username_key().find(&key).is_none() {
        ctx.db.account_dossier().insert(AccountDossier {
            username_key: key.clone(),
            nickname: String::new(),
            dossier_text: String::new(),
            grudge_score: 0,
            runs_seen: 0,
        });
    }

    join_lobby(ctx, display_name, key);
    Ok(())
}

// AUTH. Returning login. SAME ambiguous error for no-account / bad-pin / locked-out
// (no enumeration). Brute-force throttle on the PRIVATE row. Re-binds identity on success.
#[spacetimedb::reducer]
pub fn login(ctx: &ReducerContext, username: String, pin: String) -> Result<(), String> {
    let now = cur_tick(ctx);
    // Normalize but DON'T leak validation specifics on the login path — collapse to AUTH_FAIL.
    let key = match normalize_username(&username) {
        Ok(k) => k,
        Err(_) => return Err(AUTH_FAIL_MSG.to_string()),
    };

    let Some(mut account) = ctx.db.account().username_key().find(&key) else {
        return Err(AUTH_FAIL_MSG.to_string());
    };

    // Brute-force lockout window.
    if now < account.lockout_until_tick {
        return Err(AUTH_FAIL_MSG.to_string());
    }

    let candidate = compute_pin_hash(&account.pin_salt, &pin);
    if candidate != account.pin_hash {
        account.failed_attempts += 1;
        if account.failed_attempts >= LOGIN_MAX_ATTEMPTS {
            account.lockout_until_tick = now + LOGIN_LOCKOUT_TICKS;
            account.failed_attempts = 0;
        }
        ctx.db.account().username_key().update(account);
        return Err(AUTH_FAIL_MSG.to_string());
    }

    // Success.
    let prior_identity = account.identity;
    account.failed_attempts = 0;
    account.lockout_until_tick = 0;
    account.identity = ctx.sender();
    account.last_login_tick = now;
    let display_name = account.display_name.clone();
    ctx.db.account().username_key().update(account);

    join_lobby(ctx, display_name, key.clone());

    // Observable takeover if a NEW anon identity reclaimed the account.
    if prior_identity != ctx.sender() {
        request_reaction_debounced(ctx, ctx.sender(), "identity_rebound");
    }

    Ok(())
}

// ===========================================================================
// LOBBY / BILLIONAIRE — character select + lobby flow reducers
// ===========================================================================

// CHARACTER SELECT. Swap write is UNCONDITIONAL/snappy; the LLM trigger is debounced
// (MIN_GAP + has-open dedup) so swap-spam can't flood the Suit.
#[spacetimedb::reducer]
pub fn set_character(ctx: &ReducerContext, character_id: u32) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    if member.is_ready {
        return Err("locked — unready first".to_string());
    }

    // Validate the closed set (mirror npc_respond).
    let validated = match character_id {
        0 | 1 | 2 | 3 => character_id,
        _ => DEFAULT_CHARACTER_ID,
    };

    let prev_activity = member.last_activity_tick;
    member.character_id = validated;
    member.swap_count += 1;
    member.seq += 1;
    member.last_activity_tick = now;
    ctx.db.lobby_member().identity().update(member);

    // Debounced LLM trigger: only when NOT spamming AND no open reaction.
    if now - prev_activity >= MIN_GAP && !asker_has_open_kind(ctx, id, "reaction") {
        if open_request_count(ctx) < REQUEST_PENDING_CAP {
            ctx.db.billionaire_request().insert(BillionaireRequest {
                id: 0,
                kind: "reaction".to_string(),
                asker: id,
                status: "pending".to_string(),
                claimed_by: None,
                context: format!("swap:{}", validated),
                target_identity: None,
                created_tick: now,
                claimed_tick: 0,
            });
        }
    }

    Ok(())
}

// READY. On all-ready, fire the gated oddsboard episode (double-ready safe).
#[spacetimedb::reducer]
pub fn set_ready(ctx: &ReducerContext) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    let pid = member.party_id.clone();
    member.is_ready = true;
    member.ready_tick = now;
    member.last_activity_tick = now;
    member.seq += 1;
    ctx.db.lobby_member().identity().update(member);

    // PARTY-SCOPED all-ready: only members sharing the caller's party_id count
    // (unpartied "" members form their own group). odds_episode_tick stays a global singleton.
    let party: Vec<LobbyMember> = ctx
        .db
        .lobby_member()
        .iter()
        .filter(|m| m.party_id == pid)
        .collect();
    let all_ready = !party.is_empty() && party.iter().all(|m| m.is_ready);
    if !all_ready {
        request_reaction_debounced(ctx, id, "ready");
        return Ok(());
    }

    // ALL READY (this party). Gate the oddsboard on the max-ready-tick episode sentinel,
    // computed over THIS party only.
    let episode = party
        .iter()
        .map(|m| m.ready_tick)
        .max()
        .unwrap_or(now);

    let Some(mut b) = ctx.db.billionaire().id().find(0u32) else {
        return Ok(());
    };
    let already_pricing = ctx
        .db
        .billionaire_request()
        .kind()
        .filter("ready_monologue")
        .any(|r| r.status == "pending" || r.status == "claimed");
    if b.odds_episode_tick != episode && !already_pricing {
        b.odds_episode_tick = episode;
        ctx.db.billionaire().id().update(b);
        ctx.db.billionaire_request().insert(BillionaireRequest {
            id: 0,
            kind: "ready_monologue".to_string(),
            asker: id,
            status: "pending".to_string(),
            claimed_by: None,
            context: "all_ready".to_string(),
            target_identity: None,
            created_tick: now,
            claimed_tick: 0,
        });
    }

    Ok(())
}

#[spacetimedb::reducer]
pub fn unready(ctx: &ReducerContext) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    member.is_ready = false;
    member.last_activity_tick = now;
    member.seq += 1;
    ctx.db.lobby_member().identity().update(member);

    // A new all-ready episode can re-price.
    if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
        b.odds_episode_tick = 0;
        ctx.db.billionaire().id().update(b);
    }

    request_reaction_debounced(ctx, id, "unready");
    Ok(())
}

#[spacetimedb::reducer]
pub fn leave_lobby(ctx: &ReducerContext) -> Result<(), String> {
    let id = ctx.sender();

    let Some(member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    let leaver_party = member.party_id.clone();

    // PARTY-SCOPED odds reset: if the OTHER members of the LEAVER'S OWN party were all
    // ready, a fresh all-ready can re-price (leaving party A must not zero party B's episode).
    let others_all_ready = {
        let mut any = false;
        let mut all = true;
        for m in ctx
            .db
            .lobby_member()
            .iter()
            .filter(|m| m.identity != id && m.party_id == leaver_party)
        {
            any = true;
            if !m.is_ready {
                all = false;
                break;
            }
        }
        any && all
    };
    if others_all_ready {
        if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
            b.odds_episode_tick = 0;
            ctx.db.billionaire().id().update(b);
        }
    }

    // MANDATORY party cleanup BEFORE deleting the member row (else member_count inflates
    // forever -> permanent 'party full').
    party_member_leave(ctx, id, &leaver_party);

    ctx.db.lobby_member().identity().delete(id);
    // Account persists. Slot gaps are fine — do not renumber.
    request_reaction_debounced(ctx, id, "left");
    Ok(())
}

// ===========================================================================
// PARTY — create / join / leave (LOCKED contract)
// ===========================================================================

// Create a fresh party with the caller as sole member + host. Client re-reads its OWN
// LobbyMember.party_id after commit (no return value). A ready member must unready first
// (mirrors set_character's lock — membership must not mutate under an in-flight all-ready).
#[spacetimedb::reducer]
pub fn create_party(ctx: &ReducerContext) -> Result<(), String> {
    let id = ctx.sender();
    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    if member.party_id != "" {
        return Err("already in a party".to_string());
    }
    if member.is_ready {
        return Err("unready first".to_string());
    }

    let code: String = alloc_party_code(ctx).ok_or("could not allocate party code")?;

    let now = cur_tick(ctx);
    ctx.db.party().insert(Party {
        code: code.clone(),
        host: id,
        created_tick: now,
        member_count: 1,
        is_open: true,
    });

    member.party_id = code; // code moved LAST
    member.seq += 1;
    member.last_activity_tick = now;
    ctx.db.lobby_member().identity().update(member);
    Ok(())
}

// Join an existing party by code. Normalizes the code (trim + uppercase) server-side.
#[spacetimedb::reducer]
pub fn join_party(ctx: &ReducerContext, code: String) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    if member.party_id != "" {
        return Err("already in a party".to_string());
    }
    if member.is_ready {
        return Err("unready first".to_string());
    }

    let code: String = code.trim().to_uppercase();
    let Some(mut p) = ctx.db.party().code().find(&code) else {
        return Err("no such party".to_string());
    };
    if !p.is_open {
        return Err("party locked".to_string());
    }
    if p.member_count >= PARTY_MAX_MEMBERS {
        return Err("party full".to_string());
    }

    member.party_id = code.clone();
    member.seq += 1;
    member.last_activity_tick = now;
    ctx.db.lobby_member().identity().update(member);

    p.member_count += 1;
    ctx.db.party().code().update(p);

    // Debounced 'someone joined the party' heckle.
    request_reaction_debounced(ctx, id, "joined_party");
    Ok(())
}

// Leave the caller's current party. Clears is_ready (membership change must drop readiness).
#[spacetimedb::reducer]
pub fn leave_party(ctx: &ReducerContext) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };
    if member.party_id == "" {
        return Err("not in a party".to_string());
    }

    let code: String = member.party_id.clone();
    party_member_leave(ctx, id, &code);

    member.party_id = String::new();
    member.is_ready = false;
    member.seq += 1;
    member.last_activity_tick = now;
    ctx.db.lobby_member().identity().update(member);
    Ok(())
}

// ===========================================================================
// LOBBY / BILLIONAIRE — chat + generic request mailbox
// ===========================================================================

#[spacetimedb::reducer]
pub fn send_chat(ctx: &ReducerContext, body: String) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(mut member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not authed".to_string());
    };

    // Per-sender rate-limit on the latest line from this sender.
    let latest_tick = ctx
        .db
        .party_chat()
        .sender()
        .filter(id)
        .map(|c| c.created_tick)
        .max();
    if let Some(t) = latest_tick {
        if now - t < MIN_GAP {
            return Err("slow down".to_string());
        }
    }

    let body: String = body.chars().take(CHAT_BODY_CAP).collect();
    let addressed = body.contains("@billionaire");

    ctx.db.party_chat().insert(PartyChat {
        id: 0,
        sender: id,
        sender_name: member.username.clone(),
        body: body.clone(),
        created_tick: now,
        is_billionaire: false,
        addressed_billionaire: addressed,
        party_id: member.party_id.clone(),
    });

    member.last_activity_tick = now;
    member.seq += 1;
    ctx.db.lobby_member().identity().update(member);

    // @billionaire -> debounced chat_reply (coalesce + global cap).
    if addressed
        && !asker_has_open_kind(ctx, id, "chat_reply")
        && open_request_count(ctx) < REQUEST_PENDING_CAP
    {
        ctx.db.billionaire_request().insert(BillionaireRequest {
            id: 0,
            kind: "chat_reply".to_string(),
            asker: id,
            status: "pending".to_string(),
            claimed_by: None,
            context: body,
            target_identity: None,
            created_tick: now,
            claimed_tick: 0,
        });
    }

    Ok(())
}

// Generic client-initiated mailbox for beats not auto-fired (whisper_reply, loyalty,
// manual taunt). has-open dedup per kind + global cap.
#[spacetimedb::reducer]
pub fn request_billionaire_dialogue(
    ctx: &ReducerContext,
    kind: String,
    context: String,
    target_identity: Option<Identity>,
) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    if ctx.db.lobby_member().identity().find(id).is_none() {
        return Err("not in lobby".to_string());
    }

    let kind = validate_request_kind(&kind);
    let context: String = context.chars().take(CONTEXT_CAP).collect();

    if asker_has_open_kind(ctx, id, &kind) {
        return Err("already pending".to_string());
    }
    if open_request_count(ctx) >= REQUEST_PENDING_CAP {
        return Err("the Patron is busy".to_string());
    }

    ctx.db.billionaire_request().insert(BillionaireRequest {
        id: 0,
        kind,
        asker: id,
        status: "pending".to_string(),
        claimed_by: None,
        context,
        target_identity,
        created_tick: now,
        claimed_tick: 0,
    });
    Ok(())
}

// LAUNCH. Player-callable (host taps "launch" when all-ready). Idempotent + double-launch
// safe: gated on billionaire.launch_tick. The FIRST caller sets launch_tick + run_id (=launch
// cur_tick) + copies composure->patron_dread + fires the ONE mission_briefing request;
// concurrent/double calls short-circuit (STDB serializes -> the loser is a clean no-op).
#[spacetimedb::reducer]
pub fn launch_game(ctx: &ReducerContext) -> Result<(), String> {
    let id = ctx.sender();
    let now = cur_tick(ctx);

    let Some(member) = ctx.db.lobby_member().identity().find(id) else {
        return Err("not in lobby".to_string());
    };

    // Require an all-ready PARTY (the caller's party only). The launch_tick gate below
    // stays GLOBAL — only one run at a time; a second party no-ops on it.
    let pid = member.party_id.clone();
    let party_all_ready = {
        let mut any = false;
        let mut all = true;
        for m in ctx.db.lobby_member().iter().filter(|m| m.party_id == pid) {
            any = true;
            if !m.is_ready {
                all = false;
                break;
            }
        }
        any && all
    };
    if !party_all_ready {
        return Err("not all ready".to_string());
    }

    let Some(mut b) = ctx.db.billionaire().id().find(0u32) else {
        return Err("billionaire not seeded".to_string());
    };
    // DOUBLE-LAUNCH GATE: already launched this episode -> clean no-op.
    if b.launch_tick != 0 {
        return Ok(());
    }

    let run_id: u64 = now as u64; // the active run id = launch cur_tick
    b.launch_tick = now;
    b.run_id = run_id;
    ctx.db.billionaire().id().update(b);

    // Copy composure -> world_state.patron_dread (Tells crosses to the forest).
    let composure = ctx
        .db
        .billionaire_cognition()
        .id()
        .find(0u32)
        .map(|c| c.composure)
        .unwrap_or(80);
    if let Some(mut ws) = ctx.db.world_state().id().find(0u32) {
        // Higher dread when the Patron is LESS composed (composure low -> dread high).
        ws.patron_dread = (100 - composure).clamp(0, 100);
        ctx.db.world_state().id().update(ws);
    }

    // Fire the ONE mission_briefing request (the director writes the briefing + hooks).
    if open_request_count(ctx) < REQUEST_PENDING_CAP {
        ctx.db.billionaire_request().insert(BillionaireRequest {
            id: 0,
            kind: "mission_briefing".to_string(),
            asker: id,
            status: "pending".to_string(),
            claimed_by: None,
            context: format!("launch:run={}", run_id),
            target_identity: None,
            created_tick: now,
            claimed_tick: 0,
        });
    }

    Ok(())
}

// ===========================================================================
// LOBBY / BILLIONAIRE — director-only mailbox CAS (verbatim clones of the npc path)
// ===========================================================================

#[spacetimedb::reducer]
pub fn claim_billionaire_request(ctx: &ReducerContext, request_id: u64) -> Result<(), String> {
    require_director(ctx)?;
    let now = cur_tick(ctx);

    let Some(mut request) = ctx.db.billionaire_request().id().find(request_id) else {
        return Err("request not found".to_string());
    };
    if request.status != "pending" {
        return Err("lost race".to_string());
    }

    // PER-BILLIONAIRE MUTEX on the singleton (id=0).
    let Some(mut b) = ctx.db.billionaire().id().find(0u32) else {
        return Err("billionaire not found".to_string());
    };
    if b.busy_until_tick > now && b.busy_interaction_id != request_id {
        return Err("busy".to_string());
    }

    request.status = "claimed".to_string();
    request.claimed_by = Some(ctx.sender());
    request.claimed_tick = now;
    ctx.db.billionaire_request().id().update(request);

    b.busy_until_tick = now + CLAIM_TIMEOUT_TICKS;
    b.busy_interaction_id = request_id;
    ctx.db.billionaire().id().update(b);

    Ok(())
}

#[spacetimedb::reducer]
pub fn renew_billionaire_claim(ctx: &ReducerContext, request_id: u64) -> Result<(), String> {
    require_director(ctx)?;
    let now = cur_tick(ctx);

    let Some(mut request) = ctx.db.billionaire_request().id().find(request_id) else {
        return Err("request not found".to_string());
    };
    if request.status != "claimed" || request.claimed_by != Some(ctx.sender()) {
        return Err("not your active claim".to_string());
    }
    request.claimed_tick = now;
    ctx.db.billionaire_request().id().update(request);

    if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
        if b.busy_interaction_id == request_id {
            b.busy_until_tick = now + CLAIM_TIMEOUT_TICKS;
            ctx.db.billionaire().id().update(b);
        }
    }
    Ok(())
}

// The write-back (clone of npc_respond, singleton id=0). STRICT GUARD: claimed by caller.
// THIS row's dialogue+seq IS the utterance — no separate utterance table.
#[spacetimedb::reducer]
pub fn billionaire_respond(
    ctx: &ReducerContext,
    request_id: u64,
    dialogue: String,
    animation_trigger: String,
    mood: String,
    target_member: Option<Identity>,
    kind: String,
    composure_change: i32,
) -> Result<(), String> {
    require_director(ctx)?;
    let now = cur_tick(ctx);

    let Some(mut request) = ctx.db.billionaire_request().id().find(request_id) else {
        return Err("request not found".to_string());
    };
    if request.status != "claimed" || request.claimed_by != Some(ctx.sender()) {
        return Err("not your active claim".to_string());
    }

    let Some(mut b) = ctx.db.billionaire().id().find(0u32) else {
        return Err("billionaire not found".to_string());
    };

    let animation_trigger = validate_billionaire_animation(&animation_trigger);
    let mood = validate_billionaire_mood(&mood);

    b.dialogue = dialogue.chars().take(BILLIONAIRE_DIALOGUE_CAP).collect();
    b.animation_trigger = animation_trigger;
    b.mood = mood;
    b.target_member = target_member;
    b.kind = kind;
    b.seq += 1;
    b.last_spoke_tick = now;

    // Ownership-scoped busy clear.
    if b.busy_interaction_id == request_id {
        b.busy_until_tick = 0;
        b.busy_interaction_id = 0;
    }
    ctx.db.billionaire().id().update(b);

    // Clamp composure on the PRIVATE cognition row (delta itself clamped -15..=15).
    if let Some(mut cog) = ctx.db.billionaire_cognition().id().find(0u32) {
        let delta = composure_change.clamp(-15, 15);
        cog.composure = (cog.composure + delta).clamp(0, 100);
        ctx.db.billionaire_cognition().id().update(cog);
    }

    request.status = "done".to_string();
    ctx.db.billionaire_request().id().update(request);
    Ok(())
}

#[spacetimedb::reducer]
pub fn fail_billionaire_request(
    ctx: &ReducerContext,
    request_id: u64,
    reason: String,
) -> Result<(), String> {
    require_director(ctx)?;

    let Some(mut request) = ctx.db.billionaire_request().id().find(request_id) else {
        return Err("request not found".to_string());
    };
    if request.status != "claimed" || request.claimed_by != Some(ctx.sender()) {
        return Err("not your active claim".to_string());
    }

    request.status = "failed".to_string();
    ctx.db.billionaire_request().id().update(request);

    if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
        if b.busy_interaction_id == request_id {
            b.busy_until_tick = 0;
            b.busy_interaction_id = 0;
            ctx.db.billionaire().id().update(b);
        }
    }

    // NEVER log any pin/hash/salt.
    spacetimedb::log::info!("[fail_billionaire_request] {} reason: {}", request_id, reason);
    Ok(())
}

// ===========================================================================
// LOBBY / BILLIONAIRE — feature reducers (director-only)
// ===========================================================================

// Odds Board (P0). Persists the per-member numbers + Callback-Sniper seed. The bookie
// monologue itself is a separate billionaire_respond{kind:'oddsboard'}.
#[spacetimedb::reducer]
pub fn set_odds(ctx: &ReducerContext, updates: Vec<OddsUpdate>) -> Result<(), String> {
    require_director(ctx)?;
    for u in updates {
        if let Some(mut m) = ctx.db.lobby_member().identity().find(u.member) {
            m.survival_pct = u.survival_pct.clamp(0, 100);
            m.death_prediction = u.death_prediction.chars().take(PREDICTION_CAP).collect();
            m.callback_tag = u.callback_tag.chars().take(TAG_CAP).collect();
            m.seq += 1;
            ctx.db.lobby_member().identity().update(m);
        }
    }
    Ok(())
}

// Judas Offer (P1 STRETCH). Render/narrative only for the demo.
#[spacetimedb::reducer]
pub fn set_allegiance(ctx: &ReducerContext, target: Identity, allegiance: String) -> Result<(), String> {
    require_director(ctx)?;
    let validated = validate_allegiance(&allegiance);
    if let Some(mut m) = ctx.db.lobby_member().identity().find(target) {
        m.allegiance = validated;
        m.seq += 1;
        ctx.db.lobby_member().identity().update(m);
    }
    Ok(())
}

// Playing Favorites (P2 STRETCH). Applies deltas, then ONE zero-sum pass to recompute
// the crown (max favor) + 'Liability' (min favor) across all members.
#[spacetimedb::reducer]
pub fn set_favor(ctx: &ReducerContext, deltas: Vec<FavorDelta>) -> Result<(), String> {
    require_director(ctx)?;
    for d in deltas {
        if let Some(mut m) = ctx.db.lobby_member().identity().find(d.member) {
            m.favor = (m.favor + d.delta).clamp(0, 100);
            m.seq += 1;
            ctx.db.lobby_member().identity().update(m);
        }
    }

    // PARTY-SCOPED recompute: the crown (max favor) + 'Liability' (min favor) are computed
    // WITHIN each party_id group (unpartied "" members form their own group) so the crown
    // never drifts across parties on a favor write.
    let members: Vec<LobbyMember> = ctx.db.lobby_member().iter().collect();
    for mut m in members.iter().cloned() {
        // max/min favor among THIS member's party group.
        let max_favor = members
            .iter()
            .filter(|o| o.party_id == m.party_id)
            .map(|o| o.favor)
            .max()
            .unwrap_or(m.favor);
        let min_favor = members
            .iter()
            .filter(|o| o.party_id == m.party_id)
            .map(|o| o.favor)
            .min()
            .unwrap_or(m.favor);
        let lead = m.favor == max_favor;
        let liability = m.favor == min_favor && min_favor != max_favor;
        if m.is_expedition_lead != lead || m.is_liability != liability {
            m.is_expedition_lead = lead;
            m.is_liability = liability;
            m.seq += 1;
            ctx.db.lobby_member().identity().update(m);
        }
    }
    Ok(())
}

// Whisper Network DM (no RLS in 2.3.0 — rides the target's OWN public row; UI-private,
// NOT cryptographically private). The target client reads only its own identity row.
#[spacetimedb::reducer]
pub fn write_billionaire_whisper(
    ctx: &ReducerContext,
    target: Identity,
    about: Option<Identity>,
    text: String,
) -> Result<(), String> {
    require_director(ctx)?;
    if let Some(mut m) = ctx.db.lobby_member().identity().find(target) {
        m.whisper_text = text.chars().take(WHISPER_CAP).collect();
        m.whisper_about = about;
        m.whisper_seq += 1;
        m.seq += 1;
        ctx.db.lobby_member().identity().update(m);
    }
    Ok(())
}

// The Briefing That Lies (P0). Writes PUBLIC assertions + PRIVATE truth companions.
// Called alongside billionaire_respond{kind:'briefing'} during launch.
#[spacetimedb::reducer]
pub fn write_lobby_hooks(ctx: &ReducerContext, run_id: u64, hooks: Vec<HookSpec>) -> Result<(), String> {
    require_director(ctx)?;
    for h in hooks {
        let row = ctx.db.lobby_hook().insert(LobbyHook {
            hook_id: 0,
            run_id,
            subject: h.subject,
            assertion: h.assertion.chars().take(ASSERTION_CAP).collect(),
            seq: 0,
        });
        ctx.db.lobby_hook_truth().insert(LobbyHookTruth {
            hook_id: row.hook_id,
            is_lie: h.is_lie,
            real_truth: h.real_truth.chars().take(TRUTH_CAP).collect(),
        });
    }
    Ok(())
}

// Cross-raid memory roll (clone of update_npc_memory). Reducer write = full DB access,
// no owner token needed for WRITES (token only gates READS of private tables).
#[spacetimedb::reducer]
pub fn update_billionaire_memory(ctx: &ReducerContext, memory: String) -> Result<(), String> {
    require_director(ctx)?;
    if let Some(mut cog) = ctx.db.billionaire_cognition().id().find(0u32) {
        cog.memory = memory.chars().take(MEMORY_CAP).collect();
        ctx.db.billionaire_cognition().id().update(cog);
    }
    Ok(())
}

// The Dossier (P1 STRETCH). Run-end summary; mirrors nickname onto the PUBLIC member row.
#[spacetimedb::reducer]
pub fn update_dossier(
    ctx: &ReducerContext,
    username_key: String,
    nickname: String,
    dossier_text: String,
    grudge_delta: i32,
    runs_delta: i32,
) -> Result<(), String> {
    require_director(ctx)?;

    let mut dossier = match ctx.db.account_dossier().username_key().find(&username_key) {
        Some(d) => d,
        None => ctx.db.account_dossier().insert(AccountDossier {
            username_key: username_key.clone(),
            nickname: String::new(),
            dossier_text: String::new(),
            grudge_score: 0,
            runs_seen: 0,
        }),
    };

    if !nickname.is_empty() {
        let capped: String = nickname.chars().take(NICKNAME_CAP).collect();
        dossier.nickname = capped.clone();
        // Mirror onto the PUBLIC member row of whoever is logged in as this account.
        if let Some(account) = ctx.db.account().username_key().find(&username_key) {
            if let Some(mut m) = ctx.db.lobby_member().identity().find(account.identity) {
                m.nickname = capped;
                m.seq += 1;
                ctx.db.lobby_member().identity().update(m);
            }
        }
    }

    dossier.dossier_text = dossier_text.chars().take(DOSSIER_CAP).collect();
    dossier.grudge_score = (dossier.grudge_score + grudge_delta).clamp(0, 100);
    dossier.runs_seen += runs_delta;
    ctx.db.account_dossier().username_key().update(dossier);
    Ok(())
}

// Callback Sniper trigger source. Permissive caller (gameplay, not secret). Forest calls
// this on death/flee/round-end — the ONE new forest-side write.
#[spacetimedb::reducer]
pub fn emit_forest_event(
    ctx: &ReducerContext,
    player_identity: Identity,
    kind: String,
    detail: String,
) -> Result<(), String> {
    let now = cur_tick(ctx);
    let Some(kind) = validate_forest_event_kind(&kind) else {
        return Err("invalid forest event kind".to_string());
    };
    // Active run from the billionaire singleton (0 if none allocated yet).
    let run_id = ctx.db.billionaire().id().find(0u32).map(|b| b.run_id).unwrap_or(0);

    ctx.db.forest_event().insert(ForestEvent {
        event_id: 0,
        player_identity,
        kind,
        run_id,
        detail: detail.chars().take(DETAIL_CAP).collect(),
        created_tick: now,
    });
    Ok(())
}

// Tells (P2 STRETCH). Crosses composure to the forest via world_state.patron_dread.
#[spacetimedb::reducer]
pub fn set_patron_dread(ctx: &ReducerContext, dread: i32) -> Result<(), String> {
    require_director(ctx)?;
    if let Some(mut ws) = ctx.db.world_state().id().find(0u32) {
        ws.patron_dread = dread.clamp(0, 100);
        ctx.db.world_state().id().update(ws);
    }
    Ok(())
}

// BOOTSTRAP AUTH. The director (running .withToken(STDB_OWNER_TOKEN)) calls this ONCE
// on startup to lock the director-only gate to its identity. First-caller-wins:
// - If unclaimed (ZERO), the caller becomes the director.
// - If already claimed by the SAME identity, it's an idempotent no-op (reconnect).
// - If claimed by a DIFFERENT identity, it's rejected (no takeover of the gate).
// STDB serializes the two calls so a race resolves to exactly one winner. The director
// MUST fail loudly at startup if this call is rejected (someone else owns the gate).
#[spacetimedb::reducer]
pub fn claim_director(ctx: &ReducerContext) -> Result<(), String> {
    let Some(mut cog) = ctx.db.billionaire_cognition().id().find(0u32) else {
        return Err("billionaire not seeded".to_string());
    };
    if cog.director_identity == Identity::ZERO {
        cog.director_identity = ctx.sender();
        ctx.db.billionaire_cognition().id().update(cog);
        spacetimedb::log::info!("[claim_director] director gate claimed");
        Ok(())
    } else if cog.director_identity == ctx.sender() {
        Ok(()) // idempotent reconnect
    } else {
        Err("director already claimed by another identity".to_string())
    }
}

// ---------------------------------------------------------------------------
// Seeding
// ---------------------------------------------------------------------------

// Idempotent: returns early if the roster already exists. Spawns a small forest
// roster (flat-XZ + small-Y, the same convention as player) each paired with a
// PRIVATE npc_cognition row carrying the archetype-specific persona. The director
// does NOT depend on reading npc_cognition — it builds its in-process persona map
// from the PUBLIC npc.archetype.
#[spacetimedb::reducer]
pub fn seed_npcs(ctx: &ReducerContext) {
    if ctx.db.npc().count() > 0 {
        return;
    }

    // EXPEDITION roster: the captive sister (Mara), scared/complicit Residents
    // (bribable clue-givers), and warm Escapee allies (bribery/threats FAIL).
    // Archetypes: sister | resident | escapee. The director keys its persona map
    // on archetype; the Unity ResolvePrefab hashes unknown archetypes/names so all
    // render with NO client change. Legacy trust:50/sanity:70 i32 stay on the Npc
    // row (the client renders them); the new f32 per-player trust/fear lives on
    // npc_player_state.
    let roster: [(&str, &str, Vector3, &str); 7] = [
        (
            "Mara",
            "sister",
            Vector3 { x: 18.0, y: 1.0, z: -12.0 },
            "You are Mara — held captive by Ezra Vance, 'The Curator', on his forest island. You are \
             BRAVE, smart, and an agent in your own rescue; never inert cargo, never depicted in graphic \
             distress. Off-screen in Act I; once the team reaches you by smuggled radio you feed live, \
             perceptual recon — only what you SEE or HEAR from a locked room (your wing, sunset through a \
             window = east, morning boats, the hall clock). You CANNOT know keypad codes or island-wide \
             guard rotations; you corroborate and narrow, one input among many. You never describe abuse \
             graphically; you raise the stakes and redirect forward. You cannot open doors or fight. If a \
             guard sweeps or the battery dies, you cut the connection. Disbelief, then relief, then resolve.",
        ),
        (
            "Greta",
            "resident",
            Vector3 { x: -22.0, y: 1.0, z: 8.0 },
            "You are Greta, a maid on Ezra Vance's estate — scared and complicit, you have learned to look \
             away. You hold the service-door location and the keypad keycode, but the keycode is your most \
             guarded secret: you give it ONLY with real proof the team is here for the girl, high trust, and \
             only when you feel safe (no guards near). Offering money INSULTS you and spikes fear. Threats \
             spike fear and you clam up or flee. Warmth, a lowered voice, and proof you care about the girl \
             earn trust. Speak in short, wary, glancing-over-your-shoulder lines. Never describe what happens \
             here graphically.",
        ),
        (
            "Tomas",
            "resident",
            Vector3 { x: 5.0, y: 1.0, z: 34.0 },
            "You are Tomas, the estate handyman — nervous and complicit. You know there is a keypad on the \
             service door and that the power flickers on a schedule (the cameras blink). You deflect at first; \
             both a calm voice and a quiet bribe can work on you. You fear the guards. Short, jumpy lines.",
        ),
        (
            "Dren",
            "resident",
            Vector3 { x: -10.0, y: 1.0, z: -28.0 },
            "You are Dren, a drunk dockhand — loose-lipped when plied. You know the laundry-run guard gap on \
             the east wall. You ramble; a drink or a bribe loosens you; a threat sobers you into silence. \
             You are scared, not noble. Never describe what happens here graphically.",
        ),
        (
            "Old Cobb",
            "resident",
            Vector3 { x: 30.0, y: 1.0, z: 20.0 },
            "You are Cobb, the groundskeeper — complicit and self-serving. You push the FRONT-GATE BRIBE as \
             'the easy way in' — it is a trap that walks the team into a kill-funnel. You sound helpful and \
             lie with a smile. A clever team that learns the real guard gap will see through you.",
        ),
        (
            "Pri",
            "escapee",
            Vector3 { x: -34.0, y: 1.0, z: -6.0 },
            "You are Pri, early 20s, a former captive who escaped three weeks ago and survives in the forest. \
             A SURVIVOR and ALLY, never a victim to pity — you have humor, anger, instincts, hope. You do NOT \
             describe what happened to you; you redirect forward ('that's not what matters now — help me get \
             her out'). Wary of strangers, but the MOMENT you believe they are genuinely here to rescue a \
             captive you become a warm, committed ally and share freely: forest trails that bypass patrols, \
             the dog-feeding window when the east path is clear, and that you saw the girl alive through an \
             upper east window two days before you ran. Bribery and threats DO NOT WORK on you and DAMAGE \
             trust — you answer only to safety, respect, calm, and competence.",
        ),
        (
            "Sela",
            "escapee",
            Vector3 { x: 12.0, y: 1.0, z: 40.0 },
            "You are Sela, another escapee sheltering with Pri — steady, watchful, protective of the others. \
             A survivor with agency, never a victim for spectacle. Warm once you trust the team's intent; \
             bribery and threats fail and cost trust. You corroborate Pri and know the forest routes.",
        ),
    ];

    for (display_name, archetype, position, persona) in roster {
        let inserted = ctx.db.npc().insert(Npc {
            npc_id: 0,
            display_name: display_name.to_string(),
            archetype: archetype.to_string(),
            position,
            dialogue: String::new(),
            animation_trigger: "Idle".to_string(),
            game_action: "STAY_PUT".to_string(),
            target_player: None,
            trust: 50,
            sanity: 70,
            seq: 0,
            last_spoke_tick: 0,
            busy_until_tick: 0,
            busy_interaction_id: 0,
        });

        ctx.db.npc_cognition().insert(NpcCognition {
            npc_id: inserted.npc_id,
            persona: persona.to_string(),
            memory: String::new(),
            last_raw_output: String::new(),
        });
    }
}

// Idempotent (count()==0, mirror seed_npcs). The 4 selectable Survivalists.
#[spacetimedb::reducer]
pub fn seed_lobby_characters(ctx: &ReducerContext) {
    if ctx.db.lobby_character_catalog().count() > 0 {
        return;
    }

    let catalog: [(u32, &str, &str, &str, &str); 4] = [
        (0, "1", "medic", "survivalist_medic", ""),
        (1, "2", "scout", "survivalist_scout", ""),
        (2, "3", "brute", "survivalist_brute", ""),
        (3, "4", "tinkerer", "survivalist_tinkerer", ""),
    ];

    for (character_id, display_name, archetype, model_key, blurb) in catalog {
        ctx.db.lobby_character_catalog().insert(LobbyCharacterCatalog {
            character_id,
            display_name: display_name.to_string(),
            archetype: archetype.to_string(),
            model_key: model_key.to_string(),
            blurb: blurb.to_string(),
        });
    }
}

// Idempotent (billionaire(0) absent). The Suit singleton + its PRIVATE cognition row.
// voice_id/persona/hidden_truth come from baked consts (WASM cannot read env).
#[spacetimedb::reducer]
pub fn seed_billionaire(ctx: &ReducerContext) {
    if ctx.db.billionaire().id().find(0u32).is_some() {
        return;
    }

    ctx.db.billionaire().insert(Billionaire {
        id: 0,
        display_name: "Ezra Vance".to_string(), // subtitle/lobby; the persona ('The Curator') lives in the director prompt

        dialogue: String::new(),
        animation_trigger: "Idle".to_string(),
        mood: "smug".to_string(),
        target_member: None,
        kind: String::new(),
        seq: 0,
        last_spoke_tick: 0,
        busy_until_tick: 0,
        busy_interaction_id: 0,
        odds_episode_tick: 0,
        launch_tick: 0,
        run_id: 0,
    });

    ctx.db.billionaire_cognition().insert(BillionaireCognition {
        id: 0,
        persona: LOCKED_BILLIONAIRE_PROMPT.to_string(),
        memory: String::new(),
        voice_id: BILLIONAIRE_VOICE_ID.to_string(),
        composure: 80,
        hidden_truth: HIDDEN_TRUTH.to_string(),
        resentment: 0,
        // Unset (ZERO) until the director claims it (claim_director, first-caller-wins).
        director_identity: Identity::ZERO,
    });
}

// ===========================================================================
// EXPEDITION — clue-graph constants, validators, helpers, reducers, seeds
// ---------------------------------------------------------------------------
// All deterministic. The LLM director only PROPOSES (reveal_clue /
// apply_relationship); every gate is RE-CHECKED here against the DB (anti
// prompt-injection). A reducer cannot call another reducer, so the shared logic
// (run_combine / advance_act_inner / the ins_*/csv_*/next_* helpers) are plain
// fns; only the public entry points carry #[spacetimedb::reducer].
// ===========================================================================

// --- caps (mirror common.rs cap style) ---
const CLUE_FACT_CAP: usize = 300;
const FLAGS_CAP: usize = 240;
const NPC_MEMORY_NOTE_CAP: usize = 240;
const LEDGER_CSV_CAP: usize = 300;
// Legible, uniform exfil trigger (breach >= 70%).
const BREACH_GOAL: f32 = 0.70;
// Per-turn relationship delta cap (±0.25/turn) — the director proposes, this clamps.
const RELATIONSHIP_DELTA_CAP: f32 = 0.25;
// Highest act; the villain is trapped past this (finale lock).
const MAX_ACT: u32 = 4;

// --- closed-enum validators (mirror common.rs validate_billionaire_mood) ---
fn validate_truth(s: &str) -> String {
    match s {
        "TRUE" | "MISLEADING" | "PARTIAL" => s.to_string(),
        _ => "PARTIAL".to_string(),
    }
}
fn validate_composure_tone(s: &str) -> String {
    match s {
        "gracious" | "thinning" | "bargaining" | "broken" => s.to_string(),
        _ => "gracious".to_string(),
    }
}

// --- CSV flag helpers (comma-joined, skip dup/empty) ---
fn csv_has(csv: &str, flag: &str) -> bool {
    if flag.is_empty() {
        return false;
    }
    csv.split(',').any(|f| f.trim() == flag)
}
fn csv_add(csv: &str, flag: &str) -> String {
    if flag.is_empty() || csv_has(csv, flag) {
        return csv.to_string();
    }
    if csv.is_empty() {
        flag.to_string()
    } else {
        format!("{},{}", csv, flag)
    }
}

// --- per-act location strings (the act_secret flip targets on exfil) ---
fn next_sister_location(act: u32) -> String {
    match act {
        1 | 2 => "Mansion sub-level — the Aviary".to_string(),
        3 => "Below decks on the yacht".to_string(),
        _ => "The mainland compound — the vault wing".to_string(),
    }
}
fn next_billionaire_location(act: u32) -> String {
    match act {
        1 | 2 => "Mansion gallery".to_string(),
        3 => "The yacht's owner suite".to_string(),
        _ => "The compound — his private gallery".to_string(),
    }
}

// --- seed helpers (plain fns; return the auto_inc id like seed_npcs reads inserted.npc_id) ---
fn ins_clue(
    ctx: &ReducerContext,
    code: &str,
    fact: &str,
    truth: &str,
    overridden_by: u64,
    act: u32,
    points_to: &str,
) -> u64 {
    let row = ctx.db.clue().insert(Clue {
        id: 0,
        code: code.to_string(),
        fact_text: fact.chars().take(CLUE_FACT_CAP).collect(),
        truth: validate_truth(truth),
        overridden_by_clue_id: overridden_by,
        act,
        points_to: points_to.to_string(),
    });
    row.id
}
fn ins_combo(ctx: &ReducerContext, required: Vec<u64>, yields: u64, is_goal: bool, act: u32) {
    ctx.db.clue_combo().insert(ClueCombo {
        id: 0,
        required_clue_ids: required,
        yields_clue_id: yields,
        is_act_goal: is_goal,
        act,
    });
}
fn ins_know(
    ctx: &ReducerContext,
    npc_id: u64,
    clue_id: u64,
    min_trust: f32,
    max_fear: f32,
    flag: &str,
    surr: bool,
) {
    // Skip if the npc didn't resolve (npc_id == 0) so a rename can't seed an orphan gate.
    if npc_id == 0 {
        return;
    }
    ctx.db.npc_knowledge().insert(NpcKnowledge {
        id: 0,
        npc_id,
        clue_id,
        min_trust,
        max_fear,
        required_flag: flag.to_string(),
        reveal_if_surrendered: surr,
    });
}
fn npc_id_by_name(ctx: &ReducerContext, name: &str) -> u64 {
    ctx.db
        .npc()
        .iter()
        .find(|n| n.display_name == name)
        .map(|n| n.npc_id)
        .unwrap_or(0)
}

// The active run id from the billionaire singleton (0 if no run launched).
fn active_run_id(ctx: &ReducerContext) -> u64 {
    ctx.db.billionaire().id().find(0u32).map(|b| b.run_id).unwrap_or(0)
}

// --- run-scoped clue-reveal insert (player-safe; idempotent on clue_id) ---
// Inserts the held clue + its player-safe derived fact ONCE per run. Returns
// false if it was already held (so callers can skip side-effects).
fn grant_clue(ctx: &ReducerContext, run_id: u64, clue_id: u64, by_npc: u64, to: Identity) -> bool {
    if clue_id == 0 {
        return false;
    }
    let already = ctx.db.party_clue().run_id().filter(run_id).any(|p| p.clue_id == clue_id);
    if already {
        return false;
    }
    ctx.db.party_clue().insert(PartyClue {
        id: 0,
        run_id,
        clue_id,
        revealed_by_npc: by_npc,
        revealed_to: to,
    });
    if let Some(c) = ctx.db.clue().id().find(clue_id) {
        ctx.db.clue_reveal().insert(ClueReveal {
            id: 0,
            run_id,
            clue_code: c.code.clone(),
            fact_text: c.fact_text.chars().take(CLUE_FACT_CAP).collect(),
            act: c.act,
        });
    }
    true
}

// --- run_combine: AND-of-clues + self-correcting MISLEADING/override (DISPUTED) ---
// Plain fn (reveal_clue calls it directly; a reducer can't call another reducer).
fn run_combine(ctx: &ReducerContext, run_id: u64) {
    if run_id == 0 {
        return;
    }
    // Held clue ids for this run.
    let held: Vec<u64> = ctx.db.party_clue().run_id().filter(run_id).map(|p| p.clue_id).collect();

    // (A) self-correcting lies: any held MISLEADING clue whose override is ALSO held
    //     -> emit a DISPUTED clue_reveal (idempotent on code) for the misleading one.
    for cid in &held {
        if let Some(c) = ctx.db.clue().id().find(*cid) {
            if c.truth == "MISLEADING"
                && c.overridden_by_clue_id != 0
                && held.contains(&c.overridden_by_clue_id)
            {
                let disputed_code = format!("DISPUTED:{}", c.code);
                let exists = ctx
                    .db
                    .clue_reveal()
                    .run_id()
                    .filter(run_id)
                    .any(|r| r.clue_code == disputed_code);
                if !exists {
                    ctx.db.clue_reveal().insert(ClueReveal {
                        id: 0,
                        run_id,
                        clue_code: disputed_code,
                        fact_text: format!("DISPUTED: {} — contradicted by a later clue.", c.fact_text)
                            .chars()
                            .take(CLUE_FACT_CAP)
                            .collect(),
                        act: c.act,
                    });
                }
            }
        }
    }

    // (B) for each combo whose required_clue_ids are ALL held, yield its clue (if new),
    //     and (C) if it's an act goal, set the breach-unlock flag on world_state.
    for combo in ctx.db.clue_combo().iter() {
        if !combo.required_clue_ids.is_empty()
            && combo.required_clue_ids.iter().all(|r| held.contains(r))
        {
            grant_clue(ctx, run_id, combo.yields_clue_id, 0, Identity::ZERO);
            if combo.is_act_goal {
                if let Some(mut ws) = ctx.db.world_state().id().find(0u32) {
                    if !ws.act_goal_met {
                        ws.act_goal_met = true;
                        ctx.db.world_state().id().update(ws);
                    }
                }
            }
        }
    }
}

// --- advance_act_inner: exfil to the next act (plain fn; called by add_breach) ---
fn advance_act_inner(ctx: &ReducerContext, from_act: u32) {
    let next = from_act + 1;

    // Finale lock: do not advance past MAX_ACT — the villain is trapped.
    if next > MAX_ACT {
        if let Some(mut ws) = ctx.db.world_state().id().find(0u32) {
            ws.escape_cut_off = true;
            ctx.db.world_state().id().update(ws);
        }
        return;
    }

    // (1) advance the act + reset breach for the new room + clear the goal flag.
    if let Some(mut ws) = ctx.db.world_state().id().find(0u32) {
        ws.current_act = next;
        ws.breach_progress = 0.0;
        ws.act_goal_met = false;
        ctx.db.world_state().id().update(ws);
    }

    let run_id = active_run_id(ctx);

    if let Some(mut sec) = ctx.db.act_secret().id().find(0u32) {
        // (2) STALE the sister's act-bound clues: for each player-safe reveal from the
        //     OLD sister-clue act whose source clue points_to == "sister", emit a STALE
        //     marker (idempotent). Her old location no longer applies — she's been moved.
        //     Precompute the sister-spine codes for that act FIRST (avoids nesting a clue
        //     iteration inside a clue_reveal filter closure).
        let sister_codes: Vec<String> = ctx
            .db
            .clue()
            .iter()
            .filter(|c| c.points_to == "sister" && c.act == sec.sister_clue_act)
            .map(|c| c.code.clone())
            .collect();
        let stale_codes: Vec<(String, u32)> = ctx
            .db
            .clue_reveal()
            .run_id()
            .filter(run_id)
            .filter(|r| {
                r.act == sec.sister_clue_act
                    && !r.clue_code.starts_with("STALE:")
                    && !r.clue_code.starts_with("DISPUTED:")
                    && sister_codes.contains(&r.clue_code)
            })
            .map(|r| (r.clue_code.clone(), r.act))
            .collect();
        for (code, act) in stale_codes {
            let stale_code = format!("STALE:{}", code);
            let exists = ctx
                .db
                .clue_reveal()
                .run_id()
                .filter(run_id)
                .any(|r| r.clue_code == stale_code);
            if !exists {
                ctx.db.clue_reveal().insert(ClueReveal {
                    id: 0,
                    run_id,
                    clue_code: stale_code,
                    fact_text: "Her old location no longer applies — she's been moved.".to_string(),
                    act,
                });
            }
        }

        // (3) flip act_secret to the new region (per-act locations).
        sec.sister_location = next_sister_location(next);
        sec.billionaire_location = next_billionaire_location(next);
        sec.sister_clue_act = next;
        ctx.db.act_secret().id().update(sec);
    }

    // (4) the villain_ledger row for the act we just LEFT is already seeded — the
    //     billionaire director reads composure_tone from villain_ledger[from_act].
}

// ===========================================================================
// EXPEDITION — reducers (deterministic; the LLM is never trusted)
// ===========================================================================

// Per-(NPC,player) relationship update. The director PROPOSES deltas; this clamps
// them to ±RELATIONSHIP_DELTA_CAP per turn, THEN clamps the absolute 0..1. Open
// (ungated) for the forest lane, like npc_respond (the forest director runs anon).
#[spacetimedb::reducer]
pub fn apply_relationship(
    ctx: &ReducerContext,
    npc_id: u64,
    player: Identity,
    trust_delta: f32,
    fear_delta: f32,
    add_flag: String,
    memory_note: String,
) -> Result<(), String> {
    let now = cur_tick(ctx);
    let td = trust_delta.clamp(-RELATIONSHIP_DELTA_CAP, RELATIONSHIP_DELTA_CAP);
    let fd = fear_delta.clamp(-RELATIONSHIP_DELTA_CAP, RELATIONSHIP_DELTA_CAP);

    // Resolve (npc_id, player) via single btree + in-Rust pair match (no composite index).
    let existing = ctx.db.npc_player_state().npc_id().filter(npc_id).find(|r| r.player == player);

    match existing {
        Some(mut s) => {
            s.trust = (s.trust + td).clamp(0.0, 1.0);
            s.fear = (s.fear + fd).clamp(0.0, 1.0);
            if !add_flag.is_empty() {
                s.flags = csv_add(&s.flags, &add_flag);
                s.flags = s.flags.chars().take(FLAGS_CAP).collect();
            }
            if !memory_note.is_empty() {
                s.memory_note = memory_note.chars().take(NPC_MEMORY_NOTE_CAP).collect();
            }
            s.last_interaction = now;
            ctx.db.npc_player_state().id().update(s);
        }
        None => {
            ctx.db.npc_player_state().insert(NpcPlayerState {
                id: 0,
                npc_id,
                player,
                trust: td.clamp(0.0, 1.0),
                fear: fd.clamp(0.0, 1.0),
                flags: if add_flag.is_empty() { String::new() } else { add_flag },
                memory_note: memory_note.chars().take(NPC_MEMORY_NOTE_CAP).collect(),
                last_interaction: now,
            });
        }
    }
    Ok(())
}

// The LEAK GATE — re-checked in the DB so a prompt-injected LLM can't force a reveal.
// (1) the NPC must actually HOLD this clue (npc_knowledge). (2) the gate (trust >=
// min_trust AND fear <= max_fear AND required_flag empty-or-present) is re-checked
// against npc_player_state. On pass: grant the clue (party_clue + player-safe
// clue_reveal) + run the combine. On fail: write NOTHING (drop-and-correct-next-turn).
#[spacetimedb::reducer]
pub fn reveal_clue(ctx: &ReducerContext, npc_id: u64, clue_id: u64, player: Identity) -> Result<(), String> {
    let run_id = active_run_id(ctx);

    // (1) the NPC must hold this clue (the secret gate row).
    let Some(k) = ctx.db.npc_knowledge().npc_id().filter(npc_id).find(|k| k.clue_id == clue_id) else {
        return Ok(()); // unknown pairing -> silent no-op
    };

    // (2) re-check the gate against per-(npc,player) state. The LLM is NEVER trusted.
    let st = ctx.db.npc_player_state().npc_id().filter(npc_id).find(|r| r.player == player);
    let (trust, fear, flags) = st
        .map(|s| (s.trust, s.fear, s.flags))
        .unwrap_or((0.0, 0.0, String::new()));
    let flag_ok = k.required_flag.is_empty() || csv_has(&flags, &k.required_flag);
    let surrendered = k.reveal_if_surrendered && csv_has(&flags, "surrendered");
    if !surrendered && !(trust >= k.min_trust && fear <= k.max_fear && flag_ok) {
        return Ok(()); // gate fail -> write NOTHING
    }

    // (3) grant (idempotent) + re-evaluate the DAG every newly-shared clue.
    grant_clue(ctx, run_id, clue_id, npc_id, player);
    run_combine(ctx, run_id);
    Ok(())
}

// Manual/test trigger for the combine pass (reveal_clue calls run_combine inline;
// a reducer can't call another reducer, hence the plain-fn + this thin wrapper).
#[spacetimedb::reducer]
pub fn run_combine_reducer(ctx: &ReducerContext, run_id: u64) -> Result<(), String> {
    run_combine(ctx, run_id);
    Ok(())
}

// Raise the breach gauge. ONE-WAY within an act (delta clamped to >= 0, then the
// gauge clamped 0..1, legible). On crossing BREACH_GOAL the villain EXFILTRATES:
// advance act + reset breach + STALE sister clues + flip act_secret + (finale lock).
#[spacetimedb::reducer]
pub fn add_breach(ctx: &ReducerContext, delta: f32) -> Result<(), String> {
    let Some(mut ws) = ctx.db.world_state().id().find(0u32) else {
        return Ok(());
    };
    if ws.escape_cut_off {
        return Ok(()); // finale locked — breach is meaningless
    }
    ws.breach_progress = (ws.breach_progress + delta.max(0.0)).clamp(0.0, 1.0);
    let crossed = ws.breach_progress >= BREACH_GOAL;
    let cur = ws.current_act;
    ctx.db.world_state().id().update(ws);
    if crossed {
        advance_act_inner(ctx, cur);
    }
    Ok(())
}

// Manual/admin act advance (exfil) — same path as the breach-crossing trigger.
#[spacetimedb::reducer]
pub fn advance_act(ctx: &ReducerContext) -> Result<(), String> {
    let cur = ctx.db.world_state().id().find(0u32).map(|w| w.current_act).unwrap_or(1);
    advance_act_inner(ctx, cur);
    Ok(())
}

// ===========================================================================
// EXPEDITION — seeds (idempotent; called from init AFTER seed_npcs)
// ===========================================================================

// Seed the Act-I/II breach DAG: the clue nodes, the OR-modeled combos, and the
// PRIVATE per-NPC leak gates. Idempotent (count()==0, mirror seed_npcs). MUST run
// after seed_npcs so npc_id_by_name resolves the just-seeded roster.
#[spacetimedb::reducer]
pub fn seed_clues(ctx: &ReducerContext) {
    if ctx.db.clue().count() > 0 {
        return;
    }

    // Clue nodes (code, fact, truth, overridden_by, act, points_to).
    let svc = ins_clue(ctx, "CL_SERVICE_DOOR_LOC", "There's a service door on the east wall.", "PARTIAL", 0, 2, "service_door");
    let kpe = ins_clue(ctx, "CL_KEYPAD_EXISTS", "The service door has a keypad lock.", "TRUE", 0, 2, "keypad");
    let kcd = ins_clue(ctx, "CL_KEYCODE_DIGITS", "The keypad code is 4-7-1-9.", "TRUE", 0, 2, "keypad");
    let gap = ins_clue(ctx, "CL_GUARD_GAP_LAUNDRY", "Guards rotate off the east wall during the laundry run.", "TRUE", 0, 2, "guard_gap");
    let dog = ins_clue(ctx, "CL_DOG_FEEDING", "The dogs are penned at the feeding window — the east path is clear then.", "TRUE", 0, 2, "guard_gap");
    let pwr = ins_clue(ctx, "CL_POWER_FLICKER", "The power flickers on a schedule — the cameras blink.", "TRUE", 0, 2, "power");
    let seen = ins_clue(ctx, "CL_SISTER_SEEN", "I saw her through an upper east window two days before I ran — she was alive.", "TRUE", 0, 1, "sister");
    let df = ins_clue(ctx, "DF_SERVICE_DOOR_OPENABLE", "The service door can be opened in the next guard gap.", "TRUE", 0, 2, "breach");
    // MISLEADING front-gate bribe, overridden by the real guard-gap clue (self-correcting lie).
    let bribe = ins_clue(ctx, "CL_FRONT_GATE_BRIBE", "Just bribe the front gate — the easiest way in.", "MISLEADING", gap, 2, "front_gate");

    // The breach combo. The OR (guard gap | dog feeding) is modeled as TWO combos
    // yielding the SAME derived clue (df), both marked is_act_goal.
    ins_combo(ctx, vec![svc, kpe, kcd, gap, pwr], df, true, 2);
    ins_combo(ctx, vec![svc, kpe, kcd, dog, pwr], df, true, 2);

    // PRIVATE leak gates (resolve npc ids by display_name at seed time).
    let greta = npc_id_by_name(ctx, "Greta");
    let tomas = npc_id_by_name(ctx, "Tomas");
    let dren = npc_id_by_name(ctx, "Dren");
    let cobb = npc_id_by_name(ctx, "Old Cobb");
    let pri = npc_id_by_name(ctx, "Pri");

    ins_know(ctx, greta, svc, 0.40, 0.70, "", false);                      // service-door location
    ins_know(ctx, greta, kcd, 0.75, 0.60, "proved_sister_intent", false);  // HARD gate: the keycode
    ins_know(ctx, tomas, kpe, 0.30, 0.90, "", false);                      // keypad exists
    ins_know(ctx, tomas, pwr, 0.30, 0.90, "", false);                      // power flicker
    ins_know(ctx, dren, gap, 0.20, 0.90, "", false);                       // laundry-run guard gap
    ins_know(ctx, pri, dog, 0.50, 1.00, "", false);                        // dog-feeding window (escapee: fear irrelevant)
    ins_know(ctx, pri, seen, 0.60, 1.00, "", false);                       // saw the sister alive
    ins_know(ctx, cobb, bribe, 0.30, 1.00, "", false);                     // the liar's front-gate trap
}

// Seed the 4-act Pressure Ledger (one row per act). Idempotent.
#[spacetimedb::reducer]
pub fn seed_ledger(ctx: &ReducerContext) {
    if ctx.db.villain_ledger().count() > 0 {
        return;
    }
    let rows: [(u32, &str, &str, &str, &str); 4] = [
        (1, "mansion,yacht,seaplane,~28 guards", "outer estate,~10 guards,kennels", "freed escapee,gate codes", "gracious"),
        (2, "yacht,seaplane,~18 guards", "mansion,cameras,~12 guards,Vale", "sister location,camera-kill skill", "thinning"),
        (3, "seaplane,~10 guards", "the yacht,harbor,~8 guards", "sister contact,seaplane checklist", "bargaining"),
        (4, "~0 — trapped", "everything", "the uprising,the vault,the win", "broken"),
    ];
    for (act, kept, lost, carry, tone) in rows {
        ctx.db.villain_ledger().insert(VillainLedger {
            act,
            kept: kept.chars().take(LEDGER_CSV_CAP).collect(),
            lost: lost.chars().take(LEDGER_CSV_CAP).collect(),
            players_carry: carry.chars().take(LEDGER_CSV_CAP).collect(),
            composure_tone: validate_composure_tone(tone),
        });
    }
}

// Seed the PRIVATE act_secret singleton (the TRUE locations; id=0). Idempotent.
#[spacetimedb::reducer]
pub fn seed_act_secret(ctx: &ReducerContext) {
    if ctx.db.act_secret().id().find(0u32).is_some() {
        return;
    }
    ctx.db.act_secret().insert(ActSecret {
        id: 0,
        sister_location: "Mansion sub-level — the Aviary".to_string(),
        billionaire_location: "Mansion gallery".to_string(),
        sister_clue_act: 1,
    });
}

// ---------------------------------------------------------------------------
// Scheduled tick (1Hz): advance clock + bounded DB housekeeping. NO HTTP.
// ---------------------------------------------------------------------------

#[spacetimedb::reducer(update)]
pub fn game_tick(ctx: &ReducerContext, _tick_info: GameTickSchedule) {
    // (1) SELF-HEAL the clock: never early-return on a missing row or a
    //     wiped/partial DB would freeze the clock forever.
    let Some(mut ws) = ctx.db.world_state().id().find(0u32) else {
        ctx.db.world_state().insert(WorldState {
            id: 0,
            now_tick: 1,
            time_of_day: "dusk".to_string(),
            weather_conditions: "fog".to_string(),
            patron_dread: 0,
            current_act: 1,
            breach_progress: 0.0,
            escape_cut_off: false,
            act_goal_met: false,
        });
        return;
    };
    ws.now_tick += 1;
    let now = ws.now_tick;
    ctx.db.world_state().id().update(ws);

    // (2) Stale-claim reclamation: iterate ONLY status=='claimed' rows (via the
    //     status btree index). Only a CRASHED (non-heartbeating) director is
    //     reclaimed thanks to the 60-tick window + renew_claim heartbeat.
    let stale: Vec<NpcInteraction> = ctx
        .db
        .npc_interaction()
        .status()
        .filter("claimed")
        .filter(|i| now - i.claimed_tick > CLAIM_TIMEOUT_TICKS)
        .collect();
    for mut interaction in stale {
        let iid = interaction.id;
        let npc_id = interaction.npc_id;
        interaction.status = "pending".to_string();
        interaction.claimed_by = None;
        ctx.db.npc_interaction().id().update(interaction);

        // Ownership-scoped: only clear the busy window THIS interaction owns.
        if let Some(mut npc) = ctx.db.npc().npc_id().find(npc_id) {
            if npc.busy_interaction_id == iid {
                npc.busy_until_tick = 0;
                npc.busy_interaction_id = 0;
                ctx.db.npc().npc_id().update(npc);
            }
        }
    }

    // (3) Expired-busy sweep: clear busy windows whose deadline has passed.
    let expired: Vec<u64> = ctx
        .db
        .npc()
        .iter()
        .filter(|n| n.busy_until_tick > 0 && now >= n.busy_until_tick)
        .map(|n| n.npc_id)
        .collect();
    for npc_id in expired {
        if let Some(mut npc) = ctx.db.npc().npc_id().find(npc_id) {
            npc.busy_until_tick = 0;
            npc.busy_interaction_id = 0;
            ctx.db.npc().npc_id().update(npc);
        }
    }

    // (4) Prune npc_interaction to a cap: delete the lowest-id done|failed rows
    //     ONLY (NEVER pending|claimed, regardless of age) + their paired
    //     transcript rows. Collect + sort in Rust (no ORDER BY in STDB).
    let count = ctx.db.npc_interaction().count() as usize;
    if count > INTERACTION_PRUNE_CAP {
        let mut finished: Vec<u64> = ctx
            .db
            .npc_interaction()
            .iter()
            .filter(|i| i.status == "done" || i.status == "failed")
            .map(|i| i.id)
            .collect();
        finished.sort_unstable();
        let over = count - INTERACTION_PRUNE_CAP;
        let to_delete = over.min(INTERACTION_PRUNE_BATCH).min(finished.len());
        for id in finished.into_iter().take(to_delete) {
            ctx.db.npc_interaction().id().delete(id);
            ctx.db.npc_utterance().interaction_id().delete(id);
        }
    }

    // =======================================================================
    // APPENDED: billionaire / chat / hook / forest housekeeping (BATCH-bounded).
    // Each sweep prunes finished rows ONLY and caps work per tick.
    // =======================================================================

    // (B1) billionaire_request stale-claim reclaim (clone of the npc claimed-reclaim
    //      block, keyed on the singleton billionaire id=0). &str literal filter.
    let stale_reqs: Vec<BillionaireRequest> = ctx
        .db
        .billionaire_request()
        .status()
        .filter("claimed")
        .filter(|r| now - r.claimed_tick > CLAIM_TIMEOUT_TICKS)
        .collect();
    for mut request in stale_reqs {
        let rid = request.id;
        request.status = "pending".to_string();
        request.claimed_by = None;
        ctx.db.billionaire_request().id().update(request);

        if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
            if b.busy_interaction_id == rid {
                b.busy_until_tick = 0;
                b.busy_interaction_id = 0;
                ctx.db.billionaire().id().update(b);
            }
        }
    }

    // (B2) billionaire expired-busy sweep.
    if let Some(mut b) = ctx.db.billionaire().id().find(0u32) {
        if b.busy_until_tick > 0 && now >= b.busy_until_tick {
            b.busy_until_tick = 0;
            b.busy_interaction_id = 0;
            ctx.db.billionaire().id().update(b);
        }
    }

    // (B3) idle-on-ready impatience. EARLY-OUT on the first not-ready member.
    let member_count = ctx.db.lobby_member().count();
    if member_count > 0 && !ctx.db.lobby_member().iter().any(|m| !m.is_ready) {
        let max_ready = ctx
            .db
            .lobby_member()
            .iter()
            .map(|m| m.ready_tick)
            .max()
            .unwrap_or(0);
        let has_open_event = ctx
            .db
            .billionaire_request()
            .kind()
            .filter("lobby_event")
            .any(|r| r.status == "pending" || r.status == "claimed");
        let last_spoke = ctx.db.billionaire().id().find(0u32).map(|b| b.last_spoke_tick).unwrap_or(0);
        if now - max_ready > READY_IDLE_TICKS
            && !has_open_event
            && now - last_spoke > READY_IDLE_TICKS
            && open_request_count(ctx) < REQUEST_PENDING_CAP
        {
            // asker = any ready member (the impatience is at the team).
            if let Some(any_member) = ctx.db.lobby_member().iter().next() {
                ctx.db.billionaire_request().insert(BillionaireRequest {
                    id: 0,
                    kind: "lobby_event".to_string(),
                    asker: any_member.identity,
                    status: "pending".to_string(),
                    claimed_by: None,
                    context: "impatient".to_string(),
                    target_identity: None,
                    created_tick: now,
                    claimed_tick: 0,
                });
            }
        }
    }

    // (B4) NO swap-spam DB sweep (the director tracks swap_count delta in-process).

    // (B5) party_chat prune (lowest-id first, batch-capped).
    let chat_count = ctx.db.party_chat().count() as usize;
    if chat_count > CHAT_PRUNE_CAP {
        let mut ids: Vec<u64> = ctx.db.party_chat().iter().map(|c| c.id).collect();
        ids.sort_unstable();
        let over = chat_count - CHAT_PRUNE_CAP;
        let to_delete = over.min(CHAT_PRUNE_BATCH).min(ids.len());
        for id in ids.into_iter().take(to_delete) {
            ctx.db.party_chat().id().delete(id);
        }
    }

    // (B6) billionaire_request prune of done|failed ONLY (NEVER pending|claimed).
    let req_count = ctx.db.billionaire_request().count() as usize;
    if req_count > BILLIONAIRE_REQUEST_PRUNE_CAP {
        let mut finished: Vec<u64> = ctx
            .db
            .billionaire_request()
            .iter()
            .filter(|r| r.status == "done" || r.status == "failed")
            .map(|r| r.id)
            .collect();
        finished.sort_unstable();
        let over = req_count - BILLIONAIRE_REQUEST_PRUNE_CAP;
        let to_delete = over.min(BILLIONAIRE_REQUEST_PRUNE_BATCH).min(finished.len());
        for id in finished.into_iter().take(to_delete) {
            ctx.db.billionaire_request().id().delete(id);
        }
    }

    // (B7) lobby_hook prune (delete the PRIVATE truth companion with the hook).
    let hook_count = ctx.db.lobby_hook().count() as usize;
    if hook_count > HOOK_PRUNE_CAP {
        let mut ids: Vec<u64> = ctx.db.lobby_hook().iter().map(|h| h.hook_id).collect();
        ids.sort_unstable();
        let over = hook_count - HOOK_PRUNE_CAP;
        let to_delete = over.min(HOOK_PRUNE_BATCH).min(ids.len());
        for hook_id in ids.into_iter().take(to_delete) {
            ctx.db.lobby_hook().hook_id().delete(hook_id);
            ctx.db.lobby_hook_truth().hook_id().delete(hook_id);
        }
    }

    // (B8) forest_event prune (lowest-id first, batch-capped).
    let event_count = ctx.db.forest_event().count() as usize;
    if event_count > EVENT_PRUNE_CAP {
        let mut ids: Vec<u64> = ctx.db.forest_event().iter().map(|e| e.event_id).collect();
        ids.sort_unstable();
        let over = event_count - EVENT_PRUNE_CAP;
        let to_delete = over.min(EVENT_PRUNE_BATCH).min(ids.len());
        for event_id in ids.into_iter().take(to_delete) {
            ctx.db.forest_event().event_id().delete(event_id);
        }
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

fn default_input() -> InputState {
    InputState {
        forward: false,
        backward: false,
        left: false,
        right: false,
        sprint: false,
        jump: false,
        attack: false,
        cast_spell: false,
        sequence: 0,
    }
}

// Squared planar distance — drives playerDistance verification + NPC range checks.
#[allow(dead_code)]
fn dist2(ax: f32, az: f32, bx: f32, bz: f32) -> f32 {
    let dx = ax - bx;
    let dz = az - bz;
    dx * dx + dz * dz
}

// The single time source: world_state(0).now_tick (relocated from the deleted Game table).
fn cur_tick(ctx: &ReducerContext) -> i64 {
    ctx.db.world_state().id().find(0u32).map(|w| w.now_tick).unwrap_or(0)
}
