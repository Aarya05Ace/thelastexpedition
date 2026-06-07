/**
 * HauntedForest.tsx — a dead, foggy graveyard-forest ringing the arena.
 *
 * Real CC0 photoscanned models from Poly Haven (dead trees, a quiver trunk,
 * boulders, a marble bust, ferns) — NOT hand-modelled. Each model is loaded
 * once and drawn as a GPU InstancedMesh, so ~80 props cost ~6 draw calls and
 * stay at 60fps. Placement is a seeded scatter in a ring OUTSIDE the play area
 * (so it frames the tomb without blocking the heist); fog hides the far ones.
 */
import { useMemo, useRef, useLayoutEffect } from 'react';
import { useGLTF } from '@react-three/drei';
import * as THREE from 'three';

interface Xform { x: number; z: number; y: number; ry: number; s: number }

// Tiny seeded PRNG so the forest layout is stable within a session.
function mulberry32(seed: number) {
  return () => {
    seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function scatter(seed: number, n: number, rMin: number, rMax: number, y: number, sMin: number, sMax: number): Xform[] {
  const rnd = mulberry32(seed);
  const out: Xform[] = [];
  for (let i = 0; i < n; i++) {
    const a = rnd() * Math.PI * 2;
    const r = rMin + rnd() * (rMax - rMin);
    out.push({ x: Math.cos(a) * r, z: Math.sin(a) * r, y, ry: rnd() * Math.PI * 2, s: sMin + rnd() * (sMax - sMin) });
  }
  return out;
}

function InstancedModel({ url, transforms }: { url: string; transforms: Xform[] }) {
  const { scene } = useGLTF(url) as any;
  const { geometry, material } = useMemo(() => {
    let g: THREE.BufferGeometry | undefined;
    let m: THREE.Material | undefined;
    scene.updateWorldMatrix(true, true);
    scene.traverse((o: any) => {
      if (o.isMesh && !g) {
        g = o.geometry.clone();
        g!.applyMatrix4(o.matrixWorld); // bake gltf orientation/scale into the geometry
        m = o.material;
        if (m) { (m as any).fog = true; }
      }
    });
    return { geometry: g, material: m };
  }, [scene]);

  const ref = useRef<THREE.InstancedMesh>(null!);
  useLayoutEffect(() => {
    if (!ref.current) return;
    const dummy = new THREE.Object3D();
    transforms.forEach((t, i) => {
      dummy.position.set(t.x, t.y, t.z);
      dummy.rotation.set(0, t.ry, 0);
      dummy.scale.setScalar(t.s);
      dummy.updateMatrix();
      ref.current.setMatrixAt(i, dummy.matrix);
    });
    ref.current.instanceMatrix.needsUpdate = true;
    ref.current.computeBoundingSphere();
  }, [transforms, geometry]);

  if (!geometry || !material) return null;
  return <instancedMesh ref={ref} args={[geometry, material, transforms.length]} castShadow={false} receiveShadow={false} />;
}

export function HauntedForest() {
  const layout = useMemo(
    () => ({
      deadTree: scatter(1, 14, 30, 56, 0, 0.9, 1.7),
      deadTree2: scatter(2, 12, 32, 58, 0, 0.9, 1.6),
      quiver: scatter(3, 16, 27, 52, 0, 0.7, 1.3),
      boulder: scatter(4, 12, 25, 50, -0.4, 0.5, 1.5),
      bust: scatter(5, 5, 25, 40, 0, 1.2, 2.0),
      fern: scatter(6, 26, 23, 52, 0, 0.6, 1.7),
    }),
    []
  );

  return (
    <group>
      <InstancedModel url="/models/env/dead_tree_trunk/dead_tree_trunk.gltf" transforms={layout.deadTree} />
      <InstancedModel url="/models/env/dead_tree_trunk_02/dead_tree_trunk_02.gltf" transforms={layout.deadTree2} />
      <InstancedModel url="/models/env/dead_quiver_trunk/dead_quiver_trunk.gltf" transforms={layout.quiver} />
      <InstancedModel url="/models/env/boulder_01/boulder_01.gltf" transforms={layout.boulder} />
      <InstancedModel url="/models/env/marble_bust_01/marble_bust_01.gltf" transforms={layout.bust} />
      <InstancedModel url="/models/env/fern_02/fern_02.gltf" transforms={layout.fern} />
    </group>
  );
}

useGLTF.preload('/models/env/dead_tree_trunk/dead_tree_trunk.gltf');
useGLTF.preload('/models/env/dead_tree_trunk_02/dead_tree_trunk_02.gltf');
useGLTF.preload('/models/env/dead_quiver_trunk/dead_quiver_trunk.gltf');
useGLTF.preload('/models/env/boulder_01/boulder_01.gltf');
useGLTF.preload('/models/env/marble_bust_01/marble_bust_01.gltf');
useGLTF.preload('/models/env/fern_02/fern_02.gltf');
