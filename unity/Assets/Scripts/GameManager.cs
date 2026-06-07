// GameManager.cs — THE LOST EXPEDITION Unity client: the SpacetimeDB connection spine.
//
// Connects to the SpacetimeDB module, subscribes to the NPC + player tables, registers
// the local player, and pumps the connection each frame. Other scripts read
// GameManager.Conn and the table handles directly
// (e.g. GameManager.Conn.Db.Npc.OnInsert += ...).

using System;
using System.IO;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;

public class GameManager : MonoBehaviour
{
    [Header("Connection")]
    // MAINCLOUD is the default now. NOTE: the GameManager already in your scene has these values SERIALIZED,
    // so changing the code default does NOT change the existing instance — set them on the GameManager in the
    // Inspector too (local: ws://localhost:3000 / vibe-multiplayer ; cloud: the two values below).
    [Tooltip("Cloud: wss://maincloud.spacetimedb.com . Local: ws://localhost:3000")]
    public string serverUri = "wss://maincloud.spacetimedb.com";
    public string moduleName = "tombrush-lost-expedition";

    [Header("Join")]
    public string username = "Rescuer";
    public string characterClass = "Ranger";

    public static GameManager Instance { get; private set; }
    public static DbConnection Conn { get; private set; }
    public static Identity LocalIdentity { get; private set; }
    public static bool IsLocal(Identity id) => Conn != null && id == LocalIdentity;

    // Default TRUE so running the forest scene DIRECTLY auto-spawns the local player (+ its
    // third-person camera). The lobby flow overrides this to false in LobbyBootstrap.Awake and
    // calls JoinForest() on launch instead, so the player joins with the chosen character.
    public static bool AutoRegisterPlayer = true;

    // Fired after the initial subscription backfill is applied — safe to read tables.
    // IDEMPOTENT for late subscribers: OnReady fires DURING the lobby (one shot). When the
    // forest scene loads, NetworkedWorld subscribes in OnEnable AFTER the event already fired —
    // a plain `event Action` would never replay, so nothing would spawn. This custom accessor
    // invokes the handler immediately if we are already Ready, so late subscribers still wire.
    static Action _onReady;
    public static event Action OnReady
    {
        add { _onReady += value; if (Ready) value(); }
        remove { _onReady -= value; }
    }
    public static bool Ready { get; private set; }

    // Called by the lobby right before SceneManager.LoadScene("Forest_EnvironmentSample").
    // Creates the forest player row with the chosen character class (a string — NOT the
    // uint CharacterId; the lobby maps CharacterId/ModelKey -> class string before calling).
    public static void JoinForest(string username, string characterClass)
        => Conn.Reducers.RegisterPlayer(username, characterClass);

    const string TOKEN_KEY = "expedition_auth_token";

    // Per-machine server override file. Each player can edit this AFTER install (no rebuild) to
    // point at the LAN host, e.g. a single line: ws://192.168.1.50:3000 . Looked up first under
    // Application.persistentDataPath (user-editable, survives reinstall of the same build), then
    // as a shippable default under StreamingAssets. Blank lines and lines starting with # or //
    // are ignored, so the file can carry a comment explaining the format. If nothing usable is
    // found the serialized/code-default serverUri is kept. The module name is never read here.
    const string SERVER_CONFIG_FILE = "server.txt";

    // Returns the first non-blank, non-comment line from the override file, trimmed, or null.
    static string ResolveServerUriFromFile()
    {
        var persistentPath = Path.Combine(Application.persistentDataPath, SERVER_CONFIG_FILE);
        var uri = ReadFirstUriLine(persistentPath);
        if (!string.IsNullOrEmpty(uri))
        {
            Debug.Log($"[STDB] server override from persistentDataPath: {uri} ({persistentPath})");
            return uri;
        }

        var streamingPath = Path.Combine(Application.streamingAssetsPath, SERVER_CONFIG_FILE);
        uri = ReadFirstUriLine(streamingPath);
        if (!string.IsNullOrEmpty(uri))
        {
            Debug.Log($"[STDB] server override from StreamingAssets: {uri} ({streamingPath})");
            return uri;
        }

        return null;
    }

    // Reads a config file and returns the first usable line (trimmed), ignoring blanks and
    // comments (# or //). Returns null on missing file or any read error so callers fall back.
    static string ReadFirstUriLine(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith("//")) continue;
                return line;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[STDB] could not read server config {path}: {e.Message}");
        }
        return null;
    }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        // Per-machine override: lets two machines join one server without rebuilding. Falls back
        // to the serialized/code-default serverUri when no config file or no usable line exists.
        var resolvedUri = ResolveServerUriFromFile();
        if (!string.IsNullOrEmpty(resolvedUri)) serverUri = resolvedUri;
        Debug.Log($"[STDB] connecting to {serverUri} (module {moduleName})");

        var builder = DbConnection.Builder()
            .WithUri(serverUri)
            .WithDatabaseName(moduleName)
            .OnConnect(HandleConnect)
            .OnConnectError(HandleConnectError)
            .OnDisconnect(HandleDisconnect);

        var token = PlayerPrefs.GetString(TOKEN_KEY, "");
        if (!string.IsNullOrEmpty(token)) builder = builder.WithToken(token);

        Conn = builder.Build();
    }

    void Update()
    {
        // Pump the SpacetimeDB connection: applies incoming row updates + fires callbacks.
        Conn?.FrameTick();
    }

    void HandleConnect(DbConnection conn, Identity identity, string token)
    {
        LocalIdentity = identity;
        PlayerPrefs.SetString(TOKEN_KEY, token);
        Debug.Log($"[STDB] connected as {identity}");

        conn.SubscriptionBuilder()
            .OnApplied(OnSubscriptionApplied)
            .OnError((ErrorContext ctx, Exception e) => Debug.LogError($"[STDB] subscription error: {e.Message}"))
            .Subscribe(new[]
            {
                "SELECT * FROM player",
                "SELECT * FROM npc",
                "SELECT * FROM npc_interaction",
                "SELECT * FROM world_state",
                "SELECT * FROM clue_reveal",   // earned, player-safe facts -> drives MissionHud + ClueLog
                "SELECT * FROM party_clue",    // clue-graph edges -> gathered counter + NPC attribution
                "SELECT * FROM clue",          // supporting lookup: resolve party_clue.ClueId -> clue code
            });
    }

    void OnSubscriptionApplied(SubscriptionEventContext ctx)
    {
        Debug.Log("[STDB] subscription applied, joining the expedition");
        Ready = true;
        if (AutoRegisterPlayer) Conn.Reducers.RegisterPlayer(username, characterClass);
        _onReady?.Invoke();
    }

    void HandleConnectError(Exception e) => Debug.LogError($"[STDB] connect error: {e.Message}");

    void HandleDisconnect(DbConnection conn, Exception e)
    {
        Ready = false;
        Debug.LogWarning($"[STDB] disconnected: {e?.Message ?? "no reason"}");
    }
}
