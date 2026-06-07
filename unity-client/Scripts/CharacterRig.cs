// CharacterRig.cs — shared runtime rig applied to every spawned character (player, remote, NPC).
//
// One entry point, CharacterRig.Apply(model, localControl), gives a freshly-spawned model:
//   - realistic collision sized from its actual renderer bounds (a CharacterController for the
//     locally-controlled player, a CapsuleCollider for remote players + NPCs so they're solid),
//   - an Animator wired to the shared "Assets/TombRush/Locomotion.controller" (loaded in-editor via
//     AssetDatabase; in a player build, assign the controller in the Inspector instead), reusing the
//     model's own Avatar, and
//   - a CharacterLocomotion driver that feeds the Animator's "Speed" float from real movement.
//
// Fully null-safe: a missing controller asset just skips the Animator wiring — collision still applies.
// No SpacetimeDB types here, so no Vector3 alias is needed.

using UnityEngine;

public static class CharacterRig
{
    const string ControllerPath = "Assets/TombRush/Locomotion.controller";

    // model:        the spawned character GameObject (Survivalist / NPC / bodyguard instance).
    // localControl: true for the local player (CharacterController-driven); false for remote players + NPCs.
    public static void Apply(GameObject model, bool localControl)
    {
        if (model == null) return;

        // Combined renderer bounds in the MODEL's local space, so collision matches the body even if
        // the root transform is rotated/offset on the podium or in the world.
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        bool haveBounds = false;
        Bounds local = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            // Convert world-space renderer bounds into the model's local frame.
            var corners = WorldCorners(r.bounds);
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 lp = model.transform.InverseTransformPoint(corners[i]);
                if (!haveBounds) { local = new Bounds(lp, Vector3.zero); haveBounds = true; }
                else local.Encapsulate(lp);
            }
        }

        float height = haveBounds ? Mathf.Max(local.size.y, 0.2f) : 2f;
        float radius = haveBounds
            ? Mathf.Clamp(Mathf.Max(local.extents.x, local.extents.z), 0.2f, 0.5f)
            : 0.45f;
        Vector3 center = haveBounds ? local.center : new Vector3(0f, height * 0.5f, 0f);

        if (localControl)
        {
            // Slim the radius so the capsule tracks the torso (not outstretched arms) and stops snagging
            // on tree trunks / rock edges. 0.3 m = 0.6 m wide; don't go below ~0.25 or it can tunnel.
            float ccRadius = haveBounds
                ? Mathf.Clamp(Mathf.Max(local.extents.x, local.extents.z), 0.2f, 0.3f)
                : 0.3f;
            // Anchor the capsule bottom at the feet (model pivot): center.y = height/2 so isGrounded /
            // step / slope tests originate from the ground, eliminating feet-clip / float ambiguity.
            Vector3 ccCenter = new Vector3(center.x, height * 0.5f, center.z);

            var cc = model.GetComponent<CharacterController>();
            if (cc == null) cc = model.AddComponent<CharacterController>();
            cc.height = height;
            cc.radius = ccRadius;
            cc.center = ccCenter;
            cc.slopeLimit = 85f;                                 // near-vertical climbable -> unrestricted traversal over rocks/banks
            cc.stepOffset = Mathf.Min(0.6f, height * 0.45f);     // step over low ledges; kept < height + 2*radius (Unity requirement)
        }
        else
        {
            // Remote players / NPCs: a solid capsule so they don't clip through terrain or each other.
            var cap = model.GetComponent<CapsuleCollider>();
            if (cap == null) cap = model.AddComponent<CapsuleCollider>();
            cap.direction = 1; // Y axis
            cap.height = height;
            cap.radius = radius;
            cap.center = center;
        }

        WireAnimator(model);

        var loco = model.GetComponent<CharacterLocomotion>();
        if (loco == null) model.AddComponent<CharacterLocomotion>();
    }

    static void WireAnimator(GameObject model)
    {
        if (model == null) return;

        var animator = model.GetComponent<Animator>();
        if (animator == null) animator = model.AddComponent<Animator>();
        animator.applyRootMotion = false;

        // ----- 1) HUMAN AVATAR (the T-pose root cause) -------------------------------------------------
        // A HUMANOID avatar MUST be assigned to the Animator for the retargeted humanoid clips to bind to
        // the bones. If the spawned model carries no human avatar (e.g. a Generic-imported FBX), every
        // clip plays into nothing and the rig sits in its bind pose: a T-pose. Resolve + ASSIGN one.
        Avatar human = ResolveHumanAvatar(model, animator);
        if (human != null && animator.avatar != human)
            animator.avatar = human;   // explicit assignment of the HUMAN avatar to the Animator

        bool isHuman = animator.avatar != null && animator.avatar.isHuman;
        if (isHuman)
            Debug.Log($"[CharacterRig] {model.name}: human avatar assigned (avatar.isHuman=true) -> clips will retarget, no T-pose.");
        else
            Debug.LogWarning($"[CharacterRig] {model.name}: NO human avatar (avatar.isHuman=false) -> WILL T-POSE. Ensure its FBX rig = Humanoid (Resources/Models/... import must be animationType: Humanoid; right-click that FBX -> Reimport).");

        // ----- 2) CONTROLLER --------------------------------------------------------------------------
        if (animator.runtimeAnimatorController == null)
        {
            // PRIMARY: Resources copy of the built controller (works in editor AND player builds).
            var controller = Resources.Load<RuntimeAnimatorController>("Controllers/Locomotion");
#if UNITY_EDITOR
            // FALLBACK (editor only): load the freshly-built controller straight from the source path.
            if (controller == null)
                controller = UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
#endif
            if (controller != null) animator.runtimeAnimatorController = controller;
            else Debug.LogWarning($"[CharacterRig] Locomotion controller not found in Resources/Controllers/Locomotion (or at {ControllerPath}); Animator left bare.");
        }

        // ----- 3) BIND IMMEDIATELY (no one-frame T-pose) ----------------------------------------------
        // Avatar + controller may have been (re)assigned AFTER the Animator first initialized this frame,
        // which leaves the pose stale (a visible one-frame T-pose) until the next evaluation. Rebind to
        // the new avatar/controller and force-evaluate frame 0 so the idle pose shows the instant we spawn.
        if (isHuman && animator.runtimeAnimatorController != null)
        {
            try
            {
                animator.Rebind();
                animator.Update(0f);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CharacterRig] {model.name}: Rebind/Update(0) skipped ({e.Message}).");
            }
        }
    }

    // Finds a HUMANOID avatar for this model, preferring one already present on the hierarchy, then (in
    // the editor) pulling it off the source FBX. Returns null if none can be found (model will T-pose).
    static Avatar ResolveHumanAvatar(GameObject model, Animator animator)
    {
        if (model == null) return null;

        // Already a valid human avatar on the target Animator? Keep it.
        if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            return animator.avatar;

        // 1) Reuse any humanoid avatar already present on the model hierarchy (e.g. the raw FBX root's own
        //    Animator after a Humanoid import, or a prefab that bakes the avatar onto a child Animator).
        foreach (var a in model.GetComponentsInChildren<Animator>(true))
            if (a != null && a.avatar != null && a.avatar.isHuman)
                return a.avatar;

#if UNITY_EDITOR
        // 2) Otherwise pull the Humanoid avatar straight off the model's source FBX (where the mesh lives).
        //    Editor-only; a player build relies on the FBX having been imported Humanoid (the
        //    ForceHumanoidModels postprocessor guarantees this), so step 1 supplies the avatar there.
        var smr = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
        Mesh mesh = smr != null ? smr.sharedMesh : null;
        string fbxPath = mesh != null ? UnityEditor.AssetDatabase.GetAssetPath(mesh) : null;
        if (!string.IsNullOrEmpty(fbxPath))
            foreach (var obj in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                if (obj is Avatar av && av != null && av.isHuman)
                    return av;
#endif
        return null;
    }

    static Vector3[] WorldCorners(Bounds b)
    {
        Vector3 c = b.center, e = b.extents;
        return new[]
        {
            c + new Vector3(-e.x, -e.y, -e.z),
            c + new Vector3( e.x, -e.y, -e.z),
            c + new Vector3(-e.x,  e.y, -e.z),
            c + new Vector3( e.x,  e.y, -e.z),
            c + new Vector3(-e.x, -e.y,  e.z),
            c + new Vector3( e.x, -e.y,  e.z),
            c + new Vector3(-e.x,  e.y,  e.z),
            c + new Vector3( e.x,  e.y,  e.z),
        };
    }
}

