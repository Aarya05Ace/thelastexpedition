// PlayerCombat.cs — client-side combat state for the LOCAL player (a Survivalist).
//
// LocalPlayer reads movement/look/typing input and DELEGATES the combat side here. This component owns the
// player's Health (100 GREEN + 50 BLUE shield, Survivalist faction) and the death handling, and exposes the
// PlayerLoadout (the single owner of the AK model + Animator "Armed"). The player SPAWNS HOLSTERED — the AK
// is NOT armed on spawn; inventory selection (slot 1) drives "Armed" and gates firing via Loadout.AkOut.
//
// HUD + DeathScreen read the static Local + Health + Loadout. DEATH IS TERMINAL (Fortnite, one life per
// deploy): on death input latches off PERMANENTLY, the weapon holsters, the death state plays, and the only
// way to play again is RETURN TO LOBBY -> re-queue -> DEPLOY (which reloads the forest scene and spawns a
// fresh body). There is NO in-place respawn. Pure Unity, no SpacetimeDB types -> no Vector3 alias needed.
// Fully null-safe; never throws.

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

    // Camera-recoil hook: invoked on each discharged shot with the upward pitch kick (deg). LocalPlayer
    // subscribes and eases it back so the view climbs slightly as you hold fire. Null-safe (no-op if unset).
    public static System.Action<float> OnRecoil;

    // ---- gunplay feel tuning (player) ----
    [Tooltip("Base aim-spread half-angle (deg) at the first shot of a burst. Small so single shots stay crisp.")]
    public float spreadBaseDeg = 0.5f;
    [Tooltip("Spread half-angle (deg) added per consecutive shot, up to spreadMaxDeg, then it decays when not firing.")]
    public float spreadPerShotDeg = 0.35f;
    [Tooltip("Maximum spread half-angle (deg) while holding fire.")]
    public float spreadMaxDeg = 2.5f;
    [Tooltip("Spread half-angle recovered per second when not firing (deg/s).")]
    public float spreadRecoverPerSec = 6f;
    [Tooltip("Upward camera pitch kick per shot (deg), eased back by LocalPlayer.")]
    public float recoilPitchDeg = 0.6f;

    // When the player dies we latch this true; LocalPlayer halts all input (move/look/jump/fire) while set.
    public bool InputDisabled { get; private set; }

    // TERMINAL death latch. Once dead, this stays true for the life of this body (no in-place revive). LocalPlayer
    // reads it to FULLY freeze the corpse (disable the CharacterController, stop streaming position). The only
    // way back is RETURN TO LOBBY -> re-DEPLOY, which destroys this body on the scene reload.
    public bool IsDeadTerminal { get; private set; }

    // HUD / DeathScreen accessors.
    public Health Health => health;
    public PlayerLoadout Loadout => loadout;

    Health health;
    PlayerLoadout loadout;
    Animator anim;

    float currentSpreadDeg;       // grows per shot, decays when not firing (bloom)
    float lastShotTime;           // for spread decay timing
    bool wasReloading;            // edge-detect the reload window for the one-shot cue + lowered weapon

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

    // LEFT mouse -> fire one bullet ALONG the camera-center line (dir = camera forward) so it hits the
    // crosshair, not the off-axis muzzle. Self-gates on AkOut: nothing fires unless slot 1 (AK) is selected.
    // The Weapon cooldown decides if a shot actually leaves the barrel; on a real discharge we pulse the
    // Animator "Fire" trigger.
    //
    // 'camOrigin' is the third-person orbit camera position (ExpeditionCamera sits ~4 m BEHIND/above/right of
    // the body). Raycasting straight from there would pass through the player's OWN body and through any rock
    // or tree right next to the player (now solid after RockSolidifier) BEFORE reaching the target, and that
    // near geometry has no Health, so Weapon would stop the bullet on it (a "clear" crosshair shot would
    // register nothing). FIX: advance the ray origin forward along the camera line to roughly the player's
    // plane, so geometry hugging the camera/player BEHIND the muzzle can never eat the shot, while real cover
    // BETWEEN the player and the target still blocks (acceptable). Same line, same crosshair: we only trim the
    // dead near segment, so aim stays pinpoint.
    public void TryFire(Vector3 camOrigin, Vector3 dir)
    {
        if (InputDisabled || loadout == null || !loadout.AkOut) return;
        var weapon = loadout.AkWeapon;
        if (weapon == null) return;

        if (dir.sqrMagnitude < 1e-6f) dir = transform.forward;
        dir = dir.normalized;

        // Advance the origin along the SAME camera-forward line, past the player's body, so it starts in front
        // of the muzzle. We push to the camera's distance to the player plus a small margin to clear the body
        // radius, so the player capsule + any rock/tree clinging to the player can never be the first hit.
        Vector3 origin = MuzzleLineOrigin(camOrigin, dir);

        // Apply a growing aim-spread cone so sustained fire walks off the dot (recoil/bloom), recovering when
        // you let off. The first shot of a burst is near-pinpoint (spreadBaseDeg), so tap-firing stays crisp.
        Vector3 shotDir = ApplySpread(dir, spreadBaseDeg + currentSpreadDeg);

        if (weapon.TryFire(origin, shotDir, PlayerFaction))
        {
            // Bloom the cone for the next shot + kick the camera up a touch; mark the shot time for decay.
            currentSpreadDeg = Mathf.Min(spreadMaxDeg, currentSpreadDeg + spreadPerShotDeg);
            lastShotTime = Time.time;
            if (recoilPitchDeg > 0f) OnRecoil?.Invoke(recoilPitchDeg);

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

    // Manual reload (R key, routed from LocalPlayer). No-op unless the AK is out + the weapon can reload.
    public void RequestReload()
    {
        if (InputDisabled || loadout == null || !loadout.AkOut) return;
        var weapon = loadout.AkWeapon;
        if (weapon != null) weapon.TryManualReload();
    }

    // Per-frame: decay the spread cone toward 0 between shots, and edge-detect the reload window to fire the
    // reload cue ONCE (sound) + lower the rifle for the duration (procedural mag swap), restoring it after.
    void Update()
    {
        // Spread recovers toward 0 once you stop firing (a short grace, then linear decay).
        if (Time.time - lastShotTime > 0.06f && currentSpreadDeg > 0f)
            currentSpreadDeg = Mathf.Max(0f, currentSpreadDeg - spreadRecoverPerSec * Time.deltaTime);

        var weapon = loadout != null ? loadout.AkWeapon : null;
        bool reloading = weapon != null && weapon.IsReloading;

        if (reloading != wasReloading)
        {
            wasReloading = reloading;
            if (reloading)
            {
                AudioDirector.PlayReload();          // click-clack on the rising edge (no-op if no clip)
                currentSpreadDeg = 0f;               // reset bloom: the next mag starts crisp
            }
            // Lower the rifle while reloading, raise it again when done (only writes Armed while AK is out).
            if (loadout != null) loadout.SetReloading(reloading);
        }
    }

    // Perturb a fire direction by a random cone (half-angle in degrees). Mirrors CombatAI.ApplySpread.
    static Vector3 ApplySpread(Vector3 dir, float halfAngleDeg)
    {
        if (halfAngleDeg <= 0f) return dir.normalized;
        Vector3 spread = dir.normalized + Random.insideUnitSphere * Mathf.Tan(halfAngleDeg * Mathf.Deg2Rad);
        return spread.sqrMagnitude > 1e-6f ? spread.normalized : dir.normalized;
    }

    // Project the third-person camera position FORWARD onto its own aim line to a point just past the player's
    // body, and return that as the bullet origin. The direction is unchanged, so the ray stays on the exact
    // crosshair line; we only skip the dead segment from the camera to in front of the player so the player's
    // own capsule and any solid rock/tree hugging the camera or player cannot be the first hit and eat the
    // shot. Real cover BETWEEN this point and the target still blocks. Null-safe; falls back to camOrigin.
    Vector3 MuzzleLineOrigin(Vector3 camOrigin, Vector3 dir)
    {
        // How far along the camera-forward line the player's pivot sits (projected distance camera -> player).
        // We add a margin to clear the body radius + any geometry pressed against the player. Clamped so we
        // never overshoot a point-blank target (a guard within ~1 m still gets hit).
        Vector3 toPlayer = transform.position - camOrigin;
        float along = Vector3.Dot(toPlayer, dir);          // signed distance of the player along the aim line
        float advance = Mathf.Clamp(along + 0.6f, 0f, 6f); // +0.6 m margin past the body; cap at the orbit arm
        return camOrigin + dir * advance;
    }

    // Player reached 0 HP: TERMINAL. Latch input off PERMANENTLY (LocalPlayer stops moving/looking/firing AND
    // disables the CharacterController so the corpse can't crawl/settle), holster the weapon (Loadout.Select(0)
    // also drops "Armed"), and play the full-body death state. We do NOT destroy the local player here and we do
    // NOT revive it. The corpse is frozen for the brief death beat, then DeathScreen returns to the lobby and
    // the scene reload destroys it. One life per deploy (Fortnite).
    void HandleDied(Health victim)
    {
        InputDisabled = true;
        IsDeadTerminal = true;
        loadout?.Select(0);   // holster -> "Armed" false
        if (anim != null && anim.runtimeAnimatorController != null)
            anim.SetTrigger("Dead");
    }
}
