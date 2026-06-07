// AudioDirector.cs — THE LAST EXPEDITION self-bootstrapping audio bed.
//
// One persistent host (DontDestroyOnLoad) that runs the whole soundtrack with NO scene wiring and NO
// edits to any UI/rig/spawner file. It mirrors the proven self-bootstrap pattern of CombatSpawner and
// IntroCrawl ([RuntimeInitializeOnLoadMethod] -> new GameObject -> DontDestroyOnLoad).
//
// WHAT IT DOES
//   (a) AMBIENCE BED  — a soft forest-wind loop, always on once gameplay is live (low volume).
//   (b) STORY MUSIC   — an intense/cinematic cue that plays during story beats (the IntroCrawl cold open
//                       and any time SetStoryMode(true) is called). Loops.
//   (c) COMBAT/FIRE   — a tension drone / fire bed for gameplay. Loops. Ducked while story mode is on,
//                       brought up to full while gameplay is active and not in a story beat.
//   (d) STINGER       — one-shot transition sting played on the story->gameplay handoff (optional clip).
//
//   It DUCKS/CROSSFADES between story music and the gameplay bed instead of hard-cutting, so the
//   handoff from the cold open into play is smooth. Music fades down under story-beat dialogue/crawl.
//
// HOOKS (call from anywhere, all static, all null-safe):
//   AudioDirector.SetStoryMode(true/false)   // raise story music, duck the gameplay bed (and vice-versa)
//   AudioDirector.StoryMusicVolume = 0.8f     // master multiplier for story music   (0..1)
//   AudioDirector.GameplayBedVolume = 0.7f    // master multiplier for the combat bed (0..1)
//   AudioDirector.AmbienceVolume = 0.35f      // master multiplier for the ambience  (0..1)
//   AudioDirector.PlayStinger()               // fire the transition sting once, if present
//   AudioDirector.SetMuted(true/false)        // global mute toggle
//
// AUTO STORY MODE: with no external calls at all, it starts in story mode (the IntroCrawl plays at boot),
// then auto-drops to gameplay mode a few seconds after NetworkedWorld.GameplayActive flips true — so the
// soundtrack already "works" out of the box. Any explicit SetStoryMode() call takes over from there.
//
// BUILD-SAFE: pure UnityEngine, no UnityEditor, no SpacetimeDB types (-> no Vector3 alias needed), no
// emoji. Every clip is OPTIONAL: if a Resources/Audio file is missing, that source simply stays silent —
// it never logs an error and never throws. Multiple candidate filenames are probed per slot so whatever
// the user drops in (story_music / music / intense ...) is found.
//
// HOW TO ADD CLIPS: drop .wav (or .ogg/.mp3) files into
//   Assets/TombRush/Resources/Audio/
// using ANY of the accepted names below. Unity imports them as AudioClip automatically. They load at
// runtime via Resources.Load<AudioClip>("Audio/<name>") — NO file extension, NO leading "Resources/".
//
//   STORY MUSIC   : "story_music"  (also accepts: "music", "intense", "story", "theme", "main_theme")
//   GAMEPLAY BED  : "fire_loop"    (also accepts: "combat_drone", "combat", "drone", "tension", "gameplay_music")
//   AMBIENCE      : "ambience"     (also accepts: "ambient", "wind", "forest", "ambience_loop")
//   STINGER (1shot): "story_sting" (also accepts: "stinger", "sting", "transition")
//
// PRE-WIRED (already copied into Resources/Audio by setup, so it makes sound today):
//   story_music.wav   <- Assets/Audio/MUSIC/Forest_MUSICAL_01.wav      (main cinematic cue)
//   story_sting.wav   <- Assets/Audio/MUSIC/MUSFX_FlyHigh_01.wav       (musical transition sting)
//   combat_drone.wav  <- Assets/Audio/FX/Forest_LOWDRONE/lowdrone_new.wav (tension bed under firefight)
//   ambience.wav      <- Assets/Audio/AMB/WIND/Forest_WOODSWIND_01.wav  (loopable wind bed)
//   dread_stinger.wav <- Assets/Audio/FX/FOREST_START/Forest_CUT_START_2.wav (extra short stinger, optional)
//
//   (There are NO gunshot/weapon .wav anywhere in the project, so the "fire" bed is a tension drone, not
//    literal gunfire. If you add a real gunfire loop, name it fire_loop.wav and it overrides combat_drone.)

