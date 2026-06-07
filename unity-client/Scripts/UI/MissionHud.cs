// MissionHud.cs — THE LAST EXPEDITION objective tracker (Act I). RDR2 / The Last of Us style:
// a small top-left stack — an eyebrow act label, a terse second-person objective line, a dim subtext,
// and a thin breach meter. The objective is DRIVEN LIVE by the cloud clue-graph: it watches every new
// clue_reveal row (edge-trigger) plus world_state, runs a priority state-machine over the set of clue
// codes earned so far, and updates the line as new clues arrive. No emoji. Atmospheric, terse, gentle.
//
// SELF-BOOTSTRAP (like GameHud/KillHud): a single [RuntimeInitializeOnLoadMethod] spawns a persistent
// host, builds the canvas, then wires its data through GameManager.OnReady. OnReady fires once during the
// lobby AND replays immediately for late forest-scene subscribers (its custom accessor invokes the handler
// at once if already Ready), so binding is order-independent. To honor "callbacks BEFORE subscribe (v2
// backfill is immediate)" robustly, the bind step (a) registers OnInsert/OnUpdate, then (b) replays the
// already-applied rows via Iter() — so a HUD that wakes after backfill still sees prior clues.
//
// Imports: UnityEngine + UnityEngine.UI + LobbyUI + SpacetimeDB(.Types). Reads ClueReveal / WorldState /
// PartyClue only — none expose a UnityEngine.Vector3 we touch -> NO Vector3 alias needed. Fully null-safe:
// never throws if a table is briefly empty (Iter on empty = empty; world_state read is first-or-default).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB;
using SpacetimeDB.Types;

public class MissionHud : MonoBehaviour
{
    // ---- layout ----
    const float PanelW   = 460f;   // objective column width (text wraps within)
    const float MarginX  = 42f;    // inset from the left edge
    const float MarginY  = 42f;    // inset from the top edge
    const float MeterW   = 220f;   // breach meter track width
    const float MeterH   = 3f;     // breach meter track height (thin)
    const float PunchDur = 0.45f;  // fade-punch length when the objective line changes

    // ---- widgets ----
    Text actLabel;        // "ACT I" eyebrow
    Text objLine;         // primary objective (bold)
    Text subLine;         // secondary subtext (dim)
    RectTransform meterTrack;
    RectTransform meterFill;

