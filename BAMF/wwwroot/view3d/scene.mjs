// BAMF's 3D view: a platform for each network, its gateway at the centre, every other device a glowing
// model on it, coloured by state; traffic as particles along the links; a beacon on anything unusual.
// It draws what data.mjs works out, and is loaded only when the 3D tab is opened.
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { CSS2DRenderer, CSS2DObject } from "three/addons/renderers/CSS2DRenderer.js";
import { EffectComposer } from "three/addons/postprocessing/EffectComposer.js";
import { RenderPass } from "three/addons/postprocessing/RenderPass.js";
import { UnrealBloomPass } from "three/addons/postprocessing/UnrealBloomPass.js";
import { OutputPass } from "three/addons/postprocessing/OutputPass.js";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import * as BufferGeometryUtils from "three/addons/utils/BufferGeometryUtils.js";
import { MODELS, slotsFor, placeNetworks, describeScene } from "./data.mjs";
import { T, glowTexture, makeSky, makeFloor, makePlatformFx, edgeMaterial, bodyMaterial, padMaterial, makeDust, makeScreenPass } from "./fx.mjs";

const COLORS = { on: 0x3fdb7f, unk: 0xffb454, off: 0x5d6f86, odd: 0xb36cff };
const CYAN = 0x26d9ff, MAGENTA = 0xff2fd0;
const STATE_WORDS = { on: "Online", unk: "Online, not yet approved", off: "Offline", odd: "Unusual" };
const LITE_AT = 150;           // above this many devices: names only where it matters, and no glow
const MAX_PARTICLES = 700;
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fract = x => x - Math.floor(x);
const ease = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

/** Loads the models: one merged, centred geometry for each kind, sized for the platform. */
async function loadModels(base) {
  const loader = new GLTFLoader(), geos = {};
  await Promise.all(Object.keys(MODELS).map(async kind => {
    try {
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
      geos[kind] = g;
    } catch { /* a missing model falls back to a plain block */ }
  }));
  return geos;
}

