// The grid: a screen saver that is the network seen as a place at night. A glowing platform for each network with its gateway at the
// centre, every other device a hologram of what it is, coloured by state, and the camera drifting over it all: a wide orbit, a low pass
// among the devices, a flight across the platforms, a look straight down. It keeps what the watchtower does:
//   a scan is a ring of light rolling out across each platform, lighting every device as it passes;
//   a new device that nobody has marked known is an alarm: the night goes red, the camera swings round to it and circles it, a reticle
//   locks on and a callout gives its details, until it's dealt with (the saver's own buttons sit under the callout);
//   a watched device dropping is LOST CONTACT, and coming back is REACQUIRED.
// Drawn with three.js and loaded only when the saver starts. Everything it knows comes in through the options, so it has no idea
// what a host record looks like and the dashboard's own scripts stay in charge of the data.
import * as THREE from "three";
import { EffectComposer } from "three/addons/postprocessing/EffectComposer.js";
import { RenderPass } from "three/addons/postprocessing/RenderPass.js";
import { UnrealBloomPass } from "three/addons/postprocessing/UnrealBloomPass.js";
import { OutputPass } from "three/addons/postprocessing/OutputPass.js";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import * as BufferGeometryUtils from "three/addons/utils/BufferGeometryUtils.js";
import { MODELS, slotsFor, placeNetworks, buildScene } from "./data.mjs";
import { T, SKIN, glowTexture, makeSky, makeFloor, makePlatformFx, edgeMaterial, bodyMaterial, padMaterial, makeDust, makeScreenPass } from "./fx.mjs";

/** The looks. Neon keeps the sky and floor as they are; Terminal drains them to one green. */
export const SKINS = {
  neon: { on: 0x3fdb7f, unk: 0xffb454, off: 0x5d6f86, odd: 0xb36cff, accent: 0x26d9ff, accent2: 0xff2fd0, link: 0x147a9a, fog: 0x070a1c, bg: 0x03050c, mono: 0, tint: [1, 1, 1] },
  terminal: { on: 0x57ff8c, unk: 0xd9ff55, off: 0x2c6a45, odd: 0xb8ff3a, accent: 0x2dff7a, accent2: 0x9affc0, link: 0x16a04a, fog: 0x03140a, bg: 0x010804, mono: 1, tint: [0.22, 1, 0.42] },
};
const ALARM = 0xff3b30;
const STATE_GLOW = { on: 1.9, unk: 1.35, off: 1.45, odd: 1.6 };   // amber and purple read hotter than green; each state is set to look as bright as the others
const WAVE = 5.5;     // how fast the scan's ring runs out, in units a second
const DENSITY_REF = 34, LITE_AT = 150, MAX_PARTICLES = 600, SHOT_SECS = 42, FPS = 36;
const ease = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
const fract = x => x - Math.floor(x);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

/** Where a lot of line is packed into a small model the glow adds up and it blazes, so its brightness is eased down. */
function densityGain(edgeGeo, size) {
  const p = edgeGeo.attributes.position; let len = 0;
  for (let i = 0; i + 1 < p.count; i += 2) len += Math.hypot(p.getX(i) - p.getX(i + 1), p.getY(i) - p.getY(i + 1), p.getZ(i) - p.getZ(i + 1));
  const d = len / (size * size) || 1;
  return Math.min(1.15, Math.max(0.28, Math.pow(DENSITY_REF / d, 0.7)));
}

/** One model: a single merged, centred geometry, sized for its platform. */
async function loadModel(loader, base, kind) {
  const gltf = await loader.loadAsync(`${base}/models/${kind}.glb`);
  gltf.scene.updateMatrixWorld(true);
  const parts = [];
  gltf.scene.traverse(o => {
    if (!o.isMesh) return;
    let g = o.geometry.clone().applyMatrix4(o.matrixWorld);
    if (g.index) g = g.toNonIndexed();
    const p = new THREE.BufferGeometry(); p.setAttribute("position", g.attributes.position); parts.push(p);
  });
  const g = BufferGeometryUtils.mergeGeometries(parts, false);
  g.computeBoundingBox();
  const size = g.boundingBox.getSize(new THREE.Vector3()), c = g.boundingBox.getCenter(new THREE.Vector3());
  g.translate(-c.x, -c.y, -c.z);
  const k = MODELS[kind] / Math.max(size.x, size.y, size.z); g.scale(k, k, k);
  return g;
}

/**
 * options: { base, skin, calm, siren,
 *   hosts(): the devices to show; kindOf(h); nameOf(h); gatewayIp(subnet); rateOf(h); unusual(): Promise<[{hostId, kind, open, title, detail}]>
 *   waiting(): { h, test, count } for the new-device alert to hold, or null
 *   describe(h): { name, line1, line2, line3 } for the callout
 *   onBox({ x, y, w, id, test, count } | null): where the saver's own buttons go }
 */
