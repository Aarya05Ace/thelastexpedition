// DeathScreen.cs. THE LOST EXPEDITION: full-screen "YOU DIED" overlay -> RETURN TO LOBBY.
//
// DEATH IS TERMINAL (Fortnite, one life per deploy). A self-bootstrapping ScreenSpaceOverlay
// that lives ABOVE the in-game HUD (sortingOrder 200 vs the HUD's 100). It waits for the LOCAL
// player to spawn (PlayerCombat.Local, null until then), binds to that player's Health.OnDied,
// and on death fades in a dark vignette/tint with a large bold "YOU DIED" title, a one-line
// subtitle, and a RETURN TO LOBBY button. There is NO in-place respawn: after a brief death beat
// the screen AUTO-returns to the lobby (the button is the manual skip). Returning reloads the
// forest scene via LobbyBootstrap.ReturnToLobby(). GameManager survives (DontDestroyOnLoad) and
// the saved token auto-logs-in straight back to the lobby to re-queue + DEPLOY for a fresh life.
// While shown: CanvasGroup blocks raycasts + the cursor is unlocked/visible; while hidden:
// alpha 0 + no raycast (gameplay aims/fires straight through).
//
// Pure Unity (UnityEngine + UnityEngine.UI + LobbyUI helpers). No SpacetimeDB, no
// Vector3 alias, no UnityEditor use. Null-safe throughout: a destroyed/absent player never
// throws. Authored against the SHARED CONTRACT surface of PlayerCombat:
//     public static PlayerCombat Local { get; }
//     public Health Health { get; }
//     public bool IsDeadTerminal { get; }
// plus LobbyBootstrap.ReturnToLobby() for the return path.

