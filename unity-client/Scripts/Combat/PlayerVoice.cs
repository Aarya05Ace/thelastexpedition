// PlayerVoice.cs — hold-to-talk voice channel to the nearest NPC (Lost Expedition).
//
// Self-bootstraps: a RuntimeInitializeOnLoadMethod spawns a tiny poller that waits for
// PlayerCombat.Local, then adds a PlayerVoice (and a MicSttClient sibling) to that player.
//
// Hold KeyCode.V to record (MicSttClient.Begin); release to stop + transcribe (EndAndSend). On a
// non-empty transcript, find the nearest NpcAgent within ~8m and call the reducer exactly as
// LocalPlayer.AskNearest does: AskNpc(npcId, transcript, dist, isArmed=false, flashlight=false).
//
// Imports SpacetimeDB.Types (for the AskNpc reducer) -> MUST alias Vector3 per the hard gotcha.
// Coexists with LocalPlayer's E-to-type path (V vs E; LocalPlayer's 'talking' gate doesn't block V).
// Null-safe: never throws even if GameManager.Conn is null (e.g. before connect / on a death screen).

using UnityEngine;
using SpacetimeDB.Types;
using Vector3 = UnityEngine.Vector3;

public class PlayerVoice : MonoBehaviour
{
    const float TALK_RANGE = 8f;

    MicSttClient stt;
    bool sending;

    // --- Self-bootstrap -----------------------------------------------------------------------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var host = new GameObject("[PlayerVoiceBootstrap]");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<PlayerVoiceBootstrap>();
    }

    void Awake()
    {
        stt = GetComponent<MicSttClient>();
        if (stt == null) stt = gameObject.AddComponent<MicSttClient>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.V) && !sending && stt != null && !stt.IsRecording)
            stt.Begin();

        if (Input.GetKeyUp(KeyCode.V) && stt != null && stt.IsRecording)
        {
            sending = true;
            stt.EndAndSend(OnTranscript);   // always calls back -> sending reset there
        }
    }

    void OnTranscript(string text)
    {
        sending = false;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (GameManager.Conn == null) return;

        // Cheap nearest scan (mirrors LocalPlayer.ScanNearest).
        NpcAgent best = null;
        float bestSq = TALK_RANGE * TALK_RANGE;
        foreach (var a in FindObjectsByType<NpcAgent>(FindObjectsSortMode.None))
        {
            float d = (a.transform.position - transform.position).sqrMagnitude;
            if (d < bestSq) { bestSq = d; best = a; }
        }
        if (best == null) return;

        float dist = Vector3.Distance(transform.position, best.transform.position);
        // Signature per contract + LocalPlayer.cs:222 — AskNpc(npcId, transcript, dist, isArmed, flashlight).
        GameManager.Conn.Reducers.AskNpc(best.NpcId, text, dist, false, false);
        Debug.Log($"[voice -> {best.DisplayName}] {text}");
    }

    // Professional hold-to-talk indicator: a dark pill, a pulsing red record dot, and bold letter-spaced
    // "LISTENING" with a drop shadow. No emoji. (OnGUI uses a 1x1 white texture tinted via GUI.color.)
    void OnGUI()
    {
        if (stt == null || !stt.IsRecording) return;

        const float w = 232f, h = 42f;
        float x = (Screen.width - w) * 0.5f, y = Screen.height - 124f;
        var tex = SolidTex();

        // dark pill backdrop
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x, y, w, h), tex);

        // pulsing red record dot
        float pulse = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5f));
        GUI.color = new Color(1f, 0.18f, 0.15f, pulse);
        const float ds = 12f;
        GUI.DrawTexture(new Rect(x + 24f, y + (h - ds) * 0.5f, ds, ds), tex);
        GUI.color = Color.white;

        // "LISTENING" — bold, uppercase, letter-spaced, with a soft drop shadow
        var style = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        const string label = "L I S T E N I N G";
        var r = new Rect(x + 46f, y, w - 52f, h);
        style.normal.textColor = new Color(0f, 0f, 0f, 0.65f);
        GUI.Label(new Rect(r.x + 1f, r.y + 1.5f, r.width, r.height), label, style);
        style.normal.textColor = new Color(0.96f, 0.96f, 0.97f, 0.96f);
        GUI.Label(r, label, style);
    }

    static Texture2D _solid;
    static Texture2D SolidTex()
    {
        if (_solid != null) return _solid;
        _solid = new Texture2D(1, 1);
        _solid.SetPixel(0, 0, Color.white);
        _solid.Apply();
        _solid.hideFlags = HideFlags.HideAndDontSave;
        return _solid;
    }
}

// Tiny poller: waits for PlayerCombat.Local, attaches PlayerVoice once, then self-destructs.
public class PlayerVoiceBootstrap : MonoBehaviour
{
    void Update()
    {
        var pc = PlayerCombat.Local;
        if (pc == null) return;
        if (pc.GetComponent<PlayerVoice>() == null)
            pc.gameObject.AddComponent<PlayerVoice>();
        Destroy(gameObject);
    }
}
