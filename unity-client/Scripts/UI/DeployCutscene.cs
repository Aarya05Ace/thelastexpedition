// DeployCutscene.cs - THE LOST EXPEDITION deploy cold-open. Plays intro_cutscene.mov full-screen
// (opaque black backdrop, letterboxed-fill via the VideoPlayer aspect) the instant DEPLOY is pressed,
// freezes the player and suppresses all HUD, then fades to the Barlow Condensed "THE LOST EXPEDITION"
// title card before handing off to the existing IntroCrawl. Skippable with Space or Esc.
//
// FLOW: DEPLOY (LobbyBootstrap.Launch) -> Play() sets Active=true + claims the StorySequencer stage ->
// full-screen video over black, player frozen, no HUD -> on video end OR Space/Esc -> crossfade to the
// title card (fade in 1.0s, hold 2.5s, fade out 1.1s) -> handoff: clear Active, release the stage, destroy.
// The held IntroCrawl gate then constructs the IntroCrawl, which claims the now-free stage and plays the
// existing premise crawl + MissionHud reveal. The cutscene + title come BEFORE the existing IntroCrawl.
//
// Pure UnityEngine + UnityEngine.Video + UnityEngine.UI. NO SpacetimeDB types -> no Vector3 alias needed.
// NO UnityEditor -> no #if guards. Reads the video by URL from StreamingAssets so the 28MB .mov is never
// imported as a VideoClip. Null-safe throughout (a missing/corrupt .mov skips straight to the title card).
// One static guard so it plays once per process. One class, one Update, no OnGUI. No em dashes, no emoji.

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

public class DeployCutscene : MonoBehaviour
{
    // ---- PLAYER-FREEZE FLAG: LocalPlayer.Update() early-returns on this. True from Play() until handoff. ----
    public static bool Active { get; private set; }

    static bool _played;   // once per process (a second DEPLOY in the same run never replays the video)

    const float TitleFadeIn  = 1.0f;
    const float TitleHold    = 2.5f;
    const float TitleFadeOut = 1.1f;
    const float VideoFadeOut = 0.5f;   // brief crossfade from video-end into the title card
    const float PrepTimeout  = 8f;     // give up waiting on Prepare() after this and go to the title

    VideoPlayer vp;
    RenderTexture rt;
    RawImage videoImage;       // full-screen, displays rt
    CanvasGroup videoGroup;    // fades the video out
    CanvasGroup titleGroup;    // fades the title card in/out
    bool skipRequested;
    bool advancing;            // latched once we leave the video phase (skip or end) so we advance once

    // A stable, process-lifetime token so the static ClaimStage in Play() (no instance yet) and the
    // instance's ReleaseStage refer to the SAME owner. ClaimStage/ReleaseStage take an object owner.
    static readonly object StageToken = new object();

    // Called from LobbyBootstrap.Launch(). onComplete is the DEFERRED SPAWN (player + world); it fires AFTER
    // the cutscene + title finish, so the player is never spawned into the firefight during the cutscene.
    public static void Play(System.Action onComplete = null)
    {
        if (_played) { onComplete?.Invoke(); return; }   // already played once -> still spawn, never lose the callback
        _played = true;
        _onComplete = onComplete;
        Active = true;   // freeze any input synchronously

        // Claim the stage NOW so the IntroCrawl gate (which polls GameplayActive) cannot start over us.
        StorySequencer.ClaimStage(StageToken);

        var host = new GameObject("DeployCutscene");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<DeployCutscene>();
    }

    static System.Action _onComplete;   // the deferred spawn; fired exactly once after the cutscene + title
    static void FireComplete()
    {
        var cb = _onComplete;
        _onComplete = null;   // null BEFORE invoke so it can never double-fire
        cb?.Invoke();
    }

    void Awake()
    {
        // Re-claim under the SAME token (idempotent: ClaimStage returns true for the current owner) so
        // OnDestroy can release cleanly even if Play()'s claim is the live one.
        StorySequencer.ClaimStage(StageToken);
        Build();
        StartCoroutine(Run());
    }

    // =================================================================================================
    // BUILD - top-most opaque overlay: black backdrop, full-screen video RawImage, hidden title card
    // =================================================================================================

    void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 400;   // above IntroCrawl (250), the death screen (200), all HUD

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        var rootGroup = gameObject.AddComponent<CanvasGroup>();
        rootGroup.blocksRaycasts = true;   // eat clicks so nothing behind reacts during the cutscene
        rootGroup.interactable = false;

        var root = (RectTransform)transform;

        // ---- BLACK BACKDROP covering EVERYTHING (behind the video, fills any letterbox bars) ----
        var black = LobbyUI.Panel(root, "Black", new Color(0f, 0f, 0f, 1f));
        StretchFull(black);
        black.GetComponent<Image>().raycastTarget = true;

        // ---- VIDEO RENDER TARGET sized to the screen so the .mov fills the display ----
        int rw = Mathf.Max(640, Screen.width);
        int rh = Mathf.Max(360, Screen.height);
        rt = new RenderTexture(rw, rh, 0, RenderTextureFormat.ARGB32);
        rt.Create();

        var videoHost = LobbyUI.Panel(root, "Video", new Color(0f, 0f, 0f, 0f));
        StretchFull(videoHost);
        videoHost.GetComponent<Image>().raycastTarget = false;
        videoGroup = videoHost.gameObject.AddComponent<CanvasGroup>();
        videoGroup.alpha = 1f;

