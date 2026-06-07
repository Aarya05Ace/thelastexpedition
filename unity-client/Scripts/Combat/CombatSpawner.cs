// CombatSpawner.cs — self-bootstrapping spawner for the client-side combat vertical slice (NOT networked).
//
// Auto-creates itself at runtime (no scene wiring, no edits to NetworkedWorld). Once gameplay is live
// (NetworkedWorld.GameplayActive == true) AND a local player exists, it spawns ONE firefight near the
// player: two squads ~20 m apart — 3 Bodyguards (enemy) vs 3 Survivalists (ally). Each spawned body gets:
//   model -> HDRP material fix -> CharacterRig.Apply(go,false) -> Health(100, faction) -> WeaponRig.Equip
//   -> CombatAI(faction). The two squads then acquire + shoot each other; the player (a Survivalist) can
//   join in. Spawns exactly once, then disables itself.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. All model/weapon/controller loads go through
// BUILD-SAFE resolvers (SurvivalistModels/BodyguardModels/WeaponRig/CharacterRig — Resources primary, editor
// AssetDatabase fallback), so the firefight spawns in BOTH the editor AND a player build. Null-safe; never throws.

using UnityEngine;

public class CombatSpawner : MonoBehaviour
{
    [Tooltip("Combatants per side.")]
    public int squadSize = 3;

    [Tooltip("Forward distance from the player to the midpoint between the two squads (metres).")]
    public float engagementDistance = 22f;

    [Tooltip("Half the separation between the two squads along the firing line (metres).")]
    public float squadSeparation = 10f;

    [Tooltip("Lateral spacing between squad-mates (metres).")]
    public float memberSpacing = 2.2f;

    [Tooltip("How often to poll for readiness while waiting (seconds).")]
    public float pollInterval = 0.5f;

    bool spawned;
    float pollTimer;

    // ---- self-bootstrap (no scene object / no NetworkedWorld edit required) --------------------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        // One persistent host object that polls until gameplay is ready, then spawns once.
        var go = new GameObject("CombatSpawner");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<CombatSpawner>();
    }

    void Update()
    {
        if (spawned) return;

        // Cheap poll: don't FindObject every frame.
        pollTimer += Time.deltaTime;
        if (pollTimer < pollInterval) return;
        pollTimer = 0f;

        if (!NetworkedWorld.GameplayActive) return;

        var player = FindFirstObjectByType<LocalPlayer>();
        if (player == null) return;

        spawned = true;
        SpawnFirefight(player.transform);
        enabled = false; // job done; stop polling
    }

    // ---- the firefight ------------------------------------------------------------------------------

    void SpawnFirefight(Transform player)
    {
        // Build a firing line in front of the player. Survivalists on the player's side (allies);
        // Bodyguards on the far side (enemies), facing each other.
        Vector3 origin = player.position;
        Vector3 fwd = player.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd); // player-relative right along the firing line

        // Midpoint of the engagement, a bit ahead of the player.
        Vector3 mid = origin + fwd * engagementDistance;

        // Ally squad (Survivalists): near side, facing forward (toward the enemy).
        Vector3 allyLine = mid - fwd * squadSeparation;
        // Enemy squad (Bodyguards): far side, facing back (toward the allies).
        Vector3 enemyLine = mid + fwd * squadSeparation;

        for (int i = 0; i < squadSize; i++)
        {
            float lateral = (i - (squadSize - 1) * 0.5f) * memberSpacing;

            Vector3 allyPos = allyLine + right * lateral;
            SpawnCombatant(Faction.Survivalist, i, allyPos, fwd);   // allies look downrange (+fwd)

            Vector3 enemyPos = enemyLine + right * lateral;
            SpawnCombatant(Faction.Bodyguard, i, enemyPos, -fwd);   // enemies look back at the allies
        }

        Debug.Log($"[CombatSpawner] Spawned {squadSize}v{squadSize} firefight near {origin} " +
                  $"(Survivalists vs Bodyguards).");
    }

    void SpawnCombatant(Faction faction, int variant, Vector3 pos, Vector3 facing)
    {
        // Resolve + instantiate the model (BUILD-SAFE: Resources primary, editor AssetDatabase fallback).
        GameObject prefab = faction == Faction.Survivalist
            ? SurvivalistModels.ResolvePrefab(SurvivalistClassFor(variant))
            : BodyguardModels.ResolvePrefab(variant);

        if (prefab == null)
        {
            Debug.LogWarning($"[CombatSpawner] No model for {faction} variant {variant}; skipping.");
            return;
        }

        Vector3 spawnPos = GroundSnap(pos);
        Quaternion rot = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.z), Vector3.up);
        var go = Object.Instantiate(prefab, spawnPos, rot);
        if (go == null) return;
        go.name = $"Combat_{faction}_{variant}";

        // HDRP material fix so nothing renders solid white in the HDRP scene.
        if (faction == Faction.Survivalist) SurvivalistModels.FixHdrp(go);
        else BodyguardModels.FixHdrp(go);

        // Body-fitted CapsuleCollider + Animator wired to the shared locomotion controller (NPC path).
        // This collider is what the raycasts hit and what GetComponentInParent<Health>() resolves through.
        CharacterRig.Apply(go, false);

        // Damage receiver on the SAME root as the collider.
        var health = go.GetComponent<Health>();
        if (health == null) health = go.AddComponent<Health>();
        health.maxHealth = 100f;   // GREEN only
        health.maxShield = 0f;     // NO blue shield — only the human player gets shield
        health.faction = faction;

        // Mount the AK + add a configured Weapon (25 dmg) to the root.
        var weapon = WeaponRig.Equip(go);

        // The brain. Init wires faction + cached refs and keeps Health.faction in sync.
        var ai = go.GetComponent<CombatAI>();
        if (ai == null) ai = go.AddComponent<CombatAI>();
        ai.Init(faction, weapon, health);
    }

    // Map a squad slot to a distinct survivalist class so the allies look varied (medic/scout/brute/tinker).
    static string SurvivalistClassFor(int variant)
    {
        switch (variant % 4)
        {
            case 0: return "medic";
            case 1: return "scout";
            case 2: return "brute";
            default: return "tinker";
        }
    }

    // Raycast DOWN onto the terrain so combatants spawn on the ground (lowest hit = ground, not a tree),
    // mirroring NetworkedWorld.V. Falls back to the requested position if nothing is hit.
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
