// MinimapHud.cs - a REAL top-down minimap (top-right) PLUS an expandable Assassin's Creed / Fortnite-style
// full-screen map (press M to toggle, M or Esc to close).
//
// SMALL MINIMAP: a second ORTHOGRAPHIC camera renders the actual world from straight above into a RenderTexture,
// shown in a square panel with a single RED dot = YOU (always centered, because the camera follows you), PLUS a
// live amber DIRECTION WAYPOINT (a pin when the objective is on the panel, an edge arrow pointing toward it when
// it is off-panel) so the player always knows where to go.
//
// BIG MAP (press M): the same top-down render, but BIG + framed (dark + amber), drawn by a SECOND wide ortho
// camera into its own larger RenderTexture (rendered ONLY while the map is open, so closed cost == today). It
// pinpoints the LIVE mission OBJECTIVE (Cabin / Extraction / The Curator, chosen by RescueMission.Phase) with a
// LABEL + distance, a player arrow at center (the big cam follows you), a route line from you to the objective,
// and a small legend. Markers are projected world-XZ -> RT-UV -> screen px through the SAME ortho camera that
// renders the view, so pins land correctly at any zoom.
//
// Self-bootstraps via [RuntimeInitializeOnLoadMethod]; waits for PlayerCombat.Local, then builds the cameras +
// RenderTextures + UI. The minimap camera is throttled to ~4 fps; the big camera renders ~12 fps and ONLY while
// open. HDRP requires an HDAdditionalCameraData on every camera. Pure runtime; null-safe. No em dashes, no emoji.
//
// Projection math (north-up ortho cam, rotation Euler(90,0,0), NO yaw): for a world point w and the cam center
// camPos with orthographicSize o and aspect a (RT is square -> a = 1):
//   u = 0.5 + (w.x - camPos.x) / (2 * o * a)      // +X world = +U (east = right)
//   v = 0.5 + (w.z - camPos.z) / (2 * o)          // +Z world = +V (north = up)
// then panel px = (u * P, v * P) for a child anchored to the panel bottom-left. Same formula for both panels;
// only P (panel px) and o (orthographicSize) differ, so pins stay correct at the big map's wider zoom.

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.HighDefinition;

public class MinimapHud : MonoBehaviour
{
    const int   RT_SIZE    = 128;    // small-minimap RenderTexture resolution (low - a 2nd HDRP camera is costly)
    const float PANEL      = 200f;   // on-screen square size (px @1920x1080 ref)
    const float MARGIN     = 24f;    // px from the top-right corner
    const float CAM_HEIGHT = 90f;    // metres the minimap cam sits above the player
    const float ORTHO_SIZE = 32f;    // small-map half-extent shown (m); smaller = more zoomed in
    const float RENDER_HZ  = 4f;     // minimap re-render rate (low - 2nd-camera cost scales with this)

    // ---- big map (press M) tunables ----------------------------------------------------------------
    const int   BIG_RT_SIZE  = 1024;  // big-map RenderTexture resolution (only rendered while open)
    const float BIG_ORTHO    = 140f;  // big-map half-extent (m): 280m across frames spawn + cabin + boss easily
    const float BIG_RENDER_HZ = 12f;  // big-map re-render rate (only ticks while open)
    const float MAP_PANEL_W  = 1180f; // big framed panel width  (px @1920x1080 ref)
    const float MAP_PANEL_H  = 920f;  // big framed panel height (px @1920x1080 ref)
    const float MAP_INNER    = 18f;   // inset of the map render inside the frame (px)
    const float EDGE_PAD     = 14f;   // inset for clamped off-frame edge markers (px)

    // ---- shared palette (matches the cabin beacon / waypoint amber in RescueMission) ----------------
    static readonly Color Amber     = new Color(1f,    0.55f, 0.05f, 1f);
    static readonly Color AmberSoft = new Color(1f,    0.62f, 0.18f, 1f);
    static readonly Color DarkFrame = new Color(0.06f, 0.05f, 0.04f, 0.97f);
    static readonly Color Scrim     = new Color(0f,    0f,    0f,    0.6f);
    static readonly Color Parchment = new Color(0.92f, 0.88f, 0.78f, 1f);
    static readonly Color YouRed    = new Color(1f,    0.15f, 0.12f, 1f);

