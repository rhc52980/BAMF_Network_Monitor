// What the 3D tab draws on top of the devices: a tower for each service watched on a device, satellites for the places
// devices talk to on the internet, and beams for a device that is scanning. Each is a list of small things that follow
// the entities scene.mjs already keeps; the data side (which slot a satellite takes) is in data.mjs.
import * as THREE from "three";
import { CSS2DObject } from "three/addons/renderers/CSS2DRenderer.js";
import { worstService, satelliteSlots } from "./data.mjs";

const SERVICE_COLORS = { up: 0x3fdb7f, slow: 0xffb454, down: 0xff4b5c, off: 0x5d6f86, waiting: 0x5d6f86 };
const CYAN = 0x26d9ff, MAGENTA = 0xff2fd0, RED = 0xff4b5c;
const MAX_BEAMS = 64;

export function createExtras({ scene, glow, internet, ents, onCue = () => {}, reduced = false }) {
  // ---------------------------------------------------------------- services: a tower each, beside its device
  const towerGeo = new THREE.CylinderGeometry(0.045, 0.045, 0.62, 6, 1, false);
  const capGeo = new THREE.SphereGeometry(0.075, 8, 6);
  const towers = new Map();      // ent key -> { sig, group, items: [{ mesh, cap, state }] }

  function setServices(on) {
    for (const e of ents.values()) {
      const t = towers.get(e.key);
      const list = on && e.d && !e.leaving ? (e.d.services || []).slice(0, 4) : [];
      const sig = list.map(s => s.name + ":" + s.state).join("|");
      if (t && t.sig === sig) continue;
      if (t) { e.group.remove(t.group); t.items.forEach(i => { i.mesh.material.dispose(); i.cap.material.dispose(); }); towers.delete(e.key); }
      if (!list.length) continue;
      const g = new THREE.Group();
      const items = list.map((s, i) => {
        const col = new THREE.Color(SERVICE_COLORS[s.state] ?? SERVICE_COLORS.up).multiplyScalar(s.state === "down" ? 1.8 : 1.4);
        const mesh = new THREE.Mesh(towerGeo, new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: s.state === "off" || s.state === "waiting" ? 0.35 : 0.8 }));
        const cap = new THREE.Mesh(capGeo, new THREE.MeshBasicMaterial({ color: col }));
        const a = (i / Math.max(3, list.length)) * Math.PI * 0.9 + Math.PI * 0.55;       // a fan round the back of the device
        const r = 0.95;
        const x = Math.cos(a) * r, z = Math.sin(a) * r;
        mesh.position.set(x, -0.1, z); cap.position.set(x, 0.24, z);
        g.add(mesh, cap);
        return { mesh, cap, state: s.state, phase: i * 1.7 };
      });
      e.group.add(g);
      towers.set(e.key, { sig, group: g, items });
    }
    // devices that are gone
    for (const [k, t] of towers) if (!ents.has(k)) { towers.delete(k); t.items.forEach(i => { i.mesh.material.dispose(); i.cap.material.dispose(); }); }
  }

  // ---------------------------------------------------------------- destinations: satellites round the internet
  const sats = new Map();        // net -> { group, line, label, ring, slot, born }
  const satGeo = new THREE.OctahedronGeometry(0.17, 0);
  const satEdges = new THREE.EdgesGeometry(satGeo);

  function setDestinations(dests, nowMs, on) {
    const slots = on ? satelliteSlots(dests, nowMs) : [];
    const keep = new Set(slots.map(s => s.net));
    for (const [net, s] of sats) if (!keep.has(net)) { scene.remove(s.group); s.el.remove(); s.line.material.dispose(); s.glow.material.dispose(); sats.delete(net); }
    for (const slot of slots) {
      let s = sats.get(slot.net);
      if (!s) {
        const group = new THREE.Group();
        const col = new THREE.Color(slot.fresh ? MAGENTA : CYAN).multiplyScalar(1.6);
        const line = new THREE.LineSegments(satEdges, new THREE.LineBasicMaterial({ color: col, transparent: true, opacity: 0.95 }));
        const spr = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: col, transparent: true, opacity: 0.6, blending: THREE.AdditiveBlending, depthWrite: false }));
        spr.scale.setScalar(0.9);
        const el = document.createElement("div");
        const label = new CSS2DObject(el); label.position.set(0, 0.35, 0);
        group.add(line, spr, label);
        scene.add(group);
        s = { group, line, glow: spr, label, el, born: performance.now(), flashed: false };
        sats.set(slot.net, s);
        if (slot.fresh) onCue("destination");
      }
      s.slot = slot;
      s.el.className = "v3d-lbl net" + (slot.fresh ? " odd" : "");
      s.el.textContent = slot.net + (slot.fresh ? "  · new" : "");
      s.label.visible = slot.fresh || slots.length <= 3;       // a name is only worth the clutter on what is new
      const c = new THREE.Color(slot.fresh ? MAGENTA : CYAN).multiplyScalar(1.6);
      s.line.material.color.copy(c); s.glow.material.color.copy(c);
    }
  }

  // ---------------------------------------------------------------- beams: from a satellite to a device it is talking to, and a scanner's rays
  const beamPos = new Float32Array(MAX_BEAMS * 2 * 3), beamCol = new Float32Array(MAX_BEAMS * 2 * 3);
  const beamGeo = new THREE.BufferGeometry();
  beamGeo.setAttribute("position", new THREE.BufferAttribute(beamPos, 3));
  beamGeo.setAttribute("color", new THREE.BufferAttribute(beamCol, 3));
  const beams = new THREE.LineSegments(beamGeo, new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false }));
  beams.frustumCulled = false; scene.add(beams);

  // ---------------------------------------------------------------- scans
  let scanning = [];             // [{ ent, at, what, count, target, nextAt }]
  const rays = [];               // short-lived: { from, to, born }
  function setScans(scans, nowMs, byHost, on) {
    const fresh = on ? scans.filter(s => nowMs - Date.parse(s.at) < 20 * 60000) : [];
    const next = [];
    for (const s of fresh) {
      const ent = byHost.get(s.hostId);
      if (!ent) continue;
      const known = scanning.find(x => x.ent === ent);
      next.push(known ? { ...known, count: s.count, what: s.what, target: s.target } : { ent, at: Date.parse(s.at), what: s.what, count: s.count, target: s.target, nextAt: 0, ring: null });
      if (!known) onCue("scan");
    }
    for (const old of scanning) if (!next.find(x => x.ent === old.ent) && old.ring) { old.ent.group.remove(old.ring); old.ring.geometry.dispose(); old.ring.material.dispose(); }
    scanning = next;
  }
  const isScanner = ent => scanning.some(s => s.ent === ent);

  // ---------------------------------------------------------------- the clock
  const tmpA = new THREE.Vector3(), tmpB = new THREE.Vector3(), tc = new THREE.Color();
  function tick(time, dt, now, prefs) {
    // towers
    for (const t of towers.values()) for (const i of t.items) {
      if (i.state === "down") {
        const flick = reduced ? 1 : 0.55 + 0.45 * Math.abs(Math.sin(time * 9 + i.phase) * Math.sin(time * 3.1 + i.phase));
        i.cap.material.opacity = 1; i.mesh.material.opacity = 0.45 + flick * 0.5; i.cap.scale.setScalar(1 + (reduced ? 0 : 0.35 * Math.sin(time * 6 + i.phase)));
      } else if (i.state === "slow") i.cap.scale.setScalar(1 + (reduced ? 0 : 0.18 * Math.sin(time * 3 + i.phase)));
    }
    // satellites
    let nb = 0;
    const origin = internet.position;
    const worstDevice = new Map();
    for (const s of sats.values()) {
      const o = s.slot; if (!o) continue;
      const a = o.phase + time * o.speed;
      const x = Math.cos(a) * o.radius, z = Math.sin(a) * o.radius;
      s.group.position.set(origin.x + x, origin.y + Math.sin(a) * o.radius * Math.sin(o.tilt) * 0.7 + 0.2, origin.z + z * Math.cos(o.tilt));
      s.line.rotation.y += dt * 0.8; s.line.rotation.x += dt * 0.5;
      s.glow.material.opacity = (o.hot ? 0.7 : 0.35) + 0.12 * Math.sin(time * 2 + o.phase);
      if (o.fresh && !s.flashed) { s.flashed = true; s.glow.scale.setScalar(2.6); }
      s.glow.scale.setScalar(Math.max(0.9, s.glow.scale.x - dt * 1.2));
      if (!o.hot || !prefs.flow) continue;
      for (const hid of o.devices) {
        const e = [...ents.values()].find(x => x.d && x.d.id === hid);
        if (!e || e.leaving || !e.group.visible || e.d.state === "off" || nb >= MAX_BEAMS) continue;
        // a faint pulse along the beam: its brightness breathes, so it reads as a connection that's alive
        const k = 0.35 + 0.25 * Math.sin(time * 3 + o.phase);
        tc.set(o.fresh ? MAGENTA : CYAN).multiplyScalar(k);
        tmpA.copy(s.group.position); tmpB.copy(e.group.position);
        beamPos.set([tmpA.x, tmpA.y, tmpA.z, tmpB.x, tmpB.y, tmpB.z], nb * 6);
        beamCol.set([tc.r, tc.g, tc.b, tc.r * 0.3, tc.g * 0.3, tc.b * 0.3], nb * 6);
        nb++;
      }
    }
    // scans: a few rays a second from the scanner to others on its platform, fading as they go
    for (const sc of scanning) {
      const e = sc.ent;
      if (!e.group.visible) continue;
      if (!sc.ring) {
        sc.ring = new THREE.Mesh(new THREE.RingGeometry(0.92, 1, 64), new THREE.MeshBasicMaterial({ color: new THREE.Color(RED).multiplyScalar(1.8), transparent: true, depthWrite: false }));
        sc.ring.rotation.x = -Math.PI / 2; sc.ring.position.y = -0.5; e.group.add(sc.ring);
      }
      const t = (time * 0.9) % 1;
      sc.ring.scale.setScalar(0.7 + t * 2.4); sc.ring.material.opacity = (1 - t) * 0.9;
      if (now >= sc.nextAt && !reduced) {
        sc.nextAt = now + 110;
        const peers = [...ents.values()].filter(x => x !== e && !x.leaving && x.group.visible && x.d.net === e.d.net);
        if (peers.length) rays.push({ from: e, to: peers[Math.floor(Math.random() * peers.length)], born: now });
      }
    }
    for (let i = rays.length - 1; i >= 0; i--) {
      const r = rays[i], age = (now - r.born) / 600;
      if (age >= 1 || r.from.leaving || r.to.leaving || !ents.has(r.from.key) || !ents.has(r.to.key)) { rays.splice(i, 1); continue; }
      if (nb >= MAX_BEAMS) continue;
      tc.set(RED).multiplyScalar(1.6 * (1 - age));
      tmpA.copy(r.from.group.position); tmpB.copy(r.to.group.position);
      beamPos.set([tmpA.x, tmpA.y, tmpA.z, tmpB.x, tmpB.y, tmpB.z], nb * 6);
      beamCol.set([tc.r, tc.g, tc.b, tc.r, tc.g, tc.b], nb * 6);
      nb++;
    }
    beamGeo.setDrawRange(0, nb * 2);
    beamGeo.attributes.position.needsUpdate = true; beamGeo.attributes.color.needsUpdate = true;
  }

  function dispose() {
    for (const [, t] of towers) t.items.forEach(i => { i.mesh.material.dispose(); i.cap.material.dispose(); });
    towers.clear();
    for (const s of sats.values()) { scene.remove(s.group); s.el.remove(); s.line.material.dispose(); s.glow.material.dispose(); }
    sats.clear();
    scene.remove(beams); beamGeo.dispose(); beams.material.dispose();
    towerGeo.dispose(); capGeo.dispose(); satGeo.dispose(); satEdges.dispose();
  }

  return { setServices, setDestinations, setScans, tick, dispose, isScanner, get satellites() { return sats.size; }, get scanners() { return scanning.length; }, worstService };
}
