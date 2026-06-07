// WeaponRig.cs — static helper that mounts the AK-74 to a humanoid character and returns its Weapon.
//
// WeaponRig.Equip(character):
//   1) loads the AK-74M prefab BUILD-SAFE: PRIMARY Resources.Load("Weapons/ak74m") (works in editor AND
//      player builds), with the editor-only AssetDatabase("Assets/Cold War Weapons/AK74M/Prefabs/ak74m.prefab")
//      kept only as a #if UNITY_EDITOR fallback,
//   2) parents it to the right-hand bone (Animator.GetBoneTransform(HumanBodyBones.RightHand)) using
//      the researched local offset/rotation (rifle local +Z is the firing direction), scale 1,
//   3) finds/creates a 'Muzzle' anchor at the barrel tip (~local 0, 0.095, 0.50 on the rifle root),
//   4) strips the AK's own colliders (so it never blocks shots / camera) and its unused demo Animator,
//   5) adds + configures a Weapon (25 dmg) on the character and points its muzzle at the anchor.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. The Resources copy lives at
// "Assets/TombRush/Resources/Weapons/ak74m.prefab"; its internal material/mesh refs resolve by their
// original GUIDs. If neither load yields a prefab the method is a graceful no-op that still adds a Weapon
// so callers always get a non-null component to fire with. Fully null-safe; never throws.

using UnityEngine;

public static class WeaponRig
{
    const string AkPrefabPath = "Assets/Cold War Weapons/AK74M/Prefabs/ak74m.prefab";

    // Right-hand local mount transform (AK-in-fist starting pose). The grip used to float HIGH above the hand,
    // so the gun is dropped along the hand's local DOWN (-0.03 -> -0.06) and pulled slightly back (0.14 -> 0.10)
    // to seat in the palm. These are just the starting values copied onto WeaponMount on the spawned AK — the
    // real tuning happens LIVE in the Inspector (WeaponMount re-pins every LateUpdate).
    static readonly Vector3 HandLocalPos = new Vector3(0.0f, -0.09f, 0.08f);   // dropped lower into the palm
    static readonly Vector3 HandLocalEuler = new Vector3(0f, 90f, 90f);

    // Muzzle anchor in the rifle ROOT's local space (barrel tip; bore-line height).
    static readonly Vector3 MuzzleLocalPos = new Vector3(0f, 0.095f, 0.50f);

    // Mount the AK on 'character', returning the configured Weapon (added to the character root).
    // Returns null only if 'character' is null.
    public static Weapon Equip(GameObject character)
    {
        if (character == null) return null;

        // One Weapon per character; reuse if already present (idempotent).
        var weapon = character.GetComponent<Weapon>();
        if (weapon == null) weapon = character.AddComponent<Weapon>();
        weapon.damage = 25f;

        Transform muzzle = MountRifle(character);
        if (muzzle != null) weapon.muzzle = muzzle;

        return weapon;
    }

    // Instantiate + parent the rifle; returns the Muzzle transform (or null if no rifle could be loaded).
    static Transform MountRifle(GameObject character)
    {
        // PRIMARY (works in editor AND player builds): the AK prefab copied under Resources. Internal
        // material/mesh refs resolve by their original GUIDs, so the flat copy renders identically.
        var prefab = Resources.Load<GameObject>("Weapons/ak74m");
#if UNITY_EDITOR
        // FALLBACK (editor only): if the Resources copy is missing/stale, load the original via AssetDatabase.
        if (prefab == null) prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(AkPrefabPath);
#endif
        if (prefab == null)
        {
            Debug.LogWarning($"[WeaponRig] AK prefab not found at Resources \"Weapons/ak74m\" (or {AkPrefabPath}); equipping weapon with no model.");
            return null;
        }

        var rifle = Object.Instantiate(prefab);
        if (rifle == null) return null;
        rifle.name = "AK74M";

        // Find the right-hand bone via the humanoid Animator. The Animator is usually mounted the SAME frame
        // as CharacterRig, so its bone mapping may not be built yet -> Rebind() forces it before we query
        // (this is why the gun was ending up parented to the root + dangling at the hip).
        Transform hand = null;
        var anim = character.GetComponent<Animator>();
        if (anim != null && anim.avatar != null && anim.isHuman)
        {
            hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (hand == null) { anim.Rebind(); hand = anim.GetBoneTransform(HumanBodyBones.RightHand); }
        }
        if (hand == null) hand = FindHandBone(character.transform);   // name-search fallback
        if (hand == null)
        {
            Debug.LogWarning($"[WeaponRig] Right-hand bone not found on {character.name}; mounting to root (gun will float).");
            hand = character.transform;
        }

        rifle.transform.SetParent(hand, false);
        rifle.transform.localPosition = HandLocalPos;
        rifle.transform.localRotation = Quaternion.Euler(HandLocalEuler);
        rifle.transform.localScale = Vector3.one; // prefab is authored at real metres; never rescale.

        // The humanoid Animator rewrites the hand bone every frame, so the localPosition/localRotation set
        // above would be stomped by animation -> add a WeaponMount that re-pins the gun in LateUpdate (after
        // the Animator writes the bone) and exposes the offset for LIVE Inspector tuning. The WeaponMount
        // class is unconditional (runtime); only this AddComponent sits in the editor-guarded mount path.
        var mount = rifle.AddComponent<WeaponMount>();
        mount.localPos = HandLocalPos;
        mount.localEuler = HandLocalEuler;

        // The prefab carries an unused demo Animator (no avatar) that only drives its own bolt parts;
        // disable it to avoid a "controller has no avatar" console warning and any stray motion.
        var rifleAnim = rifle.GetComponent<Animator>();
        if (rifleAnim != null) rifleAnim.enabled = false;

        // Strip every collider on the rifle so it can't block the shooter's own ray or the camera.
        foreach (var c in rifle.GetComponentsInChildren<Collider>(true))
            if (c != null) Object.Destroy(c);

        // Find or create the Muzzle anchor at the barrel tip (rifle-root local space).
        Transform muzzle = rifle.transform.Find("Muzzle");
        if (muzzle == null)
        {
            var m = new GameObject("Muzzle");
            muzzle = m.transform;
            muzzle.SetParent(rifle.transform, false);
            muzzle.localPosition = MuzzleLocalPos;
            muzzle.localRotation = Quaternion.identity; // inherits rifle +Z = downrange
        }
        return muzzle;
    }

    // Last-resort recursive search for the right-hand bone by name (the humanoid GetBoneTransform path above
    // is primary; this only runs if the avatar query fails). Skips finger bones.
    static Transform FindHandBone(Transform root)
    {
        if (root == null) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("thumb") || n.Contains("index") || n.Contains("middle") ||
                n.Contains("ring") || n.Contains("pinky") || n.Contains("finger")) continue;
            bool right = n.Contains("right") || n.EndsWith("_r") || n.EndsWith(".r") || n.Contains("r_hand");
            if (n.Contains("hand") && right) return t;
        }
        return null;
    }
}
