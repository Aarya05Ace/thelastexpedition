// WeaponMount.cs — a tiny live-tunable grip pin for a weapon parented to a hand bone.
//
// The humanoid Animator REWRITES the RightHand bone transform every frame, so setting the gun's
// localPosition/localRotation once at mount time gets stomped by animation. This component re-pins the
// gun in LateUpdate (AFTER the Animator has written the bone) and exposes the offset as public fields,
// so the AK can be nudged into the palm LIVE in the Inspector during Play mode and the result kept.
//
// Pure Unity, no SpacetimeDB types -> no Vector3 alias needed. Added by WeaponRig.MountRifle to the
// instantiated AK. Fully null-safe; never throws.

using UnityEngine;

public class WeaponMount : MonoBehaviour
{
    [Tooltip("Local position offset in the hand bone's space (live-tunable in Play mode).")]
    public Vector3 localPos = new Vector3(0.0f, -0.06f, 0.10f);

    [Tooltip("Local euler rotation in the hand bone's space (live-tunable in Play mode).")]
    public Vector3 localEuler = new Vector3(0f, 90f, 90f);

    // Re-apply AFTER the Animator writes the hand bone this frame, so the gun stays pinned to the palm
    // instead of drifting with the animated hand. LateUpdate runs after the animation update.
    void LateUpdate()
    {
        var t = transform;
        t.localPosition = localPos;
        t.localRotation = Quaternion.Euler(localEuler);
    }
}
