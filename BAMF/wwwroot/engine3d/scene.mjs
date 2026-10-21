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
import { MODELS, slotsFor, placeNetworks, describeScene, worstService, FILTERS, matchesFilter, searchRank, problemsOf, behindOf, formatRate, placementFromPoint, FLOOR_GAP,
  outageOf, outageText, usageOf, formatBytes, USAGE_WINDOWS, briefingOf, weatherOf, markersOf, replaySpeed, REPLAY_RANGES, REPLAY_SECONDS } from "./data.mjs";
import { createExtras } from "./extras.mjs";
import { buildHouse } from "./house.mjs";
import { createSound } from "./sound.mjs";
import { createWeather } from "./weather.mjs";
import { createSkyline } from "./skyline.mjs";
import { createXray } from "./xray.mjs";
import { T, SKIN, glowTexture, makeSky, makeFloor, makePlatformFx, edgeMaterial, bodyMaterial, padMaterial, makeDust, makeScreenPass } from "./fx.mjs";

const COLORS = { on: 0x3fdb7f, unk: 0xffb454, off: 0x5d6f86, odd: 0xb36cff };
const CYAN = 0x26d9ff, MAGENTA = 0xff2fd0;
const STATE_WORDS = { on: "Online", unk: "Online, not yet approved", off: "Offline", odd: "Unusual" };
// Amber and purple read hotter than green at the same strength, so each state's outline is set to look as bright as the others.
const STATE_GLOW = { on: 1.9, unk: 1.35, off: 1.45, odd: 1.6 };
const DENSITY_REF = 34;        // outline length per unit of size squared that gets full brightness
const LITE_AT = 150;           // above this many devices: names only where it matters, and no glow
const MAX_PARTICLES = 700;
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fract = x => x - Math.floor(x);
/**
 * How bright a model's outline should be. Where a lot of line is packed into a small model, the glow of
 * every line adds up and it blazes; a sparse one needs no help. So brightness is eased down as the length of
 * outline per square unit of the model's size goes up.
 */
function densityGain(edgeGeo, size) {
  const p = edgeGeo.attributes.position; let len = 0;
  for (let i = 0; i + 1 < p.count; i += 2) len += Math.hypot(p.getX(i) - p.getX(i + 1), p.getY(i) - p.getY(i + 1), p.getZ(i) - p.getZ(i + 1));
  const d = len / (size * size) || 1;
  return Math.min(1.15, Math.max(0.28, Math.pow(DENSITY_REF / d, 0.7)));
}
const ease = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

/** Loads one model: a single merged, centred geometry, sized for the platform. */
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

