// RockClearance.cs. Renderer-based spawn clearance for the Lost Expedition forest.
//
// WHY this exists: the ProbuilderCollisions MeshCollider (which doubled as the map barrier AND the
// rock/cliff collision) has been REMOVED so the player can free-roam. The visible rocks are now
// cosmetic, with no colliders. That means the old physics OverlapCapsule probes (NetworkedWorld.Overlaps,
// ExpeditionPopulation.OverlapsObstacle) can no longer "feel" a rock, so a cabin / sister / Curator /
// guard / player can land embedded in a visible boulder.
//
// FIX: detect rocks by their RENDERER bounds instead of colliders. ClearOfRocks(pos, radius) tests whether
// a spawn point falls inside any nearby big-rock renderer's world-space bounds (expanded by radius). If it
// does, it ring-searches outward (same concentric-rings pattern as the existing ClearSpawn helpers),
// re-snapping each candidate to the terrain ground (the TerrainCollider is still there for the down-raycast),
// and returns the first point clear of all rock bounds. If fully boxed in, it returns the original pos.
//
// The terrain is still the solid ground. This helper NEVER moves anything onto a non-existent surface: every
// candidate is re-ground-snapped onto the terrain heightmap via a downward raycast.
//
// BUILD-SAFE: pure UnityEngine.* runtime API (no UnityEditor.*, so no #if UNITY_EDITOR). Null-safe; never
// throws. One class per name. No em dashes anywhere.

using System.Collections.Generic;
using UnityEngine;

public static class RockClearance
{
    // Name tokens that mark a renderer as a rock-like obstacle (case-insensitive substring match).
    static readonly string[] RockTokens = { "rock", "cliff", "stone", "boulder", "sandstone", "mountain" };

    // A renderer whose max XZ extent exceeds this (in metres) is treated as a "big" obstacle even if its
    // name carries no rock token, so unnamed large rock meshes still push spawns out.
    const float LargeExtentXZ = 2.5f;

    // Waist height above the ground point at which a spawn is tested against rock bounds.
    const float WaistHeight = 0.9f;

    // Down-raycast start height above worldOffset / candidate, and its length, for re-grounding candidates.
    const float RayUp = 400f;
    const float RayLen = 3000f;

    // Cached rock renderer bounds. Gathered lazily, refreshed when stale (rocks are static scenery, so a
    // long TTL is fine; a fresh scene / additive load re-gathers on the next call past the TTL).
    static readonly List<Bounds> rockBounds = new List<Bounds>();
    static float cacheTime = -999f;
    const float CacheTtl = 15f;

    // Returns a ground point near pos that is clear of all big-rock renderer bounds. If pos is already clear,
    // returns pos unchanged. If it cannot find a clear point, returns pos (build-safe fallback, never throws).
    // radius = the body's footprint half-width used to inflate the rock bounds when testing for overlap.
    public static Vector3 ClearOfRocks(Vector3 pos, float radius)
    {
        if (radius < 0f) radius = 0f;

        EnsureCache();
        if (rockBounds.Count == 0) return pos;        // nothing to avoid

        if (!InsideAnyRock(pos, radius)) return pos;  // already clear

        // Concentric rings: rising radii x 12 directions. First clear, re-grounded point wins (nearest first).
        const int dirs = 12;
        float[] radii = { 1f, 1.6f, 2.4f, 3.4f, 4.6f, 6f, 8f };
        foreach (float r in radii)
        {
            for (int i = 0; i < dirs; i++)
            {
                float ang = (Mathf.PI * 2f) * (i / (float)dirs);
                float cx = pos.x + Mathf.Cos(ang) * r;
                float cz = pos.z + Mathf.Sin(ang) * r;
                Vector3 cand = new Vector3(cx, GroundY(cx, cz, pos.y), cz);
                if (!InsideAnyRock(cand, radius)) return cand;
            }
        }

        Debug.LogWarning($"[RockClearance] No clear ground near {pos} (radius {radius}); spawning at original point.");
        return pos;   // build-safe fallback: never throw, never drop the spawn
    }

    // True if a body footprint (waist-height sphere of the given radius) at this ground point falls inside any
    // cached rock bounds (each inflated by radius). Tests the waist point so we do not flag a body that is
    // merely standing NEXT to a rock at ground level.
    static bool InsideAnyRock(Vector3 ground, float radius)
    {
        Vector3 waist = ground + Vector3.up * WaistHeight;
        for (int i = 0; i < rockBounds.Count; i++)
        {
            Bounds b = rockBounds[i];
            b.Expand(radius * 2f);   // Expand grows total size; *2 turns radius into a per-side margin
            if (b.Contains(waist)) return true;
        }
        return false;
    }

