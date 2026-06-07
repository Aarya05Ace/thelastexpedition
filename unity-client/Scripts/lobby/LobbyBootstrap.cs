// LobbyBootstrap.cs — THE LOST EXPEDITION cinematic lobby orchestrator (PART A).
//
// THE one MonoBehaviour you drop onto the forest 'Game' GO. Owns the whole lobby lifecycle as an
// AUTHORITATIVE state machine (Connecting -> Auth -> Lobby -> Launching) and builds EVERYTHING in code.
//
// STRICT GATING (PART A): NetworkedWorld.GameplayActive=false throughout Connecting/Auth/Lobby (so the
// armed Player/Npc OnInsert callbacks + back-fill loops no-op — NO forest NPCs, NO player). Book of the
// Dead's own BotD player (GameController -> PlayerController/PlayerCamera(Clone)) is suppressed each
// Update by disabling the GameController COMPONENT (primary) + name-Find on the clones (backstop). On
// Launch we flip GameplayActive=true FIRST, Teardown the lobby rig, JoinForest, then SpawnExisting().
//
// TWO-LAYER canvas: authLayer (alpha 1, raycast-blocking) is the only thing visible during Connecting+
// Auth; lobbyLayer (alpha 0, no raycasts) cross-fades in at the Lobby phase. ApplyPhaseVisibility runs
// on every transition. Sub-UI WireSubscription is deferred to OnAuthenticated (only auth wires at
// OnLobbyApplied) so nothing instantiates the lineup/roster/patron before the player is authed.
//
// CAMERA: a code-built lobbyCam (clearFlags=Skybox), depth 60 (above forest cam 0, below the depth-100
// ExpeditionCamera). The forest's tagged MainCamera (Camera + AudioListener) is disabled during the
// lobby and re-enabled on Teardown (AudioListener restore is load-bearing — ExpeditionCamera carries
// none).

using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.HighDefinition;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

[DefaultExecutionOrder(-100)] // run before the forest's GameManager so AutoRegisterPlayer=false sticks
public class LobbyBootstrap : MonoBehaviour
{
    [Header("THE ONLY THING TO WIRE: fill the 4 survivalists + the suit")]
    public CharacterLibrary library = new CharacterLibrary();

    [Header("Scene")]
    public string forestSceneName = "Forest_EnvironmentSample";

    // The scene to reload on RETURN TO LOBBY. Captured from forestSceneName so DeathScreen (and any other
    // caller) has an authoritative scene name without a serialized field of its own. Falls back to the
    // active scene name if the lobby never ran (defensive).
    static string _returnSceneName = "Forest_EnvironmentSample";

    [Header("Staging")]
    [Tooltip("Stage the lobby inside the already-loaded forest (state-swap on launch, no reload).")]
    public bool stageInForest = true;
    [Tooltip("Mirror of NetworkedWorld.worldOffset, the spawn clearing the lobby anchors to.")]
    public Vector3 worldOffset = new Vector3(-233.556f, 105.15f, -233.178f);

    [Header("Lobby rig placement (relative to worldOffset)")]
    public Vector3 lineupCenterOffset = new Vector3(0f, 0f, 2.5f);
    public Vector3 campfireOffset = new Vector3(0f, 0f, 3.9f);
    public Vector3 patronOffset = new Vector3(2.2f, 0f, 3.7f);
    public float lineupSpacing = 1.1f;   // trimmed so 6 podiums fit the frustum
    public float lineupArcDeg = 0f;       // straight row now (kept for the carousel signature)

    [Header("Lobby rig placement (!stageInForest fallback only)")]
    public Vector3 pedestalPos = new Vector3(0f, 0f, 0f);
    public Vector3 patronPos = new Vector3(2.6f, 0f, 0.6f);
    public Vector3 cameraPos = new Vector3(0f, 1.6f, -4.2f);

    enum Phase { Connecting, Auth, Lobby, Launching }
    Phase phase = Phase.Connecting;

