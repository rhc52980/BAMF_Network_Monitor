// ---- dockable scan results panel ----
function openScanPanel() { $("scanPanel").hidden = false; }
$("scanPanelClose").onclick = () => { $("scanPanel").hidden = true; };
$("scanPanelClear").onclick = () => { $("scanPanelBody").innerHTML = ""; };

function portChips(ip, ports) {
  if (!ports || !ports.length) return `<span class="none">No open ports found in that set.</span>`;
  return `<div class="ports-cell">` + ports.map(p => {
    const isWeb = ["HTTP","HTTPS","HTTP-alt","HTTPS-alt","IPP/Print","UPnP/Web","Plex"].includes(p.service);
    const scheme = (String(p.service).startsWith("HTTPS") || p.port === 8443) ? "https" : "http";
    return isWeb
      ? `<a class="port-chip web" href="${scheme}://${esc(ip)}:${p.port}" target="_blank" rel="noopener">${p.port} ${esc(p.service||"")} ↗</a>`
      : `<span class="port-chip">${p.port}${p.service?" "+esc(p.service):""}</span>`;
  }).join(" ") + `</div>`;
}

// adds an entry, returns its element so we can fill it when the scan completes
function addScanEntry(name, ip, subLabel) {
  openScanPanel();
  const body = $("scanPanelBody");
  const el = document.createElement("div");
  el.className = "scan-entry busy";
  // What's running, so a theme can show it: Identify, or a port scan.
  el.dataset.kind = subLabel === "identify" ? "identify" : "ports";
  const now = new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  el.innerHTML = `<div class="scan-entry-head">
      <span class="nm">${esc(name)}</span>
      <span class="ipx">${esc(ip)}${subLabel ? " · " + esc(subLabel) : ""}</span>
      <span class="ts">${now}</span>
    </div><div class="scan-entry-body"></div>`;
  body.prepend(el);
  return el;
}

// Shared renderer for scans that cover several addresses (network scans and
// wildcard patterns), so both read identically in the panel.
function renderMultiHostScan(el, data) {
  let html = `<div class="none">Scanned ${data.scanned} address(es), ${data.withOpenPorts} with open ports.</div>`;
  for (const hd of (data.hosts || [])) {
    html += `<div style="margin-top:10px"><div class="scan-entry-head"><span class="nm">${esc(hd.name)}</span><span class="ipx">${esc(hd.ip)} · ${esc(hd.subnet)}</span></div>${portChips(hd.ip, hd.ports)}</div>`;
  }
  el.querySelector(".scan-entry-body").innerHTML = html;
}

// Per-host link override: a port, a ":port/path", or a full URL. The server
// resolves and validates it, and tells us what it will actually open.
async function setHostLink(host) {
  const current = host.link || "";
  const answer = prompt(
    `Link for ${dispName(host)} (${host.ip})\n\n` +
    `Examples:\n` +
    `  8006                  → http://${host.ip}:8006\n` +
    `  :8006/admin           → http://${host.ip}:8006/admin\n` +
    `  https://{ip}:8006     → {ip} is replaced with the address\n\n` +
    `Leave empty to use the default (${host.linkUrl && !current ? host.linkUrl : "http://" + host.ip}).`,
    current);
  if (answer === null) return; // cancelled
  try {
    const r = await fetch(`/api/hosts/${host.id}/link`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ link: answer }),
    });
    const d = await r.json();
    if (!r.ok) { toast("Could not save the link"); return; }
    toast(answer.trim()
      ? `Link set: <span class="mono">${esc(d.linkUrl)}</span>`
      : "Link reset to the default");
    await refresh();
  } catch { toast("Could not save the link — see server logs"); }
}

// Expands a device's History panel: the online/offline session timeline and
// nothing else. Notes have their own dialog, openNoteDialog, below.
async function openHistory(host) {
  if (expandedId !== host.id) {
    expandedId = host.id;
    try { eventsCache[host.id] = await loadEvents(host.id); } catch { eventsCache[host.id] = []; }
    try { ipsCache[host.id] = await loadIps(host.id); } catch { ipsCache[host.id] = []; }
    try { latencyCache[host.id] = await loadLatency(host.id); } catch { latencyCache[host.id] = []; }
    try { portsCache[host.id] = await (await fetch(`/api/hosts/${host.id}/ports`)).json(); } catch { portsCache[host.id] = []; }
    try { timelineCache[host.id] = await (await fetch(`/api/hosts/${host.id}/timeline`)).json(); } catch { timelineCache[host.id] = []; }
    try { weekCache[host.id] = await (await fetch(`/api/hosts/${host.id}/traffic?days=7`)).json(); } catch { weekCache[host.id] = []; }
    try { presenceCache[host.id] = await (await fetch(`/api/hosts/${host.id}/presence?weeks=4`)).json(); } catch { presenceCache[host.id] = null; }
    render();
  }
}

// Notes live in a dialog rather than in the expandable row. The row is
// rebuilt by every render(), which is why the old in-row textarea needed a
// guard to stop the ten-second poll discarding half-typed text; a dialog sits
// outside the table and needs no such thing.
let noteHost = null;
// ---- pause all alerts ----
// Hold every alert for a while, from the Tools menu.
function openPauseDialog() {
  $("pauseSub").textContent = alertsPausedUntil ? `Paused until ${snoozeClock(alertsPausedUntil)}. Pick a new time to change it.` : "Every alert, for everyone, until the time you pick.";
  const morning = new Date(); if (morning.getHours() >= 8) morning.setDate(morning.getDate() + 1);
  morning.setHours(8, 0, 0, 0);
  const picks = [["30 minutes", 30], ["1 hour", 60], ["2 hours", 120], ["4 hours", 240], ["8 hours", 480], ["1 day", 1440], ["3 days", 4320],
    ["Until " + snoozeClock(morning.toISOString()), Math.ceil((morning - Date.now()) / 60e3)]];
  const box = $("pausePicks");
  box.innerHTML = "";
  for (const [label, minutes] of picks) {
    const b = document.createElement("button");
    b.type = "button"; b.className = "toggle"; b.textContent = label;
    b.onclick = () => savePause(minutes);
    box.appendChild(b);
  }
  $("pauseEnd").hidden = !alertsPausedUntil;
  $("pauseModal").hidden = false;
  box.firstElementChild.focus();
}
function closePauseDialog() { $("pauseModal").hidden = true; }
async function savePause(minutes) {
  try {
    const r = await fetch("/api/alerts/pause", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ minutes }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || "Couldn't pause alerts")); return; }
    alertsPausedUntil = d.pausedUntil || null;
    renderPause();
    toast(alertsPausedUntil ? `Alerts are paused until ${esc(snoozeClock(alertsPausedUntil))}` : "Alerts are back on");
    closePauseDialog();
    await refresh();
  } catch (e) { console.error(e); toast("Couldn't pause alerts - see the server log"); }
}
$("pauseOpen").onclick = () => { toolsOpen = false; $("toolsMenu").classList.remove("show"); openPauseDialog(); };
$("pauseEnd").onclick = () => savePause(0);
$("pauseResume").onclick = () => savePause(0);
$("pauseCancel").onclick = closePauseDialog;
$("pauseModal").onclick = e => { if (e.target.id === "pauseModal") closePauseDialog(); };

// ---- snooze ----
// Hold back one device's alerts for a while, from its ⋯ menu.
let snoozeHost = null;
function snoozeClock(iso) {
  const d = new Date(iso), now = new Date();
  const t = d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  if (d.toDateString() === now.toDateString()) return t;
  const tomorrow = new Date(now); tomorrow.setDate(now.getDate() + 1);
  if (d.toDateString() === tomorrow.toDateString()) return "tomorrow " + t;
  return d.toLocaleDateString([], { weekday: "short" }) + " " + t;
}
function openSnoozeDialog(h) {
  snoozeHost = h;
  $("snoozeTitle").textContent = "Snooze alerts \u2014 " + dispName(h);
  $("snoozeSub").textContent = h.snoozedUntil ? `Snoozed until ${snoozeClock(h.snoozedUntil)}. Pick a new time to change it.` : `${h.ip} \u00b7 alerts about this device only`;
  // The next 8 am, for "until the morning": tomorrow's, unless it's still before 8 today.
  const morning = new Date(); if (morning.getHours() >= 8) morning.setDate(morning.getDate() + 1);
  morning.setHours(8, 0, 0, 0);
  const picks = [["30 minutes", 30], ["1 hour", 60], ["2 hours", 120], ["8 hours", 480], ["1 day", 1440],
    ["Until " + snoozeClock(morning.toISOString()), Math.ceil((morning - Date.now()) / 60e3)]];
  const box = $("snoozePicks");
  box.innerHTML = "";
  for (const [label, minutes] of picks) {
    const b = document.createElement("button");
    b.type = "button"; b.className = "toggle"; b.textContent = label;
    b.onclick = () => saveSnooze(minutes);
    box.appendChild(b);
  }
  $("snoozeEnd").hidden = !h.snoozedUntil;
  $("snoozeModal").hidden = false;
  box.firstElementChild.focus();
}
function closeSnoozeDialog() { $("snoozeModal").hidden = true; snoozeHost = null; }
async function saveSnooze(minutes) {
  const h = snoozeHost;
  if (!h) return;
  try {
    const r = await fetch(`/api/hosts/${h.id}/snooze`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ minutes }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't snooze that"); return; }
    toast(d.snoozedUntil ? `${esc(dispName(h))}'s alerts are snoozed until ${esc(snoozeClock(d.snoozedUntil))}` : `${esc(dispName(h))}'s alerts are back on`);
    closeSnoozeDialog();
    await refresh();
  } catch (e) { console.error(e); toast("Couldn't snooze that - see the server log"); }
}
$("snoozeEnd").onclick = () => saveSnooze(0);
$("snoozeCancel").onclick = closeSnoozeDialog;
$("snoozeModal").onclick = e => { if (e.target.id === "snoozeModal") closeSnoozeDialog(); };

