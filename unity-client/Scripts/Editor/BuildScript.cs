// BuildScript.cs — one-click standalone build (menu: "Lost Expedition > Build Mac Standalone").
//
// Running the built .app instead of the editor is the biggest single FPS win — the editor's overhead (Scene
// view, domain reloads, profiler hooks, the inspector) is most of the "laggy" feel. This builds the scenes
// in Build Settings (or the currently-open scene if none are added) to Builds/LostExpedition.app.
//
// Editor-only (lives in an Editor/ folder so UnityEditor.* is allowed and it ships in no player build).

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BuildScript
{
    const string OutPath = "Builds/LostExpedition.app";

    [MenuItem("Lost Expedition/Build Mac Standalone")]
    public static void BuildMac()
    {
        var scenes = new List<string>();
        foreach (var s in EditorBuildSettings.scenes)
            if (s.enabled && !string.IsNullOrEmpty(s.path)) scenes.Add(s.path);

        // Fall back to the currently-open scene if Build Settings has none.
        if (scenes.Count == 0)
        {
            var active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path)) scenes.Add(active.path);
        }
        if (scenes.Count == 0)
        {
            Debug.LogError("[Build] No scene to build. Open your gameplay scene (or add it to Build Settings) and retry.");
            return;
        }

        var opts = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = OutPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None,
        };

        // Voice uses the Microphone class; macOS ABORTS the build if the usage description is blank. Set it
        // here programmatically so the build can never fail again on an empty Player Settings field.
        PlayerSettings.macOS.microphoneUsageDescription = "Used to talk to characters by voice.";

        // Clean previous outputs FIRST so the new build is unambiguous — no stale app (date is always today's)
        // and no leftover Burst debug folder with a zero-byte LostExpedition.app "impostor" inside it.
        CleanBuildOutputs();

        Debug.Log($"[Build] Building {scenes.Count} scene(s) -> {OutPath} …");
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[Build] Result: {report.summary.result}  ({report.summary.totalSize / (1024 * 1024)} MB)  at {OutPath}");
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            CleanBurstDebug();   // Burst regenerates the debug folder during the build — remove the impostor
            Debug.Log($"[Build] DONE — run {OutPath} (the only app in Builds/). Right-click → Open if macOS warns.");
            EditorUtility.RevealInFinder(OutPath);
        }
    }

    // Delete the previous app + any *_BurstDebugInformation_DoNotShip so the build output is clean + unambiguous.
    static void CleanBuildOutputs()
    {
        try { if (Directory.Exists(OutPath)) Directory.Delete(OutPath, true); }
        catch (System.Exception e) { Debug.LogWarning($"[Build] could not remove old app: {e.Message}"); }
        CleanBurstDebug();
    }

    static void CleanBurstDebug()
    {
        try
        {
            if (!Directory.Exists("Builds")) return;
            foreach (var d in Directory.GetDirectories("Builds"))
                if (d.EndsWith("_BurstDebugInformation_DoNotShip")) Directory.Delete(d, true);
        }
        catch (System.Exception e) { Debug.LogWarning($"[Build] could not remove Burst debug folder: {e.Message}"); }
    }
}
#endif
