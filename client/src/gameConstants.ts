// TOMB RUSH — client-side mirror of server gameplay constants (server/src/common.rs).
// Used for proximity-based auto pickup/bank/shove. The server re-validates everything,
// so these are slightly generous to keep the controls feeling responsive.

export const PICKUP_RANGE = 2.2;
export const SHOVE_RANGE = 3.0;
export const GATE_RANGE = 3.5;
export const GATE_RADIUS = 210.0; // bank gates ring the MASSIVE arena (~25x area)
export const RELIC_Y = 0.7;
export const NUM_RELICS = 26;
