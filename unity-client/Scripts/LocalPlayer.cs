// LocalPlayer.cs — drives the local rescuer with a CharacterController (REAL collision + gravity
// against the forest), a third-person camera, and text interrogation of the nearest NPC.
//
// Movement is CLIENT-AUTHORITATIVE: we move the CharacterController locally (so it collides with
// boulders/trees/terrain and is held on the ground by gravity), then send the resulting position
// to the server (update_player_input now trusts client_pos). Added at runtime by NetworkedWorld.

using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;
using NetVec = SpacetimeDB.Types.Vector3;

public class LocalPlayer : MonoBehaviour
{
    public Identity identity;

    [Header("Movement")]
    public float moveSpeed = 3.5f;      // walk ~3.5 m/s (was 5; -30% per request)
    public float sprintMult = 1.16f;    // sprint ~4.06 m/s (was 6.25; -35% per request)
    public float gravity = -30f;        // snappier, less-floaty fall
    public float jumpForce = 9f;        // ~1.35 m apex — clears rocks without the floaty hang
    public float rotationRate = 800f;   // Fortnite-style fast turn toward movement direction (deg/s)

    [Header("Interrogation")]
    public float talkRange = 6f;

    // The camera that actually renders — NpcAgent reads this for its world-space labels.
    public static Camera ActiveCamera;

    CharacterController cc;
    Camera cam;
    ExpeditionCamera orbit;
    Animator anim;
    PlayerCombat combat;   // owns Health (100 HP, Survivalist) + equipped AK + death handling
    float vY;
    float groundLockUntil;   // brief window after a jump where isGrounded is ignored (no phantom re-ground/re-jump)
    uint seq;
    float netTimer, scanTimer;
    Vector3 lastHitNormal = Vector3.up;   // surface normal of the last CharacterController contact (for slope projection)

    NpcAgent nearest;
    bool talking;
    string draft = "";

    void Start()
    {
        var camGo = new GameObject("ExpeditionCamera");
        cam = camGo.AddComponent<Camera>();
        cam.depth = 100;            // render on top of the Book of the Dead camera
        cam.tag = "MainCamera";
        orbit = camGo.AddComponent<ExpeditionCamera>();   // Fortnite-style orbit rig
        orbit.target = transform;
        ActiveCamera = cam;

        // Ensure exactly ONE AudioListener — on THIS gameplay camera — so NPC voice (TTS) and all audio are
        // heard. The Book of the Dead scene ships its own listener; leaving two makes Unity disable one at
        // random and can silence the NPC replies. Disable any others, then put the listener on our camera.
        foreach (var al in FindObjectsByType<AudioListener>(FindObjectsSortMode.None)) al.enabled = false;
        camGo.AddComponent<AudioListener>();

        // CharacterRig already added + BODY-FITTED the CharacterController before LocalPlayer was added,
        // so keep its fitted dimensions (don't overwrite them). Only add a sane default if it's missing.
        cc = GetComponent<CharacterController>();
        if (cc == null)
        {
            cc = gameObject.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, 1f, 0f);
            cc.height = 2f;
            cc.radius = 0.3f;
            cc.slopeLimit = 85f;        // near-vertical climbable: unrestricted traversal over rocks/banks
            cc.stepOffset = 0.6f;       // step over low rock lips/ledges (kept < height + 2*radius)
        }

        // Realistic traversal: don't let the controller climb/stick to near-vertical faces or high lips —
        // that's what let the player stand on steep "invisible-feeling" spots. Moderate slope + a low step.
        if (cc != null) { cc.slopeLimit = 55f; cc.stepOffset = 0.4f; }

        anim = GetComponent<Animator>();   // CharacterRig added it + wired the locomotion controller