using UnityEngine;

public class AudioDirector : MonoBehaviour
{
    // ---- public mix controls (static so any system can poke them; all clamped, all null-safe) --------

    public static float StoryMusicVolume  = 0.85f;  // target ceiling for story music when in story mode
    public static float GameplayBedVolume = 0.70f;  // target ceiling for the combat/fire bed in gameplay
    public static float AmbienceVolume    = 0.12f;  // ambience is a quiet always-on bed once gameplay live
                                                    // (was 0.32 — the wind file is broadband hiss; kept far
                                                    // under the music so it reads as a faint bed, not static)

    // Music stays the audible bed during normal gameplay (not silenced after the cold open). This is the
    // fraction of StoryMusicVolume the real 55s cue holds at once we are in plain play, so the soundtrack
    // is always pleasant tonal music rather than only the wind hiss. (0..1 of StoryMusicVolume.)
    public static float GameplayMusicFloor = 0.45f;

    // crossfade speed between story <-> gameplay (volume units per second; ~1.4 s for a full fade)
    public const float FadeSpeed = 0.7f;

    // ---- internal state -----------------------------------------------------------------------------

    static AudioDirector _inst;
    static bool _muted;

    // Tri-state intent. Null = auto (driven by IntroCrawl/GameplayActive); true/false = explicit override.
    static bool? _storyOverride = null;

    AudioSource _story;     // (b) story music   — loops
    AudioSource _bed;       // (c) gameplay bed  — loops
    AudioSource _amb;       // (a) ambience      — loops
    AudioSource _oneShot;   // (d) stingers + alarm — one-shots

    AudioClip _alarmClip;   // (e) sharp crate-open alarm — PlayAlarm(); falls back to the stinger clip
    AudioClip _reloadClip;  // (f) magazine reload click-clack — PlayReload(); optional, no-op if absent

    // smoothed live gains (what the sources are actually at this frame)
    float _gStory, _gBed, _gAmb;

    // auto-mode timing: hold story music through the cold-open, then drift to gameplay
    float _autoStoryHold;          // seconds of story mode still owed at boot
    const float BootStoryHold = 7.0f; // ~ IntroCrawl FadeIn(1.4)+Hold(4.2)+FadeOut(1.6) ≈ 7.2s

    bool _gameplaySeen;            // latched once NetworkedWorld.GameplayActive first goes true
    bool _stingerFiredOnHandoff;  // play the transition sting exactly once at story->gameplay

    // candidate filenames probed under Resources/Audio/, first hit wins.
    static readonly string[] StoryNames    = { "story_music", "music", "intense", "story", "theme", "main_theme" };
    static readonly string[] BedNames      = { "fire_loop", "combat_drone", "combat", "drone", "tension", "gameplay_music" };
    static readonly string[] AmbienceNames = { "ambience", "ambient", "wind", "forest", "ambience_loop" };
    static readonly string[] StingerNames  = { "story_sting", "stinger", "sting", "transition", "dread_stinger" };
    // Sharp one-shot for the crate-open "they heard you" beat. dread_stinger (0.7s noisy hit) is the best
    // existing fit; falls back to the transition stinger clip in PlayAlarm() if none of these are present.
    static readonly string[] AlarmNames    = { "alarm", "crate_alarm", "alert", "dread_stinger", "stinger" };
    // Weapon reload one-shot (the mag swap "click-clack"). Optional: if none of these are present, PlayReload
    // is a silent no-op (the on-screen RELOADING text + lowered weapon still convey the reload).
    static readonly string[] ReloadNames   = { "reload", "mag", "gun_reload", "rifle_reload", "magazine" };

