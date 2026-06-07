// TtsPlayer.cs — THE LOST EXPEDITION lobby: the Patron's voice (best-effort).
//
// Speak(text) POSTs {"text":"..."} to the Node sidecar at http://localhost:8787/tts and plays the
// returned audio/mpeg. Voice is ALWAYS best-effort: PatronPresenter shows the speech bubble FIRST,
// then calls Speak(), so a TTS failure (sidecar down, slow, garbage) never blocks the subtitle.
//
// CRITICAL Unity-6 details (verified against HDRP 17.4.0 / Unity 6000.4):
//   * A custom-verb POST UnityWebRequest with downloadHandler = DownloadHandlerAudioClip(uri, MPEG).
//     UnityWebRequestMultimedia.GetAudioClip is GET-only and cannot carry the JSON body.
//   * AudioType.MPEG (there is NO AudioType.MP3) matches ElevenLabs' audio/mpeg.
//   * streamAudio = false: mp3 is not reliably mid-stream-decodable; decode the full buffer.
//   * req.timeout so a hung sidecar (TCP accept, no response) cannot leak/stack coroutines.

using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class TtsPlayer : MonoBehaviour
{
    const string TTS_URL = "http://localhost:8787/tts";
    const int TIMEOUT_SECONDS = 8;

    AudioSource audioSource;

    void Awake()
    {
        // 2D source (spatialBlend = 0) so the Patron is always audible regardless of pedestal
        // distance. A scene AudioListener already exists via the lobby camera.
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;
    }

    public void Speak(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        StopAllCoroutines();          // a fresh line supersedes any in-flight request
        StartCoroutine(SpeakRoutine(text));
    }

    IEnumerator SpeakRoutine(string text)
    {
        string json = "{\"text\":\"" + EscapeJson(text) + "\"}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        UnityWebRequest req = null;
        try
        {
            req = new UnityWebRequest(TTS_URL, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerAudioClip(TTS_URL, AudioType.MPEG),
                timeout = TIMEOUT_SECONDS,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[TTS] request build failed (voice skipped): {e.Message}");
            req?.Dispose();
            yield break;
        }

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            AudioClip clip = null;
            try { clip = DownloadHandlerAudioClip.GetContent(req); }
            catch (System.Exception e) { Debug.LogWarning($"[TTS] decode failed (voice skipped): {e.Message}"); }

            if (clip != null && clip.loadState == AudioDataLoadState.Loaded)
            {
                audioSource.clip = clip;
                audioSource.Play();
            }
        }
        else
        {
            // Sidecar down / timeout / HTTP error: stay silent. The bubble already shows.
            Debug.LogWarning($"[TTS] {req.result}: {req.error} (voice skipped; bubble still shows)");
        }

        req.Dispose();
    }

    // Minimal JSON string escaping — Patron dialogue is LLM-generated and may contain quotes,
    // backslashes, and newlines.
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
}
