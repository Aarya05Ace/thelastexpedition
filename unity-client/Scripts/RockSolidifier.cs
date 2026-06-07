// RockSolidifier.cs. Runtime collider bootstrapper for the Lost Expedition forest (Ask 1).
//
// WHY this exists: the ProbuilderCollisions MeshCollider (the old map barrier AND the rock/cliff collision)
// has been DISABLED (NetworkedWorld.DisableProbuilderCollisions). The Terrain heightmap is now the solid
// ground, but the visible rocks, cliffs, boulders and tree trunks became COSMETIC (no colliders), so the
// player clips straight through them. We do NOT want the continuous barrier back; we want each INDIVIDUAL
// obstacle to be solid so you walk AROUND it, with open gaps between obstacles.
//
// WHAT it does: once gameplay is live (after the barrier is gone), it scans every Renderer ONCE and adds a
// real collider to the meaningful obstacles that lack one:
//   - rock / cliff / boulder -> an EXACT-SHAPE MeshCollider whose sharedMesh is the renderer's MeshFilter mesh,
//     convex = false (a static, non-convex collider that matches the VISIBLE rock silhouette exactly). Because
//     the collider follows the real mesh, the player collides with the actual surface and can NEVER stand on
//     top of an oversized invisible box (the floating bug the old BoxCollider-from-bounds approach caused). A
//     non-convex MeshCollider is valid here because the player + NPCs use CharacterControllers, and a
//     CharacterController collides correctly against non-convex static MeshColliders. A runtime MeshCollider
//     only cooks if the mesh is Read/Write enabled; the Editor postprocessor MakeRockMeshesReadable flips that
//     on (+ a one-shot reimport). If a mesh is STILL not readable at runtime, we SKIP that object entirely
//     (cosmetic, walk-through) rather than fall back to a bounding box: a box would reintroduce the float.
//   - tree trunks (pine / tree / stump / log) -> a single CapsuleCollider on the trunk (cheap, leaves the
//     canopy walk-through), one per tree, sized from the WORLD bounds. The capsule does not read mesh data, so
//     it needs no readability gate; a thin trunk capsule from bounds does not cause the float a full-AABB box
//     does, and a per-tree mesh collider would be wasteful.
// Every collider we add is forced onto the Default layer (layer 0) so the player + NPC CharacterControllers
// (which collide with Default) actually stop on it. It SKIPS grass, bushes, foliage, debris, pinecones, our
// spawned bodies, the cabin, the terrain, and the beacon, reuses any collider already present (the 18 baked
// phys colliders + the barrier), colliders LOD0 only (never per-LOD), and caps the total so a runaway scene
// can never spawn thousands of colliders. It logs HOW MANY colliders it added so the fix is confirmable.
//
// NOT a barrier: it adds ONE collider PER individual renderer, never a merged/extruded mesh, so the result
// is inherently "go around each rock/tree" with gaps between. It NEVER re-enables ProbuilderCollisions.
//
// Classification mirrors RockClearance's token + ancestor-name logic so the two stay consistent. Note the
// GOTCHA RockClearance has: its ExcludeTokens cover "tree"/"trunk" but NOT "pine", and the forest trees are
// named "Pine_*"; here "pine" is classified explicitly as a TREE (capsule), so large pines are never mis-
// solidified as rocks.
//
// BUILD-SAFE: pure UnityEngine.* runtime API (no UnityEditor.*, so no #if UNITY_EDITOR). One class per name.
// Single Update, no OnGUI. Null-safe, never throws (try/catch around the collider assignment). No SpacetimeDB
// types, so no Vector3 alias needed. No "using System;", so System.Exception is fully qualified. No em dashes.
//
// IN-EDITOR CHECKS (an agent cannot run Unity):
//   1) Play-mode confirm: you walk AROUND a rock, a cliff and a pine, and you do NOT bump invisible walls in
//      open ground (no accidental barrier; grass/bushes/debris stay walk-through).
//   2) Watch the "[RockSolidifier] Solidified ..." log; confirm the boxes + trunks count is non-zero and lands
//      in the low hundreds. If it is zero, the name tokens are missing the actual rock/tree object names. Tune
//      MinObstacleXZ / MaxColliders / the token lists.
//   3) MeshColliders match the visible silhouette exactly (no oversized box top to stand on). If a rock count
//      reads lower than expected, watch the "skipped (mesh not readable)" tally in the log: those rocks had
//      non-readable meshes at runtime and were left walk-through on purpose (no box fallback). Run
//      Tools/Lost Expedition/Make Rock Meshes Readable, let Unity reimport, then re-enter Play mode.