    Camera lobbyCam;
    GameObject rigRoot;
    Canvas canvas;
    GameObject canvasGo;
    GameObject eventSystemGo;

    // Two full-stretch CanvasGroups.
    CanvasGroup authLayer;
    CanvasGroup lobbyLayer;

    // Campfire flicker
    Light campfireLight;
    float campfireBaseLumens;

    Camera forestCam;
    AudioListener forestAudio;

    LobbyAuthPanel auth;
    CharacterCarousel carousel;
    LobbyHud hud;
    PatronPresenter patron;

    bool lobbySubscribed;
    bool launchHooked;
    bool subUisWired;
    float connectStart;
    Coroutine fadeCo;

    // EARLIEST possible neutralization. [DefaultExecutionOrder(-100)] only orders Awake/Start WITHIN one
    // scene-load batch; it does NOT order this lobby (attached at runtime to the forest 'Game' GO) ahead
    // of the Master scene's MultiSceneController, which is ALREADY running. MultiSceneController.Start() is
    // an `IEnumerator Start` that, in the Editor, skips additive-scene loading and falls straight through
    // to gameController.SpawnPlayer(nextFrame:false) — a SYNCHRONOUS spawn (SpawnPlayerCo does not
    // `yield return null` first), so it NREs (GameController.cs:104 null camera; then PlayerController
    // LateUpdate.cs:67 on the half-built clone) BEFORE our Awake ever runs. Disabling `.enabled` from Awake
    // is then too late: an already-started Start coroutine keeps running, and the synchronous spawn body
    // has already executed and NRE'd.
    //
    // The only way to beat it is to disable the component BEFORE Unity calls its Start. A BeforeSceneLoad
    // RuntimeInitialize hook runs before any scene object's first Start, so a MultiSceneController disabled
    // here never gets Start() invoked -> the spawn coroutine never begins. We also disable the tagged
    // GameController so its own Start (GameController.cs:35 dereferences Camera.main.transform -> NRE when
    // the lobby has disabled the forest MainCamera) cannot fire either. Awake below repeats this as a
    // backstop (covers any MultiSceneController/GameController that only comes into existence after this
    // hook, e.g. via a later additive scene load).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void KillBotDDemoBeforeScene()
    {
        // MultiSceneController + GameController are the two Start()s that fire/own the demo spawn. Disable
        // them as early as possible so their Start is never invoked. Editor-only for MultiSceneController
        // (in a build its Start ALSO performs the additive-scene loading the game needs); the GameController
        // disable is safe in both — the lobby fully replaces the demo player with its own networked one.
        if (Application.isEditor)
            foreach (var msc in FindObjectsByType<MultiSceneController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                msc.enabled = false;

        var gcGo = SafeFindTagged("GameController");
        if (gcGo != null)
            foreach (var mb in gcGo.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == "GameController")
                {
                    mb.StopAllCoroutines();   // in case a spawn coroutine somehow already started
                    mb.enabled = false;
                }
    }

    void Awake()
    {
        if (GameManager.Instance == null && FindAnyObjectByType<GameManager>() == null)
        {
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<GameManager>();
        }
        GameManager.AutoRegisterPlayer = false;
        NetworkedWorld.GameplayActive = false;   // gate gameplay until Launch

        if (!string.IsNullOrEmpty(forestSceneName)) _returnSceneName = forestSceneName;

        connectStart = Time.time;
#if UNITY_EDITOR
        AutoWireLibraryInEditor();   // fill the 4 survivalist prefabs if the Inspector array is empty
#endif
        BuildRig();
        BuildCanvas();
        BuildLobbyUI();
        ApplyPhaseVisibility(Phase.Connecting);
        NeutralizeBotDDemo();
    }

    void OnEnable() { GameManager.OnReady += HandleReady; }
    void OnDisable() { GameManager.OnReady -= HandleReady; }

    void HandleReady()
    {
        if (lobbySubscribed) return;
        lobbySubscribed = true;

        GameManager.Conn.SubscriptionBuilder()
            .OnApplied(OnLobbyApplied)
            .OnError((ErrorContext ctx, Exception e) =>
            {
                Debug.LogError($"[LOBBY] subscription error: {e.Message}");
                if (auth != null) auth.SetConnecting($"Lobby subscription error: {e.Message}");
            })
            .Subscribe(new[]
            {
                "SELECT * FROM lobby_member",
                "SELECT * FROM lobby_character_catalog",
                "SELECT * FROM billionaire",
                "SELECT * FROM party_chat",
                "SELECT * FROM billionaire_request",
                "SELECT * FROM party",
            });
    }

    // Lobby tables are backfilled. ONLY auth wires here; carousel/hud/patron defer to OnAuthenticated.
    void OnLobbyApplied(SubscriptionEventContext ctx)
    {
        Debug.Log("[LOBBY] lobby subscription applied");
        phase = Phase.Auth;
        ApplyPhaseVisibility(Phase.Auth);

        auth.WireSubscription();

        if (!launchHooked)
        {
            launchHooked = true;
            GameManager.Conn.Reducers.OnLaunchGame += (rctx) =>
            {
                if (rctx.Event.Status is Status.Committed) Launch();
            };
        }
    }

    void OnAuthenticated(string username)
    {
        phase = Phase.Lobby;
        Debug.Log($"[LOBBY] authenticated as {username}");

        // Defer the lineup/roster/patron wiring until authed (each is idempotent via its own `wired`).
        if (!subUisWired)
        {
            subUisWired = true;
            carousel.WireSubscription();
            hud.WireSubscription();
            // The Patron (LLM billionaire) is disabled — no spontaneous speech-bubble popups.
            if (patron != null) patron.WireSubscription();
        }

        ApplyPhaseVisibility(Phase.Lobby);
    }

    void Update()
    {
        // Campfire flicker — Perlin (no per-frame Random alloc).
        if (campfireLight != null)
            campfireLight.intensity = campfireBaseLumens * (1f + Mathf.PerlinNoise(Time.time * 8f, 0f) * 0.25f - 0.12f);

        // Keep BotD's player suppressed until launch (a coroutine clone can appear a frame late).
        if (phase != Phase.Launching)
        {
            SuppressBotDPlayer();
            // BotD's player locks/hides the cursor on its first frames; keep it free for the menu.
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        if (phase == Phase.Connecting)
        {
            if (Time.time - connectStart > 8f && auth != null)
                auth.SetConnecting($"Still connecting to {Server()}\nIs the SpacetimeDB module running?");
        }
    }

    string Server()
    {
        var gm = GameManager.Instance;
        return gm != null ? $"{gm.serverUri} / {gm.moduleName}" : "the server";
    }

    // Book of the Dead ships a playable FPS demo. In the Editor, MultiSceneController.Start() skips its
    // build-only additive-scene loading and does ONLY this: call GameController.SpawnPlayer() (-> the
    // SpawnPlayerCo coroutine) and hand control to DebugControls. That coroutine NREs on a null camera,
    // UIController.LateUpdate->ForceUpdate dereferences GameController.defaultCamera, DebugControls.Update
    // and PlayerController.LateUpdate dereference half-built state. THE LOST EXPEDITION fully replaces all
    // of this with its own networked player/camera/world, so the demo drivers only ever error.
    //
    // The PRIMARY kill is the BeforeSceneLoad hook above (it disables MultiSceneController/GameController
    // before Unity ever calls their Start, which is the only point that can actually beat the synchronous
    // SpawnPlayer(nextFrame:false)). This Awake pass is the BACKSTOP: it re-disables the same drivers plus
    // UIController/DebugControls, and kills any clone that slipped through, for the case where a driver only
    // came into existence after the hook ran. [DefaultExecutionOrder(-100)] still orders us ahead of the
    // forest's own GameManager so AutoRegisterPlayer=false sticks.
    void NeutralizeBotDDemo()
    {
        // MultiSceneController fires the spawn. In the Editor its Start() ONLY spawns the demo player
        // (the additive-scene loading is gated on !Application.isEditor), so disabling it here is safe and
        // stops the spawn before it starts. In a build it also loads scenes, so we leave it alone there.
        if (Application.isEditor)
            foreach (var msc in FindObjectsByType<MultiSceneController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                msc.enabled = false;

        // UIController + DebugControls live in Assembly-CSharp (Assets/Code, no asmdef) -> reference directly.
        foreach (var ui in FindObjectsByType<UIController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            ui.enabled = false;
        foreach (var dc in FindObjectsByType<DebugControls>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            dc.enabled = false;

        SetGameControllerEnabled(false);   // also StopAllCoroutines, in case SpawnPlayerCo already started
        KillBotDClones();
    }

    // GameController lives in the 'Gameplay' assembly (Gameplay.asmdef). To avoid a hard cross-assembly
    // compile dependency from the lobby's Assembly-CSharp scripts, we find the "GameController"-tagged
    // GameObject (a built-in tag the BotD 'Game' GO carries) and toggle every MonoBehaviour on it whose
    // type name is "GameController" by reflection — no `using Gameplay;`, no link error.
    void SetGameControllerEnabled(bool on)
    {
        var gcGo = SafeFindTagged("GameController");
        if (gcGo != null)
            foreach (var mb in gcGo.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == "GameController")
                {
                    if (!on) mb.StopAllCoroutines();   // kill SpawnPlayerCo so it can't NRE mid-spawn
                    mb.enabled = on;
                }
    }

    static GameObject SafeFindTagged(string tag)
    {
        try { return GameObject.FindWithTag(tag); }
        catch { return null; }   // tag undefined in this project -> no BotD controller to suppress
    }

    void KillBotDClones()
    {
        GameObject.Find("PlayerController(Clone)")?.SetActive(false);
        GameObject.Find("PlayerCamera(Clone)")?.SetActive(false);
    }

    // Per-frame backstop (cheap) until launch: keep GameController off + any late clone dead.
    void SuppressBotDPlayer()
    {
        SetGameControllerEnabled(false);
        KillBotDClones();
    }

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

    // ---- PHASE VISIBILITY ----
    void ApplyPhaseVisibility(Phase p)
    {
        // Cursor: visible + free everywhere in the lobby (so you can click fields/buttons AND see the
        // mouse move); hidden + locked only once real gameplay begins (Launching).
        bool inMenu = p != Phase.Launching;
        Cursor.lockState = inMenu ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = inMenu;

        switch (p)
        {
            case Phase.Connecting:
            case Phase.Auth:
                SetLayer(authLayer, 1f, true);
                SetLayer(lobbyLayer, 0f, false);
                if (carousel != null) carousel.SetActive(false);
                if (patron != null) patron.SetActive(false);
                break;

            case Phase.Lobby:
                if (carousel != null) carousel.SetActive(true);
                if (patron != null) patron.SetActive(true);
                if (fadeCo != null) StopCoroutine(fadeCo);
                fadeCo = StartCoroutine(CrossFadeToLobby());
                break;

            case Phase.Launching:
                SetLayer(authLayer, 0f, false);
                SetLayer(lobbyLayer, 0f, false);
                if (carousel != null) carousel.SetActive(false);
                if (patron != null) patron.SetActive(false);
                break;
        }
    }

    IEnumerator CrossFadeToLobby()
    {
        // lobbyLayer becomes interactable up front; auth fades out.
        lobbyLayer.gameObject.SetActive(true);
        lobbyLayer.interactable = true; lobbyLayer.blocksRaycasts = true;
        authLayer.interactable = false; authLayer.blocksRaycasts = false;

        const float dur = 0.35f;
        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / dur);
            authLayer.alpha = 1f - k;
            lobbyLayer.alpha = k;
            yield return null;
        }
        authLayer.alpha = 0f; authLayer.gameObject.SetActive(false);
        lobbyLayer.alpha = 1f;
        fadeCo = null;
    }

    void SetLayer(CanvasGroup g, float alpha, bool interactive)
    {
        if (g == null) return;
        g.gameObject.SetActive(alpha > 0.001f || interactive);
        g.alpha = alpha;
        g.interactable = interactive;
        g.blocksRaycasts = interactive;
    }

    // ---- RETURN TO LOBBY (Fortnite death -> lobby) ----
    // Authoritative "back to the lobby" entry point. Called by DeathScreen after the death beat. We do NOT try to
    // rebuild the lobby in-place (Launch's Teardown is destructive and one-way: it destroys the rig/canvas/cam
    // with no rebuild path). Instead we RELOAD the forest scene. GameManager is DontDestroyOnLoad and the saved
    // auth token auto-logs-in on reconnect, so the reloaded LobbyBootstrap.Awake rebuilds the lobby fresh and
    // lands the same identity at Connecting -> Auth -> Lobby, ready to re-queue and DEPLOY for a new life.
    //
    // GameplayActive is flipped false FIRST so no body re-spawns during the reload (mirrors the lobby gate). The
    // dead body + its camera are destroyed by the scene unload. Null/state-safe; never throws.
    public static void ReturnToLobby()
    {
        // No body spawns while we tear the scene down and rebuild the lobby.
        NetworkedWorld.GameplayActive = false;

        string scene = !string.IsNullOrEmpty(_returnSceneName) ? _returnSceneName : SceneManager.GetActiveScene().name;

        if (!Application.CanStreamedLevelBeLoaded(scene))
        {
            Debug.LogError($"[LOBBY] RETURN TO LOBBY: scene '{scene}' is not in Build Settings (File > Build Settings > Scenes In Build). Cannot reload.");
            return;
        }

        Debug.Log($"[LOBBY] RETURN TO LOBBY: reloading '{scene}' (token auto-login -> lobby).");
        SceneManager.LoadScene(scene);
    }

    // ---- LAUNCH ----
    void Launch()
    {
        if (phase == Phase.Launching) return;
        phase = Phase.Launching;

        // Compute the chosen username + class from the lobby row BEFORE we tear the lobby down.
        string username = "Rescuer";
        string characterClass = "Ranger";
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me != null)
        {
            username = !string.IsNullOrEmpty(me.Username) ? me.Username : username;
            characterClass = ClassForCharacterId(me.CharacterId);
        }

        ApplyPhaseVisibility(Phase.Launching);
        Teardown();   // destroys the lobby rig; the DeployCutscene overlay is DontDestroyOnLoad and survives

        // DEPLOY cold-open: play the full-screen cutscene + "THE LOST EXPEDITION" title FIRST, THEN spawn. We do
        // NOT flip GameplayActive or JoinForest until the cutscene ENDS, so the player + bots are not spawned
        // during the cutscene (the player can no longer be killed mid-cutscene, and the mission + its cabin
        // waypoint only start after the player spawns). The spawn is deferred into the completion callback;
        // DeployCutscene fires the callback even if the video is missing/fails, so launch never stalls.
        DeployCutscene.Play(() => SpawnIntoForest(username, characterClass));
    }

    // Spawn the player + world AFTER the deploy cutscene + title finish (invoked by DeployCutscene's callback).
    // Flipping GameplayActive HERE (not before the cutscene) is what keeps bots/player/mission out until the
    // cutscene is over, so the player starts clean and fresh.
    void SpawnIntoForest(string username, string characterClass)
    {
        NetworkedWorld.GameplayActive = true;   // flip the gate so the armed Player.OnInsert + SpawnExisting fire

        if (stageInForest)
        {
            GameManager.JoinForest(username, characterClass);
            // Back-fill existing NPC + remote-player rows (the LOCAL player arrives via the armed OnInsert).
            var nw = FindAnyObjectByType<NetworkedWorld>();
            if (nw != null) nw.SpawnExisting();
            return;
        }

        GameManager.JoinForest(username, characterClass);
        if (!Application.CanStreamedLevelBeLoaded(forestSceneName))
        {
            Debug.LogError($"[LOBBY] Scene '{forestSceneName}' is not in Build Settings.");
            return;
        }
        SceneManager.LoadScene(forestSceneName);
    }

    string ClassForCharacterId(uint id)
    {
        switch (id)
        {
            case 0: return "Medic";
            case 1: return "Scout";
            case 2: return "Brute";
            case 3: return "Tinkerer";
        }
        var cat = GameManager.Conn.Db.LobbyCharacterCatalog.CharacterId.Find(id);
        if (cat != null && !string.IsNullOrEmpty(cat.Archetype)) return cat.Archetype;
        return "Ranger";
    }

    void Teardown()
    {
        if (carousel != null) carousel.Teardown();
        if (patron != null) patron.Teardown();

        if (lobbyCam != null) { lobbyCam.enabled = false; lobbyCam.tag = "Untagged"; }
        if (rigRoot != null) Destroy(rigRoot);
        lobbyCam = null;
        campfireLight = null;
        if (canvasGo != null) Destroy(canvasGo);
        if (eventSystemGo != null) Destroy(eventSystemGo);

        if (stageInForest)
        {
            // Leave BotD's demo drivers (MultiSceneController/GameController/UIController/DebugControls)
            // DISABLED — our networked player/camera/world fully replaces them, and re-enabling only
            // re-runs GameController.Start (resets Camera.main, re-spawns the unused demo player) and
            // brings the NullReference spam back. We only restore the forest camera's AudioListener,
            // since the depth-100 ExpeditionCamera carries none.
            if (forestCam != null) forestCam.enabled = true;
            if (forestAudio != null) forestAudio.enabled = true;
        }
    }

    // ---- BUILD: camera + HDRP lights ----
    void BuildRig()
    {
        rigRoot = new GameObject("LobbyRig");

        Vector3 lineupCenter = stageInForest ? worldOffset + lineupCenterOffset : pedestalPos;
        // HERO 3/4 FRAMING: pull the cam in closer (Z -5.4 -> -3.2), raise it toward head height
        // (Y 1.65 -> 1.85), and slide it off-axis to the left (X 0 -> -0.8) so the SELECTED (centered)
        // model reads at a theatrical 45° three-quarter angle instead of a flat documentary line-up.
        Vector3 camWorldPos  = stageInForest ? worldOffset + lineupCenterOffset + new Vector3(-0.8f, 1.85f, -3.2f) : cameraPos;

        if (stageInForest)
        {
            foreach (var go in GameObject.FindGameObjectsWithTag("MainCamera"))
            {
                var c = go.GetComponent<Camera>();
                if (c != null && c != lobbyCam)
                {
                    forestCam = c; c.enabled = false;
                    var al = go.GetComponent<AudioListener>();
                    if (al != null) { forestAudio = al; al.enabled = false; }
                }
            }
        }

        var camGo = new GameObject("LobbyCamera");
        camGo.transform.SetParent(rigRoot.transform, false);
        camGo.transform.position = camWorldPos;
        lobbyCam = camGo.AddComponent<Camera>();
        lobbyCam.depth = stageInForest ? 60 : 50;
        lobbyCam.tag = "MainCamera";
        if (stageInForest)
        {
            lobbyCam.clearFlags = CameraClearFlags.Skybox;
        }
        else
        {
            lobbyCam.clearFlags = CameraClearFlags.SolidColor;
            lobbyCam.backgroundColor = new Color(0.03f, 0.04f, 0.06f);
        }
        // Tighter FOV than the default 60° makes the hero model feel larger / more intimate in-frame.
        if (stageInForest) lobbyCam.fieldOfView = 50f;
        // Aim at the selected (centered) model's upper-chest so a 3/4 head-and-shoulders hero shot reads.
        lobbyCam.transform.LookAt(lineupCenter + Vector3.up * 1.3f);
        camGo.AddComponent<AudioListener>();

        if (stageInForest)
        {
            Vector3 fireWorld = worldOffset + campfireOffset;
            fireWorld.y = GroundY(fireWorld.x, fireWorld.z) + 0.6f;
            campfireLight = MakePointLight("CampfireLight", fireWorld, new Color(1.0f, 0.6f, 0.25f), 14000f, 7f);
            campfireBaseLumens = 14000f;

            BuildCampfireParticles(worldOffset + campfireOffset);
        }
        else
        {
            MakeLight("KeyLight",  new Vector3(40f, -150f, 0f), LightType.Directional, new Color(1f, 0.96f, 0.9f), 3.2f);
            MakeLight("FillLight", new Vector3(30f,  35f, 0f),  LightType.Directional, new Color(0.6f, 0.7f, 0.9f), 1.1f);
            MakePointLight("RimLight", pedestalPos + new Vector3(0f, 2.5f, -2f), new Color(1f, 0.85f, 0.6f), 12000f, 20f);
        }
    }

    void MakeLight(string name, Vector3 euler, LightType type, Color color, float intensity)
    {
        var go = new GameObject(name);
        go.transform.SetParent(rigRoot.transform, false);
        go.transform.rotation = Quaternion.Euler(euler);
        var l = go.AddComponent<Light>();
        l.type = type;
        l.color = color;
        var hd = go.AddComponent<HDAdditionalLightData>();
        hd.intensity = intensity;
    }

    Light MakePointLight(string name, Vector3 pos, Color color, float lumens, float range)
    {
        var go = new GameObject(name);
        go.transform.SetParent(rigRoot.transform, false);
        go.transform.position = pos;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.range = range;
        var hd = go.AddComponent<HDAdditionalLightData>();
        hd.intensity = lumens;
        return l;
    }

    void BuildCampfireParticles(Vector3 fireBase)
    {
        var go = new GameObject("CampfireFX");
        go.transform.SetParent(rigRoot.transform, false);
        Vector3 baseWorld = fireBase;
        baseWorld.y = GroundY(fireBase.x, fireBase.z) + 0.1f;
        go.transform.position = baseWorld;

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 0.6f;
        main.startSpeed = 1.1f;
        main.startSize = 0.35f;
        main.maxParticles = 30;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = new Color(1f, 0.55f, 0.18f, 1f);

        var emission = ps.emission;
        emission.rateOverTime = 28f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 12f;
        shape.radius = 0.18f;
        shape.rotation = new Vector3(-90f, 0f, 0f);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(new Color(1f, 0.7f, 0.25f), 0f), new GradientColorKey(new Color(0.5f, 0.12f, 0.02f), 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.6f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.2f)));

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        var shader = Shader.Find("HDRP/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader != null)
        {
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1f, 0.6f, 0.2f, 1f));
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(1f, 0.6f, 0.2f, 1f));
            renderer.material = mat;
        }
    }

    // ---- BUILD: canvas + EventSystem + the two layers ----
    void BuildCanvas()
    {
        canvasGo = new GameObject("LobbyCanvas");
        canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        // Match HEIGHT (1f), not the 0.5 blend: anchors the layout to a fixed 1080-tall reference so every
        // top/bottom-anchored band (wordmark/operator, party/chat, deploy/quote) sits at a predictable
        // position on any display aspect. Only the horizontal axis letterboxes. This is what keeps the two
        // demo machines (which may be 16:9 or 16:10 laptops) from showing overlapping/spilling UI.
        scaler.matchWidthOrHeight = 1f;
        canvasGo.AddComponent<GraphicRaycaster>();

        if (EventSystem.current == null)
        {
            eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            // Legacy uGUI module. The whole lobby is built on UnityEngine.UI (InputField/Button/Text) and
            // the project runs Active Input Handling = Both, so this module drives pointer clicks AND the
            // InputField keyboard reliably. A code-added InputSystemUIInputModule has NO action asset
            // assigned -> it processes nothing -> you can't click/focus a field -> you can't type.
            eventSystemGo.AddComponent<StandaloneInputModule>();
        }

        authLayer = MakeLayer("AuthLayer", 1f, true);
        lobbyLayer = MakeLayer("LobbyLayer", 0f, false);

        // Full-screen ember vignette at the BACK of the lobby layer.
        var vig = LobbyUI.Vignette(lobbyLayer.transform);
        vig.transform.SetAsFirstSibling();
    }

    CanvasGroup MakeLayer(string name, float alpha, bool interactive)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(canvas.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var g = go.AddComponent<CanvasGroup>();
        g.alpha = alpha;
        g.interactable = interactive;
        g.blocksRaycasts = interactive;
        return g;
    }

