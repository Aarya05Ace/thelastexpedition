// Health.cs. the damage receiver every combatant (player, ally, enemy) carries.
//
// Raycasts from Weapon.TryFire resolve a hit collider up to this component via GetComponentInParent<Health>(),
// so Health must live on the SAME root GameObject that CharacterRig.Apply put the collider on
// (CharacterController for the local player, CapsuleCollider for everyone else).
//
// Two-pool model (SHARED CONTRACT): GREEN health + BLUE shield. Damage drains the shield first, then
// overflows to health. The player spawns with maxHealth 100 + maxShield 50; AI combatants leave maxShield 0,
// so they are pure-green 100. Only health is healed (Heal targets green); ResetFull restores both for respawn.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Defensive: every public entry point is
// null/dead-safe, never throws, and OnDied fires exactly once.

using UnityEngine;

public class Health : MonoBehaviour
{
    [Tooltip("Starting + maximum GREEN health.")]
    public float maxHealth = 100f;

    [Tooltip("Starting + maximum BLUE shield (player sets 50; AI leaves 0). Absorbs damage before health.")]
    public float maxShield = 0f;

    [Tooltip("Combat allegiance; friendly fire (attacker == this faction) is ignored.")]
    public Faction faction = Faction.Neutral;

    [Tooltip("PLOT ARMOR: incoming damage is multiplied by this. 1 = normal (AI). The human player sets <1 " +
             "so the AI can't melt them (survivability for fun).")]
    public float damageTakenMultiplier = 1f;

    [Tooltip("ON-HIT RECOVERY: GREEN HP healed per second for 'recoveryDuration' seconds after taking damage " +
             "(0 = none; AI stays 0, the player gets 1 -> +5 over 5s). Synced to the blood-vignette fade-out.")]
    public float recoveryHealPerSec = 0f;
    [Tooltip("How long the post-hit recovery runs (s). Total heal per hit = recoveryHealPerSec * recoveryDuration.")]
    public float recoveryDuration = 5f;

    float recoveryTimer;   // counts down recoveryDuration -> 0 after each hit

    // 1 right after a hit, decaying to 0 as the recovery finishes (the HUD drives the blood-vignette fade from this).
    public float RecoveryProgress01 => recoveryDuration > 0f ? Mathf.Clamp01(recoveryTimer / recoveryDuration) : 0f;

    // Current GREEN health, clamped to [0, maxHealth]. Initialised on first use from maxHealth.
    public float CurrentHealth { get; private set; }
    // Current BLUE shield, clamped to [0, maxShield]. Initialised on first use from maxShield.
    public float CurrentShield { get; private set; }
    public bool IsDead { get; private set; }

    // (victim, attackerFaction). fired on every successful (non-ignored) damage application (HUD blood vignette).
    public System.Action<Health, Faction> OnDamaged;
    // (victim). fired exactly once when CurrentHealth first reaches 0.
    public System.Action<Health> OnDied;
    // (victim). fired on ANY health/shield change incl. damage, heal and reset (HUD bars bind to this).
    public System.Action<Health> OnChanged;

    bool initialised;

    void Awake()
    {
        EnsureInit();
    }

    // ON-HIT RECOVERY (player only; AI leaves recoveryHealPerSec = 0). For 'recoveryDuration' seconds after a
    // hit, heal GREEN health by recoveryHealPerSec/sec (this is the passive +5 that runs as the blood
    // vignette fades out. Heal() fires OnChanged so the bar updates live.
    void Update()
    {
        if (recoveryHealPerSec <= 0f || IsDead || recoveryTimer <= 0f) return;
        recoveryTimer -= Time.deltaTime;
        Heal(recoveryHealPerSec * Time.deltaTime);
    }

    // Idempotent init so the pools are valid even if a caller queries them before Awake runs.
    void EnsureInit()
    {
        if (initialised) return;
        if (maxHealth <= 0f) maxHealth = 1f;
        if (maxShield < 0f) maxShield = 0f;
        CurrentHealth = maxHealth;
        CurrentShield = maxShield;
        initialised = true;
    }

    // Apply 'amount' of damage attributed to 'attacker' faction.
    //   - ignored if already dead,
    //   - ignored if attacker == this faction (no friendly fire),
    //   - ignored for non-positive amounts,
    //   - BLUE shield absorbs first, any overflow drains GREEN health (clamped to 0),
    //   - fires OnDamaged + OnChanged on every applied hit, and OnDied once when health hits 0.
    public void TakeDamage(float amount, Faction attacker)
    {
        EnsureInit();
        if (IsDead) return;
        if (amount <= 0f) return;
        if (attacker == faction) return; // friendly fire suppressed

        amount *= Mathf.Max(0f, damageTakenMultiplier); // PLOT ARMOR (player < 1; AI = 1)
        if (amount <= 0f) return;

        recoveryTimer = recoveryDuration;   // (re)start the post-hit recovery + vignette fade-out

        // Shield first, then overflow to health.
        float toShield = Mathf.Min(CurrentShield, amount);
        CurrentShield -= toShield;
        float overflow = amount - toShield;
        if (overflow > 0f) CurrentHealth = Mathf.Max(0f, CurrentHealth - overflow);

        Fire(OnDamaged, attacker);
        Fire(OnChanged);

        if (CurrentHealth <= 0f && !IsDead)
        {
            IsDead = true;
            Fire(OnDied);
        }
    }

    // Restore GREEN health by 'amount', capped at maxHealth. No effect when dead or amount<=0. Fires OnChanged.
    public void Heal(float amount)
    {
        EnsureInit();
        if (IsDead) return;
        if (amount <= 0f) return;

        CurrentHealth = Mathf.Min(maxHealth, CurrentHealth + amount);
        Fire(OnChanged);
    }

    // Restore both pools to full and revive (used by respawn). Fires OnChanged.
    public void ResetFull()
    {
        if (maxHealth <= 0f) maxHealth = 1f;
        if (maxShield < 0f) maxShield = 0f;
        CurrentHealth = maxHealth;
        CurrentShield = maxShield;
        IsDead = false;
        initialised = true;
        Fire(OnChanged);
    }

    // --- callback helpers: never let a subscriber exception escape and break the caller ---

    void Fire(System.Action<Health> cb)
    {
        if (cb == null) return;
        try { cb(this); }
        catch (System.Exception e) { Debug.LogException(e); }
    }

    void Fire(System.Action<Health, Faction> cb, Faction attacker)
    {
        if (cb == null) return;
        try { cb(this, attacker); }
        catch (System.Exception e) { Debug.LogException(e); }
    }
}
