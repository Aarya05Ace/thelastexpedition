// NpcAgent.cs — per-NPC reactor (Lost Expedition).
//
// Reads the NPC's replicated row. When the LLM director writes a new response, the row's Seq
// increments — that's the edge trigger: we show the dialogue, reflect the animation_trigger, and
// (later) drive a NavMeshAgent from game_action + speak via TTS.
//
// Placeholders for now: color encodes the animation_trigger; an on-screen label shows the name,
// trust/sanity, and the latest line. Real humanoid model + Animator + NavMesh + TTS are next.

using UnityEngine;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class NpcAgent : MonoBehaviour
{
    public ulong NpcId { get; private set; }
    public string DisplayName { get; private set; } = "?";
    public int Trust { get; private set; }
    public int Sanity { get; private set; }
    public string LastDialogue { get; private set; } = "";
    public string AnimTrigger { get; private set; } = "Idle";
    public string GameActionStr { get; private set; } = "STAY_PUT";

    bool primed;
    uint lastSeq;
    float dialogueUntil;
    Renderer rend;

    public void Init(ulong id) { NpcId = id; rend = GetComponentInChildren<Renderer>(); }

    public void Apply(Npc n)
    {
        DisplayName = n.DisplayName;
        Trust = n.Trust;
        Sanity = n.Sanity;

        // First time we see this row (spawn/backfill): set the baseline, don't fire a reaction.
        if (!primed) { primed = true; lastSeq = n.Seq; ApplyAnim(n.AnimationTrigger); return; }

        // The director bumped Seq -> a fresh LLM response arrived.
        if (n.Seq > lastSeq) { lastSeq = n.Seq; React(n); }
    }

    void React(Npc n)
    {
        LastDialogue = string.IsNullOrEmpty(n.Dialogue) ? "" : n.Dialogue;
        AnimTrigger = string.IsNullOrEmpty(n.AnimationTrigger) ? "Idle" : n.AnimationTrigger;
        GameActionStr = string.IsNullOrEmpty(n.GameAction) ? "STAY_PUT" : n.GameAction;
        dialogueUntil = Time.time + 6f;
        ApplyAnim(AnimTrigger);
        Debug.Log($"[NPC {DisplayName}] \"{LastDialogue}\" [{AnimTrigger}/{GameActionStr}] trust={Trust} sanity={Sanity}");
        // TODO ④: TTS playback of LastDialogue (POST to the Node media sidecar -> AudioSource).
        // TODO ②/④: animator.SetTrigger(AnimTrigger); NavMeshAgent.destination from GameActionStr + TargetPlayer.
    }

    void ApplyAnim(string trigger)
    {
        if (rend == null) rend = GetComponentInChildren<Renderer>();
        if (rend == null) return;
        Color c = trigger switch
        {
            "Cower"    => new Color(0.35f, 0.5f, 1f),
            "Threaten" => new Color(1f, 0.3f, 0.25f),
            "Panic"    => new Color(1f, 0.85f, 0.2f),
            "Nod"      => new Color(0.4f, 0.9f, 0.5f),
            _          => new Color(0.72f, 0.72f, 0.75f),
        };
        var mat = rend.material;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
    }

    void OnGUI()
    {
        var cam = LocalPlayer.ActiveCamera != null ? LocalPlayer.ActiveCamera : Camera.main;
        if (cam == null) return;
        Vector3 sp = cam.WorldToScreenPoint(transform.position + Vector3.up * 2.4f);
        if (sp.z <= 0f) return; // behind the camera
        float y = Screen.height - sp.y;
        DrawCentered(sp.x, y, $"{DisplayName}    trust {Trust}   sanity {Sanity}", 14, Color.white);
        if (Time.time < dialogueUntil && LastDialogue.Length > 0)
            DrawCentered(sp.x, y + 22, $"“{LastDialogue}”", 13, new Color(1f, 0.9f, 0.72f));
    }

    static void DrawCentered(float cx, float top, string text, int size, Color col)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.UpperCenter, wordWrap = true };
        const float w = 380f, h = 96f;
        float x = cx - w / 2f;
        style.normal.textColor = Color.black;
        GUI.Label(new Rect(x + 1, top + 1, w, h), text, style); // shadow
        style.normal.textColor = col;
        GUI.Label(new Rect(x, top, w, h), text, style);
    }
}
