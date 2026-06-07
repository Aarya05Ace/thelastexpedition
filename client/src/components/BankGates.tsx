/**
 * BankGates.tsx — one glowing altar/portal per robber, colored to match them.
 * Carry a relic into your own gate to bank a point. The local player's gate
 * gets a tall beacon beam; a sealed gate (Keeper curse) turns barred + red.
 */
import { useRef } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import type { BankGate } from '../generated/types';

const COLOR_MAP: Record<string, string> = {
  cyan: '#37e6e6',
  magenta: '#ff5de2',
  yellow: '#ffe14d',
  lightgreen: '#7bff7b',
  orange: '#ff9b3d',
  white: '#f5f5f5',
};
const col = (c: string) => COLOR_MAP[c] ?? c;

function Gate({ gate, isMine, sealed }: { gate: BankGate; isMine: boolean; sealed: boolean }) {
  const ring = useRef<THREE.Mesh>(null!);
  useFrame((_, dt) => {
    if (ring.current) ring.current.rotation.z += dt * (sealed ? 0.2 : 0.9);
  });
  const color = sealed ? '#ff3b3b' : col(gate.color);

  return (
    <group position={[gate.x, 0, gate.z]}>
      {/* altar base */}
      <mesh position={[0, 0.4, 0]} castShadow receiveShadow>
        <cylinderGeometry args={[2.4, 2.8, 0.8, 16]} />
        <meshStandardMaterial color={'#1a141e'} roughness={0.9} />
      </mesh>
      {/* glowing rune disc */}
      <mesh position={[0, 0.82, 0]} rotation={[-Math.PI / 2, 0, 0]}>
        <circleGeometry args={[2.1, 32]} />
        <meshStandardMaterial color={color} emissive={color} emissiveIntensity={sealed ? 0.8 : 2.2} transparent opacity={0.85} side={THREE.DoubleSide} />
      </mesh>
      {/* spinning ring */}
      <mesh ref={ring} position={[0, 0.9, 0]} rotation={[-Math.PI / 2, 0, 0]}>
        <torusGeometry args={[2.3, 0.08, 8, 40]} />
        <meshBasicMaterial color={color} />
      </mesh>
      {/* beacon beam for the local player's gate */}
      {isMine && !sealed && (
        <mesh position={[0, 8, 0]}>
          <cylinderGeometry args={[0.5, 1.3, 16, 16, 1, true]} />
          <meshBasicMaterial color={color} transparent opacity={0.16} side={THREE.DoubleSide} depthWrite={false} />
        </mesh>
      )}
      {/* sealed bars */}
      {sealed &&
        [-1.2, 0, 1.2].map((x, i) => (
          <mesh key={i} position={[x, 2.2, 0]}>
            <boxGeometry args={[0.22, 4.4, 0.22]} />
            <meshStandardMaterial color={'#ff3b3b'} emissive={'#ff1a1a'} emissiveIntensity={1.5} />
          </mesh>
        ))}
      {/* glow via emissive + Bloom, no real light (perf) */}
    </group>
  );
}

export function BankGates({
  gates,
  localIdentityHex,
  nowTick,
}: {
  gates: BankGate[];
  localIdentityHex: string | null;
  nowTick: number;
}) {
  return (
    <>
      {gates.map(g => (
        <Gate
          key={g.owner.toHexString()}
          gate={g}
          isMine={g.owner.toHexString() === localIdentityHex}
          sealed={Number(g.sealedUntilTick) > nowTick}
        />
      ))}
    </>
  );
}