function openNoteDialog(host) {
  noteHost = host;
  $("noteTitle").textContent = (host.note ? "Edit note — " : "Add note — ") + dispName(host);
  $("noteSub").textContent = host.ip + " · " + host.mac;
  const ta = $("noteText");
  ta.value = host.note || "";
  $("noteClear").style.display = host.note ? "" : "none";
  $("noteModal").hidden = false;
  ta.focus();
  ta.setSelectionRange(ta.value.length, ta.value.length);  // cursor at the end
}
function closeNoteDialog() { $("noteModal").hidden = true; noteHost = null; }
async function saveNoteDialog(text) {
  const h = noteHost;
  if (!h) return;
  const btn = $("noteSave");
  btn.disabled = true;
  try {
    await setNote(h, text);
    toast(text.trim() ? `Note saved for ${esc(dispName(h))}` : `Note cleared for ${esc(dispName(h))}`);
    closeNoteDialog();
    await refresh();
  } finally {
    btn.disabled = false;
  }
}

// ---- switch layout ----
// The user's own record of how things are cabled; the Map draws it. BAMF has
// no way to check it, and nothing here talks to a switch.
function nameOrIp(h) { const n = dispName(h); return n && n !== "—" ? n : h.ip; }

// Port choices for a switch, each showing what's already recorded on it, so
// picking a port doubles as seeing what's plugged in where.
function fillPortSelect(sel, sw, current, skip = {}) {
  sel.innerHTML = "";
  const add = (value, label) => {
    const o = document.createElement("option");
    o.value = value; o.textContent = label;
    sel.appendChild(o);
  };
  add(0, "Not recorded");
  for (let p = 1; p <= sw.ports; p++) {
    const on = [
      ...hosts.filter(h => h.switchId === sw.id && h.switchPort === p && h.id !== skip.hostId && !h.forgotten).map(nameOrIp),
      ...switches.filter(s => s.uplink === "switch" && s.uplinkSwitch === sw.id && s.uplinkPort === p && s.id !== skip.switchId).map(s => s.name),
    ];
    const where = portLabel(sw, p);
    add(p, `Port ${p}` + (where ? ` · ${where}` : "") + (on.length ? " — " + on.join(", ") : ""));
  }
  sel.value = String(current > sw.ports ? 0 : current || 0);
}

function showErr(id, msg) {
  const el = $(id);
  el.textContent = msg || "";
  el.className = "set-result" + (msg ? " err" : "");
}

let plugHost = null;
let plugAfterSave = null;   // set when a drop on the Map opened the dialog
function openPlugDialog(host, preselect = null) {
  plugHost = host;
  $("plugTitle").textContent = "Plugged into — " + nameOrIp(host);
  $("plugSub").textContent = host.ip + " · " + host.mac;
  const sel = $("plugSwitch");
  sel.innerHTML = "";
  const none = document.createElement("option");
  none.value = "0"; none.textContent = switches.length ? "Not recorded" : "No switches yet";
  sel.appendChild(none);
  for (const sw of switches) {
    const o = document.createElement("option");
    o.value = sw.id;
    o.textContent = `${sw.name} ${kindPorts(sw)}` + (sw.subnet && sw.subnet !== host.subnet ? " · " + sw.subnet : "");
    sel.appendChild(o);
  }
  sel.value = String(preselect ?? host.switchId ?? 0);
  if (!sel.value) sel.value = "0";
  syncPlugPort(preselect ? 0 : host.switchPort);
  resetBlinkBox("plugBlink");
  showErr("plugErr", "");
  $("plugModal").hidden = false;
  sel.focus();
}
function syncPlugPort(current) {
  const sw = switches.find(s => String(s.id) === $("plugSwitch").value);
  $("plugPortRow").style.display = sw && !memberKind(sw.kind) ? "" : "none";
  if (sw) fillPortSelect($("plugPort"), sw, current, { hostId: plugHost?.id });
}
function closePlugDialog() { $("plugModal").hidden = true; plugHost = null; plugAfterSave = null; blinkTick(); }
async function savePlugDialog() {
  const h = plugHost;
  if (!h) return;
  const switchId = Number($("plugSwitch").value) || 0;
  const target = switches.find(s => s.id === switchId);
  const port = switchId && target && !memberKind(target.kind) ? Number($("plugPort").value) || 0 : 0;
  const btn = $("plugSave");
  btn.disabled = true;
  try {
    const r = await fetch(`/api/hosts/${h.id}/plug`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ switchId, port }),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      showErr("plugErr", d.error || "Couldn't save (HTTP " + r.status + ").");
      return;
    }
    const sw = switches.find(s => s.id === switchId);
    toast(sw ? `${esc(nameOrIp(h))} recorded on ${esc(sw.name)}` + (port ? `, port ${port}` : "")
             : `Cleared where ${esc(nameOrIp(h))} is plugged in`);
    const after = plugAfterSave;
    closePlugDialog();
    if (after) await after();
    await refresh();
  } catch {
    showErr("plugErr", "Couldn't reach BAMF.");
  } finally {
    btn.disabled = false;
  }
}