export async function createView(container, { base = "/engine3d", onOpenDevice = () => {}, credits = "", onReplayLoad = null, replayScene = null, onReplayEnd = () => {}, actionsFor = () => [], onAction = () => {}, onSelect = () => {}, onPlace = null, onBriefingLoad = null, onWantsChange = () => {} } = {}) {
  const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const saved = (() => { try { return JSON.parse(localStorage.getItem("bamf-3d") || "{}"); } catch { return {}; } })();
  const prefs = {
    mode: saved.mode === "stack" ? "stack" : saved.mode === "house" ? "house" : "side", spin: saved.spin ?? !reduced, labels: saved.labels ?? true, flow: saved.flow ?? true,
    services: saved.services ?? true, strain: saved.strain ?? true, dests: saved.dests ?? true, scans: saved.scans ?? true, sound: false,
    free: saved.free ?? false, skyline: saved.skyline ?? false, xray: saved.xray ?? false, weather: saved.weather ?? true,
    usageHours: USAGE_WINDOWS.some(w => w.hours === saved.usageHours) ? saved.usageHours : 24,
    replayHours: REPLAY_RANGES.some(r => r.hours === saved.replayHours) ? saved.replayHours : 24,
  };
  const savePrefs = () => { try { localStorage.setItem("bamf-3d", JSON.stringify(prefs)); } catch { /* private mode */ } };

  // ---------- the page furniture ----------
  container.innerHTML = `
    <div class="v3d-stage" tabindex="0"></div>
    <div class="v3d-panel v3d-controls">
      <div class="v3d-row"><button type="button" data-v3d="side" class="v3d-btn">Side by side</button><button type="button" data-v3d="stack" class="v3d-btn">Stacked</button></div>
      <button type="button" data-v3d="house" class="v3d-btn v3d-wide" hidden>Your house</button>
      <button type="button" data-v3d="place" class="v3d-btn v3d-wide" aria-pressed="false" hidden>Place devices</button>
      <label class="v3d-chk" title="Drag to slide across the scene instead of turning it about its middle. Right-drag turns; W A S D glide; Q and E go down and up."><input type="checkbox" data-v3d="free"> Move freely</label>
      <label class="v3d-chk"><input type="checkbox" data-v3d="spin"> Slow turn</label>
      <label class="v3d-chk"><input type="checkbox" data-v3d="labels"> Names</label>
      <label class="v3d-chk"><input type="checkbox" data-v3d="flow"> Traffic</label>
      <details class="v3d-more"><summary>More to see</summary>
        <label class="v3d-chk"><input type="checkbox" data-v3d="services"> Services</label>
        <label class="v3d-chk"><input type="checkbox" data-v3d="strain"> Strain</label>
        <label class="v3d-chk"><input type="checkbox" data-v3d="dests"> Destinations</label>
        <label class="v3d-chk"><input type="checkbox" data-v3d="scans"> Scans</label>
        <label class="v3d-chk"><input type="checkbox" data-v3d="sound"> Sound</label>
        <label class="v3d-chk" title="A tower beside each device, as tall as what it has moved"><input type="checkbox" data-v3d="skyline"> Usage skyline</label>
        <label class="v3d-chk" title="Rings and badges on what is exposed, and the rest dimmed"><input type="checkbox" data-v3d="xray"> Exposure</label>
        <label class="v3d-chk" title="Clouds, rain and lightning from the network score"><input type="checkbox" data-v3d="weather"> Weather</label>
      </details>
      <button type="button" data-v3d="replay" class="v3d-btn" ${onReplayLoad ? "" : "hidden"}>Replay</button>
      <button type="button" data-v3d="brief" class="v3d-btn" ${onBriefingLoad ? "" : "hidden"}>Today's briefing</button>
      <button type="button" data-v3d="walk" class="v3d-btn" aria-pressed="false">Walk around</button>
      <button type="button" data-v3d="vr" class="v3d-btn" hidden>Enter VR</button>
      <button type="button" data-v3d="reset" class="v3d-btn">Reset view</button>
      <button type="button" data-v3d="full" class="v3d-btn" aria-pressed="false">Full screen</button>
    </div>
    <div class="v3d-panel v3d-replay" hidden>
      <button type="button" data-v3d="rpPlay" class="v3d-btn" aria-label="Pause">❚❚</button>
      <select data-v3d="rpRange" aria-label="How far back">${REPLAY_RANGES.map(r => `<option value="${r.hours}">${r.label}</option>`).join("")}</select>
      <div class="v3d-rpwrap"><input type="range" data-v3d="rpScrub" min="0" max="1000" value="0" aria-label="Time in the range"><div class="v3d-rpmarks" aria-hidden="true"></div></div>
      <span class="v3d-rptime"></span>
      <select data-v3d="rpSpeed" aria-label="Speed">${REPLAY_SECONDS.map(r => `<option value="${r.s}"${r.s === 60 ? " selected" : ""}>${r.label}</option>`).join("")}</select>
      <button type="button" data-v3d="rpClose" class="v3d-btn">Back to now</button>
    </div>
    <div class="v3d-panel v3d-legend">
      <span><i style="background:#3fdb7f;color:#3fdb7f"></i>Online</span><span><i style="background:#ffb454;color:#ffb454"></i>Not yet approved</span>
      <span><i style="background:#5d6f86;color:#5d6f86"></i>Offline</span><span><i style="background:#9a7ce8;color:#b36cff"></i>Unusual for this device</span>
      <span class="v3d-wx" hidden></span>
      <button type="button" data-v3d="credits" class="v3d-link">Model credits</button>
    </div>
    <div class="v3d-left">
    <div class="v3d-panel v3d-find">
      <input type="search" class="v3d-search" data-v3d="q" placeholder="Find a device…" aria-label="Find a device" autocomplete="off" spellcheck="false">
      <div class="v3d-chips">${FILTERS.map(f => `<button type="button" class="v3d-chip" data-v3d-chip="${f.key}" aria-pressed="false">${f.label}</button>`).join("")}</div>
      <div class="v3d-findrow"><select data-v3d="tag" aria-label="Tag"><option value="">Any tag</option></select><span class="v3d-count"></span></div>
    </div>
    <div class="v3d-panel v3d-attn" hidden></div>
    <div class="v3d-panel v3d-placing" hidden></div>
    </div>
    <div class="v3d-panel v3d-card" hidden></div>
    <div class="v3d-panel v3d-credits" hidden></div>
    <div class="v3d-note"></div>
    <div class="v3d-toast" hidden></div>
    <div class="v3d-panel v3d-usage" hidden></div>
    <div class="v3d-panel v3d-outage" hidden></div>
    <div class="v3d-panel v3d-brief" hidden></div>
    <div class="v3d-flash"></div>
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
  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));
  const bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), 0.7, 0.55, 0.26);
  const screenPass = makeScreenPass();
  composer.addPass(bloom); composer.addPass(screenPass); composer.addPass(new OutputPass());

  const glow = glowTexture();
  scene.add(makeFloor(-1.6));
  const dust = makeDust(260, glow); scene.add(dust);

  // Models come as they're needed: a network with twenty devices fetches a handful of the files, not all of them.
  const loader = new GLTFLoader(), geos = {}, fetching = new Set();
  const blocks = {};
  const geoFor = kind => geos[kind] || (blocks[kind] ||= new THREE.BoxGeometry(MODELS[kind] || 1, 0.6, MODELS[kind] || 1));   // a plain block until it arrives (or if it can't)
  function want(kind) {
    if (geos[kind] || fetching.has(kind)) return;
    fetching.add(kind);
    loadModel(loader, base, kind).then(g => { geos[kind] = g; ents.forEach(e => { if (e.model === kind) applyModel(e); }); }).catch(() => { /* stays a block */ });
  }
  loadingEl.remove();

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
  let internetDown = false;

  // ---------- state ----------
  const nets = new Map();        // cidr -> { group, disc, ring, guide, lbl, center, target, R, Rt }
  const ents = new Map();        // key -> device entity
  const pickables = [];
  let links = [], parts = [], linkSig = "", current = null, firstUpdate = true, hovered = null, selected = null, fly = null, reach = 14, lite = false;
  let houseK = 1;                 // the models are drawn smaller in the house, where the rooms are small
  const filt = { q: "", on: new Set(), tag: "" };
  let filterOn = false, behind = new Map(), placing = false, placePick = null, problemsList = [];
  const sound = createSound();
  const cue = name => { if (prefs.sound) sound.cue(name); };
  const extras = createExtras({ scene, glow, internet, ents, onCue: cue, reduced });
  const skyline = createSkyline({ scene, ents, glow, reduced });
  const xray = createXray({ ents, reduced });
  const weather = createWeather({ scene, glow, controls, flashEl: q(".v3d-flash"), reduced });
  let usageData = null, usage = null, wxScore = null, outage = null, brief = null;
  // a device that is down in the simulation, or cut off from the network by one that is
  const gone = e => !!(outage && outage.info && (outage.info.key === e.key || (outage.info.reason === "network" && outage.cutSet.has(e.key))));
  let house = null, houseInfo = null, houseSpots = new Map();   // the built house, what it was built from, and each placed device's spot
  const inHouse = () => prefs.mode === "house" && !!house;
  const toastEl = () => q(".v3d-toast");
  function say(text) { const t = toastEl(); if (!t) return; t.textContent = text; t.hidden = false; clearTimeout(say.t); say.t = setTimeout(() => { t.hidden = true; }, 4200); }

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
  function disposeNet(n) { scene.remove(n.group); n.el.remove(); [n.disc, n.ring, n.guide, n.fx].forEach(m => { m.geometry.dispose(); m.material.dispose(); }); }

  function makeEnt(d, dropIn) {
    const e = { d, key: d.key, pos: new THREE.Vector3(), target: new THREE.Vector3(), phase: Math.random() * 6.28, col: new THREE.Color(COLORS[d.state]),
                scale: 1, birth: dropIn ? performance.now() : 0, leaving: 0 };
    const g = new THREE.Group(), geo = geoFor(d.model);
    e.model = d.model; want(d.model);
    e.body = new THREE.Mesh(geo, bodyMaterial(e.col.clone()));
    e.edges = new THREE.LineSegments(new THREE.EdgesGeometry(geo, 28), edgeMaterial(e.col.clone()));
    e.dens = densityGain(e.edges.geometry, MODELS[d.model] || 1);
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
  // Puts a device's model on it (again, when it changes or its file has just arrived).
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
    scene.remove(e.group); e.el.remove(); e.edges.geometry.dispose(); e.body.material.dispose(); e.edges.material.dispose(); e.pad.geometry.dispose(); e.pad.material.dispose();
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
  function labelText(e) {
    return (e.d.name || e.d.ip) + (e.d.odd ? "  ⚠ unusual" : "") + (prefs.strain && e.d.strain > 0.5 && e.d.ms != null ? `  · ${Math.round(e.d.ms)} ms` : "")
      + (prefs.services && worstService(e.d.services) === "down" ? "  · service down" : "");
  }
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
        // plugged into a switch or access point that was recorded and is a device here: its line goes there, else to the gateway
        const up = d.parent ? ents.get(d.parent) : null;
        if (up && up !== e) add(() => up.group.position, e, false);
        else if (gw) add(() => gw.group.position, e, false);
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
  function update(data, opts = {}) {
    current = data;
    internetDown = !!opts.internetDown;
    lite = data.devices.length > LITE_AT;
    bloom.enabled = !lite;
    // networks
    const wanted = new Set(data.nets.map(n => n.id));
    for (const [cidr, n] of nets) if (!wanted.has(cidr)) { disposeNet(n); nets.delete(cidr); }
    for (const net of data.nets) if (!nets.has(net.id)) makeNet(net.id);
    // devices
    const keys = new Set(data.devices.map(d => d.key));
    for (const e of [...ents.values()]) if (!keys.has(e.key) && !e.leaving) { e.leaving = performance.now(); if (!firstUpdate) cue("leave"); }
    for (const d of data.devices) {
      let e = ents.get(d.key);
      if (e && e.leaving) { e.leaving = 0; }
      const was = e ? e.d : null;
      if (!e) { e = makeEnt(d, !firstUpdate && !reduced); if (!firstUpdate) cue("arrive"); }
      if (e.model !== d.model) { e.model = d.model; applyModel(e); }
      e.d = d; styleLabel(e); setBeacon(e, !!d.odd);
      if (was && !firstUpdate) {
        if (d.state === "odd" && was.state !== "odd") cue("unusual");
        else if (was.state === "on" && d.state === "off") cue("drop");
        else if (was.state === "off" && d.state !== "off") cue("back");
        const before = worstService(was.services), after = worstService(d.services);
        if (after === "down" && before !== "down") cue("service");
        else if (before === "down" && after && after !== "down") cue("recover");
      }
      if (selected === e) showCard(e);
    }
    extras.setServices(prefs.services);
    xray.setOn(prefs.xray); xray.refresh(); refreshUsage(); refreshOutage();
    behind = behindOf(data.devices);
    renderFindTags(); applyFilter(); renderAttention(); renderPlacing();
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
    if (inHouse()) {
      // Each device goes where it was placed on its plan; one that was never placed stays out of the house.
      for (const e of ents.values()) {
        const pl = houseSpots.get(e.d.id);
        const sp = pl ? house.spot(pl.floorId, pl.x, pl.y) : null;
        e.inHouse = !!sp;
        if (sp) e.target.set(sp.x, sp.y, sp.z);
      }
      internetTarget.set(house.bounds.cx, house.bounds.height + 9, house.bounds.cz - house.bounds.depth / 2 - 2);
      reach = Math.max(house.bounds.width, house.bounds.depth) * 0.7 + 4;
    } else for (const e of ents.values()) e.inHouse = false;
    if (firstUpdate) {
      nets.forEach(n => { n.center.copy(n.target); n.R = n.Rt; });
      ents.forEach(e => e.pos.copy(e.target).add(new THREE.Vector3(0, reduced ? 0 : 6 + Math.random() * 3, 0)));
      internet.position.copy(internetTarget);
      goHome(true);
    }
    const sig = data.nets.map(n => n.id + ":" + n.gateway + ":" + n.devices.map(d => d.key + ">" + (d.parent || "")).join(",")).join("|");
    if (sig !== linkSig) { linkSig = sig; rebuildLinks(data); }
    renderer.domElement.setAttribute("aria-label", describeScene(data));
    firstUpdate = false;
  }

  // ---------- the house, the day's extras and the replay's clock, set from outside ----------
  function setHouse(h) {
    if (house) { scene.remove(house.group); house.dispose(); house = null; }
    houseInfo = h && h.floors && h.floors.length ? h : null;
    houseSpots = new Map(houseInfo ? houseInfo.places.map(p => [p.hostId, p]) : []);
    const btn = q("[data-v3d=house]");
    btn.hidden = !houseInfo;
    if (houseInfo) {
      house = buildHouse(houseInfo.floors);
      house.group.visible = prefs.mode === "house";
      house.group.traverse(o => { if (o.isCSS2DObject) o.visible = prefs.mode === "house"; });
      scene.add(house.group);
    } else if (prefs.mode === "house") { prefs.mode = "side"; savePrefs(); syncUi(); }
    if (current) update(current);
    const placed = houseInfo ? [...ents.values()].filter(e => e.inHouse).length : 0;
    if (houseInfo && prefs.mode === "house" && ents.size && placed < ents.size) say(`${placed} of ${ents.size} devices are placed on a floor plan. The others aren't shown in the house; Floor plan, Place devices puts them in.`);
  }
  function setExtras(x) {
    const now = Date.now();
    byHost = new Map([...ents.values()].filter(e => !e.leaving).map(e => [e.d.id, e]));
    extras.setDestinations(x.destinations || [], now, prefs.dests);
    extras.setScans(x.scans || [], now, byHost, prefs.scans);
    renderAttention();
  }
  let byHost = new Map();

  // ---------- the camera ----------
  function homeCamera() {
    if (inHouse()) {
      const b = house.bounds, d = Math.max(b.width, b.depth) * 0.55 + 5;
      return { target: new THREE.Vector3(b.cx, b.height / 2 - 0.4, b.cz), pos: new THREE.Vector3(b.cx, d * 0.8 + b.height * 0.5, b.cz + d * 0.8 + b.height * 0.35) };
    }
    // far enough back that the whole width fits, whatever shape the window is
    const fitW = (reach + 2) / (Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * Math.max(camera.aspect, 0.4)) * 1.0;
    const rmax = current ? Math.max(0, ...current.nets.map(n => slotsFor(n.devices).radius)) : 0;   // a big platform needs standing back from
    const d = Math.max(reach * 0.92 + 5, fitW, rmax * 1.6 + 6), stacked = prefs.mode === "stack";
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
    if (d.rx != null || d.tx != null) rows.push(["Traffic", `↓ ${formatRate(d.rx || 0)}  ↑ ${formatRate(d.tx || 0)}`]);
    if (d.tags && d.tags.length) rows.push(["Tags", d.tags.join(", ")]);
    const bh = behind.get(d.key);
    if (bh) rows.push(["Behind it", `${bh.count} device${bh.count === 1 ? "" : "s"}${bh.offline ? `, ${bh.offline} offline` : ""}`]);
    if (d.exposure && d.exposure.risks.length) for (const r of d.exposure.risks) rows.push(["Exposure", r.label]);
    const ux = usage && usage.byKey.get(d.key);
    if (ux) rows.push([`Moved (${windowOf(prefs.usageHours).label.toLowerCase()})`, `↓ ${formatBytes(ux.rx)}  ↑ ${formatBytes(ux.tx)}`]);
    const acts = actionsFor(d) || [];
    card.innerHTML = `<button type="button" class="v3d-x" data-v3d="cardX" aria-label="Close">×</button>
      <h2>${esc(d.name === "—" ? "Unnamed device" : d.name)}</h2><div class="v3d-sub">${esc(d.ip)}</div>
      <dl>${rows.map(r => `<dt>${esc(r[0])}</dt><dd>${esc(r[1])}</dd>`).join("")}</dl>
      ${d.services && d.services.length ? `<div class="v3d-svcs">${d.services.map(s => `<div><i class="s-${esc(s.state)}"></i>${esc(s.name)} <b>${esc(s.state === "off" ? "device off" : s.state)}</b>${s.uptime != null ? ` · ${esc(s.uptime)}%` : ""}</div>`).join("")}</div>` : ""}
      ${d.odd ? `<div class="v3d-odd"><b>${esc(d.odd.title)}</b><br>${esc(d.odd.detail)}</div>` : ""}
      <div class="v3d-row"><button type="button" class="v3d-btn" data-v3d="history">History</button>${prefs.free ? '<button type="button" class="v3d-btn" data-v3d="flyto">Fly to it</button>' : ""}${bh ? '<button type="button" class="v3d-btn" data-v3d="behind">Show what\'s behind</button>' : ""}<button type="button" class="v3d-btn" data-v3d="whatif">${outage && outage.key === d.key ? "End simulation" : "What if it dies?"}</button></div>
      ${acts.length ? `<div class="v3d-acts">${acts.map(a => `<button type="button" class="v3d-act" data-v3d-act="${esc(a.id)}" title="${esc(a.title || "")}">${esc(a.label)}</button>`).join("")}</div>` : ""}`;
    card.hidden = false;
  }
  function select(e, fly_ = true) {
    selected = e;
    ents.forEach(x => { x.behindHot = false; });
    if (!e) { card.hidden = true; onSelect(null); return; }
    onSelect(e.d);
    showCard(e);
    if (!fly_) return;
    flyTo(e);
  }
  function flyTo(e) { flyToPos(e.group.position); }
  function flyToPos(pos) {
    const to = pos.clone();
    const dir = camera.position.clone().sub(controls.target); dir.y = 0;
    if (dir.lengthSq() < 0.01) dir.set(0, 0, 1);
    dir.normalize().multiplyScalar(houseK < 1 ? 4.2 : 7.5).setY(houseK < 1 ? 2.3 : 3.6);
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
    const hit = ray.intersectObjects(pickables, false).find(h => h.object.userData.ent.group.visible);
    hovered = hit ? hit.object.userData.ent : null;
    renderer.domElement.style.cursor = hovered ? "pointer" : "grab";
  });
  renderer.domElement.addEventListener("pointerdown", ev => { down = [ev.clientX, ev.clientY]; });
  renderer.domElement.addEventListener("pointerup", ev => {
    if (!down || Math.hypot(ev.clientX - down[0], ev.clientY - down[1]) > 5) return;
    if (placing && inHouse()) { placeAt(ev); return; }
    select(hovered, !prefs.free);     // moving freely: a click picks it and says what it is, and the camera stays where you put it
  });
  const syncUi = () => {
    container.querySelectorAll("[data-v3d=side],[data-v3d=stack],[data-v3d=house]").forEach(b => b.classList.toggle("on", b.dataset.v3d === prefs.mode));
    for (const k of ["free", "spin", "labels", "flow", "services", "strain", "dests", "scans", "sound", "skyline", "xray", "weather"]) q(`input[data-v3d=${k}]`).checked = !!prefs[k];
    container.classList.toggle("v3d-nolabels", !prefs.labels);
  };
  container.addEventListener("click", ev => {
    const chip = ev.target.closest("[data-v3d-chip]");
    if (chip) {
      const k = chip.dataset.v3dChip;
      if (filt.on.has(k)) filt.on.delete(k); else filt.on.add(k);
      applyFilter(); return;
    }
    const act = ev.target.closest("[data-v3d-act]");
    if (act && selected) { onAction(act.dataset.v3dAct, selected.d); return; }
    const item = ev.target.closest("[data-v3d-go]");
    if (item) { const e = ents.get(item.dataset.v3dGo); if (e) select(e, true); return; }
    const plc = ev.target.closest("[data-v3d-pick]");
    if (plc) { const e = ents.get(plc.dataset.v3dPick); if (e) { placePick = e; renderPlacing(); say(`Click the floor where ${e.d.name} goes.`); } return; }
    const t = ev.target.closest("[data-v3d]"); if (!t) return;
    const a = t.dataset.v3d;
    if (a === "side" || a === "stack" || (a === "house" && house)) { setMode(a); }
    else if (a === "reset") { select(null); goHome(false); }
    else if (a === "full") toggleFull();
    else if (a === "cardX") select(null);
    else if (a === "history" && selected) onOpenDevice(selected.d);
    else if (a === "flyto" && selected) flyTo(selected);
    else if (a === "credits") q(".v3d-credits").hidden = false;
    else if (a === "creditsX") q(".v3d-credits").hidden = true;
    else if (a === "replay") startReplay();
    else if (a === "place") togglePlacing();
    else if (a === "behind" && selected) showBehind(selected);
    else if (a === "whatif" && selected) { if (outage && outage.key === selected.key) endOutage(); else startOutage(selected); }
    else if (a === "outageEnd") endOutage();
    else if (a === "brief") startBriefing();
    else if (a === "briefPause") { if (brief) { brief.paused = !brief.paused; renderBrief(); } }
    else if (a === "briefNext") nextStop(1);
    else if (a === "briefBack") nextStop(-1);
    else if (a === "briefStop") stopBrief();
    else if (a === "rpClose") stopReplay();
    else if (a === "rpPlay") { if (replay) { replay.playing = !replay.playing; syncReplayUi(); } }
    else if (a === "walk") toggleWalk();
    else if (a === "vr") enterVr();
  });
  function setMode(m) {
    prefs.mode = m; savePrefs(); syncUi();
    if (house) {
      house.group.visible = m === "house";
      house.group.traverse(o => { if (o.isCSS2DObject) o.visible = m === "house"; });
    }
    if (current) { update(current); goHome(false); }
    if (m === "house") {
      const placed = [...ents.values()].filter(e => e.inHouse).length;
      if (ents.size && placed < ents.size) say(`${placed} of ${ents.size} devices are placed on a floor plan. The others aren't shown in the house; Floor plan, Place devices puts them in.`);
    }
  }
  container.addEventListener("input", ev => {
    const t = ev.target;
    if (t.matches && t.matches("input[data-v3d=q]")) { filt.q = t.value; applyFilter(); return; }
    if (t.matches && t.matches("input[data-v3d=rpScrub]") && replay) {
      replay.t = replay.from + (replay.to - replay.from) * (t.value / 1000); replay.playing = false; syncReplayUi(); pushReplay(true);
    }
  });
  let lastExtras = {};
  const setExtrasKept = x => { lastExtras = x; setExtras(x); };
  container.addEventListener("change", ev => {
    if (ev.target.matches && ev.target.matches("select[data-v3d=tag]")) { filt.tag = ev.target.value; applyFilter(); return; }
    if (ev.target.matches && ev.target.matches("select[data-v3d=rpSpeed]")) { if (replay) replay.speed = replaySpeed(replay.to - replay.from, Number(ev.target.value) || 60); return; }
    if (ev.target.matches && ev.target.matches("select[data-v3d=rpRange]")) {
      prefs.replayHours = Number(ev.target.value) || 24; savePrefs();
      if (replay) { stopReplayQuiet(); startReplay(); }
      return;
    }
    if (ev.target.matches && ev.target.matches("select[data-v3d=usageWin]")) { prefs.usageHours = Number(ev.target.value) || 24; savePrefs(); usageData = null; refreshUsage(); onWantsChange(); return; }
    const t = ev.target.closest("input[data-v3d]"); if (!t) return;
    const k = t.dataset.v3d;
    if (k === "rpScrub") return;
    prefs[k] = t.checked; if (k !== "sound") savePrefs();
    if (k === "spin") controls.autoRotate = t.checked && !reduced;
    if (k === "free") { applyFree(); if (selected) showCard(selected); }
    if (k === "sound") {
      if (t.checked) sound.enable().then(ok => { if (!ok) { prefs.sound = false; syncUi(); say("This browser can't play sound."); } else sound.cue("arrive"); });
      else sound.disable();
    }
    if (k === "services") extras.setServices(prefs.services);
    if (k === "dests" || k === "scans") setExtras(lastExtras);
    if (k === "strain" && current) for (const e of ents.values()) styleLabel(e);
    if (k === "skyline") { if (!prefs.skyline) usageData = null; refreshUsage(); onWantsChange(); }
    if (k === "xray") { xray.setOn(prefs.xray); xray.refresh(); onWantsChange(); }
    if (k === "weather") { applyWeather(); onWantsChange(); }
    syncUi();
  });
  syncUi();

  // ---------- finding, and what needs looking at ----------
  const findEl = q(".v3d-find"), attnEl = q(".v3d-attn"), placingEl = q(".v3d-placing");
  const searchEl = q("input[data-v3d=q]");
  function applyFilter() {
    filterOn = !!(filt.q.trim() || filt.on.size || filt.tag);
    let shown = 0;
    ents.forEach(e => { e.dim = filterOn && !matchesFilter(e.d, filt); if (!e.dim) shown++; });
    container.querySelectorAll("[data-v3d-chip]").forEach(b => { const on = filt.on.has(b.dataset.v3dChip); b.classList.toggle("on", on); b.setAttribute("aria-pressed", String(on)); });
    q(".v3d-count").textContent = filterOn ? `${shown} of ${ents.size} shown` : ents.size ? `${ents.size} devices` : "";
  }
  function renderFindTags() {
    const sel = q("select[data-v3d=tag]"), have = new Set();
    ents.forEach(e => (e.d.tags || []).forEach(t => have.add(t)));
    const list = [...have].sort((a, b) => a.localeCompare(b));
    const sig = list.join("|");
    if (sel.dataset.sig === sig) return;
    sel.dataset.sig = sig;
    sel.innerHTML = `<option value="">Any tag</option>` + list.map(t => `<option value="${esc(t)}">${esc(t)}</option>`).join("");
    sel.value = list.includes(filt.tag) ? filt.tag : "";
    findEl.classList.toggle("v3d-notags", list.length === 0);
  }
  function renderAttention() {
    const list = problemsOf(current ? current.devices : [], extras.scanningKeys());
    problemsList = list;
    if (!list.length) { attnEl.hidden = true; return; }
    const top = list.slice(0, 6);
    attnEl.innerHTML = `<h3>Needs attention <span class="v3d-n">${list.length}</span></h3><ul>` +
      top.map(p => `<li><button type="button" data-v3d-go="${esc(p.key)}" class="v3d-prob v3d-p-${esc(p.kind)}" title="${esc(p.detail)}"><i></i>${esc(p.title)}</button></li>`).join("") +
      `</ul>${list.length > top.length ? `<div class="v3d-more-n">and ${list.length - top.length} more</div>` : ""}`;
    attnEl.hidden = false;
  }
  searchEl.addEventListener("keydown", ev => {
    if (ev.key === "Escape") { searchEl.value = ""; filt.q = ""; applyFilter(); searchEl.blur(); ev.stopPropagation(); return; }
    if (ev.key !== "Enter") return;
    // fly to the best match
    let best = null, bestRank = 0;
    ents.forEach(e => { const r = searchRank(e.d, searchEl.value); if (r > bestRank && !e.leaving) { best = e; bestRank = r; } });
    if (best) select(best, true); else say("No device matches that.");
  });
  // what is behind a switch or access point: its devices light up, and the rest dim a little
  function showBehind(e) {
    const bh = behind.get(e.d.key);
    ents.forEach(x => { x.behindHot = !!bh && bh.keys.includes(x.d.key); });
    if (bh) say(`${bh.count} device${bh.count === 1 ? "" : "s"} behind ${e.d.name}${bh.offline ? `, ${bh.offline} offline` : ""}. Their lines run to it.`);
  }

  // ---------- placing devices by clicking the house ----------
  function renderPlacing() {
    const on = placing && inHouse();
    placingEl.hidden = !on;
    q("[data-v3d=place]").hidden = !inHouse();
    q("[data-v3d=place]").setAttribute("aria-pressed", String(!!placing));
    q("[data-v3d=place]").classList.toggle("on", !!placing);
    if (!on) return;
    const out = [...ents.values()].filter(e => !e.inHouse && !e.leaving && !e.d.remote).sort((a, b) => a.d.name.localeCompare(b.d.name));
    placingEl.innerHTML = `<h3>Place devices</h3><div class="v3d-hint">${placePick ? `Click the floor where <b>${esc(placePick.d.name)}</b> goes.` : "Pick a device, then click where it goes. Click a device in the house to move it."}</div>` +
      (out.length ? `<ul>${out.slice(0, 14).map(e => `<li><button type="button" data-v3d-pick="${esc(e.key)}" class="v3d-prob${placePick === e ? " on" : ""}"><i></i>${esc(e.d.name)}</button></li>`).join("")}</ul>${out.length > 14 ? `<div class="v3d-more-n">and ${out.length - 14} more</div>` : ""}` : `<div class="v3d-hint">Every device is placed.</div>`);
  }
  function togglePlacing() {
    placing = !placing; placePick = null;
    if (placing && !inHouse()) { placing = false; say("Switch to Your house first."); }
    renderPlacing();
  }
  // where on a floor the pointer is: the nearest floor whose plan the ray lands inside
  function floorHit(ev) {
    if (!houseInfo) return null;
    const r = renderer.domElement.getBoundingClientRect();
    mouse.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
    ray.setFromCamera(mouse, camera);
    let best = null;
    houseInfo.floors.forEach((f, i) => {
      const y = i * FLOOR_GAP - 0.62, dy = ray.ray.direction.y;
      if (Math.abs(dy) < 1e-6) return;
      const t = (y - ray.ray.origin.y) / dy;
      if (t <= 0) return;
      const x = ray.ray.origin.x + ray.ray.direction.x * t, z = ray.ray.origin.z + ray.ray.direction.z * t;
      const raw = placementFromPoint(x, z, f.plan);
      if (raw.x <= 0 || raw.x >= 1 || raw.y <= 0 || raw.y >= 1) return;
      if (!best || t < best.t) best = { t, floor: f, x: raw.x, y: raw.y };
    });
    return best;
  }
  function placeAt(ev) {
    if (hovered && !hovered.leaving) { placePick = hovered; renderPlacing(); say(`Click the floor where ${hovered.d.name} goes.`); return; }
    if (!placePick) { say("Pick a device from the list first."); return; }
    const hit = floorHit(ev);
    if (!hit) { say("Click inside the house."); return; }
    const e = placePick; placePick = null;
    if (onPlace) Promise.resolve(onPlace(e.d, hit.floor.id, hit.x, hit.y)).then(ok => { if (ok !== false) say(`${e.d.name} is placed.`); renderPlacing(); });
  }

  // Centred: drag turns the scene about its middle. Free: drag slides across it, right-drag turns about the point you're looking at, the
  // wheel zooms toward the pointer, and nothing pulls the view back to the middle.
  function applyFree() {
    const free = !!prefs.free;
    controls.mouseButtons = free ? { LEFT: THREE.MOUSE.PAN, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.ROTATE } : { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
    controls.touches = free ? { ONE: THREE.TOUCH.PAN, TWO: THREE.TOUCH.DOLLY_ROTATE } : { ONE: THREE.TOUCH.ROTATE, TWO: THREE.TOUCH.DOLLY_PAN };
    controls.screenSpacePanning = !free;     // free slides along the ground, as walking over the map would
    controls.zoomToCursor = free;
    controls.minDistance = free ? 1.6 : 4;
    q(".v3d-note").textContent = free
      ? "drag to move · right-drag to turn · scroll to zoom · W A S D to glide · Q E down, up · F for full screen"
      : "drag to orbit · scroll to zoom · click a device · F for full screen";
  }
  applyFree();

  // ---------- replay the day ----------
  let replay = null;
  const replayClock = ms => {
    const long = replay && replay.to - replay.from > 36 * 3600e3;
    return long ? new Date(ms).toLocaleString([], { weekday: "short", hour: "numeric", minute: "2-digit" }) : new Date(ms).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  };
  function renderMarkers() {
    const box = q(".v3d-rpmarks");
    box.innerHTML = replay ? markersOf(replay.tl).map(m => `<i class="v3d-mk v3d-mk-${m.kind}" style="left:${(m.at * 100).toFixed(2)}%" title="${esc(m.label)}"></i>`).join("") : "";
  }
  function syncReplayUi() {
    const bar = q(".v3d-replay");
    bar.hidden = !replay;
    container.classList.toggle("v3d-replaying", !!replay);
    if (!replay) return;
    q("[data-v3d=rpPlay]").textContent = replay.playing ? "❚❚" : "▶";
    q("[data-v3d=rpPlay]").setAttribute("aria-label", replay.playing ? "Pause" : "Play");
    q("[data-v3d=rpScrub]").value = String(Math.round(((replay.t - replay.from) / (replay.to - replay.from)) * 1000));
    q("[data-v3d=rpRange]").value = String(prefs.replayHours);
    q(".v3d-rptime").textContent = replayClock(replay.t) + (replay.down ? "  · internet down" : "");
  }
  function pushReplay(force) {
    if (!replay || !replayScene) return;
    const now = performance.now();
    if (!force && now - replay.pushed < 120) return;
    replay.pushed = now;
    const r = replayScene(replay.t);
    if (!r) return;
    replay.down = !!r.internetDown;
    update(r.scene, { internetDown: replay.down });
    setExtras({ destinations: [], scans: [] });
    syncReplayUi();
  }
  async function startReplay() {
    if (replay || !onReplayLoad) return;
    const btn = q("[data-v3d=replay]");
    btn.disabled = true;
    let tl = null;
    try { tl = await onReplayLoad(prefs.replayHours); } catch { tl = null; }
    btn.disabled = false;
    if (!tl || !tl.hosts || !tl.hosts.length) { say("There isn't enough history yet to replay."); return; }
    const from = Date.parse(tl.from), to = Date.parse(tl.to);
    replay = { tl, from, to, t: from, playing: true, speed: replaySpeed(to - from, Number(q("[data-v3d=rpSpeed]").value) || 60), pushed: 0, down: false };
    select(null); endOutage(); stopBrief(); applyWeather(); renderMarkers();
    pushReplay(true);
  }
  function stopReplay() {
    if (!replay) return;
    replay = null; syncReplayUi(); applyWeather(); onReplayEnd();
  }
  function stopReplayQuiet() { replay = null; }

  // ---------- usage: the skyline's window and its list ----------
  const usageEl = q(".v3d-usage");
  const windowOf = h => USAGE_WINDOWS.find(w => w.hours === h) || USAGE_WINDOWS[1];
  function setUsage(d) { usageData = d; refreshUsage(); }
  function refreshUsage() {
    usage = usageData && current ? usageOf(current.devices, usageData.devices || [], 5) : null;
    skyline.set(usage, prefs.skyline);
    renderUsage();
    if (selected) showCard(selected);
  }
  function renderUsage() {
    usageEl.hidden = !prefs.skyline;
    if (!prefs.skyline) return;
    const rows = usage ? usage.top : [];
    const sig = prefs.usageHours + "|" + rows.map(r => r.key + ":" + r.total).join(",") + "|" + (usageData ? 1 : 0);
    if (usageEl.dataset.sig === sig) return;
    usageEl.dataset.sig = sig;
    usageEl.innerHTML = `<h3>Usage skyline</h3>
      <select data-v3d="usageWin" aria-label="How far back">${USAGE_WINDOWS.map(w => `<option value="${w.hours}"${w.hours === prefs.usageHours ? " selected" : ""}>${w.label}</option>`).join("")}</select>` +
      (rows.length
        ? `<ul>${rows.map(r => `<li><button type="button" data-v3d-go="${esc(r.key)}" class="v3d-prob"><i></i>${esc(r.name)} <b>${formatBytes(r.total)}</b></button></li>`).join("")}</ul>
           <div class="v3d-more-n">${formatBytes(usage.total)} in all. <span class="v3d-key"><i style="background:#26d9ff"></i>in <i style="background:#ff2fd0"></i>out</span></div>`
        : `<div class="v3d-hint">${usageData ? "Nothing measured in that time. It needs the traffic monitor on, or switch or router counters." : "Loading…"}</div>`);
  }

  // ---------- what if it died ----------
  const outageEl = q(".v3d-outage");
  function startOutage(e) {
    if (replay || !current) return;
    endOutage();
    const info = outageOf(current.devices, e.key);
    if (!info) return;
    const ring = new THREE.Mesh(new THREE.RingGeometry(0.92, 1, 64), new THREE.MeshBasicMaterial({ color: new THREE.Color(0xff4b5c).multiplyScalar(1.8), transparent: true, depthWrite: false }));
    ring.rotation.x = -Math.PI / 2; ring.position.y = -0.5; e.group.add(ring);
    outage = { key: e.key, info, cutSet: new Set(info.cut), ring, ent: e };
    renderOutage();
    if (selected) showCard(selected);
  }
  function endOutage() {
    if (!outage) return;
    const r = outage.ring; if (r) { r.parent && r.parent.remove(r); r.geometry.dispose(); r.material.dispose(); }
    outage = null; renderOutage();
    if (selected) showCard(selected);
  }
  // The devices change under it: work out again who depends on what, or stop if the device has gone.
  function refreshOutage() {
    if (!outage) return;
    const info = current ? outageOf(current.devices, outage.key) : null;
    if (!info || !ents.get(outage.key)) { endOutage(); return; }
    outage.info = info; outage.cutSet = new Set(info.cut); renderOutage();
  }
  function renderOutage() {
    outageEl.hidden = !outage;
    container.classList.toggle("v3d-simulating", !!outage);
    if (!outage) return;
    const o = outage.info;
    outageEl.innerHTML = `<h3>What if ${esc(o.name)} died?</h3><div class="v3d-text">${esc(outageText(o))}</div>
      <div class="v3d-hint">${o.reason === "internet" ? "Amber is cut off from the internet." : o.reason === "network" ? "Dark red has lost the network." : ""} Nothing is changed: this is only a picture.</div>
      <button type="button" class="v3d-btn" data-v3d="outageEnd">End simulation</button>`;
  }

  // ---------- the briefing: a flight through what happened ----------
  const briefEl = q(".v3d-brief");
  async function startBriefing() {
    if (brief || replay || !current) return;
    if (!current.devices.length) { say("There's nothing to fly through yet."); return; }
    const btn = q("[data-v3d=brief]");
    btn.disabled = true;
    let data = null;
    try { data = onBriefingLoad ? await onBriefingLoad() : null; } catch { data = null; }
    btn.disabled = false;
    if (brief || !current) return;
    const stops = briefingOf({ devices: current.devices, problems: problemsList, timeline: data && data.timeline, alerts: (data && data.alerts) || [], nowMs: Date.now() });
    const n = current.devices.length, off = current.devices.filter(d => d.state === "off").length;
    const list = [{ key: null, kind: "intro", title: "Today on your network", detail: `${n} device${n === 1 ? "" : "s"}${off ? `, ${off} offline` : ""}. ${stops.length ? `${stops.length} thing${stops.length === 1 ? "" : "s"} to look at.` : "Nothing to report: it was a quiet day."}` }, ...stops];
    if (stops.length) list.push({ key: null, kind: "outro", title: "That's the day", detail: "Nothing else changed." });
    select(null); endOutage();
    brief = { list, i: -1, wait: 0, paused: false };
    nextStop(1);
  }
  function nextStop(dir) {
    if (!brief) return;
    const i = brief.i + dir;
    if (i >= brief.list.length || i < 0) { if (dir > 0) stopBrief(); return; }
    ents.forEach(e => { e.briefHot = false; });
    brief.i = i;
    const s = brief.list[i];
    brief.wait = s.kind === "intro" || s.kind === "outro" ? 4.5 : 6;
    brief.paused = false;
    // Where to look: the device itself, the internet for an outage, the gateway for an alert about the network, else the whole scene.
    const e = s.key && s.key !== "internet" ? ents.get(s.key) : s.kind === "alert" ? [...ents.values()].find(x => x.d.gw && !x.leaving) : null;
    if (s.key === "internet") flyToPos(internet.position);
    else if (e) { e.briefHot = true; flyTo(e); }
    else goHome(false);
    renderBrief();
  }
  function stepBrief(dt) {
    if (!brief || brief.paused) return;
    brief.wait -= dt;
    if (brief.wait <= 0) nextStop(1);
  }
  function stopBrief() {
    if (!brief) return;
    ents.forEach(e => { e.briefHot = false; });
    brief = null; renderBrief(); goHome(false);
  }
  function renderBrief() {
    briefEl.hidden = !brief;
    container.classList.toggle("v3d-briefing", !!brief);
    if (!brief) return;
    const s = brief.list[brief.i];
    briefEl.innerHTML = `<div class="v3d-brief-top"><span class="v3d-kind v3d-k-${esc(s.kind)}">${esc(({ intro: "Today", outro: "Done", down: "Down", service: "Service", scan: "Scan", odd: "Unusual", new: "New", slow: "Slow", alert: "Alert", internet: "Internet" })[s.kind] || "Note")}</span><span class="v3d-step">${brief.i + 1} of ${brief.list.length}</span></div>
      <h3>${esc(s.title)}</h3><div class="v3d-text">${esc(s.detail || "")}</div>
      <div class="v3d-row"><button type="button" class="v3d-btn" data-v3d="briefBack">Back</button><button type="button" class="v3d-btn" data-v3d="briefPause">${brief.paused ? "Play" : "Pause"}</button><button type="button" class="v3d-btn" data-v3d="briefNext">Next</button><button type="button" class="v3d-btn" data-v3d="briefStop">Stop</button></div>`;
  }

  // ---------- the weather over it ----------
  function applyWeather() {
    const w = prefs.weather && wxScore != null && !replay ? weatherOf(wxScore) : null;
    weather.set(w, !!w, lite);
    const el = q(".v3d-wx");
    el.hidden = !w;
    if (w) el.textContent = `Sky: ${w.label} · score ${Math.round(w.score)}`;
  }
  function setWeather(score) { wxScore = score == null ? null : Number(score); applyWeather(); }

  // ---------- walking around ----------
  // Ground level, a person's height up: W A S D or the arrows to move, drag to look, Shift to hurry, Esc to stop.
  let walk = null;
  const keysDown = new Set();
  function toggleWalk() {
    const b = q("[data-v3d=walk]");
    if (walk) {
      walk = null; controls.enabled = true; b.setAttribute("aria-pressed", "false"); b.textContent = "Walk around";
      container.classList.remove("v3d-walking");
      goHome(true);
      return;
    }
    select(null);
    // Start at the edge of the house, or of the biggest platform, facing in.
    const base = -0.62;
    let to;
    if (inHouse()) to = new THREE.Vector3(house.bounds.cx, base + 1.0, house.bounds.cz + house.bounds.depth / 2 + 1.6);
    else {
      const big = [...nets.values()].sort((a, b) => b.Rt - a.Rt)[0];
      to = big ? new THREE.Vector3(big.center.x, base + 1.0, big.center.z + big.Rt + 1.2) : new THREE.Vector3(0, base + 1.0, 8);
    }
    walk = { pos: to, yaw: 0, pitch: -0.12, base };
    camera.position.copy(to); fly = null;
    controls.enabled = false;
    b.setAttribute("aria-pressed", "true"); b.textContent = "Stop walking";
    container.classList.add("v3d-walking");
    stage.focus();
    say("W A S D or the arrow keys to move, drag to look, Shift to hurry, Esc to stop.");
  }
  let look = null;
  renderer.domElement.addEventListener("pointerdown", ev => { if (walk) look = [ev.clientX, ev.clientY]; });
  addEventListener("pointerup", () => { look = null; });
  addEventListener("pointermove", ev => {
    if (!walk || !look) return;
    walk.yaw -= (ev.clientX - look[0]) * 0.004; walk.pitch = Math.max(-1.2, Math.min(1.2, walk.pitch - (ev.clientY - look[1]) * 0.004));
    look = [ev.clientX, ev.clientY];
  });
  let pointerIn = false;
  container.addEventListener("pointerenter", () => { pointerIn = true; });
  container.addEventListener("pointerleave", () => { pointerIn = false; });
  const MOVE_KEYS = ["w", "a", "s", "d", "q", "e", "arrowup", "arrowdown", "arrowleft", "arrowright", "shift"];
  const walkKey = ev => {
    const k = ev.key.toLowerCase();
    if (ev.type === "keyup") { keysDown.delete(k); return; }
    if (walk && k === "escape") { toggleWalk(); return; }
    // Only while the view has the keyboard, and never while something is being typed into.
    const tag = (document.activeElement && document.activeElement.tagName) || "";
    if (/^(INPUT|SELECT|TEXTAREA)$/.test(tag) && !(walk && container.contains(document.activeElement))) return;
    if (ev.ctrlKey || ev.metaKey || ev.altKey) return;
    if (!walk && !(pointerIn || container.contains(document.activeElement))) return;
    if (MOVE_KEYS.includes(k)) { keysDown.add(k); if (k !== "shift") ev.preventDefault(); }
  };
  window.addEventListener("blur", () => keysDown.clear());
  document.addEventListener("keydown", walkKey); document.addEventListener("keyup", walkKey);
  // Glide: the camera and the point it looks at move together, along the way the camera is facing, so nothing is pulled back to the middle.
  const flat = new THREE.Vector3(), side = new THREE.Vector3(), glide = new THREE.Vector3();
  function stepGlide(dt) {
    if (walk || vrOn) return;
    const has = k => keysDown.has(k);
    const f = (has("w") || has("arrowup") ? 1 : 0) - (has("s") || has("arrowdown") ? 1 : 0);
    const r = (has("d") || has("arrowright") ? 1 : 0) - (has("a") || has("arrowleft") ? 1 : 0);
    const u = (has("e") ? 1 : 0) - (has("q") ? 1 : 0);
    if (!f && !r && !u) return;
    fly = null;
    camera.getWorldDirection(flat); flat.y = 0;
    if (flat.lengthSq() < 1e-6) flat.set(0, 0, -1);
    flat.normalize();
    side.set(-flat.z, 0, flat.x);
    const dist = Math.max(4, camera.position.distanceTo(controls.target));
    const speed = dist * 0.9 * (has("shift") ? 2.6 : 1) * dt;
    glide.set(0, 0, 0).addScaledVector(flat, f * speed).addScaledVector(side, r * speed);
    glide.y = u * speed * 0.7;
    camera.position.add(glide); controls.target.add(glide);
  }
  // Kept inside a box round the scene, so a slide can't lose it; the camera goes with the point it looks at.
  const clampTo = new THREE.Vector3();
  function keepInside() {
    const half = Math.max(24, reach * 1.8), top = inHouse() ? house.bounds.height + 12 : Math.max(14, reach);
    const t = controls.target;
    clampTo.set(Math.max(-half, Math.min(half, t.x)), Math.max(-2, Math.min(top, t.y)), Math.max(-half, Math.min(half, t.z)));
    if (clampTo.distanceToSquared(t) > 1e-6) { clampTo.sub(t); camera.position.add(clampTo); t.add(clampTo); }
  }

  function stepWalk(dt) {
    if (!walk) return;
    const sp = (keysDown.has("shift") ? 9 : 4.2) * dt;
    const fx = -Math.sin(walk.yaw), fz = -Math.cos(walk.yaw);
    let mx = 0, mz = 0;
    if (keysDown.has("w") || keysDown.has("arrowup")) { mx += fx; mz += fz; }
    if (keysDown.has("s") || keysDown.has("arrowdown")) { mx -= fx; mz -= fz; }
    if (keysDown.has("a") || keysDown.has("arrowleft")) { mx += fz; mz -= fx; }
    if (keysDown.has("d") || keysDown.has("arrowright")) { mx -= fz; mz += fx; }
    walk.pos.x += mx * sp; walk.pos.z += mz * sp;
    camera.position.copy(walk.pos);
    camera.rotation.order = "YXZ"; camera.rotation.set(walk.pitch, walk.yaw, 0);
  }

  // ---------- a headset ----------
  // WebXR where the browser and a headset offer it. The post-processing is for a flat screen, so inside the headset the
  // scene is drawn straight; the names (which are page elements) don't come with it.
  let vrOn = false;
  if (navigator.xr && navigator.xr.isSessionSupported) {
    navigator.xr.isSessionSupported("immersive-vr").then(ok => { if (ok) q("[data-v3d=vr]").hidden = false; }).catch(() => {});
  }
  async function enterVr() {
    if (vrOn || !navigator.xr) return;
    try {
      const session = await navigator.xr.requestSession("immersive-vr", { optionalFeatures: ["local-floor"] });
      renderer.xr.enabled = true;
      await renderer.xr.setSession(session);
      vrOn = true;
      select(null);
      // stand at the middle of the first platform, or the house, at the floor
      const rig = new THREE.Group(); rig.position.set(0, inHouse() ? 0 : 0.2, inHouse() ? 0 : reach * 0.4);
      scene.add(rig); rig.add(camera);
      cancelAnimationFrame(raf);
      renderer.setAnimationLoop(() => { frameXr(); });
      session.addEventListener("end", () => {
        vrOn = false; renderer.setAnimationLoop(null); renderer.xr.enabled = false;
        rig.remove(camera); scene.remove(rig); camera.position.set(0, 0, 0);
        goHome(true);
        if (active) raf = requestAnimationFrame(frame);
      });
    } catch (err) { say("The headset couldn't start: " + (err && err.message ? err.message : "no VR session")); }
  }

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
    if (ev.key === "Escape" && (brief || outage) && container.contains(document.activeElement)) { if (brief) stopBrief(); else endOutage(); }
    else if (ev.key === "Escape" && container.classList.contains("v3d-full")) toggleFull();
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
    step();
    stepWalk(lastDt);
    stepGlide(lastDt);
    if (!walk) { controls.update(); if (prefs.free) keepInside(); }
    composer.render();   // the bloom pass sits out above the lite limit; the colour handling still needs the rest
    labelRenderer.render(scene, camera);
  }
  function frameXr() {
    step();
    renderer.render(scene, camera);
  }
  let lastDt = 0.016;
  function step() {
    const dt = Math.min(clock.getDelta(), 0.05), time = clock.elapsedTime, now = performance.now();
    lastDt = dt;
    if (replay && replay.playing) {
      replay.t = Math.min(replay.to, replay.t + dt * 1000 * replay.speed);
      pushReplay(false);
      if (replay.t >= replay.to) { replay.playing = false; syncReplayUi(); }
    }
    houseK += ((inHouse() ? 0.8 : 1) - houseK) * (1 - Math.exp(-4 * dt));
    const houseMode = inHouse();
    T.value = time * (reduced ? 0.25 : 1);
    const k = 1 - Math.exp(-3.2 * dt);
    nets.forEach(n => {
      n.center.lerp(n.target, k); n.R += (n.Rt - n.R) * k;
      n.group.position.set(n.center.x, n.center.y - 0.62, n.center.z);
      n.disc.scale.set(n.R, n.R, 1); n.ring.scale.set(n.R, n.R, 1); n.guide.scale.set(n.R * 0.52, n.R * 0.52, 1); n.fx.scale.set(n.R, n.R, 1);
      n.lbl.position.set(0, 0.4, n.R + 0.9);
    });
    nets.forEach(n => { n.group.visible = !houseMode; n.lbl.visible = !houseMode; });
    internet.position.lerp(internetTarget, k);
    iAura.material.color.lerp(tc.set(internetDown || (outage && outage.info && outage.info.internet) ? 0xff4b5c : CYAN).multiplyScalar(0.9), 1 - Math.exp(-6 * dt));
    iHalo.rotation.z = time * 0.5; iHalo2.rotation.z = -time * 0.35; iEdges.rotation.y = time * 0.3; iCore.rotation.y = time * 0.3;
    iAura.material.opacity = 0.45 + Math.sin(time * 1.6) * 0.1;

    const odd = new Set();
    for (const e of [...ents.values()]) {
      e.group.visible = !houseMode || !!e.inHouse;
      if (e.leaving) {
        const t = (now - e.leaving) / 450; e.scale = Math.max(0, 1 - t); e.group.scale.setScalar(e.scale * houseK);
        if (t >= 1) { dropEnt(e); linkSig = ""; continue; }
      }
      e.pos.lerp(e.target, 1 - Math.exp(-2.6 * dt));
      const off = e.d.state === "off", still = reduced;
      const dead = !!(outage && outage.info && outage.info.key === e.key);
      const cutKind = !dead && outage && outage.info && outage.cutSet.has(e.key) ? outage.info.reason : null, sunk = dead || cutKind === "network";
      // strain: a device working hard to answer rides higher and flickers faster, the more so the slower it is
      const strain = prefs.strain && !off ? e.d.strain || 0 : 0;
      const lift = strain * 0.55 * houseK;
      const bob = off || still ? 0 : Math.sin(time * (1.3 + strain * 5) + e.phase) * (0.07 + strain * 0.09);
      e.group.position.set(e.pos.x, e.pos.y + bob + lift + (off || sunk ? -0.28 : 0), e.pos.z);
      if (!off && !still && !sunk) e.group.rotation.y += dt * 0.15;
      tc.set(COLORS[e.d.state]).multiplyScalar(STATE_GLOW[e.d.state]);
      if (dead) tc.set(0xff4b5c).multiplyScalar(2.1); else if (cutKind === "network") tc.set(0x7a2535); else if (cutKind === "internet") tc.set(0xc98a2a).multiplyScalar(0.9);
      e.col.lerp(tc, 1 - Math.exp(-5 * dt));
      const hot = e === hovered || e === selected || !!e.behindHot || !!e.briefHot;
      const xd = e.xdim && e !== selected ? 0.3 : 1;
      const dim = !!e.dim && e !== selected;
      const flicker = strain && !still ? 1 + strain * 0.45 * Math.sin(time * (4 + strain * 14) + e.phase) : 1;
      e.edges.material.uniforms.col.value.copy(e.col); e.edges.material.uniforms.gain.value = (e.dens || 1) * (off ? 0.9 : hot ? 1.05 : 1) * flicker * (dim ? 0.16 : 1) * xd * (cutKind === "network" ? 0.55 : 1) * (dead ? 1.3 + (reduced ? 0 : 0.3 * Math.sin(time * 6)) : 1);
      e.body.material.uniforms.col.value.copy(e.col); e.body.material.uniforms.op.value = dim || xd < 1 ? 0.06 : off || sunk ? 0.22 : 0.42; e.body.material.uniforms.gainRim.value = Math.min(1, (e.dens || 1) * 1.2);
      e.pad.material.uniforms.col.value.copy(e.col); e.pad.material.uniforms.off.value = off ? 1 : 0;
      e.pad.material.uniforms.hot.value += ((hot ? 1 : 0) - e.pad.material.uniforms.hot.value) * (1 - Math.exp(-8 * dt));
      if (e.ghosts) {
        const burst = !reduced && fract(Math.sin(Math.floor(time * 6 + e.phase * 3) * 91.7) * 4375.5) > 0.9;
        e.ghosts.forEach((m, i) => { m.visible = burst; if (burst) m.position.set((i ? 1 : -1) * 0.05 * (0.5 + Math.random()), (Math.random() - 0.5) * 0.04, (Math.random() - 0.5) * 0.04); });
      }
      if (!e.leaving) { e.scale += ((hot ? 1.25 : 1) - e.scale) * (1 - Math.exp(-10 * dt)); e.group.scale.setScalar(e.scale * houseK); }
      // Names only where they can be read: the nearer ones, always the gateways and anything unusual or picked.
      const near = camera.position.distanceTo(e.group.position) < (lite ? 14 : 26);
      e.pad.visible = !dim;
      if (e.beacon) { e.beacon.ripple.visible = !dim; e.beacon.beam.visible = !dim; }
      const showName = prefs.labels && e.group.visible && !dim && (filterOn || e.d.gw || e.d.odd || hot || near);
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
      L.line.visible = ents.get(L.e.key) === L.e && !L.e.leaving && !houseMode;   // a device that's gone takes its line with it
      const off = L.e.d.state === "off";
      L.line.material.opacity = L.curved ? 1 : off ? 0.16 : L.e.dim ? 0.05 : 0.38 + Math.min(9, L.e.d.rate || 1) * 0.045;
      L.line.material.color.set(L.curved ? 0x1a8fb8 : L.e.d.state === "odd" ? 0x7a3fc0 : off ? 0x1c2a3a : 0x147a9a);
      if (gone(L.e) || (outage && outage.info && outage.info.internet && L.curved && L.e.key === outage.key)) L.line.material.opacity = 0.07;
    });
    // traffic
    let n = 0;
    if (prefs.flow) for (const p of parts) {
      const L = p.L, d = L.e.d;
      if (d.state === "off" || L.e.leaving || houseMode || L.e.dim) continue;
      if (gone(L.e)) continue;
      const speed = (L.curved ? 0.22 : 0.08 + (d.rate || 1) * 0.045) * (reduced ? 0.4 : 1);
      p.t += dt * speed * p.dir; if (p.t > 1) p.t -= 1; if (p.t < 0) p.t += 1;
      pointOn(L, p.t, tmp);
      particlePos[n * 3] = tmp.x; particlePos[n * 3 + 1] = tmp.y; particlePos[n * 3 + 2] = tmp.z;
      tc.set(d.state === "unk" ? COLORS.unk : d.state === "odd" ? MAGENTA : L.curved ? CYAN : 0x6fffd0).multiplyScalar(1.6);
      particleCol[n * 3] = tc.r; particleCol[n * 3 + 1] = tc.g; particleCol[n * 3 + 2] = tc.b;
      if (++n >= MAX_PARTICLES) break;
    }
    pGeo.setDrawRange(0, n); pGeo.attributes.position.needsUpdate = true; pGeo.attributes.color.needsUpdate = true;

    extras.tick(time, dt, now, prefs);
    skyline.tick(time, dt, houseK, usage ? usage.top : null); xray.tick(time); weather.tick(time, dt); stepBrief(dt);
    if (outage && outage.ring) { const t = (time * 0.8) % 1; outage.ring.scale.setScalar(0.8 + t * 3); outage.ring.material.opacity = (1 - t) * 0.9; }
    if (fly && !walk) {
      fly.t = Math.min(1, fly.t + dt / 0.9); const e = ease(fly.t);
      controls.target.lerpVectors(fly.fromT, fly.toT, e); camera.position.lerpVectors(fly.fromP, fly.toP, e);
      if (fly.t >= 1) fly = null;
    }
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
    // The grid saver may have tinted the shared sky and floor (terminal green); this scene is always neon.
    if (on) { SKIN.mono.value = 0; SKIN.tint.value.setRGB(1, 1, 1); clock.getDelta(); resize(); raf = requestAnimationFrame(frame); }
    else cancelAnimationFrame(raf);
  }
  const onVisible = () => { if (document.hidden) cancelAnimationFrame(raf); else if (active) { clock.getDelta(); raf = requestAnimationFrame(frame); } };
  document.addEventListener("visibilitychange", onVisible);

  function destroy() {
    setActive(false); ro.disconnect(); document.removeEventListener("visibilitychange", onVisible);
    document.removeEventListener("keydown", walkKey); document.removeEventListener("keyup", walkKey);
    sound.dispose(); extras.dispose(); skyline.dispose(); xray.dispose(); weather.dispose(); if (house) house.dispose();
    document.removeEventListener("fullscreenchange", onFullChange); document.removeEventListener("keydown", onKey);
    container.classList.remove("v3d-full"); document.documentElement.classList.remove("v3d-lock");
    renderer.dispose(); controls.dispose(); container.innerHTML = "";
  }
  const api = {
    update, setActive, select: key => select(ents.get(String(key)) || null), destroy, get count() { return ents.size; }, _ents: ents, _prefs: prefs,
    setHouse, setExtras: setExtrasKept, get replaying() { return !!replay; }, stopReplay, get walking() { return !!walk; },
    focus: id => { const e = [...ents.values()].find(x => x.d.id === Number(id) && !x.leaving); if (e) { select(e, true); return true; } return false; },
    get problems() { return problemsList; }, _filter: filt, _applyFilter: applyFilter, get placing() { return placing; },
    get satellites() { return extras.satellites; }, get scanners() { return extras.scanners; }, get inHouse() { return inHouse(); }, _setMode: setMode,
    _replay: () => replay, _camera: camera, _controls: controls,
    // What the page should keep feeding: the usage window while the skyline is on, and whether the weather is wanted.
    get wants() { return { usageHours: prefs.skyline ? prefs.usageHours : 0, weather: !!prefs.weather, xray: !!prefs.xray }; },
    setUsage, setWeather, flashAlert: () => weather.flash(),
    startOutage: key => { const e = ents.get(String(key)); if (e) startOutage(e); return !!outage; }, endOutage, get outage() { return outage ? outage.info : null; },
    startBriefing, stopBrief, get briefing() { return brief ? { i: brief.i, count: brief.list.length, stop: brief.list[brief.i] } : null; },
    _skyline: skyline, _xray: xray, _weather: weather, get usage() { return usage; },
  };
  container.__v3d = api;
  return api;
}
