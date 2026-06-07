// LobbyHud.cs - THE LAST EXPEDITION cinematic lobby HUD (uGUI, built in code) - AAA RESTYLE.
//
// Menu-screen layout (NOT a busy HUD), built into the LOBBY LAYER transform (NOT the canvas root - else
// it renders under the auth card from frame 1). Three-band cinematic composition over the darkened
// forest render:
//
//   TOP BAND (content-band top-left): the wordmark "THE LAST EXPEDITION" in Barlow Condensed with a
//     thin amber accent rule, plus a quiet operator/currency line on the far right.
//   LEFT BAND (bottom-left, under the framed survivalist): CHARACTER section label, the survivor name +
//     archetype + one flavor line, and quiet < / > chevrons that drive CharacterCarousel.Cycle(-1/+1).
//   RIGHT BAND (right third): PARTY section - create/join controls when unpartied, the expedition code +
//     n/6 roster header + LEAVE when in a party, then the chat as understated [name] message rows.
//   BOTTOM BAND (centered): the Curator's ominous quote (static styled element; the live PatronPresenter
//     is currently disabled in LobbyBootstrap, so the quote is rendered here so it exists on screen).
//   BOTTOM-RIGHT: the ONE emphasized primary on this screen - the amber DEPLOY/READY button - plus a
//     countdown line and the "an expedition is already in progress" notice.
//
// Ready/launch is per-party (server-scoped); the launch caller is the LOWEST-Slot member AMONG MY PARTY
// (NOT global IsExpeditionLead). Chat renders only rows where c.PartyId==myPartyId.
//
// KEEPS all SpacetimeDB calls verbatim: ToggleReady (SetReady/Unready), SendChat, LaunchGame,
// RequestBillionaireDialogue, CreateParty/JoinParty/LeaveParty, and every table read + callback.
//
// TYPOGRAPHY: routed through StorySequencer.GetFont(Weight)/Apply (Barlow). Titles -> CondensedSemiBold;
// body/fields/buttons -> Medium/Regular/SemiBold. NO em dashes, NO emoji.

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

    // Header / operator line
    Text headerOperator;

    // Party panel (right band)
    RectTransform partyPanel;
    Text partyTitle;
    Text codeLabelTop, codeLabelBig, codeMembers;
    Button createBtn, joinBtn, leaveBtn;
    InputField joinInput;
    Text joinStatus;
    Text emptyPartyHint;

    // Character caption (left band)
    Text capName, capArchetype, capBlurb;
    Button capLeft, capRight;

    // Chat
    RectTransform chatContent;
    ScrollRect chatScroll;
    InputField chatInput;
    Button chatSendBtn;

    // Deploy / ready / countdown
    Button readyBtn;
    Text readyBtnLabel;
    Text countdownLabel;
    Text inProgressLabel;

    // How To Play (overlay panel toggled from a top-left button; closeable via CLOSE button + Esc)
    GameObject howToRoot;

    // Focus tracking for input fields (AAA focus border cue)
    readonly List<FieldFocus> focusFields = new();

    bool wired;

    // Local countdown state
    bool counting;
    float countdownEnd;
    const float CountdownSeconds = 5f;
    bool launchFired;

    // ---- AAA palette accents (square corners; retuned warm-amber-on-near-black via LobbyUI tokens) ----
    const int Radius = 6;
    static readonly Color PrimaryInk = new Color(0.043f, 0.039f, 0.035f, 1f); // near-black label on amber

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

        BuildScrim();
        BuildWordmark();
        BuildCharacterBand();
        BuildPartyPanel();
        BuildChat();
        BuildCuratorQuote();
        BuildDeploy();
        BuildHowToPlay();
    }

    // ---- font helpers (Barlow via StorySequencer) ----
    static Text Skin(Text t, StorySequencer.Weight w)
    {
        StorySequencer.Apply(t, w);   // sets Barlow font + clears faux-bold
        return t;
    }

    static void SkinButton(Button b, StorySequencer.Weight w)
    {
        if (b == null) return;
        var t = b.GetComponentInChildren<Text>();
        if (t != null) StorySequencer.Apply(t, w);
    }

    static void SkinField(InputField f, StorySequencer.Weight w)
    {
        if (f == null) return;
        if (f.textComponent != null) StorySequencer.Apply(f.textComponent, w);
        if (f.placeholder is Text ph) StorySequencer.Apply(ph, w);
    }

    // ---- background scrim (the form/menu dominates the forest render) ----
    void BuildScrim()
    {
        var scrim = LobbyUI.Panel(layer, "LobbyScrim", new Color(LobbyUI.BgDeep.r, LobbyUI.BgDeep.g, LobbyUI.BgDeep.b, 0.72f));
        scrim.anchorMin = Vector2.zero; scrim.anchorMax = Vector2.one;
        scrim.offsetMin = Vector2.zero; scrim.offsetMax = Vector2.zero;
        scrim.GetComponent<Image>().raycastTarget = false;
        scrim.SetAsFirstSibling();
    }

    // ---- WORDMARK (top of the content band) ----
    void BuildWordmark()
    {
        // Wordmark, top-left of the safe-zone content band.
        var title = LobbyUI.ShadowLabel(layer, LobbyUI.Spaced("THE LAST EXPEDITION"),
            40, LobbyUI.AshText, TextAnchor.LowerLeft);
        Skin(title, StorySequencer.Weight.CondensedSemiBold);
        // Box trimmed 720 -> 560 so the top-band wordmark never collides with the top-right operator line
        // on narrow/tall aspects. "THE LAST EXPEDITION" letter-spaced at size 40 fits well under 560.
        LobbyUI.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(64f, -56f), new Vector2(560f, 48f));

        // Thin amber accent rule under the wordmark.
        var rule = LobbyUI.Panel(layer, "WordmarkRule", LobbyUI.EmberDim);
        rule.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(rule, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(66f, -64f), new Vector2(150f, 1f));

        // Quiet operator line on the far right of the top band (LV/currency, but understated text only).
        headerOperator = LobbyUI.Label(layer, SpacedThin("OPERATOR LV 12   12,480 CR"), LobbyUI.HintSize, LobbyUI.AshDim, TextAnchor.UpperRight);
        Skin(headerOperator, StorySequencer.Weight.Medium);
        // Box trimmed 420 -> 320 so the right-anchored operator line clears the left-anchored wordmark.
        LobbyUI.Place(headerOperator.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-64f, -58f), new Vector2(320f, 20f));
    }

    // ---- CHARACTER BAND (bottom-left, under the framed survivalist) ----
    void BuildCharacterBand()
    {
        // Section label.
        var section = LobbyUI.Label(layer, SpacedThin("CHARACTER"), 22, LobbyUI.AshDim, TextAnchor.LowerLeft);
        Skin(section, StorySequencer.Weight.CondensedSemiBold);
        LobbyUI.Place(section.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(64f, 220f), new Vector2(420f, 26f));

        // Survivor name (large, Bold).
        capName = LobbyUI.ShadowLabel(layer, "", 34, LobbyUI.AshText, TextAnchor.LowerLeft);
        Skin(capName, StorySequencer.Weight.Bold);
        LobbyUI.Place(capName.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(64f, 174f), new Vector2(520f, 40f));

        // Archetype.
        capArchetype = LobbyUI.Label(layer, "", 15, LobbyUI.AshDim, TextAnchor.LowerLeft);
        Skin(capArchetype, StorySequencer.Weight.Medium);
        LobbyUI.Place(capArchetype.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(64f, 150f), new Vector2(520f, 22f));

        // One flavor line.
        capBlurb = LobbyUI.Label(layer, "", 14, new Color(LobbyUI.AshText.r, LobbyUI.AshText.g, LobbyUI.AshText.b, 0.85f), TextAnchor.UpperLeft);
        Skin(capBlurb, StorySequencer.Weight.Regular);
        capBlurb.horizontalOverflow = HorizontalWrapMode.Wrap;
        capBlurb.verticalOverflow = VerticalWrapMode.Truncate;
        LobbyUI.Place(capBlurb.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(64f, 96f), new Vector2(440f, 50f));

        // Quiet prev / next chevrons (ASCII < > - render reliably in Barlow).
        capLeft = LobbyUI.RoundedButton(layer, "<", LobbyUI.BgPanel, LobbyUI.AshDim, Radius);
        SkinButton(capLeft, StorySequencer.Weight.SemiBold);
        LobbyUI.Place(capLeft.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(64f, 36f), new Vector2(48f, 48f));
        LobbyUI.Border(capLeft.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        capLeft.onClick.AddListener(() => { if (carousel != null) carousel.Cycle(-1); });
        HoverTint(capLeft, LobbyUI.AshDim, LobbyUI.Ember);

        capRight = LobbyUI.RoundedButton(layer, ">", LobbyUI.BgPanel, LobbyUI.AshDim, Radius);
        SkinButton(capRight, StorySequencer.Weight.SemiBold);
        LobbyUI.Place(capRight.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(120f, 36f), new Vector2(48f, 48f));
        LobbyUI.Border(capRight.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        capRight.onClick.AddListener(() => { if (carousel != null) carousel.Cycle(+1); });
        HoverTint(capRight, LobbyUI.AshDim, LobbyUI.Ember);
    }

    void RefreshCharacterCaption()
    {
        if (carousel == null) return;
        if (capName) capName.text = carousel.DisplayName;
        if (capArchetype) capArchetype.text = SpacedThin((carousel.Archetype ?? "").ToUpperInvariant());
        if (capBlurb) capBlurb.text = carousel.Blurb;
        bool can = !carousel.Locked;
        if (capLeft) capLeft.interactable = can;
        if (capRight) capRight.interactable = can;
    }

    // ---- PARTY PANEL (right band) ----
    void BuildPartyPanel()
    {
        partyPanel = LobbyUI.RoundedPanel(layer, "PartyPanel", LobbyUI.BgPanel, Radius).rectTransform;
        // Height 250 -> 270 so a two-line server error in joinStatus stays inside the panel border.
        LobbyUI.Place(partyPanel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-64f, -104f), new Vector2(420f, 270f));
        LobbyUI.Border(partyPanel, LobbyUI.EmberDim, 1f);

        // Section title.
        partyTitle = LobbyUI.Label(partyPanel, SpacedThin("PARTY"), 22, LobbyUI.AshDim, TextAnchor.UpperLeft);
        Skin(partyTitle, StorySequencer.Weight.CondensedSemiBold);
        LobbyUI.Place(partyTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -18f), new Vector2(380f, 24f));

        // ---- in-party header (code + roster count) ----
        codeLabelTop = LobbyUI.Label(partyPanel, SpacedThin("EXPEDITION CODE"), 12, LobbyUI.AshDim, TextAnchor.UpperLeft);
        Skin(codeLabelTop, StorySequencer.Weight.Medium);
        LobbyUI.Place(codeLabelTop.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -54f), new Vector2(380f, 18f));

        codeLabelBig = LobbyUI.ShadowLabel(partyPanel, "...", 30, LobbyUI.EmberSoft, TextAnchor.UpperLeft);
        Skin(codeLabelBig, StorySequencer.Weight.Bold);
        LobbyUI.Place(codeLabelBig.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -74f), new Vector2(380f, 38f));

        codeMembers = LobbyUI.Label(partyPanel, "", 13, LobbyUI.AshDim, TextAnchor.UpperLeft);
        Skin(codeMembers, StorySequencer.Weight.Medium);
        LobbyUI.Place(codeMembers.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -116f), new Vector2(380f, 18f));

        leaveBtn = LobbyUI.RoundedButton(partyPanel, "LEAVE PARTY", new Color(0f, 0f, 0f, 0f), LobbyUI.AshDim, Radius);
        SkinButton(leaveBtn, StorySequencer.Weight.Medium);
        LobbyUI.Place(leaveBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -150f), new Vector2(160f, 36f));
        LobbyUI.Border(leaveBtn.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        leaveBtn.onClick.AddListener(() => GameManager.Conn.Reducers.LeaveParty());
        HoverTint(leaveBtn, LobbyUI.AshDim, LobbyUI.Crimson);

        // ---- party gate (shown when unpartied) ----
        emptyPartyHint = LobbyUI.Label(partyPanel, "No party yet. Create one or join a call sign.",
            13, LobbyUI.AshDim, TextAnchor.UpperLeft);
        Skin(emptyPartyHint, StorySequencer.Weight.Regular);
        emptyPartyHint.horizontalOverflow = HorizontalWrapMode.Wrap;
        LobbyUI.Place(emptyPartyHint.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -52f), new Vector2(380f, 36f));

        createBtn = LobbyUI.RoundedButton(partyPanel, "CREATE PARTY", LobbyUI.Ember, PrimaryInk, Radius);
        SkinButton(createBtn, StorySequencer.Weight.SemiBold);
        LobbyUI.Place(createBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -96f), new Vector2(380f, 48f));
        createBtn.onClick.AddListener(() => GameManager.Conn.Reducers.CreateParty());

        joinInput = LobbyUI.Input(partyPanel, "Join by code", false);
        SkinField(joinInput, StorySequencer.Weight.Regular);
        StyleField(joinInput);
        var jr = joinInput.GetComponent<RectTransform>();
        LobbyUI.Place(jr, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -156f), new Vector2(264f, 48f));
        joinInput.characterLimit = 5;
        joinInput.onEndEdit.AddListener(OnJoinSubmit);

        joinBtn = LobbyUI.RoundedButton(partyPanel, "JOIN", LobbyUI.BgRaised, LobbyUI.AshText, Radius);
        SkinButton(joinBtn, StorySequencer.Weight.SemiBold);
        LobbyUI.Place(joinBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(292f, -156f), new Vector2(108f, 48f));
        LobbyUI.Border(joinBtn.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        joinBtn.onClick.AddListener(DoJoin);

        joinStatus = LobbyUI.Label(partyPanel, "", 13, LobbyUI.Crimson, TextAnchor.UpperLeft);
        Skin(joinStatus, StorySequencer.Weight.Regular);
        joinStatus.horizontalOverflow = HorizontalWrapMode.Wrap;
        LobbyUI.Place(joinStatus.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -212f), new Vector2(380f, 32f));
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

    // ---- CHAT (under the party panel, right band) ----
    void BuildChat()
    {
        var chatPanel = LobbyUI.RoundedPanel(layer, "ChatPanel", LobbyUI.BgPanel, Radius);
        // Height 290 -> 250 so the bottom-anchored chat panel and the top-anchored party panel (now 270)
        // never overlap in the right column on a fixed-1080 layout. Viewport/input/send anchor relative
        // to the panel, so they track the smaller height with no further changes.
        LobbyUI.Place(chatPanel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-64f, 130f), new Vector2(420f, 250f));
        LobbyUI.Border(chatPanel.rectTransform, LobbyUI.EmberDim, 1f);

        var chatTitle = LobbyUI.Label(chatPanel.transform, SpacedThin("PARTY CHANNEL"), 12, LobbyUI.AshDim, TextAnchor.UpperLeft);
        Skin(chatTitle, StorySequencer.Weight.Medium);
        LobbyUI.Place(chatTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(16f, -14f), new Vector2(380f, 16f));

        var viewport = LobbyUI.Panel(chatPanel.transform, "Viewport", new Color(0f, 0f, 0f, 0.001f));
        viewport.anchorMin = new Vector2(0f, 0f); viewport.anchorMax = new Vector2(1f, 1f);
        viewport.offsetMin = new Vector2(12f, 58f); viewport.offsetMax = new Vector2(-12f, -36f);
        viewport.gameObject.AddComponent<RectMask2D>();

        chatContent = LobbyUI.Panel(viewport, "ChatContent", new Color(0, 0, 0, 0));
        chatContent.anchorMin = new Vector2(0f, 1f); chatContent.anchorMax = new Vector2(1f, 1f);
        chatContent.pivot = new Vector2(0.5f, 1f);
        chatContent.offsetMin = new Vector2(0f, 0f); chatContent.offsetMax = new Vector2(0f, 0f);
        var cvlg = chatContent.gameObject.AddComponent<VerticalLayoutGroup>();
        cvlg.spacing = 4f; cvlg.padding = new RectOffset(2, 2, 2, 2);
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

        chatInput = LobbyUI.Input(chatPanel.transform, "Message the party. Try @billionaire.", false);
        SkinField(chatInput, StorySequencer.Weight.Regular);
        StyleField(chatInput);
        var crt = chatInput.GetComponent<RectTransform>();
        crt.anchorMin = new Vector2(0f, 0f); crt.anchorMax = new Vector2(1f, 0f); crt.pivot = new Vector2(0f, 0f);
        crt.offsetMin = new Vector2(12f, 12f); crt.offsetMax = new Vector2(-96f, 48f);
        chatInput.onEndEdit.AddListener(OnChatSubmit);

        chatSendBtn = LobbyUI.RoundedButton(chatPanel.transform, "SEND", LobbyUI.Ember, PrimaryInk, Radius);
        SkinButton(chatSendBtn, StorySequencer.Weight.SemiBold);
        LobbyUI.Place(chatSendBtn.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-12f, 12f), new Vector2(72f, 36f));
        chatSendBtn.onClick.AddListener(SendCurrentChat);
    }

    // ---- CURATOR QUOTE (bottom band, centered, ominous) ----
    // The live PatronPresenter is disabled in LobbyBootstrap, so the Curator's line is rendered here as a
    // static styled element so it exists on screen per the brief. If PatronPresenter is re-enabled, its
    // live bubble will simply layer over this (both are null-safe and non-interactive).
    void BuildCuratorQuote()
    {
        // A thin amber rule above the quote.
        var rule = LobbyUI.Panel(layer, "CuratorRule", LobbyUI.EmberDim);
        rule.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(rule, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 86f), new Vector2(120f, 1f));

        var quote = LobbyUI.Label(layer,
            "\"They are already lost. Bring them back anyway.\"",
            20, LobbyUI.PatronGold, TextAnchor.UpperCenter);
        Skin(quote, StorySequencer.Weight.Light);
        quote.horizontalOverflow = HorizontalWrapMode.Wrap;
        // Width 720 -> 560 so the centered quote's right end clears the bottom-right DEPLOY column and its
        // left end clears the bottom-left character chevrons.
        LobbyUI.Place(quote.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 56f), new Vector2(560f, 28f));

        var attribution = LobbyUI.Label(layer, SpacedThin("THE CURATOR"), 11, LobbyUI.AshDim, TextAnchor.UpperCenter);
        Skin(attribution, StorySequencer.Weight.Medium);
        LobbyUI.Place(attribution.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 36f), new Vector2(560f, 16f));
    }

    // ---- DEPLOY (bottom-right, the ONE emphasized amber-filled element) ----
    void BuildDeploy()
    {
        // The dominant call-to-action: amber-filled DEPLOY / READY toggle.
        readyBtn = LobbyUI.RoundedButton(layer, "DEPLOY", LobbyUI.Ember, PrimaryInk, Radius);
        readyBtnLabel = readyBtn.GetComponentInChildren<Text>();
        StorySequencer.Apply(readyBtnLabel, StorySequencer.Weight.SemiBold);
        readyBtnLabel.fontSize = 22;
        readyBtnLabel.text = SpacedThin("DEPLOY");
        LobbyUI.Place(readyBtn.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-64f, 36f), new Vector2(280f, 60f));
        readyBtn.onClick.AddListener(ToggleReady);

        countdownLabel = LobbyUI.Label(layer, "", 14, LobbyUI.EmberSoft, TextAnchor.LowerRight);
        Skin(countdownLabel, StorySequencer.Weight.Medium);
        LobbyUI.Place(countdownLabel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-64f, 104f), new Vector2(360f, 22f));
        countdownLabel.gameObject.SetActive(false);

        inProgressLabel = LobbyUI.Label(layer, "", 14, LobbyUI.Crimson, TextAnchor.LowerRight);
        Skin(inProgressLabel, StorySequencer.Weight.Medium);
        LobbyUI.Place(inProgressLabel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-64f, 104f), new Vector2(420f, 22f));
        inProgressLabel.gameObject.SetActive(false);
    }

    // ---- HOW TO PLAY (top-left high-contrast button -> full-screen overlay card; closeable via CLOSE + Esc) ----
    // A judge-facing briefing: the verified controls, the objective, what to expect, and the SpacetimeDB
    // showcase. Built ONCE into the lobby LAYER, hidden by default, toggled by activeSelf (mirrors the proven
    // MinimapHud big-map pattern). The button is the SECOND amber element on screen (DEPLOY is the first); two
    // amber things is on-budget for the demo CTA pair. Null-safe; no SpacetimeDB calls touched.
    void BuildHowToPlay()
    {
        // ---- the top-left trigger button (big, high-contrast, clear of DEPLOY / party / chat / character) ----
        var howBtn = LobbyUI.RoundedButton(layer, "HOW TO PLAY", LobbyUI.Ember, PrimaryInk, Radius);
        var howLbl = howBtn.GetComponentInChildren<Text>();
        StorySequencer.Apply(howLbl, StorySequencer.Weight.SemiBold);
        if (howLbl != null) { howLbl.fontSize = 20; howLbl.text = SpacedThin("HOW TO PLAY"); }
        LobbyUI.Place(howBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(64f, -96f), new Vector2(260f, 56f));
        LobbyUI.Border(howBtn.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        howBtn.onClick.AddListener(() => { if (howToRoot != null) howToRoot.SetActive(!howToRoot.activeSelf); });

        // ---- the overlay root + dark scrim (eats clicks behind it so the lobby is inert while open) ----
        var root = LobbyUI.Panel(layer, "HowToRoot",
            new Color(LobbyUI.BgDeep.r, LobbyUI.BgDeep.g, LobbyUI.BgDeep.b, 0.92f));
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;
        var rootImg = root.GetComponent<Image>();
        if (rootImg != null) rootImg.raycastTarget = true;   // KEEP raycast on: block the lobby underneath
        root.SetAsLastSibling();
        howToRoot = root.gameObject;

        // ---- the centered card ----
        var card = LobbyUI.RoundedPanel(root, "HowToCard", LobbyUI.BgPanel, Radius).rectTransform;
        LobbyUI.Place(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 0f), new Vector2(900f, 620f));
        LobbyUI.Border(card, LobbyUI.EmberDim, 1f);

        // Title + amber accent rule under it (matches the wordmark treatment).
        var title = LobbyUI.ShadowLabel(card, LobbyUI.Spaced("HOW TO PLAY"), 34, LobbyUI.AshText, TextAnchor.UpperLeft);
        Skin(title, StorySequencer.Weight.CondensedSemiBold);
        LobbyUI.Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(40f, -34f), new Vector2(560f, 44f));
        var titleRule = LobbyUI.Panel(card, "HowToRule", LobbyUI.EmberDim);
        var trImg = titleRule.GetComponent<Image>();
        if (trImg != null) trImg.raycastTarget = false;
        LobbyUI.Place(titleRule, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(42f, -82f), new Vector2(220f, 1f));

        // CLOSE button, top-right of the card.
        var closeBtn = LobbyUI.RoundedButton(card, "CLOSE", LobbyUI.BgRaised, LobbyUI.AshText, Radius);
        SkinButton(closeBtn, StorySequencer.Weight.SemiBold);
        LobbyUI.Place(closeBtn.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-40f, -34f), new Vector2(120f, 44f));
        LobbyUI.Border(closeBtn.GetComponent<RectTransform>(), LobbyUI.EmberDim, 1f);
        closeBtn.onClick.AddListener(() => { if (howToRoot != null) howToRoot.SetActive(false); });
        HoverTint(closeBtn, LobbyUI.AshText, LobbyUI.Ember);

        // ---- four sections in two columns (no ScrollRect needed for a fixed demo card) ----
        // Local helper: an UPPER amber section header + a wrapped Regular body block, placed top-left-anchored
        // inside the card at (x, y) with the given width and an explicit body-rect height (bodyH). The body
        // height is now per-call so the right-column stack is spaced to fit inside the 620 card with a bottom
        // margin and no body rect ever overlaps the next header (was a hardcoded 240 that spilled the card).
        void Section(string header, string body, float x, float y, float width, float bodyH)
        {
            var h = LobbyUI.Label(card, SpacedThin(header), 17, LobbyUI.EmberSoft, TextAnchor.UpperLeft);
            Skin(h, StorySequencer.Weight.CondensedSemiBold);
            LobbyUI.Place(h.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(x, y), new Vector2(width, 22f));

            var b = LobbyUI.Label(card, body, 15,
                new Color(LobbyUI.AshText.r, LobbyUI.AshText.g, LobbyUI.AshText.b, 0.92f), TextAnchor.UpperLeft);
            Skin(b, StorySequencer.Weight.Regular);
            b.horizontalOverflow = HorizontalWrapMode.Wrap;
            b.verticalOverflow = VerticalWrapMode.Truncate;   // clip inside the rect rather than spill the card
            b.lineSpacing = 1.15f;
            LobbyUI.Place(b.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(x, y - 30f), new Vector2(width, bodyH));
        }

        const float colW = 380f;
        const float leftX = 40f;
        const float rightX = 480f;

        // LEFT COLUMN: the verified control list (one tall block; the card's left side is otherwise empty).
        Section("CONTROLS",
            "Move with W A S D. Hold Left Shift to sprint. Space to jump. Move the mouse to look.\n" +
            "Left Mouse to fire. Hold Right Mouse to aim down sights. R to reload.\n" +
            "1 for the AK-47. 2 for the apple (click to eat and heal). 3 is reserved.\n" +
            "E to interrogate an NPC by text. Hold V to talk to an NPC by voice.\n" +
            "M opens the full map. P toggles noclip fly (debug).",
            leftX, -118f, colW, 380f);

        // RIGHT COLUMN: objective, what to expect, and the SpacetimeDB showcase. Re-spaced so the lowest
        // body rect bottoms at -590 (30px above the -620 card bottom) and each block clears the next header.
        Section("YOUR OBJECTIVE",
            "You deploy onto the island to rescue your sister. Push to the cabin at the far end and fight " +
            "through the Curator's men. Talk to the islanders by voice to pull the clues that open the way. " +
            "Reach the cabin, free your sister, then stand against the Curator.",
            rightX, -118f, colW, 116f);

        Section("WHAT TO EXPECT",
            "A deploy cutscene and title card open the run. Follow the amber waypoint on the minimap to the " +
            "cabin. The Curator's guards hold the path. After an emotional reunion, the Curator arrives as " +
            "the final boss.",
            rightX, -274f, colW, 96f);

        Section("BUILT ON SPACETIMEDB",
            "Every islander is a Claude language model brain, driven live through SpacetimeDB tables. You " +
            "speak to them by voice and they answer in character. A server-enforced clue knowledge-graph " +
            "drives the story, so progress is real, not scripted. Multiplayer is free through SpacetimeDB " +
            "subscriptions, so every player sees the same world with no extra netcode.",
            rightX, -410f, colW, 150f);

        // Hidden until the judge presses HOW TO PLAY.
        howToRoot.SetActive(false);
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
        if (partyPanel == null) return;
        string pid = MyPartyId();
        bool inParty = !string.IsNullOrEmpty(pid);

        // gate widgets visible when unpartied
        if (emptyPartyHint) emptyPartyHint.gameObject.SetActive(!inParty);
        if (createBtn) createBtn.gameObject.SetActive(!inParty);
        if (joinInput) joinInput.gameObject.SetActive(!inParty);
        if (joinBtn) joinBtn.gameObject.SetActive(!inParty);
        // in-party widgets
        if (codeLabelTop) codeLabelTop.gameObject.SetActive(inParty);
        if (codeLabelBig) codeLabelBig.gameObject.SetActive(inParty);
        if (codeMembers) codeMembers.gameObject.SetActive(inParty);
        if (leaveBtn) leaveBtn.gameObject.SetActive(inParty);

        if (inParty)
        {
            if (codeLabelBig) codeLabelBig.text = LobbyUI.Spaced(pid);   // VERBATIM canonical uppercase
            uint count = 0;
            var party = GameManager.Conn.Db.Party.Code.Find(pid);
            if (party != null) count = party.MemberCount;
            if (codeMembers) codeMembers.text = SpacedThin($"{count} / 6 SURVIVORS");
        }

        // chat input only usable in a party (else the '' echo bucket is confusing).
        if (chatInput) chatInput.interactable = inParty;
        if (chatSendBtn) chatSendBtn.interactable = inParty;
        if (chatInput && chatInput.placeholder is Text ph)
            ph.text = inParty ? "Message the party. Try @billionaire." : "Join a party to chat.";
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

        Color nameCol;
        string who;
        if (c.IsBillionaire) { nameCol = LobbyUI.PatronGold; who = "The Patron"; }
        else if (!string.IsNullOrEmpty(c.Body) && c.Body.IndexOf("@billionaire", System.StringComparison.OrdinalIgnoreCase) >= 0)
        { nameCol = LobbyUI.Ember; who = c.SenderName; }
        else { nameCol = LobbyUI.AshDim; who = c.SenderName; }

        // Understated row: dim name, ash body, no bubbles. Rich-text colors the name only.
        string nameHex = ColorUtility.ToHtmlStringRGB(nameCol);
        var t = LobbyUI.Label(chatContent, $"<color=#{nameHex}>{who}</color>  {c.Body}", 15,
            new Color(LobbyUI.AshText.r, LobbyUI.AshText.g, LobbyUI.AshText.b, 0.92f));
        StorySequencer.Apply(t, StorySequencer.Weight.Regular);
        t.supportRichText = true;
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
        if (readyBtnLabel != null) readyBtnLabel.text = SpacedThin(me.IsReady ? "STAND DOWN" : "DEPLOY");
        if (readyBtn != null)
        {
            var img = readyBtn.GetComponent<Image>();
            // Ready -> understated (deploy armed, awaiting party); not ready -> the bright amber CTA.
            if (img != null) img.color = me.IsReady ? LobbyUI.EmberDim : LobbyUI.Ember;
            if (readyBtnLabel != null) readyBtnLabel.color = me.IsReady ? LobbyUI.AshText : PrimaryInk;
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
        // Esc closes the HOW TO PLAY overlay (mirrors MinimapHud: Esc only ever CLOSES, never opens). Checked
        // first, BEFORE the countdown early-return below, so it still works while a deploy countdown is live.
        if (howToRoot != null && howToRoot.activeSelf && Input.GetKeyDown(KeyCode.Escape))
            howToRoot.SetActive(false);

        // Drive input-field focus borders (AAA focus cue). Cheap; runs each frame.
        UpdateFieldFocus();

        if (!counting || countdownLabel == null) return;

        float remain = Mathf.Max(0f, countdownEnd - Time.time);
        int secs = Mathf.CeilToInt(remain);
        countdownLabel.text = SpacedThin($"DEPLOYING IN 0:{secs:00}");

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
                    inProgressLabel.text = "An expedition is already in progress.";
                    inProgressLabel.gameObject.SetActive(true);
                }
            }
        }
    }

    void OnTalkClick() => GameManager.Conn.Reducers.RequestBillionaireDialogue("greeting", "lobby start", null);

    // ===== AAA helpers (additive, self-contained - no LobbyUI signature changes) =====

    // Thin-space (U+2009) letter-spacing for small UPPER labels (tighter than LobbyUI.Spaced's full space).
    static string SpacedThin(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i < s.Length - 1 && s[i] != ' ') sb.Append(' ');
        }
        return sb.ToString();
    }

    // Brighten a button's label + border tint on hover via EventTrigger (quiet -> accent).
    void HoverTint(Button btn, Color idle, Color hover)
    {
        if (btn == null) return;
        var label = btn.GetComponentInChildren<Text>();
        var border = btn.GetComponentInChildren<Outline>();
        var et = btn.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();

        var enter = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
        enter.callback.AddListener(_ => { if (btn.interactable) { if (label) label.color = hover; if (border) border.effectColor = hover; } });
        et.triggers.Add(enter);

        var exit = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => { if (label) label.color = idle; if (border) border.effectColor = LobbyUI.EmberDim; });
        et.triggers.Add(exit);
    }

    // Apply AAA field skin (square corners, ember idle border, raised fill on focus) and register for
    // per-frame focus tracking.
    void StyleField(InputField field)
    {
        if (field == null) return;
        var img = field.GetComponent<Image>();
        if (img != null)
        {
            img.sprite = LobbyUI.RoundedSprite(Radius);
            img.color = LobbyUI.BgPanel;
        }
        // Idle ember-dim border (Outline-only frame, matching LobbyUI.Border).
        var border = field.GetComponent<Outline>();
        if (border == null) border = field.gameObject.AddComponent<Outline>();
        border.effectColor = LobbyUI.EmberDim;
        border.effectDistance = new Vector2(1f, 1f);
        if (field.placeholder is Text ph) ph.color = LobbyUI.AshDim;

        focusFields.Add(new FieldFocus { field = field, img = img, border = border });
    }

    // Legacy InputField has no focus event; poll the EventSystem selection and swap the border/fill.
    void UpdateFieldFocus()
    {
        if (focusFields.Count == 0) return;
        var sel = UnityEngine.EventSystems.EventSystem.current != null
            ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
        foreach (var ff in focusFields)
        {
            if (ff.field == null) continue;
            bool focused = sel == ff.field.gameObject;
            if (focused == ff.focused) continue;
            ff.focused = focused;
            if (ff.border != null) ff.border.effectColor = focused ? LobbyUI.Ember : LobbyUI.EmberDim;
            if (ff.img != null) ff.img.color = focused ? LobbyUI.BgRaised : LobbyUI.BgPanel;
        }
    }

    class FieldFocus
    {
        public InputField field;
        public Image img;
        public Outline border;
        public bool focused;
    }
}
