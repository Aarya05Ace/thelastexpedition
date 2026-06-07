// IntroCrawl.cs — THE LAST EXPEDITION cold-open: a one-shot cinematic premise that sets the fantasy
// in a few terse, second-person lines (RDR2 / The Last of Us register), then gets out of the way.
//
// Plays ONCE per process (a static guard) on entering gameplay. A full-screen warm-black backdrop
// fades up with stacked lines, holds, then fades out and self-destructs. Skippable with ANY key or a
// click — a skip jumps the envelope straight to the fade-out tail so it never feels like a wall.
//
// Self-bootstraps via [RuntimeInitializeOnLoadMethod] exactly like KillHud/GameHud. Pure Unity
// (UnityEngine + UnityEngine.UI) on a ScreenSpaceOverlay canvas built through the LobbyUI factory for
// a consistent skin. NO SpacetimeDB types -> no Vector3 alias. NO UnityEditor use -> no #if guards.
// No emoji. Null-safe; never throws.

using UnityEngine;
using UnityEngine.UI;

public class IntroCrawl : MonoBehaviour
{
    // The premise, in the brief's voice. Terse, atmospheric, second-person. First line is the mood
    // beat (rendered as the spaced faux-TMP "title"); the last is the imperative that hands you the verb.
    static readonly string[] Lines =
    {
        "Dusk. A billionaire's private island.",
        "Your sister, Mara, is somewhere inside his house.",
        "No one here knows the whole truth — but everyone knows a piece.",
        "Make them talk.",
    };

    // Timing envelope (seconds). FadeIn -> Hold -> FadeOut -> destroy.
    const float FadeIn  = 1.4f;
    const float Hold    = 4.2f;
    const float FadeOut = 1.6f;
    const float Total   = FadeIn + Hold + FadeOut;

    // Once per process: the cold-open should not replay on scene reloads / re-spawns.
    static bool _shown;

    CanvasGroup group;   // drives the whole crawl's alpha
    float t;             // elapsed seconds
    bool skipping;       // a skip rebases the clock to the start of the fade-out

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_shown) return;
        _shown = true;
        var host = new GameObject("IntroCrawlHost");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<IntroCrawl>();
    }

    void Awake() => Build();

    void Build()
    {
        // ---- ScreenSpaceOverlay canvas, on top of everything at game start ----
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 250;   // above the death screen (200) and all HUD — it's the cold open

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // Whole crawl on one CanvasGroup so a single alpha drives the fade; never eats clicks.
        group = gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;

        var root = (RectTransform)transform;

        // ---- Full-screen warm-black backdrop ----
        var backdrop = LobbyUI.Panel(root, "Backdrop", new Color(0.02f, 0.018f, 0.016f, 1f));
        backdrop.anchorMin = Vector2.zero; backdrop.anchorMax = Vector2.one;
        backdrop.offsetMin = Vector2.zero; backdrop.offsetMax = Vector2.zero;
        var bImg = backdrop.GetComponent<Image>();
        if (bImg != null) bImg.raycastTarget = false;

        // A faint vignette over the backdrop for depth (cached code-gen sprite, no asset load).
        LobbyUI.Vignette(root);

        // ---- Centered stack of lines ----
        // A transparent column anchored to screen center; lines stack downward from it.
        var column = LobbyUI.Panel(root, "Lines", new Color(0, 0, 0, 0));
        column.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(column, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      Vector2.zero, new Vector2(1180f, 360f));

        const float lineH = 64f;
        float startY = ((Lines.Length - 1) * lineH) * 0.5f;   // center the stack vertically

        for (int i = 0; i < Lines.Length; i++)
        {
            bool isTitle = i == 0;
            bool isLast  = i == Lines.Length - 1;

            int size   = isTitle ? LobbyUI.NameSize : (isLast ? LobbyUI.HeaderSize : LobbyUI.HeaderSize - 2);
            Color col  = isTitle ? LobbyUI.EmberSoft : (isLast ? LobbyUI.Ember : LobbyUI.AshText);
            string txt = isTitle ? LobbyUI.Spaced(Lines[i]) : Lines[i];

            var line = LobbyUI.ShadowLabel(column, txt, size, col, TextAnchor.MiddleCenter);
            line.horizontalOverflow = HorizontalWrapMode.Wrap;
            var lrt = line.rectTransform;
            lrt.anchorMin = new Vector2(0.5f, 0.5f);
            lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.anchoredPosition = new Vector2(0f, startY - i * lineH);
            lrt.sizeDelta = new Vector2(1140f, lineH);
        }

        // A quiet skip hint, low and dim — never the focus.
        var hint = LobbyUI.ShadowLabel(root, "press any key to continue", LobbyUI.HintSize,
                                       LobbyUI.AshDim, TextAnchor.LowerCenter);
        var hrt = hint.rectTransform;
        hrt.anchorMin = new Vector2(0.5f, 0f); hrt.anchorMax = new Vector2(0.5f, 0f);
        hrt.pivot = new Vector2(0.5f, 0f);
        hrt.anchoredPosition = new Vector2(0f, 48f);
        hrt.sizeDelta = new Vector2(420f, 22f);
    }

    void Update()
    {
        // Skip on any key / click — jump straight to the start of the fade-out tail (only if not already
        // past it), so the lines never just vanish; they fall away.
        if (!skipping && (Input.anyKeyDown || Input.GetMouseButtonDown(0)))
        {
            skipping = true;
            float fadeOutStart = FadeIn + Hold;
            if (t < fadeOutStart) t = fadeOutStart;
        }

        t += Time.deltaTime;

        // Envelope: ramp up over FadeIn, hold at 1, ramp down over FadeOut.
        float a;
        if (t < FadeIn)
            a = Mathf.Clamp01(t / FadeIn);
        else if (t < FadeIn + Hold)
            a = 1f;
        else
            a = Mathf.Clamp01(1f - (t - (FadeIn + Hold)) / FadeOut);

        if (group != null) group.alpha = a;

        if (t >= Total)
            Destroy(gameObject);
    }
}
