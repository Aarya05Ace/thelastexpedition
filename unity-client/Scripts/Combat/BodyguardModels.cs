// BodyguardModels.cs — bodyguard-enemy FBX resolution + HDRP material fixup for the client-side firefight.
//
// Mirrors SurvivalistModels.cs / NpcModels.cs, but the Bodyguards ship as raw FBX (no prefab) under
// "Assets/BodyGuards/Meshes/SkelMesh_Bodyguard_0{1..4}.fbx" with Built-in-shader materials (which render
// solid WHITE in HDRP). ResolvePrefab(index) editor-loads one of the four FBX (cycled, deterministic);
// FixHdrp(go) re-shades each Built-in/URP material to HDRP/Lit so nothing stays white in the HDRP scene.
//
// EDITOR-ONLY (AssetDatabase loads). In a player build, assign the bodyguard prefabs in the Inspector
// instead. Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Null-safe; never throws.

using UnityEngine;

public static class BodyguardModels
{
#if UNITY_EDITOR
    const string MeshDir = "Assets/BodyGuards/Meshes/";
#endif

    // The four bodyguard source meshes. Indexed 0..3 -> SkelMesh_Bodyguard_01..04 (1-based file names).
    static readonly string[] Meshes =
    {
        "SkelMesh_Bodyguard_01",
        "SkelMesh_Bodyguard_02",
        "SkelMesh_Bodyguard_03",
        "SkelMesh_Bodyguard_04",
    };

    // Resolve a bodyguard model by index (wraps around the four meshes, so any int is safe).
    // Returns the FBX root GameObject to Instantiate, or null if the asset is missing.
    public static GameObject ResolvePrefab(int index)
    {
        int i = ((index % Meshes.Length) + Meshes.Length) % Meshes.Length; // non-negative wrap

        // PRIMARY: Resources copy of the FBX root (works in editor AND player builds).
        var go = Resources.Load<GameObject>($"Models/Bodyguard/{Meshes[i]}");
        if (go != null) return go;

#if UNITY_EDITOR
        // FALLBACK (editor only): load the FBX straight from the source pack via AssetDatabase.
        return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>($"{MeshDir}{Meshes[i]}.fbx");
#else
        return null;
#endif
    }

    // Swap every renderer's Built-in/URP materials to HDRP so nothing renders white. Prefers a by-name
    // HDRP variant if a "Materials HDRP/" folder exists (free auto-upgrade, mirrors SurvivalistModels);
    // otherwise re-shades to HDRP/Lit, migrating _MainTex/_BaseMap -> _BaseColorMap, _Color -> _BaseColor,
    // _BumpMap -> _NormalMap (+ metallic/occlusion when present). Null-safe; never throws.
    public static void FixHdrp(GameObject go)
    {
        if (go == null) return;
#if UNITY_EDITOR
        const string hdrpDir = "Assets/BodyGuards/Materials HDRP/";
#endif
        var hdrpLit = Shader.Find("HDRP/Lit");

        foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
        {
            if (rend == null) continue;
            var mats = rend.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.shader != null && m.shader.name.StartsWith("HDRP")) continue; // already HDRP

                // 1) If a pre-converted HDRP variant exists by name, prefer it (free auto-upgrade).
                //    PRIMARY: Resources copy (works in editor AND builds); editor AssetDatabase as fallback.
                var hdrp = Resources.Load<Material>($"MaterialsHDRP/Bodyguard/HDRP_{m.name}");
#if UNITY_EDITOR
                if (hdrp == null) hdrp = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>($"{hdrpDir}HDRP_{m.name}.mat");
#endif
                if (hdrp != null) { mats[i] = hdrp; changed = true; continue; }

                // 2) Otherwise re-shade to HDRP/Lit, migrating the common PBR maps.
                if (hdrpLit != null)
                {
                    var fix = new Material(hdrpLit) { name = "HDRP_" + m.name };

                    Texture albedo = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex")
                                   : m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                    if (albedo != null) fix.SetTexture("_BaseColorMap", albedo);

                    if (m.HasProperty("_Color")) fix.SetColor("_BaseColor", m.GetColor("_Color"));
                    else if (m.HasProperty("_BaseColor")) fix.SetColor("_BaseColor", m.GetColor("_BaseColor"));

                    if (m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") != null)
                    {
                        fix.SetTexture("_NormalMap", m.GetTexture("_BumpMap"));
                        fix.EnableKeyword("_NORMALMAP");
                    }

                    if (m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null)
                        fix.SetTexture("_MaskMap", m.GetTexture("_MetallicGlossMap"));
                    if (m.HasProperty("_Metallic")) fix.SetFloat("_Metallic", m.GetFloat("_Metallic"));
                    if (m.HasProperty("_Glossiness")) fix.SetFloat("_Smoothness", m.GetFloat("_Glossiness"));

                    if (m.HasProperty("_OcclusionMap") && m.GetTexture("_OcclusionMap") != null)
                        fix.SetTexture("_OcclusionMap", m.GetTexture("_OcclusionMap"));

                    mats[i] = fix; changed = true;
                }
            }
            if (changed) rend.sharedMaterials = mats;
        }
    }
}
