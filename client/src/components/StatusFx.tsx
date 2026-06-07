/**
 * StatusFx.tsx — in-world visual feedback for the Tomb Keeper's effects and loot.
 *
 * Rendered as a child of each <Player> group, so it rides the same client-predicted
 * transform as the character model (no server-position lag). Reads the already-synced
 * status columns on PlayerData + the live game tick to decide which auras are active:
 *
 *   carrying  → gold halo + light + a floating spark overhead  ("you glow, you're a target")
 *   cursed    → purple swirling ring + halo                     (Keeper slowed you / shakes loot loose)
 *   blessed   → bright gold-white halo + ground ring            (Keeper aided the underdog)
 *   stunned   → red stars orbiting the head                     (shoved or hit a trap)
 *
 * Everything is additive + emissive so the scene Bloom pass carries the glow (cheap).
 */
import { useRef } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import type { PlayerData } from '../generated/types';

const ADDITIVE = {
  transparent: true,
  blending: THREE.AdditiveBlending,
  depthWrite: false,
} as const;

const CARRY = new THREE.Color('#ffd24a');
const CURSE = new THREE.Color('#b14bff');
const BLESS = new THREE.Color('#fff0b0');
const STUN = new THREE.Color('#ff5a5a');

function CarryGlow() {
  const light = useRef<THREE.PointLight>(null!);
  const halo = useRef<THREE.Mesh>(null!);
  const spark = useRef<THREE.Group>(null!);
  useFrame((s) => {
    const p = 0.5 + 0.5 * Math.sin(s.clock.elapsedTime * 3);
    if (light.current) light.current.intensity = 2.2 + p * 1.6;
    if (halo.current) (halo.current.material as THREE.Material).opacity = 0.18 + p * 0.12;
    if (spark.current) {
      spark.current.rotation.y += 0.04;
      spark.current.position.y = 2.7 + Math.sin(s.clock.elapsedTime * 2.4) * 0.12;
    }
  });
  return (
    <group>
      <pointLight ref={light} position={[0, 0.6, 0]} color={CARRY} intensity={2.6} distance={8} decay={2} castShadow={false} />
      <mesh ref={halo} position={[0, 0.5, 0]}>
        <sphereGeometry args={[1.15, 16, 16]} />
        <meshBasicMaterial color={CARRY} opacity={0.22} {...ADDITIVE} />
      </mesh>
      {/* a floating relic-spark above the head ties the glow to "they hold loot" */}
      <group ref={spark} position={[0, 2.7, 0]}>
        <mesh>
          <octahedronGeometry args={[0.22, 0]} />
          <meshBasicMaterial color={CARRY} />
        </mesh>
      </group>
    </group>
  );
}

function CurseAura() {
  const ring = useRef<THREE.Mesh>(null!);
  const halo = useRef<THREE.Mesh>(null!);
  useFrame((s, dt) => {
    if (ring.current) ring.current.rotation.z += dt * 1.8;
    if (halo.current) (halo.current.material as THREE.Material).opacity = 0.14 + 0.12 * (0.5 + 0.5 * Math.sin(s.clock.elapsedTime * 4));
  });
  return (
    <group>
      <mesh ref={ring} position={[0, 0.7, 0]} rotation={[-Math.PI / 2.2, 0, 0]}>
        <torusGeometry args={[1.0, 0.07, 8, 32]} />
        <meshBasicMaterial color={CURSE} opacity={0.85} {...ADDITIVE} />
      </mesh>
      <mesh ref={halo} position={[0, 0.6, 0]}>
        <sphereGeometry args={[1.05, 16, 16]} />
        <meshBasicMaterial color={CURSE} opacity={0.18} {...ADDITIVE} />
      </mesh>
    </group>
  );
}

function BlessAura() {
  const halo = useRef<THREE.Mesh>(null!);
  const ring = useRef<THREE.Mesh>(null!);
  useFrame((s, dt) => {
    const p = 0.5 + 0.5 * Math.sin(s.clock.elapsedTime * 3.2);
    if (halo.current) (halo.current.material as THREE.Material).opacity = 0.2 + p * 0.18;
    if (ring.current) ring.current.rotation.z -= dt * 0.8;
  });
  return (
    <group>
      <mesh ref={halo} position={[0, 0.7, 0]}>
        <sphereGeometry args={[1.2, 16, 16]} />
        <meshBasicMaterial color={BLESS} opacity={0.3} {...ADDITIVE} />
      </mesh>
      <mesh ref={ring} position={[0, 0.05, 0]} rotation={[-Math.PI / 2, 0, 0]}>
        <ringGeometry args={[0.9, 1.25, 28]} />
        <meshBasicMaterial color={BLESS} opacity={0.55} side={THREE.DoubleSide} {...ADDITIVE} />
      </mesh>
    </group>
  );
}

function StunStars() {
  const grp = useRef<THREE.Group>(null!);
  useFrame((_, dt) => {
    if (grp.current) grp.current.rotation.y += dt * 6;
  });
  return (
    <group ref={grp} position={[0, 2.5, 0]}>
      {[0, 1, 2].map((i) => {
        const a = (i / 3) * Math.PI * 2;
        return (
          <mesh key={i} position={[Math.cos(a) * 0.5, Math.sin(a * 2) * 0.1, Math.sin(a) * 0.5]}>
            <octahedronGeometry args={[0.12, 0]} />
            <meshBasicMaterial color={STUN} />
          </mesh>
        );
      })}
    </group>
  );
}

export function StatusFx({ playerData, nowTick }: { playerData: PlayerData; nowTick: number }) {
  const carrying = playerData.carryingRelicId !== undefined && playerData.carryingRelicId !== null;
  const cursed = Number(playerData.cursedUntilTick) > nowTick;
  const blessed = Number(playerData.blessedUntilTick) > nowTick;
  const stunned = Number(playerData.stunnedUntilTick) > nowTick;

  return (
    <group>
      {carrying && <CarryGlow />}
      {cursed && <CurseAura />}
      {blessed && <BlessAura />}
      {stunned && <StunStars />}
    </group>
  );
}