let swEditing = null;    // the switch being edited; null when adding one
let swReturnTo = null;   // a device whose "Plugged into" dialog sent us here
const KIND_PORTS = { switch: 8, router: 4, ap: 1, virtual: 1, ssid: 1, vpn: 1 };   // a starting port count for a new one
function openSwitchDialog(sw, fromHost = null, returnTo = null, opts = {}) {
  swEditing = sw;
  swReturnTo = returnTo;
  const kind = sw ? (sw.kind || "switch") : opts.kind || (fromHost && isGatewayHost(fromHost) ? "router" : "switch");
  $("swKind").value = kind;
  $("swTitle").textContent = sw ? `Edit ${kindNoun(kind)} — ${sw.name}` : kind === "ssid" ? "Add a wireless SSID"
    : kind === "vpn" ? "Add a VPN" : "Add a switch, router or access point";
  $("swName").value = sw ? sw.name : fromHost ? nameOrIp(fromHost) : "";
  $("swPorts").value = sw ? sw.ports : KIND_PORTS[kind] || 1;
  // The machine a virtual switch runs on: any device BAMF sees.
  const runsSel = $("swRuns");
  runsSel.innerHTML = "";
  const none = document.createElement("option");
  none.value = "0"; none.textContent = "Pick the machine…";
  runsSel.appendChild(none);
  hosts.filter(h => !h.forgotten && !vmPlatform(h.mac))
    .sort((a, b) => (a.subnet || "").localeCompare(b.subnet || "") || ipNum(a.ip) - ipNum(b.ip))
    .forEach(h => {
      const o = document.createElement("option");
      o.value = h.id; o.textContent = `${nameOrIp(h)} · ${h.ip}`;
      runsSel.appendChild(o);
    });
  runsSel.value = String(kind === "virtual" && sw && sw.runsOn ? sw.runsOn
    : kind === "virtual" && opts.runsOn ? opts.runsOn : kind === "virtual" && fromHost ? fromHost.id : 0);
  if (!runsSel.value) runsSel.value = "0";
  // The access point a wireless SSID is broadcast by.
  const apSel = $("swAp");
  apSel.innerHTML = "";
  const noAp = document.createElement("option");
  noAp.value = "0"; noAp.textContent = switches.some(x => x.kind === "ap") ? "Pick the access point…" : "No access points yet: add one first";
  apSel.appendChild(noAp);
  switches.filter(x => x.kind === "ap").forEach(a => {
    const o = document.createElement("option");
    o.value = a.id; o.textContent = a.name + (a.subnet ? ` · ${a.subnet}` : "");
    apSel.appendChild(o);
  });
  apSel.value = String(kind === "ssid" ? (sw ? sw.runsOn : opts.runsOn || 0) : 0);
  if (!apSel.value) apSel.value = "0";

  const hostSel = $("swHost");
  hostSel.innerHTML = "";
  const addOpt = (sel, value, label) => {
    const o = document.createElement("option");
    o.value = value; o.textContent = label;
    sel.appendChild(o);
  };
  addOpt(hostSel, 0, "None — unmanaged, or not seen by BAMF");
  const taken = new Set(switches.filter(s => s !== sw).map(s => s.hostId).filter(Boolean));
  hosts.filter(h => !h.forgotten && !taken.has(h.id))
    .sort((a, b) => (a.subnet || "").localeCompare(b.subnet || "") || ipNum(a.ip) - ipNum(b.ip))
    .forEach(h => addOpt(hostSel, h.id, `${nameOrIp(h)} · ${h.ip}` + (h.vendor ? " · " + h.vendor : "")));
  hostSel.value = String(sw ? sw.hostId : fromHost ? fromHost.id : 0);
  if (!hostSel.value) hostSel.value = "0";

  const netSel = $("swNet");
  netSel.innerHTML = "";
  const nets = [...new Set([...subnets, ...hosts.map(h => h.subnet).filter(Boolean), ...(sw && sw.subnet ? [sw.subnet] : [])])]
    .sort((a, b) => ipNum(a.split("/")[0]) - ipNum(b.split("/")[0]));
  nets.forEach(n => addOpt(netSel, n, n));
  const apOfNew = kind === "ssid" && opts.runsOn ? switches.find(x => x.id === opts.runsOn) : null;
  netSel.value = sw && sw.subnet ? sw.subnet
    : apOfNew && apOfNew.subnet && nets.includes(apOfNew.subnet) ? apOfNew.subnet
    : opts.subnet && nets.includes(opts.subnet) ? opts.subnet
    : (network !== "all" && nets.includes(network) ? network : nets[0] || "");
  if (!sw && !fromHost && kind === "router") pickGatewayForSwitch();

  // Everything below this switch can't be what it's plugged into.
  const below = new Set();
  if (sw) {
    const walk = id => switches.filter(s => s.uplink === "switch" && s.uplinkSwitch === id)
      .forEach(s => { if (!below.has(s.id)) { below.add(s.id); walk(s.id); } });
    walk(sw.id);
  }
  const upSel = $("swUp");
  upSel.innerHTML = "";
  addOpt(upSel, "", "Not recorded");
  addOpt(upSel, "router", "The router");
  switches.filter(s => s !== sw && !below.has(s.id) && !memberKind(s.kind))
    .forEach(s => addOpt(upSel, "sw:" + s.id, `${s.name} ${kindPorts(s)}`));
  upSel.value = !sw ? "" : sw.uplink === "switch" ? "sw:" + sw.uplinkSwitch : sw.uplink;

  $("swDelete").style.display = sw ? "" : "none";
  syncSwitchDialog(sw ? sw.uplinkPort : 0);
  showErr("swErr", "");
  $("plugModal").hidden = true;
  $("swModal").hidden = false;
  $("swName").focus();
  $("swName").select();
}
// A new router starts out linked to the network's gateway, if BAMF sees it and
// it isn't already recorded as something else.
function pickGatewayForSwitch() {
  if (swEditing || Number($("swHost").value)) return;
  const net = $("swNet").value;
  const gw = hosts.find(h => h.subnet === net && !h.forgotten && isGatewayHost(h));
  if (gw && [...$("swHost").options].some(o => o.value === String(gw.id))) {
    $("swHost").value = String(gw.id);
    if (!$("swName").value.trim()) $("swName").value = nameOrIp(gw);
    syncSwitchDialog(0);
  }
}
function syncSwitchDialog(upPort) {
  // A virtual switch has a machine instead of a device, a network and an uplink.
  // A wireless SSID has an access point and a network. An access point isn't
  // asked for ports: its devices connect over the air, through its SSIDs.
  const kind = $("swKind").value;
  const virtual = kind === "virtual", ssid = kind === "ssid", ap = kind === "ap", vpn = kind === "vpn";
  for (const id of ["swHostRow", "swUpRow"]) $(id).style.display = virtual || ssid ? "none" : "";
  $("swHelp").style.display = virtual || ssid || vpn ? "none" : "";
  $("swHelpVpn").style.display = vpn ? "" : "none";
  $("swPortsRow").style.display = virtual || ssid || ap || vpn ? "none" : "";
  $("swRunsRow").style.display = virtual ? "" : "none";
  $("swApRow").style.display = ssid ? "" : "none";
  $("swHelpVirtual").style.display = virtual ? "" : "none";
  $("swHelpSsid").style.display = ssid ? "" : "none";
  $("swHelpAp").style.display = ap ? "" : "none";
  $("swAddSsid").style.display = ap ? "" : "none";
  $("swName").placeholder = virtual ? "e.g. vmbr0 or vSwitch0" : ssid ? "e.g. Home or Guest" : ap ? "e.g. Hallway AP"
    : vpn ? "e.g. WireGuard" : "e.g. Office SG1016DE";
  if (virtual) { $("swNetRow").style.display = "none"; $("swUpPortRow").style.display = "none"; return; }
  if (ssid) { $("swNetRow").style.display = ""; $("swUpPortRow").style.display = "none"; return; }
  $("swNetRow").style.display = Number($("swHost").value) ? "none" : "";
  const v = $("swUp").value;
  const parent = v.startsWith("sw:") ? switches.find(s => s.id === Number(v.slice(3))) : null;
  $("swUpPortRow").style.display = parent ? "" : "none";
  if (parent) fillPortSelect($("swUpPort"), parent, upPort, { switchId: swEditing?.id });
}
function closeSwitchDialog() {
  $("swModal").hidden = true;
  const back = swReturnTo;
  swEditing = null; swReturnTo = null;
  return back;
}
// "Add a wireless SSID" in an access point's dialog saves the AP, then opens a new SSID on it.
let swThenSsid = false;
async function saveSwitchDialog() {
  const v = $("swUp").value;
  const kind = $("swKind").value;
  const body = {
    kind,
    runsOn: Number(kind === "ssid" ? $("swAp").value : $("swRuns").value) || 0,
    name: $("swName").value.trim(),
    ports: Number($("swPorts").value) || 0,
    subnet: $("swNet").value,
    hostId: Number($("swHost").value) || 0,
    uplink: v.startsWith("sw:") ? "switch" : v,
    uplinkSwitch: v.startsWith("sw:") ? Number(v.slice(3)) : 0,
    uplinkPort: v.startsWith("sw:") ? Number($("swUpPort").value) || 0 : 0,
  };
  const btn = $("swSave");
  btn.disabled = true;
  try {
    const r = await fetch(swEditing ? `/api/switches/${swEditing.id}` : "/api/switches", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
    });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { showErr("swErr", d.error || "Couldn't save (HTTP " + r.status + ")."); return; }
    toast(`${kindNoun(d.kind || kind)[0].toUpperCase() + kindNoun(d.kind || kind).slice(1)} ${esc(d.name)} saved`);
    const wasNew = !swEditing;
    const back = closeSwitchDialog();
    await refresh();
    if (swThenSsid && d.kind === "ap") {
      openSwitchDialog(null, null, null, { kind: "ssid", runsOn: d.id });
    } else if (back) {
      const h = hosts.find(x => x.id === back.id);
      if (h) openPlugDialog(h, d.id);
    } else if (wasNew && d.kind !== "ap") {
      // A new switch has nothing on it yet; the next thing to do is fill it in.
      const sw = switches.find(x => x.id === d.id);
      if (sw) openPortsDialog(sw);
    }
  } catch {
    showErr("swErr", "Couldn't reach BAMF.");
  } finally {
    btn.disabled = false;
    swThenSsid = false;
  }
}
async function deleteSwitchDialog() {
  const sw = swEditing;
  if (!sw) return;
  if (!confirm(`Delete the switch "${sw.name}"? Anything recorded as plugged into it goes back to unrecorded. The devices themselves aren't affected.`)) return;
  const r = await fetch(`/api/switches/${sw.id}`, { method: "DELETE" });
  if (!r.ok) { showErr("swErr", "Couldn't delete (HTTP " + r.status + ")."); return; }
  toast(`Switch ${esc(sw.name)} deleted`);
  closeSwitchDialog();
  await refresh();
}

