// ExpeditionPopulation.cs. self-bootstrapping world populator for THE LAST EXPEDITION.
//
// Spawns the broader cast that fills the playable forest, on top of (and independent from) the small
// CombatSpawner firefight. It waits the SAME way every HUD / CombatSpawner waits. a RuntimeInitialize
// bootstrap that creates one persistent host object, then polls until NetworkedWorld.GameplayActive is
// true AND a LocalPlayer exists. It then polls one more gate (RescueMission.CabinPosition is set), then
// spawns ONCE and disables itself.
//
// It spawns:
//   (a) 18 BODYGUARDS strung as a GAUNTLET along the route from the player spawn to the cabin at the far
//       end of the island (RescueMission.CabinPosition, the shared static the mission agent exposes): each
//       gets the SAME full pipeline a combat body gets. model -> HDRP fix -> CharacterRig.Apply(go,false) ->
//       Health(100, Bodyguard) -> WeaponRig.Equip (AK gripped via WeaponMount) -> CombatAI(Bodyguard). They
//       use the EXACT resolvers the firefight uses (BodyguardModels.ResolvePrefab cycling the 4 bodyguard
//       FBX), so the gun physics, rig, controller, and brain are identical to the proven CombatSpawner path.
//
// The SISTER is no longer spawned here. The scripted RescueMission now OWNS the sister (placed inside the
// cabin at the far end, idle until the reunion). There is exactly one sister and one owner.
//
// NEW MISSION 1 LAYOUT, THE GAUNTLET (replaces the old annulus scatter):
//   The mission places a CABIN at the far end of the island and exposes its world position as
//   RescueMission.CabinPosition. The objective is "Reach the cabin ... Cut through the Curator's men." So
//   the 18 bodyguards are no longer ringed around the player. they are distributed ALONG the straight
//   segment from the player spawn (start) to the cabin (end), with lateral spread off the path so the
//   player has to fight through a corridor of men to reach the door. Density is biased toward the cabin
//   end (later guards are placed deeper along the path), and a CLEAR ZONE is kept right at the cabin door
//   so the emotional reunion is not a firefight.
//
// DISTRIBUTION (per guard i of N, no NavMesh exists in this project):
//   t  = a fraction along the spawn->cabin segment, biased toward the cabin (t grows with i, weighted so
//        the back half of the path is denser). The first guards sit a little way out from spawn (so the
//        player gets a beat after landing); the last guards sit just shy of the cabin clear zone.
//   pos = lerp(spawn, cabin, t) + lateral * perpendicular(path), where 'lateral' alternates left/right
//         and grows mildly with distance from the path centre, then is ground-snapped + clamped reachable
//         + ClearSpawn-style separated so two guards never co-locate.
//   The Y is set by the same down-raycast ground-snap every other spawn uses (lowest hit = terrain, not
//   tree canopy). This matches NetworkedWorld.V / CombatSpawner.GroundSnap.
//
// FALLBACK: if RescueMission.CabinPosition is still zero after a bounded wait (timing / the cabin failed to
// place), fall back to the OLD golden-angle annulus scatter around the player so the world is never empty.
//
// REACHABILITY CLAMP: every gauntlet XZ is additionally clamped to a conservative reachable terrain box
// (ClampToReachable) so no bodyguard can ever land off the terrain. Research mapped the spawn anchor near
// the -X/-Z region of a ~500 m heightmap; the box below stays inside the confirmed bounds. The cabin itself
// is placed by the mission inside reachable terrain, so the whole spawn->cabin segment is on the map.
//
// BUILD-SAFE: every model/weapon/controller load goes through the existing Resources-primary resolvers
// (BodyguardModels / NpcModels / WeaponRig / CharacterRig), so this spawns in BOTH the editor AND a player
// build. Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Null-safe; never throws. Does NOT
// edit CharacterRig, the UI, RescueMission, or the audio (owned by other agents).

using UnityEngine;

public class ExpeditionPopulation : MonoBehaviour
{
    // ---- simple tunable constants (counts + gauntlet shape) ------------------------------------------
    // PLAYABILITY PASS: the old 18-guard, cabin-biased, flaring layout collapsed into a death wall at the door
    // (and some guards embedded in rocks). The numbers below spread a BEATABLE squad EVENLY along the longer
    // spawn->cabin path (with a 25-round reload rhythm on both sides), keep the cabin end THINNER not denser,
    // and run a hard clearance that actually re-rolls a guard out of any rock/tree it would land in.
    const int   BodyguardCount = 11;     // beatable squad strung along the route (was 18 death wall)

