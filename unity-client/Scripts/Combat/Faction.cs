// Faction.cs — combat allegiance for the client-side firefight.
//
// Pure Unity, no SpacetimeDB types (so no Vector3 alias needed). Survivalists are allies (the local
// player's faction), Bodyguards are enemies, Neutral never fights and is never targeted as a combatant.
// Friendly-fire is suppressed inside Health.TakeDamage by comparing attacker == victim faction.

public enum Faction
{
    Survivalist,
    Bodyguard,
    Neutral
}
