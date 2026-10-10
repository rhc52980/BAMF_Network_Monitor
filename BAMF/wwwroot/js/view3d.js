// The 3D tab. The 3D library and models are only fetched the first time it's opened; after that this keeps
// it fed from the dashboard's own data: the same devices, kinds, names, gateways and unusual findings the
// other tabs use, plus the service watch's rows (towers), where devices talk and who is scanning (satellites and
// beams), the floor plans (the house) and the last day's events (the replay). The drawing is in engine3d/scene.mjs
// and engine3d/extras.mjs, and what goes where in engine3d/data.mjs.
let view3d = null, view3dStarting = null, view3dFailed = null, view3dData = null;
let view3dServices = [], view3dHouseSig = "", view3dTimeline = null;

const VIEW3D_CREDITS = `<h3>Model credits</h3>
  <p>All forty-six device models were made for BAMF. The 3D drawing is <a href="https://threejs.org" target="_blank" rel="noopener noreferrer">three.js</a> (MIT licence).</p>`;

async function start3d() {
  const wrap = $("view3dWrap");
  try {
    const [scene, data] = await Promise.all([import("/engine3d/scene.mjs"), import("/engine3d/data.mjs")]);
    view3dData = data;
    view3d = await scene.createView(wrap, {
      credits: VIEW3D_CREDITS,
      // History lives in the device's row on the Devices tab, so go there with it open.
      onOpenDevice: async d => { const h = hosts.find(x => x.id === d.id); if (!h) return; await openHistory(h); jumpToHost(h.id); },
      // The last day: fetched when the replay is asked for, then every moment in it is worked out from it.
      onReplayLoad: async () => {
        const r = await fetch("/api/timeline?hours=24");
        if (!r.ok) return null;
        view3dTimeline = await r.json();
        return view3dTimeline;
      },
      replayScene: tMs => {
        if (!view3dTimeline) return null;
        const st = view3dData.stateAt(view3dTimeline, tMs);
        const titles = new Map((view3dTimeline.unusual || []).map(u => [u.hostId, u.title]));
        const shown = hosts.filter(h => inNetwork(h) && st.present.has(h.id)).map(h => ({ ...h, online: st.online.get(h.id) ?? h.online, latencyMs: null }));
        const items = [...st.unusualOpen].map(id => ({ hostId: id, open: true, kind: "replay", title: titles.get(id) || "Something unusual", detail: "" }));
        const scene = view3dData.buildScene({
          hosts: shown, kindOf: deviceKind, nameOf: dispName, unusual: items,
          gatewayIp: subnet => (networkPlaces[subnet] || {}).gateway || null, rateOf: () => 2,
        });
        return { scene, internetDown: st.internetDown };
      },
      onReplayEnd: () => refresh3d(),
    });
  } catch (err) {
    view3dFailed = err;
    wrap.innerHTML = `<div class="v3d-fail"><b>The 3D view couldn't start.</b><br>${esc(err.message || String(err))}<br>
      The <a href="#map">Map</a> and the <a href="#devices">Devices</a> tab show the same network without it.</div>`;
  }
}

// What's been noticed as unusual, for the beacons.
async function unusual3d() {
  try {
    const r = await fetch("/api/unusual");
    return r.ok ? (await r.json()).items || [] : [];
  } catch { return []; }
}

// The service watch's rows, for the towers; and where devices talk and who is scanning, for the satellites and beams.
async function services3d() {
  try {
    const r = await fetch("/api/service-watch");
    if (!r.ok) return [];
    const d = await r.json();
    return d.enabled ? d.services || [] : [];
  } catch { return []; }
}
async function flows3d() {
  try {
    const r = await fetch("/api/flows/map");
    return r.ok ? await r.json() : { destinations: [], scans: [] };
  } catch { return { destinations: [], scans: [] }; }
}

// The house: the floor plans, drawn or uploaded, with where each device was placed on them. Rebuilt only when they change.
async function house3d() {
  const d = typeof loadFloors === "function" ? await loadFloors() : null;
  if (!d || !d.floors || !d.floors.length) { if (view3dHouseSig !== "") { view3dHouseSig = ""; view3d.setHouse(null); } return; }
  const sig = JSON.stringify([d.floors.map(f => [f.id, f.updated, f.kind]), d.places.map(p => [p.hostId, p.floorId, +p.x.toFixed(3), +p.y.toFixed(3)])]);
  if (sig === view3dHouseSig) return;
  const floors = [];
  for (const f of d.floors) {
    if (f.kind === "plan") {
      const plan = await loadPlan(f);
      if (plan && plan.width && plan.height) floors.push({ id: f.id, name: f.name, kind: "plan", plan });
    } else if (f.width && f.height) {
      const w = f.width, h = f.height;
      floors.push({ id: f.id, name: f.name, kind: "image", imageUrl: `/api/floors/${f.id}/image?v=${encodeURIComponent(f.updated || "")}`,
        plan: { v: 1, width: w, height: h, unit: "ft", step: Math.max(w, h) / 16, perStep: 2, items: [] } });
    }
  }
  view3dHouseSig = sig;
  view3d.setHouse(floors.length ? { floors, places: d.places } : null);
}

// The tab fills what's left of the window below the tab bar (on a phone the stylesheet sets its height).
function size3d() {
  const wrap = $("view3dWrap");
  if (wrap.hidden) return;
  if (wrap.classList.contains("v3d-full") || document.fullscreenElement === wrap) return;   // full screen sizes itself
  wrap.style.height = innerWidth <= 700 ? "" : Math.max(480, innerHeight - wrap.getBoundingClientRect().top - 16) + "px";
}
addEventListener("resize", size3d);

// Called with each refresh while the 3D tab is open, and when it's left.
async function refresh3d() {
  // Not drawn behind the screen saver either: the saver has its own scene on the same shared clock.
  if (view !== "3d" || saverOn) { if (view3d) view3d.setActive(false); return; }
  size3d();
  if (view3dFailed) return;
  if (!view3d) {
    if (!view3dStarting) view3dStarting = start3d();
    await view3dStarting;
    if (!view3d) return;
  }
  // While the day is being replayed the scene is the replay's, not now's.
  if (view3d.replaying) { view3d.setActive(true); return; }
  const top = (trafficCache && trafficCache.top) || [];
  const rateOf = h => { const i = top.findIndex(x => x.hostId === h.id); return i < 0 ? 2 : Math.max(3, 9 - i); };
  const [items, services, flows] = await Promise.all([unusual3d(), services3d(), flows3d()]);
  if (view !== "3d" || view3d.replaying) return;
  view3dServices = services;
  const servicesOf = h => view3dServices.filter(s => s.hostId === h.id).map(s => ({ name: s.name, state: s.state, uptime: s.uptime, ms: s.ms }));
  const scene = view3dData.buildScene({
    hosts: hosts.filter(h => inNetwork(h)), kindOf: deviceKind, nameOf: dispName, unusual: items,
    gatewayIp: subnet => (networkPlaces[subnet] || {}).gateway || null, rateOf, servicesOf,
  });
  await house3d();
  view3d.update(scene);
  view3d.setExtras(flows);
  view3d.setActive(true);
}