    // ---- self-bootstrap (no scene object, no edits to any other file) --------------------------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_inst != null) return;             // never double-spawn (scene reloads, re-entry)
        var go = new GameObject("AudioDirector");
        Object.DontDestroyOnLoad(go);
        _inst = go.AddComponent<AudioDirector>();
    }

    // ---- public API (static, null-safe even if the host hasn't built yet) ----------------------------

    /// <summary>Raise story music + duck the gameplay bed (true), or the reverse (false). Explicit calls
    /// override the automatic IntroCrawl/GameplayActive driver until process end.</summary>
    public static void SetStoryMode(bool on) => _storyOverride = on;

    /// <summary>Hand control back to the automatic story/gameplay driver.</summary>
    public static void ClearStoryMode() => _storyOverride = null;

    /// <summary>Global mute toggle (does not stop sources, just zeroes output).</summary>
    public static void SetMuted(bool muted) => _muted = muted;

    /// <summary>Fire the transition sting once (no-op if no stinger clip / no host yet).</summary>
    public static void PlayStinger()
    {
        var i = _inst;
        if (i == null || i._oneShot == null || i._oneShot.clip == null) return;
        try { i._oneShot.PlayOneShot(i._oneShot.clip, 1f); } catch { /* never throw from audio */ }
    }

    /// <summary>Sharp alarm sting — RescueMission fires this the instant the crate is broken open
    /// ("they heard"). Plays loud and clean over the bed. Falls back to the transition stinger clip if no
    /// dedicated alarm clip was dropped in, and is fully null-safe (no-op if no clip / no host yet).</summary>
    public static void PlayAlarm()
    {
        var i = _inst;
        if (i == null || i._oneShot == null) return;
        AudioClip clip = i._alarmClip != null ? i._alarmClip : i._oneShot.clip;
        if (clip == null) return;
        try { i._oneShot.PlayOneShot(clip, 1f); } catch { /* never throw from audio */ }
    }

    /// <summary>Weapon reload "click-clack" — PlayerCombat fires this on the rising edge of a reload. Plays
    /// over the bed at a moderate level. No-op if no reload clip was dropped into Resources/Audio, and fully
    /// null-safe (no-op if no host yet).</summary>
    public static void PlayReload()
    {
        var i = _inst;
        if (i == null || i._oneShot == null || i._reloadClip == null) return;
        try { i._oneShot.PlayOneShot(i._reloadClip, 0.9f); } catch { /* never throw from audio */ }
    }

    // ---- lifecycle ----------------------------------------------------------------------------------

    void Awake()
    {
        // Boot in story mode for the cold open, even before anyone calls SetStoryMode.
        _autoStoryHold = BootStoryHold;

        _story   = MakeSource("AudioDirector_Story",   loop: true,  startPlaying: false);
        _bed     = MakeSource("AudioDirector_Bed",     loop: true,  startPlaying: false);
        _amb     = MakeSource("AudioDirector_Ambience",loop: true,  startPlaying: false);
        _oneShot = MakeSource("AudioDirector_OneShot", loop: false, startPlaying: false);

        // Load whatever exists; missing slots simply stay null/silent.
        AssignClip(_story,   StoryNames);
        AssignClip(_bed,     BedNames);
        AssignClip(_amb,     AmbienceNames);
        AssignClip(_oneShot, StingerNames);

        // Dedicated crate-open alarm clip (optional). Loaded straight into a field, not a source, since the
        // _oneShot source plays it on demand via PlayAlarm(). Stays null if none present (PlayAlarm falls
        // back to the transition stinger clip), so it is always safe.
        _alarmClip = LoadFirstClip(AlarmNames);

        // Dedicated reload one-shot (optional). Played on demand via PlayReload(); stays null if none present,
        // in which case PlayReload no-ops silently (never throws, never logs).
        _reloadClip = LoadFirstClip(ReloadNames);

        // Start the loops at zero gain; Update fades them in as appropriate. Playing-at-zero is cheaper
        // and glitch-free vs. starting mid-game, and harmless if the clip is null (Play() no-ops).
        SafePlay(_story);
        SafePlay(_bed);
        SafePlay(_amb);
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime; // keep audio fading even if Time.timeScale is paused/slowed

        // ----- resolve "are we in a story beat?" ------------------------------------------------------
        bool gameplay = false;
        try { gameplay = NetworkedWorld.GameplayActive; } catch { gameplay = false; }
        if (gameplay) _gameplaySeen = true;

        // Burn the cold-open hold ONLY once gameplay has actually been seen, so a slow lobby/connect cannot
        // expire the intro cue before play even begins (Fix D).
        if (_gameplaySeen && _autoStoryHold > 0f) _autoStoryHold -= dt;

        bool autoStory;
        if (!_gameplaySeen)
        {
            // Pre-gameplay (connecting / lobby / cold open): keep the cinematic up.
            autoStory = true;
        }
        else
        {
            // Once gameplay is live, stay in story mode only for the brief cold-open hold, then drop to play.
            autoStory = _autoStoryHold > 0f;
        }

        bool storyMode = _storyOverride ?? autoStory;

        // Fire the transition sting exactly once, the first time we leave story mode into live gameplay.
        if (!storyMode && _gameplaySeen && !_stingerFiredOnHandoff)
        {
            _stingerFiredOnHandoff = true;
            PlayStinger();
        }

        // ----- target gains ---------------------------------------------------------------------------
        // Ambience only once gameplay is reachable (silent during pure cold open for a cleaner intro).
        float ambTarget   = (_gameplaySeen ? Clamp01(AmbienceVolume) : 0f);
        // The real 55s cinematic cue is the bed: full while in a story beat, then a reduced-but-clearly-
        // audible floor during plain gameplay (Fix B) so the player always hears music, never just wind.
        float storyTarget = storyMode
            ? Clamp01(StoryMusicVolume)
            : Clamp01(StoryMusicVolume) * Clamp01(GameplayMusicFloor);
        // The gameplay bed lives under play; ducked (not silenced) during story beats so it never pops in.
        float bedTarget   = _gameplaySeen
            ? (storyMode ? Clamp01(GameplayBedVolume) * 0.15f : Clamp01(GameplayBedVolume))
            : 0f;

        if (_muted) { ambTarget = storyTarget = bedTarget = 0f; }

        // ----- smooth + apply -------------------------------------------------------------------------
        _gStory = MoveTo(_gStory, storyTarget, FadeSpeed * dt);
        _gBed   = MoveTo(_gBed,   bedTarget,   FadeSpeed * dt);
        _gAmb   = MoveTo(_gAmb,   ambTarget,   FadeSpeed * dt);

        Apply(_story, _gStory);
        Apply(_bed,   _gBed);
        Apply(_amb,   _gAmb);
    }

    // ---- helpers (every one null-safe) --------------------------------------------------------------

    AudioSource MakeSource(string name, bool loop, bool startPlaying)
    {
        var child = new GameObject(name);
        child.transform.SetParent(transform, false);
        var src = child.AddComponent<AudioSource>();
        src.playOnAwake = startPlaying;
        src.loop = loop;
        src.spatialBlend = 0f;   // 2D — this is a non-diegetic soundtrack bed, not positional SFX
        src.volume = 0f;
        src.priority = 64;       // music priority band; well above default SFX churn
        return src;
    }

    // Probe candidate names under Resources/Audio/, assign the first AudioClip found. Silent if none.
    static void AssignClip(AudioSource src, string[] names)
    {
        if (src == null || names == null) return;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.IsNullOrEmpty(names[i])) continue;
            AudioClip c = null;
            try { c = Resources.Load<AudioClip>("Audio/" + names[i]); } catch { c = null; }
            if (c != null) { src.clip = c; return; }
        }
        // No clip found -> src.clip stays null -> Play() no-ops, source stays silent. No error, no throw.
    }

    // Probe candidate names under Resources/Audio/, return the first AudioClip found (or null). Null-safe.
    static AudioClip LoadFirstClip(string[] names)
    {
        if (names == null) return null;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.IsNullOrEmpty(names[i])) continue;
            AudioClip c = null;
            try { c = Resources.Load<AudioClip>("Audio/" + names[i]); } catch { c = null; }
            if (c != null) return c;
        }
        return null;
    }

    static void SafePlay(AudioSource src)
    {
        if (src == null || src.clip == null) return;
        try { if (!src.isPlaying) src.Play(); } catch { /* never throw from audio */ }
    }

    static void Apply(AudioSource src, float vol)
    {
        if (src == null) return;
        src.volume = Clamp01(vol);
    }

    static float MoveTo(float cur, float target, float maxDelta)
    {
        if (maxDelta <= 0f) return cur;
        if (cur < target) { cur += maxDelta; if (cur > target) cur = target; }
        else if (cur > target) { cur -= maxDelta; if (cur < target) cur = target; }
        return cur;
    }

    static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
