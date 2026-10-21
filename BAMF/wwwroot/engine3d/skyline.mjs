// The usage skyline: beside each device a tower as tall as what it has moved over the window picked, cyan for what it took in and
// magenta for what it sent out, with the total at the top. The sums and heights are worked out in data.mjs (usageOf); this draws them.
// The towers stand in the scene, not on the devices, so they don't turn with them.
import * as THREE from "three";
import { CSS2DObject } from "three/addons/renderers/CSS2DRenderer.js";
import { formatBytes } from "./data.mjs";

const CYAN = 0x26d9ff, MAGENTA = 0xff2fd0;
const UP = new THREE.Color(MAGENTA);
const MAX_H = 6.2;               // the tallest tower, in scene units, on a platform

export function createSkyline({ scene, ents, glow, reduced = false }) {
  const boxGeo = new THREE.BoxGeometry(1, 1, 1);
  const edgeGeo = new THREE.EdgesGeometry(boxGeo);
  const towers = new Map();       // ent key -> { group, body, edges, cap, el, label, h, key }
  let usage = null, on = false;

  function make(key) {
    const group = new THREE.Group();
    const body = new THREE.Mesh(boxGeo, new THREE.MeshBasicMaterial({ color: CYAN, transparent: true, opacity: 0.2, blending: THREE.AdditiveBlending, depthWrite: false }));
    const edges = new THREE.LineSegments(edgeGeo, new THREE.LineBasicMaterial({ color: CYAN, transparent: true, opacity: 0.95 }));
    const cap = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: CYAN, transparent: true, opacity: 0.8, blending: THREE.AdditiveBlending, depthWrite: false }));
    cap.scale.setScalar(0.9);
    const el = document.createElement("div"); el.className = "v3d-lbl v3d-tw";
    const label = new CSS2DObject(el);
    group.add(body, edges, cap, label);
    scene.add(group);
    const t = { group, body, edges, cap, el, label, h: 0, key };
    towers.set(key, t);
    return t;
  }
  function drop(t) {
    scene.remove(t.group); t.el.remove(); t.body.material.dispose(); t.edges.material.dispose(); t.cap.material.dispose(); towers.delete(t.key);
  }

  /** The usage to draw (from usageOf), or null; and whether the layer is on. */
  function set(u, enabled) {
    usage = u; on = !!enabled && !!u;
    if (!on) for (const t of [...towers.values()]) drop(t);
  }

  const tc = new THREE.Color();
  function tick(time, dt, houseK, top) {
    if (!on) return;
    const k = 1 - Math.exp(-3 * dt);
    const topKeys = new Set((top || []).map(r => r.key));
    for (const [key, row] of usage.byKey) {
      const e = ents.get(key);
      if (!e || e.leaving || !e.group.visible) { const t = towers.get(key); if (t) t.group.visible = false; continue; }
      const t = towers.get(key) || make(key);
      t.group.visible = true;
      const want = row.height * MAX_H * houseK;
      t.h += (want - t.h) * (reduced ? 1 : k);
      const h = Math.max(0.05, t.h), w = 0.2 + row.height * 0.16;
      // beside the device, outside its pad, standing on the platform; the same place whichever way the device turns
      const p = e.group.position, x = p.x + 1.05 * houseK, z = p.z - 0.3 * houseK;
      t.group.position.set(x, p.y - 0.62 + (e.d.state === "off" ? 0.28 : 0) + h / 2 - 0.03, z);
      t.body.scale.set(w, h, w); t.edges.scale.set(w, h, w);
      tc.setHex(CYAN).lerp(UP, Math.min(1, row.upShare * 1.4));
      t.body.material.color.copy(tc); t.edges.material.color.copy(tc).multiplyScalar(1.5); t.cap.material.color.copy(tc).multiplyScalar(1.5);
      t.cap.position.set(0, h / 2 + 0.1, 0);
      t.cap.material.opacity = 0.55 + (reduced ? 0 : 0.2 * Math.sin(time * 2.2 + h));
      t.label.visible = topKeys.has(key) && e.group.visible && !e.dim;
      t.label.position.set(0, h / 2 + 0.5, 0);
      t.el.textContent = formatBytes(row.total);
    }
    for (const t of [...towers.values()]) if (!usage.byKey.has(t.key) || !ents.has(t.key)) drop(t);
  }

  function dispose() { for (const t of [...towers.values()]) drop(t); boxGeo.dispose(); edgeGeo.dispose(); }

  return { set, tick, dispose, get count() { return towers.size; }, get on() { return on; }, get _towers() { return towers; } };
}
