// LocomotionControllerBuilder.cs — EDITOR-only builder for the shared humanoid locomotion rig.
//
// Lives under an Editor/ folder, so UnityEditor.* APIs are allowed WITHOUT #if UNITY_EDITOR.
//
// Two jobs, both defensive (never throw):
//   1) Guarantee every character source FBX (player + NPC + bodyguard) is a Humanoid rig so the shared
//      AnimatorController retargets across all of them. Already-Humanoid models are skipped.
//   2) Build "Assets/TombRush/Locomotion.controller":
//        - a default "Locomotion" state holding a 1D BlendTree on float "Speed" (Idle 0 / Walk 1.5 / Run 4),
//        - a one-shot "Jump" state entered by the "Jump" trigger and auto-returning to Locomotion.
//      Clips come from the Kevin Iglesias "Human Animations" pack (clean Humanoid Idle/Walk/Run/Jump).
//
// Self-builds on import via [InitializeOnLoad] ONLY if the controller doesn't already exist, and is
// re-runnable on demand via Tools > Lost Expedition > Build Locomotion (rebuilds fresh, no duplicates).

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

[InitializeOnLoad]
public static class LocomotionControllerBuilder
{
    const string ControllerPath = "Assets/TombRush/Locomotion.controller";

    // BUILD-SAFE copies: the editor builder writes the controllers to the source paths above, but a PLAYER
    // BUILD can't run the builder, so the runtime loaders (CharacterRig / NpcAgent) Resources.Load these
    // copies. After every (re)build we mirror the fresh controller into Resources via AssetDatabase.CopyAsset
    // (which assigns the copy a NEW guid — NOT a duplicate of the source — so there's no GUID collision).
    const string ControllerResPath = "Assets/TombRush/Resources/Controllers/Locomotion.controller";
    const string NpcIdleResPath = "Assets/TombRush/Resources/Controllers/NpcIdle.controller";

    // Tiny breathing-idle controller for the CHAT NPCs (Mara/Eli/Brother Vael) — one looped state,
    // no params. NpcAgent swaps this in over the shared Locomotion controller at spawn (editor-only).
    const string NpcIdlePath = "Assets/TombRush/NpcIdle.controller";
    const string IdleStancePath = "Assets/Idle MoCap/Animations/Idle_Stance_02_MB_v01.fbx";

    const string SpeedParam = "Speed";
    const string JumpParam = "Jump";

    // Combat params (contract shared with LocalPlayer / NpcAgent / Health death-handler).
    const string ArmedParam = "Armed";  // bool   — holds the upper-body rifle Aim pose / gates the Combat layer
    const string FireParam = "Fire";    // trigger — one shot
    const string DeadParam = "Dead";    // trigger — full-body death
    const string HitParam = "Hit";      // trigger — full-body damage flinch

    const string KI = "Assets/Kevin Iglesias/Human Animations/Animations/Male/";

    // Upper-body avatar mask (spine/chest/head/arms/hands = 1, both legs = 0) for the Combat override layer.
    const string UpperMaskPath = "Assets/Kevin Iglesias/Human Animations/Models/Avatar Masks/Human Body Upper Mask.mask";

    // Character source FBX models. All are reported Humanoid today; we still verify + convert defensively.
    static readonly string[] CharacterFbx =
    {
        "Assets/Survivalist/Basemesh/SK_Military_Survivalist.fbx",
        "Assets/npc_casual_set_00/Mesh/npc_hmn_01m.fbx",
        "Assets/npc_casual_set_00/Mesh/npc_hmn_01f.fbx",
        "Assets/BodyGuards/Meshes/SkelMesh_Bodyguard_01.fbx",
        "Assets/BodyGuards/Meshes/SkelMesh_Bodyguard_02.fbx",
        "Assets/BodyGuards/Meshes/SkelMesh_Bodyguard_03.fbx",
        "Assets/BodyGuards/Meshes/SkelMesh_Bodyguard_04.fbx",
    };

    // Kevin Iglesias humanoid locomotion clips (clean Idle/Walk/Run + a full Jump).
    static readonly string[] IdleClipCandidates = { KI + "Idles/HumanM@Idle01.fbx" };
    static readonly string[] WalkClipCandidates = { KI + "Movement/Walk/HumanM@Walk01_Forward.fbx" };
    static readonly string[] RunClipCandidates  = { KI + "Movement/Run/HumanM@Run01_Forward.fbx" };
    static readonly string[] JumpClipCandidates = { KI + "Movement/Jump/HumanM@Jump01.fbx" };