using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class DeathScreen : MonoBehaviour
{
    // ---- self-bootstrap: ONE persistent host that waits for PlayerCombat.Local, then binds (and re-binds) ----
    // This runs on EVERY scene load (AfterSceneLoad). The host is DontDestroyOnLoad, so on the RETURN TO LOBBY
    // reload it would otherwise spawn a SECOND overlay. Guard with a singleton so exactly one ever exists across
    // reloads (extra deaths -> extra reloads must not stack overlays).
    static DeathScreen _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null) return;   // already have a persistent host (survived a scene reload)
        var host = new GameObject("DeathScreenHost");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<DeathScreen>();
    }

    // ---- bound state ----
    PlayerCombat _boundPlayer;     // the PlayerCombat we're currently subscribed to
    Health _boundHealth;           // its Health (cache so we can unsubscribe cleanly)

    // ---- visuals ----
    Canvas _canvas;
    CanvasGroup _group;            // drives alpha + raycast blocking
    Image _tint;                   // full-screen dark fill (animated 0 -> 0.82)
    Image _vignette;               // black edge vignette layered for the faux-"blur"/obscure look

    // ---- animation ----
    bool _shown;
    float _fadeFade;               // current visible factor 0..1
    Coroutine _fadeCo;
    const float FadeTime = 0.4f;
    const float TintMaxAlpha = 0.82f;

    // ---- death beat -> auto return-to-lobby ----
    Coroutine _beatCo;
    bool _returning;               // latched once the return-to-lobby is committed (idempotent; survives reload)
    const float DeathBeat = 2.2f;  // seconds the "YOU DIED" beat holds before auto-returning to the lobby

    // ---- cursor restore ----
    CursorLockMode _prevLock = CursorLockMode.Locked;
    bool _prevCursorVisible = false;

    void Awake()
    {
        // Singleton: if a persistent host already exists (e.g. a stray duplicate), destroy this one.
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;

        BuildUI();
        HideImmediate();
    }

    void Update()
    {
        // Watch for the local player to appear or change (first spawn, respawn into a new body,
        // scene reload). Only bind once a REAL Local exists — survives the early BotD demo-player
        // teardown described in the project notes.
        var local = SafeLocal();
        if (local != _boundPlayer)
            Rebind(local);
    }

    void OnDestroy()
    {
        Unbind();
        if (_instance == this) _instance = null;
    }

    // ===== binding =====

    static PlayerCombat SafeLocal()
    {
        // PlayerCombat.Local is a static; guarded so an unfinished/destroyed contract never throws.
        return PlayerCombat.Local;
    }

    // The lobby's EventSystem is destroyed on launch (LobbyBootstrap.Teardown), so gameplay has NO
    // EventSystem and uGUI clicks (incl. RETURN TO LOBBY) silently do nothing. Create a persistent one if missing.
    static void EnsureEventSystem()
    {
        if (UnityEngine.EventSystems.EventSystem.current != null) return;
        var es = new GameObject("EventSystem");
        es.AddComponent<UnityEngine.EventSystems.EventSystem>();
        es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        Object.DontDestroyOnLoad(es);
    }

    void Rebind(PlayerCombat next)
    {
        Unbind();

        _boundPlayer = next;
        _boundHealth = (next != null) ? next.Health : null;

        if (_boundHealth != null)
            _boundHealth.OnDied += OnDied;

        // A NEW, LIVE body has spawned (the next life after RETURN TO LOBBY -> re-DEPLOY). Clear the terminal
        // 'returning' latch so this fresh life's death can show the screen again, and stop any stale beat. The
        // overlay persists across the reload, so without this reset the second death would be silently swallowed.
        if (_boundHealth != null && !_boundHealth.IsDead)
        {
            _returning = false;
            if (_beatCo != null) { StopCoroutine(_beatCo); _beatCo = null; }
        }

        // A fresh body should never start on the death screen.
        if (_shown && (_boundHealth == null || !_boundHealth.IsDead))
            HideImmediate();
    }

    void Unbind()
    {
        if (_boundHealth != null)
            _boundHealth.OnDied -= OnDied;
        _boundHealth = null;
        _boundPlayer = null;
    }

    // ===== death / return to lobby =====

    void OnDied(Health h)
    {
        if (_shown || _returning) return;   // don't restack the beat if OnDied somehow fires twice
        Show();

        // Hold the death beat, then auto-return to the lobby. The RETURN TO LOBBY button shortcuts the wait.
        if (_beatCo != null) StopCoroutine(_beatCo);
        if (isActiveAndEnabled) _beatCo = StartCoroutine(DeathBeatThenReturn());
    }

    IEnumerator DeathBeatThenReturn()
    {
        // Unscaled so the beat holds even if anything paused the game.
        float t = 0f;
        while (t < DeathBeat && !_returning)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        _beatCo = null;
        ReturnToLobby();
    }

    // Button + auto-beat both land here. Idempotent: only fires the reload once.
    void ReturnToLobby()
    {
        if (_returning) return;
        _returning = true;

        if (_beatCo != null) { StopCoroutine(_beatCo); _beatCo = null; }

        // Restore gameplay cursor capture FIRST so the freed-cursor state can't leak into the rebuilt lobby
        // before its own ApplyPhaseVisibility re-frees it (defensive; the lobby re-frees the cursor anyway).
        Cursor.lockState = _prevLock;
        Cursor.visible = _prevCursorVisible;

        // The overlay persists across the scene reload (DontDestroyOnLoad), so hide it NOW so the parchment/
        // tint does not linger over the freshly rebuilt lobby. On the reloaded scene Local == null -> stays hidden.
        HideImmediate();

        // Reload the forest scene: GameManager survives, the saved token auto-logs-in, the lobby rebuilds at
        // Connecting -> Auth -> Lobby. ReturnToLobby() also flips GameplayActive=false so nothing respawns mid-reload.
        LobbyBootstrap.ReturnToLobby();
    }

    // ===== show / hide =====

    void Show()
    {
        if (_shown) return;
        _shown = true;

        // The lobby destroyed its EventSystem on launch, so gameplay has none -> the RETURN TO LOBBY button can't
        // be clicked. Guarantee one exists before showing the screen.
        EnsureEventSystem();

        // Free the cursor for the RETURN TO LOBBY button; cache the previous state to restore on return.
        _prevLock = Cursor.lockState;
        _prevCursorVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        _group.blocksRaycasts = true;
        _group.interactable = true;

        StartFade(1f);
    }

    void Hide()
    {
        if (!_shown) { StartFade(0f); return; }
        _shown = false;

        _group.blocksRaycasts = false;
        _group.interactable = false;

        StartFade(0f);
    }

    void HideImmediate()
    {
        _shown = false;
        _fadeFade = 0f;
        if (_fadeCo != null) { StopCoroutine(_fadeCo); _fadeCo = null; }
        ApplyFade(0f);
        if (_group != null)
        {
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }
    }

    void StartFade(float target)
    {
        if (!isActiveAndEnabled) { _fadeFade = target; ApplyFade(target); if (_group != null) _group.alpha = target; return; }
        if (_fadeCo != null) StopCoroutine(_fadeCo);
        _fadeCo = StartCoroutine(FadeTo(target));
    }

    IEnumerator FadeTo(float target)
    {
        float start = _fadeFade;
        float t = 0f;
        // Time-scale-independent: a paused game should still let the overlay animate.
        while (t < FadeTime)
        {
            t += Time.unscaledDeltaTime;
            float f = Mathf.SmoothStep(start, target, Mathf.Clamp01(t / FadeTime));
            ApplyFade(f);
            yield return null;
        }
        ApplyFade(target);
        _fadeCo = null;
    }

    void ApplyFade(float f)
    {
        _fadeFade = f;
        if (_group != null) _group.alpha = f;
        if (_tint != null)
            _tint.color = new Color(0.02f, 0f, 0f, TintMaxAlpha * f);
        if (_vignette != null)
            _vignette.color = new Color(0f, 0f, 0f, f); // full black edges == faux obscure/"blur"
    }

    // ===== UI construction =====

    void BuildUI()
    {
        // --- own canvas, above the HUD (sortingOrder 200 > HUD's 100) ---
        var canvasGo = new GameObject("DeathScreenCanvas", typeof(RectTransform));
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 200;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        canvasGo.AddComponent<GraphicRaycaster>();

        _group = canvasGo.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        var root = canvasGo.GetComponent<RectTransform>();

        // --- back: dark full-screen tint (fades 0 -> 0.82) ---
        _tint = LobbyUI.RoundedPanel(root, "Tint", new Color(0.02f, 0f, 0f, 0f), 1);
        Stretch(_tint.rectTransform);
        _tint.raycastTarget = true;   // eats clicks behind the overlay while shown

        // --- black edge vignette on top of the tint (fakes a blurred/obscured world) ---
        _vignette = LobbyUI.Vignette(root);
        _vignette.color = new Color(0f, 0f, 0f, 0f);
        _vignette.raycastTarget = false;

        // --- "YOU DIED" title (double-stroke: dark glow behind, crimson in front) ---
        var glow = LobbyUI.ShadowLabel(root, LobbyUI.Spaced("YOU DIED"), 64,
                                       new Color(0.20f, 0.02f, 0.02f, 1f), TextAnchor.MiddleCenter);
        LobbyUI.Place(glow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(0.5f, 0.5f), new Vector2(2f, 88f), new Vector2(900f, 120f));

        var title = LobbyUI.ShadowLabel(root, LobbyUI.Spaced("YOU DIED"), 64,
                                        LobbyUI.Crimson, TextAnchor.MiddleCenter);
        LobbyUI.Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), new Vector2(900f, 120f));

        // --- subtitle ---
        var sub = LobbyUI.ShadowLabel(root, "The expedition presses on without you. Returning to the lobby.",
                                      LobbyUI.BodySize, LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(sub.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(900f, 40f));

        // --- RETURN TO LOBBY button (manual skip of the death beat; the screen auto-returns regardless) ---
        var btn = LobbyUI.RoundedButton(root, "RETURN TO LOBBY", LobbyUI.Ember,
                                        new Color(0.05f, 0.03f, 0.02f, 1f), 12);
        LobbyUI.Place(btn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), new Vector2(320f, 64f));
        btn.onClick.AddListener(ReturnToLobby);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}
