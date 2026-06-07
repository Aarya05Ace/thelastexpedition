// Weapon.cs — the equipped rifle's firing behaviour (lives on the holder, or on the AK instance).
//
// TryFire raycasts from a supplied origin/direction; the first collider whose root carries a Health of a
// DIFFERENT faction than the shooter takes 'damage'. Honours a fire-rate cooldown and spawns a cheap,
// self-destroying muzzle flash + tracer for visual feedback.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Fully null-safe; never throws.

using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

public class Weapon : MonoBehaviour
{
    [Tooltip("Damage per bullet.")]
    public float damage = 25f;

    [Tooltip("Shots per second; gates TryFire via CanFire.")]
    public float fireRate = 8f;

    [Tooltip("Maximum raycast distance, metres. Generous so a clear player shot reaches guards strung along " +
             "the whole gauntlet (the island is ~500 m). This does NOT make the AI snipe: CombatAI clamps its " +
             "own engage range to Mathf.Min(WeaponRange, sightRange) and sightRange stays small (~18 m).")]
    public float range = 500f;

    [Tooltip("Bullet origin + tracer/flash spawn point. Falls back to this.transform if null.")]
    public Transform muzzle;

    [Tooltip("Which physics layers the bullet ray can hit.")]
    public LayerMask hitMask = ~0;

    [Tooltip("Tracer/flash lifetime, seconds.")]
    public float vfxLifetime = 0.05f;

    [Tooltip("Rounds per magazine. After this many shots the shooter reloads.")]
    public int magazineSize = 25;

    [Tooltip("Seconds the reload takes; firing is blocked for this window, then the mag refills.")]
    public float reloadDuration = 2.2f;

    [Tooltip("Extra random aim error half-angle (deg) applied to EVERY shot from this weapon (gun-feel spread). The player weapon keeps this small; the AI adds its own larger cone on top.")]
    public float baseSpreadDeg = 0f;

    float nextFireTime;

    // Magazine state. roundsLeft is lazily initialised the first shot if Awake has not run yet, so the
    // weapon is correct even when fetched the same frame it is mounted (PlayerLoadout / CombatAI both poke it).
    int roundsLeft = -1;          // -1 = not yet initialised
    float reloadEndTime;          // Time.time at which an in-flight reload completes (0 = not reloading)

    void Awake()
    {
        if (roundsLeft < 0) roundsLeft = Mathf.Max(1, magazineSize);
    }

    // Ensure the magazine is initialised even if a caller reads state before Awake ran.
    void EnsureInit()
    {
        if (roundsLeft < 0) roundsLeft = Mathf.Max(1, magazineSize);
    }

    // ---- magazine / reload (the SINGLE owner of fire-availability for player AND AI) -----------------

    // True while a reload is in progress (no shot can leave the barrel). Completing the reload (refilling
    // the mag) is done lazily here so no coroutine / Update is required: the moment the window elapses the
    // mag is topped up. Both the player HUD and CombatAI poll this to show the lowered-weapon / RELOADING cue.
    public bool IsReloading
    {
        get
        {
            if (reloadEndTime <= 0f) return false;
            if (Time.time >= reloadEndTime)
            {
                // Reload finished: refill and clear the flag.
                roundsLeft = Mathf.Max(1, magazineSize);
                reloadEndTime = 0f;
                return false;
            }
            return true;
        }
    }

    // HUD readouts.
    public int RoundsLeft { get { EnsureInit(); return roundsLeft; } }
    public int MagazineSize => Mathf.Max(1, magazineSize);

    // Reload progress 0..1 (0 = just started, 1 = complete / not reloading). Read-only, side-effect-free: it
    // does NOT trigger the lazy refill (that is owned by IsReloading), so the HUD can poll it freely. Lets the
    // player read the EXACT length of the out-of-ammo window (the same window the design wants you to push on
    // enemies), so the reload reads as a real, timed beat instead of a static word.
    public float ReloadProgress01
    {
        get
        {
            if (reloadEndTime <= 0f) return 1f;
            float dur = Mathf.Max(0.1f, reloadDuration);
            float remaining = reloadEndTime - Time.time;
            return Mathf.Clamp01(1f - remaining / dur);
        }
    }

    // Begin a reload if not already reloading and the mag is not already full. Idempotent + null-safe.
    public void BeginReload()
    {
        EnsureInit();
        if (IsReloading) return;
        if (roundsLeft >= MagazineSize) return;
        reloadEndTime = Time.time + Mathf.Max(0.1f, reloadDuration);
    }

