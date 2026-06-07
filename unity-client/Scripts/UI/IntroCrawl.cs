// IntroCrawl.cs — THE LAST EXPEDITION cold-open. A slow, letterboxed, SEQUENTIAL title sequence in the
// register of The Last of Us / RDR2 chapter cards: one line at a time, fading up over a deepened world
// inside cinematic bars, holding, fading out before the next begins. The last beat hands you the verb.
//
// It is the sole STAGE OWNER while it runs (StorySequencer.ClaimStage), and on the last frame (natural end
// OR skip) it calls StorySequencer.NotifyIntroComplete() BEFORE destroying itself. That single call is the
// gate that releases the gameplay HUD, so the cinematic title fully completes and clears before any HUD shows.
//
// Plays ONCE per process (a static guard). Self-bootstraps via [RuntimeInitializeOnLoadMethod] like the
// other HUDs. The beat machine runs in a coroutine for readable timing. Skip is BEAT-AWARE: the first tap
// jumps to the verb beat (so a single key never nukes the whole thing), a second tap tears down.
//
// Typography is Barlow via StorySequencer: BarlowCondensed-SemiBold for the big title, Barlow-Medium for
// the premise body, Barlow-SemiBold for the verb. No em dashes. No emoji. Pure Unity, build-safe:
// UnityEngine + UnityEngine.UI only. NO SpacetimeDB types -> no Vector3 alias. NO UnityEditor -> no #if.

