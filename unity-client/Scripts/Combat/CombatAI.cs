// CombatAI.cs — a self-contained AI combatant for the client-side firefight (NOT networked).
//
// Each tick: acquire the nearest LIVE Health of a DIFFERENT faction within sightRange. With a target,
// turn to face it; if it's within weapon range AND there's a clear line of sight, fire (Weapon self-gates
// to its fireRate via CanFire). Out of range -> advance toward it (move the CharacterController if present,
// else the transform), which drives the Animator "Speed" via CharacterRig's CharacterLocomotion. No target
// -> idle. On death (Health.OnDied) the AI disables itself + its collider and despawns after a delay.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Every Animator write is guarded by a live
// parameter-existence check, so a controller WITHOUT the combat layer/params never logs an error or throws.
// Null-safe throughout; never throws.

using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Health))]
public class CombatAI : MonoBehaviour
{
    [Tooltip("Acquire + engage targets within this radius (metres). Tightened so only nearby guards react; the global attacker cap (MaxActiveAttackers) then limits how many of those may actually fire.")]
    public float sightRange = 18f;

    [Tooltip("Move speed when advancing on an out-of-range target (m/s).")]
    public float moveSpeed = 3.0f;

    [Tooltip("Stop advancing once within this distance of the target (metres).")]
    public float stopDistance = 2.5f;

    [Tooltip("Re-scan for the best target this often (seconds); cheap, not every frame.")]
    public float retargetInterval = 0.4f;

    [Tooltip("Turn rate toward the target (deg/s).")]
    public float turnRate = 360f;

    [Tooltip("Chest height above the pivot for the aim ray origin / target point (metres).")]
    public float aimHeight = 1.4f;

    [Tooltip("Seconds the corpse lingers before it's destroyed.")]
    public float despawnDelay = 6f;

    [Tooltip("Downward gravity applied to the CharacterController while advancing (m/s^2).")]
    public float gravity = -18f;

    // ---- ANTI-AIMBOT (makes the AI beatable + fun, not a laser) ----
    // DEMO TUNING (judge build): the guards read as too OP / unfair, so the skill is dialed DOWN here so a
    // single player can win the firefight. Wider miss cone, slower rate of fire, longer reaction time, and a
    // longer reload all open clear windows to push and clear them. (sightRange / moveSpeed / attacker cap
    // above are also tuned toward "beatable".)
    [Tooltip("Random aim error half-angle (deg) added to every shot so the AI misses a lot. Higher = easier.")]
    public float aimSpread = 18f;
    [Tooltip("Delay after acquiring a NEW target before the AI may fire (s). Human-like reaction time. Higher = easier.")]
    public float reactionDelay = 1.4f;
    [Tooltip("AI shots/sec. Its weapon fireRate is clamped to this (the player's is much faster). Lower = easier.")]
    public float aiFireRate = 1.0f;
    [Tooltip("Seconds the AI takes to reload after emptying its 25-round mag. Longer than the player's so each NPC has a clear 'out of ammo' window you can push.")]
    public float aiReloadDuration = 3.6f;

    // ---- GLOBAL ATTACKER CAP (so you're never shot by ~20 guards at once) ----
    // At most this many guards may ACTIVELY fire at the same target at once: the N nearest. Guards that
    // currently hold a target but are NOT in the nearest-N keep their rifle up and advance into a vacated
    // slot, so the gauntlet stays alive + tense without being a death wall. Tune 3..5 in-editor.
    [Tooltip("Max guards that may FIRE on the same target simultaneously (the N nearest). Others advance/hold.")]
    public int maxActiveAttackers = 2;

    // Static registry of every guard that currently has a live target, with its squared distance to that
    // target. Shared across all CombatAI instances so they can rank themselves. Pruned of null/stale keys on
    // every read, so no domain-reload reset is required (stale entries from a prior play session self-heal).
    static readonly Dictionary<CombatAI, float> Registry = new Dictionary<CombatAI, float>();

    bool mayFire;            // cached result of the nearest-N arbitration; refreshed on the retarget cadence
    bool registered;         // are we currently present in Registry (so we unregister exactly once)

    // Animator parameter names — the contract shared with LocomotionControllerBuilder / LocalPlayer.
    // NOTE: "Speed" is driven automatically by CharacterRig's CharacterLocomotion (from real movement),
    // so this AI only writes the combat params below.
    static readonly int ArmedHash = Animator.StringToHash("Armed");
    static readonly int FireHash  = Animator.StringToHash("Fire");
    static readonly int DeadHash  = Animator.StringToHash("Dead");

