// SurvivalistModels.cs — shared survivalist-prefab resolution + HDRP material fixup.
//
// Used by BOTH the lobby carousel preview and the gameplay player spawner so a player's chosen
// character renders as the real survivalist model (not a capsule) in both places.
//
// BUILD-SAFE resolution: the prefabs are copied under "Assets/TombRush/Resources/Models/Survivalist_<n>"
// and loaded via Resources.Load (works in BOTH editor + player builds), with the editor-only
// AssetDatabase path kept ONLY as a secondary fallback. The Survivalist asset pack ships Built-in-shader
// materials (which render solid WHITE in HDRP) plus an HDRP-converted set under
// "Assets/Survivalist/Materials HDRP/HDRP_<name>". We swap each prefab's materials to that HDRP set, with
// an HDRP/Lit re-shade fallback so a model can NEVER render white.

using UnityEngine;

public static class SurvivalistModels
{
    // medic -> 1, scout/guide -> 2, brute -> 3, tinkerer -> 4 (case-insensitive; matches class or archetype).
    public static GameObject ResolvePrefab(string characterClass)
    {
        string c = (characterClass ?? "").ToLowerInvariant();
        string n = (c.Contains("scout") || c.Contains("guide")) ? "2"
                 : c.Contains("brute") ? "3"
                 : c.Contains("tinker") ? "4"
                 : "1"; // medic / default

        // PRIMARY: Resources copy (works in editor AND player builds).
        var go = Resources.Load<GameObject>($"Models/Survivalist_{n}");
        if (go != null) return go;

#if UNITY_EDITOR
        // FALLBACK (editor only): load straight from the source pack via AssetDatabase.
        return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/Survivalist/Prefab/Survivalist ({n}).prefab");
#else
        return null;
#endif
    }

    // Swap every renderer's Built-in materials to the pack's HDRP equivalents; re-shade any unmatched
    // material to HDRP/Lit (migrating the albedo) so nothing stays white in the HDRP scene.
    public static void FixHdrp(GameObject go)
    {
#if UNITY_EDITOR
        const string dir = "Assets/Survivalist/Materials HDRP/";
#endif
        var hdrpLit = Shader.Find("HDRP/Lit");
        foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
        {
            var mats = rend.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.shader != null && m.shader.name.StartsWith("HDRP")) continue; // already HDRP

                // PRIMARY: pre-converted HDRP material from Resources (works in editor AND builds).
                var hdrp = Resources.Load<Material>($"MaterialsHDRP/Survivalist/HDRP_{m.name}");
#if UNITY_EDITOR
                // FALLBACK (editor only): load the HDRP material straight from the source pack folder.
                if (hdrp == null) hdrp = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>($"{dir}HDRP_{m.name}.mat");
#endif
                if (hdrp != null) { mats[i] = hdrp; changed = true; continue; }

                if (hdrpLit != null)
                {
                    var fix = new Material(hdrpLit) { name = "HDRP_" + m.name };
                    Texture albedo = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex")
                                   : m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                    if (albedo != null) fix.SetTexture("_BaseColorMap", albedo);
                    if (m.HasProperty("_Color")) fix.SetColor("_BaseColor", m.GetColor("_Color"));
                    if (m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") != null)
                        fix.SetTexture("_NormalMap", m.GetTexture("_BumpMap"));
                    mats[i] = fix; changed = true;
                }
            }
            if (changed) rend.sharedMaterials = mats;
        }
    }
}
