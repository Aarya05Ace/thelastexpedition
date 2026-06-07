// PlayerLoadout.cs — the local player's 3-slot inventory + the single owner of the AK model.
//
// SHARED CONTRACT. Slot 1 = AK-47, Slot 2 = Bread (eat -> Heal 30), Slot 3 = empty/reserved. The player
// SPAWNS HOLSTERED (Selected = 0, empty hands). This component is the ONE place that mounts/toggles the AK
// and drives the Animator "Armed" bool — PlayerCombat asks it for the Weapon and only fires while AkOut.
//
// The AK is mounted ONCE in Awake (via WeaponRig.Equip, which is idempotent) and HIDDEN at spawn. The bread
// is the SM_Bread_03 prefab loaded BUILD-SAFE (PRIMARY Resources.Load("Items/SM_Bread_03"), editor-only
// AssetDatabase kept as a fallback; re-shaded to HDRP/Lit) gripped in the right palm via a WeaponMount so the
// animation doesn't stomp it. If neither load yields the prefab (or in a stripped build) a bread-tan primitive
// fallback keeps slot 2 working. Eating arcs the bread to the mouth (a procedural coroutine, since no eat clip
// exists), bites for the Heal 30, then returns it to the pinned palm.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Every Animator write is guarded on a non-null
// runtimeAnimatorController (the Locomotion controller auto-rebuilds and may be momentarily null). Fully
// null-safe; never throws.

using System.Collections;
using UnityEngine;

public class PlayerLoadout : MonoBehaviour
{
    // HUD inventory binds here (selection or contents changed).
    public System.Action OnChanged;

    // Length-3 labels for the HUD slot boxes.
    public string[] SlotLabels { get; private set; } = { "AK-47", "Bread", "" };

    // 0 = holstered (empty hands), 1 = AK out, 2 = bread out, 3 = empty/reserved.
    public int Selected { get; private set; }

    // True ONLY while slot 1 is selected — PlayerCombat gates firing + ADS on this.
    public bool AkOut => Selected == 1;

    // The Weapon mounted on this root (shared with PlayerCombat, which fetches it for TryFire).
    public Weapon AkWeapon { get; private set; }

    Health health;
    Animator anim;
    GameObject akModel;        // the mounted AK74M GameObject (hidden/shown per slot)
    GameObject breadModel;     // SM_Bread_03 (or fallback primitive), gripped in the right hand
    WeaponMount breadMount;    // re-pins the bread to the palm each LateUpdate so the anim doesn't stomp it
    Transform breadHand;       // the right-hand bone the bread is parented to (its home for the eat arc)
    Vector3 breadWorldScale = Vector3.one * 0.16f;  // explicit WORLD scale (computed from mesh bounds); pinned across the eat arc
    bool isEating;             // guards against stacking eat coroutines on Use spam

    const float BreadHealAmount = 30f;
    const float BreadTargetSize = 0.16f;            // desired longest world side of the bread in the hand (metres)

    void Awake()
    {
        health = GetComponent<Health>();
        anim = GetComponent<Animator>();

        // Mount the AK ONCE (idempotent — safe even if something else equipped first). Grab the model GO from
        // the right-hand bone so we can hide/show it; the model is hidden until slot 1 is selected.
        AkWeapon = WeaponRig.Equip(gameObject);
        akModel = FindMountedAk();

        BuildBread();

        // Spawn HOLSTERED: nothing out, not armed.
        Selected = 0;
        ApplyVisibility();
    }