// ---- Tags ----
let tagsHost = null;
function openTagsDialog(h) {
  tagsHost = h;
  $("tagsTitle").textContent = "Tags — " + nameOrIp(h);
  $("tagsSub").textContent = h.ip + " · " + h.mac;
  $("tagsText").value = (h.tags || []).join(", ");
  renderTagPick();
  showErr("tagsErr", "");
  $("tagsModal").hidden = false;
  $("tagsText").focus();
}
// Every tag in use, as chips that toggle in the text box.
function renderTagPick() {
  const pick = $("tagsPick");
  pick.innerHTML = "";
  const current = $("tagsText").value.split(",").map(t => t.trim()).filter(Boolean);
  for (const t of allTags()) {
    const on = current.some(c => c.toLowerCase() === t.toLowerCase());
    const b = document.createElement("button");
    b.type = "button";
    b.className = "chip-btn" + (on ? " active" : "");
    b.textContent = t;
    b.onclick = () => {
      const now = $("tagsText").value.split(",").map(x => x.trim()).filter(Boolean);
      const next = on ? now.filter(c => c.toLowerCase() !== t.toLowerCase()) : [...now, t];
      $("tagsText").value = next.join(", ");
      renderTagPick();
    };
    pick.appendChild(b);
  }
}
function closeTagsDialog() { $("tagsModal").hidden = true; tagsHost = null; }
async function saveTagsDialog() {
  const h = tagsHost;
  if (!h) return;
  const tags = $("tagsText").value.split(",").map(t => t.trim()).filter(Boolean);
  const btn = $("tagsSave");
  btn.disabled = true;
  try {
    const r = await fetch(`/api/hosts/${h.id}/tags`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tags }),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      showErr("tagsErr", d.error || "Couldn't save (HTTP " + r.status + ").");
      return;
    }
    closeTagsDialog();
    await refresh();
    toast(tags.length ? `${esc(nameOrIp(h))} tagged ${esc(tags.join(", "))}` : `Tags cleared for ${esc(nameOrIp(h))}`);
  } catch {
    showErr("tagsErr", "Couldn't reach BAMF.");
  } finally {
    btn.disabled = false;
  }
}

// ---- Network cards ----
// Combine a machine's network cards, each seen as a device of its own, into one.
let cardsHost = null;
// BAMF's own cards, one per network it has an address on.
function selfMacs() { return Object.values(networkPlaces).map(p => (p.selfMac || "").toUpperCase()).filter(Boolean); }
function openCardsDialog(h) {
  cardsHost = h;
  $("cardsTitle").textContent = "Network cards — " + nameOrIp(h);
  const isSelf = macsOf(h).some(m => selfMacs().includes((m || "").toUpperCase()));
  $("cardsSub").textContent = isSelf ? "This is the machine running BAMF." : `${h.ip} · ${h.vendor || "unknown vendor"}`;
  const list = $("cardsList");
  list.innerHTML = "";
  const row = (card, main) => {
    const r = document.createElement("div");
    r.className = "card-row";
    const addrs = (card.addresses && card.addresses.length ? card.addresses.filter(a => a.current && !a.cardId) : [{ ip: card.ip }]).map(a => a.ip);
    r.innerHTML = `<span class="card-what"><span class="mono">${esc(card.mac)}</span> · ${esc(card.vendor || "unknown vendor")}
      <small>${esc(addrs.join(", ") || card.ip)} · ${esc(card.subnet || "no network")}${main ? " · this device's own card" : ""}</small></span>`;
    if (!main) {
      const b = document.createElement("button");
      b.className = "toggle"; b.type = "button"; b.textContent = "Separate";
      b.title = "Make this card a device of its own again";
      b.onclick = () => combineCard(card.id, 0);
      r.appendChild(b);
    }
    list.appendChild(r);
  };
  row(h, true);
  (h.interfaces || []).forEach(k => row(k, false));
  // Anything else can be added; BAMF's own other cards, and devices with the
  // same name, are the likely ones and come first.
  const sel = $("cardsPick");
  sel.innerHTML = "";
  const nm = x => (x.customName || (x.hostname !== "—" ? x.hostname : "") || x.mdnsName || "").toLowerCase();
  const likely = x => (isSelf && selfMacs().includes((x.mac || "").toUpperCase())) || (nm(h) && nm(x) === nm(h));
  const pool = hosts.filter(x => x.id !== h.id && !x.forgotten).sort((a, b) =>
    likely(b) - likely(a) || (a.subnet || "").localeCompare(b.subnet || "") || ipNum(a.ip) - ipNum(b.ip));
  const none = document.createElement("option");
  none.value = "0"; none.textContent = "Pick the device that's another card of this one…";
  sel.appendChild(none);
  for (const x of pool) {
    const o = document.createElement("option");
    o.value = x.id;
    o.textContent = (likely(x) ? "★ " : "") + `${nameOrIp(x)} · ${x.ip} · ${x.mac}` + (isSelf && selfMacs().includes((x.mac || "").toUpperCase()) ? " · BAMF's own card" : "");
    sel.appendChild(o);
  }
  const first = pool.find(likely);
  sel.value = first ? String(first.id) : "0";
  showErr("cardsErr", "");
  $("cardsModal").hidden = false;
}
function closeCardsDialog() { $("cardsModal").hidden = true; cardsHost = null; }
async function combineCard(cardId, parentId) {
  const h = cardsHost;
  try {
    const r = await fetch(`/api/hosts/${cardId}/combine`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ parentId }),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      showErr("cardsErr", d.error || "Couldn't save (HTTP " + r.status + ").");
      return;
    }
    await refresh();
    const now = h && hosts.find(x => x.id === h.id);
    if (now) openCardsDialog(now); else closeCardsDialog();
    toast(parentId ? "Network card combined" : "Network card separated: it's a device of its own again");
  } catch {
    showErr("cardsErr", "Couldn't reach BAMF.");
  }
}

// ---- Gateway ----
// One checkbox per address: "this device is the gateway of that address's
// network". A router with an address on several networks is the gateway of each.
let gwHost = null;
function openGatewayDialog(h) {
  gwHost = h;
  const vpn = vpnOfHost(h);
  $("gwTitle").textContent = "Gateway — " + nameOrIp(h);
  $("gwSub").textContent = vpn ? `This device is the VPN "${vpn.name}". A VPN is never a network's gateway.`
    : `${h.mac}${h.vendor ? " · " + h.vendor : ""}`;
  const list = $("gwList");
  list.innerHTML = "";
  // Its addresses now, and any it's declared the gateway at but no longer answers on.
  const addrs = [...curAddrs(h), ...declaredGws.filter(g => g.hostId === h.id && !curAddrs(h).some(a => a.ip === g.ip))
    .map(g => ({ ip: g.ip, subnet: g.subnet, gone: true }))];
  for (const a of addrs) {
    const net = a.subnet || "";
    const d = net ? declaredGw(net) : null;
    const mine = !!d && d.hostId === h.id && d.ip === a.ip;
    const now = gatewayOf(net);
    const nowHost = now && now.hostId ? hosts.find(x => x.id === now.hostId) : now ? hosts.find(x => curAddrs(x).some(y => y.ip === now.ip)) : null;
    const nowText = !net ? "BAMF doesn't know which network this address is on."
      : mine ? (a.gone ? "Declared the gateway of this network, but it no longer answers here." : "Declared the gateway of this network.")
      : now ? `Gateway now: ${nowHost ? nameOrIp(nowHost) + " · " : ""}${now.ip}, ${now.declared ? "declared" : "from BAMF's routing table"}`
      : "No gateway known for this network.";
    const row = document.createElement("label");
    row.className = "gw-row" + (vpn || !net ? " disabled" : "");
    row.innerHTML = `<input type="checkbox"><span><span class="mono">${esc(a.ip)}</span> · gateway of ${esc(net || "an unknown network")}</span><span class="gw-now"></span>`;
    const box = row.querySelector("input");
    box.checked = mine;
    box.disabled = !!vpn || !net;
    box.dataset.ip = a.ip;
    box.dataset.net = net;
    box.dataset.was = mine ? "1" : "";
    // One gateway per network: ticking one address unticks this device's others there.
    box.onchange = () => {
      if (box.checked) list.querySelectorAll("input").forEach(o => { if (o !== box && o.dataset.net === net) o.checked = false; });
    };
    row.querySelector(".gw-now").textContent = nowText;
    list.appendChild(row);
  }
  showErr("gwErr", "");
  $("gwModal").hidden = false;
}
function closeGatewayDialog() { $("gwModal").hidden = true; gwHost = null; }
async function saveGatewayDialog() {
  const h = gwHost;
  if (!h) return;
  // Unticks first, so moving a network's gateway to another address ends on the new one.
  const changed = [...$("gwList").querySelectorAll("input")].filter(b => b.checked !== !!b.dataset.was)
    .sort((a, b) => Number(a.checked) - Number(b.checked));
  const btn = $("gwSave");
  btn.disabled = true;
  try {
    for (const b of changed) {
      const r = await fetch(`/api/hosts/${h.id}/gateway`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ip: b.dataset.ip, enabled: b.checked }),
      });
      if (!r.ok) {
        const d = await r.json().catch(() => ({}));
        showErr("gwErr", d.error || "Couldn't save (HTTP " + r.status + ").");
        await refresh();
        return;
      }
    }
    closeGatewayDialog();
    await refresh();
    const nets = gwNetsOf(hosts.find(x => x.id === h.id) || h);
    if (changed.length) toast(nets.length ? `${esc(nameOrIp(h))} is the gateway of ${esc(nets.join(", "))}` : `${esc(nameOrIp(h))} is no longer declared a gateway`);
  } catch {
    showErr("gwErr", "Couldn't reach BAMF.");
  } finally {
    btn.disabled = false;
  }
}