    // Gauntlet placement along the spawn->cabin segment (the primary layout).
    const float PathStartT     = 0.12f;  // first guard sits ~12% out from spawn (a beat after landing)
    const float PathEndT       = 0.88f;  // last guard sits ~88% along, short of the cabin clear zone
    const float DensityBias    = 0.85f;  // <1 spreads guards EVENLY and slightly THINS the cabin end (t = lerp ^
                                         // (1/bias)); was 1.3 which packed the back half into a wall at the door
    const float CabinClearRadius = 8f;   // keep this radius around the cabin door free for the reunion

    // SPAWN BUFFER (Ask 2): an ABSOLUTE metre clearance the player gets after landing before the FIRST guard.
    // Converted to a path-fraction at run time (buffer / pathLen) and combined with PathStartT, so the buffer
    // holds regardless of CabinDistance (a t alone would shrink if the cabin moved). The player lands in a
    // clear pocket and walks INTO the gauntlet rather than being dropped into the firefight.
    const float SpawnBufferMeters = 18f; // metres of clear ground in front of the player spawn (no guards)

    const float LateralBase    = 6f;     // metres: base half-width of the corridor (left/right of path)
    const float LateralGrow    = 0f;     // metres: NO flare toward the cabin (was 9, which ballooned into a
                                         // defensive cordon, a wall, at the door). Corridor stays a constant width.
    const float LateralJitter  = 3f;     // metres: small random nudge so the rows never look mechanical
    const float AlongJitter    = 5f;     // metres: small random nudge along the path so they don't form a grid

    // Min separation between any two placed guards (a real ClearSpawn-style de-overlap, no NavMesh). Raised and
    // now pushes ALONG the path as well as sideways, so guards decluster lengthwise instead of widening the wall.
    const float SeparationMin  = 7.0f;   // metres; was 4.0. Real minimum gap so they are never clumped
    const int   SeparationTries = 12;    // attempts to nudge a guard off a neighbour before accepting; was 6

    // ---- HARD per-spawn clearance (Ask 3): keep NO guard embedded in a rock/tree ---------------------
    // Mirrors NetworkedWorld.Overlaps / ClearSpawn (the proven approach, no NavMesh in this project). Probes a
    // body CAPSULE (not a single waist sphere, which slips past thin trunks) against all solid geometry, and
    // ring-searches outward to the nearest clear ground if embedded. If the ring fails, the spawn is RE-ROLLED
    // to a fresh point on the path (a different t/lateral) and re-cleared, instead of accepting the embedded
    // fallback the old code did, so NO guard ends up inside a rock the player cannot shoot.
    const float ClearRadius     = 0.55f; // body radius probed for solid overlap (~a person); was 0.45, widened
    const float ClearProbeLow   = 0.1f;  // capsule bottom above the ground point (ankle); was 0.35, lowered to
                                         // catch low/wide boulders the knee-height probe slipped over
    const float ClearProbeHigh  = 1.9f;  // capsule top above the ground point (head); was 1.7
    const int   ClearRingDirs   = 12;    // ring-search directions per radius (was 8); finer = more clear cells
    const int   RerollTries     = 8;     // fresh path points to try if the local ring cannot clear a guard

    // RENDERER-BOUNDS rock clearance radius (the ProBuilder rock colliders are gone, so physics cannot feel a
    // visible rock; RockClearance.ClearOfRocks pushes a guard out of nearby big rock RENDERER bounds). A guard
    // footprint plus a small margin so the body reads fully clear of the boulder, not just its centre.
    const float GuardRockClearRadius = 0.8f;

    // ---- fallback annulus (only used if CabinPosition never resolves) --------------------------------
    const float MinRadius      = 25f;    // metres: nearest a scattered body may spawn from the player
    const float MaxRadius      = 110f;   // metres: farthest a scattered body may spawn from the player

    // Reachable terrain box (world XZ), conservative inner margin off the research-confirmed bounds
    // (terrain world min ~(-402.7, -439.5), ~500 m span -> max ~(+97, +60)). Any spawned XZ is clamped
    // into this box so every bodyguard lands on walkable terrain.
    const float ReachMinX      = -395f;
    const float ReachMaxX      =   90f;
    const float ReachMinZ      = -432f;
    const float ReachMaxZ      =   55f;

