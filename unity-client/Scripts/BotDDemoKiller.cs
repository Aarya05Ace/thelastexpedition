// BotDDemoKiller.cs — STANDALONE, self-bootstrapping suppressor for Book of the Dead's demo player.
//
// PURPOSE: fully silence the two one-time startup NullReferenceExceptions a consumer otherwise sees:
//   1) Gameplay.GameController.SpawnPlayerCo (~GameController.cs:104) — `camera.transform` where the
//      spawn point's camera and `defaultCamera` (= Camera.main) are BOTH null, because the lobby has
//      disabled the forest's tagged MainCamera before the demo spawn runs.
//   2) Gameplay.PlayerController.LateUpdate (PlayerController.cs:67) — `playerCamera.autoFocus` /
//      `characterController.velocity` on the half-built PlayerController(Clone) the spawn produced.
//
// ORIGIN: Code/MultiSceneController.cs (Assembly-CSharp, in the "Master" scene). Its `IEnumerator Start`
// in the EDITOR skips the build-only additive-scene loading (cs:40 `if(!Application.isEditor)`) and falls
// straight to `gameController.SpawnPlayer(nextFrame: false)` (cs:80) — a SYNCHRONOUS spawn (SpawnPlayerCo
// does not `yield return null` first), so it NREs immediately, and the clone it leaves behind NREs again
// in PlayerController.LateUpdate the following frame.
//
// WHY THIS FILE EXISTS SEPARATELY FROM LobbyBootstrap: LobbyBootstrap already kills the same demo in its
// own BeforeSceneLoad hook, but ONLY if the LobbyBootstrap TYPE is actually pulled into the build/scene
// path. This killer is a guaranteed, dependency-free backstop: a `RuntimeInitializeOnLoadMethod` runs for
// any type that ships in an always-loaded assembly (Assembly-CSharp, which this file is in — TombRush has
// no asmdef), so it fires regardless of whether LobbyBootstrap is present. Running BOTH is harmless: every
// operation here is idempotent (disabling an already-disabled component is a no-op).
//
// HOW IT BEATS THE SPAWN: the ONLY point that can beat a synchronous SpawnPlayer(nextFrame:false) is to
// stop MultiSceneController's Start from ever being invoked. Unity calls a scene object's first Start AFTER
// BeforeSceneLoad initialize callbacks, so a MultiSceneController disabled in BeforeSceneLoad never gets
// Start() — the spawn coroutine never begins, no clone is created, and neither NRE site is ever reached.
//
// ASSEMBLY BOUNDARIES (load-bearing):
//   * MultiSceneController is in Assembly-CSharp (Assets/Code has no asmdef) -> reference it DIRECTLY.
//   * GameController / PlayerController are in Gameplay.asmdef. TombRush is also Assembly-CSharp and does
//     NOT reference Gameplay, so we must NOT `using Gameplay;` (hard cross-assembly link error). We toggle
//     GameController by REFLECTION on the "GameController"-tagged GameObject (a built-in tag the BotD 'Game'
//     GO carries), exactly the idiom LobbyBootstrap uses.
//
// EDITOR-GUARDED like the existing suppression: in a real build, MultiSceneController.Start ALSO performs
// the additive-scene loading the game needs, so we only disable it in the Editor. The GameController /
// clone cleanup is harmless in both (the networked player fully replaces the demo), but is likewise gated
// behind isEditor so a build's normal flow is never touched.

using System.Collections;
using UnityEngine;

public static class BotDDemoKiller
{
    // PRIMARY KILL: before any scene object's first Start runs, disable the drivers that spawn the demo.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void KillBeforeSceneLoad()
    {
        if (!Application.isEditor) return;   // build path needs MultiSceneController's real Start()
        KillDemoDrivers();
    }

    // BACKSTOP: a driver or clone can come into existence a frame late (additive load, or the editor's
    // synchronous spawn body executing before the BeforeSceneLoad disable on some Unity versions). Re-run
    // the kill once scene objects exist, then run a short-lived per-frame sweep that also deactivates any
    // half-built clone — long enough to cover the demo's one-time spawn window without lingering.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void KillAfterSceneLoad()
    {
        if (!Application.isEditor) return;
        KillDemoDrivers();
        KillClones();
        Watchdog.Begin();
    }

    static void KillDemoDrivers()
    {
        // MultiSceneController — Assembly-CSharp, reference directly. Disabling .enabled before its Start
        // is invoked stops the demo spawn at the source.
        foreach (var msc in Object.FindObjectsByType<MultiSceneController>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (msc != null) { msc.StopAllCoroutines(); msc.enabled = false; }

        // GameController — Gameplay.asmdef. Toggle by reflection via the "GameController"-tagged GO so we
        // avoid a hard cross-assembly dependency. Its own Start (GameController.cs:33) dereferences
        // Camera.main while the lobby has the forest MainCamera disabled, so disabling it kills a second
        // NRE path too. StopAllCoroutines() covers a SpawnPlayerCo that somehow already started.
        var gcGo = SafeFindTagged("GameController");
        if (gcGo != null)
            foreach (var mb in gcGo.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == "GameController")
                {
                    mb.StopAllCoroutines();
                    mb.enabled = false;
                }
    }

    static void KillClones()
    {
        // The demo spawn names its instances PlayerController(Clone) / PlayerCamera(Clone). Deactivating
        // them stops PlayerController.LateUpdate (PlayerController.cs:67) from running on a half-built clone.
        GameObject.Find("PlayerController(Clone)")?.SetActive(false);
        GameObject.Find("PlayerCamera(Clone)")?.SetActive(false);
    }

    static GameObject SafeFindTagged(string tag)
    {
        try { return GameObject.FindWithTag(tag); }
        catch { return null; }   // tag undefined in this project -> nothing to suppress
    }

    // A throwaway hidden MonoBehaviour that re-applies the kill for a few frames after scene load, then
    // self-destructs. DontDestroyOnLoad so an additive scene load can't orphan it mid-sweep. Cheap: it does
    // ~10 frames of Find + reflection, only in the Editor, only at startup.
    sealed class Watchdog : MonoBehaviour
    {
        static bool started;

        public static void Begin()
        {
            if (started) return;
            started = true;
            var go = new GameObject("~BotDDemoKillerWatchdog") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Watchdog>();
        }

        IEnumerator Start()
        {
            // Cover the demo's one-time spawn window (the editor path is synchronous, but a clone can be
            // instantiated a frame late). ~10 frames is ample; then we stop touching anything.
            for (int i = 0; i < 10; i++)
            {
                KillDemoDrivers();
                KillClones();
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
