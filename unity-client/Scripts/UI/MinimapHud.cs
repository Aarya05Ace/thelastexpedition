// MinimapHud.cs — a REAL top-down minimap (top-right). A second ORTHOGRAPHIC camera renders the actual
// world from straight above into a RenderTexture, shown in a square panel with a single RED dot = YOU
// (always centered, because the camera follows you). It marks ONLY the player — no bot/NPC/anything markers
// (other entities appear merely as the genuine aerial view, never as radar blips).
//
// Self-bootstraps via [RuntimeInitializeOnLoadMethod]; waits for PlayerCombat.Local, then builds the camera +
// RenderTexture + UI. The minimap camera is throttled to ~10 fps (manual Render()) so the extra HDRP pass
// doesn't tank framerate. HDRP requires an HDAdditionalCameraData on every camera. Pure runtime; null-safe.

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.HighDefinition;

public class MinimapHud : MonoBehaviour
{
    const int   RT_SIZE    = 128;    // RenderTexture resolution (kept low — a 2nd HDRP camera is costly)
    const float PANEL      = 200f;   // on-screen square size (px @1920x1080 ref)
    const float MARGIN     = 24f;    // px from the top-right corner
    const float CAM_HEIGHT = 90f;    // metres the minimap cam sits above the player
    const float ORTHO_SIZE = 32f;    // half-extent shown (m); smaller = more zoomed in
    const float RENDER_HZ  = 4f;     // minimap re-render rate (low — 2nd-camera cost scales with this)

    Transform player;
    Camera mapCam;
    RenderTexture rt;
    float renderTimer;
    bool built;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("MinimapHost");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<MinimapHud>();
    }

    static Transform SafePlayer()
    {
        try { return PlayerCombat.Local != null ? PlayerCombat.Local.transform : null; } catch { return null; }
    }

    void Update()
    {
        if (built) return;
        var local = SafePlayer();
        if (local == null) return;   // not spawned yet — keep waiting
        Build(local);
        built = true;
    }

    void Build(Transform local)
    {
        player = local;

        // ---- RenderTexture the minimap camera draws into ----
        rt = new RenderTexture(RT_SIZE, RT_SIZE, 16, RenderTextureFormat.DefaultHDR) { name = "MinimapRT" };
        rt.Create();

        // ---- top-down orthographic camera (HDRP needs HDAdditionalCameraData) ----
        var camGo = new GameObject("MinimapCamera");
        Object.DontDestroyOnLoad(camGo);
        mapCam = camGo.AddComponent<Camera>();
        mapCam.orthographic = true;
        mapCam.orthographicSize = ORTHO_SIZE;
        mapCam.nearClipPlane = 0.3f;
        mapCam.farClipPlane = CAM_HEIGHT + 250f;
        mapCam.targetTexture = rt;
        mapCam.cullingMask = ~0;                         // the genuine world from above
        camGo.AddComponent<HDAdditionalCameraData>();    // required on every HDRP camera
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);  // straight down, north-up
        mapCam.enabled = false;                          // we drive it manually (throttled Render())

        // ---- UI: top-right panel = border + the live map + the YOU dot ----
        var canvasGo = new GameObject("MinimapCanvas");
        Object.DontDestroyOnLoad(canvasGo);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90;                        // above world, below the death screen
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        var grp = canvasGo.AddComponent<CanvasGroup>();
        grp.blocksRaycasts = false; grp.interactable = false;

        Vector2 center = new Vector2(-(PANEL / 2f + MARGIN), -(PANEL / 2f + MARGIN)); // from the top-right corner

        // dark border behind the map
        var border = NewImage(canvasGo.transform, "MinimapBorder", new Color(0.05f, 0.05f, 0.06f, 0.9f));
        Place(border.rectTransform, center, new Vector2(PANEL + 8f, PANEL + 8f));

        // the live aerial map (RenderTexture)
        var mapGo = new GameObject("MinimapImage", typeof(RectTransform));
        mapGo.transform.SetParent(canvasGo.transform, false);
        var raw = mapGo.AddComponent<RawImage>();
        raw.texture = rt; raw.raycastTarget = false;
        Place(raw.rectTransform, center, new Vector2(PANEL, PANEL));

        // YOU — a red dot dead-center (the cam follows you, so you are always centered). ONLY the player.
        var dot = NewImage(mapGo.transform, "YouDot", new Color(1f, 0.15f, 0.12f, 1f));
        var drt = dot.rectTransform;
        drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(11f, 11f);
        drt.anchoredPosition = Vector2.zero;

        // place the camera over the player immediately so the first frame is correct
        SnapCamToPlayer();
    }

    static Image NewImage(Transform parent, string name, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = c; img.raycastTarget = false;
        return img;
    }

    // Anchor + pivot at the screen's top-right; anchoredPosition is the rect CENTER relative to that corner.
    static void Place(RectTransform rt, Vector2 centerFromTopRight, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = centerFromTopRight;
        rt.sizeDelta = size;
    }

    void SnapCamToPlayer()
    {
        if (mapCam == null || player == null) return;
        var p = player.position;
        mapCam.transform.position = new Vector3(p.x, p.y + CAM_HEIGHT, p.z);
    }

    void LateUpdate()
    {
        if (mapCam == null) return;
        if (player == null) { player = SafePlayer(); if (player == null) return; }

        SnapCamToPlayer();   // follow the player (north-up; you stay centered)

        renderTimer += Time.deltaTime;
        if (renderTimer >= 1f / RENDER_HZ)
        {
            renderTimer = 0f;
            mapCam.Render();   // manual, throttled render of the minimap
        }
    }

    void OnDestroy()
    {
        if (mapCam != null) Destroy(mapCam.gameObject);
        if (rt != null) { rt.Release(); Destroy(rt); }
    }
}