// ---- Device type ----
// What the device is, in the user's words. Shown in the list, grouped by the
// Device type chips, found by search. The Map icon is separate.
let kindHost = null;
function openKindDialog(h) {
  kindHost = h;
  $("kindTitle").textContent = "Device type \u2014 " + nameOrIp(h);
  $("kindSub").textContent = h.osGuess ? "BAMF's guess: " + h.osGuess : "BAMF has no guess for this one";
  const input = $("kindText");
  input.value = h.typeName || "";
  input.placeholder = guessFamily(h.osGuess) || "e.g. NAS, Printer, Kids' tablet";
  // Suggestions: types already in use first, then the Map icons' names.
  const used = [...new Set(hosts.map(x => x.typeName).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  const list = $("kindList");
  list.innerHTML = "";
  [...used, ...TYPE_DEFS.map(d => d[1]).filter(l => !used.some(u => u.toLowerCase() === l.toLowerCase()))].forEach(v => {
    const o = document.createElement("option"); o.value = v; list.appendChild(o);
  });
  $("kindClear").style.display = h.typeName ? "" : "none";
  showErr("kindErr", "");
  $("kindModal").hidden = false;
  input.focus(); input.select();
}
function closeKindDialog() { $("kindModal").hidden = true; kindHost = null; }
async function saveKind(text) {
  const h = kindHost;
  if (!h) return;
  try {
    const r = await fetch(`/api/hosts/${h.id}/typename`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ type: text }) });
    if (!r.ok) { const d = await r.json().catch(() => ({})); showErr("kindErr", d.error || "Couldn't save (HTTP " + r.status + ")."); return; }
    toast(text.trim() ? `${esc(nameOrIp(h))} is now "${esc(text.trim())}"` : `${esc(nameOrIp(h))} is back to BAMF's guess`);
    closeKindDialog();
    await refresh();
  } catch { showErr("kindErr", "Couldn't reach BAMF."); }
}
$("kindSave").onclick = () => saveKind($("kindText").value);
$("kindClear").onclick = () => saveKind("");
$("kindCancel").onclick = closeKindDialog;
$("kindText").onkeydown = e => { if (e.key === "Enter") saveKind($("kindText").value); };
$("kindModal").onclick = e => { if (e.target.id === "kindModal") closeKindDialog(); };

// ---- Map icon ----
// One picker for two jobs: a type for one device, or (ticked, or from
// Settings) the icon for every device of a guessed type.
let typeHost = null;     // the device being typed, or null in family mode
let typeFamilyKey = null;   // the guessed type being given an icon, in family mode
function typeIconSvg(kind) {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${TOPO_ICONS[kind] || TOPO_ICONS.device}"/></svg>`;
}
function openTypeDialog(h, family = null) {
  typeHost = h;
  typeFamilyKey = family;
  const fam = h ? guessFamily(h.osGuess) : family;
  const current = h ? (h.deviceType || "") : (typeIcons[family] || "");
  const auto = h ? autoKind({ ...h, deviceType: "" }) : topoKind({ osGuess: family });
  $("typeTitle").textContent = h ? "Map icon \u2014 " + nameOrIp(h) : `Map icon for every "${family}" device`;
  $("typeSub").textContent = h ? (h.typeName ? "Device type: " + h.typeName : h.osGuess ? "BAMF's guess: " + h.osGuess : "No guess yet") : "BAMF guesses this type; pick the icon it gets on the Map.";
  const grid = $("typeGrid");
  grid.innerHTML = "";
  const opt = (key, label, note) => {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "type-opt" + (key === current ? " sel" : "");
    b.innerHTML = typeIconSvg(key || auto) + `<span></span>` + (note ? `<span class="auto-note"></span>` : "");
    b.children[1].textContent = label;
    if (note) b.children[2].textContent = note;
    b.onclick = () => saveType(key);
    grid.appendChild(b);
  };
  opt("", "Automatic", h ? (h.typeName && autoKind({ ...h, deviceType: "" }) !== autoKind({ ...h, deviceType: "", typeName: "" }) ? "from its device type" : "BAMF's guess") : "the usual icon");
  TYPE_DEFS.forEach(([k, label]) => opt(k, label));
  // In device mode, offer the same icon for all its guessed type at once.
  $("typeAllRow").style.display = h && fam ? "" : "none";
  $("typeAll").checked = false;
  $("typeAllText").textContent = fam ? `Use this icon for every "${fam}" device` : "";
  showErr("typeErr", "");
  $("plugModal").hidden = true;
  $("typeModal").hidden = false;
}
function closeTypeDialog() { $("typeModal").hidden = true; typeHost = null; typeFamilyKey = null; }
async function saveType(key) {
  const h = typeHost;
  const fam = h ? guessFamily(h.osGuess) : typeFamilyKey;
  const everyOfType = !h || (fam && $("typeAll").checked);
  try {
    if (everyOfType) {
      // The whole guessed type gets this icon; this device then follows it.
      const r = await fetch("/api/settings/type-icons", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ icons: { [fam]: key } }),
      });
      if (!r.ok) { const d = await r.json().catch(() => ({})); showErr("typeErr", d.error || "Couldn't save (HTTP " + r.status + ")."); return; }
    }
    if (h && (!everyOfType || h.deviceType)) {
      const r = await fetch(`/api/hosts/${h.id}/type`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ type: everyOfType ? "" : key }),
      });
      if (!r.ok) { const d = await r.json().catch(() => ({})); showErr("typeErr", d.error || "Couldn't save (HTTP " + r.status + ")."); return; }
    }
    toast(everyOfType
      ? (key ? `Every "${esc(fam)}" device now shows as ${esc(typeLabel(key))}` : `"${esc(fam)}" devices are back to their usual icon`)
      : (key ? `${esc(nameOrIp(h))} now has the ${esc(typeLabel(key).toLowerCase())} icon on the Map` : `${esc(nameOrIp(h))} is back to its automatic Map icon`));
    closeTypeDialog();
    await refresh();
  } catch {
    showErr("typeErr", "Couldn't reach BAMF.");
  }
}
function renderTypeIconList() {
  const table = $("typeIconTable");
  if (!table) return;
  table.innerHTML = "";
  const entries = Object.entries(typeIcons).sort((a, b) => a[0].localeCompare(b[0]));
  $("typeIconEmpty").hidden = entries.length > 0;
  for (const [fam, key] of entries) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td class="sw-name"></td><td class="set-eff"><span class="type-cell">${typeIconSvg(key).replace("<svg", '<svg class="type-ico"')}<span></span></span></td>
      <td><button class="toggle ti-change">Change</button> <button class="toggle ti-reset">Reset</button></td>`;
    tr.children[0].textContent = fam;
    tr.querySelector(".type-cell span").textContent = typeLabel(key);
    tr.querySelector(".ti-change").onclick = () => openTypeDialog(null, fam);
    tr.querySelector(".ti-reset").onclick = async () => {
      await fetch("/api/settings/type-icons", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ icons: { [fam]: "" } }),
      });
      await refresh();
    };
    table.appendChild(tr);
  }
}

// ---- Find port ----
// BAMF sends the device bursts of traffic, a second on and a second off, so
// its switch-port light pulses in a rhythm the user can pick out. The server
// runs the bursts on a fixed schedule - on for [2k, 2k+1) seconds after it
// started - and reports that start, so every open dashboard pulses the same
// device in step with the light: its Map dot, its row, and the dots here,
// with no message per burst. Closing a dialog leaves the blink running, with
// a floating bar to stop it, so the Map can be watched while it runs.
let blink = null;        // { hostId, started, until } in server milliseconds, or null
let blinkOffset = 0;     // the server's clock minus this browser's, in ms
let blinkBoxId = null;   // the dialog box showing this blink's countdown, if any
let blinkTicker = null;

function serverNow() { return Date.now() + blinkOffset; }

function setBlink(b, serverTime) {
  if (serverTime) blinkOffset = new Date(serverTime).getTime() - Date.now();
  blink = { hostId: b.hostId, started: new Date(b.started).getTime(), until: new Date(b.until).getTime() };
  if (!blinkTicker) blinkTicker = setInterval(blinkTick, 100);
  blinkTick();
}

// "on" while a burst is going out, "off" between bursts, null when none runs.
function blinkPhase() {
  if (!blink) return null;
  const t = serverNow() - blink.started;
  if (t < 0 || serverNow() >= blink.until) return null;
  return Math.floor(t / 1000) % 2 === 0 ? "on" : "off";
}

function blinkTick() {
  if (blink && serverNow() >= blink.until) finishBlink();
  const phase = blinkPhase();
  const id = blink && phase ? String(blink.hostId) : null;
  document.querySelectorAll(".blinking").forEach(e => {
    if (e.getAttribute("data-id") !== id) e.classList.remove("blinking", "blink-on");
  });
  if (id) {
    document.querySelectorAll(`.map-svg .node.dev[data-id="${id}"], tr[data-id="${id}"]`).forEach(e => {
      e.classList.add("blinking");
      e.classList.toggle("blink-on", phase === "on");
    });
  }
  const host = blink ? hosts.find(h => h.id === blink.hostId) : null;
  const left = blink ? Math.max(0, Math.ceil((blink.until - serverNow()) / 1000)) : 0;
  const name = host ? nameOrIp(host) : "a device";
  const msg = `Pulsing ${name}'s port light — ${left} s left` + (host && !host.online ? " (it's offline, so it may not show)" : "");
  let boxShown = false;
  if (blinkBoxId && blink) {
    const box = $(blinkBoxId);
    box.querySelector(".blink-text").textContent = msg;
    box.classList.toggle("blink-on", phase === "on");
    boxShown = !box.closest(".modal-backdrop").hidden;
  }
  // The floating bar covers every other case: the dialog closed to watch the
  // Map, or a blink started from another browser.
  const bar = $("blinkBar");
  bar.hidden = !blink || boxShown;
  if (blink) {
    $("blinkBarText").textContent = msg;
    bar.classList.toggle("blink-on", phase === "on");
  }
}

