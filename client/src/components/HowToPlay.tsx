/**
 * HowToPlay.tsx — the cinematic start screen. Explains the whole game (the
 * "logistics") before you ever spawn: objective, controls, the Keeper, the
 * timer. Pure animated CSS — drifting fog, a flickering glowing title, and
 * rule cards — so it reads like a real game's title screen, not a form.
 */
interface HowToPlayProps {
  onEnter: () => void;
}

const RULES = [
  { icon: '🏺', title: 'GRAB THE RELICS', body: 'Glowing relics are scattered in the tomb. Run over one to snatch it — automatically.', accent: '#ffd24a' },
  { icon: '🏛️', title: 'BANK AT YOUR GATE', body: 'Carry it to YOUR colored gate (it has a beam of light) to score a point. Carrying slows you and makes you glow — you’re a target.', accent: '#37e6e6' },
  { icon: '👊', title: 'SHOVE  ·  press  F', body: 'Get close to a rival and press F. They drop their loot and freeze for a moment. This is the whole game. Be ruthless.', accent: '#ff5de2' },
  { icon: '💀', title: 'THE TOMB KEEPER', body: 'An AI presence watches the match, taunts you aloud, and turns on whoever’s winning — curses, sealed gates, traps.', accent: '#be46ff' },
  { icon: '⏳', title: 'THE COLLAPSE', body: 'You have 3 minutes. When the tomb collapses, whoever banked the most relics wins. Then you’ll want a rematch.', accent: '#ff5a5a' },
  { icon: '🎮', title: 'MOVE', body: 'WASD to move, mouse to look (click to lock), Shift to sprint. M mutes the Keeper.', accent: '#9be37b' },
];

export function HowToPlay({ onEnter }: HowToPlayProps) {
  return (
    <div className="htp-root">
      <div className="htp-fog htp-fog-a" />
      <div className="htp-fog htp-fog-b" />
      <div className="htp-vignette" />

      <div className="htp-content">
        <div className="htp-kicker">A 3-MINUTE TREASURE-HEIST BRAWL</div>
        <h1 className="htp-title">TOMB&nbsp;RUSH</h1>
        <div className="htp-sub">Break in. Grab the cursed relics. Shove everyone else. Don&apos;t anger what sleeps here.</div>

        <div className="htp-grid">
          {RULES.map((r, i) => (
            <div className="htp-card" key={i} style={{ ['--accent' as any]: r.accent, animationDelay: `${0.15 * i}s` }}>
              <div className="htp-card-icon">{r.icon}</div>
              <div className="htp-card-title">{r.title}</div>
              <div className="htp-card-body">{r.body}</div>
            </div>
          ))}
        </div>

        <button className="htp-enter" onClick={onEnter}>⚰&nbsp;&nbsp;ENTER THE TOMB</button>
        <div className="htp-foot">2–6 robbers · open more tabs (or share the link) to bring friends</div>
      </div>

      <style>{`
        .htp-root{position:fixed;inset:0;z-index:1000;overflow:hidden;
          background:radial-gradient(ellipse at 50% 30%, #1a0b22 0%, #0a0410 55%, #050208 100%);
          font-family:'Cinzel',Georgia,serif;color:#efe3c8;display:flex;align-items:center;justify-content:center;}
        .htp-fog{position:absolute;inset:-20%;background-repeat:repeat;opacity:.5;pointer-events:none;
          background:radial-gradient(circle at 20% 30%, rgba(120,40,180,.18), transparent 40%),
                     radial-gradient(circle at 80% 60%, rgba(40,80,180,.16), transparent 45%),
                     radial-gradient(circle at 50% 80%, rgba(180,120,40,.12), transparent 50%);}
        .htp-fog-a{animation:htpdrift 26s linear infinite;}
        .htp-fog-b{animation:htpdrift 40s linear infinite reverse;opacity:.35;}
        @keyframes htpdrift{0%{transform:translate(0,0) scale(1)}50%{transform:translate(4%,-3%) scale(1.08)}100%{transform:translate(0,0) scale(1)}}
        .htp-vignette{position:absolute;inset:0;pointer-events:none;
          box-shadow:inset 0 0 280px 70px rgba(0,0,0,.92);}
        .htp-content{position:relative;z-index:2;width:min(1080px,92vw);text-align:center;padding:24px 0;}
        .htp-kicker{letter-spacing:7px;font-size:13px;color:#b58cff;opacity:.85;margin-bottom:6px;}
        .htp-title{font-size:clamp(54px,11vw,128px);margin:0;line-height:.95;font-weight:800;letter-spacing:4px;
          color:#ffe49a;text-shadow:0 0 30px rgba(255,180,70,.55),0 0 70px rgba(190,70,255,.4);
          animation:htpflicker 5s ease-in-out infinite;}
        @keyframes htpflicker{0%,100%{opacity:1}48%{opacity:.94}50%{opacity:.74}52%{opacity:.95}}
        .htp-sub{margin:14px auto 26px;max-width:680px;font-size:clamp(14px,1.7vw,19px);color:#d9c8a6;opacity:.9;line-height:1.5;}
        .htp-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin-bottom:30px;}
        @media (max-width:780px){.htp-grid{grid-template-columns:repeat(1,1fr);}}
        .htp-card{background:linear-gradient(160deg, rgba(22,12,28,.86), rgba(10,6,14,.86));
          border:1px solid color-mix(in srgb, var(--accent) 40%, transparent);
          border-radius:14px;padding:18px 16px;text-align:left;
          box-shadow:0 0 0 1px rgba(0,0,0,.4), 0 10px 30px rgba(0,0,0,.5), inset 0 0 40px color-mix(in srgb, var(--accent) 9%, transparent);
          opacity:0;transform:translateY(14px);animation:htprise .6s ease forwards;}
        @keyframes htprise{to{opacity:1;transform:translateY(0)}}
        .htp-card-icon{font-size:30px;filter:drop-shadow(0 0 10px var(--accent));margin-bottom:6px;}
        .htp-card-title{font-size:15px;letter-spacing:2px;color:var(--accent);
          text-shadow:0 0 12px color-mix(in srgb, var(--accent) 60%, transparent);margin-bottom:7px;font-weight:700;}
        .htp-card-body{font-size:13.5px;line-height:1.55;color:#cebfa3;font-family:Georgia,serif;}
        .htp-enter{cursor:pointer;font-family:'Cinzel',Georgia,serif;font-size:22px;letter-spacing:3px;font-weight:700;
          color:#1c1207;padding:16px 44px;border:none;border-radius:10px;
          background:linear-gradient(#ffe08a,#e0a93f);
          box-shadow:0 0 30px rgba(255,190,80,.65),0 6px 24px rgba(0,0,0,.5);
          transition:transform .12s ease, box-shadow .2s ease;animation:htppulse 2.4s ease-in-out infinite;}
        .htp-enter:hover{transform:translateY(-2px) scale(1.03);box-shadow:0 0 50px rgba(255,200,90,.9),0 8px 30px rgba(0,0,0,.6);}
        @keyframes htppulse{0%,100%{box-shadow:0 0 26px rgba(255,190,80,.5),0 6px 24px rgba(0,0,0,.5)}50%{box-shadow:0 0 48px rgba(255,210,110,.85),0 6px 24px rgba(0,0,0,.5)}}
        .htp-foot{margin-top:16px;font-size:12px;letter-spacing:2px;color:#9a8a78;opacity:.7;font-family:monospace;}
      `}</style>
    </div>
  );
}
