/**
 * JoinGameDialog.tsx — the "choose your grave robber" step, shown after the
 * HowToPlay title screen and before you spawn. Themed to match the tomb
 * (Cinzel + gold/violet on dark) so the cinematic flow isn't broken by a form.
 *
 * The two archetypes map to the underlying character models — the values passed
 * to register_player stay 'Wizard' / 'Paladin' so model + animation loading is
 * unchanged; only the presentation is reskinned.
 */
import React, { useState } from 'react';

interface JoinGameDialogProps {
  onJoin: (username: string, characterClass: string) => void;
}

const ROBBERS = [
  {
    value: 'Wizard',
    name: 'THE CURSEBINDER',
    icon: '✦',
    blurb: 'A robed relic-thief who slips through the dark.',
    accent: '#b58cff',
  },
  {
    value: 'Paladin',
    name: 'THE TOMB KNIGHT',
    icon: '🛡',
    blurb: 'Armored muscle. Built to shove rivals off their loot.',
    accent: '#ffd24a',
  },
];

export const JoinGameDialog: React.FC<JoinGameDialogProps> = ({ onJoin }) => {
  const [username, setUsername] = useState('');
  const [characterClass, setCharacterClass] = useState('Wizard');

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault();
    const finalUsername = username.trim() || `Robber${Math.floor(Math.random() * 1000)}`;
    onJoin(finalUsername, characterClass);
  };

  return (
    <div className="jgd-overlay">
      <form className="jgd-dialog" onSubmit={handleSubmit}>
        <div className="jgd-kicker">YOU&apos;VE BROKEN IN</div>
        <h2 className="jgd-title">CHOOSE YOUR GRAVE ROBBER</h2>

        <input
          className="jgd-input"
          type="text"
          value={username}
          placeholder="name yourself, thief…"
          onChange={(e) => setUsername(e.target.value)}
          maxLength={16}
          autoFocus
        />

        <div className="jgd-cards">
          {ROBBERS.map((r) => {
            const selected = characterClass === r.value;
            return (
              <button
                type="button"
                key={r.value}
                onClick={() => setCharacterClass(r.value)}
                className={`jgd-card${selected ? ' jgd-card-on' : ''}`}
                style={{ ['--accent' as any]: r.accent }}
              >
                <div className="jgd-card-icon">{r.icon}</div>
                <div className="jgd-card-name">{r.name}</div>
                <div className="jgd-card-blurb">{r.blurb}</div>
              </button>
            );
          })}
        </div>

        <button type="submit" className="jgd-enter">⚰&nbsp;&nbsp;DESCEND INTO THE TOMB</button>
        <div className="jgd-foot">your gate color is branded onto you when you enter</div>
      </form>

      <style>{`
        .jgd-overlay{position:fixed;inset:0;z-index:1000;display:flex;align-items:center;justify-content:center;
          background:radial-gradient(ellipse at 50% 35%, rgba(26,11,34,.92), rgba(5,2,8,.96));}
        .jgd-dialog{position:relative;width:min(560px,92vw);text-align:center;padding:34px 30px;
          font-family:'Cinzel',Georgia,serif;color:#efe3c8;
          background:linear-gradient(160deg, rgba(22,12,28,.92), rgba(10,6,14,.92));
          border:1px solid rgba(255,200,90,.28);border-radius:16px;
          box-shadow:0 0 0 1px rgba(0,0,0,.5), 0 20px 60px rgba(0,0,0,.6), inset 0 0 60px rgba(120,40,180,.08);}
        .jgd-kicker{letter-spacing:6px;font-size:12px;color:#b58cff;opacity:.85;margin-bottom:6px;}
        .jgd-title{margin:0 0 22px;font-size:clamp(20px,3.4vw,30px);font-weight:800;letter-spacing:2px;color:#ffe49a;
          text-shadow:0 0 24px rgba(255,180,70,.45);}
        .jgd-input{width:100%;box-sizing:border-box;padding:12px 16px;margin-bottom:20px;
          background:rgba(0,0,0,.45);border:1px solid rgba(255,200,90,.35);border-radius:9px;
          color:#ffe9bf;font-family:'Cinzel',Georgia,serif;font-size:17px;text-align:center;outline:none;
          transition:border-color .15s ease, box-shadow .15s ease;}
        .jgd-input::placeholder{color:#8a7a64;font-style:italic;}
        .jgd-input:focus{border-color:#ffd76a;box-shadow:0 0 18px rgba(255,190,80,.35);}
        .jgd-cards{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:24px;}
        @media (max-width:480px){.jgd-cards{grid-template-columns:1fr;}}
        .jgd-card{cursor:pointer;text-align:left;padding:16px 16px;border-radius:12px;
          background:linear-gradient(160deg, rgba(28,16,34,.8), rgba(12,7,16,.8));
          border:1px solid rgba(255,255,255,.08);color:#cebfa3;
          transition:transform .12s ease, border-color .15s ease, box-shadow .2s ease;}
        .jgd-card:hover{transform:translateY(-2px);border-color:color-mix(in srgb, var(--accent) 55%, transparent);}
        .jgd-card-on{border-color:var(--accent);
          box-shadow:0 0 0 1px var(--accent), 0 0 26px color-mix(in srgb, var(--accent) 40%, transparent),
            inset 0 0 30px color-mix(in srgb, var(--accent) 12%, transparent);}
        .jgd-card-icon{font-size:30px;filter:drop-shadow(0 0 10px var(--accent));margin-bottom:6px;}
        .jgd-card-name{font-size:15px;letter-spacing:1.5px;color:var(--accent);font-weight:700;margin-bottom:6px;
          text-shadow:0 0 12px color-mix(in srgb, var(--accent) 55%, transparent);}
        .jgd-card-blurb{font-size:12.5px;line-height:1.5;color:#bdae93;font-family:Georgia,serif;}
        .jgd-enter{cursor:pointer;width:100%;font-family:'Cinzel',Georgia,serif;font-size:18px;letter-spacing:2px;
          font-weight:700;color:#1c1207;padding:14px 0;border:none;border-radius:10px;
          background:linear-gradient(#ffe08a,#e0a93f);
          box-shadow:0 0 28px rgba(255,190,80,.55),0 6px 22px rgba(0,0,0,.5);
          transition:transform .12s ease, box-shadow .2s ease;}
        .jgd-enter:hover{transform:translateY(-2px);box-shadow:0 0 46px rgba(255,210,110,.85),0 8px 28px rgba(0,0,0,.6);}
        .jgd-foot{margin-top:14px;font-size:11px;letter-spacing:2px;color:#9a8a78;opacity:.7;font-family:monospace;}
      `}</style>
    </div>
  );
};
