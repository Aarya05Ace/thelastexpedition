// ExpeditionCamera.cs — Fortnite-style third-person ORBIT camera (the "camera component"; control logic
// lives in LocalPlayer). Player-controlled yaw (unrestricted) + clamped pitch, a head-level pivot, a
// spring-arm distance with a right-shoulder offset, STABLE camera collision, position/rotation lag for a
// smooth feel, and smooth FOV + distance blends for sprint and ADS.
//
// Values follow Epic's Fortnite camera guide: 4m distance, 1.7m pivot, +0.5m right / +0.2m up shoulder,
// 80 FOV (sprint 90 / ADS 65), pitch -80..+60, position lag 0.08s, rotation lag 0.05s, ~0.2m sphere.
//
// STABILITY: the collision clamps the ARM LENGTH, and that length is smoothed ASYMMETRICALLY — it snaps
// IN instantly when something blocks the view (so the camera never clips through a tree) but eases OUT
// slowly when the obstacle clears (so it never "pops"/zooms). That kills the in/out jitter in dense forest.
//
// Runs in LateUpdate so it reads the player's FINAL post-movement position each frame.

using UnityEngine;

[RequireComponent(typeof(Camera))]
public class ExpeditionCamera : MonoBehaviour
{
    [Header("Target")]
    public Transform target;                       // the player (pivot = target + up*pivotHeight)

    [Header("Rig")]
    public float pivotHeight = 1.7f;               // head level
    public float baseDistance = 4.0f;
    public Vector2 shoulder = new Vector2(0.5f, 0.2f); // x = right, y = up

    [Header("FOV")]
    public float baseFov = 80f;
    public float sprintFov = 90f;
    public float adsFov = 65f;

    [Header("Distance by state")]
    public float sprintDistance = 4.5f;
    public float adsDistance = 3.0f;

    [Header("Look")]
    public float mouseSensitivity = 2.2f;
    public float pitchMin = -80f;
    public float pitchMax = 60f;
    public float followIdleDelay = 0.3f;   // mouse must be idle this long before auto-follow kicks in
    public float followRate      = 120f;   // deg/s the camera swings behind the character
    public bool  followEnabled   = false;  // OFF by default: the auto-follow fought the mouse (felt "inverted").
                                           // ON = camera gently trails behind while moving; the mouse always wins.
    public bool  invertX = false;          // mouse X: false = right->look right (standard)
    public bool  invertY = false;          // mouse Y: false = up->look up (standard)

    [Header("Lag (time constants, seconds)")]
    public float positionLag = 0.08f;              // pivot follow
    public float rotationLag = 0.05f;
    public float stateBlend = 0.3f;                // FOV / distance transition
    public float extendTime = 0.45f;               // how slowly the camera eases back OUT after a blockage clears

    [Header("Collision")]
    public LayerMask collisionMask = ~0;           // sphere starts inside the player capsule, so self is ignored
    public float sphereRadius = 0.2f;
    public float collisionBuffer = 0.15f;
    public float minDistance = 0.8f;               // never pull closer than this (stays third-person)

    // ---- driven by LocalPlayer ----
    public bool LookEnabled = true;                // false while typing -> pause look + free the cursor
    public bool Sprinting { get; set; }
    public bool Aiming { get; set; }               // dormant until the weapon lands (ADS)
    public bool  AutoFollow { get; set; }          // LocalPlayer sets true while the player is moving
    public float FollowTargetYaw { get; set; }     // LocalPlayer sets the character's facing yaw each frame

    // ---- consumed by LocalPlayer for camera-relative movement ----
    public Vector3 PlanarForward => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
    public Vector3 PlanarRight   => Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
    public float Yaw => yaw;

    [Header("Recoil")]
    public float recoilMaxDeg = 4f;        // cap on accumulated upward kick so sustained fire can't flip the view
    public float recoilRecoverPerSec = 8f; // deg/s the view settles back down after a kick

    Camera cam;
    float yaw, pitch;
    float recoilPitch;      // transient upward pitch from weapon recoil; eases back to 0 each frame
    float mouseIdleTimer;   // seconds since the mouse last moved
    float curFov, curDist;
    Vector3 smoothPivot;
    float armLen;
    Quaternion curRot;

    void Awake()
    {
        cam = GetComponent<Camera>();
        curFov = baseFov;
        curDist = baseDistance;
        cam.fieldOfView = baseFov;
    }

    void Start()
    {
        if (target != null) yaw = target.eulerAngles.y;   // start behind the player, no snap
        pitch = 10f;
        curRot = Quaternion.Euler(pitch, yaw, 0f);
        smoothPivot = (target != null ? target.position : transform.position) + Vector3.up * pivotHeight;
        armLen = new Vector3(shoulder.x, shoulder.y, baseDistance).magnitude;
        transform.SetPositionAndRotation(smoothPivot + curRot * new Vector3(shoulder.x, shoulder.y, -baseDistance), curRot);
        LockCursor(LookEnabled);
    }

