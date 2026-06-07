// GameHud.cs — THE LOST EXPEDITION in-game HUD (Fortnite-style: shield+health bars, inventory hotbar,
// blood vignette). Built entirely in code on a ScreenSpaceOverlay canvas via the LobbyUI factory helpers.
//
// SELF-BOOTSTRAP: a single [RuntimeInitializeOnLoadMethod] spawns a tiny watcher host that waits until
// PlayerCombat.Local (the LOCAL player) exists, then instantiates + binds this HUD. The watcher keeps
// running: if PlayerCombat.Local later changes to a *different* instance (scene reload / new body) it
// re-binds. This survives the BotD demo-player teardown described in the project notes because we only
// bind once a real Local exists, and re-bind if it swaps.
//
// BINDING (against the SHARED CONTRACT API):
//   PlayerCombat.Local.Health   -> CurrentHealth/maxHealth (green), CurrentShield/maxShield (blue),
//                                  OnChanged (bars), OnDamaged (vignette).
//   PlayerCombat.Local.Loadout  -> SlotLabels (3 boxes), Selected (highlight), OnChanged (inventory).
//
// Layout (bottom-left): blue shield bar on top, thicker green health bar below, each a long rounded rect
// with a BOLD centered number, fill width scaling to Current/max. Shield bar hides entirely if maxShield==0.
// (bottom-middle): a centered row of 3 inventory boxes; the Selected slot gets an ember outline.
// Blood vignette: a full-screen red edge image (raycast off) whose alpha jumps on OnDamaged and lerps back
// to 0 over ~0.7s, with a higher peak when health is low + a faint persistent throb while critical.
//
// Pure Unity (UnityEngine + UnityEngine.UI + LobbyUI). NO SpacetimeDB types -> no Vector3 alias. NO
// UnityEditor use -> no #if guards. Fully null-safe: never throws if Local is briefly null mid-frame.

using UnityEngine;
using UnityEngine.UI;

public class GameHud : MonoBehaviour
{
    // ---- Fortnite-ish bar colors (warm-dark tracks to stay in the ember/ash palette) ----
    static readonly Color ShieldFill  = new Color(0.30f, 0.78f, 1.00f, 1f);   // shield blue
    static readonly Color ShieldTrack = new Color(0.05f, 0.06f, 0.09f, 0.85f);// dark slate
    static readonly Color HealthFull  = new Color(0.45f, 0.86f, 0.36f, 1f);   // health green
    static readonly Color HealthTrack = new Color(0.06f, 0.05f, 0.04f, 0.85f);// warm-black
    static readonly Color VignetteRed = new Color(0.85f, 0.05f, 0.04f, 1f);   // crimson edge tint

    const float BarsWidth   = 420f;
    const float ShieldH     = 30f;
    const float HealthH     = 38f;
    const float FillInset   = 4f;     // px inset of the fill inside its track on each side
    const float VignettePeak   = 0.6f;   // max blood-overlay alpha right after a hit
    const float VignetteFadeIn = 0.25f;  // seconds to fade IN to peak (fade-OUT tracks the 5s recovery)

    // bound targets
    PlayerCombat combat;
    Health health;
    PlayerLoadout loadout;

    // bar widgets
    RectTransform shieldGroup;        // toggled off when maxShield <= 0
    RectTransform shieldFill;
    Text shieldNum;
    RectTransform healthFill;
    Text healthNum;
    Image healthFillImg;

    // inventory widgets
    Image[] slotBg = new Image[3];
    Image[] slotSelGlow = new Image[3];   // ember outline shown only on the selected slot
    Text[] slotLabel = new Text[3];

    // vignette
    Image vignetteImg;
    float vignetteCurrent;   // displayed alpha; eases toward VignettePeak * Health.RecoveryProgress01

    // crosshair (centered; shown only while the AK is out)
    RectTransform crosshair;

