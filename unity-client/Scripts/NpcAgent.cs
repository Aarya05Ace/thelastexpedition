// NpcAgent.cs — per-NPC reactor (Lost Expedition).
//
// Reads the NPC's replicated row. When the LLM director writes a new response, the row's Seq
// increments — that's the edge trigger: we show the dialogue, reflect the animation_trigger, and
// (later) drive a NavMeshAgent from game_action + speak via TTS.
//
// Color encodes the animation_trigger; the new line is spoken via TTS (Speak). The always-on floating
// name/trust/sanity label was removed; the player gets a "[V] Talk    [E] Interrogate" proximity prompt
// from LocalPlayer instead. Real humanoid model + Animator + NavMesh are wired elsewhere.

using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
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

    // Per-NPC voice gender ("female"/"male"), sent to /tts so the sidecar picks a matching ElevenLabs
    // voice. Derived from the assigned model key at spawn (see NetworkedWorld.SpawnNpc). Defaults to
    // "male"; a missing/unknown value on the server side falls back to the default voice.
    public string Gender { get; private set; } = "male";

    bool primed;
    uint lastSeq;
    Renderer rend;
    AudioSource voice;

    // gender: "female"/"male" for per-NPC TTS voice selection. Defaults to "male" so existing callers
    // that don't pass it (and the sidecar fallback) keep working. Null-safe: a null/empty value is
    // coerced to "male".
    public void Init(ulong id, string gender = "male")
    {
        NpcId = id;
        Gender = string.IsNullOrEmpty(gender) ? "male" : gender;
        rend = GetComponentInChildren<Renderer>();
        AssignIdleController();   // chat NPCs breathe with Idle_Stance_02 instead of the combat/loco rig
    }

    // CharacterRig has already added an Animator + the shared Locomotion controller (and a
    // CharacterLocomotion driver) before NpcAgent.Init runs, so we REASSIGN here. The breathing idle
    // controller is loaded from Resources (works in BOTH editor + player builds), with the editor-only
    // AssetDatabase path kept ONLY as a secondary fallback.
    // Null-safe: if the idle controller is missing the NPC just keeps the existing Locomotion controller
    // (graceful fallback — no crash, no T-pose).
    void AssignIdleController()
    {
        var anim = GetComponent<Animator>();
        if (anim == null) { Debug.LogWarning($"[NpcAgent {NpcId}] idle: no Animator on NPC"); return; }

        // PRIMARY: Resources copy of the built NpcIdle controller (works in editor AND builds).
        RuntimeAnimatorController ctrl = Resources.Load<RuntimeAnimatorController>("Controllers/NpcIdle");
#if UNITY_EDITOR
        // FALLBACK (editor only): load the freshly-built controller straight from the source path.
        if (ctrl == null)
            ctrl = UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/TombRush/NpcIdle.controller");
#endif
        if (ctrl == null) { Debug.LogWarning($"[NpcAgent {NpcId}] idle: NpcIdle.controller NOT FOUND in Resources/Controllers/NpcIdle (or at Assets/TombRush/NpcIdle.controller) — keeping Locomotion idle"); return; }

        // A Humanoid avatar is REQUIRED for the retargeted Idle_Stance_02 clip to bind to the bones —
        // without it the model just T-poses. CharacterRig already assigns one; bail (keep Locomotion)
        // rather than swap in an idle that can't bind.
        if (anim.avatar == null || !anim.avatar.isHuman) { Debug.LogWarning($"[NpcAgent {NpcId}] idle: avatar missing/non-human (avatar={anim.avatar != null}, human={(anim.avatar != null && anim.avatar.isHuman)}) — keeping Locomotion"); return; }

        // The NpcIdle controller has NO parameters; the CharacterLocomotion driver that CharacterRig
        // added would keep calling SetFloat("Speed", ...) on it forever (harmless but pointless, and it
        // exists only to drive the locomotion blend tree we're replacing). Remove it so the breathing
        // idle is the sole thing driving this Animator.
        var loco = GetComponent<CharacterLocomotion>();
        if (loco != null) Destroy(loco);

        anim.applyRootMotion = false;
        anim.runtimeAnimatorController = ctrl;

        // Rebind re-binds the skeleton to the swapped controller (and clears cached param lookups);
        // Update(0) forces the Animator to evaluate the new default state's first frame THIS frame, so
        // the breathing idle is applied to the pose immediately instead of leaving a one-frame T-pose
        // (or no pose at all if the Animator hadn't completed its initial bind on this fresh instance).
        anim.Rebind();
        anim.Update(0f);
        Debug.Log($"[NpcAgent {NpcId}] idle: NpcIdle.controller (Idle_Stance_02 breathing) assigned ✓");
    }

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
        ApplyAnim(AnimTrigger);
        Debug.Log($"[NPC {DisplayName}] \"{LastDialogue}\" [{AnimTrigger}/{GameActionStr}] trust={Trust} sanity={Sanity}");
        // ④: TTS playback of LastDialogue (POST to the Node media sidecar -> AudioSource on this NPC).
        // The floating subtitle is gone, so TTS is now the primary way the reply is surfaced to the player.
        if (!string.IsNullOrEmpty(LastDialogue)) Speak(LastDialogue);
        // TODO ②: animator.SetTrigger(AnimTrigger); NavMeshAgent.destination from GameActionStr + TargetPlayer.
    }

    // ④ TTS: POST {"text":...} to the media sidecar /tts and play the returned MPEG on a 3D AudioSource
    // mounted on this NPC, so the voice attenuates with distance. Best-effort; mirrors TtsPlayer's POST
    // pattern (the only differences: spatialBlend = 1 and the source lives on the NPC transform).
    const string TTS_URL = "http://localhost:8787/tts";
    const int TTS_TIMEOUT = 8;

    void Speak(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (voice == null)
        {
            voice = gameObject.AddComponent<AudioSource>();
            voice.spatialBlend = 0f;   // 2D — ALWAYS audible (3D attenuation/listener was likely the silence)
            voice.volume = 1f;
            voice.playOnAwake = false;
        }
        StopCoroutine(nameof(SpeakRoutine));   // a fresh line supersedes any in-flight request
        StartCoroutine(SpeakRoutine(text));
    }

    IEnumerator SpeakRoutine(string text)
    {
        // Gender is a fixed "female"/"male" literal set at Init, so it needs no JSON escaping.
        string json = "{\"text\":\"" + EscapeJson(text) + "\",\"gender\":\"" + Gender + "\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        UnityWebRequest req = null;
        try
        {
            req = new UnityWebRequest(TTS_URL, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerAudioClip(TTS_URL, AudioType.MPEG),
                timeout = TTS_TIMEOUT,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[NPC TTS] build failed (voice skipped): {e.Message}");
            req?.Dispose();
            yield break;
        }

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            AudioClip clip = null;
            try { clip = DownloadHandlerAudioClip.GetContent(req); }
            catch (System.Exception e) { Debug.LogWarning($"[NPC TTS] decode failed (voice skipped): {e.Message}"); }
            if (clip != null && clip.loadState == AudioDataLoadState.Loaded)
            {
                voice.clip = clip;
                voice.Play();
                Debug.Log($"[NPC TTS] {DisplayName} speaking ({clip.length:F1}s)");
            }
            else
            {
                Debug.LogWarning($"[NPC TTS] {DisplayName}: clip null/unloaded (state={(clip != null ? clip.loadState.ToString() : "null")})");
            }
        }
        else
        {
            // Sidecar down / timeout / HTTP error: stay silent. The subtitle already shows.
            Debug.LogWarning($"[NPC TTS] {req.result}: {req.error} (voice skipped; subtitle still shows)");
        }

        req.Dispose();
    }

    // Minimal JSON string escaping (copy of TtsPlayer.EscapeJson) — LLM dialogue may contain
    // quotes, backslashes, and newlines.
    static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
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

    // Ask 5: the always-on floating name/trust/sanity label (and the per-line subtitle) are REMOVED. The
    // player now sees a clean "[V] Talk    [E] Interrogate" proximity prompt drawn by LocalPlayer.OnGUI when
    // near the nearest NPC. DisplayName/Trust/Sanity remain as public state (LocalPlayer reads DisplayName);
    // dialogueUntil/LastDialogue are still set in React (and consumed by Speak/TTS), just no longer rendered
    // here. No OnGUI in this class anymore.
}
