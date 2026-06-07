// PerfConfig.cs — runtime performance pass for smoother FPS on the heavy photoreal HDRP forest.
//
// Applies the SAFE, engine-level levers that reliably help in HDRP (LOD bias, vsync uncap, camera far
// distance). The single biggest win, though, is running a STANDALONE BUILD — the Unity editor overhead is
// most of the lag (see Editor/BuildScript.cs, menu "Lost Expedition > Build Mac Standalone").
//
// The HDRP asset (Assets/Shaders/HDRenderPipelineAsset-BotD.asset) + scene Volume profiles have ALSO been
// cut hard offline: SSR/SSGI/SSAO/SSS/volumetric fog/volumetric clouds/contact shadows/distortion off,
// shadow atlas 1024 (16-bit) + max shadow res 512 + screen-space shadows off + cascades 1 + max shadow
// distance ~40, dynamic-res forced to 50% (~0.5 render scale) — that render scale is the FPS lever.
//
// STRATEGY: KEEP ALL THE GEOMETRY (grass, trees, mountains, rivers). The forest is PIXEL-bound, not
// geometry-bound (the editor is smooth WITH the full lush forest), so FPS comes from LOW RESOLUTION + the
// HDRP 50% render scale — NOT from deleting scenery. This file holds the remaining RUNTIME levers
// (resolution, LOD bias, vsync uncap, far clip). Vegetation thinning is left OFF.

using System;
using System.Reflection;
using UnityEngine;

