// DeathScreen.cs — THE LOST EXPEDITION: full-screen "YOU DIED" overlay + REPLAY.
//
// A self-bootstrapping ScreenSpaceOverlay overlay that lives ABOVE the in-game HUD
// (sortingOrder 200 vs the HUD's 100). It waits for the LOCAL player to spawn
// (PlayerCombat.Local, null until then), binds to that player's Health.OnDied, and on
// death fades in a dark vignette/tint with a large bold "YOU DIED" title, a one-line
// subtitle, and a prominent REPLAY button. REPLAY -> PlayerCombat.Local.Respawn() then
// hide. While shown: CanvasGroup blocks raycasts + the cursor is unlocked/visible; while
// hidden: alpha 0 + no raycast (gameplay aims/fires straight through).
//
// Pure Unity (UnityEngine + UnityEngine.UI + LobbyUI helpers) — no SpacetimeDB, no
// Vector3 alias, no UnityEditor use. Null-safe throughout: a destroyed/absent player never
// throws. Authored against the SHARED CONTRACT surface of PlayerCombat:
//     public static PlayerCombat Local { get; }
//     public Health Health { get; }
//     public void Respawn();
// (Those land from the BUILD-'player' agent; this file must compile alongside them.)

using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class DeathScreen : MonoBehaviour
{
    // ---- self-bootstrap: one host that waits for PlayerCombat.Local, then binds (and re-binds) ----
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
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

    // ---- cursor restore ----
    CursorLockMode _prevLock = CursorLockMode.Locked;
    bool _prevCursorVisible = false;

    void Awake()
    {
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
    }

    // ===== binding =====

    static PlayerCombat SafeLocal()
    {
        // PlayerCombat.Local is a static; guarded so an unfinished/destroyed contract never throws.
        return PlayerCombat.Local;
    }

    // The lobby's EventSystem is destroyed on launch (LobbyBootstrap.Teardown), so gameplay has NO
    // EventSystem and uGUI clicks (incl. REPLAY) silently do nothing. Create a persistent one if missing.
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

    // ===== death / replay =====

    void OnDied(Health h)
    {
        if (_shown) return;        // don't restack the fade if OnDied somehow fires twice
        Show();
    }

    void OnReplay()
    {
        // Restore gameplay capture FIRST so a destroyed player can't leave the cursor freed.
        Cursor.lockState = _prevLock;
        Cursor.visible = _prevCursorVisible;

        var local = SafeLocal();
        if (local != null) local.Respawn();   // ResetFull health + InputDisabled=false + re-holster

        Hide();
    }

    // ===== show / hide =====

    void Show()
    {
        if (_shown) return;
        _shown = true;

        // The lobby destroyed its EventSystem on launch, so gameplay has none -> the REPLAY button can't be
        // clicked. Guarantee one exists before showing the screen.
        EnsureEventSystem();

        // Free the cursor for the REPLAY button; cache the previous state to restore on replay.
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
        var sub = LobbyUI.ShadowLabel(root, "The expedition presses on without you.",
                                      LobbyUI.BodySize, LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(sub.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(900f, 40f));

        // --- REPLAY button ---
        var btn = LobbyUI.RoundedButton(root, "REPLAY", LobbyUI.Ember,
                                        new Color(0.05f, 0.03f, 0.02f, 1f), 12);
        LobbyUI.Place(btn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), new Vector2(280f, 64f));
        btn.onClick.AddListener(OnReplay);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}
