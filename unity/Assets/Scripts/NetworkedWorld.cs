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

    class Synced { public GameObject go; public Vector3 target; public NpcAgent agent; public bool isLocal; }

    readonly Dictionary<string, Synced> players = new();
    readonly Dictionary<ulong, Synced> npcs = new();

    void OnEnable() { WorldOffset = worldOffset; GameManager.OnReady += Wire; }
    void OnDisable() => GameManager.OnReady -= Wire;

    void Wire()
    {
        WorldOffset = worldOffset;
        var db = GameManager.Conn.Db;

        db.Player.OnInsert += (ctx, p) => SpawnPlayer(p);
        db.Player.OnUpdate += (ctx, _old, p) => UpdatePlayer(p);
        db.Player.OnDelete += (ctx, p) => Remove(players, Key(p.Identity));

        db.Npc.OnInsert += (ctx, n) => SpawnNpc(n);
        db.Npc.OnUpdate += (ctx, _old, n) => UpdateNpc(n);
        db.Npc.OnDelete += (ctx, n) => RemoveNpc(n.NpcId);

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
        var key = Key(p.Identity);
        if (players.ContainsKey(key)) { UpdatePlayer(p); return; }
        var pos = V(p.Position.X, p.Position.Y, p.Position.Z);
        var go = Spawn(playerPrefab, PrimitiveType.Capsule, pos);
        go.name = $"Player_{p.Username}";
        Tint(go, new Color(0.55f, 0.75f, 1f)); // teammates = cool blue
        bool isLocal = GameManager.IsLocal(p.Identity);
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
        if (npcs.ContainsKey(n.NpcId)) { UpdateNpc(n); return; }
        var pos = V(n.Position.X, n.Position.Y, n.Position.Z);
        var go = Spawn(npcPrefab, PrimitiveType.Capsule, pos);
        go.name = $"NPC_{n.DisplayName}";
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