// Drives the Animator's "Speed" float from real horizontal movement, every frame, smoothed to avoid
// blend-tree jitter. Uses the CharacterController's velocity when present (local player), else derives
// speed from the transform's frame-to-frame position delta (remote players + NPCs, which are lerped).
public class CharacterLocomotion : MonoBehaviour
{
    static readonly int SpeedHash = Animator.StringToHash("Speed");

    [Tooltip("How fast the animated Speed catches up to actual speed (higher = snappier).")]
    public float smoothing = 10f;

    Animator animator;
    CharacterController cc;
    Vector3 lastPos;
    float speed;
    bool hasSpeedParam;

    void Awake()
    {
        animator = GetComponent<Animator>();
        cc = GetComponent<CharacterController>();
        lastPos = transform.position;
        hasSpeedParam = HasParam(animator, SpeedHash, "Speed");
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 vel;
        if (cc != null)
        {
            vel = cc.velocity;
        }
        else
        {
            vel = (transform.position - lastPos) / dt;
            lastPos = transform.position;
        }
        vel.y = 0f; // horizontal speed only

        float target = vel.magnitude;
        speed = Mathf.Lerp(speed, target, 1f - Mathf.Exp(-smoothing * dt));

        if (animator != null && hasSpeedParam && animator.runtimeAnimatorController != null)
            animator.SetFloat(SpeedHash, speed);
    }

    static bool HasParam(Animator a, int hash, string name)
    {
        if (a == null || a.runtimeAnimatorController == null) return false;
        foreach (var p in a.parameters)
            if (p.nameHash == hash || p.name == name) return true;
        return false;
    }
}
