import * as THREE from 'three';

/** Arc-length sampling preserves the exported route, including its elbows. */
class WaterRoute extends THREE.Curve<THREE.Vector3> {
  private distances = [0];
  readonly length: number;
  constructor(private points: THREE.Vector3[]) {
    super();
    for (let i = 1; i < points.length; i++) this.distances.push(this.distances[i - 1] + points[i].distanceTo(points[i - 1]));
    this.length = this.distances[this.distances.length - 1];
  }
  getPoint(t: number, target = new THREE.Vector3()) {
    const distance = THREE.MathUtils.clamp(t, 0, 1) * this.length;
    let i = 1;
    while (i < this.distances.length - 1 && this.distances[i] < distance) i++;
    const amount = (distance - this.distances[i - 1]) / Math.max(this.distances[i] - this.distances[i - 1], .000001);
    return target.copy(this.points[i - 1]).lerp(this.points[i], amount);
  }
}

/** Illustrative water volume: all motion stops when circulation is unresolved. */
export function createWaterVolume(points: THREE.Vector3[], color: string, radius: number, endRadius = radius) {
  const route = new WaterRoute(points);
  const uniforms = {
    time: { value: 0 }, direction: { value: 1 }, active: { value: 0 },
    origin: { value: 0 }, spacing: { value: 6 },
    pulseStart: { value: 0 }, pulseDistance: { value: 0 }, pulseEnabled: { value: 0 },
    pulseDirection: { value: 1 },
    radius: { value: radius }, endRadius: { value: endRadius },
    routeLength: { value: route.length }, opacity: { value: 1 },
    color: { value: new THREE.Color(color) },
  };
  const material = new THREE.ShaderMaterial({
    uniforms: { ...uniforms, flowActive: uniforms.active }, transparent: true, depthWrite: false, depthTest: true,
    side: THREE.DoubleSide, toneMapped: false,
    vertexShader: `
      uniform float time, direction, flowActive, radius, endRadius, routeLength, origin, spacing;
      varying vec2 waterUv;
      varying vec3 waterNormal, waterView;
      void main() {
        waterUv = uv;
        float travel = origin + uv.x * routeLength * direction - time * 2.4;
        float localRadius = mix(radius, endRadius, uv.x);
        float swell = -0.05 + sin(travel * 6.283 / spacing) * 0.025 * flowActive;
        vec3 p = position + normal * localRadius * swell;
        vec4 viewPosition = modelViewMatrix * vec4(p, 1.0);
        waterNormal = normalize(normalMatrix * normal);
        waterView = -viewPosition.xyz;
        gl_Position = projectionMatrix * viewPosition;
      }`,
    fragmentShader: `
      uniform float time, direction, flowActive, routeLength, opacity, origin, spacing;
      uniform float pulseStart, pulseDistance, pulseEnabled, pulseDirection;
      uniform vec3 color;
      varying vec2 waterUv;
      varying vec3 waterNormal, waterView;
      void main() {
        float travel = origin + waterUv.x * routeLength * direction - time * 2.4;
        float phase = travel * 6.283 / spacing;
        // Long, soft filaments rather than bright transverse bubble rings.
        float crest = smoothstep(-0.6, 1.0, sin(phase)) * flowActive;
        // A restrained travelling crest with a fading wake indicates direction.
        float wave = fract(travel / spacing);
        float waveFront = smoothstep(0.45, 0.86, wave) * (1.0 - smoothstep(0.86, 1.0, wave)) * flowActive;
        float filaments = pow(0.5 + 0.5 * sin(waterUv.y * 18.85 + sin(phase) * 0.35), 3.0);
        float ripples = (0.5 + 0.5 * sin(phase * 2.0 + waterUv.y * 6.283)) * filaments * flowActive;
        float edge = pow(1.0 - abs(dot(normalize(waterNormal), normalize(waterView))), 2.0);
        float sheen = pow(max(0.0, dot(reflect(-normalize(waterView), normalize(waterNormal)), normalize(vec3(0.4, 0.8, 0.6)))), 20.0);
        // Neutral classic water with pale reflections; the separate flow line carries circuit colour.
        vec3 classicColor = vec3(0.04, 0.42, 0.58);
        vec3 water = mix(classicColor * 0.42, vec3(0.1, 0.65, 0.83), 0.18 + edge * 0.4);
        water = mix(water, vec3(0.8, 0.98, 1.0), min(0.65, filaments * 0.22 + crest * filaments * 0.12 + ripples * 0.08 + sheen * 0.35));
        water = mix(water, vec3(0.65, 0.92, 0.98), waveFront * 0.18);
        float pulse = exp(-pow((waterUv.x * routeLength * pulseDirection - pulseDistance + pulseStart) / 0.9, 2.0)) * pulseEnabled * flowActive;
        water = mix(water, vec3(1.0, 0.94, 0.65), pulse * 0.85);
        gl_FragColor = vec4(water, opacity * min(0.95, 0.48 + edge * 0.16 + filaments * 0.08 + crest * filaments * 0.06 + sheen * 0.05 + waveFront * 0.07 + pulse * 0.3));
        #include <colorspace_fragment>
      }`,
  });
  const geometry = new THREE.TubeGeometry(route, Math.min(96, Math.max(8, Math.ceil(route.length / .4))), radius, 10, false);
  const positions = geometry.getAttribute('position'), normals = geometry.getAttribute('normal'), uvs = geometry.getAttribute('uv');
  for (let i = 0; i < positions.count; i++) {
    const change = (endRadius - radius) * uvs.getX(i);
    positions.setXYZ(i, positions.getX(i) + normals.getX(i) * change, positions.getY(i) + normals.getY(i) * change, positions.getZ(i) + normals.getZ(i) * change);
  }
  geometry.computeBoundingSphere();
  const mesh = new THREE.Mesh(geometry, material);
  mesh.renderOrder = 2999; mesh.raycast = () => {};
  return { mesh, uniforms, radius };
}
