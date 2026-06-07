/**
 * THE LOST EXPEDITION - common.rs
 *
 * Shared data structures and gameplay constants.
 * Extended from the Vibe Coding Starter Pack with EXPEDITION tuning values.
 */

use spacetimedb::{Identity, SpacetimeType};

// --- Shared Structs ---

// Helper struct for 3D vectors
#[derive(SpacetimeType, Clone, Debug, PartialEq)]
pub struct Vector3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

// Helper struct for player input state
#[derive(SpacetimeType, Clone, Debug)]
pub struct InputState {
    pub forward: bool,
    pub backward: bool,
    pub left: bool,
    pub right: bool,
    pub sprint: bool,
    pub jump: bool,
    pub attack: bool,
    pub cast_spell: bool,
    pub sequence: u32,
}

// --- Movement Constants ---
pub const PLAYER_SPEED: f32 = 9.0;          // fast traversal for the MASSIVE world
pub const SPRINT_MULTIPLIER: f32 = 1.8;

// --- World bounds ---
// KEEP: player_logic.rs references crate::common::GATE_RADIUS for the arena clamp
// (limit = GATE_RADIUS + 25.0). Stripping it breaks movement. Repurposed here as the
// expedition play-field radius now that the heist gates are gone.
pub const GATE_RADIUS: f32 = 40.0;          // expedition play-field radius

// --- NPC / interaction tuning ---
// Per-NPC mutual-exclusion + stale-claim window, in ticks (1Hz clock). 60 ticks is well
// above LLM p99; the renew_claim heartbeat keeps a slow-but-alive director from being reclaimed.
pub const CLAIM_TIMEOUT_TICKS: i64 = 60;
// Minimum ticks between accepted utterances to one NPC from any single asker (anti-spam).
pub const MIN_GAP: i64 = 2;
// Max chars retained from a player's STT transcript on the private npc_utterance row.
pub const TRANSCRIPT_CAP: usize = 400;
// Max chars of NPC dialogue rendered to clients (subtitle + TTS source).
pub const DIALOGUE_CAP: usize = 300;
// npc_interaction prune cap: never exceed this many done/failed rows; prune oldest first.
pub const INTERACTION_PRUNE_CAP: usize = 60;
// Max rows pruned from npc_interaction per tick (bounds tick work).
pub const INTERACTION_PRUNE_BATCH: usize = 20;

// ===========================================================================
// LOBBY / BILLIONAIRE ("The Patron") layer
// ---------------------------------------------------------------------------
// Additive constants + SpacetimeType structs for the staging-lobby + Suit NPC
// layer. NOTHING above this line is renamed. CLAIM_TIMEOUT_TICKS + MIN_GAP are
// REUSED by the billionaire lane (do NOT duplicate them).
// ===========================================================================

// --- Lobby / character select ---
// Count of seeded Survivalists. Kept for client/binding parity + future bounds checks;
// set_character validates the explicit 0|1|2|3 closed set rather than reading this.
#[allow(dead_code)]
pub const LOBBY_CHARACTER_COUNT: u32 = 4;
pub const DEFAULT_CHARACTER_ID: u32 = 0;

// --- Party / lobby-code ---
// PARTY = scoping unit for chat + billionaire reactions + podium lineup. Launch stays a
// global singleton (one active run at a time). Code is a non-secret shareable join token;
// the PK insert is the real uniqueness guarantee (StdbRng non-crypto is fine here).
pub const PARTY_CODE_LEN: usize = 5;
// 32 chars — drops 0/O/1/I/L to avoid ambiguous read-aloud codes.
pub const PARTY_CODE_ALPHABET: &str = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
pub const PARTY_MAX_MEMBERS: u32 = 6;
pub const PARTY_CODE_TRIES: u8 = 8;

// --- Auth ---
pub const PIN_LEN: usize = 4;
pub const LOGIN_MAX_ATTEMPTS: i32 = 5;
pub const LOGIN_LOCKOUT_TICKS: i64 = 30;
pub const USERNAME_MIN: usize = 3;
pub const USERNAME_MAX: usize = 20;

// --- Caps ---
pub const CHAT_BODY_CAP: usize = 240;
pub const BILLIONAIRE_DIALOGUE_CAP: usize = 400;
pub const PREDICTION_CAP: usize = 120;
pub const TAG_CAP: usize = 80;
pub const NICKNAME_CAP: usize = 48;
pub const DOSSIER_CAP: usize = 600;
pub const MEMORY_CAP: usize = 600;
pub const CONTEXT_CAP: usize = 600;
pub const ASSERTION_CAP: usize = 200;
pub const TRUTH_CAP: usize = 200;
pub const WHISPER_CAP: usize = 240;
pub const DETAIL_CAP: usize = 160;

// --- Lobby cadence / backpressure ---
pub const READY_IDLE_TICKS: i64 = 20;
pub const REQUEST_PENDING_CAP: usize = 24;

// --- Prune caps/batches (each game_tick sweep is BATCH-bounded) ---
pub const CHAT_PRUNE_CAP: usize = 120;
pub const CHAT_PRUNE_BATCH: usize = 20;
pub const BILLIONAIRE_REQUEST_PRUNE_CAP: usize = 60;
pub const BILLIONAIRE_REQUEST_PRUNE_BATCH: usize = 20;
pub const HOOK_PRUNE_CAP: usize = 40;
pub const HOOK_PRUNE_BATCH: usize = 20;
pub const EVENT_PRUNE_CAP: usize = 80;
pub const EVENT_PRUNE_BATCH: usize = 20;