    Faction faction;
    Health self;
    Weapon weapon;
    Animator anim;
    CharacterController cc;

    Health target;
    float scanTimer;
    float vY;
    bool dead;
    float engageReadyTime;   // Time.time before which a freshly-acquired target may NOT be fired on (reaction)

    // Effective engagement range = the weapon's range (clamped to sightRange so we don't chase forever).
    float WeaponRange => weapon != null ? weapon.range : 0f;

    // Rifle is "up" (Armed) whenever we are equipped AND not mid-reload. Dropping Armed during the reload makes
    // the upper-body Combat layer fall back to the lowered locomotion pose (Empty<->Aim transitions already
    // exist), reading as "lower the rifle to swap a mag" with no new clip / controller change.
    bool RifleUp => weapon != null && !weapon.IsReloading;

    // Called by the spawner right after AddComponent, before Start, to set allegiance + cached refs.
    // Safe to call again later; re-resolves any missing references.
    public void Init(Faction f, Weapon w = null, Health h = null)
    {
        faction = f;
        if (w != null) weapon = w;
        if (h != null) self = h;
        ResolveRefs();
        if (self != null) self.faction = faction; // keep Health.faction authoritative + in sync
    }

    void Awake() { ResolveRefs(); }

    void Start()
    {
        ResolveRefs();
        if (self != null)
        {
            self.faction = faction;
            self.OnDied += HandleDied; // (idempotent enough: OnDied fires once, HandleDied guards via 'dead')
        }
    }

    void OnDestroy()
    {
        Unregister(); // never leave a stale slot behind that would block a live guard from firing
        if (self != null) self.OnDied -= HandleDied;
    }

    void ResolveRefs()
    {
        if (self == null) self = GetComponent<Health>();
        if (weapon == null) weapon = GetComponent<Weapon>();
        if (anim == null) anim = GetComponent<Animator>();
        if (cc == null) cc = GetComponent<CharacterController>();
        // Clamp the AI's rate of fire well below the player's snappy weapon (anti-aimbot).
        if (weapon != null && weapon.fireRate > aiFireRate) weapon.fireRate = aiFireRate;
        // Give the AI a longer reload window than the player so emptying its mag is a real opening.
        if (weapon != null) weapon.reloadDuration = aiReloadDuration;
    }

    void Update()
    {
        if (dead) return;
        if (self == null) { ResolveRefs(); if (self == null) return; }
        if (self.IsDead) { HandleDied(self); return; }

        // Periodic, cheap re-targeting. Acquiring a NEW target imposes a reaction delay before firing.
        scanTimer += Time.deltaTime;
        if (scanTimer >= retargetInterval || target == null)
        {
            scanTimer = 0f;
            var prev = target;
            target = AcquireTarget();
            if (target != null && target != prev) engageReadyTime = Time.time + reactionDelay;
        }

        // No valid target: idle (stand still) but KEEP the rifle up if we're equipped — this mirrors the
        // player, who shows Armed whenever the AK is in hand regardless of whether an enemy is present.
        // Driving Armed off weapon presence (not "has a target") is THE fix for the arms-down / floating-gun
        // pose: an equipped combatant holds the Rifle_Aim01 upper-body pose at all times, idle or fighting.
        if (target == null || target.IsDead)
        {
            target = null;
            Unregister();                 // drop our slot so the next-nearest guard can take a turn
            mayFire = false;
            SetBoolSafe(ArmedHash, RifleUp);
            ApplyGravityIdle();
            return;
        }

        Vector3 myPos = transform.position;
        Vector3 targetPos = target.transform.position;
        Vector3 flat = targetPos - myPos; flat.y = 0f;
        float dist = flat.magnitude;

        // Publish our current distance-to-target into the shared registry so all guards can rank themselves.
        // Refresh the nearest-N decision on the (cheap) retarget cadence, not every frame.
        Register(dist * dist);
        if (scanTimer == 0f) mayFire = RankAllowsFire();

        // Face the target (yaw only).
        if (flat.sqrMagnitude > 1e-4f)
        {
            Quaternion look = Quaternion.LookRotation(flat.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnRate * Time.deltaTime);
        }

        float engageRange = Mathf.Min(WeaponRange, sightRange);
        bool inRange = dist <= engageRange;

        if (inRange)
        {
            // Hold position; raise the rifle (gated on being equipped + not reloading); fire ONLY if we rank
            // among the N nearest attackers (the global cap) AND have a clear shot (Weapon gates its own
            // cadence + blocks firing while reloading). Guards that are in range but NOT in the firing slot
            // keep the rifle up and creep forward so they slide into a vacated slot as nearer guards die.
            SetBoolSafe(ArmedHash, RifleUp);
            if (mayFire)
            {
                ApplyGravityIdle();
                TryShoot(targetPos);
            }
            else if (dist > stopDistance)
            {
                Advance(flat.normalized); // waiting our turn: close in slowly instead of standing idle
            }
            else
            {
                ApplyGravityIdle();
            }
        }
        else
        {
            // Advance toward the target until within stopDistance; CharacterLocomotion turns this into "Speed".
            SetBoolSafe(ArmedHash, RifleUp); // keep the rifle up while closing the distance (down while reloading)
            if (dist > stopDistance) Advance(flat.normalized);
            else ApplyGravityIdle();
        }
    }