using System.Collections.Generic;
using UnityEngine;

public class RockSolidifier : MonoBehaviour
{
    // ---- classification tokens (case-insensitive substring, matched on the renderer + up to 4 ancestors) --

    // Rock-like statics -> exact-shape MeshCollider (readable mesh required). Superset of RockClearance.RockTokens.
    static readonly string[] RockTokens =
    {
        "rock", "cliff", "stone", "boulder", "sandstone", "mountain",
        "flatrock", "slussen", "smallcliff", "passagecave"
    };

    // Tree-like statics -> a single CapsuleCollider on the trunk. NOTE "pine" is included (the forest trees
    // are "Pine_*"), which RockClearance.ExcludeTokens misses.
    static readonly string[] TreeTokens = { "pine", "tree", "trunk", "stump", "log" };

    // Foliage / grass / debris / our bodies / scenery we must NOT collider. Superset of
    // RockClearance.ExcludeTokens. Anything matching this is left cosmetic and walk-through.
    static readonly string[] SkipTokens =
    {
        "grass", "meadow", "bush", "shrub", "fern", "leaf", "leaves", "foliage", "branch", "twig",
        "clump", "stick", "debris", "pinecone", "cone", "moss", "flower", "detail", "backdrop",
        "terrain", "ground", "floor", "cabin", "shack", "hut", "tent",
        "player", "guard", "curator", "sister", "npc", "character", "body",
        "water", "river", "fog", "cloud", "probe", "light", "beacon"
    };

    // ---- thresholds -------------------------------------------------------------------------------------

    // Meaningful-obstacle floor (perf): below this an object stays cosmetic (pebbles, twigs). An obstacle must
    // clear BOTH a footprint (XZ) and a height (Y) test so flat ground scatter and tiny props are ignored.
    const float MinObstacleXZ = 0.6f;
    const float MinObstacleY = 0.8f;

    // Trunk capsule sizing for trees. Radius derives from the renderer footprint, clamped to a sane trunk.
    const float TrunkRadiusMin = 0.22f;
    const float TrunkRadiusMax = 0.55f;
    const float TrunkRadiusFromFootprint = 0.2f;   // radius ~= min(sizeX,sizeZ) * this, then clamped

    // Hard cap on colliders added in one pass. Nearest-to-landing obstacles are processed first, so if the
    // cap is ever hit it drops only far backdrop geometry and keeps the playable area solid.
    const int MaxColliders = 400;

    // Wait this long after gameplay goes live before scanning, so DisableProbuilderCollisions has run and the
    // forest spawners have instanced their baked rock/tree children.
    const float ScanDelay = 1.5f;

    // ---- one-shot state ---------------------------------------------------------------------------------

    static bool solidified;   // idempotent across recompiles / scene re-adds (mirrors NetworkedWorld.probuilderDisabled)
    bool done;
    float timer;

    // ---- self-bootstrap (no scene wiring; mirrors RescueMission.Bootstrap) -------------------------------