    // Lowest downward-raycast hit = the floor under (wx,wz). The TerrainCollider is still present, so this
    // lands on solid ground. Falls back to fallbackY if nothing is hit (off-map / over a hole).
    static float GroundY(float wx, float wz, float fallbackY)
    {
        var hits = Physics.RaycastAll(new Vector3(wx, fallbackY + RayUp, wz), Vector3.down, RayLen);
        if (hits == null || hits.Length == 0) return fallbackY;
        float y = float.MaxValue;
        foreach (var h in hits) if (h.point.y < y) y = h.point.y;
        return y == float.MaxValue ? fallbackY : y;
    }

    // Gather (or refresh) the big-rock renderer bounds. Scans all renderers once, keeps those that look like a
    // rock by name OR are large by bounds, and skips terrain / our own spawned bodies / characters / cabin.
    static void EnsureCache()
    {
        if (Time.unscaledTime - cacheTime < CacheTtl && rockBounds.Count > 0) return;
        cacheTime = Time.unscaledTime;
        rockBounds.Clear();

        Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        if (all == null) return;

        for (int i = 0; i < all.Length; i++)
        {
            Renderer rend = all[i];
            if (rend == null) continue;
            if (!rend.enabled) continue;
            if (IsExcluded(rend)) continue;

            Bounds b = rend.bounds;
            Vector3 size = b.size;
            bool large = Mathf.Max(size.x, size.z) >= LargeExtentXZ;
            bool named = NameLooksLikeRock(rend);
            if (!large && !named) continue;        // ignore small unnamed clutter (grass, pebbles, debris)
            if (size.x <= 0.01f && size.z <= 0.01f) continue;  // degenerate / hidden renderer

            rockBounds.Add(b);
        }
    }

    // Skip terrain, characters, and anything WE spawn (players / NPCs / combatants) plus the rescue cabin, so
    // those are never treated as rocks to avoid. Mirrors the exclusion filters used by the physics probes in
    // NetworkedWorld.Overlaps and ExpeditionPopulation.OverlapsObstacle.
    static bool IsExcluded(Renderer rend)
    {
        if (rend is ParticleSystemRenderer) return true;   // fog / sparks / VFX, not geometry

        // Our spawned bodies and characters carry one of these components on themselves or an ancestor.
        if (rend.GetComponentInParent<LocalPlayer>() != null) return true;
        if (rend.GetComponentInParent<NpcAgent>() != null) return true;
        if (rend.GetComponentInParent<Health>() != null) return true;
        if (rend.GetComponentInParent<CombatAI>() != null) return true;

        // Terrain renders via a Terrain component, not a normal Renderer, so it is not in this list anyway;
        // but guard by name too in case a mesh stand-in is used. Also skip the cabin and trees by name so a
        // cabin wall or a tree trunk is not treated as a rock the cabin/sister must dodge.
        return NameContains(rend, ExcludeTokens);
    }

    static readonly string[] ExcludeTokens =
    {
        "terrain", "ground", "floor", "cabin", "shack", "hut", "tent", "tree", "trunk", "branch",
        "leaf", "leaves", "foliage", "grass", "bush", "shrub", "player", "guard", "curator",
        "sister", "npc", "character", "body", "water", "river", "fog", "cloud"
    };

    static bool NameLooksLikeRock(Renderer rend)
    {
        // Reject a rock-name hit if the object also reads as something we exclude (so a "rocky cabin" or a
        // "stone path floor" prop is not treated as a boulder).
        if (NameContains(rend, ExcludeTokens)) return false;
        return NameContains(rend, RockTokens);
    }

    // True if the renderer's own name or any of up to 4 ancestor names contains any of the given tokens.
    static bool NameContains(Renderer rend, string[] tokens)
    {
        if (rend == null || tokens == null) return false;
        Transform tr = rend.transform;
        for (int depth = 0; depth < 5 && tr != null; depth++)
        {
            string n = tr.name != null ? tr.name.ToLowerInvariant() : "";
            for (int i = 0; i < tokens.Length; i++)
                if (n.Contains(tokens[i])) return true;
            tr = tr.parent;
        }
        return false;
    }
}