    // ---- global attacker cap ------------------------------------------------------------------------

    // Record (or refresh) our squared distance to the current target in the shared registry.
    void Register(float sqrDistToTarget)
    {
        Registry[this] = sqrDistToTarget;
        registered = true;
    }

    // Remove ourselves from the registry (target lost / death / destroy). Safe to call repeatedly.
    void Unregister()
    {
        if (!registered) return;
        Registry.Remove(this);
        registered = false;
    }

    // We may fire only if we rank among the N nearest registered attackers. Counts how many OTHER live,
    // registered guards are strictly closer to their target than we are; allow fire while that count is below
    // the cap. O(n) over only guards-that-have-a-target (a small set), and prunes any null/stale keys it
    // meets so the static map self-heals across play-mode sessions without a domain-reload hook.
    bool RankAllowsFire()
    {
        if (maxActiveAttackers <= 0) return false;
        if (!registered) return false;

        float myDist;
        if (!Registry.TryGetValue(this, out myDist)) return false;

        int closer = 0;
        List<CombatAI> stale = null;
        foreach (var kv in Registry)
        {
            var other = kv.Key;
            if (other == null) { (stale ??= new List<CombatAI>()).Add(other); continue; }
            if (other == this) continue;
            if (kv.Value < myDist) closer++;
        }
        if (stale != null) for (int i = 0; i < stale.Count; i++) Registry.Remove(stale[i]);

        return closer < maxActiveAttackers;
    }

    // ---- targeting ----------------------------------------------------------------------------------

