// LobbyAuthPanel.cs - THE LAST EXPEDITION: the full-screen AUTH gate (logic preserved, AAA restyle).
//
// Code-built uGUI: a CALL SIGN InputField, an ACCESS PIN InputField (Password), a primary LOGIN button,
// an understated REGISTER button, and a subtle status line. Register -> RegisterAccount(username, pin);
// Login -> Login(username, pin). Enter on either field submits the LOGIN path (Register stays explicit).
//
// Reducers return void, so success/failure is observed ONLY via Conn.Reducers.OnRegisterAccount /
// OnLogin -> ctx.Event.Status. Status.Failed(reason) shows the reason and STAYS on screen. We never
// advance optimistically on the call.
//
// CRITICAL (review fix, UNCHANGED): advance on the ROW, not the callback. On commit we mark
// "auth committed" and only raise OnAuthenticated once Conn.Db.LobbyMember.Identity.Find(LocalIdentity)
// is non-null. CheckExistingMember() also covers the token-resumed returning player.
//
// AAA RESTYLE (visual only): dark atmospheric full-screen backdrop (BgDeep scrim + cached Vignette over
// the existing scene, no heavy new assets); a large letter-spaced "THE LAST EXPEDITION" wordmark in
// Barlow Condensed with a thin amber accent rule; a single centered column of two LABELED fields with
// subtle borders + a clear focus state (a tiny focus tracker swaps the border to amber on selection);
// ONE emphasized amber LOGIN button + an understated ghost REGISTER; a subtle bottom status line;
// generous margins + vertical rhythm. Everything routes through Barlow via StorySequencer.
//
// HARD RULES honored: NO em dashes, NO emoji. Build-safe (UnityEngine + SpacetimeDB only), null-safe.

