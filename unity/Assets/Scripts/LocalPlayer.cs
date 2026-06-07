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
    public float moveSpeed = 6f;
    public float sprintMult = 1.7f;
    public float gravity = -24f;
    public float turnSpeed = 12f;

    [Header("Camera")]
    public Vector3 camOffset = new Vector3(0f, 4.5f, 8f);
    public float camLerp = 8f;

    [Header("Interrogation")]
    public float talkRange = 6f;

    // The camera that actually renders — NpcAgent reads this for its world-space labels.
    public static Camera ActiveCamera;

    CharacterController cc;
    Camera cam;
    float vY;
    uint seq;
    float netTimer, scanTimer;

    NpcAgent nearest;
    bool talking;
    string draft = "";

    void Start()
    {
        cam = new GameObject("ExpeditionCamera").AddComponent<Camera>();
        cam.depth = 100;            // render on top of the Book of the Dead camera
        cam.tag = "MainCamera";
        ActiveCamera = cam;

        // Real collision capsule. Center (0,0,0) matches the placeholder capsule's centered pivot
        // so the visual stands ON the ground (feet at ground, not half-buried).
        cc = GetComponent<CharacterController>();
        if (cc == null) cc = gameObject.AddComponent<CharacterController>();
        cc.center = Vector3.zero;
        cc.height = 2f;
        cc.radius = 0.45f;
        cc.slopeLimit = 55f;
        cc.stepOffset = 0.4f;
    }

    void OnDestroy() { if (ActiveCamera == cam) ActiveCamera = null; }

    void Update()
    {
        if (GameManager.Conn == null || cc == null) return;

        // nearest NPC (~5Hz)
        scanTimer += Time.deltaTime;
        if (scanTimer >= 0.2f) { scanTimer = 0f; ScanNearest(); }

        // open / close the interrogation prompt
        if (!talking && nearest != null && Input.GetKeyDown(KeyCode.E)) { talking = true; draft = ""; }
        if (talking && Input.GetKeyDown(KeyCode.Escape)) talking = false;

        // ---- camera-relative movement via the CharacterController (suppressed while typing) ----
        bool typing = talking;
        Vector3 fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 right = cam.transform.right; right.y = 0f; right.Normalize();
        Vector3 wish = Vector3.zero;
        if (!typing)
        {
            if (Input.GetKey(KeyCode.W)) wish += fwd;
            if (Input.GetKey(KeyCode.S)) wish -= fwd;
            if (Input.GetKey(KeyCode.D)) wish += right;
            if (Input.GetKey(KeyCode.A)) wish -= right;
        }
        wish = Vector3.ClampMagnitude(wish, 1f);
        bool sprint = !typing && Input.GetKey(KeyCode.LeftShift);
        float speed = moveSpeed * (sprint ? sprintMult : 1f);

        if (cc.isGrounded && vY < 0f) vY = -2f;   // stick to the ground
        vY += gravity * Time.deltaTime;

        Vector3 motion = wish * speed;
        motion.y = vY;
        cc.Move(motion * Time.deltaTime);          // <- real collision happens here

        bool moving = wish.sqrMagnitude > 0.01f;
        if (moving)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(new Vector3(wish.x, 0f, wish.z)), Time.deltaTime * turnSpeed);

        // ---- send our collided position to the server (~20Hz; client-authoritative) ----
        netTimer += Time.deltaTime;
        if (netTimer >= 0.05f) { netTimer = 0f; SendPosition(moving, sprint); }

        // ---- camera follow ----
        var desired = transform.position + camOffset;
        cam.transform.position = Vector3.Lerp(cam.transform.position, desired, Time.deltaTime * camLerp);
        cam.transform.LookAt(transform.position + Vector3.up * 1.5f);
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