        var imgGo = new GameObject("VideoImage", typeof(RectTransform));
        imgGo.transform.SetParent(videoHost, false);
        var irt = imgGo.GetComponent<RectTransform>();
        StretchFull(irt);
        videoImage = imgGo.AddComponent<RawImage>();
        videoImage.texture = rt;
        videoImage.raycastTarget = false;

        // ---- VideoPlayer reading the .mov by URL from StreamingAssets ----
        vp = gameObject.AddComponent<VideoPlayer>();
        vp.playOnAwake = false;
        vp.source = VideoSource.Url;
        vp.url = System.IO.Path.Combine(Application.streamingAssetsPath, "intro_cutscene.mov");
        vp.renderMode = VideoRenderMode.RenderTexture;
        vp.targetTexture = rt;
        vp.isLooping = false;
        vp.aspectRatio = VideoAspectRatio.FitInside;   // letterbox/fill safe; the black backdrop fills the bars
        vp.audioOutputMode = VideoAudioOutputMode.Direct;   // play the .mov's own audio track
        vp.waitForFirstFrame = true;

        // ---- TITLE CARD (hidden until the video ends or is skipped) ----
        var titleHost = LobbyUI.Panel(root, "Title", new Color(0f, 0f, 0f, 0f));
        StretchFull(titleHost);
        titleHost.GetComponent<Image>().raycastTarget = false;
        titleGroup = titleHost.gameObject.AddComponent<CanvasGroup>();
        titleGroup.alpha = 0f;

        var label = LobbyUI.ShadowLabel(titleHost, LobbyUI.Spaced("THE LOST EXPEDITION"),
                                        86, LobbyUI.EmberSoft, TextAnchor.MiddleCenter);
        var lrt = label.rectTransform;
        lrt.anchorMin = new Vector2(0.5f, 0.5f); lrt.anchorMax = new Vector2(0.5f, 0.5f);
        lrt.pivot = new Vector2(0.5f, 0.5f);
        lrt.anchoredPosition = Vector2.zero;
        lrt.sizeDelta = new Vector2(1600f, 220f);
        StorySequencer.Apply(label, StorySequencer.Weight.CondensedSemiBold);   // Barlow Condensed
    }

    static void StretchFull(RectTransform t)
    {
        t.anchorMin = Vector2.zero; t.anchorMax = Vector2.one;
        t.offsetMin = Vector2.zero; t.offsetMax = Vector2.zero;
    }

    // =================================================================================================
    // RUN - play the video, then the title card, then hand off to the existing IntroCrawl
    // =================================================================================================

    IEnumerator Run()
    {
        // ---- PHASE 1: prepare + play the video ----
        if (vp != null)
        {
            vp.loopPointReached += _ => advancing = true;     // natural end
            vp.errorReceived    += (_, __) => advancing = true; // missing/corrupt .mov -> skip to the title
            vp.Prepare();
        }
        else advancing = true;

        float prep = PrepTimeout;
        while (vp != null && !vp.isPrepared && prep > 0f && !skipRequested && !advancing)
        { prep -= Time.deltaTime; yield return null; }

        if (vp != null && vp.isPrepared && !skipRequested) vp.Play();
        else advancing = true;   // never prepared (no file / codec) -> straight to the title card

        // Hold on the video until it ends OR the user skips.
        while (!advancing && !skipRequested) yield return null;

        // ---- crossfade the video out ----
        yield return Fade(videoGroup, 1f, 0f, VideoFadeOut);
        if (vp != null) vp.Stop();

        // ---- PHASE 2: the title card ----
        skipRequested = false;   // reset so a skip during the hold cuts the title short
        yield return Fade(titleGroup, 0f, 1f, TitleFadeIn);

        float held = 0f;
        while (held < TitleHold && !skipRequested) { held += Time.deltaTime; yield return null; }
        bool titleSkip = skipRequested;

        yield return Fade(titleGroup, 1f, 0f, titleSkip ? 0.3f : TitleFadeOut);

        // ---- HANDOFF: spawn the player + world (deferred from Launch), THEN start the IntroCrawl ----
        Active = false;                          // unfreeze input
        FireComplete();                          // NOW spawn the player + world (GameplayActive + JoinForest)
        StorySequencer.ReleaseStage(StageToken); // release so IntroCrawl.IntroGate can ClaimStage and play
        Destroy(gameObject);
    }

    IEnumerator Fade(CanvasGroup g, float from, float to, float dur)
    {
        if (g == null) yield break;
        if (dur <= 0f) { g.alpha = to; yield break; }
        float t = 0f;
        while (t < dur) { t += Time.deltaTime; g.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(t / dur)); yield return null; }
        g.alpha = to;
    }

    void Update()
    {
        // Space or Esc skips: during the video it advances to the title; during the title it cuts it short.
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape))
        {
            skipRequested = true;
            advancing = true;
        }
    }

    void OnDestroy()
    {
        Active = false;
        FireComplete();   // fallback: if destroyed before the normal handoff, still spawn so launch never stalls
        StorySequencer.ReleaseStage(StageToken);
        if (vp != null) vp.Stop();
        if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
    }
}
