// NetworkedWorld.cs — spawns + syncs GameObjects from SpacetimeDB tables (Lost Expedition).
//
// Players and NPCs each get a GameObject. REMOTE players + NPCs interpolate to the server position.
// The LOCAL player is driven by its own CharacterController (LocalPlayer) — we do NOT lerp it, so
// real collision/gravity isn't fought by the network layer. Each NPC gets an NpcAgent that reacts
// to the LLM director's writes.

using System.Collections.Generic;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class NetworkedWorld : MonoBehaviour
{
    [Header("Prefabs (optional — capsule placeholders used if empty)")]
    public GameObject playerPrefab;
    public GameObject npcPrefab;

    [Header("Tuning")]
    public float lerpSpeed = 14f;

    [Tooltip("Shift all spawns to where the forest actually is (set to the forest ground position)")]
    public Vector3 worldOffset;
    public static Vector3 WorldOffset; // mirror so LocalPlayer can convert world<->server coords

    // GATE (PART A): false throughout Connecting/Auth/Lobby; flipped TRUE exactly once in
    // LobbyBootstrap.Launch(). Wire() still runs + ARMS the OnInsert/OnUpdate/OnDelete callbacks during
    // the lobby (do NOT gate Wire — else the async JoinForest->RegisterPlayer local Player row would
    // NEVER spawn); only the SpawnPlayer/SpawnNpc BODIES early-return while this is false, so the armed
    // callbacks + the back-fill loop no-op until gameplay begins.
    public static bool GameplayActive = false;

    class Synced { public GameObject go; public Vector3 target; public NpcAgent agent; public bool isLocal; }

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
        var db = GameManager.Conn.Db;

        db.Player.OnInsert += (ctx, p) => SpawnPlayer(p);
        db.Player.OnUpdate += (ctx, _old, p) => UpdatePlayer(p);
        db.Player.OnDelete += (ctx, p) => Remove(players, Key(p.Identity));

        db.Npc.OnInsert += (ctx, n) => SpawnNpc(n);
        db.Npc.OnUpdate += (ctx, _old, n) => UpdateNpc(n);
        db.Npc.OnDelete += (ctx, n) => RemoveNpc(n.NpcId);

        // NOTE: the back-fill Iter loops moved into SpawnExisting() — they would have spawned forest
        // rows during the lobby. LobbyBootstrap.Launch() flips GameplayActive=true, then JoinForest(),
        // then SpawnExisting() to back-fill existing NPC/remote-player rows. The just-registered LOCAL
        // player arrives later via the now-LIVE armed Player.OnInsert.
    }

    // Back-fill rows that existed BEFORE gameplay began (forest NPCs + already-present remote players).
    // Called by LobbyBootstrap.Launch() AFTER GameplayActive is set true.
    public void SpawnExisting()
    {
        var db = GameManager.Conn.Db;
        foreach (var p in db.Player.Iter()) SpawnPlayer(p);
        foreach (var n in db.Npc.Iter()) SpawnNpc(n);
    }

    static string Key(Identity id) => id.ToString();

    // Apply the world offset, then raycast DOWN onto the terrain (lowest hit = ground, not a tree).
    // Server y becomes "height above ground". Used for spawn placement + remote interpolation.
    Vector3 V(float x, float y, float z)
    {
        float wx = x + worldOffset.x, wz = z + worldOffset.z;
        float groundY = worldOffset.y;
        var hits = Physics.RaycastAll(new Vector3(wx, worldOffset.y + 400f, wz), Vector3.down, 3000f);
        if (hits.Length > 0)
        {
            groundY = float.MaxValue;
            foreach (var h in hits) if (h.point.y < groundY) groundY = h.point.y;
        }
        return new Vector3(wx, groundY + y, wz);
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
        if (players.ContainsKey(key)) { UpdatePlayer(p); return; }
        var pos = V(p.Position.X, p.Position.Y, p.Position.Z);

        // Spawn the player's chosen survivalist MODEL (HDRP-fixed); capsule only as a last resort.
        // ResolvePrefab is BUILD-SAFE (Resources primary, editor AssetDatabase fallback), so the call is
        // unconditional — gating it on #if UNITY_EDITOR was the bug that left builds with capsules only.
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

        bool isLocal = GameManager.IsLocal(p.Identity);

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
        players[key] = new Synced { go = go, target = pos, isLocal = isLocal };
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
        var pos = V(n.Position.X, n.Position.Y, n.Position.Z);

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
        agent.Init(n.NpcId);
        npcs[n.NpcId] = new Synced { go = go, target = pos, agent = agent };
        agent.Apply(n);
    }
    void UpdateNpc(Npc n)
    {
        if (npcs.TryGetValue(n.NpcId, out var s)) { s.target = V(n.Position.X, n.Position.Y, n.Position.Z); s.agent.Apply(n); }
        else SpawnNpc(n);
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
        foreach (var s in npcs.Values) if (s.go) s.go.transform.position = Vector3.Lerp(s.go.transform.position, s.target, t);
    }
}