using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class IntroCrawl : MonoBehaviour
{
    // ---- the beats, in the brief's voice. Terse, second-person, atmospheric. ----
    enum Kind { Title, Body, Verb }

    struct Beat
    {
        public string text;
        public Kind kind;
        public float fadeIn, hold, fadeOut;
        public Beat(string t, Kind k, float fi, float h, float fo) { text = t; kind = k; fadeIn = fi; hold = h; fadeOut = fo; }
    }

    // Timing envelope (seconds). Slow on purpose: a title card, not a loading screen.
    static readonly Beat[] Beats =
    {
        new Beat("THE LAST EXPEDITION",                                            Kind.Title, 1.6f, 2.6f, 1.2f),
        new Beat("Dusk. A billionaire's private island.",                         Kind.Body,  1.0f, 2.4f, 0.9f),
        new Beat("Your sister, Mara, is somewhere inside his house.",             Kind.Body,  1.0f, 2.4f, 0.9f),
        new Beat("No one here knows the whole truth. But everyone knows a piece.",Kind.Body,  1.0f, 2.6f, 0.9f),
        new Beat("Make them talk.",                                               Kind.Verb,  1.2f, 2.8f, 1.4f),
    };

    const float InterBeatGap = 0.35f;   // quiet between beats
    const float BarsIn       = 0.8f;    // letterbox slide-in at the very start
    const float BarsOut      = 0.9f;    // letterbox + backdrop retract at the very end
    const float BarHeight    = 130f;    // cinematic ~2.4:1 look at 1920x1080

    // Once per process: the cold-open never replays on scene reloads / re-spawns.
    static bool _shown;

    CanvasGroup beatGroup;     // drives the CURRENT beat's alpha
    Text beatLabel;            // the single, reused beat line
    CanvasGroup hintGroup;     // the dim "press any key" hint, in the bottom bar
    RectTransform barTop, barBottom;
    CanvasGroup backdropGroup; // the deepened-world wash + vignette
    float barReveal;           // 0..1 letterbox openness

    int skipStage;             // 0 = none, 1 = jump to verb, 2 = tear down
    bool teardown;             // set once we begin the final retract / handoff

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_shown) return;
        // FLOW GATE: the cold open must NEVER paint over the auth screen or the lobby. With the lobby staged
        // inside the already-loaded forest scene, AfterSceneLoad fires at process start, so we must NOT play
        // here. Instead, spawn a tiny waiter that holds until NetworkedWorld.GameplayActive flips true
        // (LobbyBootstrap.Launch sets it exactly once, after the player presses DEPLOY). Only then do we
        // claim the stage and play. We do NOT set _shown yet: the once-per-process guard belongs to the
        // actual play, so a never-launched session (quit at the lobby) leaves the intro available.
        var waiterHost = new GameObject("IntroCrawlGate");
        Object.DontDestroyOnLoad(waiterHost);
        waiterHost.AddComponent<IntroGate>();
    }

    // Holds the cold open until gameplay actually begins. Pure poll so it is independent of spawn order and
    // never references SpacetimeDB. NetworkedWorld.GameplayActive is a plain static bool (default false,
    // flipped true once in LobbyBootstrap.Launch), so reading it here is build-clean.
    class IntroGate : MonoBehaviour
    {
        void Update()
        {
            if (_shown) { Destroy(gameObject); return; }   // already played this process (defensive)
            if (!NetworkedWorld.GameplayActive) return;     // still in Connecting / Auth / Lobby — wait
            if (DeployCutscene.Active) return;              // hold until the deploy cutscene + title card finish

            _shown = true;
            var host = new GameObject("IntroCrawlHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<IntroCrawl>();
            Destroy(gameObject);
        }
    }

    void Awake()
    {
        // Claim the stage so nothing else paints over the cold open. If something already owns it (it never
        // should this early), we still play but flag the intro complete defensively so HUDs are not stranded.
        bool ok = StorySequencer.ClaimStage(this);
        Build();
        if (ok) StartCoroutine(Run());
        else { StorySequencer.NotifyIntroComplete(); Destroy(gameObject); }
    }

    void OnDestroy()
    {
        // If we are torn down for any reason, never leave the gate closed or the stage held.
        StorySequencer.ReleaseStage(this);
        StorySequencer.NotifyIntroComplete();
    }

    // =================================================================================================
    // BUILD — canvas, deepened-world wash, letterbox bars, the single reused beat label, skip hint
    // =================================================================================================

    void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 250;   // above the death screen (200) and all HUD — it is the cold open

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;   // match the HUD canvases
        scaler.matchWidthOrHeight = 0.5f;

        var rootGroup = gameObject.AddComponent<CanvasGroup>();
        rootGroup.blocksRaycasts = false;   // never eats clicks
        rootGroup.interactable = false;

        var root = (RectTransform)transform;

        // ---- Deepened-world wash (NOT an opaque panel): a low-alpha warm-black over the live frame, plus
        //      the existing code-gen vignette, on its own CanvasGroup so it can retract at the end. ----
        var washHost = LobbyUI.Panel(root, "Backdrop", new Color(0, 0, 0, 0));
        washHost.anchorMin = Vector2.zero; washHost.anchorMax = Vector2.one;
        washHost.offsetMin = Vector2.zero; washHost.offsetMax = Vector2.zero;
        washHost.GetComponent<Image>().raycastTarget = false;
        backdropGroup = washHost.gameObject.AddComponent<CanvasGroup>();
        backdropGroup.alpha = 1f;

        var wash = LobbyUI.Panel(washHost, "Wash", new Color(0.02f, 0.018f, 0.016f, 0.86f));
        wash.anchorMin = Vector2.zero; wash.anchorMax = Vector2.one;
        wash.offsetMin = Vector2.zero; wash.offsetMax = Vector2.zero;
        wash.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Vignette(washHost);

        // ---- Letterbox bars (top + bottom), height driven each frame by barReveal. ----
        barTop    = MakeBar(root, true);
        barBottom = MakeBar(root, false);

        // ---- The single, reused beat label, centered. Its weight/size/color is set per beat. ----
        var bGroupHost = LobbyUI.Panel(root, "Beat", new Color(0, 0, 0, 0));
        bGroupHost.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(bGroupHost, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      Vector2.zero, new Vector2(1180f, 360f));
        beatGroup = bGroupHost.gameObject.AddComponent<CanvasGroup>();
        beatGroup.alpha = 0f;

        beatLabel = LobbyUI.ShadowLabel(bGroupHost, "", 30, LobbyUI.AshText, TextAnchor.MiddleCenter);
        beatLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
        beatLabel.verticalOverflow = VerticalWrapMode.Overflow;
        var blrt = beatLabel.rectTransform;
        blrt.anchorMin = new Vector2(0.5f, 0.5f); blrt.anchorMax = new Vector2(0.5f, 0.5f);
        blrt.pivot = new Vector2(0.5f, 0.5f);
        blrt.anchoredPosition = Vector2.zero;
        blrt.sizeDelta = new Vector2(1140f, 200f);

        // ---- Skip hint, dim, low, inside the bottom bar. Hidden until the first beat appears. ----
        var hintHost = LobbyUI.Panel(root, "Hint", new Color(0, 0, 0, 0));
        hintHost.GetComponent<Image>().raycastTarget = false;
        hintHost.anchorMin = new Vector2(0.5f, 0f); hintHost.anchorMax = new Vector2(0.5f, 0f);
        hintHost.pivot = new Vector2(0.5f, 0f);
        hintHost.anchoredPosition = new Vector2(0f, 40f);
        hintHost.sizeDelta = new Vector2(520f, 22f);
        hintGroup = hintHost.gameObject.AddComponent<CanvasGroup>();
        hintGroup.alpha = 0f;

        var hint = LobbyUI.ShadowLabel(hintHost, "press any key to continue", LobbyUI.HintSize,
                                       LobbyUI.AshDim, TextAnchor.LowerCenter);
        var hrt = hint.rectTransform;
        hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one;
        hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
        StorySequencer.Apply(hint, StorySequencer.Weight.Regular);

        ApplyBars();
    }

    RectTransform MakeBar(RectTransform root, bool top)
    {
        var bar = LobbyUI.Panel(root, top ? "BarTop" : "BarBottom", new Color(0f, 0f, 0f, 1f));
        bar.GetComponent<Image>().raycastTarget = false;
        bar.anchorMin = top ? new Vector2(0f, 1f) : new Vector2(0f, 0f);
        bar.anchorMax = top ? new Vector2(1f, 1f) : new Vector2(1f, 0f);
        bar.pivot     = top ? new Vector2(0.5f, 1f) : new Vector2(0.5f, 0f);
        bar.anchoredPosition = Vector2.zero;
        bar.sizeDelta = new Vector2(0f, 0f);
        return bar;
    }

    // Drive the two bar heights from barReveal (0 closed -> 1 full cinematic height).
    void ApplyBars()
    {
        float h = BarHeight * Mathf.Clamp01(barReveal);
        if (barTop != null)    barTop.sizeDelta    = new Vector2(0f, h);
        if (barBottom != null) barBottom.sizeDelta = new Vector2(0f, h);
    }

    // =================================================================================================
    // RUN — the sequential beat machine
    // =================================================================================================

    IEnumerator Run()
    {
        // Letterbox in over the live frame.
        yield return Envelope(BarsIn, v => { barReveal = v; ApplyBars(); });

        for (int i = 0; i < Beats.Length; i++)
        {
            // Skip stage 1: leap straight to the verb beat (the last one).
            if (skipStage >= 1 && i < Beats.Length - 1) continue;

            yield return PlayBeat(Beats[i], i);

            // Skip stage 2: a second tap during/after a beat begins teardown immediately.
            if (skipStage >= 2) break;

            if (i < Beats.Length - 1)
                yield return Wait(InterBeatGap);
        }

        // Retract backdrop + letterbox together, then hand off and self-destruct.
        teardown = true;
        yield return Envelope(BarsOut, v =>
        {
            float e = 1f - v;
            barReveal = e; ApplyBars();
            if (backdropGroup != null) backdropGroup.alpha = e;
            if (hintGroup != null) hintGroup.alpha = Mathf.Min(hintGroup.alpha, e);
        });

        StorySequencer.NotifyIntroComplete();   // releases the gameplay HUD
        StorySequencer.ReleaseStage(this);
        Destroy(gameObject);
    }

    IEnumerator PlayBeat(Beat b, int index)
    {
        // Configure the reused label for this beat.
        if (beatLabel != null)
        {
            switch (b.kind)
            {
                case Kind.Title:
                    beatLabel.text = LobbyUI.Spaced(b.text);
                    beatLabel.fontSize = 64;
                    beatLabel.color = LobbyUI.EmberSoft;
                    StorySequencer.Apply(beatLabel, StorySequencer.Weight.CondensedSemiBold);
                    break;
                case Kind.Verb:
                    beatLabel.text = b.text;
                    beatLabel.fontSize = 34;
                    beatLabel.color = LobbyUI.Ember;
                    StorySequencer.Apply(beatLabel, StorySequencer.Weight.SemiBold);
                    break;
                default:
                    beatLabel.text = b.text;
                    beatLabel.fontSize = 30;
                    beatLabel.color = LobbyUI.AshText;
                    StorySequencer.Apply(beatLabel, StorySequencer.Weight.Medium);
                    break;
            }
        }

        // Fade in.
        yield return Envelope(b.fadeIn, v => { if (beatGroup != null) beatGroup.alpha = v; });

        // Reveal the skip hint once the first beat is up.
        if (index == 0 && hintGroup != null) hintGroup.alpha = 1f;

        // Hold (interruptible: a fresh skip request cuts the hold short so input feels responsive).
        float held = 0f;
        int skipAtEntry = skipStage;
        while (held < b.hold)
        {
            if (skipStage > skipAtEntry) break;   // a new tap arrived during the hold
            held += Time.deltaTime;
            yield return null;
        }

        // Fade out.
        yield return Envelope(b.fadeOut, v => { if (beatGroup != null) beatGroup.alpha = 1f - v; });
        if (beatGroup != null) beatGroup.alpha = 0f;
    }

    // A 0->1 envelope over `dur` seconds, eased, applied via `set`. dur<=0 snaps to 1.
    IEnumerator Envelope(float dur, System.Action<float> set)
    {
        if (dur <= 0f) { set?.Invoke(1f); yield break; }
        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            set?.Invoke(Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur)));
            yield return null;
        }
        set?.Invoke(1f);
    }

    IEnumerator Wait(float dur)
    {
        float t = 0f;
        while (t < dur && skipStage == 0) { t += Time.deltaTime; yield return null; }
    }

    // =================================================================================================
    // SKIP — beat-aware: tap 1 -> jump to the verb beat; tap 2 -> tear down. One key never nukes it all.
    // =================================================================================================

    void Update()
    {
        if (teardown) return;
        if (Input.anyKeyDown || Input.GetMouseButtonDown(0))
        {
            if (skipStage < 2) skipStage++;
        }
    }
}
