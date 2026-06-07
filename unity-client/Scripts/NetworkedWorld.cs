// NetworkedWorld.cs. spawns + syncs GameObjects from SpacetimeDB tables (Lost Expedition).
//
// Players and NPCs each get a GameObject. REMOTE players + NPCs interpolate to the server position.
// The LOCAL player is driven by its own CharacterController (LocalPlayer); we do NOT lerp it, so
// real collision/gravity isn't fought by the network layer. Each NPC gets an NpcAgent that reacts
// to the LLM director's writes.

using System.Collections.Generic;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class NetworkedWorld : MonoBehaviour
{
    [Header("Prefabs (optional, capsule placeholders used if empty)")]
    public GameObject playerPrefab;
    public GameObject npcPrefab;

    [Header("Tuning")]
    public float lerpSpeed = 14f;

    [Tooltip("Shift all spawns to where the forest actually is (set to the forest ground position)")]
    public Vector3 worldOffset;
    public static Vector3 WorldOffset; // mirror so LocalPlayer can convert world<->server coords

    [Header("Spawn clearance (keep bodies out of rocks)")]
    [Tooltip("Body radius probed for solid overlap at the spawn point. 0.4 ~= a person.")]
    public float clearRadius = 0.4f;
    [Tooltip("Vertical center of the overlap probe above the ground point (waist height).")]
    public float clearProbeHeight = 0.9f;
    [Tooltip("Layers treated as the WALKABLE terrain (NOT an obstacle). Everything else is a solid rock/obstacle.")]
    public LayerMask terrainMask = ~0;

    // ---- CHAT-NPC CAMP (Ask 1) ----------------------------------------------------------------------
    // The clue NPCs (Tomas, Sela, and the rest) are SERVER-seeded; their server Position drops them right in
    // the spawn->cabin gauntlet, and a non-zero server Y floats them (V() adds the server Y on top of the
    // ground, so Tomas hovers). Rather than republish the server, we relocate the chat NPCs CLIENT-SIDE into
    // a small, visible, reachable CAMP set OFF to the side of the gauntlet corridor. The camp anchor derives
    // ONLY from RescueMission.LandingPosition (= the shared server spawn) and RescueMission.CabinPosition
    // (the shared far end), both of which every client computes identically, so the camp is DETERMINISTIC
    // (same world position for all clients, Ask 6). Each NPC's slot is keyed off its stable ulong NpcId so a
    // given NPC lands in the SAME camp slot on every client. The slot is HARD ground-snapped (server Y
    // discarded, killing the float) and ClearSpawn-cleared (no rock/tree). The camp position is then PINNED
    // against UpdateNpc's per-tick re-lerp so the server position never drags the NPC back into the firefight.
    [Tooltip("Camp distance OFF to the side of the spawn->cabin corridor (perpendicular). >> the corridor half-width so the camp is clearly off the gauntlet.")]
    public float campLateral = 34f;
    [Tooltip("Camp distance ALONG the spawn->cabin path from the spawn (a short walk from the player, still near the start so it is easy to find).")]
    public float campAlong = 10f;
    [Tooltip("Radius of the NPC cluster within the camp (so they read as a small camp and stay reachable).")]
    public float campClusterRadius = 4f;

    // Reachable terrain box (world XZ). Mirrors ExpeditionPopulation's research-confirmed bounds so the camp
    // can never be clamped off the map if the perpendicular side points toward an edge.
    const float ReachMinX = -395f, ReachMaxX = 90f, ReachMinZ = -432f, ReachMaxZ = 55f;

    // GATE (PART A): false throughout Connecting/Auth/Lobby; flipped TRUE exactly once in
    // LobbyBootstrap.Launch(). Wire() still runs + ARMS the OnInsert/OnUpdate/OnDelete callbacks during
    // the lobby (do NOT gate Wire, else the async JoinForest->RegisterPlayer local Player row would
    // NEVER spawn); only the SpawnPlayer/SpawnNpc BODIES early-return while this is false, so the armed
    // callbacks + the back-fill loop no-op until gameplay begins.
    public static bool GameplayActive = false;

    class Synced { public GameObject go; public Vector3 target; public NpcAgent agent; public bool isLocal; public bool camped; }

    readonly Dictionary<string, Synced> players = new();
    readonly Dictionary<ulong, Synced> npcs = new();

    void OnEnable() { WorldOffset = worldOffset; GameManager.OnReady += Wire; }
    void OnDisable() => GameManager.OnReady -= Wire;

    bool wired;
    void Wire()
    {
        if (wired) return;   // idempotent: OnReady can re-invoke late subscribers; subscribe Player/Npc once
        wired = true;
        WorldOffset = worldOffset;

        // Remove the ProbuilderCollisions barrier at runtime so a recompile alone applies it (no scene reload
        // needed). This MeshCollider doubled as the map wall AND the rock/cliff collision; with it gone the
        // player free-roams on the terrain and rocks become cosmetic. Runs here, before any spawning, so the
        // RockClearance renderer-bounds checks (not physics) handle rock avoidance from the first spawn.
        DisableProbuilderCollisions();

        var db = GameManager.Conn.Db;

        db.Player.OnInsert += (ctx, p) => SpawnPlayer(p);
        db.Player.OnUpdate += (ctx, _old, p) => UpdatePlayer(p);
        db.Player.OnDelete += (ctx, p) => Remove(players, Key(p.Identity));

        db.Npc.OnInsert += (ctx, n) => SpawnNpc(n);
        db.Npc.OnUpdate += (ctx, _old, n) => UpdateNpc(n);
        db.Npc.OnDelete += (ctx, n) => RemoveNpc(n.NpcId);

        // NOTE: the back-fill Iter loops moved into SpawnExisting(); they would have spawned forest
        // rows during the lobby. LobbyBootstrap.Launch() flips GameplayActive=true, then JoinForest(),
        // then SpawnExisting() to back-fill existing NPC/remote-player rows. The just-registered LOCAL
        // player arrives later via the now-LIVE armed Player.OnInsert.
    }

    // Disable the ProbuilderCollisions MeshCollider barrier at runtime. The scene file flip only takes effect
    // on a scene reload; this makes a recompile alone enough. We SetActive(false) the GameObject (matches the
    // "GONE" intent), and also disable any MeshCollider named ProbuilderCollisions as a belt-and-suspenders
    // step. We look it up via Resources.FindObjectsOfTypeAll so an already-inactive instance is still found
    // (GameObject.Find skips inactive objects). Pure runtime API, null-safe, never throws, idempotent.
    static bool probuilderDisabled;
    static void DisableProbuilderCollisions()
    {
        if (probuilderDisabled) return;
        try
        {
            var go = GameObject.Find("ProbuilderCollisions");
            if (go != null) go.SetActive(false);

            // Fallback / belt-and-suspenders: disable the MeshCollider itself even if the GameObject lookup
            // missed (e.g. it was already inactive, so Find skipped it). Resources.FindObjectsOfTypeAll
            // includes inactive objects.
            var colliders = Resources.FindObjectsOfTypeAll<MeshCollider>();
            if (colliders != null)
            {
                foreach (var mc in colliders)
                {
                    if (mc == null) continue;
                    if (mc.gameObject != null && mc.gameObject.name == "ProbuilderCollisions")
                        mc.enabled = false;
                }
            }
            probuilderDisabled = true;
            Debug.Log("[NetworkedWorld] ProbuilderCollisions barrier disabled at runtime. Terrain remains the solid ground; rocks are cosmetic (RockClearance handles spawn avoidance).");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[NetworkedWorld] DisableProbuilderCollisions failed (non-fatal): " + e.Message);
        }
    }

    // Back-fill rows that existed BEFORE gameplay began (forest NPCs + already-present remote players).
    // Called by LobbyBootstrap.Launch() AFTER GameplayActive is set true.
    public void SpawnExisting()
    {
        var db = GameManager.Conn.Db;
        // Back-fill REMOTE players only. The LOCAL body is owned solely by the armed Player.OnInsert
        // (Wire, line ~58) so the just-registered local row never gets two competing spawn paths.
        foreach (var p in db.Player.Iter()) { if (GameManager.IsLocal(p.Identity)) continue; SpawnPlayer(p); }
        foreach (var n in db.Npc.Iter()) SpawnNpc(n);
    }

    static string Key(Identity id) => id.ToString();

    // Apply the world offset, then raycast DOWN onto the terrain (lowest hit = ground, not a tree).
    // Server y becomes "height above ground". Used for spawn placement + remote interpolation.
    Vector3 V(float x, float y, float z)
    {
        float wx = x + worldOffset.x, wz = z + worldOffset.z;
        return new Vector3(wx, GroundY(wx, wz) + y, wz);
    }

    // Lowest downward-raycast hit = the floor under (wx,wz). Falls back to worldOffset.y if nothing hit.
    float GroundY(float wx, float wz)
    {
        float groundY = worldOffset.y;
        var hits = Physics.RaycastAll(new Vector3(wx, worldOffset.y + 400f, wz), Vector3.down, 3000f);
        if (hits.Length > 0)
        {
            groundY = float.MaxValue;
            foreach (var h in hits) if (h.point.y < groundY) groundY = h.point.y;
        }
        return groundY;
    }

    // SPAWN-CLEARANCE. After a body is placed via V(), check whether its capsule would be embedded in a
    // rock / boulder / crate collider. If so, ring-search outward (deterministic, no NavMesh, no physics
    // solver) for the nearest ground point whose waist-height sphere is clear of solid geometry, and
    // return that. If fully boxed in, returns the original point unchanged (never throws). The terrain
    // itself is ignored via terrainMask so standing ON the ground is not treated as an overlap.
    Vector3 ClearSpawn(Vector3 ground)
    {
        if (clearRadius <= 0f) return ground;
        if (!Overlaps(ground)) return ground;   // already clear

        // Concentric rings: 8 directions x rising radius. First clear hit wins (nearest preferred).
        const int dirs = 8;
        float[] radii = { 1f, 1.6f, 2.4f, 3.4f, 4.6f, 6f };
        foreach (float r in radii)
        {
            for (int i = 0; i < dirs; i++)
            {
                float ang = (Mathf.PI * 2f) * (i / (float)dirs);
                float cx = ground.x + Mathf.Cos(ang) * r;
                float cz = ground.z + Mathf.Sin(ang) * r;
                // keep the body's original "height above ground" offset relative to the new floor
                float heightAboveGround = ground.y - GroundY(ground.x, ground.z);
                var cand = new Vector3(cx, GroundY(cx, cz) + heightAboveGround, cz);
                if (!Overlaps(cand)) return cand;
            }
        }

        Debug.LogWarning($"[NetworkedWorld] ClearSpawn: no clear ground near {ground}; spawning at original point.");
        return ground;   // build-safe fallback: never throw, never drop the body
    }

    // True if a BODY CAPSULE at this ground point overlaps a NON-terrain (solid) collider. We probe a full
    // body capsule (knee -> head) rather than a single waist sphere, because a single waist sphere slips past
    // thin tree trunks / narrow rock edges (the center happens to miss them), leaving an NPC clipping the
    // geometry. The capsule catches a trunk the waist sphere would miss. ALL layers are probed, then filtered
    // by collider TYPE: terrain is the ground you stand on (never an obstacle), CharacterControllers and our
    // own spawned bodies (players/NPCs) are not rocks. Any other solid overlap means "embedded".
    bool Overlaps(Vector3 ground)
    {
        Vector3 bottom = ground + Vector3.up * Mathf.Min(0.35f, clearProbeHeight);
        Vector3 top    = ground + Vector3.up * Mathf.Max(1.7f, clearProbeHeight);
        var hits = Physics.OverlapCapsule(bottom, top, clearRadius, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null) return false;
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h is TerrainCollider) continue;                        // never treat terrain as a rock
            if (h is CharacterController) continue;                     // not an obstacle
            // Ignore the bodies WE spawn (players/NPCs) so two valid spawns near each other do not fight.
            if (h.GetComponentInParent<LocalPlayer>() != null) continue;
            if (h.GetComponentInParent<NpcAgent>() != null) continue;
            return true;   // embedded in solid geometry (rock/boulder/tree) -> needs a nudge
        }
        return false;
    }

    // ---- CHAT-NPC CAMP placement (Ask 1) ------------------------------------------------------------

    // True once the mission has published BOTH the spawn (LandingPosition) and the far end (CabinPosition),
    // so the deterministic camp anchor can be derived. Until then the chat NPCs use their raw server position
    // (they will be re-pinned to the camp on the first UpdateNpc tick after the mission resolves; see
    // UpdateNpc). Defensive: RescueMission owns these statics, so a try/catch keeps this build-safe.
    static bool CampReady(out Vector3 landing, out Vector3 cabin)
    {
        landing = Vector3.zero; cabin = Vector3.zero;
        try { landing = RescueMission.LandingPosition; cabin = RescueMission.CabinPosition; }
        catch { return false; }
        return landing.sqrMagnitude > 1e-3f && cabin.sqrMagnitude > 1e-3f;
    }

    // ---- SPREAD tunables (Ask 4) -------------------------------------------------------------------
    // The chat NPCs are no longer clustered in one camp; they are SPREAD to distinct deterministic anchors
    // scattered ACROSS the reachable terrain, all on the same (off-gauntlet) perpendicular side of the
    // spawn->cabin corridor. Each NPC gets its own (along, lateral) pair so they sit at clearly different
    // distances from the spawn AND different depths off the corridor, never inside the firefight corridor
    // and never out at the cabin/boss end.
    //
    // Number of distinct spread slots. Sized for the seven chat NPCs (Tomas, Sela, Greta, Dren, Old Cobb,
    // Pri, Mara); a prime keeps the id-hash modulo well distributed even if NpcIds are sequential.
    const int SpreadSlots = 7;
    // ALONG-axis: how far from the spawn the NEAREST spread slot sits, and the step between successive slots.
    // 18..(18+6*9)=72 m from Landing (a comfortable walk from spawn), capped well short of the cabin end so
    // no NPC lands at the cabin/sister/boss zone (the corridor is far longer than 72 m).
    const float SpreadAlongBase = 18f;
    const float SpreadAlongStep = 9f;
    // LATERAL: base depth off the corridor (>> the corridor half-width so every NPC is clearly off the
    // gauntlet) plus a per-slot side step so NPCs sit at three distinct depths (28, 40, 52 m).
    const float SpreadLateralBase = 28f;
    const float SpreadLateralStep = 12f;

    // The deterministic SPREAD slot for a chat NPC, keyed off its stable NpcId so every client agrees. The
    // NPCs are scattered across the reachable terrain on the OFF-gauntlet perpendicular side of the
    // spawn->cabin corridor: each gets a distinct (along, lateral) anchor so they are well separated (no
    // cluster), never in the firefight corridor, and never out at the cabin/boss end. The slot is HARD
    // ground-snapped (server Y discarded -> no float), clamped into the reachable box, then ClearSpawn +
    // RockClearance cleared (no rock/tree).
    Vector3 CampSlot(ulong npcId, Vector3 landing, Vector3 cabin)
    {
        Vector3 path = cabin - landing; path.y = 0f;
        float pathLen = path.magnitude;
        Vector3 pathDir = pathLen > 1e-4f ? path / pathLen : Vector3.forward;
        Vector3 perp = Vector3.Cross(pathDir, Vector3.up);
        if (perp.sqrMagnitude < 1e-4f) perp = Vector3.right;
        perp.Normalize();

        // Deterministic per-NPC slot index. Knuth multiplicative hash spreads even sequential NpcIds across
        // the SpreadSlots buckets far better than a raw low-bit mask, so the seven chat NPCs are unlikely to
        // collide into the same slot. (Same id -> same slot on every client, so the spread is shared.)
        uint slot = (uint)((npcId * 2654435761u) >> 16) % SpreadSlots;

        // Distinct (along, lateral) anchor per slot. Along marches up the corridor; lateral cycles through
        // three depths so successive NPCs are also offset sideways, giving a wide scatter rather than a line.
        float along = SpreadAlongBase + slot * SpreadAlongStep;
        // Never let along reach the cabin end: cap to ~75% of the corridor (or the fixed range, whichever is
        // smaller) so even a long corridor keeps the spread near the start half, away from cabin/sister/boss.
        float alongCap = pathLen > 1e-4f ? Mathf.Min(pathLen * 0.75f, SpreadAlongBase + (SpreadSlots - 1) * SpreadAlongStep)
                                         : SpreadAlongBase + (SpreadSlots - 1) * SpreadAlongStep;
        along = Mathf.Min(along, alongCap);
        float lateral = SpreadLateralBase + (slot % 3) * SpreadLateralStep;

        Vector3 anchor = landing + pathDir * along + perp * lateral;
        float cx = anchor.x;
        float cz = anchor.z;

        // Clamp into the reachable terrain box so the perpendicular side can never push a slot off the map.
        cx = Mathf.Clamp(cx, ReachMinX, ReachMaxX);
        cz = Mathf.Clamp(cz, ReachMinZ, ReachMaxZ);

        // HARD ground-snap: sit AT the terrain (tiny lift), DISCARDING the server Y. This is the float fix.
        Vector3 anchorSlot = new Vector3(cx, GroundY(cx, cz) + 0.05f, cz);
        // ClearSpawn dodges remaining colliders (trees/crate); RockClearance dodges the cosmetic rock renderers.
        return RockClearance.ClearOfRocks(ClearSpawn(anchorSlot), clearRadius);
    }

    GameObject Spawn(GameObject prefab, PrimitiveType fallback, Vector3 pos)
    {
        GameObject go;
        if (prefab != null) { go = Instantiate(prefab, pos, Quaternion.identity); }
        else { go = GameObject.CreatePrimitive(fallback); var c = go.GetComponent<Collider>(); if (c) Destroy(c); }
        go.transform.position = pos;
        return go;
    }

    // ---- players ----
    void SpawnPlayer(PlayerData p)
    {
        if (!GameplayActive) return;   // GATE (PART A): no player GameObjects during Connecting/Auth/Lobby
        var key = Key(p.Identity);
        // DEDUPE: a row for this identity already has (or is mid-creation of) a body. A concurrent second
        // SpawnPlayer for the same key sees ContainsKey == true here and bails to UpdatePlayer instead of
        // instantiating a second body. This is the early-out for the RESERVATION written below.
        if (players.ContainsKey(key)) { UpdatePlayer(p); return; }

        bool isLocal = GameManager.IsLocal(p.Identity);

        // DEDUPE (local backstop): the local body is owned by exactly ONE spawn. If a LocalPlayer already
        // exists (race: SpawnExisting back-fill AND the armed Player.OnInsert both firing for the local
        // row), never create a second visible local body / second camera / second AudioListener.
        if (isLocal && FindFirstObjectByType<LocalPlayer>() != null) return;

        // RESERVE the key BEFORE any Instantiate/await, closing the wide window that existed when the dict
        // was written only at the very end. go is back-filled once the body is built.
        var slot = new Synced { go = null, target = Vector3.zero, isLocal = isLocal };
        players[key] = slot;

        // Ground-snap, then NUDGE off any rock so no body spawns embedded in geometry. ClearSpawn still
        // dodges any remaining COLLIDERS (trees / crate); RockClearance then dodges the now-collider-less
        // visible rock RENDERERS (the ProbuilderCollisions barrier is gone, so physics can no longer feel them).
        var pos = RockClearance.ClearOfRocks(ClearSpawn(V(p.Position.X, p.Position.Y, p.Position.Z)), clearRadius);

        // Spawn the player's chosen survivalist MODEL (HDRP-fixed); capsule only as a last resort.
        // ResolvePrefab is BUILD-SAFE (Resources primary, editor AssetDatabase fallback), so the call is
        // unconditional. gating it on #if UNITY_EDITOR was the bug that left builds with capsules only.
        GameObject prefab = playerPrefab;
        if (prefab == null) prefab = SurvivalistModels.ResolvePrefab(p.CharacterClass);
        var go = Spawn(prefab, PrimitiveType.Capsule, pos);
        go.name = $"Player_{p.Username}";
        if (prefab != null)
        {
            SurvivalistModels.FixHdrp(go);   // build-safe: Resources HDRP set / HDRP/Lit re-shade fallback
        }
        else
        {
            Tint(go, new Color(0.55f, 0.75f, 1f)); // cool-blue capsule fallback only
        }

        // Body-fitted collision + locomotion Animator. Local player gets a CharacterController
        // (LocalPlayer drives it); remotes get a solid CapsuleCollider. Animator "Speed" is driven
        // automatically by CharacterRig's CharacterLocomotion. (Runtime-safe; controller load is
        // editor-only inside CharacterRig and falls back gracefully.)
        CharacterRig.Apply(go, isLocal);

        if (isLocal)
        {
            var lp = go.AddComponent<LocalPlayer>();
            lp.identity = p.Identity;
        }
        slot.go = go;
        slot.target = pos;
    }
    void UpdatePlayer(PlayerData p)
    {
        if (players.TryGetValue(Key(p.Identity), out var s)) { if (!s.isLocal) s.target = V(p.Position.X, p.Position.Y, p.Position.Z); }
        else SpawnPlayer(p);
    }

    // ---- NPCs ----
    void SpawnNpc(Npc n)
    {
        if (!GameplayActive) return;   // GATE (PART A): no forest NPCs (Mara/Eli/Brother Vael) in the lobby
        if (npcs.ContainsKey(n.NpcId)) { UpdateNpc(n); return; }

        // CHAT-NPC CAMP (Ask 1): the clue NPCs (Tomas, Sela, the rest) are server-seeded INTO the gauntlet
        // and float (server Y added on top of ground). Relocate them DETERMINISTICALLY into a side camp,
        // hard ground-snapped (no float) + cleared (no rock/tree). If the mission has not yet published the
        // camp anchor (LandingPosition/CabinPosition still zero), fall back to the raw server position this
        // frame; the first UpdateNpc tick after the mission resolves promotes them to the camp (see UpdateNpc).
        bool camped = CampReady(out Vector3 landing, out Vector3 cabin);
        Vector3 pos = camped
            ? CampSlot(n.NpcId, landing, cabin)   // CampSlot already routes through RockClearance
            : RockClearance.ClearOfRocks(ClearSpawn(V(n.Position.X, n.Position.Y, n.Position.Z)), clearRadius);

        // Resolve a real civilian MODEL (HDRP-fixed); capsule only as a last resort.
        // DisplayName drives the deterministic distinct-slot mapping for Mara/Eli/Brother Vael.
        // ResolvePrefab is BUILD-SAFE (Resources primary, editor AssetDatabase fallback) -> unconditional call.
        GameObject prefab = npcPrefab;
        if (prefab == null) prefab = NpcModels.ResolvePrefab(n.DisplayName);
        var go = Spawn(prefab, PrimitiveType.Capsule, pos);
        go.name = $"NPC_{n.DisplayName}";
        if (prefab != null)
        {
            NpcModels.FixHdrp(go);   // build-safe: Resources HDRP set / HDRP/Lit re-shade fallback
        }

        // Body-fitted CapsuleCollider + locomotion Animator (remote/NPC path: localControl = false).
        CharacterRig.Apply(go, false);

        var agent = go.AddComponent<NpcAgent>();
        // Same DisplayName key that drove ResolvePrefab -> deterministic gender that matches the model,
        // so the TTS voice always agrees with the body the player sees.
        agent.Init(n.NpcId, NpcModels.ResolveGender(n.DisplayName));
        npcs[n.NpcId] = new Synced { go = go, target = pos, agent = agent, camped = camped };
        // If the camp resolved at spawn time, snap the body straight to the camp slot (no lerp-in from the
        // raw server point), so the first frame is already correct on every client.
        if (camped && go != null) go.transform.position = pos;
        agent.Apply(n);
    }
    void UpdateNpc(Npc n)
    {
        if (!npcs.TryGetValue(n.NpcId, out var s)) { SpawnNpc(n); return; }

        // CHAT-NPC CAMP PIN (Ask 1): the camp position is AUTHORITATIVE for the stationary clue NPCs. We
        // recompute the deterministic camp slot every tick and ignore the incoming server position, so the
        // per-tick V() (which adds the server Y and would re-float / drag the NPC back into the gauntlet)
        // never wins. Dialogue/clue state still updates via agent.Apply below. If the camp is not yet ready,
        // we promote the NPC the moment it becomes ready (camped flips true once and the slot takes over).
        if (CampReady(out Vector3 landing, out Vector3 cabin))
        {
            s.camped = true;
            s.target = CampSlot(n.NpcId, landing, cabin);
        }
        else
        {
            s.target = V(n.Position.X, n.Position.Y, n.Position.Z);
        }
        if (s.agent != null) s.agent.Apply(n);
    }
    void RemoveNpc(ulong id)
    {
        if (npcs.TryGetValue(id, out var s)) { if (s.go) Destroy(s.go); npcs.Remove(id); }
    }

    static void Tint(GameObject go, Color c)
    {
        var rend = go.GetComponentInChildren<Renderer>();
        if (rend == null) return;
        var mat = rend.material;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
    }

    void Remove<TK>(Dictionary<TK, Synced> map, TK key)
    {
        if (map.TryGetValue(key, out var s)) { if (s.go) Destroy(s.go); map.Remove(key); }
    }

    void Update()
    {
        float t = Time.deltaTime * lerpSpeed;
        foreach (var s in players.Values) if (s.go && !s.isLocal) s.go.transform.position = Vector3.Lerp(s.go.transform.position, s.target, t);

        // CHAT-NPC CAMP PROMOTION (Ask 1): the clue NPCs are stationary, so the server may NEVER push an
        // UpdateNpc for them. If an NPC spawned before the mission resolved (camped == false) and the camp
        // anchor is now ready, promote it here so it still moves into the side camp even with no server tick.
        bool campReady = CampReady(out Vector3 landing, out Vector3 cabin);
        foreach (var kv in npcs)
        {
            var s = kv.Value;
            if (s.go == null) continue;
            if (campReady && !s.camped) { s.camped = true; s.target = CampSlot(kv.Key, landing, cabin); }
            s.go.transform.position = Vector3.Lerp(s.go.transform.position, s.target, t);
        }
    }
}
