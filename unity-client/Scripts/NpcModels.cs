// NpcModels.cs — shared NPC-prefab resolution + HDRP material fixup (civilian "npc_casual_set_00" pack).
//
// Mirrors SurvivalistModels.cs but for the forest NPCs (Mara, Eli, Brother Vael, ...). Lets
// NetworkedWorld.SpawnNpc render a real civilian model instead of a capsule, with the same
// HDRP material fix so nothing renders solid WHITE in the HDRP scene.
//
// EDITOR-ONLY resolution: the npc_casual_set_00 pack ships Built-in-shader materials under
// "Assets/npc_casual_set_00/Materials/" (which render WHITE in HDRP) and a URP set under
// "MaterialsUPR/" (also unusable in HDRP). There is NO HDRP material folder in this pack, so
// FixHdrp re-shades each Built-in material to HDRP/Lit (migrating albedo/color/normal). The code
// ALSO checks for an optional "Materials HDRP/" folder first, so it auto-upgrades for free if that
// set is ever added later (exactly like SurvivalistModels does). In a player build, assign prefabs
// in the Inspector instead.
//
// Determinism: ResolvePrefab maps a name/archetype to a fixed slot in the 12-prefab civilian list,
// so the same NPC always looks the same, and Mara/Eli/Brother Vael each get a distinct model.

using UnityEngine;

public static class NpcModels
{
#if UNITY_EDITOR
    const string PrefabDir = "Assets/npc_casual_set_00/Prefabs/";
#endif

    // The 12 complete civilian character prefabs (humanoid body + clothing). Ordered so adjacent
    // entries differ in gender/variant -> hashing across them keeps NPCs visually distinct.
    static readonly string[] Prefabs =
    {
        "npc_csl_00_character_01f_01", // 0  female v1 style1
        "npc_csl_00_character_01m_02", // 1  male   v1 style2
        "npc_csl_00_character_02f_02", // 2  female v2 style2
        "npc_csl_00_character_02m_01", // 3  male   v2 style1
        "npc_csl_00_character_01f_03", // 4  female v1 style3
        "npc_csl_00_character_01m_01", // 5  male   v1 style1
        "npc_csl_00_character_02f_01", // 6  female v2 style1
        "npc_csl_00_character_02m_03", // 7  male   v2 style3
        "npc_csl_00_character_01f_02", // 8  female v1 style2
        "npc_csl_00_character_01m_03", // 9  male   v1 style3
        "npc_csl_00_character_02f_03", // 10 female v2 style3
        "npc_csl_00_character_02m_02", // 11 male   v2 style2
    };

    // Map an NPC archetype OR display name to a civilian prefab. Deterministic: same input ->
    // same prefab. Named expedition NPCs get hand-picked distinct slots; everyone else is hashed
    // into the list. Null-safe; never throws.
    public static GameObject ResolvePrefab(string archetypeOrName)
    {
        string key = (archetypeOrName ?? "").Trim().ToLowerInvariant();
        int index;

        // Hand-pick the seeded expedition NPCs so they read as distinct individuals.
        if (key.Contains("mara")) index = 0;                              // survivor — female v1
        else if (key.Contains("eli")) index = 1;                          // cultist/guide — male v1 style2
        else if (key.Contains("vael") || key.Contains("brother")) index = 3; // shelter contact — male v2
        else
        {
            // Stable, non-negative hash so unknown names/archetypes still map deterministically
            // (string.GetHashCode is NOT stable across runs, so roll our own FNV-ish hash).
            uint h = 2166136261u;
            foreach (char ch in key) { h ^= ch; h *= 16777619u; }
            index = (int)(h % (uint)Prefabs.Length);
        }

        // PRIMARY: Resources copy (works in editor AND player builds).
        var go = Resources.Load<GameObject>($"Models/Npc/{Prefabs[index]}");
        if (go != null) return go;

#if UNITY_EDITOR
        // FALLBACK (editor only): load straight from the source pack via AssetDatabase.
        return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabDir}{Prefabs[index]}.prefab");
#else
        return null;
#endif
    }

    // Swap every renderer's Built-in/URP materials to HDRP so nothing renders white. Prefers a
    // by-name HDRP variant if a "Materials HDRP/" folder exists (mirrors SurvivalistModels); otherwise
    // re-shades to HDRP/Lit, migrating _MainTex/_BaseMap -> _BaseColorMap, _Color -> _BaseColor,
    // _BumpMap -> _NormalMap (+ metallic/occlusion/emission when present). Null-safe; never throws.
    public static void FixHdrp(GameObject go)
    {
        if (go == null) return;
#if UNITY_EDITOR
        const string hdrpDir = "Assets/npc_casual_set_00/Materials HDRP/";
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
                var hdrp = Resources.Load<Material>($"MaterialsHDRP/Npc/HDRP_{m.name}");
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
