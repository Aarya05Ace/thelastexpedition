// LobbyHud.cs — THE LOST EXPEDITION cinematic lobby HUD (uGUI, built in code) — PART B/C/D.
//
// Fortnite-lobby-but-horror layout, built into the LOBBY LAYER transform (NOT the canvas root — else it
// renders under the auth card from frame 1):
//   HEADER (top, floating rounded): ember diamond + 'THE LOST EXPEDITION' left; LV + currency pill right.
//   TOP-CENTER LOBBY CODE panel: party gate (CREATE / JOIN BY CODE) when unpartied; CODE + n/6 + LEAVE
//     when in a party. Code rendered VERBATIM from my LobbyMember.PartyId (already canonical uppercase).
//   PODIUM CAPTION (bottom strip): DisplayName / Archetype / wrapped Blurb under < > arrows that drive
//     CharacterCarousel.Cycle(-1/+1).
//   BOTTOM-LEFT: INFO/MODE card + the BIG ember READY CTA (the dominant call-to-action) + countdown +
//     'AN EXPEDITION IS ALREADY IN PROGRESS' notice.
//   BOTTOM-RIGHT: chat ScrollRect (per-party rows; input gated behind PartyId!='') + a row of circular
//     CHAT/EMOTE/BACK buttons + a circular LV badge.
//
// PART C scoping: ready/launch is per-party (server-scoped); the launch caller is the LOWEST-Slot member
// AMONG MY PARTY (NOT global IsExpeditionLead). Chat renders only rows where c.PartyId==myPartyId.
//
// KEEPS all SpacetimeDB calls: ToggleReady (SetReady/Unready), SendChat, LaunchGame,
// RequestBillionaireDialogue, CreateParty/JoinParty/LeaveParty.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class LobbyHud : MonoBehaviour
{
    CharacterCarousel carousel;
    Transform layer;   // the lobby LAYER transform

    // Header
    Text headerLevel, headerCurrency;

    // Lobby code panel (party gate / in-party header)
    RectTransform codePanel;
    Text codeLabelTop, codeLabelBig, codeMembers;
    Button createBtn, joinBtn, leaveBtn;
    InputField joinInput;
    Text joinStatus;

    // Caption strip
    Text capName, capArchetype, capBlurb;
    Button capLeft, capRight;

    // Chat
    RectTransform chatContent;
    ScrollRect chatScroll;
    InputField chatInput;
    Button chatSendBtn;

    // Ready / countdown
    Button readyBtn;
    Text readyBtnLabel;
    Text countdownLabel;
    Text inProgressLabel;
    Button talkBtn;

    bool wired;

    // Local countdown state
    bool counting;
    float countdownEnd;
    const float CountdownSeconds = 8f;
    bool launchFired;

    public void Build(Canvas canvas, CharacterCarousel carouselRef)
    {
        // back-compat overload: build into the canvas root if no layer was supplied.
        Build(canvas.transform, carouselRef);
    }

    public void Build(Transform lobbyLayer, CharacterCarousel carouselRef)
    {
        layer = lobbyLayer;
        carousel = carouselRef;
        if (carousel != null) carousel.OnSelectionChanged += RefreshCharacterCaption;

        BuildHeader();
        BuildCodePanel();
        BuildCaptionStrip();
        BuildChat();
        BuildActions();
    }

    // ---- HEADER ----
    void BuildHeader()
    {
        var bar = LobbyUI.RoundedPanel(layer, "Header", LobbyUI.BgPanel, 14);
        LobbyUI.Place(bar.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -16f), new Vector2(0f, 72f));
        bar.rectTransform.offsetMin = new Vector2(16f, -88f); bar.rectTransform.offsetMax = new Vector2(-16f, -16f);
        LobbyUI.Border(bar.rectTransform, LobbyUI.EmberDim, 1f);

        var diamond = LobbyUI.RoundedPanel(bar.transform, "Diamond", LobbyUI.Ember, 4);
        LobbyUI.Place(diamond.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(34f, 0f), new Vector2(16f, 16f));
        diamond.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f);

        var title = LobbyUI.ShadowLabel(bar.transform, LobbyUI.Spaced("THE LOST EXPEDITION"),
            LobbyUI.HeaderSize, LobbyUI.EmberSoft, TextAnchor.MiddleLeft);
        LobbyUI.Place(title.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(56f, 0f), new Vector2(520f, 30f));

        var pill = LobbyUI.RoundedPanel(bar.transform, "StatPill", LobbyUI.BgRaised, 12);
        LobbyUI.Place(pill.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(-16f, 0f), new Vector2(280f, 44f));

        headerLevel = LobbyUI.Label(pill.transform, "LV 12", 18, LobbyUI.EmberSoft, TextAnchor.MiddleLeft);
        LobbyUI.Place(headerLevel.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(16f, 0f), new Vector2(80f, 28f));

        headerCurrency = LobbyUI.Label(pill.transform, "◆ 12,480", 16, LobbyUI.AshText, TextAnchor.MiddleRight);
        LobbyUI.Place(headerCurrency.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(-16f, 0f), new Vector2(160f, 28f));
    }

    // ---- LOBBY CODE panel (party gate / in-party header) ----
    void BuildCodePanel()
    {
        codePanel = LobbyUI.RoundedPanel(layer, "CodePanel", LobbyUI.BgPanel, 16).rectTransform;
        LobbyUI.Place(codePanel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -104f), new Vector2(360f, 140f));
        LobbyUI.Border(codePanel, LobbyUI.EmberDim, 1f);

        codeLabelTop = LobbyUI.Label(codePanel, "EXPEDITION CODE", LobbyUI.HintSize, LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(codeLabelTop.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -14f), new Vector2(340f, 20f));

        codeLabelBig = LobbyUI.ShadowLabel(codePanel, "—", LobbyUI.NameSize, LobbyUI.EmberSoft, TextAnchor.MiddleCenter);
        LobbyUI.Place(codeLabelBig.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -46f), new Vector2(340f, 36f));

        codeMembers = LobbyUI.Label(codePanel, "", LobbyUI.HintSize, LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(codeMembers.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -82f), new Vector2(340f, 20f));

        // --- party gate widgets (shown when unpartied) ---
        createBtn = LobbyUI.RoundedButton(codePanel, LobbyUI.Spaced("CREATE PARTY"), LobbyUI.Ember, new Color(0.10f, 0.06f, 0.03f), 12);
        LobbyUI.Place(createBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -44f), new Vector2(330f, 38f));
        createBtn.onClick.AddListener(() => GameManager.Conn.Reducers.CreateParty());

        joinInput = LobbyUI.Input(codePanel, "Join by code", false);
        var jr = joinInput.GetComponent<RectTransform>();
        LobbyUI.Place(jr, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(-58f, -88f), new Vector2(214f, 36f));
        joinInput.characterLimit = 5;
        joinInput.onEndEdit.AddListener(OnJoinSubmit);

        joinBtn = LobbyUI.RoundedButton(codePanel, LobbyUI.Spaced("JOIN"), LobbyUI.BgRaised, LobbyUI.AshText, 12);
        LobbyUI.Place(joinBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(108f, -88f), new Vector2(100f, 36f));
        joinBtn.onClick.AddListener(DoJoin);

        // --- in-party widget ---
        leaveBtn = LobbyUI.RoundedButton(codePanel, LobbyUI.Spaced("LEAVE PARTY"), LobbyUI.BgRaised, LobbyUI.Crimson, 12);
        LobbyUI.Place(leaveBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -106f), new Vector2(330f, 32f));
        leaveBtn.onClick.AddListener(() => GameManager.Conn.Reducers.LeaveParty());

        joinStatus = LobbyUI.Label(layer, "", LobbyUI.HintSize, LobbyUI.Crimson, TextAnchor.MiddleCenter);
        LobbyUI.Place(joinStatus.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -250f), new Vector2(420f, 22f));
    }

    void OnJoinSubmit(string _)
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) DoJoin();
    }

    void DoJoin()
    {
        if (joinInput == null) return;
        string code = joinInput.text.Trim().ToUpper();
        if (code.Length == 0) { SetJoinStatus("Enter a code."); return; }
        GameManager.Conn.Reducers.JoinParty(code);   // server re-normalizes; ToUpper is cosmetic
    }

    void SetJoinStatus(string msg) { if (joinStatus != null) joinStatus.text = msg; }

    // ---- CAPTION STRIP (bottom center) ----
    void BuildCaptionStrip()
    {
        var strip = LobbyUI.RoundedPanel(layer, "CaptionStrip", LobbyUI.BgPanel, 14);
        LobbyUI.Place(strip.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 24f), new Vector2(440f, 110f));
        LobbyUI.Border(strip.rectTransform, LobbyUI.EmberDim, 1f);

        capLeft = LobbyUI.RoundedButton(strip.transform, "◀", LobbyUI.BgRaised, LobbyUI.AshText, 18);
        LobbyUI.Place(capLeft.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(12f, 0f), new Vector2(44f, 44f));
        capLeft.onClick.AddListener(() => { if (carousel != null) carousel.Cycle(-1); });

        capRight = LobbyUI.RoundedButton(strip.transform, "▶", LobbyUI.BgRaised, LobbyUI.AshText, 18);
        LobbyUI.Place(capRight.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(-12f, 0f), new Vector2(44f, 44f));
        capRight.onClick.AddListener(() => { if (carousel != null) carousel.Cycle(+1); });

        capName = LobbyUI.ShadowLabel(strip.transform, "", LobbyUI.NameSize, LobbyUI.EmberSoft, TextAnchor.MiddleCenter);
        LobbyUI.Place(capName.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -10f), new Vector2(320f, 34f));

        capArchetype = LobbyUI.Label(strip.transform, "", 15, LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(capArchetype.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -44f), new Vector2(320f, 20f));

        capBlurb = LobbyUI.Label(strip.transform, "", 13, LobbyUI.AshText, TextAnchor.UpperCenter);
        capBlurb.horizontalOverflow = HorizontalWrapMode.Wrap;
        capBlurb.verticalOverflow = VerticalWrapMode.Truncate;
        LobbyUI.Place(capBlurb.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -66f), new Vector2(320f, 40f));
    }

    void RefreshCharacterCaption()
    {
        if (carousel == null) return;
        if (capName) capName.text = carousel.DisplayName;
        if (capArchetype) capArchetype.text = carousel.Archetype;
        if (capBlurb) capBlurb.text = carousel.Blurb;
        bool can = !carousel.Locked;
        if (capLeft) capLeft.interactable = can;
        if (capRight) capRight.interactable = can;
    }

    // ---- CHAT (bottom-right) ----
    void BuildChat()
    {
        var chatPanel = LobbyUI.RoundedPanel(layer, "ChatPanel", LobbyUI.BgPanel, 16);
        LobbyUI.Place(chatPanel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-24f, 92f), new Vector2(420f, 300f));
        LobbyUI.Border(chatPanel.rectTransform, LobbyUI.EmberDim, 1f);

        var viewport = LobbyUI.Panel(chatPanel.transform, "Viewport", new Color(0f, 0f, 0f, 0.001f));
        viewport.anchorMin = new Vector2(0f, 0f); viewport.anchorMax = new Vector2(1f, 1f);
        viewport.offsetMin = new Vector2(10f, 50f); viewport.offsetMax = new Vector2(-10f, -10f);
        viewport.gameObject.AddComponent<RectMask2D>();

        chatContent = LobbyUI.Panel(viewport, "ChatContent", new Color(0, 0, 0, 0));
        chatContent.anchorMin = new Vector2(0f, 1f); chatContent.anchorMax = new Vector2(1f, 1f);
        chatContent.pivot = new Vector2(0.5f, 1f);
        chatContent.offsetMin = new Vector2(0f, 0f); chatContent.offsetMax = new Vector2(0f, 0f);
        var cvlg = chatContent.gameObject.AddComponent<VerticalLayoutGroup>();
        cvlg.spacing = 2f; cvlg.padding = new RectOffset(4, 4, 2, 2);
        cvlg.childControlWidth = true; cvlg.childForceExpandWidth = true;
        cvlg.childControlHeight = true; cvlg.childForceExpandHeight = false;
        cvlg.childAlignment = TextAnchor.UpperLeft;
        var csf = chatContent.gameObject.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        chatScroll = chatPanel.gameObject.AddComponent<ScrollRect>();
        chatScroll.viewport = viewport;
        chatScroll.content = chatContent;
        chatScroll.horizontal = false;
        chatScroll.vertical = true;
        chatScroll.movementType = ScrollRect.MovementType.Clamped;
        chatScroll.scrollSensitivity = 18f;

        chatInput = LobbyUI.Input(chatPanel.transform, "Message the party…  (try @billionaire)", false);
        var crt = chatInput.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0f, 0f); crt.anchorMax = new Vector2(1f, 0f); crt.pivot = new Vector2(0f, 0f);
        crt.offsetMin = new Vector2(10f, 10f); crt.offsetMax = new Vector2(-92f, 44f);
        chatInput.onEndEdit.AddListener(OnChatSubmit);

        chatSendBtn = LobbyUI.RoundedButton(chatPanel.transform, "Send", LobbyUI.Ember, new Color(0.10f, 0.06f, 0.03f), 10);
        LobbyUI.Place(chatSendBtn.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-10f, 10f), new Vector2(74f, 34f));
        chatSendBtn.onClick.AddListener(SendCurrentChat);

        // --- circular utility buttons + LV badge (below the chat panel) ---
        string[] icons = { "💬", "☻", "←" };
        for (int i = 0; i < icons.Length; i++)
        {
            var b = LobbyUI.RoundedButton(layer, icons[i], LobbyUI.BgRaised, LobbyUI.Ember, 28);
            LobbyUI.Place(b.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-24f - i * 64f, 24f), new Vector2(56f, 56f));
            LobbyUI.Border(b.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        }

        var lvBadge = LobbyUI.RoundedPanel(layer, "LvBadge", LobbyUI.BgRaised, 32);
        LobbyUI.Place(lvBadge.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-24f - 3 * 64f, 20f), new Vector2(64f, 64f));
        LobbyUI.Border(lvBadge.rectTransform, LobbyUI.Ember, 1.5f);
        var lvTxt = LobbyUI.ShadowLabel(lvBadge.transform, "12", 24, LobbyUI.EmberSoft, TextAnchor.MiddleCenter);
        lvTxt.rectTransform.anchorMin = Vector2.zero; lvTxt.rectTransform.anchorMax = Vector2.one;
        lvTxt.rectTransform.offsetMin = Vector2.zero; lvTxt.rectTransform.offsetMax = Vector2.zero;
    }

    // ---- ACTIONS: info card + ready + countdown + patron (bottom-left) ----
    void BuildActions()
    {
        var info = LobbyUI.RoundedPanel(layer, "InfoCard", LobbyUI.BgPanel, 16);
        LobbyUI.Place(info.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(24f, 150f), new Vector2(360f, 150f));
        LobbyUI.Border(info.rectTransform, LobbyUI.EmberDim, 1f);

        var mode = LobbyUI.ShadowLabel(info.transform, "EXPEDITION · NIGHT RUN", 18, LobbyUI.EmberSoft, TextAnchor.UpperLeft);
        LobbyUI.Place(mode.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(18f, -16f), new Vector2(330f, 26f));

        var divider = LobbyUI.RoundedPanel(info.transform, "Divider", LobbyUI.EmberDim, 2);
        LobbyUI.Place(divider.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(18f, -48f), new Vector2(324f, 2f));

        var desc = LobbyUI.Label(info.transform,
            "Difficulty: Harrowing\nObjective: recover the relic, survive the Patron's reckoning.",
            14, LobbyUI.AshText, TextAnchor.UpperLeft);
        desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        LobbyUI.Place(desc.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(18f, -58f), new Vector2(324f, 80f));

        // The BIG ember READY CTA (dominant call-to-action).
        readyBtn = LobbyUI.RoundedButton(layer, LobbyUI.Spaced("READY"), LobbyUI.Ember, new Color(0.10f, 0.06f, 0.03f), 18);
        LobbyUI.Place(readyBtn.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(24f, 24f), new Vector2(360f, 80f));
        LobbyUI.Border(readyBtn.GetComponent<RectTransform>(), LobbyUI.Ember, 2f);
        readyBtnLabel = readyBtn.GetComponentInChildren<Text>();
        readyBtnLabel.fontSize = LobbyUI.CtaSize; readyBtnLabel.fontStyle = FontStyle.Bold;
        readyBtn.onClick.AddListener(ToggleReady);

        countdownLabel = LobbyUI.Label(layer, "", 20, LobbyUI.EmberSoft, TextAnchor.MiddleLeft);
        LobbyUI.Place(countdownLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(24f, 112f), new Vector2(360f, 28f));
        countdownLabel.gameObject.SetActive(false);

        inProgressLabel = LobbyUI.Label(layer, "", 16, LobbyUI.Crimson, TextAnchor.MiddleLeft);
        LobbyUI.Place(inProgressLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(24f, 112f), new Vector2(420f, 28f));
        inProgressLabel.gameObject.SetActive(false);

        // The Patron (LLM billionaire) is disabled — "TALK TO THE PATRON" button removed.
    }

    public void WireSubscription()
    {
        if (wired) return;
        wired = true;
        var db = GameManager.Conn.Db;

        db.LobbyMember.OnInsert += (ctx, m) => { RefreshLaunchState(); RefreshPartyUi(); if (GameManager.IsLocal(m.Identity)) { RefreshReadyButton(m); RefreshCharacterCaption(); } RebuildChat(); };
        db.LobbyMember.OnUpdate += (ctx, _old, m) => { RefreshLaunchState(); RefreshPartyUi(); if (GameManager.IsLocal(m.Identity)) { RefreshReadyButton(m); RefreshCharacterCaption(); } RebuildChat(); };
        db.LobbyMember.OnDelete += (ctx, m) => { RefreshLaunchState(); RefreshPartyUi(); };

        db.PartyChat.OnInsert += (ctx, c) => { if (c.PartyId == MyPartyId()) AppendChat(c); };

        // Party reducer outcomes -> inline status.
        db.Party.OnInsert += (ctx, p) => RefreshPartyUi();
        db.Party.OnUpdate += (ctx, _old, p) => RefreshPartyUi();
        db.Party.OnDelete += (ctx, p) => RefreshPartyUi();
        GameManager.Conn.Reducers.OnCreateParty += (ctx) => { if (ctx.Event.Status is Status.Failed(var r)) SetJoinStatus(r); };
        GameManager.Conn.Reducers.OnJoinParty += (ctx, code) => { if (ctx.Event.Status is Status.Failed(var r)) SetJoinStatus(r); else SetJoinStatus(""); };
        GameManager.Conn.Reducers.OnLeaveParty += (ctx) => { if (ctx.Event.Status is Status.Failed(var r)) SetJoinStatus(r); };

        RebuildChat();
        var me = db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me != null) RefreshReadyButton(me);
        RefreshCharacterCaption();
        RefreshPartyUi();
        RefreshLaunchState();
    }

    // ---- party UI state ----
    string MyPartyId()
    {
        if (GameManager.Conn == null) return "";
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        return me != null ? me.PartyId : "";
    }

    void RefreshPartyUi()
    {
        if (codePanel == null) return;
        string pid = MyPartyId();
        bool inParty = !string.IsNullOrEmpty(pid);

        // gate widgets visible when unpartied
        if (createBtn) createBtn.gameObject.SetActive(!inParty);
        if (joinInput) joinInput.gameObject.SetActive(!inParty);
        if (joinBtn) joinBtn.gameObject.SetActive(!inParty);
        // in-party widget
        if (leaveBtn) leaveBtn.gameObject.SetActive(inParty);

        if (inParty)
        {
            if (codeLabelTop) codeLabelTop.text = "EXPEDITION CODE";
            if (codeLabelBig) codeLabelBig.text = LobbyUI.Spaced(pid);   // VERBATIM canonical uppercase
            uint count = 0;
            var party = GameManager.Conn.Db.Party.Code.Find(pid);
            if (party != null) count = party.MemberCount;
            if (codeMembers) codeMembers.text = $"{count} / 6  ADVENTURERS";
        }
        else
        {
            if (codeLabelTop) codeLabelTop.text = "FORM YOUR EXPEDITION";
            if (codeLabelBig) codeLabelBig.text = "";
            if (codeMembers) codeMembers.text = "";
        }

        // chat input only usable in a party (else the '' echo bucket is confusing).
        if (chatInput) chatInput.interactable = inParty;
        if (chatSendBtn) chatSendBtn.interactable = inParty;
        if (chatInput && chatInput.placeholder is Text ph)
            ph.text = inParty ? "Message the party…  (try @billionaire)" : "Join a party to chat";
    }

    // ---- chat ----
    void RebuildChat()
    {
        if (chatContent == null || GameManager.Conn == null) return;
        string pid = MyPartyId();
        for (int i = chatContent.childCount - 1; i >= 0; i--) Destroy(chatContent.GetChild(i).gameObject);
        foreach (var c in GameManager.Conn.Db.PartyChat.Iter().Where(c => c.PartyId == pid).OrderBy(c => c.CreatedTick))
            AppendChat(c);
    }

    void AppendChat(PartyChat c)
    {
        if (chatContent == null) return;

        Color col;
        string who;
        if (c.IsBillionaire) { col = LobbyUI.PatronGold; who = "[The Patron]"; }
        else if (!string.IsNullOrEmpty(c.Body) && c.Body.IndexOf("@billionaire", System.StringComparison.OrdinalIgnoreCase) >= 0)
        { col = LobbyUI.Ember; who = c.SenderName; }
        else { col = LobbyUI.AshText; who = c.SenderName; }

        var t = LobbyUI.Label(chatContent, $"{who}: {c.Body}", 14, col);
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        var le = t.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 18f;

        if (chatScroll != null)
        {
            Canvas.ForceUpdateCanvases();
            chatScroll.verticalNormalizedPosition = 0f;
        }
    }

    void OnChatSubmit(string _)
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) SendCurrentChat();
    }

    void SendCurrentChat()
    {
        if (chatInput == null || !chatInput.interactable) return;
        string body = chatInput.text.Trim();
        if (body.Length == 0) return;
        GameManager.Conn.Reducers.SendChat(body);
        chatInput.text = "";
        chatInput.ActivateInputField();
    }

    // ---- ready / countdown / patron ----
    void ToggleReady()
    {
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me == null) return;
        if (me.IsReady) GameManager.Conn.Reducers.Unready();
        else GameManager.Conn.Reducers.SetReady();
    }

    void RefreshReadyButton(LobbyMember me)
    {
        if (readyBtnLabel != null) readyBtnLabel.text = LobbyUI.Spaced(me.IsReady ? "CANCEL" : "READY");
        if (readyBtn != null)
        {
            var img = readyBtn.GetComponent<Image>();
            if (img != null) img.color = me.IsReady ? LobbyUI.EmberDim : LobbyUI.Ember;
        }
    }

    // Recompute per-party all-ready from the authoritative rows; start / cancel the local countdown.
    void RefreshLaunchState()
    {
        if (GameManager.Conn == null) return;
        string pid = MyPartyId();
        var party = GameManager.Conn.Db.LobbyMember.Iter().Where(m => m.PartyId == pid).ToList();
        bool allReady = party.Count > 0 && party.All(m => m.IsReady);

        if (allReady && !counting)
        {
            counting = true;
            launchFired = false;
            countdownEnd = Time.time + CountdownSeconds;
            if (countdownLabel != null) countdownLabel.gameObject.SetActive(true);
        }
        else if (!allReady && counting)
        {
            counting = false;
            launchFired = false;
            if (countdownLabel != null) countdownLabel.gameObject.SetActive(false);
            if (inProgressLabel != null) inProgressLabel.gameObject.SetActive(false);
        }
    }

    // Is THIS client the launch trigger? The LOWEST-Slot member AMONG MY PARTY (NOT global lead).
    bool IsTriggerClient()
    {
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me == null) return false;
        string pid = me.PartyId;
        uint minSlot = GameManager.Conn.Db.LobbyMember.Iter().Where(m => m.PartyId == pid).Min(m => m.Slot);
        return me.Slot == minSlot;
    }

    void Update()
    {
        if (!counting || countdownLabel == null) return;

        float remain = Mathf.Max(0f, countdownEnd - Time.time);
        int secs = Mathf.CeilToInt(remain);
        countdownLabel.text = $"EXPEDITION DEPARTS IN 0:{secs:00}";

        if (remain <= 0f && !launchFired)
        {
            launchFired = true;
            if (IsTriggerClient())
            {
                GameManager.Conn.Reducers.LaunchGame();
                // If another party already holds launch_tick the reducer no-ops -> surface a notice so
                // the user isn't stuck on a dead button. OnLaunchGame Committed transitions everyone.
                if (countdownLabel != null) countdownLabel.gameObject.SetActive(false);
                if (inProgressLabel != null)
                {
                    inProgressLabel.text = "AN EXPEDITION IS ALREADY IN PROGRESS…";
                    inProgressLabel.gameObject.SetActive(true);
                }
            }
        }
    }

    void OnTalkClick() => GameManager.Conn.Reducers.RequestBillionaireDialogue("greeting", "lobby start", null);
}
