import * as THREE from 'three';
import { MepGamepad, readBrowserPads, stick, xbox } from './mep-gamepad';

// Revit packages use feet; WebXR poses and controller geometry use metres.
export const XR_FEET_PER_METRE = 1 / .3048;
export class SnapTurn {
  private armed = false;
  read(value: number): number {
    if (Math.abs(value) < .2) this.armed = true;
    if (!this.armed || Math.abs(value) < .65) return 0;
    this.armed = false;
    return -Math.sign(value) * Math.PI / 6;
  }
}
export async function supportsVr(xr?: Pick<XRSystem, 'isSessionSupported'>): Promise<boolean> {
  try { return !!xr && await xr.isSessionSupported('immersive-vr'); }
  catch { return false; }
}
export function xrStick(source: XRInputSource): [number, number] {
  const pad = source.gamepad;
  if (!pad || pad.mapping !== 'xr-standard') return [0, 0];
  return stick(pad.axes[2], pad.axes[3]);
}
export function turnAboutHead(rig: THREE.Object3D, camera: THREE.Camera, angle: number) {
  const before = camera.getWorldPosition(new THREE.Vector3());
  rig.rotation.y += angle; rig.updateMatrixWorld(true);
  const after = camera.getWorldPosition(new THREE.Vector3());
  rig.position.x += before.x - after.x; rig.position.z += before.z - after.z;
  rig.updateMatrixWorld(true);
}

type Options = {
  renderer: THREE.WebGLRenderer; scene: THREE.Scene; camera: THREE.PerspectiveCamera;
  prepare: () => { footY: number }; restore: () => void;
  objects: () => THREE.Object3D[]; ready: (position: THREE.Vector3) => boolean;
  pick: (ray: THREE.Ray) => string; state: (active: boolean) => void;
};

export class MepVrVisit {
  active = false;
  private disposed = false;
  private pending = false;
  private session: XRSession | null = null;
  private rig = new THREE.Group();
  private saved: (() => void) | null = null;
  private home = new THREE.Vector3();
  private yaw = 0;
  private snap = new SnapTurn();
  private pad = new MepGamepad();
  private previous = new Map<XRInputSource, boolean[]>();
  private armed = false;
  private firstPose = true;
  private overlay = new THREE.Scene();
  private panel: THREE.Mesh<THREE.PlaneGeometry, THREE.MeshBasicMaterial> | null = null;
  private canvas: HTMLCanvasElement | null = null;
  private selection = '';
  private controllers: { object: ReturnType<THREE.WebGLRenderer['xr']['getController']>; select: () => void; line: THREE.Line }[] = [];
  constructor(private options: Options) {}

