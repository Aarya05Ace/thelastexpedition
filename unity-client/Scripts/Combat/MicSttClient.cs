// MicSttClient.cs — push-to-talk microphone capture -> Whisper STT (Lost Expedition).
//
// Wraps UnityEngine.Microphone + a 16-bit PCM WAV encoder + a POST to the Node media sidecar's
// /stt endpoint (http://localhost:8787/stt?ext=wav -> OpenAI whisper-1). PlayerVoice drives this:
// Begin() on key-down, EndAndSend(onTranscript) on key-up. The callback always fires exactly once
// ("" on any failure) so the caller can never hang.
//
// CRITICAL details:
//   * Trim to Microphone.GetPosition() so we upload ~1-3s of real audio, not the full 10s buffer —
//     the dominant STT latency win on the Unity side.
//   * Use recClip.frequency (the device-granted rate), NOT the requested 16000 — some devices coerce
//     the rate, and the WAV header must match or Whisper mis-pitches the audio.
//   * Pure-runtime APIs only (Microphone + UnityWebRequest). No SpacetimeDB.Types -> no Vector3 alias.
//     Null-safe throughout; never throws.

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class MicSttClient : MonoBehaviour
{
    const string STT_URL = "http://localhost:8787/stt?ext=wav";
    const int SAMPLE_RATE = 16000;   // 16 kHz — Whisper-friendly, small upload
    const int MAX_SECONDS = 10;      // Microphone.Start lengthSec, loop=false
    const int TIMEOUT_SECONDS = 8;

    AudioClip recClip;
    string micDevice;                // null = default device (per contract)

    public bool IsRecording { get; private set; }

    public void Begin()
    {
        if (IsRecording) return;
        try
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0) return; // no mic -> no-op
            micDevice = null;                                                          // default device
            recClip = Microphone.Start(micDevice, false, MAX_SECONDS, SAMPLE_RATE);   // loop=false
            IsRecording = recClip != null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[STT] mic begin failed: {e.Message}");
            IsRecording = false;
        }
    }

    public void EndAndSend(Action<string> onTranscript)
    {
        if (!IsRecording || recClip == null)
        {
            IsRecording = false;
            onTranscript?.Invoke("");
            return;
        }

        int sampleCount = 0;
        try { sampleCount = Microphone.GetPosition(micDevice); } catch { }
        try { Microphone.End(micDevice); } catch { }
        IsRecording = false;

        if (sampleCount <= 0 || recClip == null) { onTranscript?.Invoke(""); return; }

        // Copy exactly the recorded samples (trim the silent tail of the 10s buffer).
        float[] samples = new float[sampleCount * recClip.channels];
        try { recClip.GetData(samples, 0); }
        catch (Exception e) { Debug.LogWarning($"[STT] GetData failed: {e.Message}"); onTranscript?.Invoke(""); return; }

        byte[] wav = EncodeWav(samples, recClip.channels, recClip.frequency);
        StartCoroutine(PostStt(wav, onTranscript));
    }

    // 16-bit PCM WAV: 44-byte canonical header + interleaved little-endian short samples.
    static byte[] EncodeWav(float[] samples, int channels, int hz)
    {
        int frames = samples.Length;       // total samples across all channels
        int byteRate = hz * channels * 2;
        int dataBytes = frames * 2;        // 16-bit -> 2 bytes/sample

        using var ms = new System.IO.MemoryStream(44 + dataBytes);
        using var w = new System.IO.BinaryWriter(ms);
        // RIFF
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);           // ChunkSize
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        // fmt
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                       // Subchunk1Size (PCM)
        w.Write((short)1);                 // AudioFormat = PCM
        w.Write((short)channels);
        w.Write(hz);
        w.Write(byteRate);
        w.Write((short)(channels * 2));    // BlockAlign
        w.Write((short)16);                // BitsPerSample
        // data
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            float f = Mathf.Clamp(samples[i], -1f, 1f);
            w.Write((short)Mathf.RoundToInt(f * 32767f));
        }
        w.Flush();
        return ms.ToArray();
    }

    IEnumerator PostStt(byte[] wav, Action<string> onTranscript)
    {
        UnityWebRequest req = null;
        try
        {
            req = new UnityWebRequest(STT_URL, "POST")
            {
                uploadHandler = new UploadHandlerRaw(wav),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = TIMEOUT_SECONDS,
            };
            req.SetRequestHeader("Content-Type", "audio/wav");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[STT] request build failed: {e.Message}");
            req?.Dispose();
            onTranscript?.Invoke("");
            yield break;
        }

        yield return req.SendWebRequest();

        string text = "";
        if (req.result == UnityWebRequest.Result.Success)
        {
            try { text = JsonUtility.FromJson<SttResp>(req.downloadHandler.text)?.text ?? ""; }
            catch (Exception e) { Debug.LogWarning($"[STT] parse failed: {e.Message}"); }
        }
        else
        {
            Debug.LogWarning($"[STT] {req.result}: {req.error}");
        }

        req.Dispose();
        onTranscript?.Invoke(text ?? "");
    }

    [Serializable] class SttResp { public string text; }
}