function finishBlink(msg) {
  const host = blink ? hosts.find(h => h.id === blink.hostId) : null;
  blink = null;
  if (blinkTicker) { clearInterval(blinkTicker); blinkTicker = null; }
  if (blinkBoxId) {
    const box = $(blinkBoxId);
    box.classList.remove("blink-on");
    box.classList.add("done");
    box.querySelector(".blink-text").textContent = msg ||
      `Finished. Didn't spot it? Run it again${host && !host.online ? ", though the device is offline, so nothing may reach it" : ""}.`;
    box.querySelector(".blink-stop").hidden = true;
    blinkBoxId = null;
  }
  blinkTick();
}

function stopBlink() {
  if (!blink) return;
  fetch("/api/blink", { method: "DELETE" }).catch(() => {});
  finishBlink("Stopped.");
}

// What /api/hosts says is running. A poll answered just before a blink we
// started here can't know about it yet, so a brand-new blink isn't ended by it.
function syncBlink(b, serverTime) {
  if (b) {
    if (!blink || blink.hostId !== b.hostId || blink.started !== new Date(b.started).getTime()) setBlink(b, serverTime);
  } else if (blink && serverNow() > blink.started + 3000) {
    finishBlink();
  }
}

function resetBlinkBox(id) {
  if (blinkBoxId === id) blinkBoxId = null;
  const box = $(id);
  box.hidden = true;
  box.className = "blink-box";
  box.innerHTML = "";
  blinkTick();
}

async function startBlink(host, boxId) {
  if (blinkBoxId && blinkBoxId !== boxId) resetBlinkBox(blinkBoxId);
  const box = $(boxId);
  box.className = "blink-box";
  box.innerHTML = `
    <div class="blink-line"><span class="blink-dot"></span><span class="blink-text">Starting…</span>
      <button class="toggle blink-stop">Stop</button></div>
    <div class="blink-help">Look at the switch for a port light pulsing on and off once a second,
      in step with the dot here. The device's dot on the Map and its row in the list pulse with
      it, so you can close this and watch the Map; the blink keeps going until it ends or you
      press Stop. The port this BAMF machine is plugged into, and any cable towards the router or
      another switch, pulse too because the traffic passes through them: ignore those. A Wi-Fi
      device pulses its access point's port instead, and a virtual machine the port of the
      machine it runs on.</div>`;
  box.hidden = false;
  box.querySelector(".blink-stop").onclick = stopBlink;
  blinkBoxId = boxId;
  const fail = msg => {
    if (blinkBoxId === boxId) blinkBoxId = null;
    box.classList.add("err");
    box.querySelector(".blink-text").textContent = msg;
    box.querySelector(".blink-stop").hidden = true;
  };
  try {
    const r = await fetch(`/api/hosts/${host.id}/blink`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ seconds: 30 }),
    });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { fail(d.error || "Couldn't start (HTTP " + r.status + ")."); return; }
    setBlink({ hostId: d.hostId, started: d.started, until: d.until }, d.serverTime);
  } catch {
    fail("Couldn't reach BAMF.");
  }
}

// The Ports dialog: a whole switch on one screen, one picker per port, saved
// in one request. Replaces going device by device through the ⋯ menu.
let portsSwitch = null;
let portsUnrecorded = [];   // on this switch with no port; kept unless picked on a port
function openPortsDialog(sw) {
  if (memberKind(sw.kind)) return openVmDialog(sw);
  // An access point has no ports to fill in: its dialog is where SSIDs are added.
  if (sw.kind === "ap") return openSwitchDialog(sw);
  document.querySelector("#portsModal .ports-find").style.display = "";
  $("portsHelp").hidden = false; $("vmsHelp").hidden = true; $("ssidHelp").hidden = true; $("vpnHelp").hidden = true;
  portsSwitch = sw;
  $("portsTitle").textContent = "Ports — " + sw.name;
  $("portsSub").textContent = `${sw.ports} ports · ${uplinkText(sw)}`;
  const swHostIds = new Set(switches.map(s => s.hostId).filter(Boolean));
  const onThis = hosts.filter(h => h.switchId === sw.id && !h.forgotten);

  // One template picker, cloned per port: every device that could be plugged
  // in, grouped by network with this switch's own network first.
  const template = document.createElement("select");
  const empty = document.createElement("option");
  empty.value = "0"; empty.textContent = "— empty —";
  template.appendChild(empty);
  const candidates = hosts.filter(h => !h.forgotten && !swHostIds.has(h.id) && (!h.ignored || h.switchId === sw.id));
  const nets = [...new Set(candidates.map(h => h.subnet || ""))].sort((a, b) =>
    (b === sw.subnet) - (a === sw.subnet) || ipNum((a || "0.0.0.0").split("/")[0]) - ipNum((b || "0.0.0.0").split("/")[0]));
  for (const net of nets) {
    const group = document.createElement("optgroup");
    group.label = net || "no network";
    candidates.filter(h => (h.subnet || "") === net)
      .sort((a, b) => ipNum(a.ip) - ipNum(b.ip))
      .forEach(h => {
        const o = document.createElement("option");
        o.value = h.id;
        let where = "";
        if (h.switchId && h.switchId !== sw.id) {
          const other = switches.find(s => s.id === h.switchId);
          if (other) where = ` — on ${other.name}` + (h.switchPort ? ` ${portText(other, h.switchPort)}` : "");
        }
        const n = nameOrIp(h);
        o.textContent = (n === h.ip ? h.ip : `${n} · ${h.ip}`) + where;
        group.appendChild(o);
      });
    template.appendChild(group);
  }

  const find = template.cloneNode(true);
  find.id = "portsFindHost";
  find.options[0].textContent = "Find a device's port…";
  $("portsFindHost").replaceWith(find);
  resetBlinkBox("portsBlink");

  const table = $("portsTable");
  table.innerHTML = "";
  const head = document.createElement("tr");
  head.innerHTML = `<th></th><th>Device</th><th class="loc">Location</th>`;
  table.appendChild(head);
  // The Location box goes on a port's first row only; a port with several
  // recorded devices still has one location.
  const addRow = (label, cell, port) => {
    const tr = document.createElement("tr");
    const pn = document.createElement("td");
    pn.className = "pn"; pn.textContent = label;
    const td = document.createElement("td");
    td.appendChild(cell);
    const loc = document.createElement("td");
    loc.className = "loc";
    if (port) {
      const input = document.createElement("input");
      input.className = "loc-input";
      input.dataset.port = port;
      input.maxLength = 40;
      input.placeholder = port === 1 ? "e.g. Living Room" : "";
      input.value = portLabel(sw, port);
      input.setAttribute("aria-label", `Port ${port} location`);
      loc.appendChild(input);
    }
    tr.append(pn, td, loc);
    table.appendChild(tr);
  };
  const picker = (port, hostId) => {
    const sel = template.cloneNode(true);
    sel.dataset.port = port;
    sel.value = String(hostId || 0);
    sel.onchange = markPortDuplicates;
    return sel;
  };
  for (let p = 1; p <= sw.ports; p++) {
    const label = "Port " + p;
    const subs = switches.filter(s => s.uplink === "switch" && s.uplinkSwitch === sw.id && s.uplinkPort === p);
    let first = true;
    const row = cell => { addRow(first ? label : "", cell, first ? p : 0); first = false; };
    for (const s of subs) {
      const d = document.createElement("div");
      d.className = "sub-sw";
      d.textContent = `${s.name} (switch)`;
      row(d);
    }
    // A port can hold more than one recorded device (an unmanaged switch or
    // an access point behind it); keep every one rather than dropping any.
    const here = onThis.filter(h => h.switchPort === p);
    if (here.length) here.forEach(h => row(picker(p, h.id)));
    else if (!subs.length) row(picker(p, 0));
  }
  portsUnrecorded = onThis.filter(h => !h.switchPort || h.switchPort > sw.ports);
  $("portsExtra").textContent = portsUnrecorded.length
    ? "Also on this switch, port not recorded: " + portsUnrecorded.map(nameOrIp).join(", ") +
      ". Pick one on a port above to give it a port; otherwise it stays as it is."
    : "";
  showErr("portsErr", "");
  $("portsModal").hidden = false;
  table.querySelector("select")?.focus();
}
function markPortDuplicates() {
  const sels = [...$("portsTable").querySelectorAll("select")];
  const count = new Map();
  sels.forEach(s => { if (s.value !== "0") count.set(s.value, (count.get(s.value) || 0) + 1); });
  sels.forEach(s => s.classList.toggle("dup", s.value !== "0" && count.get(s.value) > 1));
  return sels;
}
function closePortsDialog() { $("portsModal").hidden = true; portsSwitch = null; blinkTick(); }
async function savePortsDialog() {
  const sw = portsSwitch;
  if (!sw) return;
  if (memberKind(sw.kind)) return saveVmDialog(sw);
  const sels = markPortDuplicates();
  const chosen = new Map();   // host id -> port
  for (const s of sels) {
    if (s.value === "0") continue;
    const id = Number(s.value), port = Number(s.dataset.port);
    if (chosen.has(id)) {
      const h = hosts.find(x => x.id === id);
      showErr("portsErr", `${h ? nameOrIp(h) : "A device"} is picked on ports ${chosen.get(id)} and ${port}. A device can only be on one port.`);
      return;
    }
    chosen.set(id, port);
  }
  for (const h of portsUnrecorded) if (!chosen.has(h.id)) chosen.set(h.id, 0);
  const btn = $("portsSave");
  btn.disabled = true;
  try {
    const r = await fetch(`/api/switches/${sw.id}/ports`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        ports: [...chosen].map(([hostId, port]) => ({ hostId, port })),
        labels: [...$("portsTable").querySelectorAll(".loc-input")].map(i => ({ port: Number(i.dataset.port), label: i.value.trim() })),
      }),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      showErr("portsErr", d.error || "Couldn't save (HTTP " + r.status + ").");
      return;
    }
    const n = [...chosen.values()].length;
    toast(`${esc(sw.name)} saved: ${n} device${n === 1 ? "" : "s"} recorded`);
    closePortsDialog();
    await refresh();
  } catch {
    showErr("portsErr", "Couldn't reach BAMF.");
  } finally {
    btn.disabled = false;
  }
}

