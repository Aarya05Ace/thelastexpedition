// MakeRockMeshesReadable.cs. EDITOR-only AssetPostprocessor that guarantees every rock / cliff / boulder /
// tree model FBX is imported with mesh Read/Write ENABLED (importer.isReadable = true). Without readable mesh
// data, a runtime MeshCollider silently no-ops (the collider exists but has no cooked geometry), so the player
// walks straight through the rock. This is the ROOT-CAUSE fix that lets RockSolidifier add an exact-shape
// MeshCollider per visible rock (so the player collides with the real silhouette and never floats on an
// oversized box).
//
// WHY a postprocessor (not just a one-off menu action): OnPreprocessModel runs automatically on EVERY
// (re)import, so a non-readable FBX is corrected the instant Unity touches it. This is reimport-proof and
// survives library wipes / fresh clones.
//
// WHY ALSO a one-shot reimport (ForceHumanoidReimport pattern): the AssetPostprocessor only corrects an FBX
// when Unity decides to import it. If the .meta on disk already reads isReadable: 1 but the FBX binary in the
// Library was imported when the flag was still 0 (imported on another machine, or the meta was edited after
// the fact), the cooked mesh data is STILL non-readable until a real reimport runs. The one-shot below scans
// the rock/cliff/tree FBX and forces a SaveAndReimport on any whose importer reports isReadable == false, so
// the user never has to hand-reimport. The runtime mesh.isReadable skip in RockSolidifier is the safety net
// for anything this misses.
//
// Lives under an Editor/ folder, so UnityEditor.* APIs are allowed WITHOUT #if UNITY_EDITOR, and none of this
// ships in the player build (build-safety rule). It only changes import settings; it never runs on a runtime
// code path. One class per name. Null-safe. No em dashes, no emoji.

using System;
using UnityEditor;
using UnityEngine;

public class MakeRockMeshesReadable : AssetPostprocessor
{
    // Folder fragments whose model FBX should have readable meshes (rocks, cliffs, boulders, wood, trees).
    // Forward slashes match Unity's assetPath on every platform. Kept narrow so we do not flip Read/Write on
    // non-obstacle scatter for no reason (Ground/ and Phys_Objects/ are deliberately excluded here; the
    // name-hint fallback still catches a rock/tree FBX dropped anywhere).
    static readonly string[] ReadableFolders =
    {
        "Art/Environment/Cliffs/",
        "Art/Environment/Vegetation/Trees/",
        "Art/Environment/_ExternalContent/Quixel/Megascans/Rocks/",
        "Art/Environment/_ExternalContent/Quixel/Megascans/Wood/",
    };

    // Filename fragments (lowercased) that identify a rock / cliff / tree mesh regardless of folder, so a model
    // dropped anywhere still gets a readable mesh. Same token vocabulary RockSolidifier classifies on, so the
    // postprocessor and the runtime solidifier stay in sync.
    static readonly string[] ReadableNameHints =
    {
        "rock", "cliff", "boulder", "stone", "sandstone", "slussen", "cave",
        "pine", "tree", "trunk", "stump", "log",
    };

    // Roots the one-shot scans for FBX (the model root in this project).
    static readonly string[] SearchRoots = { "Assets/Art/Environment" };

    void OnPreprocessModel()
    {
        var mi = assetImporter as ModelImporter;
        if (mi == null) return;

        if (!ShouldMakeReadable(assetPath)) return;

        // Only act when it isn't already readable (idempotent: avoids redundant reimport churn).
        if (mi.isReadable) return;

        mi.isReadable = true;   // flips mesh Read/Write on (importer counterpart of the meta isReadable field)

        Debug.Log($"[MakeRockMeshesReadable] Enabling mesh Read/Write on import: {assetPath}");
    }

    internal static bool ShouldMakeReadable(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var folder in ReadableFolders)
            if (path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        string lower = path.ToLowerInvariant();
        foreach (var hint in ReadableNameHints)
            if (lower.Contains(hint))
                return true;

        return false;
    }

    // Exposed so the one-shot enforcer can share the search roots with the matcher.
    internal static string[] Roots() => SearchRoots;
}

// MakeRockMeshesReadableReimport. EDITOR-only one-shot enforcer that GUARANTEES the rock / cliff / tree FBX
// have actually been (re)imported with readable mesh data, so the user does NOT have to click "Reimport" by
// hand. The AssetPostprocessor above only corrects an FBX when Unity decides to import it; if the .meta on disk
// already reads isReadable: 1 but the FBX binary in the Library was imported when the flag was still 0, the
// cooked mesh is still non-readable until a real reimport runs.
//
// This runs on editor load (and via a menu item): it enumerates every model FBX under the search roots, filters
// through the SAME ShouldMakeReadable matcher the postprocessor uses (so the two stay in sync), and for any
// whose importer reports isReadable == false it forces a SaveAndReimport. Idempotent + null-safe; it only
// reimports files that genuinely still report non-readable, so it does not churn on every load once fixed.
[InitializeOnLoad]
public static class MakeRockMeshesReadableReimport
{
    static MakeRockMeshesReadableReimport()
    {
        // delayCall so the AssetDatabase is fully ready (avoids reimporting mid-refresh).
        EditorApplication.delayCall += EnforceOnce;
    }

    [MenuItem("Tools/Lost Expedition/Make Rock Meshes Readable")]
    public static void EnforceMenu() => Enforce(verbose: true);

    static void EnforceOnce() => Enforce(verbose: false);

    static void Enforce(bool verbose)
    {
        try
        {
            string[] guids = AssetDatabase.FindAssets("t:Model", MakeRockMeshesReadable.Roots());
            if (guids == null)
            {
                if (verbose) Debug.LogWarning("[MakeRockMeshesReadable] No models found under the search roots.");
                return;
            }

            int fixedCount = 0;
            int scanned = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!MakeRockMeshesReadable.ShouldMakeReadable(path)) continue;

                scanned++;

                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    if (verbose) Debug.LogWarning($"[MakeRockMeshesReadable] No ModelImporter at {path} (skipped).");
                    continue;
                }

                if (importer.isReadable)
                {
                    if (verbose) Debug.Log($"[MakeRockMeshesReadable] OK (mesh already readable): {path}");
                    continue;   // already correct, do not churn
                }

                importer.isReadable = true;
                try
                {
                    importer.SaveAndReimport();
                    fixedCount++;
                    Debug.Log($"[MakeRockMeshesReadable] Reimported with mesh Read/Write enabled: {path}");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[MakeRockMeshesReadable] Reimport failed for {path}: {e.Message}");
                }
            }

            if (verbose || fixedCount > 0)
                Debug.Log($"[MakeRockMeshesReadable] Done. Scanned {scanned} rock/cliff/tree FBX; reimported {fixedCount} " +
                          "to enable mesh Read/Write. RockSolidifier can now add exact-shape MeshColliders to them.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[MakeRockMeshesReadable] Enforce aborted: {e.Message}");
        }
    }
}