    // ---- state ----
    readonly HashSet<string> _codes = new();   // clue codes earned so far (drives the machine)
    int _partyCount;                            // party_clue rows seen (gathered counter, optional flavor)
    bool _bound;
    string _lastObjective = "";                 // detect objective changes for the fade-punch
    float _punchT;                              // remaining punch time (alpha envelope on a change)
    CanvasGroup _objGroup;

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
        // invokes immediately if already Ready, so this is order-independent.
        GameManager.OnReady += hud.BindData;
    }

    void OnDestroy()
    {
        GameManager.OnReady -= BindData;
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
        catch { /* connection may already be gone — never throw on teardown */ }
    }

    // =================================================================================================
    // BUILD — canvas + the corner stack (called once, before BindData)
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
        var group = gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;   // the objective never eats clicks
        group.interactable = false;

        var root = (RectTransform)transform;

        // A transparent top-left column anchor; children stack downward from its top.
        var col = LobbyUI.Panel(root, "MissionColumn", new Color(0, 0, 0, 0));
        col.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(col, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                      new Vector2(MarginX, -MarginY), new Vector2(PanelW, 150f));

        // The objective group (eyebrow + line + subtext) gets a CanvasGroup so we can fade-punch it.
        var objHost = LobbyUI.Panel(col, "ObjectiveGroup", new Color(0, 0, 0, 0));
        objHost.GetComponent<Image>().raycastTarget = false;
        objHost.anchorMin = new Vector2(0f, 1f); objHost.anchorMax = new Vector2(0f, 1f);
        objHost.pivot = new Vector2(0f, 1f);
        objHost.anchoredPosition = Vector2.zero;
        objHost.sizeDelta = new Vector2(PanelW, 130f);
        _objGroup = objHost.gameObject.AddComponent<CanvasGroup>();
        _objGroup.blocksRaycasts = false; _objGroup.interactable = false;

        // Eyebrow: "ACT I" — small ember-dim caps, the RDR2 chapter feel.
        actLabel = LobbyUI.ShadowLabel(objHost, LobbyUI.Spaced("ACT I"), LobbyUI.HintSize,
                                       LobbyUI.EmberDim, TextAnchor.UpperLeft);
        Stack(actLabel.rectTransform, 0f, 18f);

        // Objective line: bold, ash, the primary state-machine string.
        objLine = LobbyUI.ShadowLabel(objHost, "", LobbyUI.HeaderSize, LobbyUI.AshText, TextAnchor.UpperLeft);
        objLine.horizontalOverflow = HorizontalWrapMode.Wrap;
        objLine.verticalOverflow = VerticalWrapMode.Overflow;
        Stack(objLine.rectTransform, -22f, 56f);

        // Subtext: dim, smaller, the supporting whisper.
        subLine = LobbyUI.ShadowLabel(objHost, "", LobbyUI.BodySize, LobbyUI.AshDim, TextAnchor.UpperLeft);
        subLine.fontStyle = FontStyle.Normal;
        subLine.horizontalOverflow = HorizontalWrapMode.Wrap;
        subLine.verticalOverflow = VerticalWrapMode.Overflow;
        Stack(subLine.rectTransform, -78f, 44f);

        // Breach meter: a thin track + a left-anchored ember fill, shown only when breach_progress > 0.
        var track = LobbyUI.RoundedPanel(objHost, "BreachTrack", LobbyUI.BgPanel, 2);
        track.raycastTarget = false;
        meterTrack = track.rectTransform;
        meterTrack.anchorMin = new Vector2(0f, 1f); meterTrack.anchorMax = new Vector2(0f, 1f);
        meterTrack.pivot = new Vector2(0f, 1f);
        meterTrack.anchoredPosition = new Vector2(2f, -124f);
        meterTrack.sizeDelta = new Vector2(MeterW, MeterH);

        var fill = LobbyUI.RoundedPanel(meterTrack, "BreachFill", LobbyUI.Ember, 2);
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

    // Anchor a label at the top-left of the objective column, offset down by |y|, with a fixed height.
    static void Stack(RectTransform rt, float y, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(0f, y);
        rt.sizeDelta = new Vector2(PanelW, h);
    }

    // =================================================================================================
    // BIND — register callbacks BEFORE replaying the immediate backfill (v2 backfill is synchronous)
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
    // STATE MACHINE — clue codes (+ world_state) -> objective line + subtext (highest match wins)
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
                break;   // singleton — first row is the world
            }
        }

        // --- eyebrow: act label, plus a subtle "N gathered" once intel exists ---
        string eyebrow = "ACT " + Roman(act);
        if (_codes.Count > 0) eyebrow += "   ·   " + _codes.Count + " gathered";   // middot separator
        if (actLabel != null) actLabel.text = LobbyUI.Spaced(eyebrow);

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
            // DF_SERVICE_DOOR_OPENABLE produced — the deduction is complete; go.
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
            // The way in is known to be locked by a keypad — the maid holds the code.
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
            objective = "She's alive — an upper-east window. Find a way inside.";
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
            // Default opening objective — no clues yet.
            objective = "Find someone who has seen her.";
            subtext   = "The forest hides survivors. Make them talk — gently.";
        }

        if (objLine != null) objLine.text = objective;
        if (subLine != null) subLine.text = subtext;

        // --- breach meter: show + size only when there's progress ---
        if (meterTrack != null)
        {
            bool show = breach > 0.001f;
            if (meterTrack.gameObject.activeSelf != show) meterTrack.gameObject.SetActive(show);
            if (show && meterFill != null)
            {
                float w = Mathf.Max(0f, MeterW * Mathf.Clamp01(breach));
                meterFill.sizeDelta = new Vector2(w, 0f);
            }
        }

        // --- fade-punch the objective group when the primary line changes (reads as an event) ---
        if (objective != _lastObjective)
        {
            _lastObjective = objective;
            _punchT = PunchDur;   // Update drives the alpha envelope from 0 -> 1
        }
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
    }
}
