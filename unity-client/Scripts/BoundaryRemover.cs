// BoundaryRemover.cs. frees roaming by disabling INVISIBLE blocker colliders (the real "wall" test).
//
// Self-bootstrapping exactly like PerfConfig: a RuntimeInitialize host spawns one persistent object,
// waits for the scene to settle, then runs ONCE. It scans every Collider in the scene and disables only
// those that are genuinely an INVISIBLE blocker: a non-terrain, non-trigger collider whose GameObject has
// NO MeshRenderer / SkinnedMeshRenderer anywhere in its hierarchy (so it draws nothing yet still pens the
// player) and whose world bounds are large enough to wall off an area.
//
// WHY this rule (from the scene deep-dive): every REAL obstacle in Forest_EnvironmentSample (rocks, trees,
// logs, stumps, the three cliff Planes, bushes, the rescue crate) HAS a renderer. The fog / lighting / post
// Volumes are triggers. The ground is a TerrainCollider. So "no renderer in hierarchy + not terrain + not a
// trigger + big bounds" cannot match any of the legitimate geometry. It will only fire on a genuinely added
// invisible wall (a renderer-less BoxCollider or MeshCollider with >40 m footprint, or a thin tall perimeter
// shell). This is strictly more reliable than name matching, which found nothing in this scene.
//
// HARD SAFETY. This MUST NOT free-fall the player or break lighting. It NEVER disables:
//   - TerrainCollider (the ground). explicit type skip.
//   - Any trigger collider (fog / post / lighting Volumes; CharacterController passes through triggers
//     anyway, so they never pen the player). explicit skip.
//   - Anything with a MeshRenderer or SkinnedMeshRenderer in its own / parent / child hierarchy (it draws
//     something, so it is visible geometry: rock, tree, log, stump, plane, bush, crate, prop). explicit skip.
//   - Anything on a known collision LAYER (Terrain / GroundScatter / Environment / Scatter / Undergrowth).
//   - Anything whose name (own or up to 4 ancestors) reads as ground / terrain / rock / cliff / tree /
//     stump / log / branch / root / bush / scatter / plane / prop / crate / camp / volume / fog / light /
//     probe / water (the keep-list), even if it somehow has no renderer.
//
// A collider is disabled ONLY if ALL of these hold:
//   1. not a TerrainCollider,
//   2. not a trigger,
//   3. NO MeshRenderer / SkinnedMeshRenderer in its hierarchy (the invisible test),
//   4. not on a known collision layer, and not keep-listed by name,
//   5. its world bounds are large: either footprint > 40 m on BOTH horizontal axes, OR a thin/tall
//      perimeter shell (one horizontal axis <= 2 m, the other >= 40 m, height >= 3 m).
// PLUS an explicit name gate: anything whose name reads as a boundary / wall / barrier / invisible / blocker
// (and is not keep-listed) is disabled regardless of bounds, so a deliberately placed named barrier is
// always removed.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Null-safe; never throws. Runs once, logs
// the count + the names it disabled, then stops.

using UnityEngine;