    Health AcquireTarget()
    {
        Health best = null;
        float bestSqr = sightRange * sightRange;
        Vector3 myPos = transform.position;

        var all = FindObjectsByType<Health>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var h = all[i];
            if (h == null || h == self) continue;
            if (h.IsDead) continue;
            if (h.faction == faction) continue;          // same faction -> ally, never a target
            if (h.faction == Faction.Neutral) continue;  // neutrals (civilians) are not combatants

            float d = (h.transform.position - myPos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = h; }
        }
        return best;
    }

    // ---- firing -------------------------------------------------------------------------------------

    void TryShoot(Vector3 targetWorldPos)
    {
        if (weapon == null) return;
        if (Time.time < engageReadyTime) return; // still reacting to a freshly-acquired target
        if (!weapon.CanFire) return;             // respect the (slowed) AI fire-rate cooldown

        Vector3 from = weapon.muzzle != null ? weapon.muzzle.position : transform.position + Vector3.up * aimHeight;
        Vector3 aimAt = targetWorldPos + Vector3.up * aimHeight;
        Vector3 dir = aimAt - from;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        if (!HasLineOfSight(from, dir)) return; // need a real line to the target (true aim)

        // ...but the actual bullet SPRAYS within a cone, so the AI misses a lot — beatable, not an aimbot.
        Vector3 shotDir = ApplySpread(dir, aimSpread);
        if (weapon.TryFire(from, shotDir, faction))
            SetTriggerSafe(FireHash);
    }

    // Perturb a fire direction by a random cone (half-angle in degrees).
    static Vector3 ApplySpread(Vector3 dir, float halfAngleDeg)
    {
        if (halfAngleDeg <= 0f) return dir;
        Vector3 spread = dir + Random.insideUnitSphere * Mathf.Tan(halfAngleDeg * Mathf.Deg2Rad);
        return spread.sqrMagnitude > 1e-6f ? spread.normalized : dir;
    }

    // Clear shot if the first thing the ray meets is either open air to the target distance, the target's
    // own faction-enemy body, or world geometry that is closer than... well — we accept any non-friendly
    // first hit. We only REFUSE the shot if a same-faction/dead body is the very first solid thing hit AND
    // would be struck (Weapon itself skips friendlies, so this LOS check mostly avoids wasting shots into
    // walls). Conservative + cheap: one RaycastAll, stop at the first blocker.
    bool HasLineOfSight(Vector3 from, Vector3 dir)
    {
        float range = Mathf.Min(WeaponRange, sightRange);
        if (range <= 0f) range = sightRange;

        var hits = Physics.RaycastAll(from, dir, range,
            weapon != null ? weapon.hitMask : ~0, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0) return true; // nothing in the way at all

        if (hits.Length > 1)
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            var col = hits[i].collider;
            if (col == null) continue;

            var h = col.GetComponentInParent<Health>();
            if (h != null)
            {
                if (h == self) continue;                 // never blocked by our own collider
                // Friendlies, corpses, AND neutrals (the sister/civilians) do NOT block the ray and do NOT count
                // as a clear shot: the bullet passes through them (Weapon skips them too). This stops the AI from
                // firing just because a civilian is in the line of fire during the escort.
                if (h.faction == faction || h.faction == Faction.Neutral || h.IsDead) continue;
                return true;                             // first solid thing is an enemy -> clear shot
            }
            else
            {
                return false;                            // first solid thing is world geometry -> blocked
            }
        }
        return true; // only friendlies/corpses along the ray -> the bullet would pass through to the target
    }

    // ---- movement ------------------------------------------------------------------------------------

    void Advance(Vector3 dir)
    {
        Vector3 horiz = dir * moveSpeed;
        if (cc != null && cc.enabled)
        {
            if (cc.isGrounded && vY < 0f) vY = -2f;
            vY += gravity * Time.deltaTime;
            Vector3 motion = horiz; motion.y = vY;
            cc.Move(motion * Time.deltaTime);
        }
        else
        {
            // No CharacterController on this body -> move the transform directly. CharacterLocomotion
            // derives "Speed" from the transform delta, so locomotion still animates.
            transform.position += horiz * Time.deltaTime;
        }
    }

    // Keep the body settled on the ground while idle/holding (apply gravity through the controller),
    // and let "Speed" decay to idle via CharacterLocomotion (it reads cc.velocity / transform delta).
    void ApplyGravityIdle()
    {
        if (cc != null && cc.enabled)
        {
            if (cc.isGrounded && vY < 0f) vY = -2f;
            vY += gravity * Time.deltaTime;
            cc.Move(new Vector3(0f, vY, 0f) * Time.deltaTime);
        }
    }

    // ---- death ---------------------------------------------------------------------------------------

    void HandleDied(Health _victim)
    {
        if (dead) return;
        dead = true;

        Unregister();           // free our firing slot the instant we die so the next-nearest guard takes over
        mayFire = false;

        SetBoolSafe(ArmedHash, false);
        SetTriggerSafe(DeadHash);

        // Stop fighting + stop colliding (so the corpse doesn't block live combatants or eat bullets),
        // but keep the model visible until it despawns.
        enabled = false;
        if (cc != null) cc.enabled = false;
        foreach (var col in GetComponents<Collider>()) if (col != null) col.enabled = false;

        // Stop CharacterLocomotion from driving Speed on a dead body (otherwise it twitches).
        var loco = GetComponent<CharacterLocomotion>();
        if (loco != null) loco.enabled = false;

        Destroy(gameObject, Mathf.Max(0.5f, despawnDelay));
    }

    // ---- Animator guards (combat layer/params may not be built yet) -----------------------------------

    bool HasParam(int hash)
    {
        if (anim == null || anim.runtimeAnimatorController == null) return false;
        var ps = anim.parameters;
        for (int i = 0; i < ps.Length; i++) if (ps[i].nameHash == hash) return true;
        return false;
    }

    void SetBoolSafe(int hash, bool value) { if (HasParam(hash)) anim.SetBool(hash, value); }
    void SetTriggerSafe(int hash) { if (HasParam(hash)) anim.SetTrigger(hash); }
}