    // Locate the AK74M model WeaponRig parented to the hand (named "AK74M" in MountRifle). May be null in a
    // player build where the editor-only prefab load is skipped — visibility toggles then no-op gracefully.
    GameObject FindMountedAk()
    {
        var found = transform.Find("AK74M");      // unlikely (it's under the hand bone), but cheap to check
        if (found != null) return found.gameObject;
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t != null && t.name == "AK74M") return t.gameObject;
        return null;
    }

    // Build the bread once: the SM_Bread_03 prefab (Resources.Load primary, editor AssetDatabase fallback)
    // gripped in the right palm via a WeaponMount, collider stripped, start inactive. If neither load yields the
    // prefab -> a bread-tan primitive fallback so slot 2 still works.
    //
    // WHY THE HAND WAS EMPTY (three compounding runtime bugs the prior static-check missed):
    //   1) MATERIAL: M_Bread_Flatbread_01a.mat uses the BUILT-IN "Standard" shader (fileID 46, guid 0000..f000..0000).
    //      That shader is INVALID in HDRP -> the mesh draws magenta/black "error", so even when present it doesn't
    //      read as bread. Fix: re-shade every renderer onto HDRP/Lit with a bread-tan _BaseColor.
    //   2) SCALE: the FBX is authored in CENTIMETRES (UnitScaleFactor=100); the imported mesh longest side is tiny.
    //      The old hardcoded localScale=0.15 produced a ~4 cm crumb (reads as "nothing"). Fix: compute the scale
    //      from the actual renderer world bounds so the longest side is ~0.16 m, robust to whatever the importer did.
    //   3) MOUNT SCALE: parenting under the hand bone inherits the bone's (often NON-UNIFORM) world scale, which can
    //      squash/blow up the bread. Fix: store an explicit WORLD scale and re-pin it (alongside the WeaponMount)
    //      so neither the bone scale nor the eat coroutine can leave it tiny/huge.
    void BuildBread()
    {
        // PRIMARY (works in editor AND player builds): the bread prefab copied under Resources. Its internal
        // mesh/material refs resolve by their original GUIDs, so the flat copy is identical to the source.
        var prefab = Resources.Load<GameObject>("Items/SM_Bread_03");
#if UNITY_EDITOR
        // FALLBACK (editor only): if the Resources copy is missing/stale, load the original via AssetDatabase.
        if (prefab == null)
            prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Baked Bread and Pastry/Prefabs/SM_Bread_03.prefab");
#endif
        if (prefab != null) breadModel = Object.Instantiate(prefab);

        bool isFallback = false;
        if (breadModel == null)
        {
            // GUARANTEED FALLBACK: a bread-tan box so the hand is NEVER empty without the editor-only asset.
            breadModel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            isFallback = true;
        }

        breadModel.name = "Bread";

        // Strip any collider (the prefab ships a convex MeshCollider) so it never blocks shots / the camera.
        foreach (var c in breadModel.GetComponentsInChildren<Collider>(true))
            if (c != null) Destroy(c);

        // (1) Re-shade onto HDRP/Lit so the bread can't draw as the built-in-Standard magenta/black error shader.
        //     Done for BOTH the prefab and the fallback box (the primitive ships a built-in material too).
        ReshadeToHdrp(breadModel, new Color(0.78f, 0.55f, 0.30f));   // warm bread-tan

        // (2) Compute the scale from the REAL renderer bounds so the longest world side is ~0.16 m, regardless of
        //     how the cm-authored FBX got imported. Parent at scale 1 first so bounds are measured unparented,
        //     then derive the local scale (and remember the world scale to re-pin against the hand bone).
        breadModel.transform.SetParent(null, false);
        breadModel.transform.localScale = Vector3.one;
        float longest = LongestRendererBound(breadModel);
        float s = (longest > 1e-5f) ? (BreadTargetSize / longest) : BreadTargetSize;   // box mesh is 1m -> s = 0.16
        breadWorldScale = Vector3.one * s;
        breadModel.transform.localScale = breadWorldScale;

        Transform hand = FindRightHand();
        if (hand != null)
        {
            breadHand = hand;
            // worldPositionStays:true keeps the just-set world scale even under a non-uniform hand bone; then we
            // re-pin that world scale explicitly so the bone's scale can never squash/inflate the bread.
            breadModel.transform.SetParent(hand, true);
            PinBreadWorldScale();

            // Grip in the palm via a WeaponMount so the Animator-driven hand bone doesn't stomp it each frame
            // (same pattern as the AK). Tunable live in Play mode; flat bread sits nestled in the palm.
            breadMount = breadModel.AddComponent<WeaponMount>();
            breadMount.localPos = new Vector3(0.02f, -0.02f, 0.06f);
            breadMount.localEuler = new Vector3(0f, 90f, 90f);
        }
        else
        {
            // No hand bone -> park it on the root so it still exists; it just won't be hand-pinned.
            breadModel.transform.SetParent(transform, true);
            PinBreadWorldScale();
        }

        if (isFallback) Debug.LogWarning("[PlayerLoadout] SM_Bread_03 prefab unavailable; using bread-tan fallback box.");
        Debug.Log($"[PlayerLoadout] Bread built. source={(isFallback ? "FALLBACK CUBE" : "SM_Bread_03 prefab")}, handBone={(breadHand != null ? breadHand.name : "NONE (parked on root)")}, longestBound={longest:F3}m, worldScale={s:F3}");

        breadModel.SetActive(false);
    }

    // Longest side of the combined RENDERER bounds (world units at the current scale). Renderer bounds are already
    // world-space, so call this while the model sits at scale 1 / unparented. Falls back to mesh.bounds, then 1.
    static float LongestRendererBound(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends != null && rends.Length > 0)
        {
            bool has = false;
            Bounds b = default;
            foreach (var r in rends)
            {
                if (r == null) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            if (has)
            {
                Vector3 sz = b.size;
                float m = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
                if (m > 1e-5f) return m;
            }
        }
        // Fallback to the un-transformed mesh bounds if renderer bounds were degenerate.
        var mf = go.GetComponentInChildren<MeshFilter>(true);
        if (mf != null && mf.sharedMesh != null)
        {
            Vector3 sz = mf.sharedMesh.bounds.size;
            float m = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
            if (m > 1e-5f) return m;
        }
        return 1f;
    }

    // Re-pin the bread to its computed WORLD scale, compensating for the parent's (possibly non-uniform) lossyScale
    // so the bread always shows at ~0.16 m no matter what the hand bone is scaled to. Null-safe.
    void PinBreadWorldScale()
    {
        if (breadModel == null) return;
        var t = breadModel.transform;
        Vector3 parentScale = (t.parent != null) ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(
            breadWorldScale.x / (Mathf.Approximately(parentScale.x, 0f) ? 1f : parentScale.x),
            breadWorldScale.y / (Mathf.Approximately(parentScale.y, 0f) ? 1f : parentScale.y),
            breadWorldScale.z / (Mathf.Approximately(parentScale.z, 0f) ? 1f : parentScale.z));
    }

    // Force every renderer onto HDRP/Lit with a bread-tan base colour. The bread package material uses the
    // BUILT-IN Standard shader, which renders magenta/black in HDRP; the primitive fallback ships a built-in
    // material too. Swapping the shader (or, if HDRP/Lit can't be found, at least the colour) keeps it visible.
    static void ReshadeToHdrp(GameObject go, Color c)
    {
        var hdrp = Shader.Find("HDRP/Lit");
        foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
        {
            if (rend == null) continue;
            var mat = rend.material;   // instance (safe to mutate; won't touch the shared asset)
            if (mat == null) continue;
            if (hdrp != null) mat.shader = hdrp;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        }
    }

    // Right-hand bone via the humanoid Animator, with the same Rebind() fallback WeaponRig uses (the bone map
    // may not be built the same frame the Animator is mounted).
    Transform FindRightHand()
    {
        if (anim != null && anim.avatar != null && anim.isHuman)
        {
            var h = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (h == null) { anim.Rebind(); h = anim.GetBoneTransform(HumanBodyBones.RightHand); }
            if (h != null) return h;
        }
        return null;
    }

    // Select a slot. Re-selecting the currently active slot holsters (back to 0 / empty hands). Slot 0 is the
    // explicit holster used by death/respawn. Always re-applies visibility + the Armed flag, then fires OnChanged.
    public void Select(int slot)
    {
        if (slot == Selected && slot != 0) slot = 0;   // toggle the active slot off -> holster
        Selected = Mathf.Clamp(slot, 0, 3);
        ApplyVisibility();
        OnChanged?.Invoke();
    }

    // Safety net: if this component is disabled mid-eat (death/respawn StopAllCoroutines, scene unload, etc.) the
    // eat coroutine won't reach its re-pin block, which would leave the bread detached in world space at the wrong
    // scale and the mount disabled -> an empty hand next time. Re-home it here so slot 2 always has a model.
    void OnDisable()
    {
        if (!isEating || breadModel == null) return;
        var t = breadModel.transform;
        if (breadHand != null) t.SetParent(breadHand, true);
        else t.SetParent(transform, true);
        PinBreadWorldScale();
        if (breadMount != null) breadMount.enabled = true;
        isEating = false;
    }

    // Apply the model show/hide + Animator "Armed" for the current selection.
    void ApplyVisibility()
    {
        bool ak = Selected == 1;
        bool bread = Selected == 2;

        if (akModel != null) akModel.SetActive(ak);
        // Keep the bread shown while an eat is mid-flight even if the slot toggles, so the arc isn't cut off;
        // the coroutine restores the correct visibility when it finishes.
        if (breadModel != null) breadModel.SetActive(bread || isEating);

        if (anim != null && anim.runtimeAnimatorController != null)
            anim.SetBool("Armed", ak);
    }

    // Lower / raise the rifle for a reload. Drops the Animator "Armed" bool (so the upper-body Combat layer
    // falls back to the lowered locomotion pose, reading as "swap the mag") while r is true, then restores it.
    // Only ever writes Armed while the AK is the selected slot, so it never fights ApplyVisibility's own writes
    // (which run on Select). Null-safe + guarded on a live controller, exactly like ApplyVisibility.
    public void SetReloading(bool r)
    {
        if (!AkOut) return;   // only meaningful with the rifle out; holstered/bread states own Armed themselves
        if (anim != null && anim.runtimeAnimatorController != null)
            anim.SetBool("Armed", !r);
    }

    // Use the currently selected item. Only the bread (slot 2) does anything: play a procedural eat (arc to
    // the mouth, bite, return) that fires the Heal 30 + OnChanged at the bite. No eat clip exists (asset facts)
    // -> the arc is hand-coded. Guarded so spamming Use can't stack coroutines mid-eat.
    public void UseSelected()
    {
        if (Selected != 2) return;
        if (health == null || breadModel == null) return;
        if (isEating) return;

        StartCoroutine(EatBread());
    }

    // Procedural eat: disable the palm pin, arc the bread up to the mouth, hold for a bite (Heal here), then
    // arc it back to the hand and re-enable the pin. Animated in WORLD space so the moving hand bone doesn't
    // drag it. Fully null-safe; the mount is always restored (even on an early-out) so the bread never detaches.
    IEnumerator EatBread()
    {
        isEating = true;

        var t = breadModel.transform;
        Transform origParent = t.parent;

        // Stop the WeaponMount yanking the bread back to the palm mid-arc.
        if (breadMount != null) breadMount.enabled = false;

        // Detach to world space so we can drive an arc independent of the animated hand bone.
        t.SetParent(null, true);
        Quaternion startRot = t.rotation;

        const float upDur = 0.35f;    // hand -> mouth
        const float holdDur = 0.25f;  // bite
        bool healed = false;

        float e = 0f;
        while (e < upDur)
        {
            // Re-sample each frame: the player (and hand) can be moving while eating.
            Vector3 from = (breadHand != null) ? breadHand.position : t.position;
            Vector3 to = MouthPoint();
            float k = e / upDur;
            // Slerp gives a natural curved arc up to the face rather than a straight line.
            Vector3 p = Vector3.Slerp(from - transform.position, to - transform.position, k) + transform.position;
            t.position = p;
            t.rotation = Quaternion.Slerp(startRot, BiteRot(), k);   // tilt as if biting
            e += Time.deltaTime;
            yield return null;
        }

        // Hold at the mouth for the bite; fire the heal once (matching the original single Heal + OnChanged).
        e = 0f;
        while (e < holdDur)
        {
            t.position = MouthPoint();
            t.rotation = BiteRot();
            if (!healed && e >= holdDur * 0.4f)
            {
                healed = true;
                if (health != null) health.Heal(BreadHealAmount);
                OnChanged?.Invoke();   // HUD feedback hook
            }
            e += Time.deltaTime;
            yield return null;
        }
        if (!healed && health != null) { health.Heal(BreadHealAmount); OnChanged?.Invoke(); }

        // ONE BREAD: the bite CONSUMES it. A quick shrink "eaten" beat at the mouth, then it's destroyed for
        // good — slot 2 becomes empty and can never be eaten again (UseSelected no-ops once breadModel == null).
        Vector3 baseScale = t.localScale;
        e = 0f; const float shrinkDur = 0.18f;
        while (e < shrinkDur)
        {
            t.position = MouthPoint();
            t.localScale = baseScale * Mathf.Max(0f, 1f - e / shrinkDur);
            e += Time.deltaTime;
            yield return null;
        }

        if (breadMount != null) Destroy(breadMount);
        if (breadModel != null) Destroy(breadModel);
        breadModel = null;
        breadMount = null;
        breadHand = null;
        SlotLabels[1] = "";   // slot 2 is now empty (the single bread has been eaten)
        isEating = false;
        Select(0);            // empty hands; Select fires OnChanged so the HUD refreshes the now-empty slot
    }

    // Mouth target: the humanoid Head bone (slightly in front), else head height above the root. Null-safe.
    Vector3 MouthPoint()
    {
        if (anim != null && anim.avatar != null && anim.isHuman)
        {
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) { anim.Rebind(); head = anim.GetBoneTransform(HumanBodyBones.Head); }
            if (head != null) return head.position + head.forward * 0.10f + Vector3.up * 0.05f;
        }
        return transform.position + Vector3.up * 1.6f;   // fallback: head/eye height
    }

    // A small tilt so the bread reads as being bitten rather than held flat at the mouth.
    Quaternion BiteRot()
    {
        Vector3 fwd = (anim != null) ? transform.forward : Vector3.forward;
        return Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(35f, 0f, 0f);
    }
}
