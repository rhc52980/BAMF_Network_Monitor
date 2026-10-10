// The house: the floor plans you drew (walls, doors, windows, labels) or uploaded (a picture for the floor) stood up as
// glowing outlines, one storey per plan, so every device can sit in the room it was placed in. The geometry maths is in
// data.mjs (houseOf, placeInHouse); this only builds and disposes the drawing.
import * as THREE from "three";
import { CSS2DObject } from "three/addons/renderers/CSS2DRenderer.js";
import * as BufferGeometryUtils from "three/addons/utils/BufferGeometryUtils.js";
import { houseOf, placeInHouse, WALL_HEIGHT, FLOOR_GAP } from "./data.mjs";

const CYAN = 0x26d9ff, AMBER = 0xffb454;

/** A plan-shaped description of an uploaded picture, so it can be placed on the same footing: its longest side is 16 units. */
export function planForImage(floor) {
  const w = floor.width || 1000, h = floor.height || 700;
  return { v: 1, width: w, height: h, unit: "ft", step: Math.max(w, h) / 16, perStep: 2, items: [] };
}

/**
 * floors: [{ id, name, kind: "plan" | "image", plan, imageUrl }] bottom floor first.
 * Returns { group, spots(floorId) -> fn(x, y) -> {x, y, z}, bounds, floors, dispose }.
 */
export function buildHouse(floors) {
  const group = new THREE.Group();
  const disposables = [];
  const keep = o => { disposables.push(o); return o; };
  const spots = new Map();
  let min = [Infinity, Infinity], max = [-Infinity, -Infinity];

  floors.forEach((f, index) => {
    const plan = f.plan, h = houseOf(plan), y = index * FLOOR_GAP;
    const floor = new THREE.Group();
    floor.position.y = y;
    // the slab, with the plan's own centre at the origin of the floor
    const slabGeo = keep(new THREE.PlaneGeometry(Math.max(8, h.size[0] + 4), Math.max(6, h.size[1] + 4)));
    const slab = new THREE.Mesh(slabGeo, keep(new THREE.MeshBasicMaterial({ color: 0x040a18, transparent: true, opacity: 0.86, depthWrite: false, side: THREE.DoubleSide })));
    slab.rotation.x = -Math.PI / 2; slab.position.set(h.center[0], -0.62, h.center[1]);
    floor.add(slab);
    const rim = new THREE.LineSegments(keep(new THREE.EdgesGeometry(slabGeo)), keep(new THREE.LineBasicMaterial({ color: new THREE.Color(CYAN).multiplyScalar(1.0), transparent: true, opacity: 0.8 })));
    rim.rotation.x = -Math.PI / 2; rim.position.set(h.center[0], -0.61, h.center[1]);
    floor.add(rim);
    if (f.imageUrl) {
      const tex = keep(new THREE.TextureLoader().load(f.imageUrl));
      tex.colorSpace = THREE.SRGBColorSpace;
      const pic = new THREE.Mesh(keep(new THREE.PlaneGeometry(plan.width * h.scale, plan.height * h.scale)), keep(new THREE.MeshBasicMaterial({ map: tex, transparent: true, opacity: 0.55, depthWrite: false, side: THREE.DoubleSide })));
      pic.rotation.x = -Math.PI / 2; pic.position.y = -0.6;
      floor.add(pic);
    }
    // walls: one box each, merged, with their outline on top
    if (h.walls.length) {
      const boxes = h.walls.map(wl => {
        const dx = wl.b[0] - wl.a[0], dz = wl.b[1] - wl.a[1], len = Math.hypot(dx, dz) || 0.01;
        const g = new THREE.BoxGeometry(len + 0.12, WALL_HEIGHT, 0.12);
        g.rotateY(-Math.atan2(dz, dx));
        g.translate((wl.a[0] + wl.b[0]) / 2, WALL_HEIGHT / 2 - 0.62, (wl.a[1] + wl.b[1]) / 2);
        return g.toNonIndexed();
      });
      const merged = BufferGeometryUtils.mergeGeometries(boxes, false);
      boxes.forEach(b => b.dispose());
      keep(merged);
      const body = new THREE.Mesh(merged, keep(new THREE.MeshBasicMaterial({ color: 0x0a2a44, transparent: true, opacity: 0.34, depthWrite: false })));
      const edges = new THREE.LineSegments(keep(new THREE.EdgesGeometry(merged, 25)), keep(new THREE.LineBasicMaterial({ color: new THREE.Color(CYAN).multiplyScalar(1.2), transparent: true, opacity: 0.6 })));
      floor.add(body, edges);
    }
    // doors on the floor, windows a little up: lines in the same coordinates
    const marks = (list, yy, color) => {
      if (!list.length) return;
      const pos = [];
      for (const m of list) pos.push(m.a[0], yy, m.a[1], m.b[0], yy, m.b[1]);
      const geo = keep(new THREE.BufferGeometry());
      geo.setAttribute("position", new THREE.Float32BufferAttribute(pos, 3));
      floor.add(new THREE.LineSegments(geo, keep(new THREE.LineBasicMaterial({ color: new THREE.Color(color).multiplyScalar(1.6) }))));
    };
    marks(h.doors, -0.58, AMBER);
    marks(h.windows, WALL_HEIGHT * 0.5 - 0.62, 0x8fd8ff);
    // room names
    for (const lb of h.labels) {
      const el = document.createElement("div"); el.className = "v3d-lbl net"; el.textContent = lb.t;
      const o = new CSS2DObject(el); o.position.set(lb.p[0], -0.4, lb.p[1]); floor.add(o);
    }
    const nameEl = document.createElement("div"); nameEl.className = "v3d-lbl gw"; nameEl.textContent = f.name;
    const name = new CSS2DObject(nameEl); name.position.set(h.center[0], 0.2, h.max[1] + 1.4); floor.add(name);
    group.add(floor);
    spots.set(f.id, (px, py) => placeInHouse({ x: px, y: py }, plan, index));
    min = [Math.min(min[0], h.min[0] - 1), Math.min(min[1], h.min[1] - 1)];
    max = [Math.max(max[0], h.max[0] + 1), Math.max(max[1], h.max[1] + 1)];
  });

  return {
    group, spots,
    bounds: { min, max, width: max[0] - min[0], depth: max[1] - min[1], cx: (min[0] + max[0]) / 2, cz: (min[1] + max[1]) / 2, height: Math.max(0, (floors.length - 1) * FLOOR_GAP) },
    spot(floorId, x, y) { const f = spots.get(floorId); return f ? f(x, y) : null; },
    dispose() {
      group.traverse(o => { if (o.isCSS2DObject && o.element) o.element.remove(); });
      disposables.forEach(o => o.dispose && o.dispose());
    },
  };
}
