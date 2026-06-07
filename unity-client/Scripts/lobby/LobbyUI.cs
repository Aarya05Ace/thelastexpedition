// LobbyUI.cs — THE LOST EXPEDITION lobby: code-driven uGUI factory helpers (PART D skin).
//
// Shared so the auth panel, carousel, HUD, and Patron bubble all build consistent legacy-uGUI
// widgets (Text / Button / InputField / Image) in code. Uses the built-in legacy uGUI (com.unity.ugui
// 2.0.0) — NO TMP asset wiring (TMP renders BLANK with no TMP Essentials imported). Faux-TMP polish is
// achieved via UnityEngine.UI.Shadow + Bold + a thin-space letter-spacing helper.
//
// PART D ADDITIONS (additive — every existing Panel/Label/Button/Input/Place/WorldLabel/DefaultFont
// surface is preserved so all callers still compile):
//   * an ember/ash color token palette + a type-scale const block,
//   * RoundedSprite(radius) — a CACHED code-gen 9-slice rounded-rect Texture2D->Sprite (generate ONCE
//     per radius, never per-panel, or it GC-thrashes), plus RoundedPanel / RoundedButton wrappers,
//   * Border / ShadowLabel / Vignette helpers,
//   * Spaced() — thin-space letter-spacing for the faux-TMP titles.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static class LobbyUI
{
    // ---- color tokens (amber/ember horror palette — NOT Fortnite blue/yellow) ----
    public static readonly Color BgDeep      = new Color(0.035f, 0.030f, 0.028f, 0.96f); // near-black warm
    public static readonly Color BgPanel     = new Color(0.075f, 0.062f, 0.052f, 0.92f); // dark panel
    public static readonly Color BgRaised    = new Color(0.115f, 0.095f, 0.078f, 0.95f); // raised chip
    public static readonly Color Ember       = new Color(1.00f, 0.55f, 0.18f, 1f);       // bright ember
    public static readonly Color EmberDim    = new Color(0.62f, 0.32f, 0.12f, 1f);       // hairline / borders
    public static readonly Color EmberSoft   = new Color(1.00f, 0.78f, 0.46f, 1f);       // soft title glow
    public static readonly Color AshText     = new Color(0.90f, 0.86f, 0.80f, 1f);       // primary text
    public static readonly Color AshDim      = new Color(0.62f, 0.58f, 0.53f, 1f);       // secondary text
    public static readonly Color ReadyGreen  = new Color(0.42f, 0.78f, 0.40f, 1f);       // READY pill
    public static readonly Color NotReadyGrey= new Color(0.50f, 0.48f, 0.46f, 1f);       // NOT READY pill
    public static readonly Color Crimson     = new Color(0.92f, 0.34f, 0.30f, 1f);       // error / fail
    public static readonly Color PatronGold  = new Color(0.96f, 0.82f, 0.50f, 1f);       // the Patron voice

    // ---- type scale ----
    public const int TitleSize  = 44;
    public const int HeaderSize = 22;
    public const int NameSize   = 30;
    public const int BodySize   = 16;
    public const int HintSize   = 13;
    public const int CtaSize    = 28;

    static Font _font;

    public static Font DefaultFont()
    {
        if (_font == null)
        {
            // Unity 6 ships LegacyRuntime.ttf; fall back to Arial for older editors.
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        return _font;
    }

    // Thin-space (U+2009) letter-spacing for faux-TMP titles. Cheap, no TMP needed.
    public static string Spaced(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length * 2);
        for (int i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (i < s.Length - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    // ===== legacy plain widgets (UNCHANGED surface) =====

    public static RectTransform Panel(Transform parent, string name, Color bg)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        return go.GetComponent<RectTransform>();
    }

    public static Text Label(Transform parent, string text, int size, Color color,
                             TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = DefaultFont();
        t.fontSize = size;
        t.color = color;
        t.alignment = anchor;
        t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    public static Button Button(Transform parent, string text, Color bg, Color fg)
    {
        var go = new GameObject("Button", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var label = Label(go.transform, text, 18, fg, TextAnchor.MiddleCenter);
        var lrt = label.rectTransform;
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        return btn;
    }

    // Returns the InputField; the placeholder/text children are created for it.
    public static InputField Input(Transform parent, string placeholder, bool password)
    {
        var go = new GameObject("InputField", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = RoundedSprite(10);
        img.type = Image.Type.Sliced;
        img.color = new Color(0.04f, 0.035f, 0.03f, 0.96f);
        var field = go.AddComponent<InputField>();

        var ph = Label(go.transform, placeholder, 16, AshDim);
        StretchPadded(ph.rectTransform);
        var txt = Label(go.transform, "", 16, AshText);
        StretchPadded(txt.rectTransform);
        txt.supportRichText = false;

        field.targetGraphic = img;
        field.textComponent = txt;
        field.placeholder = ph;
        field.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
        field.lineType = InputField.LineType.SingleLine;
        return field;
    }

    static void StretchPadded(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(12f, 4f);
        rt.offsetMax = new Vector2(-12f, -4f);
    }

    // Anchor + position helper (anchored from the given pivot/anchor pair).
    public static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
                             Vector2 anchoredPos, Vector2 size)
    {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;
    }

    // ===== PART D: rounded sprites (CACHED) + skinned widgets =====

    static readonly Dictionary<int, Sprite> _roundedCache = new();
    static Sprite _vignette;

    // 9-slice rounded-rect sprite, GENERATED ONCE per radius and cached (per perf: never per-panel).
    // White RGBA so Image.color tints it; alpha is the rounded mask.
    public static Sprite RoundedSprite(int radius)
    {
        if (radius < 1) radius = 1;
        if (_roundedCache.TryGetValue(radius, out var cached)) return cached;

        int size = radius * 2 + 2;                 // +2 px center stretch strip
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Distance from the nearest corner center -> antialiased rounded alpha.
            float cx = (x < radius) ? radius : (x > size - radius - 1 ? size - radius - 1 : x);
            float cy = (y < radius) ? radius : (y > size - radius - 1 ? size - radius - 1 : y);
            float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            float a = Mathf.Clamp01(radius - d + 0.5f);   // 1 inside, AA at the edge
            px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();

        var border = new Vector4(radius, radius, radius, radius);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                                   100f, 0, SpriteMeshType.FullRect, border);
        _roundedCache[radius] = sprite;
        return sprite;
    }

    public static Image RoundedPanel(Transform parent, string name, Color bg, int radius = 14)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = RoundedSprite(radius);
        img.type = Image.Type.Sliced;
        img.color = bg;
        return img;
    }

    public static Button RoundedButton(Transform parent, string text, Color bg, Color fg, int radius = 14)
    {
        var go = new GameObject("Button", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = RoundedSprite(radius);
        img.type = Image.Type.Sliced;
        img.color = bg;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
        colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
        btn.colors = colors;

        var label = Label(go.transform, text, 18, fg, TextAnchor.MiddleCenter);
        var lrt = label.rectTransform;
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        return btn;
    }

    // A 1px hairline border around an existing rect (a child Image with an inset outline sprite).
    public static void Border(RectTransform rt, Color color, float thickness = 1f)
    {
        var go = new GameObject("Border", typeof(RectTransform));
        go.transform.SetParent(rt, false);
        var brt = go.GetComponent<RectTransform>();
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
        brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.sprite = RoundedSprite(14);
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        // Hollow look: tint only — paired with a slightly smaller filled panel behind it. To keep this
        // cheap and dependency-free we render it as a thin tinted frame by disabling fill via alpha edge.
        var ol = go.AddComponent<Outline>();
        ol.effectColor = color;
        ol.effectDistance = new Vector2(thickness, thickness);
        img.color = new Color(color.r, color.g, color.b, 0f); // frame drawn by the Outline only
    }

    // Shadowed label = faux-TMP weight. Bold + UnityEngine.UI.Shadow.
    public static Text ShadowLabel(Transform parent, string text, int size, Color color, TextAnchor anchor)
    {
        var t = Label(parent, text, size, color, anchor);
        t.fontStyle = FontStyle.Bold;
        var sh = t.gameObject.AddComponent<Shadow>();
        sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
        sh.effectDistance = new Vector2(1.5f, -1.5f);
        return t;
    }

    // Code-gen radial-darken vignette (cached). Tints toward black at the edges.
    public static Image Vignette(Transform parent)
    {
        var go = new GameObject("Vignette", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.sprite = VignetteSprite();
        img.type = Image.Type.Simple;
        img.color = Color.white;
        img.raycastTarget = false;
        return img;
    }

    static Sprite VignetteSprite()
    {
        if (_vignette != null) return _vignette;
        const int N = 64;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        var px = new Color32[N * N];
        Vector2 c = new Vector2((N - 1) * 0.5f, (N - 1) * 0.5f);
        float maxD = c.magnitude;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float d = Vector2.Distance(new Vector2(x, y), c) / maxD; // 0 center -> 1 corner
            float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d - 0.45f) / 0.55f)) * 0.72f;
            px[y * N + x] = new Color32(0, 0, 0, (byte)(a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();
        _vignette = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
        return _vignette;
    }

    // ---- world-space label (two-pass black-shadow + colored text) ----
    // Shared by the Patron speech bubble AND the per-member floating name labels in the lineup.
    // Build it once, then each frame set .rect.position = lobbyCam.WorldToScreenPoint(head + up*N)
    // on a ScreenSpaceOverlay canvas (screen pixels == position), hiding when sp.z <= 0.
    public struct WorldLabelHandle
    {
        public RectTransform rect;
        public Text shadow;
        public Text fg;
        public CanvasGroup group;
    }

    public static WorldLabelHandle WorldLabel(Canvas canvas, int size, Color color, Vector2 boxSize)
        => WorldLabel(canvas.transform, size, color, boxSize);

    public static WorldLabelHandle WorldLabel(Transform parent, int size, Color color, Vector2 boxSize)
    {
        var go = new GameObject("WorldLabel", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = boxSize;
        var group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        var shadow = MakeWorldLabelText(rect, new Vector2(2f, -2f), Color.black, size);
        var fg = MakeWorldLabelText(rect, Vector2.zero, color, size);

        return new WorldLabelHandle { rect = rect, shadow = shadow, fg = fg, group = group };
    }

    static Text MakeWorldLabelText(RectTransform parent, Vector2 offset, Color color, int size)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = offset; rt.offsetMax = offset;
        var t = go.AddComponent<Text>();
        t.font = DefaultFont();
        t.fontSize = size;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.color = color;
        t.raycastTarget = false;
        return t;
    }
}