public static class PerfConfig
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Apply()
    {
        // BIGGEST WIN + THE FPS LEVER: render the standalone at 720p. The photoreal forest is PIXEL-bound (the
        // editor is smooth WITH the full lush geometry — native-Retina fullscreen is what crushed the old build),
        // so we buy FPS with low resolution, NOT by deleting grass/trees/landscape. HD window so the forest still
        // looks like the winning factor; the HDRP 50% render scale stacks on top. Ignored in the editor Game view;
        // applies to the player. (If 720p+50% is too soft, drop to 960x540 — a later dial; correctness first.)
        if (!Application.isEditor)
            Screen.SetResolution(1280, 720, FullScreenMode.FullScreenWindow);

        // LODs swap to cheaper meshes much sooner -> far fewer triangles drawn (applies in HDRP).
        QualitySettings.lodBias = 0.4f;
        // Shadows are a top cost in dense forest — pull them in hard (harmless if a Volume overrides).
        QualitySettings.shadowDistance = 35f;
        QualitySettings.shadowCascades = 1;
        // Don't cap FPS to the monitor refresh; let the GPU run free.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        // Push the far clip OUT on the gameplay camera once it spawns so the full distant landscape renders
        // (there's no fog to hide a near cut — m_Fog:0 + HDRP volumetrics off — a short far plane floats rocks).
        var host = new GameObject("PerfConfigRuntime");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<PerfRuntime>();
    }

    class PerfRuntime : MonoBehaviour
    {
        // GEOMETRY IS KEPT — the forest is PIXEL-bound (the editor is smooth WITH the full lush forest), so
        // FPS comes from LOW RESOLUTION + the HDRP 50% render scale, NOT from deleting scenery. When true,
        // this would strip grass/foliage; we leave it OFF so the grass + trees stay (lush forest restored).
        const bool ThinVegetation = false;

        bool done;
        bool vegThinned;
        bool beamKilled;
        float t;

        void Update()
        {
            if (done) return;
            t += Time.deltaTime;

            if (ThinVegetation && !vegThinned) ThinVegetationRoots();
            if (!beamKilled) KillLightBeams();

            var cam = LocalPlayer.ActiveCamera != null ? LocalPlayer.ActiveCamera : Camera.main;
            if (cam != null)
            {
                // Keep the FULL distant landscape (mountains/rivers/terrain) — a 160m cut made rocks float
                // and sheared off the background. Push the far plane out to ~1000m. Set directly (NOT Mathf.Min,
                // which only ever LOWERS it). Distant cost is cheap thanks to the low render scale + LOD bias.
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 1000f);
                done = true;
            }
            else if (t > 10f) done = true;   // give up if no camera appears
        }

        // ONE-SHOT beam killer — removes the hard WHITE LIGHT-BEAM streak across the sky WITHOUT darkening
        // the scene. The streak is an SRP Lens Flare (Assets/Art/FX/forest_flare.asset — a 0.15x5.28 warm-white
        // sliver, the diagonal "beam") on a "Directional Light" object, plus the active sun's HDRP angular-flare
        // glow (flareSize/flareMultiplier) now bare because the haze/volumetrics were cut. We DISABLE the flares
        // (the glow) and NEVER touch light intensity — the directional sun keeps lighting the scene exactly as before.
        // All HDRP access is via reflection (component looked up by type NAME) so this needs no HDRP assembly
        // reference and stays build-safe; everything is null-guarded, runs once, and is reversible (just re-enable).
        void KillLightBeams()
        {
            beamKilled = true;  // run once
            int flares = 0, suns = 0;

            // 1) Kill EVERY lens flare in the scene — SRP (LensFlareComponentSRP) and legacy (LensFlare),
            //    whether on an active or (re)activated light, and regardless of GameObject name. Disabling the
            //    COMPONENT (not the GameObject/light) removes the streak while leaving the light's lighting intact.
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                string tn = mb.GetType().Name;
                if (tn == "LensFlareComponentSRP" && mb.enabled) { mb.enabled = false; flares++; }
            }
            foreach (var lf in Object.FindObjectsByType<LensFlare>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lf != null && lf.enabled) { lf.enabled = false; flares++; }
            }

            // 2) Clamp the HDRP angular SUN flare (the soft glow the physical sun renders) to nothing — set
            //    the public-float flare params (flareSize / flareMultiplier / flareFalloff) to 0 via reflection
            //    on HDAdditionalLightData. This kills only the glow; m_Intensity is untouched so the sun still
            //    lights the scene fully. Also clear legacy Light.flare so no light re-introduces a flare at runtime.
            foreach (var lt in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (lt == null) continue;
                if (lt.flare != null) { lt.flare = null; flares++; }

                var hd = lt.GetComponent("HDAdditionalLightData");
                if (hd == null) continue;
                var ty = hd.GetType();
                bool touched = false;
                touched |= TrySetFloat(hd, ty, "flareSize", 0f);
                touched |= TrySetFloat(hd, ty, "flareMultiplier", 0f);
                touched |= TrySetFloat(hd, ty, "flareFalloff", 0f);
                if (touched) suns++;
            }

            Debug.Log($"[PerfConfig] KillLightBeams: disabled {flares} lens flare(s), clamped {suns} HDRP sun-flare glow(s) (scene lighting unchanged)");
        }

        // Reflection helper: set a float field OR property by name if it exists. Returns true if it set something.
        static bool TrySetFloat(object target, Type ty, string name, float value)
        {
            try
            {
                var f = ty.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(float)) { f.SetValue(target, value); return true; }
                var p = ty.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite && p.PropertyType == typeof(float)) { p.SetValue(target, value); return true; }
            }
            catch { /* HDRP version/name drift — null-safe no-op */ }
            return false;
        }

        // ONE-SHOT geometry strip — the real heavy cost in Book-of-the-Dead is the high-poly MESH trees
        // (a "Trees" group + ~28 "UberTreeSpawner" baked-tree roots) plus the grass/foliage. The old version
        // only killed "Vegetation"/"Meadow_Grass" and never touched the trees, so it did almost nothing.
        // Iterate every Transform ONCE and disable anything matching the vegetation/tree names. Null-safe.
        void ThinVegetationRoots()
        {
            vegThinned = true;  // run once
            int killed = 0;
            foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (tr == null) continue;
                string n = tr.name;
                // KEEP THE TREES (the forest's identity) — only strip GRASS / small ground foliage, which is the
                // worst overdraw and barely registers next to the trees. The low render resolution carries the rest.
                bool grassOnly =
                    n == "Vegetation" || n == "Meadow_Grass" ||
                    n.Contains("Grass") || n.Contains("Meadow") ||
                    n.Contains("Fern")  || n.Contains("Plant")  || n.Contains("Foliage");
                if (grassOnly && tr.gameObject.activeSelf) { tr.gameObject.SetActive(false); killed++; }
            }
            var terrain = Terrain.activeTerrain;
            if (terrain != null)
            {
                terrain.detailObjectDistance = 0f;     // no terrain detail grass (overdraw)
                terrain.treeDistance = 300f;           // KEEP terrain trees, just cull the very-distant ones
                terrain.treeBillboardDistance = 60f;
            }
            Debug.Log($"[PerfConfig] ThinVegetation: kept trees, stripped {killed} grass/foliage objects");
        }
    }
}
