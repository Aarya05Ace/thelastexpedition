// MissionHud.cs , THE LAST EXPEDITION objective tracker (Act I). RDR2 / The Last of Us style:
// a small top-left stack , an eyebrow act label, a terse second-person objective line, a dim subtext,
// and a thin breach meter. The objective is DRIVEN LIVE by the cloud clue-graph: it watches every new
// clue_reveal row (edge-trigger) plus world_state, runs a priority state-machine over the set of clue
// codes earned so far, and updates the line as new clues arrive. No emoji. Atmospheric, terse, gentle.
//
// SELF-BOOTSTRAP (like GameHud/KillHud): a single [RuntimeInitializeOnLoadMethod] spawns a persistent
// host, builds the canvas, then wires its data through GameManager.OnReady. OnReady fires once during the
// lobby AND replays immediately for late forest-scene subscribers (its custom accessor invokes the handler
// at once if already Ready), so binding is order-independent. To honor "callbacks BEFORE subscribe (v2
// backfill is immediate)" robustly, the bind step (a) registers OnInsert/OnUpdate, then (b) replays the
// already-applied rows via Iter() , so a HUD that wakes after backfill still sees prior clues.
//
// Imports: UnityEngine + UnityEngine.UI + LobbyUI + SpacetimeDB(.Types). Reads ClueReveal / WorldState /
// PartyClue only , none expose a UnityEngine.Vector3 we touch -> NO Vector3 alias needed. Fully null-safe:
// never throws if a table is briefly empty (Iter on empty = empty; world_state read is first-or-default).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB;
using SpacetimeDB.Types;

public class MissionHud : MonoBehaviour
{
    // ---- layout ----
    // Top-center banner so the objective reads on ANY background (bright forest, dark crate) and never
    // fights the top-left Intel panel. A wide column, centered children, on a dark backing panel.
    const float PanelW   = 760f;   // objective banner width (text wraps within), centered at the top
    const float MarginX  = 56f;    // (kept for reference; the banner is centered, not left-inset)
    const float MarginY  = 40f;    // inset from the top edge
    const float MeterW   = 260f;   // breach meter track width
    const float MeterH   = 3f;     // breach meter track height (a hairline, slightly thicker to read)
    const float PunchDur = 0.7f;   // fade-punch length when the objective line changes
    const float RevealDur= 0.6f;   // fade the whole tracker in when the intro completes

    // ---- type scale (BIG + readable on a photoreal forest) ----
    const int   EyebrowSize   = 18;   // "ACT I" eyebrow (up from HintSize 13)
    const int   ObjectiveSize = 34;   // primary objective line (up from 20)
    const int   SubSize       = 20;   // supporting subtext (up from BodySize 16)
    const float ColH          = 200f; // banner column height (room for the larger stack)
    const float BackPadX      = 26f;  // backing-panel horizontal padding around the text block
    const float BackPadTop    = 14f;  // backing-panel top padding
    const float BackPadBot    = 18f;  // backing-panel bottom padding

    // ---- widgets ----
    Text actLabel;        // "ACT I" eyebrow
    Text objLine;         // primary objective (big, white, outlined)
    Text subLine;         // secondary subtext (dim)
    RectTransform meterTrack;
    RectTransform meterFill;
    RectTransform _backing; // semi-transparent dark panel BEHIND the text so it is never invisible

    // ---- state ----
    readonly HashSet<string> _codes = new();   // clue codes earned so far (drives the machine)
    int _partyCount;                            // party_clue rows seen (gathered counter, optional flavor)
    bool _bound;
    string _lastObjective = "";                 // detect objective changes for the fade-punch
    float _punchT;                              // remaining punch time (alpha envelope on a change)
    CanvasGroup _objGroup;
    CanvasGroup _rootGroup;                      // gates the WHOLE tracker on intro-complete
    float _revealT;                             // remaining reveal-fade time (0 -> 1 on intro-complete)
    bool _revealed;                             // true once the intro has released us
    RectTransform _underline;                   // 1px ember wipe under the objective on a change
    float _wipeT;                               // remaining underline-wipe time

