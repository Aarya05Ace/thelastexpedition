// DisableEnvironmentBoundariesSafe.cs — one-shot editor cleanup that opens the map for exploration by
// disabling ONLY artificial invisible boundary/blocker objects + teleport/cutscene triggers, while leaving
// ALL terrain, rocks, cliffs, trees, floors, and water SOLID (so the player never falls through the world or
// sees inside meshes) and ALL HDRP render Volumes + Lights untouched (so the lighting never breaks).
//
// HONEST NOTE: in this project the real "invisible wall" was the player CharacterController slopeLimit (the
// banks read steeper than the old 55deg clamp), now raised to 78 in LocalPlayer. There is no named invisible
// collider, so this will most likely report 0 disabled. It is here as a safe catch for any genuinely-named
// blocker objects. Run via the menu: Tools > Lost Expedition > Open Boundaries Safely.

#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

public static class DisableEnvironmentBoundariesSafe
{
    [MenuItem("Tools/Lost Expedition/Open Boundaries Safely")]
    public static void OpenBoundariesSafely()
    {
        int disabled = 0;
        var all = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);

        foreach (var col in all)
        {
            if (col == null) continue;
            var obj = col.gameObject;
            string n = obj.name.ToLower();

            // EXCLUSION (never touch): the world you stand on / collide with, plus lighting + post volumes.
            if (n.Contains("terrain") || n.Contains("ground") || n.Contains("floor") ||
                n.Contains("rock") || n.Contains("cliff") || n.Contains("stone") || n.Contains("boulder") ||
                n.Contains("tree") || n.Contains("trunk") || n.Contains("bush") || n.Contains("grass") ||
                n.Contains("water") || n.Contains("river") || n.Contains("cabin") ||
                n.Contains("volume") || n.Contains("light") || n.Contains("fog") || n.Contains("sky") ||
                n.Contains("post") || n.Contains("reflection") || n.Contains("probe"))
                continue;
            if (col is TerrainCollider) continue;                 // the heightmap ground
            if (obj.GetComponent<Volume>() != null) continue;      // HDRP/post Volume -> would break lighting
            if (obj.GetComponent<Light>() != null) continue;       // a light source

            // TARGET (disable): explicit artificial blockers + teleport/cutscene triggers ONLY. "volume" is
            // deliberately NOT a target (it almost always means an HDRP render volume in this scene).
            bool isBlocker = n.Contains("boundary") || n.Contains("blocker") || n.Contains("invisible_wall") ||
                             n.Contains("invisiblewall") || n.Contains("restrict") || n.Contains("barrier") ||
                             n.Contains("fence") || n.Contains("cage");
            bool isRestrictTrigger = col.isTrigger &&
                (n.Contains("teleport") || n.Contains("cutscene") || n.Contains("transition"));

            if ((isBlocker || isRestrictTrigger) && obj.activeSelf)
            {
                Undo.RecordObject(obj, "Open Boundaries (disable blocker)");
                obj.SetActive(false);
                disabled++;
                Debug.Log($"[OpenBoundaries] disabled blocker: {obj.name}");
            }
        }

        EditorUtility.DisplayDialog(
            "Open Boundaries Safely",
            disabled > 0
                ? $"Disabled {disabled} artificial boundary object(s). Terrain, rocks, trees, and lighting are untouched."
                : "Found 0 named boundary objects. There is no invisible-collider wall in this scene; the traversal limit was the player slope setting (already raised in LocalPlayer). Nothing was changed.",
            "OK");

        if (disabled > 0)
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }
}
#endif