#if UNITY_EDITOR
    // Map the 4 catalog ModelKeys to the Survivalist display prefabs so the lineup shows REAL skins
    // instead of the capsule fallback. Runs UNCONDITIONALLY in the Editor (overwrites whatever is in the
    // Inspector) so a stale/empty manual wiring can't leave you staring at capsules. Logs the result so
    // it's verifiable in the Console. Editor-only — in a build this whole block is stripped and you fill
    // CharacterLibrary.characters in the Inspector instead.
    void AutoWireLibraryInEditor()
    {
        var map = new (string key, string path)[]
        {
            ("survivalist_medic",    "Assets/Survivalist/Prefab/Survivalist (1).prefab"),
            ("survivalist_scout",    "Assets/Survivalist/Prefab/Survivalist (2).prefab"),
            ("survivalist_brute",    "Assets/Survivalist/Prefab/Survivalist (3).prefab"),
            ("survivalist_tinkerer", "Assets/Survivalist/Prefab/Survivalist (4).prefab"),
        };

        var list = new System.Collections.Generic.List<CharacterLibrary.ModelEntry>();
        int found = 0;
        foreach (var (key, path) in map)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) { list.Add(new CharacterLibrary.ModelEntry { modelKey = key, prefab = prefab }); found++; }
            else Debug.LogWarning($"[LOBBY] survivalist prefab NOT FOUND at '{path}', that skin falls back to a capsule.");
        }

        if (library == null) library = new CharacterLibrary();
        library.characters = list.ToArray();
        Debug.Log($"[LOBBY] auto-wired {found}/4 survivalist skins.");
    }
#endif

    void BuildLobbyUI()
    {
        Vector3 lineupCenter = stageInForest ? worldOffset + lineupCenterOffset : pedestalPos;
        Vector3 fireWorld    = stageInForest ? worldOffset + campfireOffset : pedestalPos;
        Vector3 patronWorld  = stageInForest ? worldOffset + patronOffset : patronPos;

        auth = gameObject.AddComponent<LobbyAuthPanel>();
        auth.Build(authLayer.transform);
        auth.OnAuthenticated += OnAuthenticated;

        carousel = gameObject.AddComponent<CharacterCarousel>();
        carousel.Init(library, lobbyCam, canvas, lineupCenter, fireWorld, lineupSpacing, lineupArcDeg, GroundY);

        hud = gameObject.AddComponent<LobbyHud>();
        hud.Build(lobbyLayer.transform, carousel);

        // The Patron (LLM billionaire) presence is DISABLED pending your keep/remove decision — not
        // created, so there is no suit model and no speech-bubble popup. Every other patron reference is
        // null-guarded. To bring it back later, restore these two lines.
        // patron = gameObject.AddComponent<PatronPresenter>();
        // patron.Init(library, lobbyCam, canvas, patronWorld, lineupCenter, GroundY);
    }
}
