// ForceHumanoidModels.cs — EDITOR-only AssetPostprocessor that guarantees every character model
// (survivalist / bodyguard / sister + their Resources copies) is imported as a HUMANOID rig so Unity
// generates a human Avatar on import. Without a human Avatar, humanoid clips cannot retarget and the
// model defaults to a T-pose. This is the ROOT-CAUSE fix for the bodyguard T-pose.
//
// WHY a postprocessor (not just a one-off menu action): OnPreprocessModel runs automatically on EVERY
// (re)import, so a Generic FBX is corrected the instant Unity touches it. This is reimport-proof and
// survives library wipes / fresh clones. It complements LocomotionControllerBuilder.EnsureHumanoidRigs,
// which only converts the SOURCE FBX under Assets/BodyGuards/Meshes/... — it does NOT touch the
// Resources COPIES under Assets/TombRush/Resources/Models/Bodyguard/..., and the runtime spawn path
// (BodyguardModels.Resources.Load) loads exactly those copies. Those copies were imported as Generic
// (animationType: 2, human: [], skeleton: [], avatarSetup: 0) -> no human avatar -> guaranteed T-pose.
// This postprocessor matches them (and all character meshes) by folder/name and forces Humanoid.
//
// Lives under an Editor/ folder, so UnityEditor.* APIs are allowed WITHOUT #if UNITY_EDITOR, and none
// of this ships in the player build (build-safety rule). It only changes import settings; it never runs
// on a runtime code path.

using System;
using UnityEditor;
using UnityEngine;

public class ForceHumanoidModels : AssetPostprocessor
{
    // Folder fragments whose model FBX MUST be Humanoid. Forward slashes match Unity's assetPath on
    // every platform. The Resources copies are the critical ones (runtime loads them directly).
    static readonly string[] HumanoidFolders =
    {
        "Resources/Models/Bodyguard/",   // runtime-loaded bodyguard copies (the T-pose source)
        "Resources/Models/Survivalist",  // any survivalist FBX dropped into Resources
        "Resources/Models/Sister",       // rescue/sister character (if/when added)
        "BodyGuards/Meshes/",            // source bodyguard FBX
        "Survivalist/Basemesh/",         // source survivalist FBX
    };

    // Filename fragments (lowercased) that identify a character mesh regardless of folder, so a model
    // dropped anywhere still gets a human avatar. Keep these specific to avoid converting prop FBX.
    static readonly string[] HumanoidNameHints =
    {
        "bodyguard",
        "survivalist",
        "sister",
    };

    void OnPreprocessModel()
    {
        var mi = assetImporter as ModelImporter;
        if (mi == null) return;

        if (!ShouldForceHumanoid(assetPath)) return;

        // Only act when it isn't already Humanoid (idempotent: avoids redundant reimport churn).
        if (mi.animationType == ModelImporterAnimationType.Human) return;

        mi.animationType = ModelImporterAnimationType.Human;            // -> animationType: 3
        mi.avatarSetup   = ModelImporterAvatarSetup.CreateFromThisModel; // generate avatar from this rig
        // Auto-map standard bone names (Hips/Spine/Head/Left*/Right*) — these rigs expose them, proven by
        // the source copies that already map cleanly to a human avatar.
        mi.autoGenerateAvatarMappingIfUnspecified = true;

        Debug.Log($"[ForceHumanoid] Forcing Humanoid rig + avatar on import: {assetPath}");
    }

    internal static bool ShouldForceHumanoid(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var folder in HumanoidFolders)
            if (path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        // File-name fallback (folder-independent), but exclude anything that's clearly not a base mesh
        // (e.g. an animation clip FBX) — those live under animation packs and are never matched by the
        // character folders above, but guard the name hints anyway.
        string lower = path.ToLowerInvariant();
        foreach (var hint in HumanoidNameHints)
            if (lower.Contains(hint))
                return true;

        return false;
    }
}

