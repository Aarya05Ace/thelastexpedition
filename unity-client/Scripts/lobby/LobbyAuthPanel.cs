// LobbyAuthPanel.cs — THE LOST EXPEDITION lobby: the full-screen AUTH gate (PART A logic + PART D skin).
//
// Code-built uGUI: username InputField, PIN InputField (Password), Register + Login buttons, a status
// line. Register -> RegisterAccount(username, pin); Login -> Login(username, pin). Enter on either
// field submits the LOGIN path (Register stays an explicit button).
//
// Reducers return void, so success/failure is observed ONLY via Conn.Reducers.OnRegisterAccount /
// OnLogin -> ctx.Event.Status. Status.Failed(reason) shows the reason and STAYS on screen. We never
// advance optimistically on the call.
//
// CRITICAL (review fix): advance on the ROW, not the callback. The server assigns identity + a default
// LobbyMember row on commit, but that row arrives via a later subscription update — it is not
// guaranteed present the instant OnLogin fires Committed. So on commit we mark "auth committed" and
// only raise OnAuthenticated once Conn.Db.LobbyMember.Identity.Find(LocalIdentity) is non-null. We also
// re-check that Find in CheckExistingMember() so a token-resumed returning player skips straight in.
//
// PART A: built into the AUTH LAYER transform (passed by LobbyBootstrap), the only raycast-blocking
// layer during Connecting + Auth. PART D: ember-skinned — rounded centered card, vignette, EmberSoft
// shadowed title, rounded inputs/buttons, Crimson status on Failed(reason).

using System;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB;          // Status (Committed / Failed / OutOfEnergy) lives here
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;   // card-scale uses Vector3; disambiguate from SpacetimeDB.Types.Vector3

public class LobbyAuthPanel : MonoBehaviour
{
    public event Action<string> OnAuthenticated;

    RectTransform root;
    InputField userField;
    InputField pinField;
    Text status;

    bool authCommitted;     // a Register/Login reducer committed; waiting for the row
    bool advanced;          // OnAuthenticated already raised
    bool reducersWired;
    string username = "";

    // Build into the AUTH LAYER (a full-stretch CanvasGroup owned by LobbyBootstrap).
    public void Build(Transform authLayer)
    {
        root = LobbyUI.Panel(authLayer, "AuthPanel", LobbyUI.BgDeep);
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;

        // Full-screen darkening vignette behind the card.
        LobbyUI.Vignette(root);

        // Centered rounded card. Every child is anchored to the card's TOP-CENTER (anchor+pivot 0.5,1),
        // so each y below is simply the distance DOWN from the card's top edge — one clean vertical rhythm.
        //
        // CRISP HERO SCALE: instead of stretching the rendered card with rectTransform.localScale (which
        // upscales the already-rasterized legacy-Text font atlas -> blurry glyphs), we bake the scale
        // factor S into the REAL layout numbers and font sizes. Same ~770x672 footprint + proportions,
        // but every glyph rasterizes at its final pixel size -> sharp text.
        const float S = 1.75f;
        const float CARD_W = 440f * S, CARD_H = 384f * S;   // 770 x 672
        const float FIELD_W = CARD_W - 56f * S;             // 672  (= (440-56)*1.75)
        var topC = new Vector2(0.5f, 1f);

        var card = LobbyUI.RoundedPanel(root, "AuthCard", LobbyUI.BgPanel, Mathf.RoundToInt(20 * S));
        LobbyUI.Place(card.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CARD_W, CARD_H));
        LobbyUI.Border(card.rectTransform, LobbyUI.EmberDim, 1.5f * S);

