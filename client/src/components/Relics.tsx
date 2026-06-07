/**
 * Relics.tsx — the glowing loot. Each relic floats + spins while loose,
 * rides above its carrier while carried, and rests dim once banked.
 * Emissive materials + the scene Bloom pass make these the eye-catchers.
 */
import { useRef } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import type { Relic } from '../generated/types';

const LOOSE_COLOR = new THREE.Color('#ffd24a');
const CARRIED_COLOR = new THREE.Color('#aef3ff');
const BANKED_COLOR = new THREE.Color('#6a6048');

function RelicMesh({ relic }: { relic: Relic }) {
  const group = useRef<THREE.Group>(null!);
  const target = useRef(new THREE.Vector3(relic.x, relic.y, relic.z));

  useFrame((state, dt) => {
    if (!group.current) return;
    target.current.set(relic.x, relic.y, relic.z);
    // Smoothly chase the server position (relics move when carried).
    group.current.position.lerp(target.current, Math.min(1, dt * 14));
    const t = state.clock.elapsedTime;
    group.current.rotation.y += dt * 1.6;
    if (relic.state === 'loose') {
      group.current.position.y = target.current.y + Math.sin(t * 2 + relic.x) * 0.18;
    }
  });

  const banked = relic.state === 'banked';
  const carried = relic.state === 'carried';
  const color = banked ? BANKED_COLOR : carried ? CARRIED_COLOR : LOOSE_COLOR;
  const intensity = banked ? 0.4 : carried ? 3.2 : 2.2;
  const scale = banked ? 0.6 : 1;

  return (
    <group ref={group} position={[relic.x, relic.y, relic.z]} scale={scale}>
      {/* the gem */}
      <mesh castShadow>
        <octahedronGeometry args={[0.45, 0]} />
        <meshStandardMaterial
          color={color}
          emissive={color}
          emissiveIntensity={intensity}
          metalness={0.3}
          roughness={0.15}
        />
      </mesh>
      {/* inner core */}
      <mesh scale={0.55}>
        <octahedronGeometry args={[0.45, 0]} />
        <meshBasicMaterial color={'#ffffff'} />
      </mesh>
      {/* No real lights here — emissive + the scene Bloom pass carry the glow (perf). */}
      {/* ground ring marker for loose relics */}
      {relic.state === 'loose' && (
        <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, -relic.y + 0.03, 0]}>
          <ringGeometry args={[0.8, 1.0, 24]} />
          <meshBasicMaterial color={color} transparent opacity={0.5} side={THREE.DoubleSide} />
        </mesh>
      )}
    </group>
  );
}

export function Relics({ relics }: { relics: Relic[] }) {
  return (
    <>
      {relics.map(r => (
        <RelicMesh key={r.id.toString()} relic={r} />
      ))}
    </>
  );
}