using System;
using UnityEngine;
using UnityEngine.UI;
using SpacetimeDB;          // Status (Committed / Failed / OutOfEnergy) lives here
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;   // disambiguate from SpacetimeDB.Types.Vector3

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

    // ---- AAA copy (approved; no em dashes, no emoji) ----
    const string Wordmark    = "THE LAST EXPEDITION";
    const string Tagline     = "Rescue operation. Sign in to deploy.";
    const string LabelUser   = "CALL SIGN";
    const string LabelPin    = "ACCESS PIN";
    const string PhUser      = "Enter your call sign";
    const string PhPin       = "4 digit PIN";
    const string BtnLogin    = "LOGIN";
    const string BtnRegister = "REGISTER";

    // ---- layout constants (1080p reference; CanvasScaler scales the canvas) ----
    const float ColumnW   = 440f;   // centered column width
    const float FieldH    = 56f;    // field / primary button height
    const float MicroGap  = 8f;     // micro-label -> its field
    const float FieldGap  = 18f;    // field block -> next field block
    const float SecGap    = 14f;    // primary -> secondary button

    // Build into the AUTH LAYER (a full-stretch CanvasGroup owned by LobbyBootstrap). This layer is the
    // only raycast-blocking layer during Connecting + Auth, so the form owns input until auth succeeds.
    public void Build(Transform authLayer)
    {
        // ---- backdrop: a near-opaque BgDeep scrim over the existing scene, then the cached radial
        //      vignette (the LotU corner-darkening). No heavy new assets. On AUTH the scrim is strong so
        //      the form dominates; the lobby uses a lighter scrim so the forest reads through. ----
        root = LobbyUI.Panel(authLayer, "AuthPanel", new Color(LobbyUI.BgDeep.r, LobbyUI.BgDeep.g, LobbyUI.BgDeep.b, 0.92f));
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;
        LobbyUI.Vignette(root);

        // ---- a single centered, vertically-centered column. Everything is anchored to the column's
        //      TOP-CENTER (anchor + pivot 0.5,1), so each y below is the distance DOWN from the top edge:
        //      one clean vertical rhythm, no nested guesswork. The column itself is transparent (the scrim
        //      is the background) so this reads like a menu screen, not a card. ----
        const float COL_H = 520f;
        var column = LobbyUI.Panel(root, "AuthColumn", new Color(0, 0, 0, 0));
        LobbyUI.Place(column, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(ColumnW, COL_H));
        var topC = new Vector2(0.5f, 1f);
        float y = 0f;

        // 1) Wordmark - Barlow Condensed SemiBold, large, letter-spaced (thin spaces, not full).
        var title = LobbyUI.BarlowLabel(column.transform, LobbyUI.SpacedThin(Wordmark), 60,
            LobbyUI.AshText, StorySequencer.Weight.CondensedSemiBold, TextAnchor.MiddleCenter);
        var sh = title.gameObject.AddComponent<Shadow>();   // faint depth, not faux-weight
        sh.effectColor = new Color(0f, 0f, 0f, 0.6f);
        sh.effectDistance = new Vector2(1f, -2f);
        LobbyUI.Place(title.rectTransform, topC, topC, topC, new Vector2(0f, -y), new Vector2(ColumnW + 80f, 72f));
        y += 72f + 16f;

        // 2) Thin amber accent rule under the wordmark, centered.
        var rule = LobbyUI.Rule(column.transform, LobbyUI.EmberDim, 120f, 1f);
        LobbyUI.Place(rule.rectTransform, topC, topC, topC, new Vector2(0f, -y), new Vector2(120f, 1f));
        y += 1f + 14f;

        // 3) Tagline - quiet, dim.
        var tag = LobbyUI.BarlowLabel(column.transform, Tagline, 14, LobbyUI.AshDim,
            StorySequencer.Weight.Regular, TextAnchor.MiddleCenter);
        LobbyUI.Place(tag.rectTransform, topC, topC, topC, new Vector2(0f, -y), new Vector2(ColumnW, 20f));
        y += 20f + 44f;   // generous gap before the form

        // 4) CALL SIGN field (micro-label above, then the field).
        y = BuildLabeledField(column.transform, topC, y, LabelUser, PhUser, false, out userField);
        userField.onEndEdit.AddListener(OnFieldSubmit);

        y += FieldGap;

        // 5) ACCESS PIN field.
        y = BuildLabeledField(column.transform, topC, y, LabelPin, PhPin, true, out pinField);
        pinField.characterLimit = 4;
        pinField.contentType = InputField.ContentType.Pin; // numeric + password mask
        pinField.onEndEdit.AddListener(OnFieldSubmit);

        y += 36f;   // form -> button block

        // 6) Primary LOGIN button - the ONE emphasized amber element. Near-square corners (radius 6),
        //    near-black label on amber fill, SemiBold, letter-spaced.
        var login = LobbyUI.RoundedButton(column.transform, LobbyUI.SpacedThin(BtnLogin),
            LobbyUI.Ember, LobbyUI.BgDeep, 6);
        LobbyUI.Place(login.GetComponent<RectTransform>(), topC, topC, topC,
            new Vector2(0f, -y), new Vector2(ColumnW, FieldH));
        StyleButtonLabel(login, 19, StorySequencer.Weight.SemiBold);
        TuneButtonColors(login, primary: true);
        login.onClick.AddListener(OnLoginClick);
        y += FieldH + SecGap;

        // 7) Secondary REGISTER button - ghost: a near-transparent warm fill (a faint base so the hover/
        //    press color multiplier reads as a gentle wash, never a solid box), a thin EmberDim border, and
        //    an ash label. It must not compete with the primary, so no amber fill. The amber-on-focus feel
        //    is carried by the border (see TuneButtonColors comment).
        var register = LobbyUI.RoundedButton(column.transform, LobbyUI.SpacedThin(BtnRegister),
            new Color(LobbyUI.AshText.r, LobbyUI.AshText.g, LobbyUI.AshText.b, 0.05f), LobbyUI.AshText, 6);
        var regRt = register.GetComponent<RectTransform>();
        LobbyUI.Place(regRt, topC, topC, topC, new Vector2(0f, -y), new Vector2(ColumnW, 48f));
        LobbyUI.Border(regRt, LobbyUI.EmberDim, 1f);
        StyleButtonLabel(register, 16, StorySequencer.Weight.Medium);
        TuneButtonColors(register, primary: false);
        register.onClick.AddListener(OnRegisterClick);
        y += 48f;

        // 8) Status / footer line - pinned to the BOTTOM of the column, subtle. Turns Crimson on error,
        //    EmberSoft on success, AshDim otherwise.
        status = LobbyUI.BarlowLabel(column.transform, "Connecting.", 13, LobbyUI.AshDim,
            StorySequencer.Weight.Regular, TextAnchor.MiddleCenter);
        LobbyUI.Place(status.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(ColumnW + 60f, 22f));
    }

    // Build one micro-labeled field at vertical cursor `y` (distance DOWN from the column top). Returns the
    // updated cursor (top of the NEXT element). Field gets a subtle border + a focus tracker that swaps the
    // border to amber and lifts the fill while selected.
    float BuildLabeledField(Transform parent, Vector2 topC, float y, string microLabel,
                            string placeholder, bool password, out InputField field)
    {
        // micro-label (UPPER, letter-spaced, dim, Barlow Medium)
        var ml = LobbyUI.BarlowLabel(parent, LobbyUI.SpacedThin(microLabel), 12, LobbyUI.AshDim,
            StorySequencer.Weight.Medium, TextAnchor.MiddleLeft);
        LobbyUI.Place(ml.rectTransform, topC, topC, topC, new Vector2(0f, -y), new Vector2(ColumnW, 16f));
        y += 16f + MicroGap;

        // the field itself
        field = LobbyUI.Input(parent, placeholder, password);
        var rt = field.GetComponent<RectTransform>();
        LobbyUI.Place(rt, topC, topC, topC, new Vector2(0f, -y), new Vector2(ColumnW, FieldH));

        // restyle the field to the AAA fields: BgPanel fill, square-ish (radius 6), Barlow text, dim
        // placeholder, 16px left pad. The Input() factory built a rounded Image already; retune it.
        var bg = field.GetComponent<Image>();
        if (bg != null) { bg.sprite = LobbyUI.RoundedSprite(6); bg.color = LobbyUI.BgPanel; }
        if (field.textComponent != null)
        {
            LobbyUI.ApplyBarlow(field.textComponent, StorySequencer.Weight.Regular);
            field.textComponent.fontSize = 18;
            field.textComponent.color = LobbyUI.AshText;
            PadField(field.textComponent.rectTransform);
        }
        if (field.placeholder is Text ph)
        {
            LobbyUI.ApplyBarlow(ph, StorySequencer.Weight.Regular);
            ph.fontSize = 18;
            ph.color = LobbyUI.AshDim;
            PadField(ph.rectTransform);
        }

        // subtle idle border + focus tracker
        LobbyUI.Border(rt, LobbyUI.EmberDim, 1f);
        var focus = field.gameObject.AddComponent<AuthFieldFocus>();
        focus.Init(field, bg);

        y += FieldH;
        return y;
    }

    // 16px horizontal pad (the AAA fields breathe more than the factory's 12).
    static void PadField(RectTransform rt)
    {
        if (rt == null) return;
        rt.offsetMin = new Vector2(16f, 4f);
        rt.offsetMax = new Vector2(-16f, -4f);
    }

    // Route a button's child label through Barlow at the given weight + size, UPPER already supplied.
    static void StyleButtonLabel(Button b, int size, StorySequencer.Weight weight)
    {
        if (b == null) return;
        var lbl = b.GetComponentInChildren<Text>();
        if (lbl == null) return;
        LobbyUI.ApplyBarlow(lbl, weight);
        lbl.fontSize = size;
    }

    // Tune the Button.colors for AAA feel. Primary: a touch brighter on hover, slightly pressed.
    // Secondary (ghost): the fill stays transparent; hover/press is carried by the label/border, but a
    // gentle tint multiplier still gives feedback on the (alpha-0) target graphic without showing a box.
    static void TuneButtonColors(Button b, bool primary)
    {
        if (b == null) return;
        var c = b.colors;
        c.fadeDuration = 0.12f;
        if (primary)
        {
            c.normalColor      = Color.white;                       // multiplies the amber fill -> amber
            c.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
            c.pressedColor     = new Color(0.90f, 0.90f, 0.90f, 1f);
            c.selectedColor    = Color.white;
            c.disabledColor    = new Color(0.5f, 0.5f, 0.5f, 0.6f);
        }
        else
        {
            // Ghost: the base fill is a faint warm wash (alpha 0.05). The color tint MULTIPLIES that fill,
            // so brightening the multiplier on hover/press lifts the wash gently without ever showing a
            // solid box. normalColor keeps the faint base; highlighted/pressed push it up a little.
            c.normalColor      = Color.white;
            c.highlightedColor = new Color(2.0f, 2.0f, 2.0f, 2.4f);  // faint wash -> slightly stronger
            c.pressedColor     = new Color(2.4f, 2.4f, 2.4f, 3.0f);
            c.selectedColor    = Color.white;
            c.disabledColor    = new Color(0.5f, 0.5f, 0.5f, 0.4f);
        }
        b.colors = c;
    }

    // ============================ LOGIC - UNCHANGED reducer/table wiring ============================

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

    // onEndEdit fires on focus-loss too - only submit on an actual Enter keypress (-> Login path).
    void OnFieldSubmit(string _)
    {
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) OnLoginClick();
    }

    void OnRegisterClick()
    {
        if (!Validate()) return;
        username = userField.text.Trim();
        SetStatus("Creating operator profile.", false);
        GameManager.Conn.Reducers.RegisterAccount(username, pinField.text);
    }

    void OnLoginClick()
    {
        if (!Validate()) return;
        username = userField.text.Trim();
        SetStatus("Verifying credentials.", false);
        GameManager.Conn.Reducers.Login(username, pinField.text);
    }

    bool Validate()
    {
        if (string.IsNullOrWhiteSpace(userField.text)) { SetStatus("Enter your call sign.", true); return false; }
        if (string.IsNullOrEmpty(pinField.text)) { SetStatus("Enter your access PIN.", true); return false; }
        if (GameManager.Conn == null || !GameManager.Ready) { SetStatus("Still connecting.", true); return false; }
        return true;
    }

    void OnAuthResult(Status s, string u, string verb)
    {
        // TaggedEnum variants are positional records; deconstruct as the autogen does
        // (e.g. `case Status.Failed(var reason)`).
        if (s is Status.Failed(var reason))
        {
            authCommitted = false;
            SetStatus(verb == "login"
                ? "Sign in failed. Check your call sign and PIN."
                : $"Registration failed. {reason}", true);
            return;
        }
        if (s is Status.OutOfEnergy)
        {
            authCommitted = false;
            SetStatus("Server out of energy. Try again.", true);
            return;
        }
        // Committed: identity is assigned; the LobbyMember row will arrive on a later tick.
        authCommitted = true;
        username = u;
        SetStatus("Welcome back, operator.", false);
        CheckExistingMember();
    }

    // Advance ONLY when the authoritative local row exists (review fix: row, not callback).
    void CheckExistingMember()
    {
        if (advanced) return;
        if (GameManager.Conn == null) return;
        var me = GameManager.Conn.Db.LobbyMember.Identity.Find(GameManager.LocalIdentity);
        if (me == null) return;            // not yet in cache - wait for the next insert/update
        advanced = true;
        if (string.IsNullOrEmpty(username)) username = me.Username;
        OnAuthenticated?.Invoke(username);
    }

    void SetStatus(string msg, bool isError)
    {
        if (status == null) return;
        status.text = msg;
        // success cue (EmberSoft) vs error (Crimson) vs neutral (AshDim).
        if (isError) status.color = LobbyUI.Crimson;
        else if (authCommitted) status.color = LobbyUI.EmberSoft;
        else status.color = LobbyUI.AshDim;
    }

    public void SetConnecting(string msg) => SetStatus(msg, false);
}