// A virtual switch's VMs: a checklist, not a port list. Devices whose MAC a
// hypervisor gave out are marked and listed first, with one click to tick them.
function openVmDialog(sw) {
  portsSwitch = sw;
  const ssid = sw.kind === "ssid", vpn = sw.kind === "vpn";
  const on = runsOnHost(sw), ap = apOf(sw);
  const vpnHost = vpn && sw.hostId ? hosts.find(h => h.id === sw.hostId) : null;
  $("portsTitle").textContent = (ssid ? "Devices — " : vpn ? "Clients — " : "VMs — ") + sw.name;
  $("portsSub").textContent = ssid ? (ap ? `wireless SSID on ${ap.name}` : "wireless SSID")
    : vpn ? "VPN" + (vpnHost ? ` · ${curAddrs(vpnHost).map(a => a.ip).join(", ")}` : "")
    : on ? `virtual switch on ${nameOrIp(on)} · ${on.ip}` : "virtual switch";
  document.querySelector("#portsModal .ports-find").style.display = "none";
  $("portsHelp").hidden = true; $("vmsHelp").hidden = ssid || vpn; $("ssidHelp").hidden = !ssid; $("vpnHelp").hidden = !vpn;
  resetBlinkBox("portsBlink");
  const swHostIds = new Set(switches.map(s => s.hostId).filter(Boolean));
  // Any network, not just the machine's own: a hypervisor often bridges VMs
  // onto other networks and VLANs. Grouped by network, the switch's own first.
  const home = sw.subnet || (on && on.subnet) || "";
  // A virtual switch's own machine can't be one of its VMs. (An SSID's runsOn
  // is an access point's switch id, not a device, so it excludes nothing.)
  const list = hosts.filter(h => !h.forgotten && !swHostIds.has(h.id) && !(sw.kind === "virtual" && h.id === sw.runsOn) && (!h.ignored || h.switchId === sw.id));
  const nets = [...new Set(list.map(h => h.subnet || ""))].sort((a, b) =>
    (b === home) - (a === home) || ipNum((a || "0.0.0.0").split("/")[0]) - ipNum((b || "0.0.0.0").split("/")[0]));
  const byNet = nets.map(net => [net, list.filter(h => (h.subnet || "") === net)
    .sort((a, b) => (b.switchId === sw.id) - (a.switchId === sw.id) || (!ssid && !!vmPlatform(b.mac) - !!vmPlatform(a.mac)) || ipNum(a.ip) - ipNum(b.ip))]);
  const table = $("portsTable");
  table.innerHTML = "";
  const tbl = document.createElement("table");
  tbl.className = "vm-table";
  let suggested = 0;
  for (const [net, group] of byNet) {
    if (nets.length > 1) {
      const hr = document.createElement("tr");
      const hd = document.createElement("td");
      hd.colSpan = 2; hd.className = "vm-net";
      hd.textContent = (net || "no network") + (net === home && home ? " · this network" : "");
      hr.appendChild(hd);
      tbl.appendChild(hr);
    }
    for (const h of group) {
      const tr = document.createElement("tr");
      const c1 = document.createElement("td"), c2 = document.createElement("td");
      const box = document.createElement("input");
      box.type = "checkbox"; box.className = "vm-pick"; box.dataset.id = h.id;
      box.checked = h.switchId === sw.id;
      box.id = "vm-" + h.id;
      c1.appendChild(box);
      const label = document.createElement("label");
      label.htmlFor = box.id;
      label.textContent = nameOrIp(h);
      const ip = document.createElement("span");
      ip.className = "vm-ip"; ip.textContent = h.ip + (alsoAt(h).length ? ` +${alsoAt(h).length}` : "");
      if (alsoAt(h).length) ip.title = "Also at " + alsoAt(h).map(a => a.ip).join(", ");
      label.appendChild(ip);
      const plat = sw.kind === "virtual" ? vmPlatform(h.mac) : null;
      if (plat) {
        const b = document.createElement("span");
        b.className = "vm-badge"; b.textContent = `${plat} MAC`;
        label.appendChild(b);
        if (!box.checked) suggested++;
      }
      const other = h.switchId && h.switchId !== sw.id ? switches.find(s => s.id === h.switchId) : null;
      if (other) {
        const w = document.createElement("span");
        w.className = "vm-ip"; w.textContent = `— on ${other.name}`;
        label.appendChild(w);
      }
      c2.appendChild(label);
      tr.append(c1, c2);
      tbl.appendChild(tr);
    }
  }
  const holder = document.createElement("tr");
  const cell = document.createElement("td");
  cell.appendChild(tbl);
  holder.appendChild(cell);
  table.appendChild(holder);
  const extra = $("portsExtra");
  extra.innerHTML = "";
  if (suggested) {
    extra.append(`${suggested} device${suggested === 1 ? " has" : "s have"} a virtual-machine MAC address. `);
    const tick = document.createElement("button");
    tick.className = "toggle"; tick.type = "button"; tick.textContent = "Tick suggested";
    tick.onclick = () => tbl.querySelectorAll(".vm-pick").forEach(b => {
      const h = hosts.find(x => x.id === Number(b.dataset.id));
      if (h && vmPlatform(h.mac)) b.checked = true;
    });
    extra.appendChild(tick);
  } else if (!list.length) {
    extra.textContent = "No other devices yet.";
  }
  showErr("portsErr", "");
  $("portsModal").hidden = false;
}
async function saveVmDialog(sw) {
  const ids = [...$("portsTable").querySelectorAll(".vm-pick")].filter(b => b.checked).map(b => Number(b.dataset.id));
  const btn = $("portsSave");
  btn.disabled = true;
  try {
    const r = await fetch(`/api/switches/${sw.id}/ports`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ports: ids.map(hostId => ({ hostId, port: 0 })) }),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      showErr("portsErr", d.error || "Couldn't save (HTTP " + r.status + ").");
      return;
    }
    toast(sw.kind === "ssid" ? `${esc(sw.name)}: ${ids.length} device${ids.length === 1 ? "" : "s"} on this SSID`
      : sw.kind === "vpn" ? `${esc(sw.name)}: ${ids.length} client${ids.length === 1 ? "" : "s"} on this VPN`
      : `${esc(sw.name)}: ${ids.length} VM${ids.length === 1 ? "" : "s"} recorded`);
    closePortsDialog();
    await refresh();
  } catch {
    showErr("portsErr", "Couldn't reach BAMF.");
  } finally {
    btn.disabled = false;
  }
}