    // ---- runtime state ------------------------------------------------------------------------------
    Transform player;
    Camera mapCam;
    RenderTexture rt;
    float renderTimer;
    bool built;

    // big map
    bool expanded;
    Camera bigCam;
    RenderTexture bigRt;
    float bigRenderTimer;
    GameObject bigRoot;          // the big-map canvas root (toggled active)
    RectTransform bigMapRect;    // the big RawImage rect (pin parent)
    GameObject bigRouteGo;       // route line (you -> objective) on the big map
    GameObject bigPinGo;         // objective pin on the big map
    Text bigPinLabel;            // objective label text on the big map
    GameObject bigPlayerArrowGo; // player facing arrow at the big-map center
    Text bigTitleObjective;      // the live objective line in the title bar
    Text bigLegendObjective;     // the objective name + distance in the legend

    // small minimap waypoint
    RectTransform miniMapRect;   // the small RawImage rect (waypoint parent)
    GameObject miniPinGo;        // amber pin shown when the objective is inside the panel
    GameObject miniArrowGo;      // amber edge arrow shown when the objective is off-panel
    GameObject miniRoot;         // the minimap canvas root (hidden by the in-game gate pre-game)

    // cursor-lock restore (the big map frees the cursor for clicks; ExpeditionCamera owns the lock)
    ExpeditionCamera orbit;
    bool savedLookEnabled;

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
        if (!built)
        {
            var local = SafePlayer();
            if (local == null) return;   // not spawned yet - keep waiting
            Build(local);
            built = true;
            return;
        }

        // IN-GAME GATE: hide the minimap (and force the big map closed) unless the player is really in the world.
        // Blocks the M toggle during auth/lobby + the deploy cutscene so the map can never open pre-game.
        if (!NetworkedWorld.GameplayActive || DeployCutscene.Active)
        {
            if (miniRoot != null) miniRoot.SetActive(false);
            if (expanded) Toggle();   // close the big map if it was somehow open
            else if (bigRoot != null) bigRoot.SetActive(false);
            return;
        }
        if (miniRoot != null && !miniRoot.activeSelf) miniRoot.SetActive(true);

