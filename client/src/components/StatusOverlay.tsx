/**
 * StatusOverlay.tsx — DOM feedback for what the Tomb Keeper is doing TO YOU.
 *
 * The server already syncs every status column on your player row + your gate's
 * seal; this surfaces them so the Keeper's moves are legible instead of silent:
 *   - a row of status badges (with live countdowns) under the collapse timer
 *   - a full-screen edge-tint that pulses the color of your strongest active effect
 *
 * Pure DOM, pointer-events: none — sits above the canvas, never eats input.
 */
import type { PlayerData } from '../generated/types';

interface StatusOverlayProps {
  player: PlayerData | null;
  nowTick: number;
  gateSealedUntil: number | null;
}

interface Badge {
  key: string;
  icon: string;
  label: string;
  secs: number | null; // null = no countdown
  color: string;
  bg: string;
}

export function StatusOverlay({ player, nowTick, gateSealedUntil }: StatusOverlayProps) {
  if (!player) return null;

  const left = (until: number) => Math.max(0, until - nowTick);
  const cursed = left(Number(player.cursedUntilTick));
  const blessed = left(Number(player.blessedUntilTick));
  const stunned = left(Number(player.stunnedUntilTick));
  const sealed = gateSealedUntil != null ? left(gateSealedUntil) : 0;
  const carrying = player.carryingRelicId !== undefined && player.carryingRelicId !== null;

  const badges: Badge[] = [];
  if (stunned > 0) badges.push({ key: 'stun', icon: '💫', label: 'STUNNED', secs: null, color: '#ffd9d9', bg: 'rgba(120,20,20,0.6)' });
  if (cursed > 0) badges.push({ key: 'curse', icon: '🔮', label: 'CURSED', secs: cursed, color: '#ecccff', bg: 'rgba(70,20,110,0.6)' });
  if (sealed > 0) badges.push({ key: 'seal', icon: '⛔', label: 'GATE SEALED', secs: sealed, color: '#ffd0d0', bg: 'rgba(110,20,20,0.6)' });
  if (blessed > 0) badges.push({ key: 'bless', icon: '✨', label: 'BLESSED', secs: blessed, color: '#fff2c2', bg: 'rgba(110,80,20,0.55)' });
  if (carrying) badges.push({ key: 'carry', icon: '💎', label: 'CARRYING', secs: null, color: '#fff2c2', bg: 'rgba(120,90,20,0.5)' });

  // Screen tint: strongest *negative* effect wins; carrying alone gets no tint.
  let tint: { color: string; strong: boolean } | null = null;
  if (stunned > 0) tint = { color: '255,70,70', strong: true };
  else if (cursed > 0) tint = { color: '150,60,255', strong: false };
  else if (sealed > 0) tint = { color: '255,60,60', strong: false };
  else if (blessed > 0) tint = { color: '255,200,90', strong: false };

  return (
    <>
      {tint && (
        <div
          aria-hidden
          style={{
            position: 'absolute',
            inset: 0,
            zIndex: 18,
            pointerEvents: 'none',
            boxShadow: `inset 0 0 ${tint.strong ? 180 : 130}px ${tint.strong ? 60 : 30}px rgba(${tint.color},${tint.strong ? 0.55 : 0.32})`,
            animation: `statusTint ${tint.strong ? 0.7 : 2.2}s ease-in-out infinite`,
          }}
        />
      )}

      {badges.length > 0 && (
        <div
          style={{
            position: 'absolute',
            top: 92,
            left: '50%',
            transform: 'translateX(-50%)',
            zIndex: 22,
            pointerEvents: 'none',
            display: 'flex',
            gap: 8,
            flexWrap: 'wrap',
            justifyContent: 'center',
            maxWidth: '80vw',
          }}
        >
          {badges.map(b => (
            <div
              key={b.key}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 7,
                padding: '5px 12px',
                borderRadius: 999,
                background: b.bg,
                border: `1px solid ${b.color}55`,
                color: b.color,
                fontFamily: "'Cinzel', Georgia, serif",
                fontSize: 14,
                fontWeight: 700,
                letterSpacing: 1,
                textShadow: '0 1px 4px rgba(0,0,0,0.8)',
                boxShadow: `0 0 16px rgba(0,0,0,0.5)`,
                animation: b.key === 'stun' ? 'statusBadgePulse 0.5s ease-in-out infinite' : undefined,
              }}
            >
              <span style={{ fontSize: 16 }}>{b.icon}</span>
              <span>{b.label}</span>
              {b.secs != null && <span style={{ opacity: 0.8, fontWeight: 400 }}>{b.secs}s</span>}
            </div>
          ))}
        </div>
      )}

      <style>{`
        @keyframes statusTint { 0%,100%{opacity:0.55} 50%{opacity:1} }
        @keyframes statusBadgePulse { 0%,100%{transform:scale(1)} 50%{transform:scale(1.08)} }
      `}</style>
    </>
  );
}