    void LateUpdate()
    {
        if (target == null) return;

        if (LookEnabled)
        {
            float mx = Input.GetAxis("Mouse X");
            float my = Input.GetAxis("Mouse Y");
            bool mouseMoved = Mathf.Abs(mx) > 0.0001f || Mathf.Abs(my) > 0.0001f;

            if (mouseMoved)
            {
                // Active mouse-look ALWAYS wins. Non-inverted: mouse RIGHT -> look right, mouse UP -> look UP
                // (mouse up RAISES pitch, which tilts the view up in this rig). invertX/invertY flip per preference.
                yaw   += (invertX ? -mx : mx) * mouseSensitivity;
                pitch += (invertY ? -my :  my) * mouseSensitivity;
                pitch  = Mathf.Clamp(pitch, pitchMin, pitchMax);
                mouseIdleTimer = 0f;
            }
            else
            {
                mouseIdleTimer += Time.deltaTime;

                // Auto-follow is OFF by default — it fought the mouse (the "inverted" feel). When followEnabled
                // is on, it gently eases behind the character while moving + the mouse is idle (mouse still wins).
                if (followEnabled && AutoFollow && mouseIdleTimer >= followIdleDelay)
                {
                    // Shortest-arc, constant deg/s ease toward the character's facing (handles 359->1 wraparound).
                    yaw = Mathf.MoveTowardsAngle(yaw, FollowTargetYaw, followRate * Time.deltaTime);
                }
            }
        }
        else
        {
            mouseIdleTimer = 0f;   // typing/dead: don't accumulate, don't auto-spin
        }
        LockCursor(LookEnabled);

        // State-driven FOV + distance (ADS over sprint over base), smoothly blended.
        float wantFov  = Aiming ? adsFov      : (Sprinting ? sprintFov      : baseFov);
        float wantDist = Aiming ? adsDistance : (Sprinting ? sprintDistance : baseDistance);
        float sk = Step(stateBlend);
        curFov  = Mathf.Lerp(curFov, wantFov, sk);
        curDist = Mathf.Lerp(curDist, wantDist, sk);
        cam.fieldOfView = curFov;

        // Recoil settles back to 0 over time (the view climbs on each shot, then recovers between shots).
        if (recoilPitch > 0f)
            recoilPitch = Mathf.Max(0f, recoilPitch - recoilRecoverPerSec * Time.deltaTime);

        // Smooth the pivot follow + rotation (the slight Fortnite lag). Recoil ADDS to pitch (higher pitch =
        // look UP in this rig now, matching the mouse "pitch += my" convention) so the view climbs up per shot.
        float renderPitch = Mathf.Clamp(pitch + recoilPitch, pitchMin, pitchMax);
        Vector3 pivot = target.position + Vector3.up * pivotHeight;
        smoothPivot = Vector3.Lerp(smoothPivot, pivot, Step(positionLag));
        curRot = Quaternion.Slerp(curRot, Quaternion.Euler(renderPitch, yaw, 0f), Step(rotationLag));

        // Ideal (unobstructed) arm from the pivot to the shoulder-offset camera spot.
        Vector3 idealPos = smoothPivot + curRot * new Vector3(shoulder.x, shoulder.y, -curDist);
        Vector3 arm = idealPos - smoothPivot;
        float idealLen = arm.magnitude;
        Vector3 armDir = idealLen > 0.001f ? arm / idealLen : -(curRot * Vector3.forward);

        // Collision clamps the ARM LENGTH (not the raw position) so it never clips into geometry.
        float allowedLen = idealLen;
        if (Physics.SphereCast(smoothPivot, sphereRadius, armDir, out RaycastHit hit, idealLen, collisionMask, QueryTriggerInteraction.Ignore))
            allowedLen = Mathf.Max(minDistance, hit.distance - collisionBuffer);

        // ASYMMETRIC: snap IN instantly (never clip), ease OUT slowly (never pop). This is the glitch fix.
        if (allowedLen < armLen) armLen = allowedLen;
        else armLen = Mathf.Lerp(armLen, allowedLen, Step(extendTime));

        transform.SetPositionAndRotation(smoothPivot + armDir * armLen, curRot);
    }

    // Add an upward recoil kick (deg), accumulated up to recoilMaxDeg. LocalPlayer routes PlayerCombat.OnRecoil
    // here on each discharged shot; the kick eases back to 0 in LateUpdate so aim recovers between shots.
    public void AddRecoilPitch(float deg)
    {
        if (deg <= 0f) return;
        recoilPitch = Mathf.Min(recoilMaxDeg, recoilPitch + deg);
    }

    // Exponential approach factor for a given time-constant (seconds). Smaller tau = snappier.
    static float Step(float tau) => 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, tau));

    void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void OnDisable() { LockCursor(false); }
}
