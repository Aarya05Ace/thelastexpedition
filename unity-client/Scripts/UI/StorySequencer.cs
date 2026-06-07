// StorySequencer.cs — THE LAST EXPEDITION single owner of "what is on screen now."
//
// One DontDestroyOnLoad singleton that arbitrates the story UI so beats never overlap each other or the
// gameplay HUD. It does three jobs:
//
//   1) FONT  — loads the Barlow OFL family at runtime via Resources.Load<Font>("Fonts/Barlow-*") with a
//              static per-weight cache and a LegacyRuntime fallback, then exposes Font(Weight) +
//              Apply(text, weight). This is fully build-safe (no UnityEditor, Resources only) and is the
//              reason the story UI finally reads in Barlow instead of LegacyRuntime.ttf.
//
//   2) GATE  — a static IntroComplete bool + an OnIntroComplete event, patterned EXACTLY on
//              GameManager.OnReady: subscribing AFTER the intro already finished fires the handler at once,
//              so subscription order never matters (the late-bootstrap / scene-reload case is covered).
//              IntroCrawl calls NotifyIntroComplete() at the very end (or on a full skip). MissionHud and
//              ClueLogHud (and any other HUD that opts in) stay hidden until this fires. This is the gate
//              that guarantees the cinematic title fully completes and clears BEFORE any gameplay HUD shows.
//
//   3) STAGE — a tiny exclusivity arbiter: at most ONE cinematic "owner" (the intro, a future cutscene)
//              holds the stage at a time. While a stage owner is active, the running HUDs treat themselves
//              as suppressed. This is the "single owner of what is on screen now" the brief asks for, kept
//              deliberately small and null-safe so a briefly-absent sequencer never crashes a caller.
//
// Pure UnityEngine. NO SpacetimeDB types -> no Vector3 alias. NO UnityEditor -> no #if guards. Null-safe:
// every accessor tolerates the sequencer not existing yet (HUDs read "not complete" -> stay hidden).

using System;
using UnityEngine;
using UnityEngine.UI;

public class StorySequencer : MonoBehaviour
{
    // =================================================================================================
    // SINGLETON
    // =================================================================================================

    public static StorySequencer Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var host = new GameObject("StorySequencer");
        UnityEngine.Object.DontDestroyOnLoad(host);
        Instance = host.AddComponent<StorySequencer>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // =================================================================================================
    // GATE — intro-complete, mirrors GameManager.OnReady's "invoke immediately if already done"
    // =================================================================================================

    static bool _introComplete;
    static Action _onIntroComplete;

    // True once the cold-open has finished or been fully skipped. HUDs read this to decide visibility.
    public static bool IntroComplete => _introComplete;

    // Fires ONCE when the intro completes. A subscriber that attaches AFTER completion is invoked at once
    // (exactly like GameManager.OnReady), so a HUD that bootstraps late / after a scene reload still shows.
    public static event Action OnIntroComplete
    {
        add { _onIntroComplete += value; if (_introComplete) value(); }
        remove { _onIntroComplete -= value; }
    }

    // Called by IntroCrawl on the last frame (natural end OR full skip), BEFORE it destroys itself.
    // Idempotent: a second call is ignored so a skip-then-end can never double-fire the HUD reveal.
    public static void NotifyIntroComplete()
    {
        if (_introComplete) return;
        _introComplete = true;
        _stageOwner = null;      // the intro relinquishes the stage as it completes
        try { _onIntroComplete?.Invoke(); }
        catch { /* a subscriber threw — never let one bad HUD block the others */ }
    }

    // =================================================================================================
    // STAGE — at most one cinematic owner at a time (single owner of "what is on screen now")
    // =================================================================================================

    static object _stageOwner;

    // True while ANY cinematic beat owns the screen (the intro today; a cutscene tomorrow). Running HUDs
    // can consult this to stay suppressed during a story moment even after the intro is done.
    public static bool StageBusy => _stageOwner != null;

    // Claim the stage for a cinematic owner. Returns false if another owner already holds it, so callers
    // never stomp a beat in progress (the brief's "beats never overlap each other" guarantee).
    public static bool ClaimStage(object owner)
    {
        if (owner == null) return false;
        if (_stageOwner != null && !ReferenceEquals(_stageOwner, owner)) return false;
        _stageOwner = owner;
        return true;
    }

    // Release the stage if (and only if) the caller is the current owner.
    public static void ReleaseStage(object owner)
    {
        if (owner != null && ReferenceEquals(_stageOwner, owner)) _stageOwner = null;
    }

    // =================================================================================================
    // FONT — Barlow family loaded once per weight from Resources/Fonts, LegacyRuntime fallback
    // =================================================================================================

    // The weights we ship. The enum value maps to the file stem under Resources/Fonts/.
    public enum Weight { Light, Regular, Medium, SemiBold, Bold, CondensedSemiBold }

    static readonly System.Collections.Generic.Dictionary<Weight, Font> _fontCache = new();
    static Font _fallback;

    static string Stem(Weight w)
    {
        switch (w)
        {
            case Weight.Light:             return "Barlow-Light";
            case Weight.Regular:           return "Barlow-Regular";
            case Weight.Medium:            return "Barlow-Medium";
            case Weight.SemiBold:          return "Barlow-SemiBold";
            case Weight.Bold:              return "Barlow-Bold";
            case Weight.CondensedSemiBold: return "BarlowCondensed-SemiBold";
            default:                       return "Barlow-Regular";
        }
    }

    // Resolve a Barlow weight at runtime. Cached. Falls back to LegacyRuntime/Arial so a missing TTF never
    // leaves a label fontless (which renders blank).
    public static Font GetFont(Weight w)
    {
        if (_fontCache.TryGetValue(w, out var f) && f != null) return f;

        Font loaded = null;
        try { loaded = Resources.Load<Font>("Fonts/" + Stem(w)); }
        catch { loaded = null; }

        if (loaded == null) loaded = Fallback();
        _fontCache[w] = loaded;
        return loaded;
    }

    static Font Fallback()
    {
        if (_fallback != null) return _fallback;
        try { _fallback = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (_fallback == null) { try { _fallback = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
        return _fallback;
    }

    // Re-skin an existing Text in a Barlow weight. Centralized so every story label routes through one
    // place. Leaves the label untouched (never throws) if it or the font is briefly null.
    public static void Apply(Text t, Weight w)
    {
        if (t == null) return;
        var f = GetFont(w);
        if (f != null) t.font = f;
        // Barlow already carries real weight in the glyphs; drop the faux-bold the LobbyUI factory adds so
        // the chosen weight reads cleanly (faux-bold on a real SemiBold looks muddy).
        t.fontStyle = FontStyle.Normal;
    }
}