        // Ember diamond accent.
        var ember = LobbyUI.RoundedPanel(card.transform, "Diamond", LobbyUI.Ember, Mathf.RoundToInt(4 * S));
        LobbyUI.Place(ember.rectTransform, topC, topC, topC, new Vector2(0f, -26f * S), new Vector2(13f * S, 13f * S));
        ember.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f);

        // Title — clean (no per-letter spacing), sized to sit comfortably inside the card width.
        var title = LobbyUI.ShadowLabel(card.transform, "THE LOST EXPEDITION", Mathf.RoundToInt(30 * S), LobbyUI.EmberSoft, TextAnchor.MiddleCenter);
        LobbyUI.Place(title.rectTransform, topC, topC, topC, new Vector2(0f, -52f * S), new Vector2(CARD_W - 36f * S, 40f * S));

        var sub = LobbyUI.Label(card.transform, "3-20 letters, numbers or _   ·   PIN: 4 digits",
            Mathf.RoundToInt(LobbyUI.HintSize * S), LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(sub.rectTransform, topC, topC, topC, new Vector2(0f, -100f * S), new Vector2(CARD_W - 36f * S, 22f * S));

        userField = LobbyUI.Input(card.transform, "Username", false);
        LobbyUI.Place(userField.GetComponent<RectTransform>(), topC, topC, topC, new Vector2(0f, -140f * S), new Vector2(FIELD_W, 46f * S));
        userField.textComponent.fontSize = Mathf.RoundToInt(16 * S);             // LobbyUI.Input bakes 16 -> scale crisp
        ((Text)userField.placeholder).fontSize = Mathf.RoundToInt(16 * S);
        userField.onEndEdit.AddListener(OnFieldSubmit);

        pinField = LobbyUI.Input(card.transform, "PIN (4 digits)", true);
        LobbyUI.Place(pinField.GetComponent<RectTransform>(), topC, topC, topC, new Vector2(0f, -196f * S), new Vector2(FIELD_W, 46f * S));
        pinField.textComponent.fontSize = Mathf.RoundToInt(16 * S);
        ((Text)pinField.placeholder).fontSize = Mathf.RoundToInt(16 * S);
        pinField.characterLimit = 4;
        pinField.contentType = InputField.ContentType.Pin; // numeric + password mask
        pinField.onEndEdit.AddListener(OnFieldSubmit);

        // Two buttons side by side, centered, spanning the field width with a small gap.
        const float GAP = 14f * S;
        float btnW = (FIELD_W - GAP) * 0.5f;
        float btnX = (btnW + GAP) * 0.5f;

        var register = LobbyUI.RoundedButton(card.transform, "REGISTER", LobbyUI.BgRaised, LobbyUI.AshText);
        LobbyUI.Place(register.GetComponent<RectTransform>(), topC, topC, topC, new Vector2(-btnX, -258f * S), new Vector2(btnW, 46f * S));
        var registerLabel = register.GetComponentInChildren<Text>(); if (registerLabel != null) registerLabel.fontSize = Mathf.RoundToInt(18 * S); // RoundedButton bakes 18
        register.onClick.AddListener(OnRegisterClick);

        var login = LobbyUI.RoundedButton(card.transform, "LOGIN", LobbyUI.Ember, new Color(0.10f, 0.06f, 0.03f));
        LobbyUI.Place(login.GetComponent<RectTransform>(), topC, topC, topC, new Vector2(btnX, -258f * S), new Vector2(btnW, 46f * S));
        var loginLabel = login.GetComponentInChildren<Text>(); if (loginLabel != null) { loginLabel.fontStyle = FontStyle.Bold; loginLabel.fontSize = Mathf.RoundToInt(18 * S); }
        login.onClick.AddListener(OnLoginClick);

        status = LobbyUI.Label(card.transform, "Connecting…", Mathf.RoundToInt(LobbyUI.BodySize * S), LobbyUI.AshDim, TextAnchor.MiddleCenter);
        LobbyUI.Place(status.rectTransform, topC, topC, topC, new Vector2(0f, -320f * S), new Vector2(CARD_W - 24f * S, 22f * S));
    }

    // Called from the lobby subscription's OnApplied: reducers + tables are now safe.
    public void WireSubscription()
    {
        if (!reducersWired)
        {
            reducersWired = true;
            GameManager.Conn.Reducers.OnRegisterAccount += (ctx, u, p) => OnAuthResult(ctx.Event.Status, u, "register");
            GameManager.Conn.Reducers.OnLogin += (ctx, u, p) => OnAuthResult(ctx.Event.Status, u, "login");

            // Advance the moment the local LobbyMember row appears (post-commit or token-resume).
            GameManager.Conn.Db.LobbyMember.OnInsert += (ctx, m) => CheckExistingMember();
            GameManager.Conn.Db.LobbyMember.OnUpdate += (ctx, _old, m) => CheckExistingMember();
        }

        SetStatus("Sign in or register to continue.", false);
        // Token-resume path: a returning identity may already own a LobbyMember row.
        CheckExistingMember();
    }

    // onEndEdit fires on focus-loss too — only submit on an actual Enter keypress (-> Login path).
    void OnFieldSubmit(string _)
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) OnLoginClick();
    }

    void OnRegisterClick()
    {
        if (!Validate()) return;
        username = userField.text.Trim();
        SetStatus("Registering…", false);
        GameManager.Conn.Reducers.RegisterAccount(username, pinField.text);
    }

    void OnLoginClick()
    {
        if (!Validate()) return;
        username = userField.text.Trim();
        SetStatus("Logging in…", false);
        GameManager.Conn.Reducers.Login(username, pinField.text);
    }

    bool Validate()
    {
        if (string.IsNullOrWhiteSpace(userField.text)) { SetStatus("Enter a username.", true); return false; }
        if (string.IsNullOrEmpty(pinField.text)) { SetStatus("Enter a PIN.", true); return false; }
        if (GameManager.Conn == null || !GameManager.Ready) { SetStatus("Still connecting…", true); return false; }
        return true;
    }

    void OnAuthResult(Status s, string u, string verb)
    {
        // TaggedEnum variants are positional records; deconstruct as the autogen does
        // (e.g. `case Status.Failed(var reason)`).
        if (s is Status.Failed(var reason))
        {
            authCommitted = false;
            SetStatus($"{char.ToUpper(verb[0]) + verb.Substring(1)} failed: {reason}", true);
            return;
        }
        if (s is Status.OutOfEnergy)
        {
            authCommitted = false;
            SetStatus("Server out of energy — try again.", true);
            return;
        }
        // Committed: identity is assigned; the LobbyMember row will arrive on a later tick.
        authCommitted = true;
        username = u;
        SetStatus("Authenticated — entering the lobby…", false);
        CheckExistingMember();
    }

    // Advance ONLY when the authoritative local row exists (review fix: row, not callback).
    void CheckExistingMember()
    {
        if (advanced) return;
        if (GameManager.Conn == null) return;
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me == null) return;            // not yet in cache — wait for the next insert/update
        advanced = true;
        if (string.IsNullOrEmpty(username)) username = me.Username;
        OnAuthenticated?.Invoke(username);
    }

    void SetStatus(string msg, bool isError)
    {
        if (status == null) return;
        status.text = msg;
        status.color = isError ? LobbyUI.Crimson : LobbyUI.AshDim;
    }

    public void SetConnecting(string msg) => SetStatus(msg, false);
}
