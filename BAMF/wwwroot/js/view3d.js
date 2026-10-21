// The 3D tab. The 3D library and models are only fetched the first time it's opened; after that this keeps
// it fed from the dashboard's own data: the same devices, kinds, names, gateways and unusual findings the
// other tabs use, plus the service watch's rows (towers), where devices talk and who is scanning (satellites and
// beams), the floor plans (the house), the events of the days replayed, what each device has moved (the usage skyline), what is exposed
// (the x-ray) and the network score and alerts (the weather). The drawing is in engine3d/scene.mjs, extras.mjs, skyline.mjs, xray.mjs
// and weather.mjs, and what goes where in engine3d/data.mjs.
let view3d = null, view3dStarting = null, view3dFailed = null, view3dData = null;
let view3dServices = [], view3dHouseSig = "", view3dTimeline = null;
let view3dFocusId = null, view3dFocusTries = 0;      // a device to fly to once the scene has it: from #3d/12, or a Show in 3D
let view3dUsage = { hours: 0, at: 0 }, view3dSecAt = 0, view3dSkyAt = 0, view3dTrafAt = 0, view3dAlertSeen = null;   // when each of the layers' feeds was last asked

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
      // The days asked for: fetched when the replay is asked for, then every moment in it is worked out from it.
      onReplayLoad: async hours => {
        const r = await fetch("/api/timeline?hours=" + (hours || 24));
        if (!r.ok) return null;
        view3dTimeline = await r.json();
        return view3dTimeline;
      },
      // The briefing: the last day's events and the alerts raised in it.
      onBriefingLoad: async () => {
        const [t, a] = await Promise.all([fetch("/api/timeline?hours=24"), fetch("/api/alerts")]);
        return { timeline: t.ok ? await t.json() : null, alerts: a.ok ? await a.json() : [] };
      },
      // A layer was switched on, off or changed: ask for what it needs now, not at the next refresh.
      onWantsChange: () => { view3dUsage.at = 0; view3dSecAt = 0; view3dSkyAt = 0; refresh3d(); },
      replayScene: tMs => {
        if (!view3dTimeline) return null;
        const st = view3dData.stateAt(view3dTimeline, tMs);
        const titles = new Map((view3dTimeline.unusual || []).map(u => [u.hostId, u.title]));
        const shown = hosts.filter(h => inNetwork(h) && st.present.has(h.id)).map(h => ({ ...h, online: st.online.get(h.id) ?? h.online, latencyMs: null }));
        const items = [...st.unusualOpen].map(id => ({ hostId: id, open: true, kind: "replay", title: titles.get(id) || "Something unusual", detail: "" }));
        const scene = view3dData.buildScene({
          hosts: shown, kindOf: deviceKind, nameOf: name3d, unusual: items,
          gatewayIp: subnet => (networkPlaces[subnet] || {}).gateway || null, rateOf: () => 2,
        });
        return { scene, internetDown: st.internetDown };
      },
      onReplayEnd: () => refresh3d(),
      // What can be done to the picked device from its card, using what the other tabs already do.
      actionsFor: d => {
        const h = hosts.find(x => x.id === d.id);
        if (!h) return [];
        const writer = role !== "viewer", out = [];
        if (d.web) out.push({ id: "web", label: "Open page", title: d.web.url });
        if (networkToolsEnabled && !h.remote && writer) out.push({ id: "ping", label: "Ping" }, { id: "trace", label: "Trace" });
        if (writer && !h.remote) {
          out.push({ id: "known", label: h.known ? "Mark unknown" : "Mark known" });
          out.push({ id: "watch", label: h.watched ? "Stop watching" : "Watch", title: "Alert if it goes offline" });
          out.push({ id: "snooze", label: h.snoozedUntil ? "Snoozed…" : "Snooze…" });
          if (!h.online) out.push({ id: "wake", label: "Wake", title: "Send a Wake-on-LAN packet" });
        }
        out.push({ id: "copy", label: "Copy IP" });
        return out;
      },
      onAction: async (id, d) => {
        const h = hosts.find(x => x.id === d.id);
        if (!h) return;
        try {
          if (id === "web") window.open(d.web.url, "_blank", "noopener");
          else if (id === "ping" || id === "trace") openTools(h, id);
          else if (id === "snooze") openSnoozeDialog(h);
          else if (id === "copy") { await navigator.clipboard.writeText(h.ip); toast(`${esc(h.ip)} copied`); }
          else if (id === "known") { await setKnown(h, !h.known); await refresh(); }
          else if (id === "watch") {
            await fetch(`/api/hosts/${h.id}/watch`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ watched: !h.watched }) });
            toast(h.watched ? `No longer watching ${esc(dispName(h))}` : `Now watching ${esc(dispName(h))} for downtime`);
            await refresh();
          } else if (id === "wake") {
            const r = await fetch(`/api/hosts/${h.id}/wake`, { method: "POST" });
            const data = await r.json().catch(() => ({}));
            toast(data.ok ? `Magic packet sent to ${esc(dispName(h))}` : "Wake failed: " + esc(data.error || "unknown"));
          }
        } catch { toast("That didn't go through"); }
      },
      // The address says which device is picked, so a link to it, or a reload, comes back to it.
      onSelect: d => { try { history.replaceState(null, "", d ? "#3d/" + d.id : "#3d"); } catch { /* ignore */ } },
      // A device placed from the house: where it goes on its floor, as a share of the plan.
      onPlace: async (d, floorId, x, y) => {
        try {
          const r = await fetch(`/api/floors/${floorId}/places`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ hostId: d.id, x, y }) });
          if (!r.ok) { toast("Couldn't place that"); return false; }
          await loadFloors(true);
          view3dHouseSig = "";
          await refresh3d();
          return true;
        } catch { toast("Couldn't place that"); return false; }
      },
    });
  } catch (err) {
    view3dFailed = err;
    wrap.innerHTML = `<div class="v3d-fail"><b>The 3D view couldn't start.</b><br>${esc(err.message || String(err))}<br>
      The <a href="#map">Map</a> and the <a href="#devices">Devices</a> tab show the same network without it.</div>`;
  }
}