    // =================================================================================================
    // SELF-BOOTSTRAP
    // =================================================================================================

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("MissionHud");
        Object.DontDestroyOnLoad(host);
        var hud = host.AddComponent<MissionHud>();
        hud.Build();
        // Defer data wiring to OnReady so it works for the late forest-scene subscriber case. OnReady
        // invokes immediately if already Ready, so this is order-independent. Data can compute the moment
        // it arrives; only the VISIBILITY waits for the intro (StorySequencer.OnIntroComplete below).
        GameManager.OnReady += hud.BindData;
        StorySequencer.OnIntroComplete += hud.OnIntroComplete;
        // SHARED API: the hardcoded Mission-1 scripted objective. Each phase change repaints the line. This
        // is the PRIMARY source for the objective text; the cloud clue-graph is only a fallback (Recompute).
        RescueMission.OnPhaseChanged += hud.OnRescuePhaseChanged;
    }

    // Gate release: fade the (already-correct) tracker in once the cold open clears. Fires immediately if
    // the intro is already done (late bootstrap / scene reload), mirroring GameManager.OnReady , in that
    // case Build() already set us to alpha 1, so skip the redundant re-fade (no flash).
    void OnIntroComplete()
    {
        if (_revealed && _rootGroup != null && _rootGroup.alpha >= 1f) return;
        _revealed = true;
        _revealT = RevealDur;
    }

    // The hardcoded mission advanced a phase: repaint the objective line (drives the fade-punch + wipe).
    void OnRescuePhaseChanged() => Recompute();

    void OnDestroy()
    {
        GameManager.OnReady -= BindData;
        StorySequencer.OnIntroComplete -= OnIntroComplete;
        try { RescueMission.OnPhaseChanged -= OnRescuePhaseChanged; } catch { /* never throw on teardown */ }
        // Best-effort detach so a teardown + re-bootstrap doesn't double-fire into a dead instance.
        try
        {
            var db = GameManager.Conn?.Db;
            if (db != null && _bound)
            {
                db.ClueReveal.OnInsert -= OnClueRevealed;
                db.WorldState.OnInsert -= OnWorldInsert;
                db.WorldState.OnUpdate -= OnWorldUpdate;
                db.PartyClue.OnInsert -= OnPartyClue;
            }
        }
        catch { /* connection may already be gone , never throw on teardown */ }
    }

    // =================================================================================================
    // BUILD , canvas + the corner stack (called once, before BindData)
    // =================================================================================================

    void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 110;   // above GameHud (100), below KillHud hitmarkers (150)

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        gameObject.AddComponent<GraphicRaycaster>();
        _rootGroup = gameObject.AddComponent<CanvasGroup>();
        _rootGroup.blocksRaycasts = false;   // the objective never eats clicks
        _rootGroup.interactable = false;
        // GATED: build hidden. The intro owns the screen first; StorySequencer.OnIntroComplete fades us in.
        // If the intro already finished (late bootstrap), reveal immediately so we are never stranded.
        _revealed = StorySequencer.IntroComplete;
        _rootGroup.alpha = _revealed ? 1f : 0f;

        var root = (RectTransform)transform;

        // A transparent top-CENTER column anchor; children stack downward from its top, centered.
        // Centered reads on any background and never collides with the top-left Intel panel.
        var col = LobbyUI.Panel(root, "MissionColumn", new Color(0, 0, 0, 0));
        col.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(col, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                      new Vector2(0f, -MarginY), new Vector2(PanelW, ColH));

        // BACKING PANEL (the key legibility fix): a ~72%-opaque dark rounded panel behind the text so a
        // big white objective always reads over bright sunlit foliage. Added FIRST (lowest sibling index)
        // so it renders BEHIND the eyebrow / objective / subtext. Sized live in Update() to hug the text.
        var back = LobbyUI.RoundedPanel(col, "ObjectiveBacking", new Color(0.02f, 0.02f, 0.02f, 0.72f), 10);
        back.raycastTarget = false;
        _backing = back.rectTransform;
        _backing.anchorMin = new Vector2(0.5f, 1f); _backing.anchorMax = new Vector2(0.5f, 1f);
        _backing.pivot = new Vector2(0.5f, 1f);
        _backing.anchoredPosition = Vector2.zero;
        _backing.sizeDelta = new Vector2(PanelW, 120f);   // initial; Update() resizes to fit the text

        // The objective group (eyebrow + line + subtext) gets a CanvasGroup so we can fade-punch it.
        var objHost = LobbyUI.Panel(col, "ObjectiveGroup", new Color(0, 0, 0, 0));
        objHost.GetComponent<Image>().raycastTarget = false;
        objHost.anchorMin = new Vector2(0.5f, 1f); objHost.anchorMax = new Vector2(0.5f, 1f);
        objHost.pivot = new Vector2(0.5f, 1f);
        objHost.anchoredPosition = Vector2.zero;
        objHost.sizeDelta = new Vector2(PanelW, ColH);
        _objGroup = objHost.gameObject.AddComponent<CanvasGroup>();
        _objGroup.blocksRaycasts = false; _objGroup.interactable = false;

        // Eyebrow: "ACT I" , ember caps, the RDR2 chapter feel. Pure chapter label (the gathered counter
        // lives in the Intel panel now, not inline here , TLOU keeps the objective free of clutter).
        actLabel = LobbyUI.ShadowLabel(objHost, LobbyUI.Spaced("ACT I"), EyebrowSize,
                                       LobbyUI.Ember, TextAnchor.UpperCenter);
        StorySequencer.Apply(actLabel, StorySequencer.Weight.SemiBold);
        StackCenter(actLabel.rectTransform, -BackPadTop, 26f);

        // Objective line: BIG (34px), PURE WHITE, Barlow-SemiBold, with a hard 4-direction black outline
        // PLUS the drop shadow from ShadowLabel. This is what guarantees it is NEVER invisible.
        objLine = LobbyUI.ShadowLabel(objHost, "", ObjectiveSize, Color.white, TextAnchor.UpperCenter);
        StorySequencer.Apply(objLine, StorySequencer.Weight.SemiBold);
        objLine.horizontalOverflow = HorizontalWrapMode.Wrap;
        objLine.verticalOverflow = VerticalWrapMode.Overflow;
        // Hard outline on top of the drop shadow: reads on bright AND dark backgrounds.
        var ol = objLine.gameObject.AddComponent<UnityEngine.UI.Outline>();
        ol.effectColor = new Color(0f, 0f, 0f, 0.95f);
        ol.effectDistance = new Vector2(2.5f, -2.5f);
        ol.useGraphicAlpha = true;
        StackCenter(objLine.rectTransform, -(BackPadTop + 30f), 100f);

        // A thin ember underline that wipes center-out under the objective when it changes ("updated").
        var ul = LobbyUI.Panel(objHost, "ObjUnderline", LobbyUI.Ember);
        ul.GetComponent<Image>().raycastTarget = false;
        _underline = ul;
        _underline.anchorMin = new Vector2(0.5f, 1f); _underline.anchorMax = new Vector2(0.5f, 1f);
        _underline.pivot = new Vector2(0.5f, 1f);
        _underline.anchoredPosition = new Vector2(0f, -(BackPadTop + 30f + ObjectiveSize + 12f));
        _underline.sizeDelta = new Vector2(0f, 2f);

        // Subtext: Barlow-Regular, dim, the supporting whisper (now 20px, centered, still secondary).
        subLine = LobbyUI.ShadowLabel(objHost, "", SubSize, LobbyUI.AshText, TextAnchor.UpperCenter);
        StorySequencer.Apply(subLine, StorySequencer.Weight.Regular);
        subLine.color = LobbyUI.AshDim;   // keep the hierarchy: white objective, dim subtext
        subLine.horizontalOverflow = HorizontalWrapMode.Wrap;
        subLine.verticalOverflow = VerticalWrapMode.Overflow;
        StackCenter(subLine.rectTransform, -(BackPadTop + 30f + ObjectiveSize + 20f), 56f);

        // Breach meter: a thin ember-dim track + a centered ember fill, shown only when breach_progress > 0.
        var track = LobbyUI.RoundedPanel(objHost, "BreachTrack", LobbyUI.EmberDim, 1);
        track.raycastTarget = false;
        meterTrack = track.rectTransform;
        meterTrack.anchorMin = new Vector2(0.5f, 1f); meterTrack.anchorMax = new Vector2(0.5f, 1f);
        meterTrack.pivot = new Vector2(0.5f, 1f);
        meterTrack.anchoredPosition = new Vector2(0f, -(BackPadTop + 30f + ObjectiveSize + 56f));
        meterTrack.sizeDelta = new Vector2(MeterW, MeterH);

        var fill = LobbyUI.RoundedPanel(meterTrack, "BreachFill", LobbyUI.Ember, 1);
        fill.raycastTarget = false;
        meterFill = fill.rectTransform;
        meterFill.anchorMin = new Vector2(0f, 0f); meterFill.anchorMax = new Vector2(0f, 1f);
        meterFill.pivot = new Vector2(0f, 0.5f);
        meterFill.anchoredPosition = Vector2.zero;
        meterFill.sizeDelta = new Vector2(0f, 0f);
        meterTrack.gameObject.SetActive(false);   // hidden until breach begins

        // Set the initial (no-clue) objective so the HUD reads cleanly before any data arrives.
        Recompute();
    }

    // Anchor a label at the top-CENTER of the objective column, offset down by |y|, with a fixed height.
    static void StackCenter(RectTransform rt, float y, float h)
    {
        rt.anchorMin = new Vector2(0.5f, 1f); rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, y);
        rt.sizeDelta = new Vector2(PanelW - BackPadX * 2f, h);
    }

    // =================================================================================================
    // BIND , register callbacks BEFORE replaying the immediate backfill (v2 backfill is synchronous)
    // =================================================================================================

    void BindData()
    {
        if (_bound) return;   // OnReady can re-invoke for late subscribers; wire exactly once
        var conn = GameManager.Conn;
        if (conn == null) return;
        var db = conn.Db;
        if (db == null) return;
        _bound = true;

        // (a) Register edge-trigger callbacks first.
        db.ClueReveal.OnInsert += OnClueRevealed;
        db.WorldState.OnInsert += OnWorldInsert;
        db.WorldState.OnUpdate += OnWorldUpdate;   // world_state is a singleton -> updates, not inserts
        db.PartyClue.OnInsert += OnPartyClue;

        // (b) Replay the rows that backfill already applied (so a late HUD still sees prior clues).
        foreach (var r in db.ClueReveal.Iter())
            if (r != null && !string.IsNullOrEmpty(r.ClueCode)) _codes.Add(r.ClueCode);
        foreach (var _ in db.PartyClue.Iter()) _partyCount++;

        Recompute();
    }

    // ---- live callbacks (signatures per NetworkedWorld.cs: insert (ctx,row), update (ctx,old,new)) ----
    void OnClueRevealed(EventContext ctx, ClueReveal row)
    {
        if (row != null && !string.IsNullOrEmpty(row.ClueCode)) _codes.Add(row.ClueCode);
        Recompute();   // edge-trigger: re-evaluate the objective on every new clue
    }

    void OnWorldInsert(EventContext ctx, WorldState row) => Recompute();
    void OnWorldUpdate(EventContext ctx, WorldState oldRow, WorldState newRow) => Recompute();
    void OnPartyClue(EventContext ctx, PartyClue row) { _partyCount++; Recompute(); }

    // =================================================================================================
    // STATE MACHINE , clue codes (+ world_state) -> objective line + subtext (highest match wins)
    // =================================================================================================

    void Recompute()
    {
        // Read the world_state singleton null-safely (id=0 is the only row; first-or-default via Iter).
        uint act = 1;
        float breach = 0f;
        bool goalMet = false;
        var db = GameManager.Conn?.Db;
        if (db != null)
        {
            foreach (var w in db.WorldState.Iter())
            {
                if (w == null) continue;
                act = w.CurrentAct == 0 ? 1u : w.CurrentAct;
                breach = w.BreachProgress;
                goalMet = w.ActGoalMet;
                break;   // singleton , first row is the world
            }
        }

        // --- eyebrow: pure act/chapter label. The "N gathered" counter moved to the Intel panel header
        //     (gamey inline counters are exactly what TLOU strips off the objective). ---
        if (actLabel != null) actLabel.text = LobbyUI.Spaced("ACT " + Roman(act));

        // --- PRIORITY 0: the hardcoded scripted Mission-1 objective (SHARED API). When RescueMission has a
        //     non-empty objective line, it ALWAYS wins over the cloud clue-graph so Mission 1 always shows a
        //     real, on-rails objective even with an empty clue-graph. A phase-appropriate subtext supports
        //     it. This is the load-bearing integration; everything below is the fallback machine. ---
        string rescueObj = SafeRescueObjective();
        if (!string.IsNullOrEmpty(rescueObj))
        {
            string rescueSub = RescueSubtext();

            if (objLine != null) objLine.text = rescueObj;
            if (subLine != null) subLine.text = rescueSub;

            // Never let the tracker stay stranded at alpha 0: once a real scripted objective exists, force
            // the whole-tracker reveal (independent of the intro gate) so Mission 1 is always visible.
            // GATED: only self-reveal once gameplay is truly live and the cutscene is gone. The default phase
            // produces a non-empty Objective during auth/lobby, so without this guard Recompute() (which runs
            // at Build during the lobby) would flip _revealed early. Update() still hard-hides via alpha while
            // gated, but flipping reveal state on lobby-time data violates the render-only-when-gameplay
            // contract, so we gate the state change at its source.
            if (!_revealed && NetworkedWorld.GameplayActive && !DeployCutscene.Active)
            { _revealed = true; _revealT = RevealDur; }

            // Breach meter still follows world_state if it ever populates during the scripted mission.
            UpdateBreachMeter(breach);

            // Fade-punch + underline-wipe on a change, same as the clue path.
            PunchOnChange(rescueObj);
            return;
        }

        // --- objective state machine (top-down; first satisfied rule wins) ---
        bool hasKeycode  = _codes.Contains("CL_KEYCODE_DIGITS");
        bool hasGuardGap = _codes.Contains("CL_GUARD_GAP_LAUNDRY") || _codes.Contains("CL_DOG_FEEDING");
        bool hasFlicker  = _codes.Contains("CL_POWER_FLICKER");
        bool hasKeypad   = _codes.Contains("CL_KEYPAD_EXISTS");
        bool hasDoorLoc  = _codes.Contains("CL_SERVICE_DOOR_LOC");
        bool hasSighting = _codes.Contains("CL_SISTER_SEEN");

        string objective, subtext;

        if (goalMet)
        {
            // DF_SERVICE_DOOR_OPENABLE produced , the deduction is complete; go.
            objective = "Breach the service entrance.";
            subtext   = "The door opens on the gap. Move on his signal.";
        }
        else if (hasKeycode && hasGuardGap && hasFlicker)
        {
            // Full chain gathered, awaiting the server deduction / the window.
            objective = "You have it all. Wait for the window.";
            subtext   = "Door, code, the gap, the flicker. Time it.";
        }
        else if (hasKeycode)
        {
            // Code is known but the timing pieces (a gap and/or the flicker) are missing.
            objective = "Find the guard gap.";
            subtext   = "A patrol misses a beat. The drunk at the dock saw it.";
        }
        else if (hasKeypad && hasDoorLoc)
        {
            // The way in is known to be locked by a keypad , the maid holds the code.
            objective = "Earn the maid's trust. She knows the code.";
            subtext   = "Lower your voice. Promise her she's safe.";
        }
        else if (hasDoorLoc)
        {
            // A service door is located; learn what locks it.
            objective = "There's a way in. Learn what locks it.";
            subtext   = "A service door behind the laundry. Ask the handyman.";
        }
        else if (hasSighting)
        {
            // The escapee placed her alive at an upper-east window.
            objective = "She's alive. An upper-east window. Find a way inside.";
            subtext   = "Two days ago, behind glass. Find the service door.";
        }
        else if (_codes.Count > 0)
        {
            // Some intel, but nothing that advances the breach yet (e.g. forest trail, misleading bribe).
            objective = "Keep them talking. Every piece counts.";
            subtext   = "No one knows it all. Everyone knows a piece.";
        }
        else
        {
            // Default opening objective , no clues yet.
            objective = "Find someone who has seen her.";
            subtext   = "The forest hides survivors. Make them talk, gently.";
        }

        if (objLine != null) objLine.text = objective;
        if (subLine != null) subLine.text = subtext;

        UpdateBreachMeter(breach);
        PunchOnChange(objective);
    }

    // --- breach meter: show + size only when there's progress ---
    void UpdateBreachMeter(float breach)
    {
        if (meterTrack == null) return;
        bool show = breach > 0.001f;
        if (meterTrack.gameObject.activeSelf != show) meterTrack.gameObject.SetActive(show);
        if (show && meterFill != null)
        {
            float w = Mathf.Max(0f, MeterW * Mathf.Clamp01(breach));
            meterFill.sizeDelta = new Vector2(w, 0f);
        }
    }

    // --- fade-punch the objective group + wipe the underline when the primary line changes ---
    void PunchOnChange(string objective)
    {
        if (objective == _lastObjective) return;
        bool firstSet = _lastObjective.Length == 0;
        _lastObjective = objective;
        _punchT = PunchDur;                 // Update drives the alpha envelope from 0 -> 1
        if (!firstSet) _wipeT = PunchDur;   // underline wipes only on a real CHANGE, not the first paint
    }

    // Read the hardcoded scripted objective null-safely (RescueMission is owned by the mission script).
    static string SafeRescueObjective()
    {
        try { return RescueMission.Objective; }
        catch { return null; }
    }

    // A terse, phase-appropriate subtext for the scripted mission. No em dashes, no emoji. Second-person.
    // Switches on RescueMission.Phase by NAME (ToString) so it is robust whether RescuePhase is declared
    // nested on RescueMission or as a top-level enum , the two agents only contracted the member NAMES.
    static string RescueSubtext()
    {
        try
        {
            switch (RescueMission.Phase.ToString())
            {
                case "TrekToCabin": return "She is at the far end. Cut a path to her.";
                case "Reunion":     return "";   // the reunion conversation owns the screen here
                case "Escape":      return "Do not look back. Keep moving.";
                case "Confront":    return "He blocks the only way home.";
                case "BossFight":   return "Drop him and this ends.";
                case "Victory":     return "She is safe. You are going home.";
                default:            return "";
            }
        }
        catch { return ""; }
    }

    // Small roman numeral for the act eyebrow (1..4 cover the planned acts; falls back to the number).
    static string Roman(uint n)
    {
        switch (n)
        {
            case 1: return "I";
            case 2: return "II";
            case 3: return "III";
            case 4: return "IV";
            default: return n.ToString();
        }
    }

    void Update()
    {
        // IN-GAME GATE: this tracker must only exist once the player is really in the world. During auth/lobby
        // GameplayActive is false, and during the deploy cutscene DeployCutscene.Active is true, so this hard
        // hide keeps the ACT I banner off the lobby + How To Play. This beats the self-reveal override because
        // Update() is the only thing that writes _rootGroup.alpha to a visible value.
        if (!NetworkedWorld.GameplayActive || DeployCutscene.Active)
        {
            if (_rootGroup != null) _rootGroup.alpha = 0f;
            return;
        }

        // Whole-tracker reveal: fade alpha 0 -> 1 over RevealDur once the intro releases us. Stays hidden
        // (alpha 0) until then, so the cold open never has the objective stacked under it.
        if (_rootGroup != null)
        {
            if (_revealed && _revealT > 0f)
            {
                _revealT -= Time.deltaTime;
                _rootGroup.alpha = Mathf.Clamp01(1f - (_revealT / RevealDur));
            }
            else if (_revealed && _rootGroup.alpha != 1f) _rootGroup.alpha = 1f;
            else if (!_revealed && _rootGroup.alpha != 0f) _rootGroup.alpha = 0f;
        }

        // Objective fade-punch: alpha 0 -> 1 over PunchDur so a new objective announces itself, then rests.
        if (_objGroup != null)
        {
            if (_punchT > 0f)
            {
                _punchT -= Time.deltaTime;
                _objGroup.alpha = Mathf.Clamp01(1f - (_punchT / PunchDur));
            }
            else if (_objGroup.alpha != 1f)
            {
                _objGroup.alpha = 1f;
            }
        }

        // Underline wipe: width sweeps 0 -> 280px from the center out over PunchDur on an objective change.
        if (_underline != null)
        {
            if (_wipeT > 0f)
            {
                _wipeT -= Time.deltaTime;
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - (_wipeT / PunchDur)));
                _underline.sizeDelta = new Vector2(280f * e, 2f);
            }
        }

        // Backing panel: hug the actual (wrapped) text block so the dark plate is never too tall/short.
        // Spans from the eyebrow top down past the subtext (and the meter when shown). preferredHeight
        // reflects the wrapped layout, so a one- or two-line objective both get a snug plate.
        if (_backing != null)
        {
            float objH = (objLine != null) ? objLine.preferredHeight : ObjectiveSize * 1.2f;
            float subH = (subLine != null && !string.IsNullOrEmpty(subLine.text))
                         ? subLine.preferredHeight + 6f : 0f;
            // eyebrow + gap + objective + gap + subtext + (meter if active) + paddings.
            float content = 26f + 6f + objH + subH;
            if (meterTrack != null && meterTrack.gameObject.activeSelf) content += 14f;
            float h = BackPadTop + content + BackPadBot;
            float w = PanelW;
            if (objLine != null)
            {
                // Hug the widest line horizontally for short objectives, but never exceed the banner width.
                float widest = Mathf.Max(objLine.preferredWidth,
                                         (subLine != null) ? subLine.preferredWidth : 0f);
                w = Mathf.Clamp(widest + BackPadX * 2f, 280f, PanelW);
            }
            var s = _backing.sizeDelta;
            if (!Mathf.Approximately(s.x, w) || !Mathf.Approximately(s.y, h))
                _backing.sizeDelta = new Vector2(w, h);
        }
    }
}