    // ENABLED with the exact-shape MeshCollider approach. The OLD version added a BoxCollider sized to each
    // rock's world AABB, which for an irregular rock/cliff was much bigger than the visible mesh, so the player
    // stood ON TOP of the oversized invisible box and floated. That is fixed: rocks now get a MeshCollider whose
    // sharedMesh is the visible LOD0 mesh (convex = false), so the collider matches the silhouette exactly and
    // the player can never float on a box top. A rock whose mesh is not readable at runtime is SKIPPED (left
    // walk-through), never boxed, so the float bug can never come back. Trees keep a cheap trunk capsule.
    const bool Enabled = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!Enabled) return;     // exact-shape MeshColliders (no oversized boxes, so no floating)
        if (solidified) return;   // already solidified this session; do not spawn a second bootstrapper
        var go = new GameObject("RockSolidifier");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<RockSolidifier>();
    }

    // ---- readiness gate: run the one-shot scan after the barrier is gone + gameplay is live --------------

    void Update()
    {
        if (done) return;

        // Same gate RescueMission.Update uses: wait for gameplay to begin (so the barrier has been disabled
        // first and the forest is populated), then a short settle delay before the single scan.
        if (!NetworkedWorld.GameplayActive) return;

        timer += Time.deltaTime;
        if (timer < ScanDelay) return;

        done = true;
        if (solidified) return;   // another instance already did the pass (idempotency belt-and-suspenders)
        solidified = true;

        Solidify();
    }

    // ---- the scan ---------------------------------------------------------------------------------------

    void Solidify()
    {
        Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        if (all == null || all.Length == 0)
        {
            Debug.Log("[RockSolidifier] No renderers found; nothing to solidify.");
            return;
        }

        // Sort nearest-to-landing first so the MaxColliders cap (if ever hit) keeps the playable area solid
        // and only drops far backdrop geometry. Landing = the shared spawn; falls back to the world offset.
        Vector3 anchor = RescueMission.LandingPosition;
        if (anchor == Vector3.zero) anchor = NetworkedWorld.WorldOffset;

        List<Renderer> ordered = new List<Renderer>(all.Length);
        for (int i = 0; i < all.Length; i++) if (all[i] != null) ordered.Add(all[i]);
        ordered.Sort((a, b) => SqrFlat(a.bounds.center, anchor).CompareTo(SqrFlat(b.bounds.center, anchor)));

        int rocks = 0, trees = 0, skipped = 0, capped = 0, unreadable = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            Renderer rend = ordered[i];
            if (rend == null) continue;
            if (!rend.enabled) continue;
            if (rend is ParticleSystemRenderer) continue;   // fog / sparks / VFX, not geometry

            // Reuse-existing-collider guard, NARROWED to this renderer's own GameObject plus its IMMEDIATE
            // parent (not a full ancestor walk). The old full GetComponentInParent<Collider> walk skipped an
            // entire grouped subtree the moment any ancestor (a shared "Rocks" group, a LODGroup root) carried
            // a stray collider, mass-skipping real rocks that needed solidifying. This narrow check still covers
            // the baked phys colliders + the (disabled) barrier on this object and keeps a re-run idempotent.
            if (HasOwnOrParentCollider(rend)) continue;

            // Skip our spawned bodies + characters (same filter RockClearance uses).
            if (rend.GetComponentInParent<LocalPlayer>() != null) continue;
            if (rend.GetComponentInParent<NpcAgent>() != null) continue;
            if (rend.GetComponentInParent<Health>() != null) continue;
            if (rend.GetComponentInParent<CombatAI>() != null) continue;

            // LOD0-only guard: collider the base / LOD0 mesh only, never LOD1/LOD2, so we add one collider per
            // object, not one per LOD level.
            if (IsNonZeroLod(rend)) continue;

            // Foliage / grass / debris / scenery skip (the "do not collider every blade of grass" rule).
            if (NameContains(rend, SkipTokens)) { skipped++; continue; }

            // Meaningful-obstacle floor (perf): ignore tiny / flat clutter.
            Vector3 size = rend.bounds.size;
            float xz = Mathf.Max(size.x, size.z);
            if (xz < MinObstacleXZ || size.y < MinObstacleY) { skipped++; continue; }
            if (size.x <= 0.01f && size.z <= 0.01f) { skipped++; continue; }   // degenerate / hidden

            // Perf cap (after all skips, before we actually add). Stop adding once we hit the cap; keep
            // counting how many we dropped so the log is honest.
            if (rocks + trees >= MaxColliders) { capped++; continue; }

            bool isTree = NameContains(rend, TreeTokens);
            bool isRock = NameContains(rend, RockTokens);

            if (isTree && !isRock)
            {
                if (AddTrunkCapsule(rend)) trees++;
            }
            else
            {
                // Everything else meaningful that passed the size + skip filters is solidified as a rock /
                // boulder / cliff with an exact-shape MeshCollider: a rock by name token, OR an unnamed obstacle
                // that already cleared the footprint + height floor (a real mid-size or large boulder). The old
                // hard 2.5m gate let real mid-size unnamed rocks slip through; the size floor above is enough.
                // AddRockCollider returns 1 = added, 0 = plain skip (no usable MeshFilter mesh), -1 = mesh not
                // readable at runtime (skipped on purpose, never boxed, so the float bug can never come back).
                int r = AddRockCollider(rend);
                if (r == 1) rocks++;
                else if (r == -1) unreadable++;
            }
        }

        if (capped > 0)
            Debug.LogWarning($"[RockSolidifier] Collider cap {MaxColliders} reached; {capped} far obstacle(s) left cosmetic. Raise MaxColliders if the far field needs collision.");

        if (unreadable > 0)
            Debug.LogWarning($"[RockSolidifier] {unreadable} rock(s)/cliff(s) skipped because their mesh was NOT readable at runtime; left walk-through on purpose (no box fallback, so no floating). Run Tools/Lost Expedition/Make Rock Meshes Readable, let Unity reimport, then re-enter Play mode.");

        int total = rocks + trees;
        Debug.Log($"[RockSolidifier] Solidified {total} collider(s): rocks/cliffs={rocks} (MeshCollider, exact shape) + trees={trees} (trunk CapsuleCollider), all on the Default layer. Skipped foliage/clutter={skipped}; skipped mesh-not-readable={unreadable}. ProbuilderCollisions stays disabled; obstacles are individual, not a barrier.");
    }

    // ---- collider builders ------------------------------------------------------------------------------

    // Rock / cliff / boulder -> an EXACT-SHAPE MeshCollider whose sharedMesh is the renderer's MeshFilter mesh,
    // convex = false (a static, non-convex collider that matches the VISIBLE rock silhouette exactly). Returns
    // 1 on success, 0 on plain skip (no MeshFilter / no mesh / degenerate), -1 when the mesh exists but is NOT
    // readable at runtime (so the caller can count + log the unreadable skips separately).
    //
    // We use a MeshCollider, NOT a BoxCollider from bounds: the old box wrapped each irregular rock in an
    // oversized invisible AABB, so the player stood ON TOP of the box and floated. The mesh collider follows the
    // real surface, so the player collides with the visible rock and can never float. Non-convex is valid here
    // because the player + NPCs use CharacterControllers, which collide correctly against non-convex static
    // MeshColliders.
    //
    // A runtime MeshCollider only cooks if the mesh is Read/Write enabled. If mesh.isReadable is false at
    // runtime we SKIP this object (return -1) and leave it cosmetic/walk-through. We DELIBERATELY do NOT fall
    // back to a bounding box: a box would reintroduce the float bug. The Editor postprocessor
    // MakeRockMeshesReadable flips Read/Write on so this skip path is rarely taken; this runtime check is the
    // guarantee that a still-unreadable mesh can never reintroduce floating.
    //
    // Forced onto the Default layer so the player + NPC CharacterControllers collide. Null-safe, never throws.
    static int AddRockCollider(Renderer rend)
    {
        try
        {
            // Mesh must come from a MeshFilter (skinned / ProBuilder / no-MeshFilter renderers are skipped, not
            // boxed). No world-bounds math: a MeshCollider inherits the transform automatically, so there is no
            // local-space conversion that could mis-size the collider.
            var mf = rend.GetComponent<MeshFilter>();
            if (mf == null) return 0;

            Mesh mesh = mf.sharedMesh;
            if (mesh == null) return 0;
            if (mesh.vertexCount <= 0) return 0;

            // CRITICAL anti-float gate: a MeshCollider can only cook a readable mesh. If it is not readable,
            // SKIP (do NOT add a bounding box, which caused the float). This readability check MUST come before
            // any mesh.triangles access: Mesh.triangles throws on a non-readable mesh (vertexCount is metadata
            // and is safe, but triangles reads CPU data). Checking isReadable first means the unreadable case
            // returns -1 (counted + logged so the user is told to run the reimport) instead of throwing into
            // the catch and being mis-counted as a plain skip.
            if (!mesh.isReadable) return -1;

            // Now safe to touch CPU-side mesh data: reject degenerate meshes (no usable triangles).
            int[] tris = mesh.triangles;
            if (tris == null || tris.Length < 3) return 0;

            var mc = rend.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;   // exact static rock shape (matches the visible silhouette, no oversized box top)

            ForceDefaultLayer(rend.gameObject);
            return 1;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[RockSolidifier] AddRockCollider failed on '" + SafeName(rend) + "' (non-fatal): " + e.Message);
            return 0;
        }
    }

    // Tree -> a single CapsuleCollider approximating the trunk (Y axis). Sized from the renderer's WORLD bounds
    // (never the mesh / localBounds, which can be degenerate on ProBuilder / skinned / no-MeshFilter renderers),
    // then expressed in local space so the object's own transform/scale is honored. Keeps the canopy walkable
    // and is cheap (one capsule per tree). Forced onto the Default layer. Returns true on success. Null-safe.
    static bool AddTrunkCapsule(Renderer rend)
    {
        try
        {
            Bounds wb = rend.bounds;   // world AABB
            if (wb.size.x <= 0.001f && wb.size.y <= 0.001f && wb.size.z <= 0.001f) return false;

            var cap = rend.gameObject.AddComponent<CapsuleCollider>();
            cap.direction = 1;   // Y axis (up)

            Transform t = rend.transform;

            // Convert the world AABB extents into local space so radius/height respect the object's scale.
            Vector3 le = t.InverseTransformVector(wb.extents);
            float lx = Mathf.Abs(le.x) * 2f;   // local footprint X
            float ly = Mathf.Abs(le.y) * 2f;   // local height Y
            float lz = Mathf.Abs(le.z) * 2f;   // local footprint Z

            float footprint = Mathf.Min(lx, lz);
            float radius = Mathf.Clamp(footprint * TrunkRadiusFromFootprint, TrunkRadiusMin, TrunkRadiusMax);
            float height = Mathf.Max(ly, radius * 2f + 0.01f);   // CapsuleCollider requires height >= 2*radius

            cap.radius = radius;
            cap.height = height;

            // Center the capsule on the trunk: world AABB center XZ, base at the world AABB bottom, lifted by
            // half-height. Convert that world anchor into local space.
            Vector3 worldBase = new Vector3(wb.center.x, wb.min.y, wb.center.z);
            Vector3 localBase = t.InverseTransformPoint(worldBase);
            cap.center = new Vector3(localBase.x, localBase.y + height * 0.5f, localBase.z);

            ForceDefaultLayer(rend.gameObject);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[RockSolidifier] AddTrunkCapsule failed on '" + SafeName(rend) + "' (non-fatal): " + e.Message);
            return false;
        }
    }

    // Put the collider's GameObject on the Default layer (layer 0) so the player + NPC CharacterControllers
    // (which collide with Default) actually stop on it. No combat body or collider in this project is assigned
    // a custom layer, so Default is the shared collision layer; an obstacle imported onto an Ignore-Raycast /
    // non-colliding layer would silently let the player pass through, which we prevent here. Idempotent.
    static void ForceDefaultLayer(GameObject go)
    {
        if (go == null) return;
        if (go.layer != 0) go.layer = 0;   // 0 = Default
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    // True if THIS renderer's own GameObject or its IMMEDIATE parent already has a collider, so a re-run is
    // idempotent and the baked phys colliders are reused. Deliberately NOT a full ancestor walk: walking all
    // the way up would skip an entire grouped subtree the moment any ancestor (a "Rocks" group, a LODGroup
    // root) carried a stray collider, mass-skipping real rocks that need solidifying.
    static bool HasOwnOrParentCollider(Renderer rend)
    {
        if (rend == null) return false;
        if (rend.GetComponent<Collider>() != null) return true;
        Transform p = rend.transform.parent;
        if (p != null && p.GetComponent<Collider>() != null) return true;
        return false;
    }

    // True if the renderer's own name or any of up to 4 ancestor names contains any token (case-insensitive).
    // Mirrors RockClearance.NameContains; copied per project convention (consistency over DRY).
    static bool NameContains(Renderer rend, string[] tokens)
    {
        if (rend == null || tokens == null) return false;
        Transform tr = rend.transform;
        for (int depth = 0; depth < 5 && tr != null; depth++)
        {
            string n = tr.name != null ? tr.name.ToLowerInvariant() : "";
            for (int i = 0; i < tokens.Length; i++)
                if (n.Length > 0 && n.Contains(tokens[i])) return true;
            tr = tr.parent;
        }
        return false;
    }

    // True if this renderer's own name marks it as a non-zero LOD level (LOD1 / LOD2 / _LOD3 ...), so we
    // collider only the base / LOD0 mesh and add one collider per object, not one per LOD.
    static bool IsNonZeroLod(Renderer rend)
    {
        if (rend == null) return false;
        string n = rend.gameObject.name != null ? rend.gameObject.name.ToLowerInvariant() : "";
        int idx = n.LastIndexOf("lod");
        if (idx < 0) return false;
        // Read the digit(s) following "lod"; a value > 0 is a non-base LOD level we skip.
        int p = idx + 3;
        int val = 0; bool any = false;
        while (p < n.Length && n[p] >= '0' && n[p] <= '9') { val = val * 10 + (n[p] - '0'); any = true; p++; }
        return any && val > 0;
    }

    static float SqrFlat(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    static string SafeName(Renderer rend)
    {
        if (rend == null || rend.gameObject == null) return "(null)";
        return rend.gameObject.name != null ? rend.gameObject.name : "(unnamed)";
    }
}