        // CLIENT-SIDE COMBAT: the local player is a Survivalist (ally). PlayerCombat.Awake adds/configures
        // Health (100 GREEN + 50 BLUE shield, Survivalist faction) on this root and wires the death handler;
        // it ensures a PlayerLoadout (3 slots, owns the AK model). The player SPAWNS HOLSTERED — pressing 1
        // arms the AK. Added last so it sees the finished CharacterRig (collider + humanoid Animator).
        combat = gameObject.GetComponent<PlayerCombat>();
        if (combat == null) combat = gameObject.AddComponent<PlayerCombat>();
    }

    void OnDestroy()
    {
        if (ActiveCamera == cam) ActiveCamera = null;
        if (cam != null) Destroy(cam.gameObject);
    }

    // Capture the surface we last touched so Update can re-project movement along slopes/rocks
    // (keeps horizontal speed on contact instead of stalling against uneven terrain).
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        lastHitNormal = hit.normal;
    }

    void Update()
    {
        if (GameManager.Conn == null || cc == null) return;

        // nearest NPC (~5Hz)
        scanTimer += Time.deltaTime;
        if (scanTimer >= 0.2f) { scanTimer = 0f; ScanNearest(); }

        // open / close the interrogation prompt
        if (!talking && nearest != null && Input.GetKeyDown(KeyCode.E)) { talking = true; draft = ""; }
        if (talking && Input.GetKeyDown(KeyCode.Escape)) talking = false;

        // ---- camera-relative movement via the CharacterController (suppressed while typing or DEAD) ----
        // A dead player keeps gravity (settles the corpse) but takes no movement/look/jump/aim/fire input.
        bool dead = combat != null && combat.InputDisabled;
        bool typing = talking || dead;
        if (orbit != null) orbit.LookEnabled = !typing;   // pause mouse-look + free the cursor while typing/dead

        Vector3 fwd = orbit != null ? orbit.PlanarForward : transform.forward;
        Vector3 right = orbit != null ? orbit.PlanarRight : transform.right;
        fwd.y = 0f; fwd.Normalize(); right.y = 0f; right.Normalize();
        Vector3 wish = Vector3.zero;
        if (!typing)
        {
            if (Input.GetKey(KeyCode.W)) wish += fwd;
            if (Input.GetKey(KeyCode.S)) wish -= fwd;
            if (Input.GetKey(KeyCode.D)) wish += right;
            if (Input.GetKey(KeyCode.A)) wish -= right;
        }
        wish = Vector3.ClampMagnitude(wish, 1f);
        bool moving = wish.sqrMagnitude > 0.01f;
        bool sprint = !typing && moving && Input.GetKey(KeyCode.LeftShift);
        if (orbit != null) orbit.Sprinting = sprint;       // drives the sprint FOV + distance push
        float speed = moveSpeed * (sprint ? sprintMult : 1f);

        // ---- inventory slot select (1/2/3) — suppressed while typing or dead ----
        var loadout = combat != null ? combat.Loadout : null;
        if (!typing && loadout != null)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) loadout.Select(1);   // AK-47
            if (Input.GetKeyDown(KeyCode.Alpha2)) loadout.Select(2);   // Apple
            if (Input.GetKeyDown(KeyCode.Alpha3)) loadout.Select(3);   // empty / reserved
        }

        // ---- ADS (right mouse) + FIRE / EAT (left mouse) — suppressed while typing or dead ----
        // The player spawns HOLSTERED; the rifle is only held (Armed) while slot 1 is selected. ADS only makes
        // sense with the AK out, so gate it on AkOut. Left click branches on what's in hand: with the apple out
        // (slot 2) a single click eats it (Heal 30); otherwise a held click fires (no-ops unless AkOut). Fire
        // from the CAMERA-CENTER ray (origin = camera position, dir = camera forward) so the bullet lands on
        // the crosshair; the Weapon's fire-rate cooldown gates the actual discharge.
        bool akOut = loadout != null && loadout.AkOut;
        bool aiming = !typing && akOut && Input.GetMouseButton(1);
        if (orbit != null) orbit.Aiming = aiming;          // ExpeditionCamera 65 FOV / 3m ADS blend
        if (!typing && combat != null)
        {
            if (loadout != null && loadout.Selected == 2 && Input.GetMouseButtonDown(0))
                loadout.UseSelected();                     // eat apple -> Heal(30), one bite per click
            else if (cam != null && Input.GetMouseButton(0))
                combat.TryFire(cam.transform.position, cam.transform.forward);  // no-ops unless AkOut
        }

        // Grounded only when the controller truly rests on ground AND not inside the brief post-jump lock,
        // so an isGrounded flicker right after launch can't re-ground / re-jump on a phantom surface.
        bool grounded = cc.isGrounded && Time.time >= groundLockUntil;
        if (grounded && vY < 0f) vY = -2f;   // stick to the ground
        if (!typing && grounded && Input.GetKeyDown(KeyCode.Space))
        {
            vY = jumpForce;                  // spacebar launch
            groundLockUntil = Time.time + 0.2f;
            if (anim != null && anim.runtimeAnimatorController != null) anim.SetTrigger("Jump");
        }
        vY += gravity * Time.deltaTime;

        // Horizontal move, re-projected onto the ground slope so the player keeps full speed up/down
        // uneven terrain instead of bleeding velocity into the surface. lastHitNormal is captured in
        // OnControllerColliderHit; using it to slide along rocks/walls prevents sticking on contact.
        Vector3 horiz = wish * speed;
        if (grounded && lastHitNormal.y > 0.01f && lastHitNormal.y < 0.999f && horiz.sqrMagnitude > 0.0001f)
        {
            Vector3 projected = Vector3.ProjectOnPlane(horiz, lastHitNormal);
            // Preserve the intended ground speed after re-projection (projection shortens the vector).
            if (projected.sqrMagnitude > 1e-6f) horiz = projected.normalized * speed;
        }

        Vector3 motion = horiz;
        motion.y = vY;
        cc.Move(motion * Time.deltaTime);          // <- real collision happens here

        // Character snaps toward the movement direction fast (Fortnite rotation rate ~800 deg/s).
        if (moving)
        {
            Quaternion look = Quaternion.LookRotation(new Vector3(wish.x, 0f, wish.z));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, rotationRate * Time.deltaTime);
        }

        // Feed the camera the character's facing so it can swing behind while moving (after the rotation above,
        // so FollowTargetYaw reflects the freshly-updated facing). Only follow while actually moving, not typing/dead.
        if (orbit != null)
        {
            orbit.AutoFollow      = moving && !typing;
            orbit.FollowTargetYaw = transform.eulerAngles.y;
        }

        // ---- send our collided position to the server (~20Hz; client-authoritative) ----
        netTimer += Time.deltaTime;
        if (netTimer >= 0.05f) { netTimer = 0f; SendPosition(moving, sprint); }

        // Camera framing (orbit, shoulder offset, lag, collision, sprint/ADS FOV) is owned by
        // ExpeditionCamera in its LateUpdate — nothing more to do here.
    }

    void SendPosition(bool moving, bool sprint)
    {
        seq++;
        var sp = transform.position - NetworkedWorld.WorldOffset; // world -> server-relative
        var input = new InputState(moving, false, false, false, sprint, false, false, false, seq);
        var pos = new NetVec(sp.x, sp.y, sp.z);
        var rot = new NetVec(0f, transform.eulerAngles.y * Mathf.Deg2Rad, 0f);
        string anim = moving ? (sprint ? "run-forward" : "walk-forward") : "idle";
        GameManager.Conn.Reducers.UpdatePlayerInput(input, pos, rot, anim);
    }

    void ScanNearest()
    {
        NpcAgent best = null; float bestD = talkRange * talkRange;
        foreach (var a in FindObjectsByType<NpcAgent>(FindObjectsSortMode.None))
        {
            float d = (a.transform.position - transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = a; }
        }
        nearest = best;
        if (nearest == null) talking = false;
    }

    void AskNearest()
    {
        if (nearest == null || GameManager.Conn == null) { talking = false; return; }
        string text = draft.Trim();
        talking = false;
        if (text.Length == 0) return;
        float dist = Vector3.Distance(transform.position, nearest.transform.position);
        // isPlayerArmed / flashlightInFace stay false until the weapon + flashlight (milestone ⑤).
        GameManager.Conn.Reducers.AskNpc(nearest.NpcId, text, dist, false, false);
        Debug.Log($"[you -> {nearest.DisplayName}] {text}");
    }

    void OnGUI()
    {
        if (nearest == null) return;

        if (!talking)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            s.normal.textColor = Color.white;
            GUI.Label(new Rect(Screen.width / 2f - 220, Screen.height - 74, 440, 24), $"[E] interrogate {nearest.DisplayName}", s);
            return;
        }

        float w = 560f, x = Screen.width / 2f - w / 2f, y = Screen.height - 60f;
        GUI.Box(new Rect(x - 8, y - 30, w + 16, 70), GUIContent.none);
        var lbl = new GUIStyle(GUI.skin.label) { fontSize = 14 }; lbl.normal.textColor = Color.white;
        GUI.Label(new Rect(x, y - 24, w, 20), $"Say to {nearest.DisplayName}   (Enter = send, Esc = cancel)", lbl);

        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { AskNearest(); e.Use(); return; }

        GUI.SetNextControlName("askField");
        draft = GUI.TextField(new Rect(x, y, w, 28), draft, 200);
        GUI.FocusControl("askField");
    }
}