// AuthFieldFocus - tiny build-safe focus tracker for legacy InputField (which has no native focus event).
// Each Update it checks whether THIS field is the EventSystem's selected object and swaps the field fill +
// border to the focused look, restoring the idle look on blur. One class per name; null-safe throughout.
public class AuthFieldFocus : MonoBehaviour
{
    InputField field;
    Image bg;
    Image[] borderImages;   // the Border() child Image(s); we tint their Outline to amber on focus
    bool focused;

    public void Init(InputField f, Image fieldBg)
    {
        field = f;
        bg = fieldBg;
        // The Border() helper parents a child named "Border" carrying an Outline. Cache its Image so we can
        // recolor the Outline on focus. (Border draws the frame via Outline; the Image itself is alpha-0.)
        if (field != null)
            borderImages = field.GetComponentsInChildren<Image>(true);
    }

    void Update()
    {
        if (field == null) return;
        var es = UnityEngine.EventSystems.EventSystem.current;
        bool now = es != null && es.currentSelectedGameObject == field.gameObject;
        if (now == focused) return;
        focused = now;
        Apply();
    }

    void Apply()
    {
        if (bg != null)
            bg.color = focused ? LobbyUI.BgRaised : LobbyUI.BgPanel;

        if (borderImages != null)
        {
            var col = focused ? LobbyUI.Ember : LobbyUI.EmberDim;
            for (int i = 0; i < borderImages.Length; i++)
            {
                var img = borderImages[i];
                if (img == null || img.gameObject == this.gameObject) continue; // skip the field's own fill
                var ol = img.GetComponent<Outline>();
                if (ol != null) ol.effectColor = col;
            }
        }
    }
}