        // toggle the big map; Esc only closes (so it never opens it).
        if (Input.GetKeyDown(KeyCode.M)) Toggle();
        else if (expanded && Input.GetKeyDown(KeyCode.Escape)) Toggle();
    }

    // ================================================================================================
    // BUILD
    // ================================================================================================

    void Build(Transform local)
    {
        player = local;

        // ---- RenderTexture the small minimap camera draws into ----
        rt = new RenderTexture(RT_SIZE, RT_SIZE, 16, RenderTextureFormat.DefaultHDR) { name = "MinimapRT" };
        rt.Create();

        // ---- top-down orthographic camera (HDRP needs HDAdditionalCameraData) ----
        mapCam = MakeOrthoCam("MinimapCamera", ORTHO_SIZE, rt);

        // ---- big-map wide ortho camera + its own larger RT (rendered ONLY while open) ----
        bigRt = new RenderTexture(BIG_RT_SIZE, BIG_RT_SIZE, 24, RenderTextureFormat.DefaultHDR) { name = "BigMapRT" };
        bigRt.Create();
        bigCam = MakeOrthoCam("BigMapCamera", BIG_ORTHO, bigRt);

        BuildMinimapUi();
        BuildBigMapUi();

        // place the cameras over the player immediately so the first frame is correct
        SnapCamsToPlayer();
    }

    // One straight-down, north-up (no yaw) ortho camera into the given RT. HDRP-safe, driven manually.
    static Camera MakeOrthoCam(string name, float orthoSize, RenderTexture target)
    {
        var camGo = new GameObject(name);
        Object.DontDestroyOnLoad(camGo);
        var c = camGo.AddComponent<Camera>();
        c.orthographic = true;
        c.orthographicSize = orthoSize;
        c.nearClipPlane = 0.3f;
        c.farClipPlane = CAM_HEIGHT + 400f;   // ample for both zooms
        c.targetTexture = target;
        c.cullingMask = ~0;                    // the genuine world from above
        camGo.AddComponent<HDAdditionalCameraData>();              // required on every HDRP camera
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);  // straight down, north-up (NO yaw - projection)
        c.enabled = false;                     // we drive it manually (throttled Render())
        return c;
    }

    void BuildMinimapUi()
    {
        var canvasGo = NewCanvas("MinimapCanvas", 90, false);   // above world, below the death screen (200)
        miniRoot = canvasGo;   // cached so the in-game gate can hide the minimap pre-game / during the cutscene

        Vector2 center = new Vector2(-(PANEL / 2f + MARGIN), -(PANEL / 2f + MARGIN)); // from the top-right corner

        // dark border behind the map
        var border = NewImage(canvasGo.transform, "MinimapBorder", new Color(0.05f, 0.05f, 0.06f, 0.9f));
        PlaceTopRight(border.rectTransform, center, new Vector2(PANEL + 8f, PANEL + 8f));

        // the live aerial map (RenderTexture)
        var mapGo = new GameObject("MinimapImage", typeof(RectTransform));
        mapGo.transform.SetParent(canvasGo.transform, false);
        var raw = mapGo.AddComponent<RawImage>();
        raw.texture = rt; raw.raycastTarget = false;
        PlaceTopRight(raw.rectTransform, center, new Vector2(PANEL, PANEL));
        miniMapRect = raw.rectTransform;

        // amber direction waypoint: a pin (objective on-panel) + an edge arrow (objective off-panel). Both
        // anchored to the RawImage bottom-left and repositioned each frame; only one is visible at a time.
        miniPinGo   = MakePin(miniMapRect, "MiniObjectivePin", 12f, Amber);
        miniArrowGo = MakeArrow(miniMapRect, "MiniObjectiveArrow", 18f, Amber);
        miniPinGo.SetActive(false);
        miniArrowGo.SetActive(false);

        // YOU - a red dot dead-center (the cam follows you, so you are always centered). ONLY the player.
        var dot = NewImage(mapGo.transform, "YouDot", YouRed);
        var drt = dot.rectTransform;
        drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(11f, 11f);
        drt.anchoredPosition = Vector2.zero;
    }

    void BuildBigMapUi()
    {
        var canvasGo = NewCanvas("BigMapCanvas", 95, true);   // above the minimap (90), below the death screen (200)
        bigRoot = canvasGo;

        // 1) full-screen dim scrim - ALSO the raycast catcher that blocks gameplay clicks while open.
        var scrim = NewImage(canvasGo.transform, "MapScrim", Scrim);
        scrim.raycastTarget = true;
        Stretch(scrim.rectTransform);

        // 2) centered framed panel (dark) + a thin amber border inset 4px.
        var frame = NewImage(canvasGo.transform, "MapFrame", DarkFrame);
        frame.raycastTarget = true;
        CenterRect(frame.rectTransform, MAP_PANEL_W, MAP_PANEL_H);

        var amberBorder = NewImage(frame.transform, "MapAmberBorder", Amber);
        amberBorder.raycastTarget = false;
        StretchInset(amberBorder.rectTransform, 4f);

        var inner = NewImage(amberBorder.transform, "MapInner", DarkFrame);
        inner.raycastTarget = false;
        StretchInset(inner.rectTransform, 3f);

        // 3) the big wide ortho render, filling the inner frame.
        var rawGo = new GameObject("BigMapImage", typeof(RectTransform));
        rawGo.transform.SetParent(inner.transform, false);
        var raw = rawGo.AddComponent<RawImage>();
        raw.texture = bigRt; raw.raycastTarget = false;
        StretchInset(raw.rectTransform, MAP_INNER);
        bigMapRect = raw.rectTransform;

        // a faint parchment tint over the render for the AC feel.
        var tint = NewImage(bigMapRect, "MapParchmentTint", new Color(1f, 0.78f, 0.42f, 0.06f));
        tint.raycastTarget = false;
        Stretch(tint.rectTransform);

        // 4) title bar (top of the frame): "MAP" + the live objective line.
        var title = NewText(frame.transform, "MapTitle", "MAP", StorySequencer.Weight.Bold, 30, Parchment);
        title.alignment = TextAnchor.UpperLeft;
        var trt = title.rectTransform;
        trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(0f, 1f); trt.pivot = new Vector2(0f, 1f);
        trt.anchoredPosition = new Vector2(MAP_INNER + 6f, -10f);
        trt.sizeDelta = new Vector2(420f, 40f);

        bigTitleObjective = NewText(frame.transform, "MapTitleObjective", "", StorySequencer.Weight.Regular, 18, AmberSoft);
        bigTitleObjective.alignment = TextAnchor.UpperLeft;
        var otr = bigTitleObjective.rectTransform;
        otr.anchorMin = new Vector2(0f, 1f); otr.anchorMax = new Vector2(0f, 1f); otr.pivot = new Vector2(0f, 1f);
        otr.anchoredPosition = new Vector2(MAP_INNER + 8f, -46f);
        otr.sizeDelta = new Vector2(MAP_PANEL_W - 2f * MAP_INNER, 26f);

        // 5) objective pin + label on the map (anchored to the map rect bottom-left, moved each frame).
        bigPinGo = MakePin(bigMapRect, "BigObjectivePin", 20f, Amber);
        bigPinGo.SetActive(false);
        bigPinLabel = NewText(bigMapRect, "BigObjectiveLabel", "", StorySequencer.Weight.SemiBold, 17, Parchment);
        bigPinLabel.alignment = TextAnchor.MiddleLeft;
        bigPinLabel.rectTransform.anchorMin = bigPinLabel.rectTransform.anchorMax = new Vector2(0f, 0f);
        bigPinLabel.rectTransform.pivot = new Vector2(0f, 0.5f);
        bigPinLabel.rectTransform.sizeDelta = new Vector2(280f, 24f);
        bigPinLabel.gameObject.SetActive(false);

        // 6) player arrow at the map center (the big cam follows the player, so the player is centered).
        bigPlayerArrowGo = MakeArrow(bigMapRect, "BigPlayerArrow", 26f, Color.white);
        var part = (RectTransform)bigPlayerArrowGo.transform;
        part.anchorMin = part.anchorMax = new Vector2(0.5f, 0.5f);
        part.pivot = new Vector2(0.5f, 0.5f);
        part.anchoredPosition = Vector2.zero;

        // 7) waypoint route line (you-center -> objective) as a thin rotated amber image, pivot at the player end.
        bigRouteGo = new GameObject("BigRoute", typeof(RectTransform));
        bigRouteGo.transform.SetParent(bigMapRect, false);
        var rline = bigRouteGo.AddComponent<Image>();
        rline.color = new Color(Amber.r, Amber.g, Amber.b, 0.85f);
        rline.raycastTarget = false;
        var rrt = rline.rectTransform;
        rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0.5f);   // player center
        rrt.pivot = new Vector2(0f, 0.5f);                          // grow from the player toward the objective
        rrt.sizeDelta = new Vector2(0f, 3f);
        bigRouteGo.SetActive(false);

        // keep the player arrow + label drawn on top of the route line.
        bigRouteGo.transform.SetSiblingIndex(0);

        // 8) legend (bottom-left of the frame): swatches + the active objective + distance.
        BuildLegend(frame.transform);

        canvasGo.SetActive(false);   // closed by default
    }

    void BuildLegend(Transform frame)
    {
        float lx = MAP_INNER + 6f;
        float ly = MAP_INNER + 6f;

        // a small backing panel for readability
        var panel = NewImage(frame, "LegendPanel", new Color(0f, 0f, 0f, 0.45f));
        panel.raycastTarget = false;
        var prt = panel.rectTransform;
        prt.anchorMin = prt.anchorMax = new Vector2(0f, 0f); prt.pivot = new Vector2(0f, 0f);
        prt.anchoredPosition = new Vector2(lx, ly);
        prt.sizeDelta = new Vector2(290f, 118f);

        // rows (top to bottom inside the panel)
        LegendRow(panel.transform, 86f, Amber,       "diamond", "Objective");
        LegendRow(panel.transform, 60f, Color.white, "arrow",   "You");
        LegendRow(panel.transform, 34f, Amber,       "line",    "Route");

        // active objective name + distance (updated each frame while open)
        bigLegendObjective = NewText(panel.transform, "LegendObjective", "", StorySequencer.Weight.Regular, 15, Parchment);
        bigLegendObjective.alignment = TextAnchor.LowerLeft;
        var lor = bigLegendObjective.rectTransform;
        lor.anchorMin = lor.anchorMax = new Vector2(0f, 0f); lor.pivot = new Vector2(0f, 0f);
        lor.anchoredPosition = new Vector2(10f, 8f);
        lor.sizeDelta = new Vector2(270f, 20f);
    }

    void LegendRow(Transform parent, float y, Color swatchCol, string swatchKind, string label)
    {
        GameObject swatch;
        if (swatchKind == "arrow") swatch = MakeArrow(parent as RectTransform, "Sw_" + label, 14f, swatchCol);
        else if (swatchKind == "line")
        {
            var img = NewImage(parent, "Sw_" + label, swatchCol);
            img.raycastTarget = false;
            var r = img.rectTransform;
            r.anchorMin = r.anchorMax = new Vector2(0f, 0f); r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(18f, 3f);
            swatch = img.gameObject;
        }
        else swatch = MakePin(parent as RectTransform, "Sw_" + label, 12f, swatchCol);

        var srt = (RectTransform)swatch.transform;
        srt.anchorMin = srt.anchorMax = new Vector2(0f, 0f); srt.pivot = new Vector2(0.5f, 0.5f);
        srt.anchoredPosition = new Vector2(20f, y);

        var txt = NewText(parent, "Lbl_" + label, label, StorySequencer.Weight.Regular, 15, Parchment);
        txt.alignment = TextAnchor.MiddleLeft;
        var trt = txt.rectTransform;
        trt.anchorMin = trt.anchorMax = new Vector2(0f, 0f); trt.pivot = new Vector2(0f, 0.5f);
        trt.anchoredPosition = new Vector2(40f, y);
        trt.sizeDelta = new Vector2(230f, 20f);
    }

    // ================================================================================================
    // TOGGLE
    // ================================================================================================

    void Toggle()
    {
        expanded = !expanded;
        if (bigRoot != null) bigRoot.SetActive(expanded);

        // Free the cursor so the player can interact with the map; restore exactly what it was on close.
        // ExpeditionCamera owns Cursor.lockState (it re-asserts LockCursor(LookEnabled) every LateUpdate), so
        // toggling LookEnabled is the correct, convention-respecting way to free / re-lock the cursor.
        if (orbit == null) orbit = ResolveOrbit();
        if (orbit != null)
        {
            if (expanded)
            {
                savedLookEnabled = orbit.LookEnabled;
                orbit.LookEnabled = false;   // frees the cursor + pauses mouse-look while the map is open
            }
            else
            {
                orbit.LookEnabled = savedLookEnabled;   // restore prior look/cursor state
            }
        }

        if (expanded) { bigRenderTimer = 999f; RenderBig(); }   // render one big frame immediately on open
    }

    static ExpeditionCamera ResolveOrbit()
    {
        try { return LocalPlayer.ActiveCamera != null ? LocalPlayer.ActiveCamera.GetComponent<ExpeditionCamera>() : null; }
        catch { return null; }
    }

    // ================================================================================================
    // PER-FRAME: follow + render + markers
    // ================================================================================================

    void SnapCamsToPlayer()
    {
        if (player == null) return;
        var p = player.position;
        var top = new Vector3(p.x, p.y + CAM_HEIGHT, p.z);
        if (mapCam != null) mapCam.transform.position = top;
        if (bigCam != null) bigCam.transform.position = top;
    }

    void RenderBig()
    {
        bigRenderTimer += Time.deltaTime;
        if (bigCam != null && bigRenderTimer >= 1f / BIG_RENDER_HZ)
        {
            bigRenderTimer = 0f;
            bigCam.Render();   // manual, throttled - only called while expanded
        }
    }

    void LateUpdate()
    {
        // IN-GAME GATE: no follow/render/marker work pre-game or during the deploy cutscene.
        if (!NetworkedWorld.GameplayActive || DeployCutscene.Active) return;
        if (mapCam == null) return;
        if (player == null) { player = SafePlayer(); if (player == null) return; }

        SnapCamsToPlayer();   // follow the player (north-up; you stay centered on both maps)

        renderTimer += Time.deltaTime;
        if (renderTimer >= 1f / RENDER_HZ)
        {
            renderTimer = 0f;
            mapCam.Render();   // manual, throttled render of the small minimap
        }

        // ---- live objective + its world position from the mission phase ----
        Vector3 objPos; string objLabel;
        bool haveObjective = ResolveObjective(out objPos, out objLabel);

        // small minimap waypoint (always-on direction), every frame (cheap RectTransform math).
        UpdateMiniWaypoint(haveObjective, objPos);

        // big map only when open.
        if (expanded)
        {
            RenderBig();
            UpdateBigMap(haveObjective, objPos, objLabel);
        }
    }

    // The active objective for the current phase. Returns false (and hides all markers) when there is none
    // (Victory) or the position is not yet set. Null/zero-safe: gates the boss pin on Phase == BossFight.
    static bool ResolveObjective(out Vector3 pos, out string label)
    {
        pos = Vector3.zero; label = "";
        RescuePhase phase;
        try { phase = RescueMission.Phase; } catch { return false; }

        switch (phase)
        {
            case RescuePhase.TrekToCabin:
            case RescuePhase.Reunion:
                pos = SafeStatic(() => RescueMission.CabinPosition);
                label = "Cabin (your sister)";
                break;
            case RescuePhase.Escape:
            case RescuePhase.Confront:
                pos = SafeStatic(() => RescueMission.LandingPosition);
                label = "Extraction (the shore)";
                break;
            case RescuePhase.BossFight:
                pos = SafeStatic(() => RescueMission.BossPosition);
                label = "The Curator";
                break;
            default:
                return false;   // Victory: no marker
        }

        // a not-yet-set position reads as (0,0,0); treat that as "no objective yet" so we never pin the origin.
        if (pos.sqrMagnitude < 1e-4f) return false;
        return true;
    }

    static Vector3 SafeStatic(System.Func<Vector3> get)
    {
        try { return get(); } catch { return Vector3.zero; }
    }

    // ---- small minimap: pin if the objective is on the panel, else an edge arrow pointing toward it ----
    void UpdateMiniWaypoint(bool haveObjective, Vector3 objPos)
    {
        if (!haveObjective || mapCam == null)
        {
            if (miniPinGo != null) miniPinGo.SetActive(false);
            if (miniArrowGo != null) miniArrowGo.SetActive(false);
            return;
        }

        float u, v;
        WorldToUv(mapCam, objPos, out u, out v);
        bool inside = u >= 0f && u <= 1f && v >= 0f && v <= 1f;

        if (inside)
        {
            if (miniArrowGo != null) miniArrowGo.SetActive(false);
            if (miniPinGo != null)
            {
                miniPinGo.SetActive(true);
                ((RectTransform)miniPinGo.transform).anchoredPosition = new Vector2(u * PANEL, v * PANEL);
            }
        }
        else
        {
            if (miniPinGo != null) miniPinGo.SetActive(false);
            if (miniArrowGo != null)
            {
                miniArrowGo.SetActive(true);
                PlaceEdgeArrow((RectTransform)miniArrowGo.transform, u, v, PANEL, PANEL, EDGE_PAD);
            }
        }
    }

    // ---- big map: objective pin + label, player facing arrow, route line, legend ----
    void UpdateBigMap(bool haveObjective, Vector3 objPos, string objLabel)
    {
        if (bigMapRect == null) return;
        float w = bigMapRect.rect.width;
        float h = bigMapRect.rect.height;

        // title + legend objective text (live).
        string objectiveLine = SafeObjectiveLine();
        if (bigTitleObjective != null) bigTitleObjective.text = objectiveLine;

        // player facing arrow at center, rotated to the player's heading (north-up screen: -atan2(fwd.x, fwd.z)).
        if (bigPlayerArrowGo != null)
        {
            Vector3 fwd = PlayerFacing();
            float deg = -Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            bigPlayerArrowGo.transform.localRotation = Quaternion.Euler(0f, 0f, deg);
        }

        if (!haveObjective || bigCam == null)
        {
            if (bigPinGo != null) bigPinGo.SetActive(false);
            if (bigPinLabel != null) bigPinLabel.gameObject.SetActive(false);
            if (bigRouteGo != null) bigRouteGo.SetActive(false);
            if (bigLegendObjective != null) bigLegendObjective.text = "You made it home.";
            return;
        }

        float u, v;
        WorldToUv(bigCam, objPos, out u, out v);
        bool inside = u >= 0f && u <= 1f && v >= 0f && v <= 1f;

        // pin position in panel px (clamped to the frame edge when off-map).
        Vector2 pinPx;
        if (inside) pinPx = new Vector2(u * w, v * h);
        else
        {
            float padU = EDGE_PAD / Mathf.Max(1f, w);
            float padV = EDGE_PAD / Mathf.Max(1f, h);
            pinPx = new Vector2(Mathf.Clamp(u, padU, 1f - padU) * w, Mathf.Clamp(v, padV, 1f - padV) * h);
        }

        if (bigPinGo != null)
        {
            bigPinGo.SetActive(true);
            ((RectTransform)bigPinGo.transform).anchoredPosition = pinPx;
        }

        // distance for the labels.
        float dist = player != null ? FlatDistance(player.position, objPos) : 0f;
        string distText = dist.ToString("F0") + " m";

        if (bigPinLabel != null)
        {
            bigPinLabel.gameObject.SetActive(true);
            bigPinLabel.text = objLabel + "   " + distText;
            // keep the label inside the frame: flip it to the left of the pin if it would overrun the right edge.
            bool right = pinPx.x < w - 300f;
            bigPinLabel.rectTransform.pivot = new Vector2(right ? 0f : 1f, 0.5f);
            bigPinLabel.alignment = right ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
            bigPinLabel.rectTransform.anchoredPosition = pinPx + new Vector2(right ? 16f : -16f, 0f);
        }

        // route line from the player center to the pin.
        if (bigRouteGo != null)
        {
            bigRouteGo.SetActive(true);
            Vector2 center = new Vector2(0.5f * w, 0.5f * h);
            Vector2 d = pinPx - center;
            float len = d.magnitude;
            var rrt = (RectTransform)bigRouteGo.transform;
            rrt.sizeDelta = new Vector2(len, 3f);
            rrt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        }

        if (bigLegendObjective != null) bigLegendObjective.text = objLabel + "   " + distText;
    }

    static string SafeObjectiveLine()
    {
        try { return RescueMission.Objective ?? ""; } catch { return ""; }
    }

    // The player's flat facing (camera forward if available, else the player transform forward).
    Vector3 PlayerFacing()
    {
        Vector3 fwd;
        var cam = LocalPlayer.ActiveCamera;
        if (cam != null) fwd = cam.transform.forward;
        else if (player != null) fwd = player.forward;
        else fwd = Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        return fwd.normalized;
    }

    // ================================================================================================
    // PROJECTION + small UI helpers
    // ================================================================================================

    // World XZ -> RT-UV in [0,1] (0,0 = bottom-left), for a north-up ortho camera (no yaw). See file header.
    static void WorldToUv(Camera cam, Vector3 w, out float u, out float v)
    {
        Vector3 c = cam.transform.position;
        float o = cam.orthographicSize;
        float halfW = o * Mathf.Max(0.0001f, cam.aspect);
        float halfH = o;
        u = 0.5f + (w.x - c.x) / (2f * halfW);
        v = 0.5f + (w.z - c.z) / (2f * halfH);
    }

    // Clamp an off-panel marker to the panel border and rotate it to point outward toward the target.
    static void PlaceEdgeArrow(RectTransform rt, float u, float v, float panelW, float panelH, float pad)
    {
        float padU = pad / Mathf.Max(1f, panelW);
        float padV = pad / Mathf.Max(1f, panelH);
        float cu = Mathf.Clamp(u, padU, 1f - padU);
        float cv = Mathf.Clamp(v, padV, 1f - padV);
        rt.anchoredPosition = new Vector2(cu * panelW, cv * panelH);
        // direction from panel center toward the target (in panel space).
        float dx = u - 0.5f, dy = v - 0.5f;
        float deg = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;   // arrow art points +X at 0 deg
        rt.localRotation = Quaternion.Euler(0f, 0f, deg);
    }

    GameObject NewCanvas(string name, int sortingOrder, bool blocksRaycasts)
    {
        var canvasGo = new GameObject(name);
        Object.DontDestroyOnLoad(canvasGo);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        var grp = canvasGo.AddComponent<CanvasGroup>();
        grp.blocksRaycasts = blocksRaycasts; grp.interactable = blocksRaycasts;
        // The big map needs a GraphicRaycaster so its scrim actually eats gameplay clicks.
        if (blocksRaycasts) canvasGo.AddComponent<GraphicRaycaster>();
        return canvasGo;
    }

    static Image NewImage(Transform parent, string name, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = c; img.raycastTarget = false;
        return img;
    }

    static Text NewText(Transform parent, string name, string content, StorySequencer.Weight weight, int size, Color col)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        var f = StorySequencer.GetFont(weight);
        if (f != null) t.font = f;
        t.text = content;
        t.fontSize = size;
        t.color = col;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    // A filled amber diamond "pin" (a 45-degree-rotated square) - reads as a map marker without any sprite.
    static GameObject MakePin(RectTransform parent, string name, float size, Color col)
    {
        var img = NewImage(parent, name, col);
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);   // panel bottom-left coords
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);    // square -> diamond
        return img.gameObject;
    }

    // A simple triangular arrow approximated by a thin rotated rectangle with a "nose". For a no-sprite build we
    // use a small square rotated to its heading; it reads as a directional tick. Pivot at center; +X = 0 deg.
    static GameObject MakeArrow(RectTransform parent, string name, float size, Color col)
    {
        var root = new GameObject(name, typeof(RectTransform));
        root.transform.SetParent(parent, false);
        var rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = rrt.anchorMax = new Vector2(0f, 0f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(size, size);

        // shaft
        var shaft = NewImage(root.transform, "shaft", col);
        var srt = shaft.rectTransform;
        srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f); srt.pivot = new Vector2(0.5f, 0.5f);
        srt.sizeDelta = new Vector2(size * 0.85f, size * 0.32f);
        srt.anchoredPosition = new Vector2(-size * 0.05f, 0f);

        // head (a diamond at the +X tip reads as an arrowhead)
        var head = NewImage(root.transform, "head", col);
        var hrt = head.rectTransform;
        hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f); hrt.pivot = new Vector2(0.5f, 0.5f);
        hrt.sizeDelta = new Vector2(size * 0.55f, size * 0.55f);
        hrt.anchoredPosition = new Vector2(size * 0.35f, 0f);
        hrt.localRotation = Quaternion.Euler(0f, 0f, 45f);

        return root;
    }

    // Anchor + pivot at the screen's top-right; anchoredPosition is the rect CENTER relative to that corner.
    static void PlaceTopRight(RectTransform rt, Vector2 centerFromTopRight, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = centerFromTopRight;
        rt.sizeDelta = size;
    }

    static void CenterRect(RectTransform rt, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(w, h);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    static void StretchInset(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void OnDestroy()
    {
        if (mapCam != null) Destroy(mapCam.gameObject);
        if (bigCam != null) Destroy(bigCam.gameObject);
        if (rt != null) { rt.Release(); Destroy(rt); }
        if (bigRt != null) { bigRt.Release(); Destroy(bigRt); }
    }
}