    const float PollInterval   = 0.5f;   // seconds between readiness polls while waiting
    const float CabinWaitMax   = 12f;    // seconds to wait for RescueMission.CabinPosition before falling back

    bool spawned;
    float pollTimer;
    float waitedForCabin;                // accumulated seconds spent waiting for CabinPosition

    // Placed XZ positions so far this run (for the lightweight separation pass). Sized to the count.
    Vector3[] placedXZ;
    int placedXZCount;

    // DETERMINISTIC layout (co-op parity): the jitter/re-roll RNG is seeded off the shared spawn + cabin so
    // every client computes the SAME guard layout. Cabin/landing are deterministic across clients (RescueMission
    // exposes the same statics everywhere), so seeding off them makes the per-guard offsets match too. Without
    // this, Random.Range would diverge per client and each player would see different guard positions.
    static int LayoutSeed(Vector3 spawn, Vector3 cabin)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(spawn.x * 4f);
            h = h * 31 + Mathf.RoundToInt(spawn.z * 4f);
            h = h * 31 + Mathf.RoundToInt(cabin.x * 4f);
            h = h * 31 + Mathf.RoundToInt(cabin.z * 4f);
            h = h * 31 + BodyguardCount;
            return h;
        }
    }

    // ---- self-bootstrap (no scene wiring / no NetworkedWorld edit) -----------------------------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("ExpeditionPopulation");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<ExpeditionPopulation>();
    }

    void Update()
    {
        if (spawned) return;

        pollTimer += Time.deltaTime;
        if (pollTimer < PollInterval) return;
        pollTimer = 0f;

        if (!NetworkedWorld.GameplayActive) return;

        var player = FindFirstObjectByType<LocalPlayer>();
        if (player == null) return;

        // Gate on the cabin being placed so the gauntlet can be strung spawn->cabin. The mission sets
        // RescueMission.CabinPosition once it has placed the cabin; until then it reads as Vector3.zero.
        // Wait up to CabinWaitMax, then fall back to the annulus scatter so the world is never empty.
        Vector3 cabin = SafeCabinPosition();
        bool cabinReady = cabin.sqrMagnitude > 1e-3f;
        if (!cabinReady)
        {
            waitedForCabin += PollInterval;
            if (waitedForCabin < CabinWaitMax) return;   // keep polling for the cabin
            Debug.LogWarning($"[ExpeditionPopulation] RescueMission.CabinPosition not set after " +
                             $"{CabinWaitMax:0}s; falling back to annulus scatter around the player.");
        }

        spawned = true;
        if (cabinReady) PopulateGauntlet(player.transform.position, cabin);
        else            PopulateAnnulus(player.transform.position);
        enabled = false; // job done; stop polling
    }

    // Read RescueMission.CabinPosition defensively. The mission agent owns that static; this never throws
    // even if a reload leaves the type in an odd state.
    static Vector3 SafeCabinPosition()
    {
        try { return RescueMission.CabinPosition; }
        catch { return Vector3.zero; }
    }

    // ---- primary layout: the gauntlet (spawn -> cabin) ----------------------------------------------

    void PopulateGauntlet(Vector3 spawn, Vector3 cabin)
    {
        placedXZ = new Vector3[BodyguardCount];
        placedXZCount = 0;

        // DETERMINISTIC: seed the jitter/re-roll RNG off the shared spawn + cabin so every client lays out the
        // SAME gauntlet. Restore the global RNG state afterwards so we do not perturb anyone else's randomness.
        Random.State prevRng = Random.state;
        Random.InitState(LayoutSeed(spawn, cabin));

        // Flat path from spawn to the cabin and its left/right perpendicular (for the corridor spread).
        Vector3 path = cabin - spawn; path.y = 0f;
        float pathLen = path.magnitude;
        Vector3 pathDir = pathLen > 1e-4f ? path / pathLen : Vector3.forward;
        Vector3 perp = Vector3.Cross(pathDir, Vector3.up);   // unit, perpendicular in the XZ plane
        if (perp.sqrMagnitude < 1e-4f) perp = Vector3.right;
        perp.Normalize();

        // Do not let any guard sit inside the cabin clear zone, even after the t-bias. Convert the clear
        // radius to a max t so the last guards still stop short of the door.
        float clearT = pathLen > 1e-4f ? Mathf.Clamp01(1f - (CabinClearRadius / pathLen)) : PathEndT;
        float endT = Mathf.Min(PathEndT, clearT);

        // SPAWN BUFFER (Ask 2): the first guard must sit AT LEAST SpawnBufferMeters out from the spawn, so the
        // player lands in a clear pocket. Convert the absolute buffer to a path-fraction (independent of
        // CabinDistance) and take the larger of that and PathStartT, then guard against a very short path.
        float bufferT = pathLen > 1e-4f ? Mathf.Clamp01(SpawnBufferMeters / pathLen) : PathStartT;
        float startT = Mathf.Min(Mathf.Max(PathStartT, bufferT), endT);

        int placed = 0;
        int embedded = 0;   // count of guards the clearance could not fully de-embed (for the demo log)
        for (int i = 0; i < BodyguardCount; i++)
        {
            // Build a candidate XZ for guard i at a given path fraction. Used for the first attempt AND for the
            // re-roll path inside the hard clearance (so a boxed-in guard moves to a fresh stretch of corridor).
            Vector3 firstXZ = BuildGuardXZ(i, spawn, pathDir, perp, pathLen, startT, endT, /*rerollOffset*/ 0);

            firstXZ = ClampToReachable(firstXZ);
            firstXZ = SeparateFromPlaced(firstXZ, pathDir, perp);   // real de-overlap (along + sideways)

            Vector3 pos = GroundSnap(firstXZ);
            // HARD clearance (Ask 3): if the body is inside a rock/tree, ring-search out; if that fails,
            // RE-ROLL to a fresh point on the path and clear again so NO guard ends up unshootable in geometry.
            bool cleared;
            pos = ClearOrReroll(i, pos, spawn, pathDir, perp, pathLen, startT, endT, out cleared);
            // RENDERER-BOUNDS rock pass: the ProBuilder rock COLLIDERS are removed, so the physics OverlapCapsule
            // clearance above can no longer feel a visible rock. Route the cleared point through the shared
            // RockClearance.ClearOfRocks (renderer-bounds, the only way to detect rocks now), then re-ground-snap,
            // so no guard lands inside a cosmetic boulder. Same helper + order used by the player/NPC/Curator spawns.
            pos = GroundSnap(RockClearance.ClearOfRocks(pos, GuardRockClearRadius));
            if (!cleared) embedded++;
            RecordPlaced(pos);   // record the FINAL cleared XZ so separation accounts for the nudge

            // Face roughly back down the path (toward the incoming player) so the cordon reads as defending
            // the route, not staring into the trees. A small yaw jitter keeps it natural.
            Vector3 facing = -pathDir;
            Quaternion rot = facing.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(facing, Vector3.up) * Quaternion.Euler(0f, Random.Range(-25f, 25f), 0f)
                : Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            if (SpawnBodyguard(i, pos, rot)) placed++;
        }

        // Restore the caller's RNG so the deterministic seed does not leak into the rest of the game.
        Random.state = prevRng;

        Debug.Log($"[ExpeditionPopulation] Strung {placed}/{BodyguardCount} bodyguards as a GAUNTLET along " +
                  $"spawn {spawn} -> cabin {cabin} (len {pathLen:0} m, spawn buffer {SpawnBufferMeters:0} m, " +
                  $"t {startT:0.00}..{endT:0.00}, density bias {DensityBias} (even/thin at cabin), corridor +/- " +
                  $"{LateralBase:0} m flat, min sep {SeparationMin:0} m, cabin clear zone {CabinClearRadius:0} m, " +
                  $"hard rock/tree clearance + re-roll ON, {embedded} could not fully clear). Deterministic " +
                  $"seed {LayoutSeed(spawn, cabin)} (co-op parity). Sister owned by RescueMission.");
    }

    // Build a candidate XZ for guard i. 'rerollOffset' shifts the guard to a different stretch of the corridor
    // when the local clearance ring fails (so a boxed-in guard relocates instead of embedding in a rock).
    Vector3 BuildGuardXZ(int i, Vector3 spawn, Vector3 pathDir, Vector3 perp, float pathLen,
                         float startT, float endT, int rerollOffset)
    {
        // Even base fraction 0..1 across the squad, then density-biased. DensityBias < 1 spreads guards EVENLY
        // and slightly thins the cabin end (no back-half packing). On a re-roll, walk the fraction along by a
        // deterministic step so the guard lands on a fresh, hopefully clear, stretch of corridor.
        float baseFrac = BodyguardCount > 1 ? i / (float)(BodyguardCount - 1) : 0.5f;
        if (rerollOffset != 0)
        {
            float step = 1f / Mathf.Max(2, BodyguardCount);
            baseFrac = Mathf.Repeat(baseFrac + rerollOffset * step * 0.5f, 1f);
        }
        float biased = Mathf.Pow(baseFrac, 1f / DensityBias);
        float t = Mathf.Lerp(startT, endT, biased);

        // Position along the path, with a small along-path jitter so two adjacent guards do not line up.
        float alongJit = Random.Range(-AlongJitter, AlongJitter);
        Vector3 onPath = spawn + pathDir * (t * pathLen + alongJit);

        // Lateral spread: alternate left/right by a CONSTANT-width corridor (LateralGrow is 0 now, no flare).
        // On a re-roll, flip the side so the guard tries the opposite lane.
        float side = ((i + rerollOffset) % 2 == 0) ? 1f : -1f;
        float halfWidth = LateralBase + LateralGrow * t;
        float lateral = side * Random.Range(halfWidth * 0.35f, halfWidth) + Random.Range(-LateralJitter, LateralJitter);
        return onPath + perp * lateral;
    }

    // Run the hard clearance; if the local ring cannot de-embed the guard, RE-ROLL it to a fresh path point and
    // clear again, up to RerollTries times. Sets 'cleared' true if the final point is out of all geometry.
    Vector3 ClearOrReroll(int i, Vector3 pos, Vector3 spawn, Vector3 pathDir, Vector3 perp, float pathLen,
                          float startT, float endT, out bool cleared)
    {
        Vector3 ringed = ClearOfObstacles(pos);
        if (!OverlapsObstacle(ringed)) { cleared = true; return ringed; }

        for (int r = 1; r <= RerollTries; r++)
        {
            Vector3 xz = BuildGuardXZ(i, spawn, pathDir, perp, pathLen, startT, endT, r);
            xz = ClampToReachable(xz);
            xz = SeparateFromPlaced(xz, pathDir, perp);
            Vector3 snapped = GroundSnap(xz);
            Vector3 cand = ClearOfObstacles(snapped);
            if (!OverlapsObstacle(cand)) { cleared = true; return cand; }
        }

        cleared = false;   // every attempt was boxed in (very dense rock field); accept the best ringed point
        return ringed;
    }

    // ---- fallback layout: the old annulus scatter (only if the cabin never resolved) ----------------

    void PopulateAnnulus(Vector3 center)
    {
        placedXZ = new Vector3[BodyguardCount];
        placedXZCount = 0;

        // DETERMINISTIC: seed off the player center so both clients scatter identically (co-op parity).
        Random.State prevRng = Random.state;
        Random.InitState(LayoutSeed(center, center));

        int placed = 0;
        for (int i = 0; i < BodyguardCount; i++)
        {
            Vector3 xz = ScatterPoint(center, i, BodyguardCount);
            Vector3 pos = GroundSnap(xz);
            // HARD clearance (Ask 3): ring-search out of any rock/tree. The annulus is mostly open ground, so
            // the ring almost always clears; no path re-roll here (there is no path), the ring is enough.
            pos = ClearOfObstacles(pos);
            // RENDERER-BOUNDS rock pass (same reason as the gauntlet): with the rock colliders gone, the physics
            // clearance cannot feel a visible rock; nudge out of any big rock RENDERER bounds, then re-ground-snap.
            pos = GroundSnap(RockClearance.ClearOfRocks(pos, GuardRockClearRadius));
            Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            RecordPlaced(pos);
            if (SpawnBodyguard(i, pos, rot)) placed++;
        }

        Random.state = prevRng;

        Debug.Log($"[ExpeditionPopulation] FALLBACK: placed {placed}/{BodyguardCount} bodyguards in the old " +
                  $"annulus ({MinRadius}-{MaxRadius} m) around {center} (no CabinPosition).");
    }

    // ---- spawn (unchanged combat pipeline) ----------------------------------------------------------

    // Spawn one bodyguard with the EXACT combat pipeline (mirrors CombatSpawner.SpawnCombatant for the
    // Bodyguard faction). Returns true if a model was placed.
    bool SpawnBodyguard(int variant, Vector3 pos, Quaternion rot)
    {
        // BUILD-SAFE resolver: Resources primary, editor AssetDatabase fallback. Cycles the 4 FBX by index.
        GameObject prefab = BodyguardModels.ResolvePrefab(variant);
        if (prefab == null)
        {
            Debug.LogWarning($"[ExpeditionPopulation] No bodyguard model for variant {variant}; skipping.");
            return false;
        }

        var go = Object.Instantiate(prefab, pos, rot);
        if (go == null) return false;
        go.name = $"Bodyguard_{variant:00}";

        // HDRP material fix so the bodyguard doesn't render solid white in the HDRP forest.
        BodyguardModels.FixHdrp(go);

        // Body-fitted collider + Animator wired to the shared Locomotion+Combat controller (the humanoid
        // avatar is assigned inside CharacterRig.WireAnimator; that is the T-pose fix the rig owner handles).
        CharacterRig.Apply(go, false);

        // Damage receiver on the SAME root the collider sits on (pure green, no shield. AI rules).
        var health = go.GetComponent<Health>();
        if (health == null) health = go.AddComponent<Health>();
        health.maxHealth = 100f;
        health.maxShield = 0f;
        health.faction = Faction.Bodyguard;

        // Mount the AK (gripped to the right-hand bone, re-pinned each LateUpdate by WeaponMount) + the Weapon.
        var weapon = WeaponRig.Equip(go);

        // The combat brain: acquires + faces + advances + fires on enemy factions, drives Armed/Fire/Dead.
        var ai = go.GetComponent<CombatAI>();
        if (ai == null) ai = go.AddComponent<CombatAI>();
        ai.Init(Faction.Bodyguard, weapon, health);

        return true;
    }

    // ---- separation (light, no NavMesh) -------------------------------------------------------------

    // Nudge an XZ off any already-placed guard so no two are clumped. Pushes BOTH along the path ('pathDir', so
    // guards decluster lengthwise) AND sideways ('perp', so they keep their lane), away from the nearest
    // neighbour, retrying SeparationTries times. Best-effort; if it cannot fully separate it accepts the last
    // position (build-safe, never loops). The along-path push is what kills the old sideways-only wall widening.
    Vector3 SeparateFromPlaced(Vector3 xz, Vector3 pathDir, Vector3 perp)
    {
        for (int attempt = 0; attempt < SeparationTries; attempt++)
        {
            int hitIndex = -1;
            for (int j = 0; j < placedXZCount; j++)
            {
                float dx = xz.x - placedXZ[j].x, dz = xz.z - placedXZ[j].z;
                if (dx * dx + dz * dz < SeparationMin * SeparationMin) { hitIndex = j; break; }
            }
            if (hitIndex < 0) return xz;   // clear of everyone

            // Vector from the colliding neighbour to us, decomposed onto the path and the perpendicular. Push
            // out along WHICHEVER axis we are already leaning, so we move directly apart, not just sideways.
            float ddx = xz.x - placedXZ[hitIndex].x, ddz = xz.z - placedXZ[hitIndex].z;
            float alongComp = ddx * pathDir.x + ddz * pathDir.z;
            float sideComp  = ddx * perp.x    + ddz * perp.z;
            float alongSign = alongComp >= 0f ? 1f : -1f;
            float sideSign  = sideComp  >= 0f ? 1f : -1f;
            // Bias toward the path axis (declustering lengthwise) while still spreading the lane a little.
            xz += pathDir * (alongSign * SeparationMin * 0.7f);
            xz += perp    * (sideSign  * SeparationMin * 0.5f);
            xz = ClampToReachable(xz);
        }
        return xz;
    }

    void RecordPlaced(Vector3 xz)
    {
        if (placedXZ == null) return;
        if (placedXZCount >= placedXZ.Length) return;
        placedXZ[placedXZCount++] = xz;
    }

    // ---- HARD rock/tree clearance (Ask 3, no NavMesh) -----------------------------------------------

    // Nudge a ground-snapped point off any rock/tree it is embedded in. Mirrors NetworkedWorld.ClearSpawn:
    // if the body capsule is clear, return as-is; otherwise ring-search outward (ClearRingDirs directions x
    // rising radius) for the nearest clear ground point, re-ground-snapping and re-clamping each candidate.
    // Best-effort and build-safe: if fully boxed in, returns the original point (the caller, ClearOrReroll,
    // then relocates the guard to a fresh path point rather than accepting an embedded body).
    static Vector3 ClearOfObstacles(Vector3 pos)
    {
        if (!OverlapsObstacle(pos)) return pos;   // already clear

        float[] radii = { 1f, 1.6f, 2.4f, 3.4f, 4.6f, 6f, 8f };
        for (int ri = 0; ri < radii.Length; ri++)
        {
            float r = radii[ri];
            for (int i = 0; i < ClearRingDirs; i++)
            {
                float ang = (Mathf.PI * 2f) * (i / (float)ClearRingDirs);
                float cx = pos.x + Mathf.Cos(ang) * r;
                float cz = pos.z + Mathf.Sin(ang) * r;
                Vector3 cand = ClampToReachable(new Vector3(cx, pos.y, cz));
                cand = GroundSnap(cand);          // sit the candidate on its own local ground
                if (!OverlapsObstacle(cand)) return cand;
            }
        }
        return pos;   // boxed in: build-safe fallback (caller re-rolls to a fresh path point)
    }

    // True if a BODY CAPSULE at this point overlaps a NON-terrain solid collider (a rock/tree). Probes a full
    // knee->head capsule (a single waist sphere slips past thin trunks), filters out terrain, character
    // controllers, and our own combat bodies (so guards do not treat each other / the player as obstacles).
    static bool OverlapsObstacle(Vector3 pos)
    {
        Vector3 bottom = pos + Vector3.up * ClearProbeLow;
        Vector3 top    = pos + Vector3.up * ClearProbeHigh;
        var hits = Physics.OverlapCapsule(bottom, top, ClearRadius, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null) return false;
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h == null) continue;
            if (h is TerrainCollider) continue;                     // the ground is not a rock
            if (h is CharacterController) continue;                 // not an obstacle
            // Ignore the bodies WE spawn (guards/player/NPCs) so valid bodies near each other do not fight.
            if (h.GetComponentInParent<Health>() != null) continue;
            if (h.GetComponentInParent<CombatAI>() != null) continue;
            if (h.GetComponentInParent<LocalPlayer>() != null) continue;
            if (h.GetComponentInParent<NpcAgent>() != null) continue;
            return true;   // embedded in solid geometry (rock/boulder/tree) -> needs a nudge
        }
        return false;
    }

    // ---- fallback annulus scatter (golden-angle, unchanged) -----------------------------------------

    static Vector3 ScatterPoint(Vector3 center, int i, int count)
    {
        const float golden = 2.39996323f; // golden angle in radians (~137.5 deg)
        float angle = i * golden + Random.Range(-0.15f, 0.15f);

        float t = count > 1 ? (i + 0.5f) / count : 0.5f;
        float radius = Mathf.Lerp(MinRadius, MaxRadius, Mathf.Sqrt(t));
        radius += Random.Range(-6f, 6f);
        radius = Mathf.Clamp(radius, MinRadius, MaxRadius);

        Vector3 p = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        return ClampToReachable(p);
    }

    // ---- shared helpers -----------------------------------------------------------------------------

    // Hard backstop: keep the XZ inside the research-confirmed reachable terrain box so no spawn lands off
    // the map (Y is set later by GroundSnap).
    static Vector3 ClampToReachable(Vector3 p)
    {
        p.x = Mathf.Clamp(p.x, ReachMinX, ReachMaxX);
        p.z = Mathf.Clamp(p.z, ReachMinZ, ReachMaxZ);
        return p;
    }

    // Raycast DOWN onto the terrain so bodies spawn ON the ground (lowest hit = ground, not tree canopy),
    // mirroring NetworkedWorld.V / CombatSpawner.GroundSnap. Falls back to the requested point if nothing hit.
    static Vector3 GroundSnap(Vector3 pos)
    {
        var hits = Physics.RaycastAll(new Vector3(pos.x, pos.y + 400f, pos.z), Vector3.down, 3000f,
            ~0, QueryTriggerInteraction.Ignore);
        if (hits != null && hits.Length > 0)
        {
            float groundY = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].point.y < groundY) groundY = hits[i].point.y;
            if (groundY < float.MaxValue) return new Vector3(pos.x, groundY + 0.05f, pos.z);
        }
        return pos;
    }
}
