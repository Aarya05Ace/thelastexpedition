/**
 * GameScene.tsx — the R3F Canvas for TOMB RUSH.
 * Dark torchlit tomb + fog + bloom/vignette, with relics, bank gates, traps,
 * and all player heroes rendered from synced SpacetimeDB state.
 */
import React, { Suspense, useRef } from 'react';
import { Canvas } from '@react-three/fiber';
import { EffectComposer, Bloom, Vignette, GodRays, HueSaturation, BrightnessContrast } from '@react-three/postprocessing';
import * as THREE from 'three';
import { PlayerData, InputState, Relic, BankGate, Trap } from '../generated/types';
import { Identity } from 'spacetimedb';
import { Player } from './Player';
import { Tomb } from './Tomb';
import { Forest } from './Forest';
import { Relics } from './Relics';
import { BankGates } from './BankGates';
import { Traps } from './Traps';

interface GameSceneProps {
  players: ReadonlyMap<string, PlayerData>;
  relics: Relic[];
  gates: BankGate[];
  traps: Trap[];
  nowTick: number;
  localPlayerIdentity: Identity | null;
  onPlayerRotation?: (rotation: THREE.Euler) => void;
  currentInputRef?: React.MutableRefObject<InputState>;
  isDebugPanelVisible?: boolean;
}

export const GameScene: React.FC<GameSceneProps> = ({
  players,
  relics,
  gates,
  traps,
  nowTick,
  localPlayerIdentity,
  onPlayerRotation,
  currentInputRef,
  isDebugPanelVisible = false,
}) => {
  // The "cursed sun" mesh on the horizon — both a visible glow and the origin for
  // the volumetric god-ray shafts that stream through the temple columns/arches.
  const sunRef = useRef<THREE.Mesh>(null!);
  return (
    <Canvas
      camera={{ position: [0, 22, 30], fov: 62, near: 0.5, far: 4000 }}
      style={{ position: 'absolute', top: 0, left: 0, width: '100%', height: '100%', zIndex: 1 }}
      dpr={[1, 1.5]}
      gl={{ antialias: false, powerPreference: 'high-performance', toneMapping: THREE.ACESFilmicToneMapping, toneMappingExposure: 1.08 }}
    >
      {/* misty golden-forest haze — thin so the huge map reads into the distance */}
      <fogExp2 attach="fog" args={['#27241a', 0.0016]} />

      {/* environment, lights, floor, pillars, torches, dust.
          Suspense boundary: drei <Environment> suspends while the HDRI loads. */}
      <Suspense fallback={null}>
      <Tomb />
      <Forest />

      {/* gameplay entities */}
      <BankGates gates={gates} localIdentityHex={localPlayerIdentity?.toHexString() ?? null} nowTick={nowTick} />
      <Relics relics={relics} />
      <Traps traps={traps} />

      {/* players */}
      {Array.from(players.values()).map(player => {
        const isLocal = localPlayerIdentity?.toHexString() === player.identity.toHexString();
        return (
          <Player
            key={player.identity.toHexString()}
            playerData={player}
            isLocalPlayer={isLocal}
            nowTick={nowTick}
            onRotationChange={isLocal ? onPlayerRotation : undefined}
            currentInputRef={isLocal ? currentInputRef : undefined}
            isDebugArrowVisible={isLocal ? isDebugPanelVisible : false}
            isDebugPanelVisible={isDebugPanelVisible}
          />
        );
      })}
      </Suspense>

      {/* the cursed sun, low on the horizon behind the temple arch. Bright + fog-immune
          so the god-ray pass has a strong source; a soft halo sells the dawn glow. */}
      <mesh ref={sunRef} position={[0, 90, -780]}>
        <sphereGeometry args={[60, 32, 32]} />
        <meshBasicMaterial color={'#ffd089'} toneMapped={false} fog={false} />
      </mesh>
      <mesh position={[0, 90, -785]}>
        <sphereGeometry args={[130, 32, 32]} />
        <meshBasicMaterial color={'#ff9a40'} transparent opacity={0.28} blending={THREE.AdditiveBlending} depthWrite={false} toneMapped={false} fog={false} />
      </mesh>

      <EffectComposer multisampling={0}>
        {/* volumetric shafts streaming through the colonnade — the signature look */}
        <GodRays sun={sunRef} samples={60} density={0.96} decay={0.93} weight={0.5} exposure={0.5} clampMax={1} blur />
        {/* golden bloom on the sun, torches, relics, auras */}
        <Bloom intensity={1.15} luminanceThreshold={0.2} luminanceSmoothing={0.9} mipmapBlur radius={0.75} />
        {/* cinematic grade: richer color + deeper contrast (orange-teal feel) */}
        <HueSaturation saturation={0.06} />
        <BrightnessContrast brightness={-0.03} contrast={0.12} />
        <Vignette eskil={false} offset={0.18} darkness={0.95} />
      </EffectComposer>
    </Canvas>
  );
};