  async enter() {
    if (this.disposed || this.pending || this.active || !navigator.xr) return;
    this.pending = true;
    let session: XRSession | null = null;
    try {
      // Keep the request in the button's user activation, before any await.
      session = await navigator.xr.requestSession('immersive-vr', { requiredFeatures: ['local-floor'] });
      if (this.disposed) { await session.end(); return; }
      this.session = session;
      session.addEventListener('end', this.onEnd);
      const { renderer, camera, scene } = this.options;
      const position = camera.position.clone(), quaternion = camera.quaternion.clone(), scale = camera.scale.clone();
      const parent = camera.parent, near = camera.near, far = camera.far, aspect = camera.aspect, fov = camera.fov, zoom = camera.zoom;
      const xrEnabled = renderer.xr.enabled;
      this.saved = () => {
        this.rig.remove(camera); if (parent) parent.add(camera);
        camera.position.copy(position); camera.quaternion.copy(quaternion); camera.scale.copy(scale);
        camera.near = near; camera.far = far; camera.aspect = aspect; camera.fov = fov; camera.zoom = zoom; camera.updateProjectionMatrix();
        renderer.xr.enabled = xrEnabled;
      };
      const { footY } = this.options.prepare();
      this.home.set(camera.position.x, footY, camera.position.z);
      const forward = camera.getWorldDirection(new THREE.Vector3());
      this.yaw = Math.atan2(-forward.x, -forward.z);
      this.rig.position.copy(this.home); this.rig.rotation.set(0, this.yaw, 0);
      this.rig.scale.setScalar(XR_FEET_PER_METRE); scene.add(this.rig); this.rig.add(camera);
      camera.position.set(0, 0, 0); camera.quaternion.identity(); camera.scale.set(1, 1, 1);
      camera.near = .03; camera.far = far / XR_FEET_PER_METRE; camera.updateProjectionMatrix();
      renderer.xr.enabled = true; renderer.xr.setReferenceSpaceType('local-floor');
      this.createControls();
      await renderer.xr.setSession(session);
      if (this.disposed || this.session !== session) { await session.end(); this.onEnd(); return; }
      this.active = true; this.firstPose = true; this.armed = false;
      this.snap = new SnapTurn(); this.previous.clear(); this.options.state(true);
    } catch (error) {
      if (session) await session.end().catch(() => {});
      this.onEnd(); throw error;
    } finally { this.pending = false; }
  }
  async exit() { await this.session?.end(); }
  private onEnd = () => {
    this.session?.removeEventListener('end', this.onEnd); this.session = null;
    this.active = false;
    for (const item of this.controllers) {
      item.object.removeEventListener('select', item.select); item.object.remove(item.line);
      this.rig.remove(item.object); item.line.geometry.dispose(); (item.line.material as THREE.Material).dispose();
    }
    this.controllers = [];
    if (this.panel) { this.overlay.remove(this.panel); this.panel.geometry.dispose(); this.panel.material.map?.dispose(); this.panel.material.dispose(); }
    this.panel = null; this.canvas = null;
    this.saved?.(); this.saved = null; this.options.scene.remove(this.rig);
    if (!this.disposed) { this.options.restore(); this.options.state(false); }
  };
  dispose() { this.disposed = true; void this.session?.end().catch(() => {}); this.onEnd(); }
  private createControls() {
    this.canvas = document.createElement('canvas'); this.canvas.width = 1024; this.canvas.height = 320;
    const texture = new THREE.CanvasTexture(this.canvas); texture.colorSpace = THREE.SRGBColorSpace;
    this.panel = new THREE.Mesh(new THREE.PlaneGeometry(.9, .28), new THREE.MeshBasicMaterial({ map: texture, transparent: true, depthTest: false, depthWrite: false, toneMapped: false }));
    this.panel.matrixAutoUpdate = false; this.overlay.add(this.panel); this.selection = ''; this.drawPanel();
    for (let index = 0; index < 2; index++) {
      const object = this.options.renderer.xr.getController(index);
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3(0, 0, -3)]), new THREE.LineBasicMaterial({ color: '#38bdf8' }));
      object.add(line); this.rig.add(object);
      const select = () => { if (this.active && this.armed && this.session?.visibilityState === 'visible') this.select(object); };
      object.addEventListener('select', select); this.controllers.push({ object, line, select });
    }
  }
  private drawPanel() {
    const ctx = this.canvas?.getContext('2d'); if (!ctx || !this.panel) return;
    ctx.clearRect(0, 0, 1024, 320); ctx.fillStyle = 'rgba(15,23,42,.93)'; ctx.fillRect(0, 0, 1024, 320);
    ctx.fillStyle = 'white'; ctx.font = 'bold 36px sans-serif'; ctx.fillText('BIMaestro · Visite VR', 28, 48);
    ctx.font = '26px sans-serif';
    ['Stick gauche : marcher · stick droit : rotation par crans', 'Gâchette : inspecter · A : départ · B : quitter', this.selection.slice(0, 68) || 'Visez un objet avec la gâchette pour l’inspecter'].forEach((text, index) => ctx.fillText(text, 28, 96 + index * 42));
    ctx.fillStyle = '#1e40af'; ctx.fillRect(16, 240, 485, 64); ctx.fillStyle = '#334155'; ctx.fillRect(523, 240, 485, 64);
    ctx.fillStyle = 'white'; ctx.fillText('Revenir au départ', 110, 282); ctx.fillText('Quitter la VR', 650, 282);
    this.panel.material.map!.needsUpdate = true;
  }
  private select(object: THREE.Object3D) {
    const ray = new THREE.Ray(object.getWorldPosition(new THREE.Vector3()), object.getWorldDirection(new THREE.Vector3()).negate());
    // Cameras look along -Z, unlike controller Object3D.getWorldDirection.
    if (object === this.options.camera) ray.direction.negate();
    if (this.panel) {
      const hit = new THREE.Raycaster(ray.origin, ray.direction).intersectObject(this.panel)[0];
      if (hit?.uv && hit.uv.y < .25) { if (hit.uv.x < .5) this.recenter(); else void this.exit(); return; }
    }
    this.selection = this.options.pick(ray); this.drawPanel();
  }
  private recenter() {
    this.rig.rotation.y = this.yaw; this.rig.position.copy(this.home); this.rig.updateMatrixWorld(true);
    const head = this.options.camera.getWorldPosition(new THREE.Vector3());
    this.rig.position.x += this.home.x - head.x; this.rig.position.z += this.home.z - head.z;
    this.rig.updateMatrixWorld(true); this.armed = false; this.snap = new SnapTurn();
  }
  private move(dx: number, dz: number) {
    if (!dx && !dz) return;
    const head = this.options.camera.getWorldPosition(new THREE.Vector3()), candidate = head.clone();
    candidate.x += dx; candidate.z += dz; candidate.y = this.rig.position.y;
    if (!this.options.ready(candidate)) return;
    const objects = this.options.objects(), ray = new THREE.Raycaster();
    const distance = Math.hypot(dx, dz), direction = new THREE.Vector3(dx, 0, dz).normalize();
    for (const height of [.3, .9, 1.4]) {
      ray.set(new THREE.Vector3(head.x, candidate.y + height * XR_FEET_PER_METRE, head.z), direction);
      ray.far = distance + .2 * XR_FEET_PER_METRE;
      if (ray.intersectObjects(objects, false).length) return;
    }
    ray.set(new THREE.Vector3(candidate.x, candidate.y + .35 * XR_FEET_PER_METRE, candidate.z), new THREE.Vector3(0, -1, 0));
    ray.far = .65 * XR_FEET_PER_METRE;
    const ground = ray.intersectObjects(objects, false).find(hit => hit.face && hit.face.normal.clone().transformDirection(hit.object.matrixWorld).y > .45);
    if (!ground) return;
    this.rig.position.x += dx; this.rig.position.z += dz; this.rig.position.y = ground.point.y + .02;
    this.rig.updateMatrixWorld(true);
  }
  step(delta: number) {
    if (!this.active || !this.session) return;
    const { renderer, camera } = this.options;
    const space = renderer.xr.getReferenceSpace(), frame = renderer.xr.getFrame();
    if (!space || !frame?.getViewerPose(space)) { this.armed = false; return; }
    renderer.xr.updateCamera(camera);
    if (this.firstPose) { this.firstPose = false; this.recenter(); }
    const sources = Array.from(this.session.inputSources);
    const left = sources.find(s => s.handedness === 'left' && s.gamepad?.mapping === 'xr-standard');
    const right = sources.find(s => s.handedness === 'right' && s.gamepad?.mapping === 'xr-standard');
    const visible = this.session.visibilityState === 'visible' && !document.hidden;
    const pad = this.pad.read(readBrowserPads(), visible);
    const [nativeX, nativeY] = left ? xrStick(left) : [0, 0];
    const [nativeTurn] = right ? xrStick(right) : [0];
    const nativeActive = !!(nativeX || nativeY || nativeTurn || sources.some(s => s.gamepad?.buttons.some(b => b.pressed)));
    const [mx, my, turn] = nativeActive ? [nativeX, nativeY, nativeTurn] : [pad.moveX, pad.moveY, pad.lookX];
    let reset = false, exit = false;
    for (const source of sources) {
      const buttons = source.gamepad?.buttons.map(b => b.pressed) || [], before = this.previous.get(source);
      if (before && source.handedness === 'right') { reset ||= !!buttons[4] && !before[4]; exit ||= !!buttons[5] && !before[5]; }
      this.previous.set(source, buttons);
    }
    for (const source of this.previous.keys()) if (!sources.includes(source)) this.previous.delete(source);
    const neutral = !mx && !my && !turn && !sources.some(s => s.gamepad?.buttons.some(b => b.pressed)) && !pad.held.some(Boolean);
    if (!visible) this.armed = false;
    if (!this.armed && visible && neutral) this.armed = true;
    if (visible && this.armed) {
      if (exit || pad.tapped[xbox.b]) { void this.exit(); return; }
      if (reset || pad.tapped[xbox.a]) this.recenter();
      if (pad.tapped[xbox.rt]) this.select(camera);
      turnAboutHead(this.rig, camera, this.snap.read(turn));
      const forward = camera.getWorldDirection(new THREE.Vector3()); forward.y = 0; forward.normalize();
      const rightward = new THREE.Vector3(-forward.z, 0, forward.x);
      const offset = forward.multiplyScalar(-my).addScaledVector(rightward, mx).multiplyScalar(1.3 * XR_FEET_PER_METRE * Math.min(delta, .05));
      this.move(offset.x, 0); this.move(0, offset.z);
    }
    if (this.panel) {
      camera.updateWorldMatrix(true, false);
      this.panel.matrix.copy(camera.matrixWorld).multiply(new THREE.Matrix4().makeTranslation(0, -.3, -1.2));
      this.panel.updateMatrixWorld(true);
    }
  }
  renderOverlay() { if (this.active) this.options.renderer.render(this.overlay, this.options.camera); }
}
