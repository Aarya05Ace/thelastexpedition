/**
 * Tomb.tsx — floor + base lighting + atmosphere. Perf-first: no realtime
 * reflections, no shadow maps. The structures/torches live in TombRuins; here
 * we do the normal-mapped ground (Poly Haven CC0), IBL from a night HDRI, a
 * little fill light, and floating dust.
 */
import { useMemo } from 'react';
import { Environment, Sparkles, useTexture } from '@react-three/drei';
import * as THREE from 'three';

function Floor() {
  const [diff, nor, arm] = useTexture([
    '/textures/forest/forest_ground_04_diff_1k.jpg',
    '/textures/forest/forest_ground_04_nor_1k.jpg',
    '/textures/forest/forest_ground_04_arm_1k.jpg',
  ]);
  useMemo(() => {
    [diff, nor, arm].forEach(t => {
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.repeat.set(26, 26);
      t.anisotropy = 4;
    });
    diff.colorSpace = THREE.SRGBColorSpace;
    nor.colorSpace = THREE.NoColorSpace;
    arm.colorSpace = THREE.NoColorSpace;
  }, [diff, nor, arm]);

  return (
    <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, 0, 0]}>
      <planeGeometry args={[200, 200]} />
      <meshStandardMaterial
        map={diff}
        normalMap={nor}
        roughnessMap={arm}
        metalnessMap={arm}
        normalScale={new THREE.Vector2(1.3, 1.3)}
        color={'#8c8a72'}
        roughness={1}
        metalness={0}
        envMapIntensity={0.7}
      />
    </mesh>
  );
}

export function Tomb() {
  return (
    <group>
      <Environment
        files="/hdri/forest_grove_2k.hdr"
        background
        backgroundBlurriness={0.4}
        backgroundIntensity={0.55}
        environmentIntensity={1.0}
      />

      {/* Forest light: warm golden KEY low on the -Z horizon (matches the GodRays sun,
          reads as sun breaking through the canopy) + cool green-teal FILL + warm rim.
          Still no shadow maps (perf-first). */}
      <ambientLight intensity={0.3} color={'#6b6450'} />
      <hemisphereLight args={['#d2bd86', '#14100a', 0.5]} />
      {/* key: golden dawn through the trees (dominant) */}
      <directionalLight position={[0, 20, -70]} intensity={2.1} color={'#ffd27a'} castShadow={false} />
      {/* fill: soft cool shade from the opposite side (subtle, not green) */}
      <directionalLight position={[-26, 14, 34]} intensity={0.3} color={'#88a4b2'} castShadow={false} />
      {/* warm rim to separate trunks from the misty depth */}
      <directionalLight position={[0, 5, 44]} intensity={0.28} color={'#ffae5a'} castShadow={false} />

      <Floor />

      {/* drifting pollen/motes (gold) + low ground mist sparkle (pale green) */}
      <Sparkles count={90} scale={[110, 22, 110]} position={[0, 9, 0]} size={2.8} speed={0.1} opacity={0.4} color={'#ffe6a0'} />
      <Sparkles count={70} scale={[80, 6, 80]} position={[0, 2.5, 0]} size={3.6} speed={0.18} opacity={0.4} color={'#bfe6a0'} />
    </group>
  );
}
