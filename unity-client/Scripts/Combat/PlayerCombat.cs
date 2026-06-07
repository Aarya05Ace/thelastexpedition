// PlayerCombat.cs — client-side combat state for the LOCAL player (a Survivalist).
//
// LocalPlayer reads movement/look/typing input and DELEGATES the combat side here. This component owns the
// player's Health (100 GREEN + 50 BLUE shield, Survivalist faction) and the death handling, and exposes the
// PlayerLoadout (the single owner of the AK model + Animator "Armed"). The player SPAWNS HOLSTERED — the AK
// is NOT armed on spawn; inventory selection (slot 1) drives "Armed" and gates firing via Loadout.AkOut.
//
// HUD + DeathScreen read the static Local + Health + Loadout. Respawn() restores full health, re-enables input
// and re-holsters. Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Fully null-safe; never throws.

using UnityEngine;

[RequireComponent(typeof(Health))]
public class PlayerCombat : MonoBehaviour
{
    public const Faction PlayerFaction = Faction.Survivalist;
    public const float PlayerMaxHealth = 100f;
    public const float PlayerMaxShield = 50f;
    public const float PlayerDamageTaken = 0.3f;   // PLOT ARMOR: player only takes 30% of incoming damage

    // The LOCAL player's PlayerCombat (HUD + DeathScreen read this; null until spawned).
    public static PlayerCombat Local { get; private set; }

    // Kill-feedback hooks (KillHud listens): a shot CONNECTED with an enemy / a shot KILLED an enemy.
    public static System.Action OnEnemyHit;
    public static System.Action OnEnemyKilled;

    // When the player dies we latch this true; LocalPlayer halts all input (move/look/jump/fire) while set.
    public bool InputDisabled { get; private set; }

    // HUD / DeathScreen accessors.
    public Health Health => health;
    public PlayerLoadout Loadout => loadout;

    Health health;
    PlayerLoadout loadout;
    Animator anim;

    void Awake()
    {
        Local = this;

        // Health (the damage receiver every combatant carries) lives on this same root GameObject so the
        // collider CharacterRig.Apply added resolves to it via GetComponentInParent<Health>() in Weapon.TryFire.
        health = GetComponent<Health>();
        if (health == null) health = gameObject.AddComponent<Health>();
        health.maxHealth = PlayerMaxHealth;   // GREEN
        health.maxShield = PlayerMaxShield;   // BLUE (player-only)
        health.faction = PlayerFaction;
        health.damageTakenMultiplier = PlayerDamageTaken;   // PLOT ARMOR
        health.recoveryHealPerSec = 1f;                      // +1 HP/sec for...
        health.recoveryDuration = 5f;                        // ...5s after each hit = +5, as the blood vignette fades
        health.OnDied += HandleDied;

        anim = GetComponent<Animator>();

        // PlayerLoadout is the single owner of the AK model + "Armed" flag. Ensure it exists (order-independent;
        // it mounts the AK in its own Awake). We fetch the Weapon from it lazily in TryFire to avoid Awake-order
        // dependence. The player spawns HOLSTERED — Loadout starts Selected = 0, so no AK is out and !AkOut.
        loadout = GetComponent<PlayerLoadout>();
        if (loadout == null) loadout = gameObject.AddComponent<PlayerLoadout>();
    }

    void OnDestroy()
    {
        if (health != null) health.OnDied -= HandleDied;
        if (Local == this) Local = null;
    }

    // LEFT mouse -> fire one bullet from the camera-center ray (origin = camera position, dir = camera
    // forward) so it hits the crosshair, not the off-axis muzzle. Self-gates on AkOut: nothing fires unless
    // slot 1 (AK) is selected. The Weapon cooldown decides if a shot actually leaves the barrel; on a real
    // discharge we pulse the Animator "Fire" trigger.
    public void TryFire(Vector3 origin, Vector3 dir)
    {
        if (InputDisabled || loadout == null || !loadout.AkOut) return;
        var weapon = loadout.AkWeapon;
        if (weapon == null) return;
        if (weapon.TryFire(origin, dir, PlayerFaction))
        {
            if (anim != null && anim.runtimeAnimatorController != null)
                anim.SetTrigger("Fire");

            if (weapon.LastHit != null) OnEnemyHit?.Invoke();   // hitmarker on any connecting shot
            if (weapon.LastHitKilled)
            {
                OnEnemyKilled?.Invoke();                         // "ELIMINATED" confirmation
                if (health != null) health.Heal(5f);            // +5 on kill (player only)
            }
        }
    }

    // Player reached 0 HP: latch input off (LocalPlayer stops moving/looking/firing), holster the weapon
    // (Loadout.Select(0) also drops "Armed"), and play the full-body death state. We do NOT destroy the local
    // player — the corpse stays in the world until Respawn().
    void HandleDied(Health victim)
    {
        InputDisabled = true;
        loadout?.Select(0);   // holster -> "Armed" false
        if (anim != null && anim.runtimeAnimatorController != null)
            anim.SetTrigger("Dead");
    }

    // Bring the player back to full and re-enable input (HUD/DeathScreen call this from Replay). Restores both
    // health pools, re-holsters (empty hands), and clears the "Dead" trigger so the death state can exit.
    public void Respawn()
    {
        if (health != null) health.ResetFull();   // green = 100, blue = 50, alive again
        InputDisabled = false;
        loadout?.Select(0);                        // re-holster (empty hands, "Armed" false)
        if (anim != null && anim.runtimeAnimatorController != null)
            anim.ResetTrigger("Dead");
    }
}