    // Player manual reload (R key). Returns true if a reload actually started (so callers can play the cue).
    public bool TryManualReload()
    {
        EnsureInit();
        if (IsReloading || roundsLeft >= MagazineSize) return false;
        BeginReload();
        return true;
    }

    // Cooldown elapsed AND not mid-reload AND at least one round chambered.
    public bool CanFire => Time.time >= nextFireTime && !IsReloading && RoundsLeft > 0;

    // Last discharged shot's outcome (for the player's +5-on-kill). Reset at the top of every fired shot, so a
    // shot that hits a wall or nothing leaves these at null/false.
    public Health LastHit { get; private set; }        // the Health the last TryFire damaged (or null)
    public bool   LastHitKilled { get; private set; }  // true if that shot transitioned the victim alive -> dead

    // Fire a single shot from 'origin' along 'dir' (need not be normalised). Returns true if the shot
    // was actually discharged (cooldown ready); false if still cooling down. The first Health of a
    // different faction than 'shooter' along the ray takes 'damage'. Self / same-faction / dead bodies
    // are skipped so the ray does NOT stop on them — it continues to the first valid victim or wall.
    public bool TryFire(Vector3 origin, Vector3 dir, Faction shooter)
    {
        if (!CanFire) return false;

        float interval = fireRate > 0f ? 1f / fireRate : 0.1f;
        nextFireTime = Time.time + interval;

        // Consume a round. CanFire already guaranteed RoundsLeft > 0 above.
        EnsureInit();
        roundsLeft--;

        // Clear last-shot outcome so a miss (wall / nothing) leaves null/false.
        LastHit = null;
        LastHitKilled = false;

        if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
        dir = dir.normalized;

        // Per-weapon base spread for gun feel (a small constant cone). The AI adds a larger cone on top of
        // this in CombatAI before calling TryFire; the player passes a near-pinpoint dir and relies on this.
        if (baseSpreadDeg > 0f)
        {
            Vector3 jitter = dir + Random.insideUnitSphere * Mathf.Tan(baseSpreadDeg * Mathf.Deg2Rad);
            if (jitter.sqrMagnitude > 1e-6f) dir = jitter.normalized;
        }

        Vector3 endPoint = origin + dir * range;

        // RaycastAll so we can skip self / same-faction / dead colliders without the ray being blocked
        // by them, and still hit a wall behind a friendly. Sorted by distance.
        var hits = Physics.RaycastAll(origin, dir, range, hitMask, QueryTriggerInteraction.Ignore);
        if (hits != null && hits.Length > 1)
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        if (hits != null)
        {
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.collider == null) continue;

                var health = hit.collider.GetComponentInParent<Health>();
                if (health != null)
                {
                    // A combatant collider: skip own/friendly/dead bodies and keep scanning,
                    // but DON'T let them block the bullet visually.
                    // NEUTRALS (the sister / civilians) are NON-COMBATANTS: a bullet passes THROUGH them and
                    // never damages them, so a stray shot or the AI's spread cone can't gun her down during the
                    // escort. (CombatAI already refuses to aim AT neutrals; this guarantees the path-of-fire too.)
                    if (health.faction == shooter || health.faction == Faction.Neutral || health.IsDead) continue;

                    bool wasDead = health.IsDead;          // false here (dead bodies were skipped above)
                    health.TakeDamage(damage, shooter);
                    LastHit = health;
                    LastHitKilled = !wasDead && health.IsDead;  // alive -> dead transition on THIS shot
                    endPoint = hit.point;
                    break;
                }
                else
                {
                    // Solid world geometry (no Health): the bullet stops here.
                    endPoint = hit.point;
                    break;
                }
            }
        }

        // Damage ray uses 'origin' (the player's camera-center ray = the crosshair) for pinpoint aim, but the
        // VISIBLE tracer should leave the GUN, not the camera — spawn it from the muzzle to the impact point.
        Vector3 tracerStart = muzzle != null ? muzzle.position : origin;
        SpawnTracer(tracerStart, endPoint);
        SpawnFlash(origin);

        // Out of ammo on THIS shot -> auto-reload. CanFire will block further shots until the window elapses,
        // for BOTH the player and the AI (both gate on CanFire), so neither becomes an infinite-fire turret.
        if (roundsLeft <= 0) BeginReload();
        return true;
    }

    // ---- cheap, code-generated, auto-destroying VFX (no prefabs needed) ----

    static Material s_lineMat;

    static Material LineMaterial()
    {
        if (s_lineMat != null) return s_lineMat;
        // HDRP "Unlit" is the cheapest emissive-looking line shader; fall back gracefully.
        var sh = Shader.Find("HDRP/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;
        s_lineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        if (s_lineMat.HasProperty("_UnlitColor")) s_lineMat.SetColor("_UnlitColor", new Color(1f, 0.85f, 0.4f, 1f));
        if (s_lineMat.HasProperty("_Color")) s_lineMat.SetColor("_Color", new Color(1f, 0.85f, 0.4f, 1f));
        return s_lineMat;
    }

    void SpawnTracer(Vector3 a, Vector3 b)
    {
        var go = new GameObject("Tracer");
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        lr.startWidth = lr.endWidth = 0.02f;
        lr.useWorldSpace = true;
        lr.numCapVertices = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        var mat = LineMaterial();
        if (mat != null) lr.sharedMaterial = mat;
        Destroy(go, Mathf.Max(0.01f, vfxLifetime));
    }

    // Muzzle flash — PRIMARY route is a fully code-generated, HDR-EMISSIVE flash that CANNOT render black:
    //
    //   1) a camera-facing quad with an OPAQUE HDRP/Lit HDR-emissive material (bright orange→yellow,
    //      _EmissiveIntensity high, exposure-independent) so the quad face emits warm fire light + bloom; and
    //   2) a brief HDRP point light at the muzzle (warm orange, raw-candela intensity) so the surrounding
    //      barrel/hands/world get a real lighting kick the moment the gun fires.
    //
    // Why not the WarFX prefab any more: its particle material uses the legacy "WFX/Additive Alpha8" shader
    // whose flash SHAPE lives ENTIRELY in the texture's alpha channel (grayScaleToAlpha, Alpha8 format) — the
    // RGB is black. The prior fix pointed HDRP/Unlit's _EmissiveColorMap at that texture, but HDRP samples the
    // map's RGB (= black) for emission, so the flash rendered BLACK. There is no reliable runtime way to make
    // HDRP read that alpha channel as the emissive shape, so we abandon the prefab and synthesize the flash.
    //
    // Why OPAQUE (not additive transparent): in HDRP, _SurfaceType/_BlendMode/_DoubleSidedEnable are NOT
    // honored by setting the float + EnableKeyword at runtime — they require HDMaterial.ValidateMaterial (an
    // editor-time / UI step) to recompute the GPU render state (blend op, stencil, depth, render queue, pass
    // enables). Set from script alone they silently no-op, so the "additive transparent" path was unreliable
    // (it kept the default OPAQUE render state). An OPAQUE HDRP/Lit material with a high HDR _EmissiveColor is
    // the project-VERIFIED recipe (identical to CharacterPodium's glow, which is confirmed bright in-game):
    // it ignores scene lighting, ignores auto-exposure (weight 0), bloom flares it, and it CANNOT render black
    // — emission only ever ADDS color on top of an opaque surface. No ValidateMaterial dependency.
    void SpawnFlash(Vector3 at)
    {
        Transform anchor = muzzle != null ? muzzle : transform;
        float life = Mathf.Max(0.02f, vfxLifetime);   // ~0.05s by default

        // ---- (1) HDR-emissive camera-facing quad ("the fire") ----
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "MuzzleFlash";
        // Strip the auto-added collider so the flash never blocks rays / physics.
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);

        go.transform.position = anchor.position + anchor.forward * 0.04f;  // just off the barrel tip
        go.transform.localScale = Vector3.one * 0.25f;
        // Face the camera if there is one (so the flat quad always shows its bright face), else face down-barrel.
        // The opaque quad is single-sided and its visible face normal is -Z, so aim its FORWARD (+Z) AWAY from
        // the camera (LookRotation toward go - cam) → the lit -Z face turns toward the viewer.
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 awayFromCam = go.transform.position - cam.transform.position;
            if (awayFromCam.sqrMagnitude < 1e-6f) awayFromCam = anchor.forward;
            go.transform.rotation = Quaternion.LookRotation(awayFromCam, Vector3.up);
        }
        else
        {
            go.transform.rotation = anchor.rotation;
        }

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mat = FlashMaterial();
            if (mat != null) mr.sharedMaterial = mat;
        }
        Destroy(go, life);

        // ---- (2) brief warm HDRP point light ("the muzzle blast lighting the scene") ----
        SpawnFlashLight(anchor.position + anchor.forward * 0.05f, life);
    }

    // Cached HDR-emissive OPAQUE flash material — the project-verified glow recipe (CharacterPodium /
    // MakeEmissiveMaterial). HDRP/Lit primary; HDRP/Unlit then legacy as fallbacks. Every texel emits warm
    // fire at high _EmissiveIntensity and bloom flares it; _EmissiveExposureWeight = 0 makes it ignore HDRP
    // auto-exposure so it can never be dimmed to nothing. OPAQUE so no runtime surface-type validation is
    // needed (the unreliable bit). CANNOT render black — emission only adds color.
    static Material s_flashMat;

    static Material FlashMaterial()
    {
        if (s_flashMat != null) return s_flashMat;

        var sh = Shader.Find("HDRP/Lit");
        if (sh == null) sh = Shader.Find("HDRP/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return null;

        s_flashMat = new Material(sh) { name = "MuzzleFlashEmissive", hideFlags = HideFlags.HideAndDontSave };

        // Warm muzzle-fire color (orange biased toward yellow at the core).
        var fire = new Color(1f, 0.62f, 0.18f, 1f);

        // Base/unlit color so the quad is visibly bright even on the (non-HDRP) fallback shaders. On HDRP/Lit
        // the base is dim-tinted (the EMISSION is what reads as fire), matching the podium recipe.
        if (s_flashMat.HasProperty("_BaseColor"))  s_flashMat.SetColor("_BaseColor", fire);
        if (s_flashMat.HasProperty("_UnlitColor")) s_flashMat.SetColor("_UnlitColor", fire);
        if (s_flashMat.HasProperty("_Color"))      s_flashMat.SetColor("_Color", fire);

        // HDR emission — this is what makes it "fire" in HDRP. Color drives the hue; _EmissiveIntensity
        // drives the brightness. _EmissiveExposureWeight = 0 makes it ignore auto-exposure (never dims).
        // Matches CharacterPodium.MakeEmissiveMaterial exactly (verified bright in-game).
        if (s_flashMat.HasProperty("_EmissiveColor"))         s_flashMat.SetColor("_EmissiveColor", fire * Mathf.LinearToGammaSpace(1f));
        if (s_flashMat.HasProperty("_UseEmissiveIntensity"))  s_flashMat.SetFloat("_UseEmissiveIntensity", 1f);
        if (s_flashMat.HasProperty("_EmissiveIntensity"))     s_flashMat.SetFloat("_EmissiveIntensity", 8f);
        if (s_flashMat.HasProperty("_EmissiveExposureWeight")) s_flashMat.SetFloat("_EmissiveExposureWeight", 0f);
        s_flashMat.EnableKeyword("_EMISSIVE_COLOR_MAP");
        s_flashMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        return s_flashMat;
    }

    // Brief warm HDRP point light at the muzzle. Uses the project-verified recipe: AddComponent<Light>() +
    // AddComponent<HDAdditionalLightData>() + hd.intensity = RAW CANDELA. (NOT AddHDLight, NOT LightUnit —
    // those APIs don't exist in this HDRP version and break the assembly; see CharacterPodium/LobbyBootstrap.)
    static void SpawnFlashLight(Vector3 pos, float life)
    {
        var go = new GameObject("MuzzleFlashLight");
        go.transform.position = pos;

        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(1f, 0.6f, 0.25f);   // warm orange
        l.range = 4f;

        // HDAdditionalLightData is required for the light to be visible at all in HDRP. Guard the
        // AddComponent so a non-HDRP project (or missing type) can never throw — the emissive quad above
        // is the guaranteed-visible part regardless.
        var hd = go.AddComponent<HDAdditionalLightData>();
        // RAW CANDELA (NOT lumens) — matches the project-verified podium/lobby recipe scale (250-450 for a
        // steady key). A muzzle pop is momentary (~0.05s), so a stronger 1500 reads as a punchy flash kick
        // without blowing out the whole scene through bloom. Tune down if it overexposes indoors.
        if (hd != null) hd.intensity = 1500f;

        Destroy(go, life);
    }
}
