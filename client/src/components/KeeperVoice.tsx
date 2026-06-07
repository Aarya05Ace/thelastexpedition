/**
 * KeeperVoice.tsx — the Tomb Keeper speaks.
 *
 * Subscribes (via props) to the singleton `keeper` row. Whenever its `seq`
 * changes, the new line is spoken aloud with the browser Web Speech API
 * (free, no key) in a slow, low, menacing voice, and shown as a subtitle.
 */
import { useEffect, useRef, useState } from 'react';
import type { Keeper } from '../generated/types';

interface KeeperVoiceProps {
  keeper: Keeper | null;
  muted?: boolean;
}

export function KeeperVoice({ keeper, muted = false }: KeeperVoiceProps) {
  const lastSeq = useRef<number>(-1);
  const [subtitle, setSubtitle] = useState<string>('');
  const [visible, setVisible] = useState(false);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (!keeper) return;
    if (keeper.seq === lastSeq.current) return;
    lastSeq.current = keeper.seq;

    const line = keeper.lastLine?.trim();
    if (!line) return;

    setSubtitle(line);
    setVisible(true);
    if (hideTimer.current) clearTimeout(hideTimer.current);
    hideTimer.current = setTimeout(() => setVisible(false), 6500);

    if (!muted && typeof window !== 'undefined' && window.speechSynthesis) {
      try {
        const u = new SpeechSynthesisUtterance(line);
        u.rate = 0.82;
        u.pitch = 0.4;
        u.volume = 1.0;
        // Prefer a deep/male voice if available.
        const voices = window.speechSynthesis.getVoices();
        const preferred = voices.find(v => /(Daniel|Google UK English Male|Microsoft David|male)/i.test(v.name));
        if (preferred) u.voice = preferred;
        window.speechSynthesis.cancel();
        window.speechSynthesis.speak(u);
      } catch {
        /* speech not available — subtitle still shows */
      }
    }
  }, [keeper, muted]);

  return (
    <div
      style={{
        position: 'absolute',
        bottom: '8%',
        left: '50%',
        transform: 'translateX(-50%)',
        maxWidth: '70vw',
        textAlign: 'center',
        pointerEvents: 'none',
        zIndex: 30,
        opacity: visible ? 1 : 0,
        transition: 'opacity 0.6s ease',
      }}
    >
      <div
        style={{
          display: 'inline-block',
          padding: '10px 22px',
          background: 'rgba(8,2,12,0.72)',
          border: '1px solid rgba(190,70,255,0.45)',
          borderRadius: 10,
          color: '#e9c9ff',
          fontFamily: "'Cinzel', Georgia, serif",
          fontSize: 'clamp(15px, 2.1vw, 24px)',
          letterSpacing: '0.5px',
          textShadow: '0 0 14px rgba(190,70,255,0.8)',
          boxShadow: '0 0 30px rgba(120,20,180,0.4)',
        }}
      >
        <span style={{ opacity: 0.6, fontSize: '0.7em', letterSpacing: '2px' }}>THE TOMB KEEPER</span>
        <br />
        “{subtitle}”
      </div>
    </div>
  );
}