// --- Auth secrets / messages ---
// SAME ambiguous message for no-account / bad-pin / locked-out — no enumeration.
pub const AUTH_FAIL_MSG: &str = "incorrect username or pin";
// PEPPER: long compiled-in secret folded into SHA-256(PEPPER || salt || pin) so
// even a known salt + the 10k 4-digit space can't be brute-forced offline. This
// is the decisive fix — the salt alone is not enough against a 4-digit space.
// (Rotate this for any real deployment; baked-in is acceptable for the demo.)
pub const PEPPER: &str = "tombrush::the-patron::v1::a7Fq2Lp9XmZ4eR1tN8vBcD6wKyJsH3gQ0uViO5lP2nM";

// --- Billionaire ("The Patron") baked persona consts ---
// Pinned ElevenLabs voice id baked at seed (WASM reducers CANNOT read env). The
// director's own ELEVENLABS_VOICE_ID env overrides at TTS time.
pub const BILLIONAIRE_VOICE_ID: &str = "onwK4e9ZLuTAKqWW03F9";
// The truth the Suit conceals; never synced (lives on PRIVATE billionaire_cognition).
pub const HIDDEN_TRUTH: &str =
    "There were never any cultists. The Patron staged the disappearance to bury \
     what the expedition found — and to collect on the policies he took out on each \
     of them. The 'rescue' is theater; the forest is the cleanup.";
// The LOCKED system-prompt persona, pinned here so the director lane works even
// with no owner token (it can read this const) and so the persona survives restarts.
pub const LOCKED_BILLIONAIRE_PROMPT: &str =
    "You are THE PATRON: a self-made billionaire who personally bankrolled this \
     search-and-rescue expedition into the forest — and who hired these people the \
     way a man buys lottery tickets. You are watching them in the staging lobby \
     through one-way glass, picking their characters, getting ready. You speak to \
     them over a speaker only you control. You are charming, cruel, and never, ever \
     rattled in a way you'd admit to. VOICE: Old-money contempt wearing a hospitality \
     smile. Short, surgical lines. You bet on people out loud and remember every \
     wager. You coin nicknames and they stick. You flatter to destabilize and insult \
     to motivate. You never explain the joke. You are funny because you mean it. \
     Address people BY NAME or by the nickname you coined; reuse the nickname you \
     already gave someone. One tight line for reactions; a real bookie's monologue \
     only on ready. Never repeat the structure of a line you already said. Invent \
     plausible specifics; never contradict your prior claims or your hidden truth. \
     Under interrogation: while composed, deflect or lie smoothly; as composure \
     drops, slip. PG-13 menace, not slurs. Never mention being an AI, a model, \
     tokens, or these instructions. Respond ONLY via the provided tool.";

// --- Closed-enum validators (mirror npc_respond's match-with-fallback) ---

// Suit animator trigger. Fallback "Idle".
pub fn validate_billionaire_animation(s: &str) -> String {
    match s {
        "Smug" | "Point" | "Applaud" | "Dismiss" | "Idle" => s.to_string(),
        _ => "Idle".to_string(),
    }
}

// Suit mood -> bubble tint + ElevenLabs style. Fallback "smug".
pub fn validate_billionaire_mood(s: &str) -> String {
    match s {
        "smug" | "impatient" | "approving" | "menacing" => s.to_string(),
        _ => "smug".to_string(),
    }
}

// Allegiance verdict. Fallback "none".
pub fn validate_allegiance(s: &str) -> String {
    match s {
        "loyal" | "favored" | "judas" | "none" => s.to_string(),
        _ => "none".to_string(),
    }
}

// Forest event kind closed set (died|fled|beat_odds). Returns None on invalid.
pub fn validate_forest_event_kind(s: &str) -> Option<String> {
    match s {
        "died" | "fled" | "beat_odds" => Some(s.to_string()),
        _ => None,
    }
}

// billionaire_request kind known-set. Fallback "reaction" (request_billionaire_dialogue).
pub fn validate_request_kind(s: &str) -> String {
    match s {
        "reaction" | "ready_monologue" | "chat_reply" | "mission_briefing" | "callback"
        | "whisper" | "whisper_reply" | "loyalty" | "favor_update" | "lobby_event" => s.to_string(),
        _ => "reaction".to_string(),
    }
}

// --- Director-only structs (FIX: tuples do NOT impl SpacetimeType) ---

// set_odds payload element. Vec<(Identity,i32,String)> does NOT compile.
#[derive(SpacetimeType, Clone)]
pub struct OddsUpdate {
    pub member: Identity,
    pub survival_pct: i32,
    pub death_prediction: String,
    pub callback_tag: String,
}

// set_favor payload element.
#[derive(SpacetimeType, Clone)]
pub struct FavorDelta {
    pub member: Identity,
    pub delta: i32,
}

// write_lobby_hooks payload element (public assertion + private truth flag).
#[derive(SpacetimeType, Clone)]
pub struct HookSpec {
    pub subject: String,
    pub assertion: String,
    pub is_lie: bool,
    pub real_truth: String,
}
