import * as THREE from 'three';
import { LineMaterial } from 'three/examples/jsm/lines/LineMaterial.js';

/** A continuous current, evaluated on the GPU along the actual path distance. */
export function createFlowMaterial(color: string) {
  const time = { value: 0 };
  const direction = { value: 1 };
  const pulseDirection = { value: 1 };
  const active = { value: 0 };
  const origin = { value: 0 }, spacing = { value: 6 };
  const pulseStart = { value: 0 }, pulseDistance = { value: 0 }, pulseEnabled = { value: 0 };
  const extraPulseStarts = { value: new Float32Array(7) }, extraPulseCount = { value: 0 };
  const material = new LineMaterial({ color, linewidth: 3.4, transparent: true,
    opacity: .85, depthTest: true, depthWrite: false, toneMapped: false });
  material.onBeforeCompile = (shader) => {
    shader.uniforms.flowTime = time;
    shader.uniforms.flowDirection = direction;
    shader.uniforms.flowActive = active;
    shader.uniforms.flowOrigin = origin;
    shader.uniforms.flowSpacing = spacing;
    shader.uniforms.pulseStart = pulseStart;
    shader.uniforms.pulseDistance = pulseDistance;
    shader.uniforms.pulseEnabled = pulseEnabled;
    shader.uniforms.pulseDirection = pulseDirection;
    shader.uniforms.extraPulseStarts = extraPulseStarts;
    shader.uniforms.extraPulseCount = extraPulseCount;
    shader.vertexShader = shader.vertexShader
      .replace('void main() {', 'attribute float instanceDistanceStart; attribute float instanceDistanceEnd; varying float currentDistance; void main() {\n currentDistance = position.y < 0.5 ? instanceDistanceStart : instanceDistanceEnd;');
    shader.fragmentShader = shader.fragmentShader
      .replace('void main() {', 'uniform float extraPulseStarts[7]; uniform float extraPulseCount; uniform float flowTime; uniform float flowDirection; uniform float flowActive; uniform float flowOrigin; uniform float flowSpacing; uniform float pulseStart; uniform float pulseDistance; uniform float pulseEnabled; uniform float pulseDirection; varying float currentDistance; void main() {')
      .replace('gl_FragColor = vec4( diffuseColor.rgb, alpha );', `
        float wave = fract((flowOrigin + currentDistance * flowDirection - flowTime * 2.4) / flowSpacing);
        // A sharp front and a long fading wake make travel direction legible.
        float crest = pow(wave, 3.0) * (1.0 - smoothstep(0.94, 1.0, wave));
        vec3 water = mix(diffuseColor.rgb, vec3(0.88, 0.98, 1.0), crest * flowActive * 0.78);
        float pulse = exp(-pow((currentDistance * pulseDirection - pulseDistance + pulseStart) / 0.9, 2.0)) * pulseEnabled;
        for (int i = 0; i < 7; i++) {
          if (float(i) < extraPulseCount) pulse = max(pulse, exp(-pow((currentDistance * pulseDirection - pulseDistance + extraPulseStarts[i]) / 0.9, 2.0)) * pulseEnabled);
        }
        water = mix(water, vec3(1.0, 0.93, 0.6), pulse * 0.9);
        gl_FragColor = vec4(water, alpha * min(1.0, mix(0.42, 0.3 + crest * 0.7, flowActive) + pulse * 0.6));`);
  };
  material.customProgramCacheKey = () => 'mep-water-current-v6';
  return { material, time, direction, active, origin, spacing, pulseStart, pulseDistance, pulseEnabled, pulseDirection, extraPulseStarts, extraPulseCount };
}

/** Two broad chevrons in the XY plane, with their tips pointing along +Y. */
export function createFlowChevronGeometry() {
  const shapes = [0, -.32].map(offset => {
    const shape = new THREE.Shape();
    shape.moveTo(-.24, offset);
    shape.lineTo(0, offset + .26);
    shape.lineTo(.24, offset);
    shape.lineTo(.16, offset - .075);
    shape.lineTo(0, offset + .095);
    shape.lineTo(-.16, offset - .075);
    shape.closePath();
    return shape;
  });
  return new THREE.ShapeGeometry(shapes);
}

export function attachFlowDistances(geometry: THREE.BufferGeometry, points: THREE.Vector3[]) {
  const starts: number[] = [], ends: number[] = [];
  let distance = 0;
  for (let index = 1; index < points.length; index++) {
    starts.push(distance);
    distance += points[index].distanceTo(points[index - 1]);
    ends.push(distance);
  }
  geometry.setAttribute('instanceDistanceStart', new THREE.InstancedBufferAttribute(new Float32Array(starts), 1));
  geometry.setAttribute('instanceDistanceEnd', new THREE.InstancedBufferAttribute(new Float32Array(ends), 1));
}