export async function createGridSaver(container, o) {
  const base = o.base || "/engine3d", calm = !!o.calm, P = SKINS[o.skin] || SKINS.neon;
  const COLORS = { on: P.on, unk: P.unk, off: P.off, odd: P.odd };
  SKIN.mono.value = P.mono; SKIN.tint.value.setRGB(...P.tint);

  container.innerHTML = `<div class="g3d-stage"></div><div class="g3d-hud"><div class="g3d-alarm"></div>
    <div class="g3d-frame"><i></i><i></i><i></i><i></i></div><div class="g3d-banner"></div><div class="g3d-sweep">SWEEP</div>
    <div class="g3d-ret"><b></b></div><div class="g3d-call"><div class="g3d-call-h"></div><div class="g3d-call-n"></div><div class="g3d-call-a"></div><div class="g3d-call-m"></div><div class="g3d-call-m"></div></div>
    <div class="g3d-labels"></div></div>`;
  const q = s => container.querySelector(s);
  const stage = q(".g3d-stage"), hud = q(".g3d-hud"), alarmEl = q(".g3d-alarm"), banner = q(".g3d-banner"), sweepEl = q(".g3d-sweep");
  const ret = q(".g3d-ret"), call = q(".g3d-call"), labelBox = q(".g3d-labels");

  let renderer;
  try { renderer = new THREE.WebGLRenderer({ antialias: false, powerPreference: "high-performance" }); }
  catch { throw new Error("This browser can't draw 3D (WebGL is off)."); }
  renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 1.5));
  stage.appendChild(renderer.domElement);
  renderer.domElement.setAttribute("role", "img");
  renderer.domElement.setAttribute("aria-label", "A 3D scene of your network, slowly turning.");

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(P.bg);
  scene.fog = new THREE.FogExp2(P.fog, 0.011);
  scene.add(makeSky());
  const camera = new THREE.PerspectiveCamera(52, 1, 0.1, 400);
  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));
  const bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), 0.7, 0.55, 0.26);
  const screenPass = makeScreenPass();
  composer.addPass(bloom); composer.addPass(screenPass); composer.addPass(new OutputPass());
  const glow = glowTexture();
  scene.add(makeFloor(-1.6));
  scene.add(makeDust(220, glow));

  // Models come as they're needed; a plain block stands in until one arrives.
  const loader = new GLTFLoader(), geos = {}, blocks = {}, fetching = new Set();
  const geoFor = kind => geos[kind] || (blocks[kind] ||= new THREE.BoxGeometry(MODELS[kind] || 1, 0.6, MODELS[kind] || 1));
  function want(kind) {
    if (geos[kind] || fetching.has(kind)) return;
    fetching.add(kind);
    loadModel(loader, base, kind).then(g => { geos[kind] = g; ents.forEach(e => { if (e.model === kind) applyModel(e); }); renderOnce(); }).catch(() => { /* stays a block */ });
  }

  // ---------- the internet, up top ----------
  const internet = new THREE.Group();
  const iGeo = new THREE.IcosahedronGeometry(0.75, 2), accent = new THREE.Color(P.accent), accent2 = new THREE.Color(P.accent2);
  const iCore = new THREE.Mesh(iGeo, new THREE.MeshBasicMaterial({ color: 0x020812, transparent: true, opacity: 0.9 }));
  const iEdges = new THREE.LineSegments(new THREE.EdgesGeometry(iGeo, 1), new THREE.LineBasicMaterial({ color: accent.clone().multiplyScalar(1.5), transparent: true, opacity: 0.9 }));
  const iAura = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: accent.clone().multiplyScalar(0.9), transparent: true, opacity: 0.55, blending: THREE.AdditiveBlending, depthWrite: false }));
  iAura.scale.setScalar(5.5);
  const iHalo = new THREE.Mesh(new THREE.TorusGeometry(1.2, 0.012, 6, 96), new THREE.MeshBasicMaterial({ color: accent.clone().multiplyScalar(1.5) }));
  const iHalo2 = new THREE.Mesh(new THREE.TorusGeometry(1.5, 0.01, 6, 96), new THREE.MeshBasicMaterial({ color: accent2.clone().multiplyScalar(1.3) }));
  iHalo.rotation.x = Math.PI / 2.4; iHalo2.rotation.x = Math.PI / 1.8; iHalo2.rotation.y = 0.6;
  internet.add(iCore, iEdges, iAura, iHalo, iHalo2);
  scene.add(internet);
  const internetTarget = new THREE.Vector3(0, 10, -1);

  // ---------- state ----------
  const nets = new Map(), ents = new Map();
  let links = [], parts = [], linkSig = "", current = null, firstUpdate = true, lite = false, reach = 14, rmax = 6, unusualItems = [];
  const particlePos = new Float32Array(MAX_PARTICLES * 3), particleCol = new Float32Array(MAX_PARTICLES * 3);
  const pGeo = new THREE.BufferGeometry();
  pGeo.setAttribute("position", new THREE.BufferAttribute(particlePos, 3)); pGeo.setAttribute("color", new THREE.BufferAttribute(particleCol, 3));
  const points = new THREE.Points(pGeo, new THREE.PointsMaterial({ size: 0.42, map: glow, vertexColors: true, transparent: true, opacity: 1, blending: THREE.AdditiveBlending, depthWrite: false, sizeAttenuation: true }));
  points.frustumCulled = false; scene.add(points);

  function makeNet(cidr) {
    const g = new THREE.Group();
    const disc = new THREE.Mesh(new THREE.CircleGeometry(1, 72), new THREE.MeshBasicMaterial({ color: new THREE.Color(P.bg).lerp(new THREE.Color(0x061224), 0.6), transparent: true, opacity: 0.88, depthWrite: false }));
    disc.rotation.x = -Math.PI / 2;
    const ring = new THREE.Mesh(new THREE.RingGeometry(0.985, 1, 96), new THREE.MeshBasicMaterial({ color: accent.clone().multiplyScalar(1.1), transparent: true, opacity: 0.9 }));
    ring.rotation.x = -Math.PI / 2;
    const fx = makePlatformFx(P.accent);
    // the scan's wave, a bright ring that runs out from the middle
    const wave = new THREE.Mesh(new THREE.RingGeometry(0.94, 1, 96), new THREE.MeshBasicMaterial({ color: accent.clone().multiplyScalar(2.2), transparent: true, opacity: 0, depthWrite: false, blending: THREE.AdditiveBlending }));
    wave.rotation.x = -Math.PI / 2; wave.position.y = 0.03; wave.visible = false;
    g.add(disc, ring, fx, wave);
    scene.add(g);
    const n = { cidr, group: g, disc, ring, fx, wave, center: new THREE.Vector3(), target: new THREE.Vector3(), R: 6, Rt: 6 };
    nets.set(cidr, n); return n;
  }
  function disposeNet(n) { scene.remove(n.group); [n.disc, n.ring, n.fx, n.wave].forEach(m => { m.geometry.dispose(); m.material.dispose(); }); }

  function makeEnt(d) {
    const e = { d, key: d.key, pos: new THREE.Vector3(), target: new THREE.Vector3(), phase: Math.random() * 6.28, col: new THREE.Color(COLORS[d.state]), scale: 1, leaving: 0, flash: 0, wave: 0, net: null, born: 0, glitch: 0, alarm: false };
    const g = new THREE.Group(), geo = geoFor(d.model);
    e.model = d.model; want(d.model);
    e.body = new THREE.Mesh(geo, bodyMaterial(e.col.clone()));
    e.edges = new THREE.LineSegments(new THREE.EdgesGeometry(geo, 28), edgeMaterial(e.col.clone()));
    e.dens = densityGain(e.edges.geometry, MODELS[d.model] || 1);
    e.pad = new THREE.Mesh(new THREE.CircleGeometry(1, 48), padMaterial(e.col.clone()));
    e.pad.rotation.x = -Math.PI / 2; e.pad.position.y = -(MODELS[d.model] || 1) / 2 - 0.02; e.pad.scale.setScalar(Math.max(0.8, (MODELS[d.model] || 1) * 0.62) * (d.gw ? 1.35 : 1));
    g.add(e.body, e.edges, e.pad);
    e.group = g; scene.add(g); ents.set(d.key, e);
    return e;
  }
  function applyModel(e) {
    want(e.model);
    e.edges.geometry.dispose();
    e.body.geometry = geoFor(e.model); e.edges.geometry = new THREE.EdgesGeometry(e.body.geometry, 28);
    e.dens = densityGain(e.edges.geometry, MODELS[e.model] || 1);
    if (e.ghosts) e.ghosts.forEach(m => { m.geometry = e.edges.geometry; });
    const size = MODELS[e.model] || 1;
    e.pad.position.y = -size / 2 - 0.02; e.pad.scale.setScalar(Math.max(0.8, size * 0.62) * (e.d.gw ? 1.35 : 1));
  }
  function dropEnt(e) {
    scene.remove(e.group); e.edges.geometry.dispose(); e.body.material.dispose(); e.edges.material.dispose(); e.pad.geometry.dispose(); e.pad.material.dispose();
    setBeacon(e, null); ents.delete(e.key);
  }
  // A beacon: a ripple on the ground, a beam, and two ghost outlines that jump apart now and then. Purple for something unusual, red for an alarm.
  function setBeacon(e, kind) {
    if (kind && (!e.beacon || e.beacon.kind !== kind)) { setBeacon(e, null); }
    if (kind && !e.beacon) {
      const c = new THREE.Color(kind === "alarm" ? ALARM : COLORS.odd);
      const ripple = new THREE.Mesh(new THREE.RingGeometry(0.92, 1, 64), new THREE.MeshBasicMaterial({ color: c.clone().multiplyScalar(1.8), transparent: true, depthWrite: false }));
      ripple.rotation.x = -Math.PI / 2; ripple.position.y = -0.55;
      const beam = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.03, 7, 6, 1, true), new THREE.MeshBasicMaterial({ color: c.clone().multiplyScalar(1.6), transparent: true, opacity: 0.45, blending: THREE.AdditiveBlending, depthWrite: false }));
      beam.position.y = 3.4; e.group.add(ripple, beam); e.beacon = { ripple, beam, kind };
      e.ghosts = [P.accent, P.accent2].map(col => { const m = new THREE.LineSegments(e.edges.geometry, new THREE.LineBasicMaterial({ color: new THREE.Color(col).multiplyScalar(1.4), transparent: true, opacity: 0.7, blending: THREE.AdditiveBlending, depthWrite: false })); m.visible = false; e.group.add(m); return m; });
    } else if (!kind && e.beacon) {
      e.group.remove(e.beacon.ripple, e.beacon.beam); e.beacon.ripple.geometry.dispose(); e.beacon.beam.geometry.dispose(); e.beacon = null;
      if (e.ghosts) { e.ghosts.forEach(m => { e.group.remove(m); m.material.dispose(); }); e.ghosts = null; }
    }
  }

  // ---------- links ----------
  function rebuildLinks(data) {
    links.forEach(l => { scene.remove(l.line); l.geo.dispose(); l.line.material.dispose(); });
    links = []; parts = [];
    const add = (aPos, e, curved) => {
      const N = curved ? 28 : 2, geo = new THREE.BufferGeometry();
      geo.setAttribute("position", new THREE.BufferAttribute(new Float32Array(N * 3), 3));
      const line = new THREE.Line(geo, new THREE.LineBasicMaterial({ color: P.link, transparent: true, opacity: curved ? 0.9 : 0.6, blending: THREE.AdditiveBlending, depthWrite: false }));
      scene.add(line);
      const L = { aPos, e, curved, N, geo, line, mid: new THREE.Vector3() }; links.push(L);
      const n = curved ? 5 : Math.max(1, Math.round((e.d.rate || 1) / 3));
      for (let i = 0; i < n; i++) parts.push({ L, t: Math.random(), dir: Math.random() < 0.5 ? 1 : -1 });
    };
    for (const net of data.nets) {
      const gw = net.gateway ? ents.get(net.gateway) : null, n = nets.get(net.id);
      if (gw) add(() => internet.position, gw, true);
      for (const d of net.devices) {
        const e = ents.get(d.key); if (!e || e === gw) continue;
        add(gw ? () => gw.group.position : () => n.group.position, e, false);
      }
    }
  }
  const pointOn = (L, t, out) => {
    const a = L.aPos(), b = L.e.group.position;
    if (!L.curved) return out.copy(a).lerp(b, t);
    const m = L.mid, u = 1 - t;
    return out.set(u * u * a.x + 2 * u * t * m.x + t * t * b.x, u * u * a.y + 2 * u * t * m.y + t * t * b.y, u * u * a.z + 2 * u * t * m.z + t * t * b.z);
  };

  // ---------- the data in ----------
  function update(data) {
    current = data;
    lite = data.devices.length > LITE_AT;
    bloom.enabled = !lite;
    const wanted = new Set(data.nets.map(n => n.id));
    for (const [cidr, n] of nets) if (!wanted.has(cidr)) { disposeNet(n); nets.delete(cidr); }
    for (const net of data.nets) if (!nets.has(net.id)) makeNet(net.id);
    const keys = new Set(data.devices.map(d => d.key));
    for (const e of [...ents.values()]) if (!keys.has(e.key) && !e.leaving) e.leaving = performance.now();
    for (const d of data.devices) {
      let e = ents.get(d.key);
      if (e && e.leaving) e.leaving = 0;
      if (!e) { e = makeEnt(d); if (!firstUpdate) e.born = performance.now(); }
      if (e.model !== d.model) { e.model = d.model; applyModel(e); }
      e.d = d; e.netId = d.net;
    }
    const placed = data.nets.map(net => ({ net, ...slotsFor(net.devices) }));
    const plan = placeNetworks(placed.map(p => p.radius), "side");
    placed.forEach((p, i) => {
      const n = nets.get(p.net.id), c = plan.centers[i];
      n.target.set(c.x, c.y, c.z); n.Rt = p.radius;
      for (const d of p.net.devices) {
        const e = ents.get(d.key), s = p.slots.get(d.key);
        e.target.set(c.x + (s ? s.x : 0), c.y, c.z + (s ? s.z : 0));
      }
    });
    internetTarget.set(0, 10 + Math.max(0, plan.reach - 16) * 0.25, -1);
    reach = plan.reach; rmax = Math.max(0, ...placed.map(p => p.radius));
    if (firstUpdate) {
      nets.forEach(n => { n.center.copy(n.target); n.R = n.Rt; });
      ents.forEach(e => e.pos.copy(e.target).add(new THREE.Vector3(0, calm ? 0 : 6 + Math.random() * 3, 0)));
      internet.position.copy(internetTarget);
    }
    const sig = data.nets.map(n => n.id + ":" + n.gateway + ":" + n.devices.map(d => d.key).join(",")).join("|");
    if (sig !== linkSig) { linkSig = sig; rebuildLinks(data); }
    firstUpdate = false;
    renderOnce();
  }
  async function refresh() {
    update(buildScene({ hosts: o.hosts(), kindOf: o.kindOf, nameOf: o.nameOf, unusual: unusualItems, gatewayIp: o.gatewayIp, rateOf: o.rateOf }));
  }
  async function refreshUnusual() { try { unusualItems = (await o.unusual()) || []; } catch { /* the beacons just stay as they were */ } }

  // ---------- what's going on ----------
  let cur = null, events = [], wave = null, waveId = 0, bannerUntil = 0;
  const hostKey = h => String(h.id);
  const entOf = h => ents.get(hostKey(h));
  function event(kind, data) {
    if (kind === "scan") { wave = { t0: performance.now(), id: ++waveId }; if (calm) { renderOnce(); } return; }
    if (kind === "change" && data) {
      const list = o.hosts();
      for (const id of data.off || []) { const h = list.find(x => x.id === id); if (h && h.watched) events.push({ kind: "lost", h, secs: 6 }); }
      for (const id of data.back || []) { const h = list.find(x => x.id === id); if (h && h.watched) events.push({ kind: "back", h, secs: 5 }); }
      refresh();
    }
    if (calm) { if (!cur && events.length) begin(events.shift(), performance.now()); renderOnce(); }
  }
  function begin(e, now) {
    cur = { ...e, t0: now };
    if (e.kind === "new") { refresh(); if (o.siren) o.siren(); }
    const ent = entOf(e.h);
    if (ent) { ent.glitch = e.kind === "new" ? 0 : 2.5; if (e.kind === "new") ent.born = now; }
    if (calm && e.kind !== "new") setTimeout(() => { cur = null; if (events.length) begin(events.shift(), performance.now()); renderOnce(); }, e.secs * 1000);
  }
  // A new device's alert holds until it's dealt with: the oldest waiting is the one shown.
  function checkAlerts(now) {
    const w = o.waiting();
    if (w && !(cur && cur.kind === "new" && cur.h.id === w.h.id)) begin({ kind: "new", h: w.h, test: w.test, count: w.count, secs: Infinity }, now);
    else if (!w && cur && cur.kind === "new") { const ent = entOf(cur.h); if (ent) ent.alarm = false; cur = null; }
    else if (w && cur && cur.kind === "new") cur.count = w.count;
  }

  // ---------- the camera ----------
  const camPos = new THREE.Vector3(), camLook = new THREE.Vector3(), wantPos = new THREE.Vector3(), wantLook = new THREE.Vector3();
  let shot = 0, shotT0 = 0, started = false;
  const R0 = () => Math.max(reach * 0.8 + 5, rmax * 1.5 + 5, ((reach + 2) / (Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * Math.max(camera.aspect, 0.45))) * 0.72);
  function pose(time) {
    const R = R0(), a = time * 0.03, kinds = ["wide", "low", "rush", "top", "low"], kind = calm ? "wide" : kinds[shot % kinds.length];
    const netList = [...nets.values()];
    if (kind === "low" && netList.length) {
      const n = netList[Math.floor(shot / kinds.length * 2 + shot) % netList.length], rad = n.R * 1.05 + 3.8, b = time * 0.07 + shot;
      wantPos.set(n.center.x + Math.sin(b) * rad, 2.6 + Math.sin(time * 0.3) * 0.5, n.center.z + Math.cos(b) * rad);
      wantLook.set(n.center.x, 1.0, n.center.z);
    } else if (kind === "rush") {
      const u = Math.min(1, (time - shotT0) / SHOT_SECS), lane = (shot % 2 ? 1 : -1) * 2.5, span = Math.max(reach, 12) * 0.9;
      wantPos.set(THREE.MathUtils.lerp(-span, span, u), 3.4 + Math.sin(time * 0.4) * 0.4, lane);
      wantLook.set(wantPos.x + 7, 1.8, lane * 0.3);
    } else if (kind === "top") {
      wantPos.set(Math.sin(a * 1.7) * R * 0.14, R * 1.08, Math.cos(a * 1.7) * R * 0.14 + 0.5);
      wantLook.set(0, 0, 0);
    } else {
      wantPos.set(Math.sin(a) * R, R * 0.5 + Math.sin(time * 0.04) * R * 0.06, Math.cos(a) * R);
      wantLook.set(0, 3.2, 0);
    }
  }
  const sizeOf = e => MODELS[e.model] || 1;
  function focusPose(ent, age, time) {
    const r = 5.2 + sizeOf(ent) * 1.6, b = time * 0.2;
    wantPos.set(ent.group.position.x + Math.sin(b) * r, ent.group.position.y + 2.5 + Math.sin(time * 0.6) * 0.3, ent.group.position.z + Math.cos(b) * r);
    wantLook.copy(ent.group.position).add(new THREE.Vector3(0, 0.3, 0));
  }

  // ---------- the HUD ----------
  const labelEls = [], labelPool = () => { const el = document.createElement("div"); el.className = "g3d-lbl"; labelBox.appendChild(el); labelEls.push(el); return el; };
  const v = new THREE.Vector3();
  function project(pos, dy) { v.copy(pos); v.y += dy; v.project(camera); return { x: (v.x * 0.5 + 0.5) * W, y: (-v.y * 0.5 + 0.5) * H, z: v.z }; }
  let W = 1, H = 1, lastBox = "", sorted = [], sortAt = 0;
  function drawLabels(now) {
    if (now - sortAt > 800) { sortAt = now; sorted = [...ents.values()].filter(e => !e.leaving).sort((a, b) => a.group.position.distanceToSquared(camera.position) - b.group.position.distanceToSquared(camera.position)); }
    const show = new Set(), cap = lite ? 4 : 9;
    for (const e of sorted) { if (show.size >= cap) break; if (e.d.gw || show.size < cap - 2) show.add(e); }
    for (const e of sorted) if ((e.d.odd || (cur && cur.h && String(cur.h.id) === e.key)) && show.size < cap + 3) show.add(e);
    let i = 0;
    for (const e of show) {
      const el = labelEls[i] || labelPool(), p = project(e.group.position, (MODELS[e.model] || 1) * 0.75 + 0.35);
      i++;
      if (p.z > 1 || p.x < 0 || p.x > W || p.y < 0 || p.y > H) { el.style.display = "none"; continue; }
      el.style.display = ""; el.textContent = (e.d.name === "—" ? e.d.ip : e.d.name) + (e.d.odd ? "  ⚠" : "");
      el.className = "g3d-lbl" + (e.d.gw ? " gw" : "") + (e.d.odd ? " odd" : "") + (e.d.state === "off" ? " off" : "");
      el.style.transform = `translate(${Math.round(p.x)}px, ${Math.round(p.y)}px) translate(-50%, -100%)`;
    }
    for (; i < labelEls.length; i++) labelEls[i].style.display = "none";
  }
  const KIND = { new: ["NEW DEVICE  ·  LOCKED ON", "alarm"], lost: ["LOST CONTACT", "warn"], back: ["REACQUIRED", "ok"] };
  function drawAlert(now) {
    const ent = cur ? entOf(cur.h) : null;
    if (!cur || !ent) { ret.style.opacity = 0; call.style.opacity = 0; alarmEl.style.opacity = 0; if (lastBox) { o.onBox(null); lastBox = ""; } return; }
    const age = (now - cur.t0) / 1000, k = KIND[cur.kind] || KIND.new;
    const fade = calm ? 1 : Math.min(1, age * 3), settle = calm || age > 15 ? 0.6 : 1;
    const lock = calm ? 1 : ease(Math.min(1, Math.max(0, (age - 0.8) / 0.7)));
    const p = project(ent.group.position, 0.2);
    hud.dataset.kind = k[1];
    alarmEl.style.opacity = cur.kind === "new" ? (0.7 + (calm || age > 15 ? 0 : 0.3 * Math.sin(now / 220))) * fade * settle : 0;
    ret.style.opacity = lock > 0 ? fade : 0;
    ret.style.transform = `translate(${Math.round(p.x)}px, ${Math.round(p.y)}px) translate(-50%, -50%) scale(${1 + 1.6 * (1 - lock)})`;
    const slide = calm ? 1 : ease(Math.min(1, Math.max(0, (age - 1.2) / 0.5)));
    const d = o.describe(cur.h), cw = Math.min(340, W * 0.8), toLeft = p.x > W * 0.6;
    call.dataset.kind = k[1];
    q(".g3d-call-h").textContent = cur.kind === "new" && cur.test ? "TEST  ·  NEW DEVICE" : k[0];
    q(".g3d-call-n").textContent = d.name; q(".g3d-call-a").textContent = d.line1;
    const ms = container.querySelectorAll(".g3d-call-m"); ms[0].textContent = cur.kind === "new" ? d.line2 : ""; ms[1].textContent = cur.kind === "new" ? d.line3 : "";
    call.style.width = cw + "px";
    const ch = call.offsetHeight || 140;
    const cx = Math.min(W - cw - 20, Math.max(20, toLeft ? p.x - 90 - cw : p.x + 90)), cy = Math.max(20, Math.min(H - ch - 80, p.y - ch * 1.1));
    call.style.opacity = slide * fade; call.style.transform = `translate(${Math.round(cx)}px, ${Math.round(cy)}px)`;
    if (cur.kind === "new" && slide > 0.9) {
      const box = { x: cx, y: cy + ch + 10, w: cw, id: cur.h.id, test: !!cur.test, count: cur.count || 1 }, key = [box.x | 0, box.y | 0, box.w | 0, box.id, box.test, box.count].join();
      if (key !== lastBox) { lastBox = key; o.onBox(box); }
    } else if (lastBox) { o.onBox(null); lastBox = ""; }
  }

  // ---------- sizing, and drawing ----------
  function resize() {
    W = Math.max(1, stage.clientWidth || innerWidth); H = Math.max(1, stage.clientHeight || innerHeight);
    renderer.setSize(W, H); composer.setSize(W, H); bloom.resolution.set(W / 2, H / 2); screenPass.uniforms.height.value = H;
    camera.aspect = W / H; camera.updateProjectionMatrix();
  }
  addEventListener("resize", resize); resize();

  const clock = new THREE.Clock(), tmp = new THREE.Vector3(), tc = new THREE.Color();
  let raf = 0, last = 0, stopped = false;
  function frameAt(now, dt, time) {
    T.value = time * (calm ? 0.25 : 1);
    const k = 1 - Math.exp(-3.2 * dt);
    // networks and the internet settle into place
    nets.forEach(n => {
      n.center.lerp(n.target, k); n.R += (n.Rt - n.R) * k;
      n.group.position.set(n.center.x, n.center.y - 0.62, n.center.z);
      n.disc.scale.set(n.R, n.R, 1); n.ring.scale.set(n.R, n.R, 1); n.fx.scale.set(n.R, n.R, 1);
    });
    internet.position.lerp(internetTarget, k);
    iHalo.rotation.z = time * 0.5; iHalo2.rotation.z = -time * 0.35; iEdges.rotation.y = time * 0.3; iCore.rotation.y = time * 0.3;
    iAura.material.opacity = 0.45 + Math.sin(time * 1.6) * 0.1;

    // the scan: a ring runs out on every platform, and each device lights as it passes
    let waveBoost = 0;
    if (wave) {
      const r = (now - wave.t0) / 1000 * WAVE;
      let live = false;
      nets.forEach(n => { const m = n.wave, lim = n.R * 1.3; m.visible = r < lim + 1; if (m.visible) { live = true; m.scale.set(Math.max(0.01, r), Math.max(0.01, r), 1); m.material.opacity = Math.max(0, 1 - r / lim) * 0.9; } });
      waveBoost = Math.max(0, 1 - r / (reach * 1.4)) * 0.35;
      if (!live) { wave = null; nets.forEach(n => { n.wave.visible = false; }); }
    }
    sweepEl.style.opacity = wave ? 1 : 0;

    for (const e of [...ents.values()]) {
      if (e.leaving) {
        const t = (now - e.leaving) / 450; e.scale = Math.max(0, 1 - t); e.group.scale.setScalar(e.scale);
        if (t >= 1) { dropEnt(e); linkSig = ""; continue; }
      }
      e.pos.lerp(e.target, 1 - Math.exp(-2.6 * dt));
      const d = e.d, off = d.state === "off", still = calm;
      e.group.position.set(e.pos.x, e.pos.y + (off || still ? 0 : Math.sin(time * 1.3 + e.phase) * 0.07) + (off ? -0.28 : 0), e.pos.z);
      if (!off && !still) e.group.rotation.y += dt * 0.15;
      const isCur = cur && String(cur.h.id) === e.key;
      const alarm = isCur && cur.kind === "new";
      if (alarm !== e.alarm) { e.alarm = alarm; setBeacon(e, alarm ? "alarm" : d.odd ? "odd" : null); }
      else if (!alarm && ((d.odd ? "odd" : null) !== (e.beacon ? e.beacon.kind : null))) setBeacon(e, d.odd ? "odd" : null);
      tc.set(alarm ? ALARM : COLORS[d.state]).multiplyScalar(alarm ? 2.0 : STATE_GLOW[d.state]);
      e.col.lerp(tc, 1 - Math.exp(-5 * dt));
      // a scan lights a device as the ring crosses it
      if (wave && e.wave !== wave.id && e.netId != null) {
        const n = nets.get(e.netId);
        if (n && Math.hypot(e.pos.x - n.center.x, e.pos.z - n.center.z) <= (now - wave.t0) / 1000 * WAVE) { e.wave = wave.id; e.flash = 1; }
      }
      e.flash = Math.max(0, e.flash - dt * 1.4);
      const bornAge = e.born ? (now - e.born) / 1000 : 9;
      const build = bornAge < 1.4 ? 1 + 1.6 * (1 - bornAge / 1.4) : 1;           // a new device flares as it appears
      const flick = e.glitch > 0 ? (Math.random() < 0.35 ? 0.25 : 1.2) : 1;
      e.glitch = Math.max(0, e.glitch - dt);
      const gain = (e.dens || 1) * (off ? 0.9 : 1) * (1 + e.flash * 1.4) * build * flick;
      e.edges.material.uniforms.col.value.copy(e.col); e.edges.material.uniforms.gain.value = gain;
      e.body.material.uniforms.col.value.copy(e.col); e.body.material.uniforms.op.value = (off ? 0.22 : 0.42) + e.flash * 0.3; e.body.material.uniforms.gainRim.value = Math.min(1, (e.dens || 1) * 1.2);
      e.pad.material.uniforms.col.value.copy(e.col); e.pad.material.uniforms.off.value = off ? 1 : 0;
      e.pad.material.uniforms.hot.value += ((isCur || e.flash > 0.3 ? 1 : 0) - e.pad.material.uniforms.hot.value) * (1 - Math.exp(-8 * dt));
      if (e.ghosts) {
        const burst = (!calm && fract(Math.sin(Math.floor(time * 6 + e.phase * 3) * 91.7) * 4375.5) > 0.9) || e.glitch > 0;
        e.ghosts.forEach((m, i) => { m.visible = burst; if (burst) m.position.set((i ? 1 : -1) * 0.05 * (0.5 + Math.random()), (Math.random() - 0.5) * 0.04, (Math.random() - 0.5) * 0.04); });
      }
      if (!e.leaving) { e.scale += ((alarm ? 1.3 : 1) - e.scale) * (1 - Math.exp(-10 * dt)); e.group.scale.setScalar(e.scale); }
      if (e.beacon) {
        const t = (time * (alarm ? 1.1 : 0.55)) % 1;
        e.beacon.ripple.scale.setScalar(0.8 + t * 2.8); e.beacon.ripple.material.opacity = (1 - t) * 0.85;
        e.beacon.beam.material.opacity = 0.32 + Math.sin(time * 2.2) * 0.12;
      }
    }
    bloom.strength = 0.7 + waveBoost + (cur && cur.kind === "new" ? 0.15 : 0);

    // the links follow, with traffic along them
    links.forEach(L => {
      const b = L.e.group.position, a = L.aPos(), arr = L.geo.attributes.position.array;
      if (L.curved) {
        L.mid.set((a.x + b.x) / 2, Math.max(a.y, b.y) + 0.5, (a.z + b.z) / 2);
        for (let i = 0; i < L.N; i++) { pointOn(L, i / (L.N - 1), tmp); arr[i * 3] = tmp.x; arr[i * 3 + 1] = tmp.y; arr[i * 3 + 2] = tmp.z; }
      } else { arr[0] = a.x; arr[1] = a.y; arr[2] = a.z; arr[3] = b.x; arr[4] = b.y; arr[5] = b.z; }
      L.geo.attributes.position.needsUpdate = true;
      L.line.visible = ents.get(L.e.key) === L.e && !L.e.leaving;
      const off = L.e.d.state === "off";
      L.line.material.opacity = L.curved ? 1 : off ? 0.16 : 0.75;
    });
    let n = 0;
    for (const p of parts) {
      const L = p.L, d = L.e.d;
      if (d.state === "off" || L.e.leaving) continue;
      const speed = (L.curved ? 0.22 : 0.08 + (d.rate || 1) * 0.045) * (calm ? 0.4 : 1);
      p.t += dt * speed * p.dir; if (p.t > 1) p.t -= 1; if (p.t < 0) p.t += 1;
      pointOn(L, p.t, tmp);
      particlePos[n * 3] = tmp.x; particlePos[n * 3 + 1] = tmp.y; particlePos[n * 3 + 2] = tmp.z;
      tc.set(d.state === "unk" ? COLORS.unk : d.state === "odd" ? P.accent2 : L.curved ? P.accent : P.on).multiplyScalar(1.6);
      particleCol[n * 3] = tc.r; particleCol[n * 3 + 1] = tc.g; particleCol[n * 3 + 2] = tc.b;
      if (++n >= MAX_PARTICLES) break;
    }
    pGeo.setDrawRange(0, n); pGeo.attributes.position.needsUpdate = true; pGeo.attributes.color.needsUpdate = true;

    // the camera: the current shot, or round whatever has the alarm
    checkAlerts(now);
    if (cur && now - cur.t0 > cur.secs * 1000) cur = null;
    if (!cur && events.length) begin(events.shift(), now);
    const ent = cur ? entOf(cur.h) : null;
    if (!calm && time - shotT0 > SHOT_SECS && !ent) { shot++; shotT0 = time; }
    if (ent) focusPose(ent, (now - cur.t0) / 1000, time); else pose(time);
    const follow = 1 - Math.exp(-(ent ? 2.2 : 0.8) * dt);
    if (!started) { camPos.copy(wantPos); camLook.copy(wantLook); started = true; }
    camPos.lerp(wantPos, follow); camLook.lerp(wantLook, follow);
    camera.position.copy(camPos); camera.lookAt(camLook);
    // a banner for a lost or regained device; the callout carries the rest
    if (cur && cur.kind !== "new") { banner.textContent = `${KIND[cur.kind][0]}  ·  ${o.describe(cur.h).name}`; banner.style.opacity = 1; } else banner.style.opacity = 0;
    composer.render();
    drawAlert(now); drawLabels(now);
  }
  function frame(nowMs) {
    raf = requestAnimationFrame(frame);
    if (document.hidden || stopped || nowMs - last < 1000 / FPS) return;
    last = nowMs;
    const dt = Math.min(clock.getDelta(), 0.06), time = clock.elapsedTime;
    frameAt(performance.now(), dt, time);
  }
  // Reduced motion: it holds still, and draws again only when something changes.
  function renderOnce() { if (calm && !stopped) { frameAt(performance.now(), 0.016, 0); } }

  await refresh(); await refreshUnusual();
  const timers = [setInterval(refresh, 3000), setInterval(async () => { await refreshUnusual(); }, 30000)];
  if (calm) { checkAlerts(performance.now()); renderOnce(); }
  else { clock.getDelta(); raf = requestAnimationFrame(frame); }

  return {
    event, label: "ON GRID", busy: () => !!cur, holding: () => !!(cur && cur.kind === "new"),
    redraw() { if (calm) { checkAlerts(performance.now()); renderOnce(); } },
    stop() {
      stopped = true; cancelAnimationFrame(raf); timers.forEach(clearInterval); removeEventListener("resize", resize);
      o.onBox(null); renderer.dispose(); composer.dispose(); container.innerHTML = "";
    },
  };
}