    // Kevin Iglesias humanoid combat clips (rifle aim/fire upper-body + full-body death/damage).
    static readonly string[] AimClipCandidates    = { KI + "Combat/Rifle/HumanM@Rifle_Aim01.fbx" };
    static readonly string[] FireClipCandidates   = { KI + "Combat/Rifle/HumanM@Rifle_Aim01_Shoot01.fbx" };
    static readonly string[] DeathClipCandidates  = { KI + "Combat/HumanM@Death01.fbx" };
    static readonly string[] DamageClipCandidates = { KI + "Combat/HumanM@Damage01.fbx" };

    static LocomotionControllerBuilder()
    {
        EditorApplication.delayCall += () =>
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) == null)
                Build();
            else if (!NpcIdleIsValid())
                BuildNpcIdle();   // Locomotion exists, but the NPC idle is missing/empty/stale — (re)build just it.
        };
    }

    // True only if NpcIdle.controller exists AND actually holds a state whose motion is the
    // Idle_Stance_02 clip. Catches the silent failure where the controller exists on disk but was
    // built empty / with a null motion / before the clip was importable — in which case the chat NPCs
    // would swap in a controller that plays nothing. Returning false forces a clean rebuild.
    static bool NpcIdleIsValid()
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(NpcIdlePath);
        if (ctrl == null || ctrl.layers == null || ctrl.layers.Length == 0) return false;

        var sm = ctrl.layers[0].stateMachine;
        if (sm == null || sm.states == null) return false;

        foreach (var s in sm.states)
        {
            var clip = s.state != null ? s.state.motion as AnimationClip : null;
            if (clip != null && AssetDatabase.GetAssetPath(clip) == IdleStancePath) return true;
        }
        return false;
    }

    [MenuItem("Tools/Lost Expedition/Build Locomotion")]
    public static void Build()
    {
        try
        {
            EnsureHumanoidRigs();
            BuildController();
            BuildNpcIdle();   // tiny breathing-idle controller for the chat NPCs (independent of the above)
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Locomotion] Build aborted: {e.Message}\n{e.StackTrace}");
        }
    }

    // ---- 1) Humanoid rig enforcement on the CHARACTER meshes (the clips are already Humanoid) ---------

    static void EnsureHumanoidRigs()
    {
        foreach (var path in CharacterFbx)
            EnsureHumanoidRig(path);
    }

    // ---- 2) AnimatorController: locomotion blend tree + one-shot jump --------------------------------

    static void BuildController()
    {
        var idle = LoadFirst(IdleClipCandidates, "Idle");
        var walk = LoadFirst(WalkClipCandidates, "Walk");
        var run  = LoadFirst(RunClipCandidates, "Run");
        var jump = LoadFirst(JumpClipCandidates, "Jump");

        // Combat clips (each may be null; every use below is null-guarded so the build never throws).
        var aim    = LoadFirst(AimClipCandidates, "Aim");
        var fire   = LoadFirst(FireClipCandidates, "Fire");
        var death  = LoadFirst(DeathClipCandidates, "Death");
        var damage = LoadFirst(DamageClipCandidates, "Damage");

        var any = idle ?? walk ?? run;
        if (any == null) { Debug.LogWarning("[Locomotion] No locomotion clips found; controller not built."); return; }
        idle = idle ?? any;
        walk = walk ?? run ?? any;
        run  = run ?? walk;

        EnsureClipsLoop(idle, walk, run);   // jump is one-shot; do NOT loop it
        if (aim != null) EnsureClipsLoop(aim);   // sustained ADS hold pose loops; Fire/Death/Damage stay one-shot

        // Recreate fresh so re-runs never pile up duplicate states / parameters / blend trees.
        AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        if (controller == null) { Debug.LogWarning("[Locomotion] Failed to create controller asset."); return; }

        controller.AddParameter(SpeedParam, AnimatorControllerParameterType.Float);
        controller.AddParameter(JumpParam, AnimatorControllerParameterType.Trigger);
        controller.AddParameter(ArmedParam, AnimatorControllerParameterType.Bool);
        controller.AddParameter(FireParam, AnimatorControllerParameterType.Trigger);
        controller.AddParameter(DeadParam, AnimatorControllerParameterType.Trigger);
        controller.AddParameter(HitParam, AnimatorControllerParameterType.Trigger);

        var sm = controller.layers[0].stateMachine;

        // Locomotion: 1D blend tree on Speed.
        var blendTree = new BlendTree
        {
            name = "Locomotion",
            blendType = BlendTreeType.Simple1D,
            blendParameter = SpeedParam,
            useAutomaticThresholds = false,
        };
        AssetDatabase.AddObjectToAsset(blendTree, controller);
        var children = new List<ChildMotion>();
        // Thresholds in m/s to MATCH the player's real speeds (jog 5, sprint 6.25) — CharacterLocomotion
        // feeds raw horizontal velocity into "Speed". Idle at rest, Walk during the accel ramp, Run by jog.
        AddChild(children, idle, 0f);
        AddChild(children, walk, 2.5f);
        AddChild(children, run, 5.5f);
        blendTree.children = children.ToArray();

        var loco = sm.AddState("Locomotion");
        loco.motion = blendTree;
        loco.writeDefaultValues = true;
        sm.defaultState = loco;

        // Jump: a one-shot state entered by the Jump trigger; eases back to locomotion after ~60% of the clip.
        if (jump != null)
        {
            var jumpState = sm.AddState("Jump");
            jumpState.motion = jump;
            jumpState.writeDefaultValues = true;

            var toJump = loco.AddTransition(jumpState);
            toJump.hasExitTime = false;
            toJump.duration = 0.08f;
            toJump.AddCondition(AnimatorConditionMode.If, 0f, JumpParam);

            var toLoco = jumpState.AddTransition(loco);
            toLoco.hasExitTime = true;
            toLoco.exitTime = 0.6f;
            toLoco.duration = 0.2f;
        }

        // ---- Base-layer full-body one-shots: Death + Damage (override the legs too) ------------------
        // Death MUST be added before Damage so its AnyState transition is evaluated first and a flinch
        // never cancels a death. Death has no exit transition (character stays in the dead pose).
        AnimatorState deathState = null;
        if (death != null)
        {
            deathState = sm.AddState("Death");
            deathState.motion = death;
            deathState.writeDefaultValues = true;
            deathState.tag = "Dead";   // lets script detect the dead state if needed

            var toDeath = sm.AddAnyStateTransition(deathState);
            toDeath.hasExitTime = false;
            toDeath.duration = 0.1f;
            toDeath.canTransitionToSelf = false;
            toDeath.AddCondition(AnimatorConditionMode.If, 0f, DeadParam);
        }

        if (damage != null)
        {
            var damageState = sm.AddState("Damage");
            damageState.motion = damage;
            damageState.writeDefaultValues = true;

            var toDamage = sm.AddAnyStateTransition(damageState);
            toDamage.hasExitTime = false;
            toDamage.duration = 0.08f;
            toDamage.canTransitionToSelf = false;
            toDamage.AddCondition(AnimatorConditionMode.If, 0f, HitParam);

            // Auto-return to locomotion after the flinch.
            var damageBack = damageState.AddTransition(loco);
            damageBack.hasExitTime = true;
            damageBack.exitTime = 0.7f;
            damageBack.duration = 0.2f;
        }

        // ---- Combat override layer (upper-body): Empty / Aim / Fire ----------------------------------
        BuildCombatLayer(controller, aim, fire);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(ControllerPath);
        MirrorToResources(ControllerPath, ControllerResPath);   // keep the build-safe Resources copy in sync
        Debug.Log($"[Locomotion] Built {ControllerPath} (Idle/Walk/Run/Jump = {Name(idle)}/{Name(walk)}/{Name(run)}/{Name(jump)}; " +
                  $"Combat Aim/Fire/Death/Damage = {Name(aim)}/{Name(fire)}/{Name(death)}/{Name(damage)}).");
    }

    // ---- NPC breathing-idle controller --------------------------------------------------------------
    // A one-state, zero-parameter AnimatorController whose motion is the Idle_Stance_02 humanoid clip
    // (looped). NpcAgent assigns this to the CHAT NPCs so they breathe in place instead of running the
    // full Locomotion/Combat controller. Fully defensive: if the clip is missing it logs + skips, leaving
    // the NPCs on the existing Locomotion controller.
    static void BuildNpcIdle()
    {
        // Make sure the FBX is imported as a Humanoid rig: NpcAgent swaps this controller onto NPCs that
        // carry a HUMANOID avatar, so a Generic/Legacy idle clip would fail to retarget and T-pose. The
        // Idle MoCap FBX already ships Humanoid (animationType: 3) — this is a defensive re-affirm.
        EnsureHumanoidRig(IdleStancePath);

        var idle = LoadClipAt(IdleStancePath);
        if (idle == null)
        {
            Debug.LogWarning($"[Locomotion] Idle stance clip not found at {IdleStancePath}; " +
                             "NpcIdle controller not built (chat NPCs keep the Locomotion controller).");
            return;
        }

        EnsureClipsLoop(idle);   // loopTime is already on in the .meta; harmless re-affirm for safety.

        // Recreate fresh so re-runs never pile up duplicate states (and so a stale/empty controller on
        // disk is replaced wholesale).
        AssetDatabase.DeleteAsset(NpcIdlePath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(NpcIdlePath);
        if (controller == null) { Debug.LogWarning("[Locomotion] Failed to create NpcIdle controller asset."); return; }

        // Single default state, no parameters — pure looped breathing idle.
        var sm = controller.layers[0].stateMachine;
        var idleState = sm.AddState("Idle");
        idleState.motion = idle;
        idleState.writeDefaultValues = true;
        sm.defaultState = idleState;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(NpcIdlePath);
        MirrorToResources(NpcIdlePath, NpcIdleResPath);   // keep the build-safe Resources copy in sync

        // Verify the motion actually stuck (it can silently end up null if the clip ref went stale during
        // the reimport above) so we don't ship a controller that plays nothing.
        if (NpcIdleIsValid())
            Debug.Log($"[Locomotion] Built {NpcIdlePath} (Idle = {Name(idle)}).");
        else
            Debug.LogWarning($"[Locomotion] Built {NpcIdlePath} but its Idle state has no/ wrong motion; " +
                             "chat NPCs will keep the Locomotion controller.");
    }

    // Mirror a freshly-built controller into Resources so the runtime loaders (which Resources.Load it for
    // build-safety) always pick up the LATEST build instead of a stale snapshot. AssetDatabase.CopyAsset
    // creates the copy WITH A NEW GUID (no duplicate-GUID collision with the source) and writes a fresh .meta
    // for it — that's fine, .meta only matters in-editor; Resources.Load resolves by the path/name. Fully
    // defensive: a missing source or a failed copy just logs + leaves the existing Resources copy in place.
    static void MirrorToResources(string srcPath, string resPath)
    {
        try
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(srcPath) == null)
            {
                Debug.LogWarning($"[Locomotion] Source controller missing at {srcPath}; Resources copy left untouched.");
                return;
            }

            // Ensure the destination folder chain exists (Assets/TombRush/Resources/Controllers).
            EnsureFolder("Assets/TombRush/Resources");
            EnsureFolder("Assets/TombRush/Resources/Controllers");

            // Replace any stale copy so the mirror is always the latest build.
            if (AssetDatabase.LoadMainAssetAtPath(resPath) != null) AssetDatabase.DeleteAsset(resPath);

            if (AssetDatabase.CopyAsset(srcPath, resPath))
            {
                AssetDatabase.ImportAsset(resPath);
                Debug.Log($"[Locomotion] Mirrored build-safe copy -> {resPath}");
            }
            else
            {
                Debug.LogWarning($"[Locomotion] Failed to mirror {srcPath} -> {resPath}; existing Resources copy left in place.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Locomotion] MirrorToResources({srcPath}) failed: {e.Message}");
        }
    }

    // Create a project folder if it doesn't already exist (AssetDatabase.CreateFolder is no-op-unsafe on an
    // existing folder, so guard it). Only handles one level under an existing parent, called in chain above.
    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        int slash = folder.LastIndexOf('/');
        if (slash <= 0) return;
        string parent = folder.Substring(0, slash);
        string leaf = folder.Substring(slash + 1);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // Force a single FBX to import as a Humanoid rig (idempotent: already-Humanoid models are skipped).
    static void EnsureHumanoidRig(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) { Debug.LogWarning($"[Locomotion] No ModelImporter at {path} (skipped)."); return; }
        if (importer.animationType == ModelImporterAnimationType.Human) return;

        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        try { importer.SaveAndReimport(); Debug.Log($"[Locomotion] Converted to Humanoid: {path}"); }
        catch (System.Exception e) { Debug.LogWarning($"[Locomotion] Could not reimport {path} as Humanoid: {e.Message}"); }
    }

    // ---- Combat override layer builder ---------------------------------------------------------------
    // New OVERRIDE layer "Combat" masked to the upper body so a rifle Aim/Fire plays on the torso/arms while
    // the base layer keeps driving the legs. defaultWeight = 1 (mask zeroes the legs); the layer holds an
    // Empty default state, so when NOT Armed the upper body falls through to base-layer locomotion.
    static void BuildCombatLayer(AnimatorController controller, AnimationClip aim, AnimationClip fire)
    {
        if (controller == null) return;

        var upperMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperMaskPath);
        float weight = 1f;
        if (upperMask == null)
        {
            // Safe fallback: build the layer maskless at weight 0 so it can never corrupt the base locomotion.
            Debug.LogWarning($"[Locomotion] Upper-body mask not found at {UpperMaskPath}; " +
                             "creating Combat layer maskless at weight 0 (no upper-body combat pose).");
            weight = 0f;
        }

        var combatSm = new AnimatorStateMachine { name = "Combat", hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(combatSm, controller);

        // Empty default state — when not Armed, upper body falls through to the base layer.
        var empty = combatSm.AddState("Empty");
        empty.writeDefaultValues = true;
        combatSm.defaultState = empty;

        AnimatorState aimState = null;
        if (aim != null)
        {
            aimState = combatSm.AddState("Aim");
            aimState.motion = aim;
            aimState.writeDefaultValues = true;

            // Empty -> Aim when Armed becomes true; Aim -> Empty when Armed becomes false.
            var toAim = empty.AddTransition(aimState);
            toAim.hasExitTime = false;
            toAim.duration = 0.15f;
            toAim.AddCondition(AnimatorConditionMode.If, 0f, ArmedParam);

            var toEmpty = aimState.AddTransition(empty);
            toEmpty.hasExitTime = false;
            toEmpty.duration = 0.2f;
            toEmpty.AddCondition(AnimatorConditionMode.IfNot, 0f, ArmedParam);

            if (fire != null)
            {
                var fireState = combatSm.AddState("Fire");
                fireState.motion = fire;
                fireState.writeDefaultValues = true;

                // Aim -> Fire (snappy) on the Fire trigger; Fire -> Aim auto-returns so firing can repeat.
                var toFire = aimState.AddTransition(fireState);
                toFire.hasExitTime = false;
                toFire.duration = 0.02f;
                toFire.AddCondition(AnimatorConditionMode.If, 0f, FireParam);

                var fireBack = fireState.AddTransition(aimState);
                fireBack.hasExitTime = true;
                fireBack.exitTime = 0.85f;
                fireBack.duration = 0.05f;
            }
        }

        var combat = new AnimatorControllerLayer
        {
            name = "Combat",
            avatarMask = upperMask,                 // null in the fallback path; weight 0 keeps it inert
            defaultWeight = weight,
            blendingMode = AnimatorLayerBlendingMode.Override,
            stateMachine = combatSm,
        };
        controller.AddLayer(combat);
    }

    static void AddChild(List<ChildMotion> list, AnimationClip clip, float threshold)
    {
        if (clip == null) return;
        list.Add(new ChildMotion { motion = clip, threshold = threshold, timeScale = 1f });
    }

    // ---- clip loading helpers ------------------------------------------------------------------------

    static AnimationClip LoadFirst(string[] candidates, string label)
    {
        if (candidates != null)
        {
            foreach (var path in candidates)
            {
                if (string.IsNullOrEmpty(path)) continue;
                var clip = LoadClipAt(path);
                if (clip != null) return clip;
            }
        }
        Debug.LogWarning($"[Locomotion] No '{label}' clip found among candidates; will fall back.");
        return null;
    }

    static AnimationClip LoadClipAt(string fbxPath)
    {
        var direct = AssetDatabase.LoadAssetAtPath<AnimationClip>(fbxPath);
        if (direct != null) return direct;

        var all = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
        if (all != null)
        {
            foreach (var obj in all)
                if (obj is AnimationClip c && (c.hideFlags & HideFlags.HideInHierarchy) == 0)
                    return c;
            foreach (var obj in all)
                if (obj is AnimationClip c) return c;
        }
        return null;
    }

    static void EnsureClipsLoop(params AnimationClip[] clips)
    {
        var done = new HashSet<string>();
        foreach (var clip in clips)
        {
            if (clip == null) continue;
            var path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path) || !done.Add(path)) continue;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) continue;

            var clipImports = importer.clipAnimations;
            if (clipImports == null || clipImports.Length == 0) clipImports = importer.defaultClipAnimations;
            if (clipImports == null || clipImports.Length == 0) continue;

            bool changed = false;
            for (int i = 0; i < clipImports.Length; i++)
                if (!clipImports[i].loopTime) { clipImports[i].loopTime = true; changed = true; }
            if (!changed) continue;

            importer.clipAnimations = clipImports;
            try { importer.SaveAndReimport(); }
            catch (System.Exception e) { Debug.LogWarning($"[Locomotion] Could not set loop on {path}: {e.Message}"); }
        }
    }

    static string Name(AnimationClip c) => c != null ? c.name : "<none>";
}