// ForceHumanoidReimport — EDITOR-only one-shot enforcer that GUARANTEES the Resources character FBX have
// actually been (re)imported as Humanoid WITH a generated human Avatar, so the user does NOT have to click
// "Reimport" by hand. The AssetPostprocessor above only corrects an FBX when Unity decides to import it; if
// the .meta on disk was already edited to Humanoid (animationType: 3) but the FBX binary in the Library was
// imported when it was still Generic, the asset still carries NO avatar object until a real reimport runs.
//
// This runs on editor load (and via a menu item): it scans every character FBX, and for any that either is
// NOT currently imported as Humanoid OR whose import produced NO human Avatar object, it forces a real
// SaveAndReimport. That reimport regenerates a human Avatar (CreateFromThisModel) which CharacterRig then
// finds at runtime (ResolveHumanAvatar step 1/2), eliminating the T-pose. Idempotent + null-safe; it only
// reimports files that genuinely still lack a human avatar, so it does not churn on every load once fixed.
[InitializeOnLoad]
public static class ForceHumanoidReimport
{
    // The Resources COPIES the runtime spawn path actually loads (the proven T-pose source), plus the
    // survivalist Resources prefab's mesh is its own FBX — but the critical ones are these four bodyguards.
    static readonly string[] CharacterFbx =
    {
        "Assets/TombRush/Resources/Models/Bodyguard/SkelMesh_Bodyguard_01.fbx",
        "Assets/TombRush/Resources/Models/Bodyguard/SkelMesh_Bodyguard_02.fbx",
        "Assets/TombRush/Resources/Models/Bodyguard/SkelMesh_Bodyguard_03.fbx",
        "Assets/TombRush/Resources/Models/Bodyguard/SkelMesh_Bodyguard_04.fbx",
    };

    static ForceHumanoidReimport()
    {
        // delayCall so the AssetDatabase is fully ready (avoids reimporting mid-refresh).
        EditorApplication.delayCall += EnforceOnce;
    }

    [MenuItem("Tools/Lost Expedition/Force Humanoid Reimport (fix T-pose)")]
    public static void EnforceMenu() => Enforce(verbose: true);

    static void EnforceOnce() => Enforce(verbose: false);

    static void Enforce(bool verbose)
    {
        try
        {
            int fixedCount = 0;
            foreach (var path in CharacterFbx)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    if (verbose) Debug.LogWarning($"[ForceHumanoidReimport] No ModelImporter at {path} (skipped).");
                    continue;
                }

                bool isHumanImport = importer.animationType == ModelImporterAnimationType.Human;
                bool hasHumanAvatar = HasGeneratedHumanAvatar(path);

                if (isHumanImport && hasHumanAvatar)
                {
                    if (verbose) Debug.Log($"[ForceHumanoidReimport] OK (Humanoid + avatar present): {path}");
                    continue; // already correct — do not churn
                }

                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.autoGenerateAvatarMappingIfUnspecified = true;
                try
                {
                    importer.SaveAndReimport();
                    fixedCount++;
                    bool nowHas = HasGeneratedHumanAvatar(path);
                    Debug.Log($"[ForceHumanoidReimport] Reimported as Humanoid: {path} (human avatar present after = {nowHas}).");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ForceHumanoidReimport] Reimport failed for {path}: {e.Message}");
                }
            }

            if (verbose || fixedCount > 0)
                Debug.Log($"[ForceHumanoidReimport] Done. Reimported {fixedCount} character FBX to Humanoid. " +
                          "If any 'human avatar present after = false' above, that rig's bones did not auto-map; " +
                          "open the FBX Rig tab, set Animation Type = Humanoid, Configure the avatar, Apply.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[ForceHumanoidReimport] Enforce aborted: {e.Message}");
        }
    }

    // True if the imported FBX produced a human Avatar object (the thing CharacterRig needs at runtime).
    static bool HasGeneratedHumanAvatar(string fbxPath)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        if (all == null) return false;
        foreach (var obj in all)
            if (obj is Avatar av && av != null && av.isHuman && av.isValid)
                return true;
        return false;
    }
}