// A device's name, or its address when it has none, so the scene never labels something with a dash.
const name3d = h => { const n = dispName(h); return n && n !== "\u2014" ? n : h.ip; };

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
  // The traffic answer is otherwise only fetched while Activity is open, so the rates, and the DHCP and DNS servers' jobs, would be stale or missing here.
  if (Date.now() - view3dTrafAt > 8000) { view3dTrafAt = Date.now(); try { trafficCache = await loadTraffic(); } catch { /* keep what it had */ } }
  const top = (trafficCache && trafficCache.top) || [];
  // measured bytes a second where the traffic monitor has them, else how busy it ranks among the busiest
  const trafficOf = h => { const t = top.find(x => x.hostId === h.id); return t ? { rx: t.rx || 0, tx: t.tx || 0 } : null; };
  const rateOf = h => {
    const t = trafficOf(h), r = t ? view3dData.rateScale((t.rx || 0) + (t.tx || 0)) : 0;
    if (r) return r;
    const i = top.findIndex(x => x.hostId === h.id);
    return i < 0 ? 2 : Math.max(3, 9 - i);
  };
  // what a device is plugged into, as the Map has it: a switch or access point that is itself a device
  const parentOf = h => {
    const own = switches.find(s => s.hostId === h.id);
    let sw = h.switchId ? switches.find(s => s.id === h.switchId) : null;
    if (!sw && own && own.uplinkSwitch) sw = switches.find(s => s.id === own.uplinkSwitch) || null;
    if (!sw) return null;
    if (sw.kind === "ssid") sw = switches.find(s => s.id === sw.runsOn && s.kind === "ap") || null;
    return sw && sw.hostId && sw.hostId !== h.id ? sw.hostId : null;
  };
  const webOf = h => {
    const open = h.openPorts || [];
    for (const [port, scheme] of [[443, "https"], [8443, "https"], [80, "http"], [8080, "http"], [8123, "http"], [5000, "http"]])
      if (open.includes(port)) return { url: `${scheme}://${h.ip}${(scheme === "https" && port === 443) || (scheme === "http" && port === 80) ? "" : ":" + port}/` };
    return null;
  };
  const wants = view3d.wants;
  // what is exposed: the certificates and UPnP are read now and then, only while the x-ray is on
  if (wants.xray && (!securityCache || Date.now() - view3dSecAt > 120000)) { view3dSecAt = Date.now(); await loadSecurity(); }
  const certDays = new Map();
  for (const c of (securityCache && securityCache.certs) || []) {
    if (!c.notAfter) continue;
    const days = Math.floor((Date.parse(c.notAfter) - Date.now()) / 86400000);
    if (!certDays.has(c.hostId) || days < certDays.get(c.hostId)) certDays.set(c.hostId, days);
  }
  const upnpIds = new Set(((securityCache && securityCache.upnp && securityCache.upnp.devices) || []).map(u => u.hostId));
  const dhcpIps = new Set([...((trafficCache && trafficCache.dhcp) || []).map(d => d.ip), ...(((dhcpProbeCache && dhcpProbeCache.last && dhcpProbeCache.last.networks) || []).flatMap(n => (n.offers || []).map(o => o.server)))]);
  const dnsIps = new Set(((trafficCache && trafficCache.dns) || []).map(d => d.ip));
  const exposureOf = h => {
    const roles = [], mine = (h.addresses && h.addresses.length ? h.addresses.map(a => a.ip) : [h.ip]);   // a device with a second address is the server at either
    if (mine.some(ip => dhcpIps.has(ip))) roles.push("DHCP");
    if (mine.some(ip => dnsIps.has(ip))) roles.push("DNS");
    return view3dData.exposureOf({ ports: h.openPorts || [], cert: certDays.has(h.id) ? { days: certDays.get(h.id) } : null, upnp: upnpIds.has(h.id), roles });
  };
  const [items, services, flows] = await Promise.all([unusual3d(), services3d(), flows3d()]);
  if (view !== "3d" || view3d.replaying) return;
  view3dServices = services;
  const servicesOf = h => view3dServices.filter(s => s.hostId === h.id).map(s => ({ name: s.name, state: s.state, uptime: s.uptime, ms: s.ms }));
  const scene = view3dData.buildScene({
    hosts: hosts.filter(h => inNetwork(h)), kindOf: deviceKind, nameOf: name3d, unusual: items,
    gatewayIp: subnet => (networkPlaces[subnet] || {}).gateway || null, rateOf, servicesOf,
    tagsOf: h => h.tags || [], parentOf, trafficOf, webOf, exposureOf,
  });
  await house3d();
  view3d.update(scene);
  view3d.setExtras(flows);
  view3d.setActive(true);
  // what each device has moved, for the usage skyline: once a minute, and at once when the window changes
  if (wants.usageHours && (view3dUsage.hours !== wants.usageHours || Date.now() - view3dUsage.at > 60000)) {
    view3dUsage = { hours: wants.usageHours, at: Date.now() };
    try { const r = await fetch("/api/traffic/usage?hours=" + wants.usageHours); if (r.ok) view3d.setUsage(await r.json()); } catch { /* keep what it had */ }
  }
  // the weather: the network score, and a flash for an alert that has come in since the last look, every half minute
  if (wants.weather && Date.now() - view3dSkyAt > 30000) {
    view3dSkyAt = Date.now();
    try {
      const r = await fetch("/api/score");
      if (r.ok) { const s = await r.json(); view3d.setWeather(s.enabled === false ? null : s.value); }
      const a = await fetch("/api/alerts");
      if (a.ok) {
        const list = await a.json(), newest = list.length ? Date.parse(list[0].at) || 0 : 0;
        if (view3dAlertSeen != null && newest > view3dAlertSeen) view3d.flashAlert();
        view3dAlertSeen = Math.max(view3dAlertSeen ?? 0, newest);
      }
    } catch { /* keep the sky it had */ }
  }
  // flown to once it is there, tried for a few refreshes in case the device list is still arriving
  if (view3dFocusId != null) {
    if (view3d.focus(view3dFocusId) || ++view3dFocusTries > 4) { view3dFocusId = null; view3dFocusTries = 0; }
  }
}
