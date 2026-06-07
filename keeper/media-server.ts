// media-server.ts — voice I/O sidecar for The Lost Expedition.
//
//   POST /tts  { text, voiceId? }   -> ElevenLabs  -> audio/mpeg   (NPC / billionaire speaks)
//   POST /stt  (raw audio body; ?ext=wav|m4a|webm) -> OpenAI Whisper -> { text } (player speech)
//   GET  /health
//
// Keys come ONLY from env (keeper/.env): ELEVENLABS_API_KEY (+ ELEVENLABS_VOICE_ID), OPENAI_API_KEY.
// The Unity client hits these endpoints; the keys never ship in the game build.

import 'dotenv/config';
import http from 'node:http';

const PORT = Number(process.env.MEDIA_PORT || 8787);
const OPENAI_KEY = process.env.OPENAI_API_KEY || '';
const ELEVEN_KEY = process.env.ELEVENLABS_API_KEY || '';
const DEFAULT_VOICE = process.env.ELEVENLABS_VOICE_ID || '21m00Tcm4TlvDq8ikWAM';

function readBody(req: http.IncomingMessage): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', (c) => chunks.push(c as Buffer));
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Headers', '*');
  if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }

  try {
    const url = new URL(req.url || '/', `http://localhost:${PORT}`);

    if (req.method === 'GET' && url.pathname === '/health') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ ok: true, tts: !!ELEVEN_KEY, stt: !!OPENAI_KEY }));
      return;
    }

    // ---- TTS: text -> ElevenLabs -> mp3 ----
    if (req.method === 'POST' && url.pathname === '/tts') {
      const { text, voiceId } = JSON.parse((await readBody(req)).toString() || '{}');
      if (!text) { res.writeHead(400); res.end('missing text'); return; }
      const vid = voiceId || DEFAULT_VOICE;
      const r = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${vid}`, {
        method: 'POST',
        headers: { 'xi-api-key': ELEVEN_KEY, 'content-type': 'application/json', accept: 'audio/mpeg' },
        body: JSON.stringify({
          text,
          model_id: 'eleven_flash_v2_5',
          voice_settings: { stability: 0.4, similarity_boost: 0.8, style: 0.3 },
        }),
      });
      if (!r.ok) { res.writeHead(502, { 'content-type': 'text/plain' }); res.end(`elevenlabs ${r.status}: ${await r.text()}`); return; }
      const audio = Buffer.from(await r.arrayBuffer());
      res.writeHead(200, { 'content-type': 'audio/mpeg', 'content-length': audio.length });
      res.end(audio);
      return;
    }

    // ---- STT: audio -> OpenAI Whisper -> { text } ----
    if (req.method === 'POST' && url.pathname === '/stt') {
      const ext = (url.searchParams.get('ext') || 'webm').toLowerCase();
      const audio = await readBody(req);
      if (audio.length === 0) { res.writeHead(400); res.end('empty audio'); return; }
      const fd = new FormData();
      fd.append('file', new Blob([audio]), `audio.${ext}`);
      fd.append('model', 'gpt-4o-mini-transcribe');
      const r = await fetch('https://api.openai.com/v1/audio/transcriptions', {
        method: 'POST',
        headers: { Authorization: `Bearer ${OPENAI_KEY}` },
        body: fd,
      });
      const txt = await r.text();
      res.writeHead(r.ok ? 200 : 502, { 'content-type': 'application/json' });
      res.end(txt);
      return;
    }

    res.writeHead(404); res.end('not found');
  } catch (e: any) {
    res.writeHead(500, { 'content-type': 'text/plain' });
    res.end(String(e?.message || e));
  }
});

server.listen(PORT, () => console.log(`[media] STT(Whisper) + TTS(ElevenLabs) listening on :${PORT}`));