    // =================================================================================================
    // SELF-BOOTSTRAP
    // =================================================================================================

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("CombatHudHost");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<HudBootstrapper>();
    }

    // Tiny watcher: polls PlayerCombat.Local; spawns+binds the HUD on first real Local, re-binds if it
    // changes to a new instance. Pure poll (Update) so it survives any spawn order / demo-player teardown.
    class HudBootstrapper : MonoBehaviour
    {
        PlayerCombat bound;
        GameHud hud;

        void Update()
        {
            var local = SafeLocal();

            if (local == null)
                return; // not spawned yet (or demo-player torn down) — keep waiting, never throw.

            if (hud == null)
            {
                var go = new GameObject("GameHud");
                Object.DontDestroyOnLoad(go);
                hud = go.AddComponent<GameHud>();
                hud.Build();
            }

            if (!ReferenceEquals(local, bound))
            {
                bound = local;
                hud.Bind(local);
            }
        }

        // PlayerCombat.Local is a static getter on the player agent's class; guard in case the property
        // throws or the type isn't fully initialised this frame.
        static PlayerCombat SafeLocal()
        {
            try { return PlayerCombat.Local; }
            catch { return null; }
        }
    }

    // =================================================================================================
    // BUILD — canvas + all widgets (called once, before first Bind)
    // =================================================================================================

    void Build()
    {
        // ---- ScreenSpaceOverlay canvas that never eats clicks (player aims/fires through the HUD) ----
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        gameObject.AddComponent<GraphicRaycaster>();

        var group = gameObject.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        var root = (RectTransform)transform;

        // Child order = layering (first = back): vignette under the bars/inventory.
        BuildVignette(root);
        BuildBars(root);
        BuildInventory(root);
        BuildCrosshair(root);
    }

    // ---- CENTER red crosshair (4 ticks + a dot; hidden unless the AK is out) ----
    void BuildCrosshair(RectTransform root)
    {
        var ch = LobbyUI.Panel(root, "Crosshair", new Color(0f, 0f, 0f, 0f));
        ch.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(ch, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                      Vector2.zero, new Vector2(48f, 48f));
        crosshair = ch;

        Color red = new Color(1f, 0.16f, 0.13f, 0.95f);
        CrossTick(ch, red, new Vector2(3f, 3f), Vector2.zero);          // center dot
        CrossTick(ch, red, new Vector2(2.5f, 10f), new Vector2(0f, 10f));  // top
        CrossTick(ch, red, new Vector2(2.5f, 10f), new Vector2(0f, -10f)); // bottom
        CrossTick(ch, red, new Vector2(10f, 2.5f), new Vector2(-10f, 0f)); // left
        CrossTick(ch, red, new Vector2(10f, 2.5f), new Vector2(10f, 0f));  // right
        // Always visible while alive (Update hides it on death). It used to only show with the AK out, which
        // is why it never appeared — you'd die before drawing the gun.
    }

    void CrossTick(RectTransform parent, Color c, Vector2 size, Vector2 pos)
    {
        var t = LobbyUI.Panel(parent, "tick", c);
        var img = t.GetComponent<Image>();
        if (img != null) img.raycastTarget = false;
        LobbyUI.Place(t, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
    }

    // ---- BLOOD VIGNETTE (back layer) ----
    void BuildVignette(RectTransform root)
    {
        // Full-screen blood overlay using the imported PNG (Assets/TombRush/UI/BloodVignette.png). The sprite
        // is already red, so we tint white and drive ALPHA from the recovery. Falls back to LobbyUI's code-gen
        // vignette (tinted red) if the sprite can't be loaded (e.g. a player build).
        var go = new GameObject("BloodVignette", typeof(RectTransform));
        go.transform.SetParent(root, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

        vignetteImg = go.AddComponent<Image>();
        vignetteImg.raycastTarget = false;
        vignetteImg.type = Image.Type.Simple;

        var sprite = LoadBloodSprite();
        if (sprite != null)
        {
            vignetteImg.sprite = sprite;
            vignetteImg.color = new Color(1f, 1f, 1f, 0f);   // white tint shows the red sprite true; alpha = pulse
        }
        else
        {
            Object.Destroy(go);                              // no sprite -> use the code-gen fallback instead
            vignetteImg = LobbyUI.Vignette(root);
            vignetteImg.color = new Color(VignetteRed.r, VignetteRed.g, VignetteRed.b, 0f);
            vignetteImg.raycastTarget = false;
        }
        vignetteImg.rectTransform.SetAsFirstSibling();
    }

    static Sprite LoadBloodSprite()
    {
#if UNITY_EDITOR
        const string path = "Assets/TombRush/UI/BloodVignette.png";
        var sp = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sp != null) return sp;
        var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex != null)
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
#endif
        return null;
    }

    // ---- BOTTOM-LEFT shield + health bars ----
    void BuildBars(RectTransform root)
    {
        var container = LobbyUI.Panel(root, "Bars", new Color(0, 0, 0, 0)); // transparent layout anchor
        container.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(container, Vector2.zero, Vector2.zero, Vector2.zero,
                      new Vector2(48f, 110f), new Vector2(BarsWidth, ShieldH + HealthH + 8f)); // raised so it isn't cut off

        // SHIELD (top) — wrapped in a group so we can hide it whole when maxShield == 0.
        shieldGroup = LobbyUI.Panel(container, "ShieldGroup", new Color(0, 0, 0, 0));
        shieldGroup.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(shieldGroup, Vector2.zero, Vector2.zero, new Vector2(0f, 1f),
                      new Vector2(0f, 0f), new Vector2(BarsWidth, ShieldH));
        BuildOneBar(shieldGroup, ShieldTrack, ShieldFill, ShieldH, 18,
                    out shieldFill, out _, out shieldNum);

        // HEALTH (below, thicker).
        var healthTrack = BuildOneBar(container, HealthTrack, HealthFull, HealthH, 22,
                                      out healthFill, out healthFillImg, out healthNum);
        LobbyUI.Place(healthTrack, Vector2.zero, Vector2.zero, new Vector2(0f, 1f),
                      new Vector2(0f, -(ShieldH + 6f)), new Vector2(BarsWidth, HealthH));
    }

    // Builds one track (rounded) with a left-anchored fill child and a centered bold number child.
    // Returns the track rect; out-params expose the fill rect (width-scaled), the fill Image (tint), the number.
    RectTransform BuildOneBar(RectTransform parent, Color trackColor, Color fillColor, float h, int numSize,
                              out RectTransform fillRt, out Image fillImg, out Text num)
    {
        var track = LobbyUI.RoundedPanel(parent, "Track", trackColor, 8);
        track.raycastTarget = false;
        var trackRt = track.rectTransform;
        // Default placement (top-left of parent); BuildBars overrides health's anchoredPos afterwards.
        LobbyUI.Place(trackRt, Vector2.zero, Vector2.zero, new Vector2(0f, 1f),
                      new Vector2(0f, 0f), new Vector2(BarsWidth, h));
        LobbyUI.Border(trackRt, LobbyUI.EmberDim, 1f);

        var fill = LobbyUI.RoundedPanel(trackRt, "Fill", fillColor, 8);
        fill.raycastTarget = false;
        fillImg = fill;
        fillRt = fill.rectTransform;
        // Left-anchored, vertically stretched with inset; width is driven each refresh via sizeDelta.x.
        fillRt.anchorMin = new Vector2(0f, 0f);
        fillRt.anchorMax = new Vector2(0f, 1f);
        fillRt.pivot = new Vector2(0f, 0.5f);
        fillRt.anchoredPosition = new Vector2(FillInset, 0f);
        fillRt.offsetMin = new Vector2(FillInset, FillInset);
        fillRt.offsetMax = new Vector2(FillInset, -FillInset);
        fillRt.sizeDelta = new Vector2(BarsWidth - FillInset * 2f, 0f); // full until refreshed; y from anchors

        num = LobbyUI.ShadowLabel(trackRt, "", numSize, Color.white, TextAnchor.MiddleCenter);
        var nrt = num.rectTransform;
        nrt.anchorMin = Vector2.zero; nrt.anchorMax = Vector2.one;
        nrt.offsetMin = Vector2.zero; nrt.offsetMax = Vector2.zero;

        return trackRt;
    }

    // ---- BOTTOM-MIDDLE inventory hotbar ----
    void BuildInventory(RectTransform root)
    {
        const float box = 72f, gap = 12f;
        float rowW = box * 3f + gap * 2f;

        var row = LobbyUI.Panel(root, "Inventory", new Color(0, 0, 0, 0));
        row.GetComponent<Image>().raycastTarget = false;
        LobbyUI.Place(row, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                      new Vector2(0f, 90f), new Vector2(rowW, box)); // raised off the bottom edge

        for (int i = 0; i < 3; i++)
        {
            var bg = LobbyUI.RoundedPanel(row, "Slot" + i, LobbyUI.BgPanel, 10);
            bg.raycastTarget = false;
            var brt = bg.rectTransform;
            LobbyUI.Place(brt, Vector2.zero, Vector2.zero, new Vector2(0f, 0.5f),
                          new Vector2(i * (box + gap), 0f), new Vector2(box, box));
            slotBg[i] = bg;

            // Unselected hairline frame (always visible).
            LobbyUI.Border(brt, LobbyUI.EmberDim, 1f);

            // Selected ember outline (toggled per-refresh) — a brighter, thicker frame.
            var glow = LobbyUI.RoundedPanel(brt, "SelGlow", new Color(LobbyUI.Ember.r, LobbyUI.Ember.g, LobbyUI.Ember.b, 0f), 10);
            glow.raycastTarget = false;
            var grt = glow.rectTransform;
            grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one;
            grt.offsetMin = Vector2.zero; grt.offsetMax = Vector2.zero;
            LobbyUI.Border(grt, LobbyUI.Ember, 2.5f);
            slotSelGlow[i] = glow;

            // Slot number chip (top-left).
            var chip = LobbyUI.ShadowLabel(brt, (i + 1).ToString(), 14, LobbyUI.AshDim, TextAnchor.UpperLeft);
            var crt = chip.rectTransform;
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(0f, 1f);
            crt.pivot = new Vector2(0f, 1f);
            crt.anchoredPosition = new Vector2(6f, -4f);
            crt.sizeDelta = new Vector2(24f, 18f);

            // Content label (slot name, centered).
            var lbl = LobbyUI.ShadowLabel(brt, "", 15, LobbyUI.AshText, TextAnchor.MiddleCenter);
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(2f, 2f); lrt.offsetMax = new Vector2(-2f, -2f);
            slotLabel[i] = lbl;
        }
    }

    // =================================================================================================
    // BIND — wire to a (possibly new) local PlayerCombat
    // =================================================================================================

    void Bind(PlayerCombat local)
    {
        Unbind();

        combat = local;
        if (combat == null) return;

        // Read through the CONTRACT API; tolerate nulls (player agent edits may land slightly out of sync).
        health = SafeHealth(combat);
        loadout = SafeLoadout(combat);

        if (health != null)
        {
            health.OnChanged += RefreshBars;
            RefreshBars(health);
        }
        if (loadout != null)
        {
            loadout.OnChanged += RefreshInventory;
            RefreshInventory();
        }
    }

    void Unbind()
    {
        if (health != null)
        {
            health.OnChanged -= RefreshBars;
        }
        if (loadout != null) loadout.OnChanged -= RefreshInventory;
        health = null;
        loadout = null;
        combat = null;
    }

    static Health SafeHealth(PlayerCombat c)
    {
        try { return c.Health; } catch { return null; }
    }

    static PlayerLoadout SafeLoadout(PlayerCombat c)
    {
        try { return c.Loadout; } catch { return null; }
    }

    void OnDestroy() => Unbind();

    // =================================================================================================
    // REFRESH — event-driven (cheap); Update only runs the vignette fade + per-frame fallback
    // =================================================================================================

    void RefreshBars(Health h)
    {
        if (h == null) return;

        // SHIELD — hide the whole group when there's no shield pool (AI = 0; player = 50).
        bool hasShield = h.maxShield > 0f;
        if (shieldGroup != null && shieldGroup.gameObject.activeSelf != hasShield)
            shieldGroup.gameObject.SetActive(hasShield); // only toggle on change (per-frame fallback stays cheap)
        if (hasShield)
        {
            float sRatio = Mathf.Clamp01(h.CurrentShield / Mathf.Max(0.0001f, h.maxShield));
            SetFillWidth(shieldFill, sRatio);
            if (shieldNum != null) shieldNum.text = Mathf.CeilToInt(h.CurrentShield).ToString();
        }

        // HEALTH — width + bold number + low-health crimson tint.
        float hRatio = Mathf.Clamp01(h.CurrentHealth / Mathf.Max(0.0001f, h.maxHealth));
        SetFillWidth(healthFill, hRatio);
        if (healthNum != null) healthNum.text = Mathf.CeilToInt(h.CurrentHealth).ToString();
        if (healthFillImg != null)
        {
            // Below ~30% lerp the green toward crimson for urgency (ties into the stronger vignette).
            float danger = Mathf.InverseLerp(0.30f, 0.05f, hRatio); // 0 above 30% -> 1 near death
            healthFillImg.color = Color.Lerp(HealthFull, LobbyUI.Crimson, danger);
        }
    }

    // Drive a left-anchored fill's width from a 0..1 ratio (track width minus the inset on both sides).
    void SetFillWidth(RectTransform fill, float ratio)
    {
        if (fill == null) return;
        float trackW = (fill.parent as RectTransform) != null ? ((RectTransform)fill.parent).rect.width : BarsWidth;
        if (trackW <= 0f) trackW = BarsWidth;
        float w = Mathf.Max(0f, (trackW - FillInset * 2f) * Mathf.Clamp01(ratio));
        fill.sizeDelta = new Vector2(w, fill.sizeDelta.y);
    }

    void RefreshInventory()
    {
        string[] labels = SafeSlotLabels(loadout);
        int selected = SafeSelected(loadout); // 0 = holstered (no highlight), 1/2/3 = active slot

        for (int i = 0; i < 3; i++)
        {
            string name = (labels != null && i < labels.Length && labels[i] != null) ? labels[i] : "";
            if (slotLabel[i] != null)
            {
                slotLabel[i].text = name;
                slotLabel[i].color = string.IsNullOrEmpty(name) ? LobbyUI.AshDim : LobbyUI.AshText;
            }
            // Highlight: slot index i corresponds to Selected == i+1.
            bool sel = selected == (i + 1);
            if (slotSelGlow[i] != null) slotSelGlow[i].gameObject.SetActive(sel);
        }
    }

    static string[] SafeSlotLabels(PlayerLoadout l)
    {
        try { return l != null ? l.SlotLabels : null; } catch { return null; }
    }

    static int SafeSelected(PlayerLoadout l)
    {
        try { return l != null ? l.Selected : 0; } catch { return 0; }
    }

    // =================================================================================================
    // VIGNETTE — flash on damage, fade in Update; cheap per-frame bar fallback for safety
    // =================================================================================================

    void Update()
    {
        // BLOOD VIGNETTE: target = peak right after a hit (RecoveryProgress01 = 1), decaying to 0 across the
        // 5s recovery. MoveTowards is fast (1/VignetteFadeIn) so it FADES IN ~0.25s, then tracks the slow
        // RecoveryProgress01 decay on the way down (FADE OUT over 5s) — perfectly synced with the +5 heal.
        float target = (health != null && !health.IsDead) ? VignettePeak * health.RecoveryProgress01 : 0f;
        vignetteCurrent = Mathf.MoveTowards(vignetteCurrent, target, Time.deltaTime / Mathf.Max(0.01f, VignetteFadeIn));
        if (vignetteImg != null)
        {
            Color baseCol = vignetteImg.sprite != null ? Color.white : VignetteRed; // sprite already red; else tint
            vignetteImg.color = new Color(baseCol.r, baseCol.g, baseCol.b, vignetteCurrent);
        }

        // Cheap per-frame fallback: if a refresh was somehow missed (e.g. a frame where the track width
        // was still 0 at bind time, before layout ran), keep the bars correct. Event-driven is the norm.
        if (health != null) RefreshBars(health);

        // Crosshair: visible whenever the player is alive (the death screen covers it otherwise).
        if (crosshair != null)
        {
            bool alive = health == null || !health.IsDead;
            if (crosshair.gameObject.activeSelf != alive) crosshair.gameObject.SetActive(alive);
        }
    }
}
