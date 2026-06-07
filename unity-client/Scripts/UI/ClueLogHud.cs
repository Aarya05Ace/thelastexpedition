// ClueLogHud.cs — THE LAST EXPEDITION "Intel" panel: a toggle log (Tab) of the clues you've pried out
// of the islanders, rendered as gathered-intel cards. Reads the live, RLS-gated clue_reveal table off
// GameManager.Conn — the client only ever receives facts the run has actually earned, so every row is
// safe to show verbatim.
//
// SELF-BOOTSTRAP like GameHud/KillHud: a [RuntimeInitializeOnLoadMethod] spawns a DontDestroyOnLoad host.
// Data wiring is deferred into GameManager.OnReady (which replays immediately for late forest-scene
// subscribers). To honor "register callbacks BEFORE subscribe (v2 backfill is immediate)" robustly, on
// ready we attach ClueReveal.OnInsert AND THEN replay ClueReveal.Iter(), de-duping by row Id so backfill
// + live never double-add.
//
// Attribution ("Greta:") is best-effort: clue_reveal carries no NPC FK, so we walk party_clue (clue_id ->
// clue.id) to find revealed_by_npc, then npc.npc_id -> display_name. If it can't be resolved, the card
// shows the fact alone — never throws, never blocks.
//
// Pure Unity UI via the LobbyUI factory. No emoji. No UnityEditor use -> no #if guards. Null-safe.
//
// Npc exposes a SpacetimeDB.Types.Vector3 field, and this file also uses UnityEngine.Vector2/Color UI
// literals; the alias below makes any bare Vector3 unambiguous per the project gotcha.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class ClueLogHud : MonoBehaviour
{
    // Panel geometry (left-anchored sheet, RDR2 journal feel).
    const float PanelW   = 560f;
    const float PanelH   = 720f;
    const float HeaderH  = 64f;
    const float CardGap  = 10f;
    const float CardPadX = 16f;
    const float Pad      = 20f;
    const float SlideX   = 40f;     // px the panel slides in from the left on open
    const float AnimDur  = 0.18f;   // open/close fade + slide time

    Canvas canvas;
    CanvasGroup group;
    RectTransform panel;
    RectTransform content;     // where cards stack
    Text emptyState;

    readonly HashSet<ulong> _seen = new();   // de-dupe clue_reveal rows by Id
    float _nextCardY;                          // running stack cursor (cards grow downward)
    int _cardCount;

    bool _open;
    float _anim;               // 0 = closed, 1 = open (eased)
    bool _bound;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("ClueLogHudHost");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<ClueLogHud>();
    }

    void Awake()
    {
        Build();
        // Defer data wiring until the subscription backfill is applied (replays for late subscribers).
        GameManager.OnReady += BindData;
    }

    void OnDestroy()
    {
        GameManager.OnReady -= BindData;
        var conn = SafeConn();
        if (_bound && conn != null)
        {
            try { conn.Db.ClueReveal.OnInsert -= OnClueRevealed; } catch { }
        }
    }

    // =================================================================================================
    // BUILD — canvas + the (hidden) journal panel
    // =================================================================================================

    void Build()
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 120;   // above GameHud (100), below KillHud (150)

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        gameObject.AddComponent<GraphicRaycaster>();

        group = gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;   // only blocks while open (so cards can scroll/be read)
        group.interactable = false;

        var root = (RectTransform)transform;

        // ---- Left-anchored sheet ----
        var sheet = LobbyUI.RoundedPanel(root, "CluePanel", LobbyUI.BgDeep, 16);
        panel = sheet.rectTransform;
        LobbyUI.Place(panel, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                      new Vector2(40f, 0f), new Vector2(PanelW, PanelH));
        LobbyUI.Border(panel, LobbyUI.EmberDim, 1f);

        // ---- Header ----
        var header = LobbyUI.ShadowLabel(panel, LobbyUI.Spaced("INTEL"), LobbyUI.HeaderSize,
                                         LobbyUI.EmberSoft, TextAnchor.MiddleLeft);
        var hrt = header.rectTransform;
        hrt.anchorMin = new Vector2(0f, 1f); hrt.anchorMax = new Vector2(1f, 1f);
        hrt.pivot = new Vector2(0f, 1f);
        hrt.anchoredPosition = new Vector2(Pad, -Pad);
        hrt.sizeDelta = new Vector2(-Pad * 2f, 30f);

        var sub = LobbyUI.ShadowLabel(panel, "what the island let slip", LobbyUI.HintSize,
                                      LobbyUI.AshDim, TextAnchor.MiddleLeft);
        var srt = sub.rectTransform;
        srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f);
        srt.pivot = new Vector2(0f, 1f);
        srt.anchoredPosition = new Vector2(Pad, -Pad - 28f);
        srt.sizeDelta = new Vector2(-Pad * 2f, 18f);

        // Hairline under the header.
        var rule = LobbyUI.Panel(panel, "Rule", LobbyUI.EmberDim);
        rule.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(rule, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                      new Vector2(Pad, -HeaderH), new Vector2(-Pad * 2f, 1f));

        // ---- Card content column (cards manually stacked downward from the top) ----
        var col = LobbyUI.Panel(panel, "Content", new Color(0, 0, 0, 0));
        col.GetComponent<Image>().raycastTarget = false;
        content = col;
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0f, 1f);
        content.anchoredPosition = new Vector2(0f, -HeaderH - 6f);
        content.sizeDelta = new Vector2(0f, PanelH - HeaderH - 6f);
        _nextCardY = 0f;

        // ---- Empty state ----
        emptyState = LobbyUI.ShadowLabel(content, "No intel yet. Talk to the islanders.",
                                         LobbyUI.BodySize, LobbyUI.AshDim, TextAnchor.UpperLeft);
        emptyState.horizontalOverflow = HorizontalWrapMode.Wrap;
        var ert = emptyState.rectTransform;
        ert.anchorMin = new Vector2(0f, 1f); ert.anchorMax = new Vector2(1f, 1f);
        ert.pivot = new Vector2(0f, 1f);
        ert.anchoredPosition = new Vector2(Pad, -8f);
        ert.sizeDelta = new Vector2(-Pad * 2f, 28f);
    }

    // =================================================================================================
    // DATA — register the callback, THEN replay the backfill (de-duped)
    // =================================================================================================

    void BindData()
    {
        if (_bound) return;
        var conn = SafeConn();
        if (conn == null) return;   // OnReady will fire again / we re-enter; stay safe.
        _bound = true;

        // Register FIRST so no live insert is missed, then absorb the already-applied backfill.
        conn.Db.ClueReveal.OnInsert += OnClueRevealed;

        try
        {
            foreach (var row in conn.Db.ClueReveal.Iter())
                AddCard(row);
        }
        catch { /* table briefly empty / not ready — replay is best-effort */ }
    }

    void OnClueRevealed(EventContext ctx, ClueReveal row) => AddCard(row);

    void AddCard(ClueReveal row)
    {
        if (row == null) return;
        if (!_seen.Add(row.Id)) return;   // already shown (backfill + live overlap)

        if (emptyState != null && emptyState.gameObject.activeSelf)
            emptyState.gameObject.SetActive(false);

        string fact = string.IsNullOrEmpty(row.FactText) ? "(an unspoken thing)" : row.FactText;
        string who  = ResolveSpeaker(row);   // "Greta" or "" if unknowable

        // ---- Card panel ----
        var card = LobbyUI.RoundedPanel(content, "Clue" + row.Id, LobbyUI.BgPanel, 10);
        card.raycastTarget = false;
        var crt = card.rectTransform;

        // Estimate height from wrapped fact length (manual stack; no layout group surprises).
        float innerW = PanelW - Pad * 2f - CardPadX * 2f;
        int   approxCharsPerLine = Mathf.Max(1, Mathf.FloorToInt(innerW / 8.4f)); // ~ body glyph advance
        int   lines = Mathf.Max(1, Mathf.CeilToInt((float)fact.Length / approxCharsPerLine));
        float factH = lines * 22f;
        bool  hasWho = !string.IsNullOrEmpty(who);
        float cardH = (hasWho ? 24f : 6f) + factH + 16f;

        crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(0f, 1f);
        crt.pivot = new Vector2(0f, 1f);
        crt.anchoredPosition = new Vector2(Pad, -_nextCardY);
        crt.sizeDelta = new Vector2(PanelW - Pad * 2f, cardH);
        LobbyUI.Border(crt, LobbyUI.EmberDim, 1f);

        float y = -8f;

        // Speaker chip (only when we could resolve it).
        if (hasWho)
        {
            var chip = LobbyUI.ShadowLabel(crt, who, LobbyUI.HintSize, LobbyUI.Ember, TextAnchor.UpperLeft);
            var chrt = chip.rectTransform;
            chrt.anchorMin = new Vector2(0f, 1f); chrt.anchorMax = new Vector2(1f, 1f);
            chrt.pivot = new Vector2(0f, 1f);
            chrt.anchoredPosition = new Vector2(CardPadX, y);
            chrt.sizeDelta = new Vector2(-CardPadX * 2f, 18f);
            y -= 22f;
        }

        // The earned fact, verbatim.
        var factLbl = LobbyUI.ShadowLabel(crt, fact, LobbyUI.BodySize, LobbyUI.AshText, TextAnchor.UpperLeft);
        factLbl.horizontalOverflow = HorizontalWrapMode.Wrap;
        factLbl.verticalOverflow = VerticalWrapMode.Truncate;
        var frt = factLbl.rectTransform;
        frt.anchorMin = new Vector2(0f, 1f); frt.anchorMax = new Vector2(1f, 1f);
        frt.pivot = new Vector2(0f, 1f);
        frt.anchoredPosition = new Vector2(CardPadX, y);
        frt.sizeDelta = new Vector2(-CardPadX * 2f, factH);

        _nextCardY += cardH + CardGap;
        _cardCount++;
    }

    // Best-effort "who said this": clue_reveal -> party_clue (by clue code via clue table) -> npc name.
    // Returns "" if nothing matches; callers must tolerate an empty speaker.
    string ResolveSpeaker(ClueReveal row)
    {
        var conn = SafeConn();
        if (conn == null || row == null || string.IsNullOrEmpty(row.ClueCode)) return "";

        try
        {
            // Find the clue.id whose code matches this reveal's clue_code.
            ulong clueId = 0;
            bool found = false;
            foreach (var c in conn.Db.Clue.Iter())
            {
                if (c != null && c.Code == row.ClueCode) { clueId = c.Id; found = true; break; }
            }
            if (!found) return "";

            // Find the party_clue edge for this clue in this run -> the NPC who revealed it.
            ulong npcId = 0;
            bool edge = false;
            foreach (var pc in conn.Db.PartyClue.Iter())
            {
                if (pc != null && pc.ClueId == clueId && pc.RunId == row.RunId)
                {
                    npcId = pc.RevealedByNpc; edge = true; break;
                }
            }
            if (!edge)
            {
                // Fall back to any edge for this clue (run filter may not line up on the client view).
                foreach (var pc in conn.Db.PartyClue.Iter())
                {
                    if (pc != null && pc.ClueId == clueId) { npcId = pc.RevealedByNpc; edge = true; break; }
                }
            }
            if (!edge) return "";

            var npc = conn.Db.Npc.NpcId.Find(npcId);
            return npc != null && !string.IsNullOrEmpty(npc.DisplayName) ? npc.DisplayName : "";
        }
        catch { return ""; }
    }

    static DbConnection SafeConn()
    {
        try { return GameManager.Conn; } catch { return null; }
    }

    // =================================================================================================
    // TOGGLE + ANIMATION
    // =================================================================================================

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab))
            _open = !_open;

        float target = _open ? 1f : 0f;
        _anim = Mathf.MoveTowards(_anim, target, Time.deltaTime / Mathf.Max(0.01f, AnimDur));
        float e = Mathf.SmoothStep(0f, 1f, _anim);

        if (group != null)
        {
            group.alpha = e;
            bool interactive = _open && e > 0.5f;
            if (group.blocksRaycasts != interactive)
            {
                group.blocksRaycasts = interactive;
                group.interactable = interactive;
            }
        }

        if (panel != null)
        {
            // Slide in from the left as it fades.
            float x = 40f - (1f - e) * SlideX;
            var p = panel.anchoredPosition;
            if (!Mathf.Approximately(p.x, x))
                panel.anchoredPosition = new Vector2(x, p.y);
        }
    }
}
