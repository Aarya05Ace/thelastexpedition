/**
 * TombRuins.tsx — the environment: a torchlit Egyptian tomb-temple courtyard
 * ringing the arena. All real CC0 low-poly models (Polygonal Mind "Tomb Chaser",
 * via the ToxSam open-source-3D-assets registry) — NOT hand-modelled:
 * columns, obelisks, monumental temple arches, Anubis/Ra/Bastet god statues,
 * palm trees, fire torches (each a real warm light), spiderwebs, urns.
 *
 * Perf: each GLB loads once; placements are cheap clones that SHARE geometry +
 * material (no per-clone geometry copy), no shadow maps, fog-culled. Low-poly
 * sources keep the whole ring well within a 60fps budget.
 */
import { useMemo } from 'react';
import { useGLTF } from '@react-three/drei';
import * as THREE from 'three';
import { GATE_RADIUS } from '../gameConstants';

const TOMB = '/models/tomb';

interface P { x: number; z: number; y?: number; ry?: number; s?: number }

function mulberry32(seed: number) {
  return () => {
    seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// evenly-spaced ring
function ringEven(n: number, r: number, faceCenter = false, angOffset = 0): P[] {
  const out: P[] = [];
  for (let i = 0; i < n; i++) {
    const ang = (i / n) * Math.PI * 2 + angOffset;
    const x = Math.cos(ang) * r, z = Math.sin(ang) * r;
    out.push({ x, z, ry: faceCenter ? Math.atan2(-x, -z) : ang, s: 1 });
  }
  return out;
}
// jittered scatter in an annulus
function scatterRing(seed: number, n: number, rMin: number, rMax: number, sMin: number, sMax: number): P[] {
  const rnd = mulberry32(seed); const out: P[] = [];
  for (let i = 0; i < n; i++) {
    const ang = rnd() * Math.PI * 2, r = rMin + rnd() * (rMax - rMin);
    out.push({ x: Math.cos(ang) * r, z: Math.sin(ang) * r, ry: rnd() * Math.PI * 2, s: sMin + rnd() * (sMax - sMin) });
  }
  return out;
}

function Scattered({ url, placements, baseScale = 1 }: { url: string; placements: P[]; baseScale?: number }) {
  const { scene } = useGLTF(url) as any;
  const minY = useMemo(() => new THREE.Box3().setFromObject(scene).min.y, [scene]);
  const clones = useMemo(() => placements.map(() => {
    const c = scene.clone(true);
    c.traverse((o: any) => { if (o.isMesh) { o.castShadow = false; o.receiveShadow = false; o.frustumCulled = true; if (o.material) o.material.fog = true; } });
    return c;
  }), [scene, placements]);
  return (
    <>
      {clones.map((c: THREE.Object3D, i: number) => {
        const p = placements[i];
        const eff = (p.s ?? 1) * baseScale;
        return <primitive key={i} object={c} position={[p.x, -minY * eff + (p.y ?? 0), p.z]} rotation={[0, p.ry ?? 0, 0]} scale={eff} />;
      })}
    </>
  );
}

function Torch({ x, z }: { x: number; z: number }) {
  return (
    <group position={[x, 0, z]}>
      <Scattered url={`${TOMB}/FireTorch01_Art.glb`} placements={[{ x: 0, z: 0, s: 1.6 }]} />
      <pointLight position={[0, 2.2, 0]} color={'#ff8a3a'} intensity={9} distance={20} decay={1.7} castShadow={false} />
    </group>
  );
}

export function TombRuins() {
  const L = useMemo(() => {
    const r = GATE_RADIUS; // 22
    return {
      columns: ringEven(14, r + 5, false, 0.12),
      obelisks: ringEven(6, r + 9, false, 0.45),
      palms: scatterRing(11, 10, r + 7, r + 26, 0.8, 1.35),
      anubis: ringEven(2, r + 2.5, true, 0.0),
      ra: ringEven(2, r + 2.5, true, Math.PI * 0.66),
      bastet: ringEven(2, r + 2.5, true, Math.PI * 1.33),
      jars: scatterRing(21, 5, r - 2, r + 3, 0.8, 1.2),
      webs: scatterRing(31, 6, r + 4.5, r + 6, 1.0, 1.8),
      torches: ringEven(6, r + 2, false, 0.3).map(p => ({ x: p.x, z: p.z })),
    };
  }, []);

  return (
    <group>
      {/* colonnade + verticals */}
      <Scattered url={`${TOMB}/Column_Art.glb`} placements={L.columns} baseScale={1.15} />
      <Scattered url={`${TOMB}/Obelisk_Art.glb`} placements={L.obelisks} baseScale={1.1} />

      {/* monumental gateway arches looming on the horizon (the centerpiece) */}
      <Scattered
        url={`${TOMB}/TempleArch02_Art.glb`}
        placements={[
          { x: 0, z: -(GATE_RADIUS + 28), ry: 0, s: 0.85 },
          { x: 0, z: GATE_RADIUS + 28, ry: Math.PI, s: 0.85 },
          { x: -(GATE_RADIUS + 28), z: 0, ry: Math.PI / 2, s: 0.8 },
        ]}
      />

      {/* COLOSSAL guardian on the far horizon, silhouetted against the cursed sun —
          the "epic vista" landmark. It partially occludes the sun so the god-ray pass
          throws shafts around it. Faces the arena. */}
      <Scattered url={`${TOMB}/GodAnubis_Art.glb`} placements={[{ x: 0, z: -96, ry: 0, s: 1 }]} baseScale={7.5} />

      {/* guardian god statues facing the robbers */}
      <Scattered url={`${TOMB}/GodAnubis_Art.glb`} placements={L.anubis} baseScale={1.7} />
      <Scattered url={`${TOMB}/GodRa_Art.glb`} placements={L.ra} baseScale={1.6} />
      <Scattered url={`${TOMB}/GodBastet_Art.glb`} placements={L.bastet} baseScale={1.7} />

      {/* the oasis: palm trees */}
      <Scattered url={`${TOMB}/PalmTree_Art.glb`} placements={L.palms} baseScale={1.2} />

      {/* decay + decor */}
      <Scattered url={`${TOMB}/Jar01_Art.glb`} placements={L.jars} baseScale={1} />
      <Scattered url={`${TOMB}/Spiderweb_Art.glb`} placements={L.webs.map(p => ({ ...p, y: 3.4 }))} baseScale={1.4} />

      {/* torches (real warm lights) */}
      {L.torches.map((t, i) => (
        <Torch key={i} x={t.x} z={t.z} />
      ))}
    </group>
  );
}

['Column_Art', 'Obelisk_Art', 'TempleArch02_Art', 'GodAnubis_Art', 'GodRa_Art', 'GodBastet_Art', 'PalmTree_Art', 'Jar01_Art', 'Spiderweb_Art', 'FireTorch01_Art']
  .forEach(n => useGLTF.preload(`${TOMB}/${n}.glb`));
