/**
 * Traps.tsx — hazards the Tomb Keeper drops onto the floor. Pulsing red runes;
 * stepping on one (server-side) makes you drop loot and stunlocks you briefly.
 */
import { useRef } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import type { Trap } from '../generated/types';

function TrapMesh({ trap }: { trap: Trap }) {
  const mat = useRef<THREE.MeshStandardMaterial>(null!);
  const ring = useRef<THREE.Mesh>(null!);
  useFrame((state, dt) => {
    const p = 0.5 + 0.5 * Math.sin(state.clock.elapsedTime * 6 + trap.x);
    if (mat.current) mat.current.emissiveIntensity = 1 + p * 3;
    if (ring.current) ring.current.rotation.z -= dt * 1.4;
  });
  return (
    <group position={[trap.x, 0.06, trap.z]}>
      <mesh ref={ring} rotation={[-Math.PI / 2, 0, 0]}>
        <ringGeometry args={[0.9, 1.6, 6]} />
        <meshStandardMaterial ref={mat} color={'#ff2a2a'} emissive={'#ff0000'} emissiveIntensity={2} transparent opacity={0.8} side={THREE.DoubleSide} />
      </mesh>
      {/* spikes */}
      {[0, 1, 2, 3, 4].map(i => {
        const a = (i / 5) * Math.PI * 2;
        return (
          <mesh key={i} position={[Math.cos(a) * 0.9, 0.3, Math.sin(a) * 0.9]} rotation={[0, 0, 0]}>
            <coneGeometry args={[0.16, 0.7, 6]} />
            <meshStandardMaterial color={'#3a0808'} emissive={'#ff2a2a'} emissiveIntensity={0.6} />
          </mesh>
        );
      })}
      {/* emissive + Bloom glow, no real light (perf) */}
    </group>
  );
}

export function Traps({ traps }: { traps: Trap[] }) {
  return (
    <>
      {traps.filter(t => t.armed).map(t => (
        <TrapMesh key={t.id.toString()} trap={t} />
      ))}
    </>
  );
}
