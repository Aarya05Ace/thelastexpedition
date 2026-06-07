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

using UnityEngine;

[RequireComponent(typeof(Health))]
public class CombatAI : MonoBehaviour
{
    [Tooltip("Acquire + engage targets within this radius (metres).")]
    public float sightRange = 45f;

    [Tooltip("Move speed when advancing on an out-of-range target (m/s).")]
    public float moveSpeed = 3.5f;

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
    [Tooltip("Random aim error half-angle (deg) added to every shot so the AI misses a lot.")]
    public float aimSpread = 10f;
    [Tooltip("Delay after acquiring a NEW target before the AI may fire (s) — human-like reaction time.")]
    public float reactionDelay = 0.85f;
    [Tooltip("AI shots/sec — its weapon fireRate is clamped to this (the player's is much faster).")]
    public float aiFireRate = 1.8f;

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
            SetBoolSafe(ArmedHash, weapon != null);
            ApplyGravityIdle();
            return;
        }

        Vector3 myPos = transform.position;
        Vector3 targetPos = target.transform.position;
        Vector3 flat = targetPos - myPos; flat.y = 0f;
        float dist = flat.magnitude;

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
            // Hold position; raise the rifle (gated on being equipped, like the player); fire if we have a
            // clear shot (Weapon gates its own cadence).
            SetBoolSafe(ArmedHash, weapon != null);
            ApplyGravityIdle();
            TryShoot(targetPos);
        }
        else
        {
            // Advance toward the target until within stopDistance; CharacterLocomotion turns this into "Speed".
            SetBoolSafe(ArmedHash, weapon != null); // keep the rifle up while closing the distance
            if (dist > stopDistance) Advance(flat.normalized);
            else ApplyGravityIdle();
        }
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
                if (h.faction == faction || h.IsDead) continue; // friendlies/corpses don't block (Weapon skips them too)
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
