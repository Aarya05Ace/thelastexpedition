// KillHud.cs — professional kill feedback, built in code (no emojis, no clutter):
//   • a crisp 4-tick HITMARKER that pops at screen-center on every shot that connects (white),
//     turning red + larger on a kill,
//   • a refined "ELIMINATED" confirmation that scales in, holds, and fades when a shot kills.
//
// Self-bootstraps via [RuntimeInitializeOnLoadMethod]; binds to PlayerCombat.OnEnemyHit / OnEnemyKilled,
// so it stays decoupled from the combat code. Pure Unity (UnityEngine + UnityEngine.UI + LobbyUI factory).
// Null-safe; never throws.

using UnityEngine;
using UnityEngine.UI;

public class KillHud : MonoBehaviour
{
    static readonly Color HitColor  = new Color(1f, 1f, 1f, 1f);
    static readonly Color KillColor = new Color(1f, 0.22f, 0.18f, 1f);

    const float MarkerDur = 0.22f;   // hitmarker pop length (s)
    const float KillDur   = 1.10f;   // "ELIMINATED" lifetime (s)

    RectTransform marker;
    CanvasGroup markerGroup;
    Image[] ticks = new Image[4];
    float markerT;
    bool markerKill;

    Text killText;
    CanvasGroup killGroup;
    float killT;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("KillHudHost");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<KillHud>();
    }

    void Awake() => Build();

    void OnEnable()
    {
        PlayerCombat.OnEnemyHit += OnHit;
        PlayerCombat.OnEnemyKilled += OnKill;
    }

    void OnDisable()
    {
        PlayerCombat.OnEnemyHit -= OnHit;
        PlayerCombat.OnEnemyKilled -= OnKill;
    }

    void OnHit()  { markerT = MarkerDur; markerKill = false; }
    void OnKill() { markerT = MarkerDur; markerKill = true; killT = KillDur; }

    void Build()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 150;   // above the HUD bars (100), below the death screen (200)
        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        var grp = gameObject.AddComponent<CanvasGroup>();
        grp.blocksRaycasts = false; grp.interactable = false;

        var root = (RectTransform)transform;

        // ---- HITMARKER: 4 diagonal ticks forming an X around the crosshair center ----
        var mGo = new GameObject("Hitmarker", typeof(RectTransform));
        mGo.transform.SetParent(root, false);
        marker = (RectTransform)mGo.transform;
        marker.anchorMin = marker.anchorMax = new Vector2(0.5f, 0.5f);
        marker.pivot = new Vector2(0.5f, 0.5f);
        marker.anchoredPosition = Vector2.zero;
        marker.sizeDelta = new Vector2(44f, 44f);
        markerGroup = mGo.AddComponent<CanvasGroup>();
        markerGroup.alpha = 0f;

        float[] ang = { 45f, 135f, 225f, 315f };
        for (int i = 0; i < 4; i++)
        {
            var tick = NewImage(marker, "Tick" + i, HitColor);
            var rt = tick.rectTransform;
            rt.sizeDelta = new Vector2(13f, 2.5f);
            float a = ang[i] * Mathf.Deg2Rad;
            rt.anchoredPosition = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 12f;
            rt.localRotation = Quaternion.Euler(0f, 0f, ang[i]);
            ticks[i] = tick;
        }

        // ---- "ELIMINATED" kill confirmation (just below center) ----
        killText = LobbyUI.ShadowLabel(root, "ELIMINATED", 30, KillColor, TextAnchor.MiddleCenter);
        killText.fontStyle = FontStyle.Bold;
        var krt = killText.rectTransform;
        krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 0.5f);
        krt.pivot = new Vector2(0.5f, 0.5f);
        krt.anchoredPosition = new Vector2(0f, -78f);
        krt.sizeDelta = new Vector2(520f, 48f);
        killGroup = killText.gameObject.AddComponent<CanvasGroup>();
        killGroup.alpha = 0f;
    }

    static Image NewImage(Transform parent, string name, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = c; img.raycastTarget = false;
        return img;
    }

    void Update()
    {
        // HITMARKER — pop in larger (1.4x) then settle to 1.0 while fading out.
        if (markerGroup != null)
        {
            if (markerT > 0f)
            {
                markerT -= Time.deltaTime;
                float k = Mathf.Clamp01(markerT / MarkerDur);   // 1 -> 0 over the pop
                Color c = markerKill ? KillColor : HitColor;
                for (int i = 0; i < 4; i++) if (ticks[i] != null) ticks[i].color = c;
                markerGroup.alpha = k;
                marker.localScale = Vector3.one * Mathf.Lerp(1.0f, 1.4f, k);
            }
            else if (markerGroup.alpha != 0f) markerGroup.alpha = 0f;
        }

        // "ELIMINATED" — fade in (first 15%), hold, fade out (last 40%); a gentle scale-in.
        if (killGroup != null)
        {
            if (killT > 0f)
            {
                killT -= Time.deltaTime;
                float p = 1f - Mathf.Clamp01(killT / KillDur);  // 0 -> 1 over the life
                float alpha = (p < 0.15f) ? (p / 0.15f) : (p > 0.60f ? (1f - p) / 0.40f : 1f);
                killGroup.alpha = Mathf.Clamp01(alpha);
                killText.rectTransform.localScale = Vector3.one * Mathf.SmoothStep(0.85f, 1f, Mathf.Clamp01(p / 0.25f));
            }
            else if (killGroup.alpha != 0f) killGroup.alpha = 0f;
        }
    }
}