// ---- switch traffic counters (SNMP) ----
let snmpCache = null, snmpSwitch = null, snmpLoading = false;
function loadSnmp() {
  if (snmpLoading) return;
  snmpLoading = true;
  fetch("/api/switches/snmp").then(r => r.ok ? r.json() : null).then(d => {
    snmpLoading = false;
    if (d) { snmpCache = d; renderSwitchList(); }
  }).catch(() => { snmpLoading = false; });
}
function openSnmpDialog(sw) {
  snmpSwitch = sw;
  const st = (snmpCache || []).find(x => x.switchId === sw.id);
  const host = sw.hostId ? hosts.find(h => h.id === sw.hostId) : null;
  $("snmpTitle").textContent = "Traffic counters \u2014 " + sw.name;
  $("snmpOn").checked = st ? st.enabled : true;
  // The address field holds only an address typed in; the switch's own device's is the placeholder.
  $("snmpAddr").value = st && st.address && (!host || st.address !== host.ip) ? st.address : "";
  $("snmpAddr").placeholder = host ? host.ip + " (its device)" : "the switch's address";
  $("snmpComm").value = "";
  $("snmpComm").placeholder = st && st.hasCommunity ? "saved (leave blank to keep)" : "public";
  renderSnmpResult(st && st.lastPoll ? st : null);
  $("snmpModal").hidden = false;
}
function renderSnmpResult(st) {
  const res = $("snmpResult"), table = $("snmpPorts");
  table.innerHTML = "";
  res.className = "snmp-result";
  if (!st) { res.textContent = ""; return; }
  if (!st.enabled) { res.textContent = "Off: BAMF isn't reading this switch."; return; }
  if (st.error) { res.classList.add("bad"); res.textContent = st.error; return; }
  res.textContent = `Read ${st.portsRead} port${st.portsRead === 1 ? "" : "s"} (${st.mapping}) ${fmtAgo(st.lastPoll)}. ` +
    (st.counted ? `Counting ${st.counted} device${st.counted === 1 ? "" : "s"}.` : "No port has exactly one device recorded on it yet, so nothing is counted.");
  for (const p of st.ports || []) {
    const tr = document.createElement("tr");
    if (p.hostId) tr.className = "counted";
    tr.innerHTML = `<td class="n"></td><td class="${p.hostId ? "who" : "why"}"></td>`;
    tr.children[0].textContent = `${p.port}${p.name ? " \u00b7 " + p.name : ""}`;
    tr.children[1].textContent = p.hostId ? p.device : p.note;
    table.appendChild(tr);
  }
}
async function saveSnmp() {
  const sw = snmpSwitch;
  if (!sw) return;
  const btn = $("snmpSave");
  btn.disabled = true;
  $("snmpResult").className = "snmp-result";
  $("snmpResult").textContent = $("snmpOn").checked ? "Asking the switch\u2026" : "";
  try {
    const comm = $("snmpComm").value.trim();
    const r = await fetch(`/api/switches/${sw.id}/snmp`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: $("snmpOn").checked, address: $("snmpAddr").value.trim(), community: comm || null }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { $("snmpResult").className = "snmp-result bad"; $("snmpResult").textContent = d.error || "Couldn't save (HTTP " + r.status + ")."; return; }
    $("snmpComm").value = "";
    if (d.hasCommunity) $("snmpComm").placeholder = "saved (leave blank to keep)";
    renderSnmpResult(d);
    loadSnmp();
  } catch (e) { console.error(e); $("snmpResult").className = "snmp-result bad"; $("snmpResult").textContent = "Couldn't reach BAMF."; }
  finally { btn.disabled = false; }
}
function closeSnmpDialog() { $("snmpModal").hidden = true; snmpSwitch = null; }
$("snmpSave").onclick = saveSnmp;
$("snmpCancel").onclick = closeSnmpDialog;
$("snmpModal").onclick = e => { if (e.target.id === "snmpModal") closeSnmpDialog(); };

function renderSwitchList() {
  const table = $("swTable");
  if (!table) return;
  if (snmpCache === null) loadSnmp();
  table.innerHTML = "";
  $("swEmpty").hidden = switches.length > 0;
  for (const sw of switches) {
    const host = sw.hostId ? hosts.find(h => h.id === sw.hostId) : null;
    const n = hosts.filter(h => h.switchId === sw.id && !h.forgotten).length;
    const tr = document.createElement("tr");
    const snmpOn = (snmpCache || []).some(x => x.switchId === sw.id && x.enabled);
    tr.innerHTML = `<td class="sw-name"></td><td class="set-eff"></td><td style="white-space:nowrap"><button class="toggle sw-ports-btn">Ports</button> <button class="toggle sw-edit-btn">Edit</button>` +
      (sw.kind === "switch" || sw.kind === "router" ? ` <button class="toggle sw-snmp-btn" title="Read this switch's traffic per port over SNMP">${snmpOn ? "Counters \u2713" : "Counters"}</button>` : "") + `</td>`;
    tr.children[0].textContent = sw.name;
    const ssids = sw.kind === "ap" ? switches.filter(x => x.kind === "ssid" && x.runsOn === sw.id) : [];
    tr.children[1].textContent = [
      sw.kind === "virtual" ? "Virtual switch" : sw.kind === "ssid" ? "Wireless SSID" : sw.kind === "ap" ? "Access point" : sw.kind === "vpn" ? "VPN"
        : `${kindNoun(sw.kind)[0].toUpperCase() + kindNoun(sw.kind).slice(1)}, ${sw.ports} ports`,
      sw.subnet || "no network", innerKind(sw.kind) ? null : host ? host.ip : "no address BAMF sees",
      uplinkText(sw),
      sw.kind === "ap" ? (ssids.length ? `SSIDs: ${ssids.map(x => x.name).join(", ")}` : "no SSIDs yet")
        : `${n} ${sw.kind === "virtual" ? "VM" : sw.kind === "vpn" ? "client" : "device"}${n === 1 ? "" : "s"} recorded`,
    ].filter(Boolean).join(" · ");
    const pb = tr.querySelector(".sw-ports-btn");
    if (sw.kind === "virtual") pb.textContent = "VMs";
    if (sw.kind === "ssid") pb.textContent = "Devices";
    if (sw.kind === "vpn") pb.textContent = "Clients";
    if (sw.kind === "ap") pb.textContent = "Add SSID";
    pb.onclick = () => sw.kind === "ap" ? openSwitchDialog(null, null, null, { kind: "ssid", runsOn: sw.id }) : openPortsDialog(sw);
    tr.querySelector(".sw-edit-btn").onclick = () => openSwitchDialog(sw);
    const cb = tr.querySelector(".sw-snmp-btn");
    if (cb) cb.onclick = () => openSnmpDialog(sw);
    table.appendChild(tr);
  }
}

// On-demand device identification. Results land in the scan panel alongside
// port scans, so there's one place to read everything BAMF probed.
async function identifyHost(host) {
  const el = addScanEntry(dispName(host), host.ip, "identify");
  try {
    const r = await fetch(`/api/hosts/${host.id}/identify`, { method: "POST" });
    const d = await r.json();
    el.classList.remove("busy");
    if (!r.ok || d.ok !== true) {
      el.querySelector(".scan-entry-body").innerHTML = `<span class="none">Identify failed.</span>`;
      return;
    }
    const bits = [
      `<div><strong>${esc(d.osGuess || "Unknown")}</strong></div>`,
      `<div class="none">TTL ${d.ttl ?? "no ICMP reply"}</div>`,
      d.openPorts && d.openPorts.length
        ? portChips(host.ip, d.openPorts.map(p => ({ port: p, service: "" })))
        : `<span class="none">No fingerprint ports open.</span>`,
    ];
    el.querySelector(".scan-entry-body").innerHTML = bits.join("");
    await refresh();
  } catch (e) {
    el.classList.remove("busy");
    el.querySelector(".scan-entry-body").innerHTML = `<span class="none">Identify failed — see server logs.</span>`;
  }
}

async function scanPorts(host, _btn, spec) {
  const el = addScanEntry(dispName(host), host.ip, spec ? ("ports " + spec) : "common");
  try {
    const q = spec ? "?ports=" + encodeURIComponent(spec) : "";
    const r = await fetch(`/api/hosts/${host.id}/portscan${q}`);
    const data = await r.json();
    const ports = Array.isArray(data) ? data : data.ports;
    el.classList.remove("busy");
    el.querySelector(".scan-entry-body").innerHTML = portChips(host.ip, ports) +
      (data.newlyOpen ? `<div class="watch-note">${data.newlyOpen} port${data.newlyOpen === 1 ? "" : "s"} open that ${data.newlyOpen === 1 ? "wasn't" : "weren't"} last time.</div>` : "");
  } catch (e) {
    el.classList.remove("busy");
    el.querySelector(".scan-entry-body").innerHTML = `<span class="none">Scan failed.</span>`;
  }
}