export async function createView(container, { base = "/view3d", onOpenDevice = () => {}, credits = "" } = {}) {
  const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const saved = (() => { try { return JSON.parse(localStorage.getItem("bamf-3d") || "{}"); } catch { return {}; } })();
  const prefs = { mode: saved.mode === "stack" ? "stack" : "side", spin: saved.spin ?? !reduced, labels: saved.labels ?? true, flow: saved.flow ?? true };
  const savePrefs = () => { try { localStorage.setItem("bamf-3d", JSON.stringify(prefs)); } catch { /* private mode */ } };

  // ---------- the page furniture ----------
  container.innerHTML = `
    <div class="v3d-stage" tabindex="0"></div>
    <div class="v3d-panel v3d-controls">
      <div class="v3d-row"><button type="button" data-v3d="side" class="v3d-btn">Side by side</button><button type="button" data-v3d="stack" class="v3d-btn">Stacked</button></div>
      <label class="v3d-chk"><input type="checkbox" data-v3d="spin"> Slow turn</label>
      <label class="v3d-chk"><input type="checkbox" data-v3d="labels"> Names</label>
      <label class="v3d-chk"><input type="checkbox" data-v3d="flow"> Traffic</label>
      <button type="button" data-v3d="reset" class="v3d-btn">Reset view</button>
      <button type="button" data-v3d="full" class="v3d-btn" aria-pressed="false">Full screen</button>
    </div>
    <div class="v3d-panel v3d-legend">
      <span><i style="background:#3fdb7f;color:#3fdb7f"></i>Online</span><span><i style="background:#ffb454;color:#ffb454"></i>Not yet approved</span>
      <span><i style="background:#5d6f86;color:#5d6f86"></i>Offline</span><span><i style="background:#9a7ce8;color:#b36cff"></i>Unusual for this device</span>
      <button type="button" data-v3d="credits" class="v3d-link">Model credits</button>
    </div>
    <div class="v3d-panel v3d-card" hidden></div>
    <div class="v3d-panel v3d-credits" hidden></div>
    <div class="v3d-note">drag to orbit · scroll to zoom · click a device · F for full screen</div>
    <div class="v3d-loading">Loading the 3D view…</div>`;
  const q = s => container.querySelector(s);
  const stage = q(".v3d-stage"), card = q(".v3d-card"), loadingEl = q(".v3d-loading");
  q(".v3d-credits").innerHTML = `<button type="button" class="v3d-x" data-v3d="creditsX" aria-label="Close">×</button>${credits}`;

  // ---------- the scene ----------
  let renderer;
  try { renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance" }); }
  catch { throw new Error("This browser can't draw 3D (WebGL is off)."); }
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  stage.appendChild(renderer.domElement);
  renderer.domElement.setAttribute("role", "img");
  const labelRenderer = new CSS2DRenderer();
  labelRenderer.domElement.style.cssText = "position:absolute;inset:0;pointer-events:none;overflow:hidden";
  stage.appendChild(labelRenderer.domElement);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x03050c);
  scene.fog = new THREE.FogExp2(0x070a1c, 0.011);
  scene.add(makeSky());
  const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 400);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true; controls.dampingFactor = 0.08; controls.minDistance = 4; controls.maxDistance = 120;
  controls.maxPolarAngle = Math.PI * 0.52; controls.autoRotateSpeed = 0.35; controls.autoRotate = prefs.spin && !reduced;
  controls.listenToKeyEvents(stage);   // the arrow keys move the view, for anyone not using a pointer
  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));
  const bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), 0.55, 0.5, 0.3);
  const screenPass = makeScreenPass();
  composer.addPass(bloom); composer.addPass(screenPass); composer.addPass(new OutputPass());

  const glow = glowTexture();
  scene.add(makeFloor(-1.6));
  const dust = makeDust(260, glow); scene.add(dust);

  const geos = await loadModels(base);
  loadingEl.remove();
  const geoFor = kind => geos[kind] || new THREE.BoxGeometry(MODELS[kind] || 1, 0.6, MODELS[kind] || 1);

  // ---------- the internet, up top ----------
  const internet = new THREE.Group();
  const iGeo = new THREE.IcosahedronGeometry(0.75, 2);
  const iCore = new THREE.Mesh(iGeo, new THREE.MeshBasicMaterial({ color: 0x020812, transparent: true, opacity: 0.9 }));
  const iEdges = new THREE.LineSegments(new THREE.EdgesGeometry(iGeo, 1), new THREE.LineBasicMaterial({ color: new THREE.Color(CYAN).multiplyScalar(1.5), transparent: true, opacity: 0.9 }));
  const iAura = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: new THREE.Color(CYAN).multiplyScalar(0.9), transparent: true, opacity: 0.55, blending: THREE.AdditiveBlending, depthWrite: false }));
  iAura.scale.setScalar(5.5);
  const iHalo = new THREE.Mesh(new THREE.TorusGeometry(1.2, 0.012, 6, 96), new THREE.MeshBasicMaterial({ color: new THREE.Color(CYAN).multiplyScalar(1.5) }));
  const iHalo2 = new THREE.Mesh(new THREE.TorusGeometry(1.5, 0.01, 6, 96), new THREE.MeshBasicMaterial({ color: new THREE.Color(MAGENTA).multiplyScalar(1.3) }));
  iHalo.rotation.x = Math.PI / 2.4; iHalo2.rotation.x = Math.PI / 1.8; iHalo2.rotation.y = 0.6;
  internet.add(iCore, iEdges, iAura, iHalo, iHalo2);
  { const el = document.createElement("div"); el.className = "v3d-lbl gw"; el.textContent = "Internet"; const o = new CSS2DObject(el); o.position.set(0, 1.5, 0); internet.add(o); }
  scene.add(internet);
  const internetTarget = new THREE.Vector3(0, 10, -1);

  // ---------- state ----------
  const nets = new Map();        // cidr -> { group, disc, ring, guide, lbl, center, target, R, Rt }
  const ents = new Map();        // key -> device entity
  const pickables = [];
  let links = [], parts = [], linkSig = "", current = null, firstUpdate = true, hovered = null, selected = null, fly = null, reach = 14, lite = false;

  const particlePos = new Float32Array(MAX_PARTICLES * 3), particleCol = new Float32Array(MAX_PARTICLES * 3);
  const pGeo = new THREE.BufferGeometry();
  pGeo.setAttribute("position", new THREE.BufferAttribute(particlePos, 3)); pGeo.setAttribute("color", new THREE.BufferAttribute(particleCol, 3));
  const points = new THREE.Points(pGeo, new THREE.PointsMaterial({ size: 0.42, map: glow, vertexColors: true, transparent: true, opacity: 1, blending: THREE.AdditiveBlending, depthWrite: false, sizeAttenuation: true }));
  points.frustumCulled = false; scene.add(points);

  function makeNet(cidr) {
    const g = new THREE.Group();
    const disc = new THREE.Mesh(new THREE.CircleGeometry(1, 72), new THREE.MeshBasicMaterial({ color: 0x040a18, transparent: true, opacity: 0.88, depthWrite: false }));
    disc.rotation.x = -Math.PI / 2;
    const ring = new THREE.Mesh(new THREE.RingGeometry(0.985, 1, 96), new THREE.MeshBasicMaterial({ color: new THREE.Color(CYAN).multiplyScalar(1.1), transparent: true, opacity: 0.9 }));
    ring.rotation.x = -Math.PI / 2;
    const guide = new THREE.Mesh(new THREE.RingGeometry(0.5, 0.506, 72), new THREE.MeshBasicMaterial({ color: 0x14506b, transparent: true, opacity: 0.8 }));
    guide.rotation.x = -Math.PI / 2;
    const fx = makePlatformFx(CYAN);
    g.add(disc, ring, guide, fx);
    const el = document.createElement("div"); el.className = "v3d-lbl net"; const lbl = new CSS2DObject(el); g.add(lbl);
    scene.add(g);
    const n = { cidr, group: g, disc, ring, guide, fx, lbl, el, center: new THREE.Vector3(), target: new THREE.Vector3(), R: 6, Rt: 6 };
    nets.set(cidr, n); return n;
  }
  function disposeNet(n) { scene.remove(n.group); [n.disc, n.ring, n.guide, n.fx].forEach(m => { m.geometry.dispose(); m.material.dispose(); }); }

  function makeEnt(d, dropIn) {
    const e = { d, key: d.key, pos: new THREE.Vector3(), target: new THREE.Vector3(), phase: Math.random() * 6.28, col: new THREE.Color(COLORS[d.state]),
                scale: 1, birth: dropIn ? performance.now() : 0, leaving: 0 };
    const g = new THREE.Group(), geo = geoFor(d.model);
    e.model = d.model;
    e.body = new THREE.Mesh(geo, bodyMaterial(e.col.clone()));
    e.edges = new THREE.LineSegments(new THREE.EdgesGeometry(geo, 28), edgeMaterial(e.col.clone()));
    // a pad under it, and (for anything unusual) two ghost copies of its outline that jump now and then
    e.pad = new THREE.Mesh(new THREE.CircleGeometry(1, 48), padMaterial(e.col.clone()));
    e.pad.rotation.x = -Math.PI / 2; e.pad.position.y = -(MODELS[d.model] || 1) / 2 - 0.02; e.pad.scale.setScalar(Math.max(0.8, (MODELS[d.model] || 1) * 0.62) * (d.gw ? 1.35 : 1));
    const pick = new THREE.Mesh(new THREE.SphereGeometry(d.gw ? 1.1 : 0.8, 10, 8), new THREE.MeshBasicMaterial({ visible: false }));
    pick.userData.ent = e; pickables.push(pick); e.pick = pick;
    g.add(e.body, e.edges, e.pad, pick);
    e.el = document.createElement("div"); e.label = new CSS2DObject(e.el); g.add(e.label);
    e.group = g; scene.add(g); ents.set(d.key, e);
    return e;
  }
  function dropEnt(e) {
    scene.remove(e.group); e.edges.geometry.dispose(); e.body.material.dispose(); e.edges.material.dispose(); e.pad.geometry.dispose(); e.pad.material.dispose();
    if (e.ghosts) e.ghosts.forEach(m => m.material.dispose());
    const i = pickables.indexOf(e.pick); if (i >= 0) pickables.splice(i, 1);
    if (e.beacon) { e.beacon.ripple.geometry.dispose(); e.beacon.beam.geometry.dispose(); }
    ents.delete(e.key); if (selected === e) select(null);
  }
  function setBeacon(e, on) {
    if (on && !e.beacon) {
      const ripple = new THREE.Mesh(new THREE.RingGeometry(0.92, 1, 64), new THREE.MeshBasicMaterial({ color: new THREE.Color(COLORS.odd).multiplyScalar(1.8), transparent: true, depthWrite: false }));
      ripple.rotation.x = -Math.PI / 2; ripple.position.y = -0.55;
      const beam = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.03, 6, 6, 1, true), new THREE.MeshBasicMaterial({ color: new THREE.Color(COLORS.odd).multiplyScalar(1.6), transparent: true, opacity: 0.45, blending: THREE.AdditiveBlending, depthWrite: false }));
      beam.position.y = 3; e.group.add(ripple, beam); e.beacon = { ripple, beam };
      // two ghosts of its outline, cyan and magenta, that jump apart for an instant now and then
      e.ghosts = [CYAN, MAGENTA].map(c => { const m = new THREE.LineSegments(e.edges.geometry, new THREE.LineBasicMaterial({ color: new THREE.Color(c).multiplyScalar(1.4), transparent: true, opacity: 0.7, blending: THREE.AdditiveBlending, depthWrite: false })); m.visible = false; e.group.add(m); return m; });
    } else if (!on && e.beacon) {
      e.group.remove(e.beacon.ripple, e.beacon.beam); e.beacon.ripple.geometry.dispose(); e.beacon.beam.geometry.dispose(); e.beacon = null;
      if (e.ghosts) { e.ghosts.forEach(m => { e.group.remove(m); m.material.dispose(); }); e.ghosts = null; }
    }
  }
  function labelText(e) { return (e.d.name || e.d.ip) + (e.d.odd ? "  ⚠ unusual" : ""); }
  function styleLabel(e) {
    e.el.className = "v3d-lbl" + (e.d.gw ? " gw" : "") + (e.d.odd ? " odd" : "") + (e.d.state === "off" && !e.d.odd ? " off" : "");
    e.el.textContent = labelText(e);
  }

  // ---------- links ----------
  function rebuildLinks(data) {
    links.forEach(l => { scene.remove(l.line); l.geo.dispose(); l.line.material.dispose(); });
    links = []; parts = [];
    const add = (aPos, e, curved) => {
      const N = curved ? 28 : 2, geo = new THREE.BufferGeometry();
      geo.setAttribute("position", new THREE.BufferAttribute(new Float32Array(N * 3), 3));
      const line = new THREE.Line(geo, new THREE.LineBasicMaterial({ color: 0x2a4b66, transparent: true, opacity: curved ? 0.9 : 0.6, blending: THREE.AdditiveBlending, depthWrite: false }));
      scene.add(line);
      const L = { aPos, e, curved, N, geo, line, mid: new THREE.Vector3() }; links.push(L);
      const n = curved ? 5 : Math.max(1, Math.round((e.d.rate || 1) / 3));
      for (let i = 0; i < n; i++) parts.push({ L, t: Math.random(), dir: Math.random() < 0.5 ? 1 : -1 });
    };
    for (const net of data.nets) {
      const gw = net.gateway ? ents.get(net.gateway) : null;
      const n = nets.get(net.id);
      if (gw) add(() => internet.position, gw, true);
      for (const d of net.devices) {
        const e = ents.get(d.key); if (!e || e === gw) continue;
        if (gw) add(() => gw.group.position, e, false);
        else add(() => n.group.position, e, false);   // no gateway known: it hangs from the platform's centre
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
    // networks
    const wanted = new Set(data.nets.map(n => n.id));
    for (const [cidr, n] of nets) if (!wanted.has(cidr)) { disposeNet(n); nets.delete(cidr); }
    for (const net of data.nets) if (!nets.has(net.id)) makeNet(net.id);
    // devices
    const keys = new Set(data.devices.map(d => d.key));
    for (const e of [...ents.values()]) if (!keys.has(e.key) && !e.leaving) e.leaving = performance.now();
    for (const d of data.devices) {
      let e = ents.get(d.key);
      if (e && e.leaving) { e.leaving = 0; }
      if (!e) e = makeEnt(d, !firstUpdate && !reduced);
      if (e.model !== d.model) {
        e.model = d.model; e.edges.geometry.dispose();
        e.body.geometry = geoFor(d.model); e.edges.geometry = new THREE.EdgesGeometry(e.body.geometry, 28);
        if (e.ghosts) e.ghosts.forEach(m => { m.geometry = e.edges.geometry; });
        e.pad.position.y = -(MODELS[d.model] || 1) / 2 - 0.02; e.pad.scale.setScalar(Math.max(0.8, (MODELS[d.model] || 1) * 0.62) * (d.gw ? 1.35 : 1));
      }
      e.d = d; styleLabel(e); setBeacon(e, !!d.odd);
      if (selected === e) showCard(e);
    }
    // where everything goes
    const placed = data.nets.map(net => ({ net, ...slotsFor(net.devices) }));
    const plan = placeNetworks(placed.map(p => p.radius), prefs.mode);
    placed.forEach((p, i) => {
      const n = nets.get(p.net.id), c = plan.centers[i];
      n.target.set(c.x, c.y, c.z); n.Rt = p.radius;
      n.el.textContent = `${p.net.cidr}  ·  ${p.net.devices.length}`;
      for (const d of p.net.devices) {
        const e = ents.get(d.key), s = p.slots.get(d.key);
        e.target.set(c.x + (s ? s.x : 0), c.y, c.z + (s ? s.z : 0));
      }
    });
    internetTarget.set(0, prefs.mode === "stack" ? (plan.height || 0) + 6 : 10 + Math.max(0, plan.reach - 16) * 0.25, prefs.mode === "stack" ? 0 : -1);
    reach = plan.reach;
    if (firstUpdate) {
      nets.forEach(n => { n.center.copy(n.target); n.R = n.Rt; });
      ents.forEach(e => e.pos.copy(e.target).add(new THREE.Vector3(0, reduced ? 0 : 6 + Math.random() * 3, 0)));
      internet.position.copy(internetTarget);
      goHome(true);
    }
    const sig = data.nets.map(n => n.id + ":" + n.gateway + ":" + n.devices.map(d => d.key).join(",")).join("|");
    if (sig !== linkSig) { linkSig = sig; rebuildLinks(data); }
    renderer.domElement.setAttribute("aria-label", describeScene(data));
    firstUpdate = false;
  }

  // ---------- the camera ----------
  function homeCamera() {
    // far enough back that the whole width fits, whatever shape the window is
    const fitW = (reach + 2) / (Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * Math.max(camera.aspect, 0.4)) * 1.0;
    const d = Math.max(reach * 0.92 + 5, fitW), stacked = prefs.mode === "stack";
    const h = current && stacked ? (placeNetworks(current.nets.map(n => slotsFor(n.devices).radius), "stack").height || 0) : 0;
    return { target: new THREE.Vector3(0, stacked ? h / 2 + 2.2 : 4.4, 0), pos: new THREE.Vector3(0, d * 0.5 + (stacked ? h * 0.9 : 0), d * 0.85 + (stacked ? h * 1.3 : 0)) };
  }
  function goHome(instant) {
    const h = homeCamera();
    if (instant || reduced) { controls.target.copy(h.target); camera.position.copy(h.pos); fly = null; }
    else fly = { t: 0, fromT: controls.target.clone(), toT: h.target, fromP: camera.position.clone(), toP: h.pos };
  }

  // ---------- selection ----------
  function showCard(e) {
    const d = e.d;
    const rows = [["Network", d.net], ["Vendor", d.vendor || "—"], ["Kind", d.kind], ["Status", STATE_WORDS[d.state]], ["Answers in", d.ms == null ? "—" : d.ms + " ms"], ["Approved", d.known ? "Yes" : "Not yet"]];
    card.innerHTML = `<button type="button" class="v3d-x" data-v3d="cardX" aria-label="Close">×</button>
      <h2>${esc(d.name === "—" ? "Unnamed device" : d.name)}</h2><div class="v3d-sub">${esc(d.ip)}</div>
      <dl>${rows.map(r => `<dt>${esc(r[0])}</dt><dd>${esc(r[1])}</dd>`).join("")}</dl>
      ${d.odd ? `<div class="v3d-odd"><b>${esc(d.odd.title)}</b><br>${esc(d.odd.detail)}</div>` : ""}
      <button type="button" class="v3d-btn" data-v3d="history">History</button>`;
    card.hidden = false;
  }
  function select(e) {
    selected = e;
    if (!e) { card.hidden = true; return; }
    showCard(e);
    const to = e.group.position.clone();
    const dir = camera.position.clone().sub(controls.target); dir.y = 0;
    if (dir.lengthSq() < 0.01) dir.set(0, 0, 1);
    dir.normalize().multiplyScalar(7.5).setY(3.6);
    if (reduced) { controls.target.copy(to); camera.position.copy(to).add(dir); fly = null; }
    else fly = { t: 0, fromT: controls.target.clone(), toT: to, fromP: camera.position.clone(), toP: to.clone().add(dir) };
  }

  // ---------- the controls ----------
  const ray = new THREE.Raycaster(), mouse = new THREE.Vector2();
  let down = null;
  renderer.domElement.addEventListener("pointermove", ev => {
    const r = renderer.domElement.getBoundingClientRect();
    mouse.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
    ray.setFromCamera(mouse, camera);
    const hit = ray.intersectObjects(pickables, false)[0];
    hovered = hit ? hit.object.userData.ent : null;
    renderer.domElement.style.cursor = hovered ? "pointer" : "grab";
  });
  renderer.domElement.addEventListener("pointerdown", ev => { down = [ev.clientX, ev.clientY]; });
  renderer.domElement.addEventListener("pointerup", ev => {
    if (!down || Math.hypot(ev.clientX - down[0], ev.clientY - down[1]) > 5) return;
    select(hovered);
  });
  const syncUi = () => {
    container.querySelectorAll("[data-v3d=side],[data-v3d=stack]").forEach(b => b.classList.toggle("on", b.dataset.v3d === prefs.mode));
    for (const k of ["spin", "labels", "flow"]) q(`input[data-v3d=${k}]`).checked = !!prefs[k];
    container.classList.toggle("v3d-nolabels", !prefs.labels);
  };
  container.addEventListener("click", ev => {
    const t = ev.target.closest("[data-v3d]"); if (!t) return;
    const a = t.dataset.v3d;
    if (a === "side" || a === "stack") { prefs.mode = a; savePrefs(); syncUi(); if (current) { update(current); goHome(false); } }
    else if (a === "reset") { select(null); goHome(false); }
    else if (a === "full") toggleFull();
    else if (a === "cardX") select(null);
    else if (a === "history" && selected) onOpenDevice(selected.d);
    else if (a === "credits") q(".v3d-credits").hidden = false;
    else if (a === "creditsX") q(".v3d-credits").hidden = true;
  });
  container.addEventListener("change", ev => {
    const t = ev.target.closest("input[data-v3d]"); if (!t) return;
    prefs[t.dataset.v3d] = t.checked; savePrefs();
    if (t.dataset.v3d === "spin") controls.autoRotate = t.checked && !reduced;
    syncUi();
  });
  syncUi();

  // ---------- full screen ----------
  // The browser's own full screen where there is one (not on an iPhone), else the view fills the window.
  const isFull = () => document.fullscreenElement === container || container.classList.contains("v3d-full");
  function syncFull() {
    const on = isFull(), b = q("[data-v3d=full]");
    b.textContent = on ? "Exit full screen" : "Full screen"; b.setAttribute("aria-pressed", String(on));
    document.documentElement.classList.toggle("v3d-lock", container.classList.contains("v3d-full"));
  }
  function toggleFull() {
    if (isFull()) {
      container.classList.remove("v3d-full"); setTimeout(() => { resize(); if (!selected) goHome(true); }, 50);
      if (document.fullscreenElement === container) document.exitFullscreen().catch(() => {});
    } else if (container.requestFullscreen) container.requestFullscreen().catch(() => { container.classList.add("v3d-full"); syncFull(); });
    else { container.classList.add("v3d-full"); setTimeout(() => { resize(); if (!selected) goHome(true); }, 50); }
    syncFull();
  }
  const onFullChange = () => { syncFull(); resize(); if (!selected) goHome(true); };
  const onKey = ev => {
    if (ev.key === "Escape" && container.classList.contains("v3d-full")) toggleFull();
    else if ((ev.key === "f" || ev.key === "F") && !ev.ctrlKey && !ev.metaKey && !ev.altKey && container.contains(document.activeElement) && document.activeElement.tagName !== "INPUT") toggleFull();
  };
  document.addEventListener("fullscreenchange", onFullChange); document.addEventListener("keydown", onKey);

  // ---------- sizing, and drawing only while it can be seen ----------
  function resize() {
    const w = Math.max(1, stage.clientWidth), h = Math.max(1, stage.clientHeight);
    renderer.setSize(w, h); composer.setSize(w, h); labelRenderer.setSize(w, h); bloom.resolution.set(w, h); screenPass.uniforms.height.value = h;
    camera.aspect = w / h; camera.updateProjectionMatrix();
  }
  const ro = new ResizeObserver(resize); ro.observe(stage); resize();

  const clock = new THREE.Clock(), tmp = new THREE.Vector3(), tc = new THREE.Color();
  let raf = 0, active = false;
  function frame() {
    raf = requestAnimationFrame(frame);
    const dt = Math.min(clock.getDelta(), 0.05), time = clock.elapsedTime, now = performance.now();
    T.value = time * (reduced ? 0.25 : 1);
    const k = 1 - Math.exp(-3.2 * dt);
    nets.forEach(n => {
      n.center.lerp(n.target, k); n.R += (n.Rt - n.R) * k;
      n.group.position.set(n.center.x, n.center.y - 0.62, n.center.z);
      n.disc.scale.set(n.R, n.R, 1); n.ring.scale.set(n.R, n.R, 1); n.guide.scale.set(n.R * 0.52, n.R * 0.52, 1); n.fx.scale.set(n.R, n.R, 1);
      n.lbl.position.set(0, 0.4, n.R + 0.9);
    });
    internet.position.lerp(internetTarget, k);
    iHalo.rotation.z = time * 0.5; iHalo2.rotation.z = -time * 0.35; iEdges.rotation.y = time * 0.3; iCore.rotation.y = time * 0.3;
    iAura.material.opacity = 0.45 + Math.sin(time * 1.6) * 0.1;

    const odd = new Set();
    for (const e of [...ents.values()]) {
      if (e.leaving) {
        const t = (now - e.leaving) / 450; e.scale = Math.max(0, 1 - t); e.group.scale.setScalar(e.scale);
        if (t >= 1) { dropEnt(e); linkSig = ""; continue; }
      }
      e.pos.lerp(e.target, 1 - Math.exp(-2.6 * dt));
      const off = e.d.state === "off", still = reduced;
      e.group.position.set(e.pos.x, e.pos.y + (off || still ? 0 : Math.sin(time * 1.3 + e.phase) * 0.07) + (off ? -0.28 : 0), e.pos.z);
      if (!off && !still) e.group.rotation.y += dt * 0.15;
      tc.set(COLORS[e.d.state]).multiplyScalar(off ? 0.9 : 1.6);
      e.col.lerp(tc, 1 - Math.exp(-5 * dt));
      const hot = e === hovered || e === selected;
      e.edges.material.uniforms.col.value.copy(e.col); e.edges.material.uniforms.gain.value = off ? 0.8 : hot ? 1.5 : 1;
      e.body.material.uniforms.col.value.copy(e.col); e.body.material.uniforms.op.value = off ? 0.22 : 0.42;
      e.pad.material.uniforms.col.value.copy(e.col); e.pad.material.uniforms.off.value = off ? 1 : 0;
      e.pad.material.uniforms.hot.value += ((hot ? 1 : 0) - e.pad.material.uniforms.hot.value) * (1 - Math.exp(-8 * dt));
      if (e.ghosts) {
        const burst = !reduced && fract(Math.sin(Math.floor(time * 6 + e.phase * 3) * 91.7) * 4375.5) > 0.9;
        e.ghosts.forEach((m, i) => { m.visible = burst; if (burst) m.position.set((i ? 1 : -1) * 0.05 * (0.5 + Math.random()), (Math.random() - 0.5) * 0.04, (Math.random() - 0.5) * 0.04); });
      }
      if (!e.leaving) { e.scale += ((hot ? 1.25 : 1) - e.scale) * (1 - Math.exp(-10 * dt)); e.group.scale.setScalar(e.scale); }
      // Names only where they can be read: the nearer ones, always the gateways and anything unusual or picked.
      const near = camera.position.distanceTo(e.group.position) < (lite ? 14 : 26);
      const showName = prefs.labels && (e.d.gw || e.d.odd || hot || near);
      e.label.visible = showName; e.label.position.set(0, (e.d.gw ? 1.5 : 0.95) * (MODELS[e.model] > 1.6 ? 1.1 : 1), 0);
      if (e.birth && !e.rippled && now - e.birth < 1600) { e.rippled = true; ripple(e); }
      if (e.beacon) {
        const t = (time * 0.55) % 1;
        e.beacon.ripple.scale.setScalar(0.8 + t * 2.8); e.beacon.ripple.material.opacity = (1 - t) * 0.85;
        e.beacon.beam.material.opacity = 0.32 + Math.sin(time * 2.2) * 0.12;
      }
    }
    // links follow
    links.forEach(L => {
      const b = L.e.group.position, a = L.aPos(), arr = L.geo.attributes.position.array;
      if (L.curved) {
        L.mid.set((a.x + b.x) / 2, Math.max(a.y, b.y) + 0.5, (a.z + b.z) / 2);
        for (let i = 0; i < L.N; i++) { pointOn(L, i / (L.N - 1), tmp); arr[i * 3] = tmp.x; arr[i * 3 + 1] = tmp.y; arr[i * 3 + 2] = tmp.z; }
      } else { arr[0] = a.x; arr[1] = a.y; arr[2] = a.z; arr[3] = b.x; arr[4] = b.y; arr[5] = b.z; }
      L.geo.attributes.position.needsUpdate = true;
      L.line.visible = ents.get(L.e.key) === L.e && !L.e.leaving;   // a device that's gone takes its line with it
      const off = L.e.d.state === "off";
      L.line.material.opacity = L.curved ? 1 : off ? 0.16 : 0.75;
      L.line.material.color.set(L.curved ? 0x1a8fb8 : L.e.d.state === "odd" ? 0x7a3fc0 : off ? 0x1c2a3a : 0x147a9a);
    });
    // traffic
    let n = 0;
    if (prefs.flow) for (const p of parts) {
      const L = p.L, d = L.e.d;
      if (d.state === "off" || L.e.leaving) continue;
      const speed = (L.curved ? 0.22 : 0.08 + (d.rate || 1) * 0.045) * (reduced ? 0.4 : 1);
      p.t += dt * speed * p.dir; if (p.t > 1) p.t -= 1; if (p.t < 0) p.t += 1;
      pointOn(L, p.t, tmp);
      particlePos[n * 3] = tmp.x; particlePos[n * 3 + 1] = tmp.y; particlePos[n * 3 + 2] = tmp.z;
      tc.set(d.state === "unk" ? COLORS.unk : d.state === "odd" ? MAGENTA : L.curved ? CYAN : 0x6fffd0).multiplyScalar(1.6);
      particleCol[n * 3] = tc.r; particleCol[n * 3 + 1] = tc.g; particleCol[n * 3 + 2] = tc.b;
      if (++n >= MAX_PARTICLES) break;
    }
    pGeo.setDrawRange(0, n); pGeo.attributes.position.needsUpdate = true; pGeo.attributes.color.needsUpdate = true;

    if (fly) {
      fly.t = Math.min(1, fly.t + dt / 0.9); const e = ease(fly.t);
      controls.target.lerpVectors(fly.fromT, fly.toT, e); camera.position.lerpVectors(fly.fromP, fly.toP, e);
      if (fly.t >= 1) fly = null;
    }
    controls.update();
    composer.render();   // the bloom pass sits out above the lite limit; the colour handling still needs the rest
    labelRenderer.render(scene, camera);
  }
  function ripple(e) {
    const m = new THREE.Mesh(new THREE.RingGeometry(0.92, 1, 64), new THREE.MeshBasicMaterial({ color: new THREE.Color(COLORS.unk).multiplyScalar(1.8), transparent: true, depthWrite: false }));
    m.rotation.x = -Math.PI / 2; m.position.copy(e.target).setY(e.target.y - 0.6); scene.add(m);
    const born = performance.now();
    (function step() {
      const t = (performance.now() - born) / 1400;
      if (t >= 1 || !active) { scene.remove(m); m.geometry.dispose(); m.material.dispose(); return; }
      m.scale.setScalar(0.5 + t * 4); m.material.opacity = (1 - t) * 0.9; requestAnimationFrame(step);
    })();
  }
  function setActive(on) {
    if (on === active) return;
    active = on;
    if (on) { clock.getDelta(); resize(); raf = requestAnimationFrame(frame); }
    else cancelAnimationFrame(raf);
  }
  const onVisible = () => { if (document.hidden) cancelAnimationFrame(raf); else if (active) { clock.getDelta(); raf = requestAnimationFrame(frame); } };
  document.addEventListener("visibilitychange", onVisible);

  function destroy() {
    setActive(false); ro.disconnect(); document.removeEventListener("visibilitychange", onVisible);
    document.removeEventListener("fullscreenchange", onFullChange); document.removeEventListener("keydown", onKey);
    container.classList.remove("v3d-full"); document.documentElement.classList.remove("v3d-lock");
    renderer.dispose(); controls.dispose(); container.innerHTML = "";
  }
  const api = { update, setActive, select: key => select(ents.get(String(key)) || null), destroy, get count() { return ents.size; }, _ents: ents, _prefs: prefs };
  container.__v3d = api;
  return api;
}