public static class BoundaryRemover
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("BoundaryRemover");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<BoundaryRuntime>();
    }

    class BoundaryRuntime : MonoBehaviour
    {
        bool done;
        float t;
        const float RunAfterSeconds = 0.5f; // let the scene (and any recovery merges) settle, like the other bootstraps

        // ---- bounds thresholds -----------------------------------------------------------------------
        const float BigFootprintMin = 40f; // metres: a wall that closes off an area is large on both axes
        const float ThinMax         = 2.0f; // metres: a perimeter panel is <= ~2 m thick on one axis
        const float LongMin         = 40f;  // metres: ...and runs at least ~40 m along the other
        const float TallMin         = 3.0f; // metres: ...and is at least ~3 m tall (cannot step over)

        // ---- name matching ---------------------------------------------------------------------------

        // Explicit barrier tokens: a non-trigger collider named like this (and not keep-listed) is disabled
        // regardless of size, so a deliberately placed named wall is always removed.
        static readonly string[] WallTokens =
        {
            "boundary", "wall", "barrier", "invisible", "limit", "fence", "clip", "blocker",
            "perimeter", "deathzone", "killzone", "outofbounds", "out_of_bounds", "playarea", "play_area",
        };

        // Keep-list: ground / scenery / props / lighting volumes / the rescue crate / camp. A name hit here
        // is NEVER disabled, even if it has a wall token or no renderer.
        static readonly string[] KeepTokens =
        {
            "terrain", "ground", "floor", "rock", "stone", "sandstone", "cliff", "mountain", "tree", "trunk",
            "branch", "root", "stump", "log", "wood", "grass", "foliage", "fern", "plant", "bush", "scatter",
            "prop", "hive", "plane", "volume", "fog", "light", "post", "reflection", "probe", "trigger",
            "water", "river", "lake", "house", "building", "tent", "crate", "camp", "road", "path", "bridge",
            "stair", "glade", "wind",
        };

        // Layers that are real collision geometry; never disable anything on these (Terrain / GroundScatter /
        // EnvironmentSmall / EnvironmentLarge / the Scatter / Undergrowth range, plus AreaVolume audio).
        static readonly string[] KeepLayers =
        {
            "Terrain", "GroundScatter", "EnvironmentSmall", "EnvironmentLarge", "Environment",
            "Scatter", "Undergrowth", "AreaVolume",
        };

        void Update()
        {
            if (done) return;
            t += Time.deltaTime;
            if (t < RunAfterSeconds) return;
            done = true;
            RemoveBoundaries();
            enabled = false;
        }

        void RemoveBoundaries()
        {
            int disabled = 0;
            var names = new System.Text.StringBuilder();

            // Include inactive so a merged/disabled-then-reactivated wall is still caught; sort none for speed.
            var colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (colliders == null)
            {
                Debug.Log("[BoundaryRemover] No colliders found; nothing to do.");
                return;
            }

            foreach (var col in colliders)
            {
                if (col == null || !col.enabled) continue;

                // HARD SKIP 1: never touch the terrain ground.
                if (col is TerrainCollider) continue;

                // HARD SKIP 2: never touch triggers (fog/post/lighting/audio volumes; CharacterController
                // passes through triggers, so they never pen the player).
                if (col.isTrigger) continue;

                // HARD SKIP 3: never touch anything on a real collision layer.
                if (IsKeepLayer(col)) continue;

                // HARD SKIP 4: never touch anything keep-listed by name (ground/rock/crate/camp/...).
                if (NameIsKeepListed(col)) continue;

                bool isWall = false;
                string why = null;

                // PATH A. explicit named barrier: a non-trigger collider named like a wall, not keep-listed.
                // (keep-list already excluded above.) Disabled regardless of size.
                if (NameReadsAsWall(col))
                {
                    isWall = true;
                    why = "named-barrier";
                }

                // PATH B. the real invisible-wall test: the GameObject draws NOTHING (no MeshRenderer /
                // SkinnedMeshRenderer anywhere in its hierarchy) yet has a large blocking footprint. This is
                // what distinguishes a genuine invisible wall from every visible rock/tree/log/plane/crate.
                if (!isWall && !HasRendererInHierarchy(col) && HasBlockingBounds(col))
                {
                    isWall = true;
                    why = "invisible-large-collider";
                }

                if (!isWall) continue;

                col.enabled = false;
                disabled++;
                if (names.Length > 0) names.Append(", ");
                names.Append(col.gameObject.name).Append(" (").Append(why).Append(")");
            }

            if (disabled == 0)
                Debug.Log("[BoundaryRemover] 0 invisible blocker colliders found (this scene's 'wall' is steep terrain + slope/step limits, fixed in LocalPlayer). Terrain, rocks, props, crate, fog/post volumes untouched.");
            else
                Debug.Log($"[BoundaryRemover] Disabled {disabled} invisible blocker collider(s): {names}. Terrain, visible geometry, crate, and fog/post volumes left intact.");
        }

        // ---- helpers ---------------------------------------------------------------------------------

        // True if the GameObject (or any parent / child, including inactive) has a MeshRenderer or
        // SkinnedMeshRenderer. If so it draws something => it is visible geometry => never disable.
        static bool HasRendererInHierarchy(Collider col)
        {
            if (col == null) return false;
            var go = col.gameObject;

            // Own + children (include inactive).
            if (go.GetComponentInChildren<MeshRenderer>(true) != null) return true;
            if (go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) return true;

            // Own + parents (a renderer can sit above the collider in the hierarchy).
            if (go.GetComponentInParent<MeshRenderer>(true) != null) return true;
            if (go.GetComponentInParent<SkinnedMeshRenderer>(true) != null) return true;

            return false;
        }

        // True if the collider's world-space bounds are large enough to wall off an area: either a big
        // footprint on both horizontal axes, OR a thin/tall perimeter shell.
        static bool HasBlockingBounds(Collider col)
        {
            if (col == null) return false;
            // col.bounds is already world-space (axis-aligned). For a disabled-but-present collider Unity still
            // reports bounds from the underlying shape; guard against a degenerate zero box anyway.
            Vector3 size = col.bounds.size;
            float sx = Mathf.Abs(size.x);
            float sy = Mathf.Abs(size.y);
            float sz = Mathf.Abs(size.z);

            if (sx <= 0.001f && sy <= 0.001f && sz <= 0.001f) return false; // degenerate / unresolvable

            bool bigFootprint = sx > BigFootprintMin && sz > BigFootprintMin;
            bool thinX = sx <= ThinMax && sz >= LongMin && sy >= TallMin;
            bool thinZ = sz <= ThinMax && sx >= LongMin && sy >= TallMin;
            return bigFootprint || thinX || thinZ;
        }

        // True if the collider's own name (or an ancestor's) contains a wall token. (Keep-list is checked
        // separately and earlier; callers have already excluded keep-listed colliders.)
        static bool NameReadsAsWall(Collider col)
        {
            var tr = col.transform;
            for (int depth = 0; tr != null && depth < 4; depth++, tr = tr.parent)
            {
                string n = tr.name != null ? tr.name.ToLowerInvariant() : "";
                for (int i = 0; i < WallTokens.Length; i++)
                    if (n.Contains(WallTokens[i])) return true;
            }
            return false;
        }

        // True if the collider's own name OR any ancestor name contains a keep token (ground/prop/volume/...).
        static bool NameIsKeepListed(Collider col)
        {
            var tr = col.transform;
            for (int depth = 0; tr != null && depth < 4; depth++, tr = tr.parent)
            {
                string n = tr.name != null ? tr.name.ToLowerInvariant() : "";
                for (int i = 0; i < KeepTokens.Length; i++)
                    if (n.Contains(KeepTokens[i])) return true;
            }
            return false;
        }

        // True if the collider's GameObject is on a layer that is real collision geometry.
        static bool IsKeepLayer(Collider col)
        {
            if (col == null) return false;
            string layerName = LayerMask.LayerToName(col.gameObject.layer);
            if (string.IsNullOrEmpty(layerName)) return false;
            for (int i = 0; i < KeepLayers.Length; i++)
                if (string.Equals(layerName, KeepLayers[i], System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
