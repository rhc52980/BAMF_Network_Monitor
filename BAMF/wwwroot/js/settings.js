function renderArpToggle() {
  const t = $("setArp");
  setToggleState(t, activeArp.enabled);
  t.classList.toggle("nodriver", activeArp.enabled && !activeArp.npcapAvailable);
  const lbl = $("arpToggleLbl");
  if (lbl) lbl.textContent = activeArp.npcapAvailable
    ? (activeArp.enabled ? "Driver found — sending raw ARP." : "Driver found.")
    : (activeArp.enabled ? "Driver not installed — running the ping sweep instead." : "");
  t.title = !activeArp.npcapAvailable
    ? (activeArp.enabled
        ? "Active ARP is on, but the Npcap driver isn't installed - running ping sweep. Install it from npcap.com."
        : "Turn on active ARP scanning (needs the Npcap driver from npcap.com)")
    : (activeArp.enabled
        ? "Active ARP scanning is on - click to switch to ping sweep"
        : "Click to turn on active ARP scanning");
}

// ---- The New tab's length ----
$("setNewDaysSave").onclick = async () => {
  const days = Math.round(Number($("setNewDays").value));
  try {
    const r = await fetch("/api/settings/newdays", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ days }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(d.error || "Couldn't save that"); return; }
    newDays = d.days;
    render();
    toast(`A device counts as new for ${d.days} day${d.days === 1 ? "" : "s"}, or until it's marked known`);
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};

function renderUpdateToggle() {
  const t = $("setUpd");
  const on = !!(updateInfo && updateInfo.enabled);
  setToggleState(t, on);
  t.title = on
    ? "Checking GitHub once a day for a newer release - click to stop"
    : "Off. Click to check GitHub once a day for a newer release (read-only; nothing is downloaded)";

  // Hide the badge when checking is off, even if a previous check found one —
  // otherwise turning the feature off leaves its result on screen.
  const badge = $("updateBadge");
  if (on && updateInfo && updateInfo.available && updateInfo.latest) {
    badge.hidden = false;
    badge.textContent = "update " + updateInfo.latest;
    badge.href = updateInfo.url || "#";
    badge.title = `BAMF ${updateInfo.latest} is available (you're on ${appVersion || "?"}) - opens the release page`;
  } else {
    badge.hidden = true;
  }

  // The Settings row gets the words; the header badge stays for the one case
  // worth interrupting for, a release that is actually newer.
  const status = $("setUpdStatus");
  if (status) {
    if (!on) status.textContent = "Off.";
    else if (updateInfo && updateInfo.available && updateInfo.latest)
      status.textContent = `${updateInfo.latest} is available — you're on ${appVersion || "?"}.`;
    else if (updateInfo && updateInfo.checkedUtc)
      status.textContent = `You're on the latest release (checked ${fmtAgo(updateInfo.checkedUtc)}).`;
    else status.textContent = "On — the first check runs shortly.";
  }
}

$("setUpd").onclick = async () => {
  const next = !(updateInfo && updateInfo.enabled);
  try {
    const r = await fetch("/api/settings/update-check", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: next }),
    });
    const d = await r.json();
    updateInfo = { ...(updateInfo || {}), enabled: next, available: d.available, latest: d.latest, url: d.url };
    renderUpdateToggle();
    toast(!next
      ? "Update checks off"
      : d.available
        ? `Update available: <span class="mono">${esc(d.latest)}</span>`
        : "Update checks on — you're on the latest release");
  } catch (e) { console.error(e); }
};

function renderTrafficToggle() {
  const t = $("setTraffic");
  if (!t) return;
  const st = trafficStatus || {};
  setToggleState(t, !!st.enabled);
  $("setTrafficStatus").textContent = !st.available ? "The Npcap driver isn't installed, so this does nothing."
    : !st.enabled ? "Off."
    : st.running ? `Watching ${(st.interfaces || []).join(", ")} since ${fmtAgo(st.since).replace(" ago", "")} ago.`
    : st.error ? "Not running: " + st.error : "Starting with the next scan.";
  t.title = st.enabled ? "The traffic monitor is on - click to turn it off" : "Click to watch bytes per device and DHCP/DNS servers";
}
$("setTraffic").onclick = async () => {
  const next = !(trafficStatus && trafficStatus.enabled);
  try {
    const r = await fetch("/api/settings/traffic-monitor", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }),
    });
    if (!r.ok) throw new Error("HTTP " + r.status);
    trafficStatus = { ...trafficStatus, enabled: next };
    renderTrafficToggle();
    toast(next ? "Traffic monitor on: it starts with the next scan" : "Traffic monitor off: it stops with the next scan");
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};

// Names from the router: its status, Import now, and copying the names over.
async function loadRouterImport() {
  try { renderRouterImport(await (await fetch("/api/router-import")).json()); } catch { }
}
function renderRouterImport(st) {
  const named = hosts.filter(h => h.routerName).length;
  $("routerStatus").textContent = !st.enabled
    ? "Not set up. Add Bamf:RouterImport to appsettings.json; the README has an example for each router."
    : `${st.kind} at ${st.host}` + (st.lastRun ? ` · last read ${fmtAgo(st.lastRun)}` : " · not read yet")
      + (st.error ? ` · failed: ${st.error}` : st.lastRun ? ` · ${st.count} named device${st.count === 1 ? "" : "s"}, ${named} of them seen by BAMF` : "");
  $("routerRun").disabled = !st.enabled;
  $("routerApply").disabled = !named;
}
$("routerRun").onclick = async () => {
  const b = $("routerRun"); b.disabled = true; b.textContent = "Reading…";
  try {
    const r = await fetch("/api/router-import/run", { method: "POST" });
    const st = await r.json();
    await refresh();
    renderRouterImport(st);
    toast(r.ok ? `Read ${st.count} name${st.count === 1 ? "" : "s"} from the router` : "The router didn't answer: " + (st.error || r.status));
  } catch (e) { toast("Couldn't reach BAMF"); }
  finally { b.textContent = "Import now"; }
};
$("routerApply").onclick = async () => {
  try {
    const r = await fetch("/api/router-import/apply", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ overwrite: false }) });
    const d = await r.json();
    await refresh();
    toast(d.named ? `Named ${d.named} device${d.named === 1 ? "" : "s"} from the router` : "Every device the router names already has a name");
  } catch { toast("Couldn't save the names"); }
};

function renderGreyNoiseToggle() {
  const t = $("setGreyNoise");
  if (!t) return;
  const on = !!(greynoiseCache && greynoiseCache.enabled);
  setToggleState(t, on);
  t.title = on ? "BAMF checks your public address with GreyNoise daily - click to stop" : "Click to have BAMF check your public address with GreyNoise daily";
  $("setGreyNoiseStatus").textContent = on ? greyNoiseLine().text : "";
}
$("setGreyNoise").onclick = async () => {
  const next = !(greynoiseCache && greynoiseCache.enabled);
  const t = $("setGreyNoise");
  if (next) $("setGreyNoiseStatus").textContent = "Checking…";
  try {
    const r = await fetch("/api/settings/greynoise", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }) });
    if (!r.ok) throw new Error("HTTP " + r.status);
    greynoiseCache = await r.json();
    renderGreyNoiseToggle();
    renderHygiene();
    toast(!next ? "The GreyNoise check is off: BAMF won't contact GreyNoise or ipify"
      : greynoiseCache.result && greynoiseCache.result.noise ? "GreyNoise has seen your address scanning the internet: see the hygiene card"
      : greynoiseCache.result && greynoiseCache.result.error ? "GreyNoise check on, but this one failed: " + greynoiseCache.result.error
      : "GreyNoise check on: your address hasn't been seen scanning");
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); renderGreyNoiseToggle(); }
};
function renderArpWatchToggle() {
  const t = $("setArpWatch");
  if (!t) return;
  setToggleState(t, arpWatch);
  t.title = arpWatch ? "The ARP watch is on - click to stop it" : "Click to watch for IP conflicts and a changed gateway";
}
function renderIpv6WatchToggle() {
  const t = $("setIpv6Watch");
  if (!t) return;
  setToggleState(t, ipv6Watch);
  t.title = ipv6Watch ? "IPv6 addresses are watched - click to stop" : "Click to watch devices' IPv6 addresses";
}
function renderCertWatchToggle() {
  const t = $("setCertWatch");
  if (!t) return;
  setToggleState(t, certWatch);
  t.title = certWatch ? "Certificates are checked every morning - click to stop" : "Click to check HTTPS certificates every morning";
}
for (const [id, url, get, set, on, off] of [
  ["setArpWatch", "/api/settings/arp-watch", () => arpWatch, v => { arpWatch = v; renderArpWatchToggle(); },
    "The ARP watch is on", "The ARP watch is off"],
  ["setCertWatch", "/api/settings/cert-watch", () => certWatch, v => { certWatch = v; renderCertWatchToggle(); },
    "Certificates will be checked every morning at 4:30", "The certificate watch is off"],
  ["setIpv6Watch", "/api/settings/ipv6-watch", () => ipv6Watch, v => { ipv6Watch = v; renderIpv6WatchToggle(); },
    "IPv6 addresses will be watched", "The IPv6 watch is off"],
]) {
  $(id).onclick = async () => {
    const next = !get();
    try {
      const r = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }) });
      if (!r.ok) throw new Error("HTTP " + r.status);
      set(next);
      toast(next ? on : off);
    } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
  };
}
function renderLatencyToggle() {
  const t = $("setLatency");
  if (!t) return;
  setToggleState(t, latencyProbe);
  t.title = latencyProbe ? "Latency is measured after each scan - click to stop" : "Click to measure latency after each scan";
}
$("setLatency").onclick = async () => {
  const next = !latencyProbe;
  try {
    const r = await fetch("/api/settings/latency-probe", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }),
    });
    if (!r.ok) throw new Error("HTTP " + r.status);
    latencyProbe = next;
    renderLatencyToggle();
    toast(next ? "Latency will be measured after each scan" : "Latency measuring is off; the column keeps its last readings");
  } catch (e) { console.error(e); toast("Couldn't save that - see the server log"); }
};

function renderRandToggle() {
  const t = $("setRand");
  setToggleState(t, autoIgnoreRandom);
  t.title = autoIgnoreRandom
    ? "New devices with randomized MACs are auto-filed under Ignored - click to disable"
    : "Click to auto-ignore new devices with randomized MACs (phone privacy addresses)";
}

$("wallOpen").onclick = () => {
  window.open("/wall" + (network !== "all" ? "?net=" + encodeURIComponent(network) : ""), "_blank", "noopener");
};

$("setRand").onclick = async () => {
  const next = !autoIgnoreRandom;
  try {
    await fetch("/api/settings/auto-ignore-random", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: next }),
    });
    autoIgnoreRandom = next;
    renderRandToggle();
    toast(next
      ? "Auto-ignoring randomized MACs - applies to new devices from next scan"
      : "Randomized MACs will alert like any other new device");
  } catch (e) { console.error(e); }
};

$("setArp").onclick = async () => {
  const next = !activeArp.enabled;
  try {
    await fetch("/api/settings/active-arp", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: next }),
    });
    activeArp.enabled = next;
    renderArpToggle();
    toast(next
      ? "Active ARP scanning on - takes effect next scan"
      : "Switched to ping sweep - takes effect next scan");
  } catch (e) { console.error(e); }
};

// ---- What's new ----
// The notes ship with BAMF, in whats-new.json, so they're there on a network
// that never reaches the internet. Each browser remembers the last version it
// was shown; when BAMF comes up newer, a strip under the header says so, and
// its button lists what changed in the versions in between. A browser that has
// never opened BAMF before starts at the current version with nothing to show.
// The version chip in the header opens the list any time.
let newsList = null, newsSince = null, newsChecked = false;
const verParts = v => String(v).split(/[^0-9.]/)[0].split(".").map(n => parseInt(n, 10) || 0);
function verCmp(a, b) {
  const x = verParts(a), y = verParts(b);
  for (let i = 0; i < Math.max(x.length, y.length); i++) { const d = (x[i] || 0) - (y[i] || 0); if (d) return d; }
  return 0;
}
function loadNews() {
  if (newsList) return Promise.resolve(newsList);
  return fetch("/whats-new.json", { cache: "no-cache" }).then(r => r.ok ? r.json() : null)
    .then(d => (newsList = (d && d.versions || []).slice().sort((a, b) => verCmp(b.version, a.version))))
    .catch(() => []);
}
function seenVersion() { try { return localStorage.getItem("bamf-seen-version"); } catch { return null; } }
function markSeen() { try { localStorage.setItem("bamf-seen-version", verParts(appVersion).join(".")); } catch {} }
async function checkNews() {
  if (newsChecked || !appVersion) return;
  newsChecked = true;
  const seen = seenVersion();
  if (!seen) { markSeen(); return; }
  if (verCmp(appVersion, seen) <= 0) return;
  const since = (await loadNews()).filter(v => verCmp(v.version, seen) > 0 && verCmp(v.version, appVersion) <= 0);
  if (!since.length) { markSeen(); return; }
  newsSince = seen;
  $("newsVer").textContent = verParts(appVersion).join(".");
  $("newsLead").textContent = since[0].items[0] || "";
  $("newsBar").hidden = false;
}
async function openNews() {
  const all = await loadNews();
  // From the strip: every version since the one this browser last saw. From
  // the chip: the last few, whatever has been seen.
  const list = newsSince ? all.filter(v => verCmp(v.version, newsSince) > 0 && verCmp(v.version, appVersion) <= 0)
    : all.filter(v => verCmp(v.version, appVersion) <= 0).slice(0, 5);
  const when = d => { const t = new Date(d + "T12:00:00"); return isNaN(t) ? "" : t.toLocaleDateString([], { month: "short", day: "numeric", year: "numeric" }); };
  $("newsBody").innerHTML = list.length
    ? list.map(v => `<div class="news-ver">${esc(v.version)}<span>${esc(when(v.date))}</span></div>` +
        `<ul class="news-list">${(v.items || []).map(i => `<li>${esc(i)}</li>`).join("")}</ul>`).join("")
    : `<p class="modal-help">No notes for this version.</p>`;
  $("newsAll").href = (repoUrl || "https://github.com/rhc52980/BAMF_Network_Monitor") + "/releases";
  $("newsModal").hidden = false;
}
function closeNews() {
  $("newsModal").hidden = true;
  if (newsSince) { newsSince = null; markSeen(); $("newsBar").hidden = true; }
}
$("newsOpen").onclick = openNews;
$("newsDismiss").onclick = () => { newsSince = null; markSeen(); $("newsBar").hidden = true; };
$("newsClose").onclick = closeNews;
$("newsModal").onclick = e => { if (e.target.id === "newsModal") closeNews(); };
$("version").onclick = openNews;
$("version").onkeydown = e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openNews(); } };
$("freeModal").onclick = e => { if (e.target.id === "freeModal") $("freeModal").hidden = true; };
$("scanCancel").onclick = () => { $("scanModal").hidden = true; };
$("scanModal").onclick = e => { if (e.target.id === "scanModal") $("scanModal").hidden = true; };
$("scanPortsMode").onchange = e => {
  $("scanCustomRow").style.display = e.target.value === "custom" ? "" : "none";
};
$("scanTarget").onchange = e => {
  const isIp = e.target.value === "ip";
  $("scanIpRow").style.display = isIp ? "" : "none";
  if (isIp) $("scanIp").focus();
};
$("scanIp").onkeydown = e => { if (e.key === "Enter") { e.preventDefault(); $("scanGo").click(); } };
$("scanGo").onclick = async () => {
  const target = $("scanTarget").value;
  const mode = $("scanPortsMode").value;
  const custom = $("scanCustom").value.trim();
  if (mode === "custom" && !custom) { $("scanResult").innerHTML = '<span class="scan-progress">Enter a port list first.</span>'; return; }

  const params = new URLSearchParams();
  if (mode === "custom") params.set("ports", custom);
  const portLabel = mode === "custom" ? ("ports " + custom) : "common";

  // Single arbitrary IP, or a wildcard pattern expanded across your networks.
  if (target === "ip") {
    const ip = $("scanIp").value.trim();
    if (!ip) { $("scanResult").innerHTML = '<span class="scan-progress">Enter an IP address or pattern first.</span>'; return; }
    const isPattern = /[*?]/.test(ip);
    params.set("ip", ip);
    $("scanModal").hidden = true;
    const el = addScanEntry(isPattern ? "Pattern scan" : "IP scan", ip, portLabel);
    try {
      const r = await fetch(`/api/portscan/${isPattern ? "pattern" : "ip"}?` + params.toString());
      const data = await r.json();
      el.classList.remove("busy");
      if (!r.ok) {
        el.querySelector(".scan-entry-body").innerHTML = `<span class="none">${esc(data.error || "Scan failed.")}</span>`;
        return;
      }
      if (isPattern) {
        renderMultiHostScan(el, data);
      } else {
        if (data.name) el.querySelector(".nm").textContent = data.name;
        el.querySelector(".scan-entry-body").innerHTML = portChips(data.ip, data.ports);
      }
    } catch (e) {
      el.classList.remove("busy");
      el.querySelector(".scan-entry-body").innerHTML = '<span class="none">Scan failed — see server logs.</span>';
    }
    return;
  }

  let scope = "all online hosts";
  if (target.startsWith("net:")) { params.set("subnet", target.slice(4)); scope = target.slice(4); }

  // close the dialog, open the results panel, show a busy header entry
  $("scanModal").hidden = true;
  const el = addScanEntry("Network scan", scope, portLabel);

  try {
    const r = await fetch("/api/portscan?" + params.toString());
    const data = await r.json();
    el.classList.remove("busy");
    el.querySelector(".nm").textContent = "Network scan";
    renderMultiHostScan(el, data);
  } catch (e) {
    el.classList.remove("busy");
    el.querySelector(".scan-entry-body").innerHTML = '<span class="none">Scan failed — see server logs.</span>';
  }
};

// Notifications live in the Settings tab with the rest of the settings. This
// fills the card from what the last /api/hosts poll reported; the URL field is
// left empty on purpose, since the server only ever hands back a masked form.
function fillNotify() {
  // Settings carries the webhook's details too. Opening Settings straight after
  // the page loads used to beat the device list to them, and the card said no
  // webhook was saved when one was.
  const se = settingsData && settingsData.editable;
  if (se && se.webhookConfigured !== undefined) {
    webhookConfigured = !!se.webhookConfigured;
    webhookMasked = se.webhookMasked || null;
    webhookFormat = se.webhookFormat || webhookFormat;
  }
  $("notifyFormat").value = webhookFormat;
  $("notifyCurrent").textContent = webhookMasked
    ? "Currently saved: " + webhookMasked
    : "No webhook saved — alerts are off.";
  $("notifyClear").style.display = webhookConfigured ? "" : "none";
  renderDestinations();
}

// ---- Where alerts go ----
// The main webhook takes every kind of alert unless some are unticked; more
// destinations each take the kinds ticked on them. URLs come back masked, so
// editing a saved one leaves its URL box empty, and empty keeps it.
const ALERT_KINDS = [
  ["devices", "New devices", "A device BAMF hasn't seen before"],
  ["status", "Offline and back", "Watched devices going offline and coming back, alert rules, and a snooze ending"],
  ["unusual", "Unusual activity", "A device off far longer than usual, on at an hour it never is, or much slower than usual"],
  ["security", "Security", "ARP spoofing and IP conflicts, new DHCP or DNS servers, newly open ports, certificates, GreyNoise"],
  ["internet", "Internet", "The internet watch: down, back, slow and back to normal"],
  ["reports", "Reports", "The scheduled report"],
];
const kindLabel = k => (ALERT_KINDS.find(d => d[0] === k) || [k, k])[1];
function kindBoxes(box, selected, onChange) {
  box.innerHTML = "";
  for (const [k, label, hint] of ALERT_KINDS) {
    const l = document.createElement("label");
    l.title = hint;
    l.innerHTML = `<input type="checkbox" value="${k}"${selected.includes(k) ? " checked" : ""}> <span></span>`;
    l.querySelector("span").textContent = label;
    if (onChange) l.querySelector("input").onchange = onChange;
    box.appendChild(l);
  }
}
const checkedKinds = box => [...box.querySelectorAll("input:checked")].map(i => i.value);
let destEditing = null;   // the destination being edited, or {} for a new one
function destData() { return (settingsData && settingsData.editable && settingsData.editable.destinations) || []; }
function renderDestinations() {
  const e = settingsData && settingsData.editable;
  if (!e) return;
  // The main webhook's kinds, once there is one.
  $("mainKindsRow").hidden = !webhookConfigured;
  kindBoxes($("mainKinds"), e.webhookKinds || ALERT_KINDS.map(k => k[0]), async () => {
    const kinds = checkedKinds($("mainKinds"));
    try {
      const r = await fetch("/api/settings/webhookkinds", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kinds }) });
      const d = await r.json();
      e.webhookKinds = d.kinds;
      toast(d.kinds.length ? "The main webhook gets: " + d.kinds.map(kindLabel).join(", ") : "The main webhook gets nothing now");
    } catch { toast("Couldn't save that - see the server log"); }
  });
  const list = $("destList");
  list.innerHTML = "";
  for (const d of destData()) {
    const row = document.createElement("div");
    row.className = "dest-row";
    row.innerHTML = `<div class="what"><b></b><small></small></div>
      <button class="toggle" data-a="test">Test</button><button class="toggle" data-a="edit">Edit</button><button class="toggle del" data-a="del">Remove</button>`;
    row.querySelector("b").textContent = d.name;
    const sm = row.querySelector("small");
    sm.textContent = `${d.masked || ""} · ${d.format === "auto" ? "auto" : d.format} · `;
    const k = document.createElement("span");
    if (d.kinds.length) k.textContent = d.kinds.map(kindLabel).join(", ");
    else { k.textContent = "gets nothing: tick something under Edit"; k.className = "none"; }
    sm.appendChild(k);
    row.querySelector('[data-a="test"]').onclick = () => testDestination(d.id, d.name);
    row.querySelector('[data-a="edit"]').onclick = () => openDestForm(d);
    row.querySelector('[data-a="del"]').onclick = async () => {
      if (!confirm(`Stop sending alerts to ${d.name}?`)) return;
      await saveDestinations(destData().filter(x => x.id !== d.id).map(x => ({ id: x.id, name: x.name, format: x.format, kinds: x.kinds })));
      toast(`${esc(d.name)} removed`);
    };
    list.appendChild(row);
  }
  if (!destData().length) list.innerHTML = `<div class="modal-note">None yet: everything goes to the webhook above.</div>`;
}
function openDestForm(d) {
  destEditing = d || {};
  $("destName").value = d ? d.name : "";
  $("destUrl").value = "";
  $("destUrl").placeholder = d ? `Saved: ${d.masked || "(saved)"} - leave empty to keep it` : "https://ntfy.sh/topic or a Discord webhook URL";
  $("destFormat").value = d ? d.format : "auto";
  kindBoxes($("destKinds"), d ? d.kinds : ALERT_KINDS.map(k => k[0]));
  $("destForm").hidden = false;
  $("destAdd").hidden = true;
  $("destResult").innerHTML = "";
  $("destName").focus();
}
function closeDestForm() { destEditing = null; $("destForm").hidden = true; $("destAdd").hidden = false; }
async function saveDestinations(list) {
  const r = await fetch("/api/settings/destinations", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ destinations: list }) });
  const d = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(d.error || "HTTP " + r.status);
  settingsData.editable.destinations = d.destinations;
  renderDestinations();
  refresh();   // the alerts-off banner may change
  return d.destinations;
}
// A line under the list, the way the webhook above reports: plain, or amber for trouble.
const destSay = (text, bad) => { $("destResult").innerHTML = `<span class="${bad ? "none" : "scan-progress"}">${esc(text)}</span>`; };
async function testDestination(id, name) {
  destSay(`Sending a test to ${name}…`);
  try {
    const r = await fetch(`/api/destinations/${encodeURIComponent(id)}/test`, { method: "POST" });
    const d = await r.json();
    destSay(d.ok ? `Test sent to ${name}. Check it arrived.` : `${name}: ${d.error || "the test didn't go through."}`, !d.ok);
  } catch { destSay("Couldn't reach BAMF.", true); }
}
$("destAdd").onclick = () => openDestForm(null);
$("destCancel").onclick = closeDestForm;
$("destSave").onclick = async () => {
  const d = destEditing || {};
  const entry = { id: d.id || "", name: $("destName").value.trim(), url: $("destUrl").value.trim(), format: $("destFormat").value, kinds: checkedKinds($("destKinds")) };
  if (!d.id && !entry.url) { destSay("Paste the URL alerts should go to.", true); return; }
  const before = new Set(destData().map(x => x.id));
  const others = destData().filter(x => x.id !== d.id).map(x => ({ id: x.id, name: x.name, format: x.format, kinds: x.kinds }));
  const list = d.id ? destData().map(x => x.id === d.id ? entry : { id: x.id, name: x.name, format: x.format, kinds: x.kinds }) : [...others, entry];
  try {
    const saved = await saveDestinations(list);
    closeDestForm();
    const mine = d.id ? saved.find(x => x.id === d.id) : saved.find(x => !before.has(x.id));
    if (mine) await testDestination(mine.id, mine.name);
  } catch (e) { destSay(e.message, true); }
};
// ---- Alert rules, quiet hours, port watch ----
function ruleTargets() {
  const out = [["any", "any device"], ["watched", "watched devices"]];
  for (const t of allTags()) out.push(["tag:" + t, "devices tagged " + t]);
  for (const h of hosts.filter(x => !x.forgotten && !x.ignored).sort((a, b) => nameOrIp(a).localeCompare(nameOrIp(b)))) out.push(["host:" + h.id, nameOrIp(h)]);
  return out;
}
function fillRules() {
  const d = rulesData;
  if (!d) return;
  const list = $("ruleList");
  list.innerHTML = "";
  if (!d.rules.length) list.innerHTML = `<div class="modal-note">No rules yet. Watched devices already alert the moment they drop or return; rules add patience, groups and hours.</div>`;
  for (const r of d.rules) {
    const row = document.createElement("div");
    row.className = "rule-row" + (r.enabled ? "" : " off");
    row.innerHTML = `<button type="button" class="arp-toggle" aria-pressed="${r.enabled}" title="${r.enabled ? "On - click to pause" : "Paused - click to turn on"}"><span class="pill"></span><span class="tstate"></span></button>` +
      `<span class="txt">${esc(r.text)}${r.name ? `<small>${esc(r.name)}</small>` : ""}</span><button type="button" class="toggle" title="Delete this rule">Delete</button>`;
    const tog = row.querySelector(".arp-toggle"); setToggleState(tog, r.enabled);
    tog.onclick = () => saveRules(d.rules.map(x => x.id === r.id ? { ...x, enabled: !x.enabled } : x));
    row.querySelector(".toggle").onclick = () => saveRules(d.rules.filter(x => x.id !== r.id));
    list.appendChild(row);
  }
  const sel = $("ruleTarget");
  const want = sel.value;
  sel.innerHTML = ruleTargets().map(([v, t]) => `<option value="${esc(v)}">${esc(t)}</option>`).join("");
  if ([...sel.options].some(o => o.value === want)) sel.value = want;
  syncRuleForm();
  $("quietFrom").value = d.quiet.from || ""; $("quietTo").value = d.quiet.to || "";
  $("quietDigest").checked = !!d.quiet.digest;
  $("quietStatus").textContent = !d.quiet.from ? "No quiet hours: alerts go out as they happen."
    : `Quiet from ${d.quiet.from} to ${d.quiet.to}${d.quiet.now ? " · quiet now" : ""}${d.quiet.held ? ` · ${d.quiet.held} alert${d.quiet.held === 1 ? "" : "s"} held` : ""}.`;
  const pw = $("setPortWatch");
  setToggleState(pw, !!d.portWatch);
  pw.title = d.portWatch ? "Ports are scanned daily - click to stop" : "Click to scan every known device's ports daily";
}
function syncRuleForm() {
  const k = $("ruleKind").value;
  $("ruleMinutes").hidden = k !== "offline"; $("ruleMinLbl").hidden = k !== "offline";
  $("ruleFrom").hidden = k !== "hours" && k !== "wake"; $("ruleAnd").hidden = k !== "hours"; $("ruleTo").hidden = k !== "hours";
  $("ruleDays").hidden = k !== "wake";
  // Waking "any device" makes no sense; the other targets stay.
  const any = [...$("ruleTarget").options].find(o => o.value === "any");
  if (any) { any.disabled = k === "wake"; if (k === "wake" && $("ruleTarget").value === "any") $("ruleTarget").value = [...$("ruleTarget").options].find(o => !o.disabled)?.value || "any"; }
  if (k === "wake" && $("ruleFrom").value === "22:00") $("ruleFrom").value = "07:00";
}
$("ruleKind").onchange = syncRuleForm;
async function saveRules(rules) {
  try {
    const r = await fetch("/api/settings/rules", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(rules) });
    const d = await r.json();
    if (!r.ok) { showErr("rulesErr", d.error || "Couldn't save."); return; }
    showErr("rulesErr", "");
    rulesData = d; fillRules();
  } catch { showErr("rulesErr", "Couldn't reach BAMF."); }
}
$("ruleAdd").onclick = () => {
  const kind = $("ruleKind").value;
  const rule = { id: "", name: $("ruleName").value.trim(), kind, target: $("ruleTarget").value,
    minutes: Number($("ruleMinutes").value) || 15, from: $("ruleFrom").value, to: kind === "wake" ? $("ruleDays").value : $("ruleTo").value, enabled: true };
  saveRules([...(rulesData ? rulesData.rules : []), rule]).then(() => { $("ruleName").value = ""; toast("Rule added"); });
};
async function saveQuiet(from, to) {
  try {
    const r = await fetch("/api/settings/quiet", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ from, to, digest: $("quietDigest").checked }) });
    const d = await r.json();
    if (!r.ok) { showErr("rulesErr", d.error || "Couldn't save."); return; }
    showErr("rulesErr", "");
    rulesData = d; fillRules();
    toast(from ? `Quiet hours ${from}–${to}` : "Quiet hours cleared");
  } catch { showErr("rulesErr", "Couldn't reach BAMF."); }
}
$("quietSave").onclick = () => saveQuiet($("quietFrom").value, $("quietTo").value);
$("quietClear").onclick = () => saveQuiet("", "");
$("setPortWatch").onclick = async () => {
  const next = !(rulesData && rulesData.portWatch);
  try {
    await fetch("/api/settings/port-watch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }) });
    if (rulesData) rulesData.portWatch = next;
    fillRules();
    toast(next ? "Ports will be scanned daily at 4 am" : "Daily port scan off");
  } catch { toast("Couldn't save that"); }
};

// ---- Scheduled report ----
function fillReport(r) {
  const hourSel = $("reportHour");
  if (!hourSel.options.length) for (let h = 0; h < 24; h++) { const o = document.createElement("option"); o.value = h; o.textContent = `${String(h).padStart(2, "0")}:00`; hourSel.appendChild(o); }
  $("reportSchedule").value = r.schedule || "off";
  $("reportHour").value = String(r.hour ?? 8);
  $("reportDay").value = String(r.day ?? 1);
  $("reportTz").textContent = r.timeZone ? `(${r.timeZone}, the server's clock)` : "";
  syncReportRow();
  $("reportStatus").textContent = (r.schedule || "off") === "off" ? "Off."
    : `Next: ${r.next ? new Date(r.next).toLocaleString([], { weekday: "short", hour: "2-digit", minute: "2-digit", month: "short", day: "numeric" }) : "—"}` +
      (r.lastSent ? ` · last sent ${fmtAgo(r.lastSent)}` : " · never sent yet") +
      (webhookConfigured ? "" : " · no webhook saved, so nothing will go out");
}
function syncReportRow() {
  const sch = $("reportSchedule").value;
  $("reportDayWrap").style.display = sch === "weekly" ? "" : "none";
  $("reportMonthWrap").style.display = sch === "monthly" ? "" : "none";
  $("reportHour").parentElement.style.display = sch === "off" ? "none" : "";
}
$("reportSchedule").onchange = syncReportRow;
$("reportSave").onclick = async () => {
  const out = $("reportResult");
  try {
    const r = await fetch("/api/settings/report", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ schedule: $("reportSchedule").value, hour: Number($("reportHour").value), day: Number($("reportDay").value) }) });
    const d = await r.json();
    if (!r.ok) { out.innerHTML = `<span class="none">${esc(d.error || "Couldn't save.")}</span>`; return; }
    fillReport(d);
    out.innerHTML = "";
    toast(d.schedule === "off" ? "Scheduled report off" : `Report ${d.schedule}, at ${String(d.hour).padStart(2, "0")}:00`);
  } catch { out.innerHTML = `<span class="none">Couldn't reach BAMF.</span>`; }
};
$("reportPreview").onclick = async () => {
  const out = $("reportResult");
  out.innerHTML = `<span class="scan-progress">Composing…</span>`;
  try {
    const d = await (await fetch("/api/reports/preview")).json();
    out.innerHTML = `<div class="report-preview"><b>${esc(d.title)}</b>\n${esc(d.text)}</div>`;
  } catch { out.innerHTML = `<span class="none">Couldn't reach BAMF.</span>`; }
};
$("reportSend").onclick = async () => {
  const out = $("reportResult");
  out.innerHTML = `<span class="scan-progress">Sending…</span>`;
  try {
    const r = await fetch("/api/reports/send", { method: "POST" });
    const d = await r.json();
    out.innerHTML = d.ok ? `<span class="scan-progress">Sent.</span><div class="report-preview"><b>${esc(d.title)}</b>\n${esc(d.text)}</div>`
      : `<span class="none">${esc(d.error || "Not sent.")}</span>` + (d.text ? `<div class="report-preview"><b>${esc(d.title)}</b>\n${esc(d.text)}</div>` : "");
    if (d.ok) loadSettings();
  } catch { out.innerHTML = `<span class="none">Couldn't reach BAMF.</span>`; }
};

// ---- Layout export and import ----
$("layoutExport").onclick = async () => {
  try {
    const r = await fetch("/api/layout");
    if (!r.ok) throw new Error("HTTP " + r.status);
    const blob = await r.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `bamf-layout-${new Date().toISOString().slice(0, 10)}.json`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 2000);
    toast("Layout exported");
  } catch { toast("Couldn't export the layout"); }
};
// Fetched rather than linked, so a refusal shows as a message instead of
// being saved to disk as a file called bamf.db.
$("backupDownload").onclick = async () => {
  const b = $("backupDownload"), out = $("backupResult");
  b.disabled = true;
  out.textContent = "Taking a copy\u2026";
  try {
    const r = await fetch("/api/backup");
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      out.textContent = d.error || `Couldn't take a backup (HTTP ${r.status}).`;
      return;
    }
    const name = ((r.headers.get("Content-Disposition") || "").match(/filename="?([^";]+)"?/) || [])[1] || "bamf-backup.db";
    const blob = await r.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 10000);
    const mb = blob.size / 1048576;
    out.textContent = `Saved ${name} \u00b7 ${mb >= 1 ? mb.toFixed(1) + " MB" : Math.max(1, Math.round(blob.size / 1024)) + " kB"}`;
  } catch { out.textContent = "Couldn't reach BAMF."; }
  finally { b.disabled = false; }
};

$("backupRestore").onclick = () => $("backupFile").click();
// ---- Tidy up old devices ----
let tidyCache = null;
function renderTidy() {
  const d = tidyCache;
  if (!d) return;
  const on = $("tidyEnabled");
  on.classList.toggle("on", d.enabled);
  on.setAttribute("aria-pressed", d.enabled ? "true" : "false");
  $("tidyDays").value = d.days;
  const last = d.last ? ` Last tidy ${fmtAgo(d.last.at)}: ${d.last.count} device${d.last.count === 1 ? "" : "s"} forgotten.` : "";
  $("tidyStatus").textContent = (d.would
    ? `${d.would} device${d.would === 1 ? "" : "s"} would be tidied now (${d.wouldNames.slice(0, 4).join(", ")}${d.would > 4 ? ", \u2026" : ""}).`
    : "Nothing to tidy: no device untouched by you has been gone that long.") + last;
  $("tidyRun").disabled = !d.would;
}
async function loadTidy() {
  try { const r = await fetch("/api/settings/tidy"); if (r.ok) { tidyCache = await r.json(); renderTidy(); } } catch { /* leave it */ }
}
async function saveTidy(patch) {
  const d = tidyCache || {};
  const out = $("tidyResult");
  try {
    const r = await fetch("/api/settings/tidy", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: d.enabled, days: Number($("tidyDays").value) || d.days, ...patch }) });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) { out.textContent = j.error || `Couldn't save (HTTP ${r.status}).`; out.className = "set-result err"; return false; }
    tidyCache = j; renderTidy(); out.textContent = ""; out.className = "set-result";
    return true;
  } catch { out.textContent = "Couldn't reach BAMF."; return false; }
}
$("tidyEnabled").onclick = async () => {
  const next = !(tidyCache && tidyCache.enabled);
  if (await saveTidy({ enabled: next })) toast(next ? "Old devices will be tidied up every day" : "Old devices are left alone");
};
$("tidySave").onclick = async () => { if (await saveTidy({})) toast("Saved"); };
$("tidyRun").onclick = async () => {
  if (!await saveTidy({})) return;
  const n = tidyCache.would;
  if (!n || !confirm(`Forget ${n} device${n === 1 ? "" : "s"} that ${n === 1 ? "has" : "have"} been gone more than ${tidyCache.days} days?\n\n${tidyCache.wouldNames.slice(0, 8).join("\n")}${n > 8 ? "\n\u2026" : ""}\n\nThey go to the Forgotten tab, where each can be brought back.`)) return;
  try {
    const r = await fetch("/api/tidy/run", { method: "POST" });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) { toast("Couldn't tidy up"); return; }
    toast(`${j.count} device${j.count === 1 ? "" : "s"} forgotten`);
    await refresh();
    loadTidy();
  } catch { toast("Couldn't reach BAMF"); }
};

$("settingsExport").onclick = () => { location.href = "/api/settings/export"; };
$("settingsImport").onclick = () => $("settingsFile").click();
$("settingsFile").onchange = async () => {
  const f = $("settingsFile").files[0];
  $("settingsFile").value = "";
  if (!f) return;
  let body;
  try { body = JSON.parse(await f.text()); } catch { toast("That isn't a JSON file"); return; }
  if (!confirm(`Apply the settings in ${f.name}?

They replace the same settings here, and the alert rules and other BAMF servers if the file has them. Passwords, webhook URLs, this site's networks and the devices aren't touched.`)) return;
  try {
    const r = await fetch("/api/settings/import", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || `Couldn't import (HTTP ${r.status})`)); return; }
    const parts = [`${d.applied} setting${d.applied === 1 ? "" : "s"}`];
    if (d.rules) parts.push(`${d.rules} alert rule${d.rules === 1 ? "" : "s"}`);
    if (d.remotes) parts.push(`${d.remotes} other server${d.remotes === 1 ? "" : "s"}`);
    toast(`Settings imported: ${parts.join(", ")}` +
      (d.rulesSkipped ? `. ${d.rulesSkipped} rule${d.rulesSkipped === 1 ? " was" : "s were"} about one device and left out.` : ""));
    await refresh();
    loadSettings();
  } catch { toast("Couldn't reach BAMF"); }
};

// ---- Settings → Alerts: unusual activity ----
async function loadUnusualSetting() {
  try {
    const r = await fetch("/api/unusual");
    if (!r.ok) return;
    const d = await r.json();
    setToggleState($("setUnusual"), d.enabled);
    $("unusualStatus").textContent = !d.enabled ? "Off."
      : `On, watching ${d.watching} device${d.watching === 1 ? "" : "s"}` + (d.learning ? `; ${d.learning} still learning.` : ".");
  } catch { /* the next look shows it */ }
}
$("setUnusual").onclick = async () => {
  const on = !$("setUnusual").classList.contains("on");
  try {
    const r = await fetch("/api/settings/unusual", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: on }) });
    if (!r.ok) throw new Error();
    toast(on ? "BAMF will say when something isn't normal" : "Unusual activity off");
    loadUnusualSetting();
  } catch { toast("Couldn't save that"); }
};

// ---- Settings → System: nightly backups ----
function renderBackup(b) {
  if (!b) return;
  setToggleState($("backupNightly"), b.enabled);
  $("backupHour").value = String(b.hour);
  if (document.activeElement !== $("backupKeep")) $("backupKeep").value = b.keep;
  if (document.activeElement !== $("backupCopyTo")) $("backupCopyTo").value = b.copyTo || "";
  $("backupCopySource").innerHTML = b.copyToSource === "file"
    ? `Set in <span class="mono">appsettings.json</span>; saving here replaces it.`
    : b.copyToSource === "settings" ? `<a href="#" id="backupCopyReset">Use appsettings.json's instead</a>` : "";
  const reset = $("backupCopyReset");
  if (reset) reset.onclick = e => { e.preventDefault(); resetCopyTo(); };
  const st = $("backupStatus"), last = b.last;
  const kept = b.count ? ` ${b.count} kept, ${fmtBytes(b.totalBytes)} in all.` : "";
  const copy = b.copyTo ? ` Also copied to <span class="mono">${esc(b.copyTo)}</span>.` : "";
  st.classList.toggle("bad", !!(last && last.error));
  st.innerHTML = last && last.error ? `<b>The last backup failed</b> (${esc(fmtAgo(last.at))}): ${esc(last.error)}`
    : last && last.file ? `Last: <span class="mono">${esc(last.file)}</span>, ${fmtBytes(last.size)}, ${esc(fmtAgo(last.at))}.${kept}${copy}`
    : (b.enabled ? "None yet. The first is taken tonight." : "Off.") + kept + copy;
}
async function saveBackup(body, done) {
  const out = $("backupNowResult");
  try {
    const r = await fetch("/api/settings/backup", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(d.error || "HTTP " + r.status);
    renderBackup(d);
    out.textContent = done; out.className = "set-result ok";
  } catch (err) { out.textContent = err.message; out.className = "set-result err"; }
}
$("backupNightly").onclick = () => {
  const on = !$("backupNightly").classList.contains("on");
  saveBackup({ enabled: on }, on ? "Nightly backups on." : "Nightly backups off.");
};
$("backupHour").onchange = () => saveBackup({ hour: Number($("backupHour").value) }, "Saved.");
$("backupSave").onclick = () => saveBackup({ keep: Number($("backupKeep").value) }, "Saved.");
$("backupCopySave").onclick = () => {
  const v = $("backupCopyTo").value.trim();
  $("backupNowResult").textContent = v ? "Checking BAMF can write there\u2026" : "";
  saveBackup({ copyTo: v }, v ? "Saved. Each night's backup is copied there too." : "No second copy.");
};
async function resetCopyTo() {
  try {
    const r = await fetch("/api/settings/backup/copyto/reset", { method: "POST" });
    if (r.ok) { renderBackup(await r.json()); $("backupNowResult").textContent = "Back to appsettings.json's."; $("backupNowResult").className = "set-result ok"; }
  } catch { /* the next look shows it */ }
}
// The backups kept on the server, each with a Restore button.
async function loadSavedBackups() {
  const box = $("backupSaved");
  try {
    const r = await fetch("/api/backup/saved");
    if (!r.ok) { box.textContent = "Couldn't read the list."; return; }
    const list = await r.json();
    box.innerHTML = "";
    if (!list.length) { box.innerHTML = `<div class="watch-note">None yet.</div>`; return; }
    for (const b of list) {
      const row = document.createElement("div");
      row.className = "bk-row";
      const when = new Date(b.at).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
      row.innerHTML = `<span class="bk-what"><span class="bk-when">${esc(when)}</span><span class="bk-kind">${esc(b.kind)} · ${fmtBytes(b.size)}</span></span>`;
      const go = document.createElement("button");
      go.type = "button";
      go.className = "toggle";
      go.textContent = "Restore";
      go.title = b.name;
      go.onclick = () => restoreSaved(b, when);
      row.appendChild(go);
      box.appendChild(row);
    }
  } catch { box.textContent = "Couldn't reach BAMF."; }
}
async function restoreSaved(b, when) {
  if (!confirm(`Put back the backup from ${when} (${b.name})?\n\nEvery device and its history, the names, notes and tags, the Map, the floor plans, alert rules and the settings saved here become what they were then. The database it replaces is kept first, in this same list, so this can be undone.`)) return;
  const out = $("backupResult");
  out.textContent = "Restoring\u2026";
  try {
    const r = await fetch("/api/backup/saved/restore", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: b.name }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { out.textContent = d.error || `Couldn't restore it (HTTP ${r.status}).`; return; }
    out.textContent = `Restored. The database it replaced is kept as ${d.kept}. Reloading\u2026`;
    setTimeout(() => location.reload(), 1500);
  } catch { out.textContent = "Couldn't reach BAMF."; }
}
$("backupSavedBox").ontoggle = () => { if ($("backupSavedBox").open) loadSavedBackups(); };
$("backupNow").onclick = async () => {
  const b = $("backupNow"), out = $("backupNowResult");
  b.disabled = true; out.textContent = "Backing up…"; out.className = "set-result";
  try {
    const r = await fetch("/api/settings/backup/run", { method: "POST" });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(d.error || "HTTP " + r.status);
    renderBackup(d);
    if ($("backupSavedBox").open) loadSavedBackups();
    out.textContent = "Done."; out.className = "set-result ok";
  } catch (err) { out.textContent = err.message; out.className = "set-result err"; }
  finally { b.disabled = false; }
};
$("backupFile").onchange = async () => {
  const f = $("backupFile").files[0];
  $("backupFile").value = "";
  if (!f) return;
  if (!confirm(`Replace everything BAMF knows with ${f.name}?\n\nEvery device and its history, the names, notes and tags, the Map, the floor plans, alert rules and the settings saved here become what's in the backup. The database it replaces is kept on the server first, in backups beside it.`)) return;
  const out = $("backupResult");
  out.textContent = "Restoring\u2026";
  try {
    const r = await fetch("/api/backup/restore", { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: f });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { out.textContent = d.error || `Couldn't restore it (HTTP ${r.status}).`; return; }
    out.textContent = `Restored. The database it replaced is kept as ${d.kept}. Reloading\u2026`;
    setTimeout(() => location.reload(), 1500);
  } catch { out.textContent = "Couldn't reach BAMF."; }
};

$("layoutImport").onclick = () => $("layoutFile").click();
$("layoutFile").onchange = async () => {
  const f = $("layoutFile").files[0];
  $("layoutFile").value = "";
  if (!f) return;
  let body;
  try { body = JSON.parse(await f.text()); } catch { toast("That isn't a JSON file"); return; }
  const n = (body.switches || []).length, m = (body.devices || []).length;
  if (!confirm(`Replace the recorded layout with this file (${n} switch${n === 1 ? "" : "es"}, ${m} device${m === 1 ? "" : "s"})? Everything recorded here is replaced. The devices themselves aren't affected.`)) return;
  try {
    const r = await fetch("/api/layout", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const d = await r.json();
    if (!r.ok) { toast(esc(d.error || "Couldn't import")); return; }
    toast(`Layout imported: ${d.switches} switch${d.switches === 1 ? "" : "es"}, ${d.devices} device${d.devices === 1 ? "" : "s"}` +
      (d.skipped.length ? `. ${d.skipped.length} device${d.skipped.length === 1 ? " isn't" : "s aren't"} known here yet: import again once BAMF has seen ${d.skipped.length === 1 ? "it" : "them"}.` : ""));
    await refresh();
    loadSettings();
  } catch { toast("Couldn't reach BAMF"); }
};

// Tools ▾ → Notifications… used to open a dialog; it now goes to the card, so
// the old habit still lands somewhere sensible.
// ---- The alerts-off banner ----
// Nothing on the dashboard said when alerts weren't going anywhere: the only
// sign was a line inside Settings. A viewer can't save a webhook, so they
// aren't told about something they can't fix.
// The Pause alerts entry in Tools, and the bar that says alerts are paused.
function renderPause() {
  const open = $("pauseOpen");
  if (open) open.hidden = !pauseEnabled || role === "viewer";
  const bar = $("pauseBar");
  if (!bar) return;
  const until = pauseEnabled && alertsPausedUntil && new Date(alertsPausedUntil) > new Date() ? alertsPausedUntil : null;
  bar.hidden = !until;
  if (until) $("pauseUntil").textContent = snoozeClock(until);
  $("pauseResume").hidden = role === "viewer";
  const box = $("pauseEnabled");
  if (box) box.checked = pauseEnabled;
}
$("restartAlertEnabled").onchange = async () => {
  const enabled = $("restartAlertEnabled").checked;
  try {
    const r = await fetch("/api/settings/restart-alert", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    restartAlertEnabled = enabled;
    toast(enabled ? "BAMF will say when it starts again after stopping unexpectedly" : "BAMF won't say when it restarts");
  } catch { $("restartAlertEnabled").checked = !enabled; toast("Couldn't save that"); }
};
// The low disk space alert: its switch and the percentage, saved together.
async function saveDiskAlert() {
  const enabled = $("diskAlertEnabled").checked, percent = Number($("diskAlertPercent").value);
  try {
    const r = await fetch("/api/settings/disk-alert", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled, percent: percent || null }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || "Couldn't save that")); loadDiskAlertSettings(); return; }
    $("diskAlertPercent").value = d.percent;
    toast(enabled ? `BAMF will warn when its disk has under ${d.percent}% free` : "The low disk space alert is off");
    loadHealth();
  } catch { toast("Couldn't reach BAMF"); }
}
async function loadDiskAlertSettings() {
  try {
    const r = await fetch("/api/health");
    if (!r.ok) return;
    const d = await r.json();
    $("diskAlertEnabled").checked = d.alert.enabled;
    $("diskAlertPercent").value = d.alert.percent;
  } catch { /* leave it */ }
}
$("diskAlertEnabled").onchange = saveDiskAlert;
$("diskAlertPercent").onchange = saveDiskAlert;
// How alerts are sent: retried, grouped under a switch, and calmed for a flapping device.
async function loadAlertBehaviour() {
  try {
    const r = await fetch("/api/settings/alert-behaviour");
    if (!r.ok) return;
    const d = await r.json();
    $("behRetry").checked = d.retry; $("behCascade").checked = d.cascade; $("behFlapAlert").checked = d.flapAlert;
    $("behFlapHold").checked = d.flapHold; $("behFlapDrops").value = d.flapDrops;
  } catch { /* leave it */ }
}
async function saveAlertBehaviour(patch, what) {
  try {
    const r = await fetch("/api/settings/alert-behaviour", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || "Couldn't save that")); loadAlertBehaviour(); return; }
    $("behRetry").checked = d.retry; $("behCascade").checked = d.cascade; $("behFlapAlert").checked = d.flapAlert;
    $("behFlapHold").checked = d.flapHold; $("behFlapDrops").value = d.flapDrops;
    toast(what);
  } catch { toast("Couldn't reach BAMF"); }
}
$("behRetry").onchange = () => saveAlertBehaviour({ retry: $("behRetry").checked }, $("behRetry").checked ? "Failed alerts will be tried again" : "Failed alerts won't be tried again");
$("behCascade").onchange = () => saveAlertBehaviour({ cascade: $("behCascade").checked }, $("behCascade").checked ? "Devices behind a switch that went down share one alert" : "Each device alerts on its own");
$("behFlapAlert").onchange = () => saveAlertBehaviour({ flapAlert: $("behFlapAlert").checked, flapDrops: Number($("behFlapDrops").value) || null }, $("behFlapAlert").checked ? "BAMF will say when a device keeps dropping" : "Flapping devices aren't called out");
$("behFlapDrops").onchange = () => saveAlertBehaviour({ flapDrops: Number($("behFlapDrops").value) || null }, "Saved");
$("behFlapHold").onchange = () => saveAlertBehaviour({ flapHold: $("behFlapHold").checked }, $("behFlapHold").checked ? "A flapping device's own alerts are held back" : "A flapping device's alerts are sent as usual");
$("pauseEnabled").onchange = async () => {
  const enabled = $("pauseEnabled").checked;
  try {
    const r = await fetch("/api/settings/pause", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    pauseEnabled = enabled;
    if (!enabled) alertsPausedUntil = null;
    renderPause();
    toast(enabled ? "Pause alerts is in the Tools menu" : "Pause alerts is off");
  } catch { $("pauseEnabled").checked = !enabled; toast("Couldn't save that"); }
};
function renderAlertsOff() {
  $("alertsOff").hidden = alertsConfigured || alertsNudgeOff || role === "viewer";
}
$("alertsOffSetup").onclick = () => {
  showView("settings/alerts");
  setTimeout(() => {
    // Straight to the phone setup: for most people that's the whole answer.
    if ($("phonePanel").hidden) $("phoneSetup").click();
    $("phoneSetup").scrollIntoView({ behavior: "smooth", block: "center" });
  }, 150);
};
$("alertsOffDismiss").onclick = async () => {
  alertsNudgeOff = true;
  renderAlertsOff();
  try {
    const r = await fetch("/api/settings/alertnudge", { method: "POST",
      headers: { "Content-Type": "application/json" }, body: JSON.stringify({ off: true }) });
    if (!r.ok) throw new Error();
    toast("Won't remind you. Settings \u2192 Notifications is where alerts are set up.");
  } catch {
    alertsNudgeOff = false;
    renderAlertsOff();
    toast("Couldn't reach BAMF");
  }
};

$("notifyUrl").onkeydown = e => { if (e.key === "Enter") { e.preventDefault(); $("notifySave").click(); } };

// Notes dialog. Same shape as the notifications one: backdrop click and
// Cancel both close without saving, Clear saves an empty note.
$("noteSave").onclick = () => saveNoteDialog($("noteText").value);
$("noteClear").onclick = () => saveNoteDialog("");
$("tagsCancel").onclick = closeTagsDialog;
$("tagsSave").onclick = saveTagsDialog;
$("tagsText").addEventListener("input", renderTagPick);
$("tagsText").addEventListener("keydown", e => { if (e.key === "Enter") { e.preventDefault(); saveTagsDialog(); } });
$("tagsModal").onclick = e => { if (e.target.id === "tagsModal") closeTagsDialog(); };
$("homeGroup").onchange = () => render();
$("cardsClose").onclick = closeCardsDialog;
$("cardsAdd").onclick = () => {
  const id = Number($("cardsPick").value);
  if (!id) { showErr("cardsErr", "Pick the device to combine into this one."); return; }
  if (cardsHost) combineCard(id, cardsHost.id);
};
$("cardsModal").onclick = e => { if (e.target.id === "cardsModal") closeCardsDialog(); };
$("gwCancel").onclick = closeGatewayDialog;
$("gwSave").onclick = saveGatewayDialog;
$("gwModal").onclick = e => { if (e.target.id === "gwModal") closeGatewayDialog(); };
$("noteCancel").onclick = closeNoteDialog;
$("noteModal").onclick = e => { if (e.target.id === "noteModal") closeNoteDialog(); };
$("noteText").onkeydown = e => {
  if (e.key === "Escape") { e.preventDefault(); closeNoteDialog(); }
  // Enter inserts a newline as usual; Ctrl/Cmd+Enter saves.
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); $("noteSave").click(); }
};

// Switch layout dialogs: same shape as the notes one.
$("plugSwitch").onchange = () => syncPlugPort(0);
$("plugSave").onclick = savePlugDialog;
$("plugCancel").onclick = closePlugDialog;
$("plugNewSwitch").onclick = () => { const h = plugHost; closePlugDialog(); openSwitchDialog(null, null, h); };
$("plugModal").onclick = e => { if (e.target.id === "plugModal") closePlugDialog(); };
$("swHost").onchange = () => {
  // Picking the switch's own device names it after that device, unless a name was typed.
  const h = hosts.find(x => x.id === Number($("swHost").value));
  if (h && !$("swName").value.trim()) $("swName").value = nameOrIp(h);
  syncSwitchDialog(0);
};
$("swUp").onchange = () => syncSwitchDialog(0);
$("swKind").onchange = () => {
  const kind = $("swKind").value;
  if (!swEditing) {
    $("swTitle").textContent = `Add a ${kindNoun(kind)}`;
    // Swap the starting port count, unless one was already typed in.
    if (Object.values(KIND_PORTS).includes(Number($("swPorts").value))) $("swPorts").value = KIND_PORTS[kind];
    if (kind === "router") pickGatewayForSwitch();
  }
  syncSwitchDialog(0);
};
$("swSave").onclick = saveSwitchDialog;
$("swAddSsid").onclick = () => { swThenSsid = true; saveSwitchDialog(); };
$("swDelete").onclick = deleteSwitchDialog;
$("swCancel").onclick = () => {
  const back = closeSwitchDialog();
  if (back) { const h = hosts.find(x => x.id === back.id); if (h) openPlugDialog(h); }
};
$("swModal").onclick = e => { if (e.target.id === "swModal") closeSwitchDialog(); };
$("swName").onkeydown = e => { if (e.key === "Enter") { e.preventDefault(); saveSwitchDialog(); } };
$("swAdd").onclick = () => openSwitchDialog(null);
$("plugFind").onclick = () => { const h = plugHost; closePlugDialog(); if (h) jumpToHost(h.id); };
$("plugBlinkBtn").onclick = () => { if (plugHost) startBlink(plugHost, "plugBlink"); };
$("plugType").onclick = () => { const h = plugHost; closePlugDialog(); if (h) openTypeDialog(h); };
$("typeCancel").onclick = closeTypeDialog;
$("typeModal").onclick = e => { if (e.target.id === "typeModal") closeTypeDialog(); };
$("blinkBarStop").onclick = stopBlink;
$("portsFindBtn").onclick = () => {
  const h = hosts.find(x => x.id === Number($("portsFindHost").value));
  if (h) startBlink(h, "portsBlink");
};
$("portsSave").onclick = savePortsDialog;
$("portsCancel").onclick = closePortsDialog;
$("portsEditSwitch").onclick = () => { const sw = portsSwitch; closePortsDialog(); if (sw) openSwitchDialog(sw); };
$("portsModal").onclick = e => { if (e.target.id === "portsModal") closePortsDialog(); };

// One Escape to close whatever is open, innermost first: a dialog, then an
// open ⋯ menu, then an expanded History panel. Bound on the document so it
// works wherever focus happens to be, not only inside the note field.
document.addEventListener("keydown", e => {
  // "/" jumps to the search box from anywhere that isn't already a text field.
  if (e.key === "/" && !e.ctrlKey && !e.metaKey && !e.altKey) {
    const t = e.target;
    const typing = t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.tagName === "SELECT" || t.isContentEditable);
    if (!typing) { e.preventDefault(); $("search").focus(); $("search").select(); }
    return;
  }
  if (e.key !== "Escape") return;
  if (document.activeElement === $("search")) {
    if ($("search").value) { $("search").value = ""; query = ""; render(); }
    $("search").blur();
    return;
  }
  if (!$("freeModal").hidden) { $("freeModal").hidden = true; return; }
  if (!$("newsModal").hidden) { closeNews(); return; }
  if (floorArmed !== null || floorArmedGroup !== null) { floorArmed = null; floorArmedGroup = null; renderFloor(); return; }
  if (!$("tagsModal").hidden) { closeTagsDialog(); return; }
  if (!$("cardsModal").hidden) { closeCardsDialog(); return; }
  if (!$("gwModal").hidden) { closeGatewayDialog(); return; }
  if (!$("bulkModal").hidden) { closeBulkModal(); return; }
  if (!$("toolModal").hidden) { closeToolDialog(); return; }
  if (!$("snoozeModal").hidden) { closeSnoozeDialog(); return; }
  if (!$("snmpModal").hidden) { closeSnmpDialog(); return; }
  if (!$("kindModal").hidden) { closeKindDialog(); return; }
  if (!$("noteModal").hidden) { closeNoteDialog(); return; }
  if (!$("swModal").hidden) { closeSwitchDialog(); return; }
  if (!$("plugModal").hidden) { closePlugDialog(); return; }
  if (!$("portsModal").hidden) { closePortsDialog(); return; }
  if (!$("typeModal").hidden) { closeTypeDialog(); return; }
  if (!$("secretModal").hidden) { closeSecret(); return; }
  if (!$("scanModal").hidden) { $("scanModal").hidden = true; return; }
  if (blink) { stopBlink(); return; }
  if (mapExpanded) { toggleMapExpanded(mapExpanded); return; }
  const openMenu = document.querySelector(".row-menu.show");
  if (openMenu) { openMenu.classList.remove("show"); return; }
  if (toolsOpen) { toolsOpen = false; $("toolsMenu").classList.remove("show"); return; }
  if (scanOpen) { closeScanMenu(); $("scanBtn").focus(); return; }
  if (guessFilter !== null) { guessFilter = null; render(); return; }
  if (expandedId !== null) { expandedId = null; render(); }
});

// Copy that also works on a plain http:// LAN address, where the browser
// leaves navigator.clipboard undefined because the page isn't a secure context.
async function copyText(text) {
  try { await navigator.clipboard.writeText(text); return true; } catch {}
  try {
    const t = document.createElement("textarea");
    t.value = text;
    t.style.cssText = "position:fixed;top:-1000px;opacity:0";
    document.body.appendChild(t);
    t.select();
    const ok = document.execCommand("copy");
    t.remove();
    return ok;
  } catch { return false; }
}

// ---- Phone alerts without Discord: ntfy, with the topic picked for you ----
// The topic name is the only thing keeping the channel private, so it's 12
// random characters from an alphabet with no lookalikes to mistype.
function newPhoneTopic() {
  const n = new Uint8Array(12);
  if (window.crypto && crypto.getRandomValues) crypto.getRandomValues(n);
  else for (let i = 0; i < n.length; i++) n[i] = Math.floor(Math.random() * 256);
  const ALPHA = "abcdefghijkmnpqrstuvwxyz23456789";
  return "bamf-" + [...n].map(v => ALPHA[v % ALPHA.length]).join("");
}
function phoneUrl() { return "https://ntfy.sh/" + $("phoneTopic").textContent; }

$("phoneSetup").onclick = () => {
  const panel = $("phonePanel");
  panel.hidden = !panel.hidden;
  $("phoneSetup").setAttribute("aria-expanded", String(!panel.hidden));
  if (!panel.hidden && !$("phoneTopic").textContent) $("phoneTopic").textContent = newPhoneTopic();
};
$("phoneNew").onclick = () => { $("phoneTopic").textContent = newPhoneTopic(); };
$("phoneCopy").onclick = async () => {
  toast(await copyText(phoneUrl()) ? "Copied " + phoneUrl() : phoneUrl());
};
$("phoneUse").onclick = () => {
  $("notifyUrl").value = phoneUrl();
  $("notifyFormat").value = "ntfy";
  $("notifySave").click();
};

async function saveWebhook(url) {
  const r = await fetch("/api/settings/webhook", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ url, format: $("notifyFormat").value }),
  });
  return { ok: r.ok, data: await r.json() };
}

$("notifySave").onclick = async () => {
  const url = $("notifyUrl").value.trim();
  const out = $("notifyResult");
  if (!url) { out.innerHTML = '<span class="scan-progress">Paste a webhook URL first.</span>'; return; }
  out.innerHTML = '<span class="scan-progress">Saving…</span>';
  try {
    const { ok, data } = await saveWebhook(url);
    if (!ok) { out.innerHTML = `<span class="none">${esc(data.error || "Could not save that URL.")}</span>`; return; }

    out.innerHTML = '<span class="scan-progress">Saved — sending a test…</span>';
    const t = await fetch("/api/webhook/test", { method: "POST" });
    const td = await t.json();
    if (td.ok) {
      out.innerHTML = `<span class="scan-progress">Test sent${data.discord ? " — check your Discord channel" : ""}.</span>`
        + (data.insecure ? '<div class="none">Note: this is an http:// URL, so alerts travel in plaintext.</div>' : "");
      toast("Notifications on");
    } else {
      out.innerHTML = `<span class="none">Saved, but the test failed: ${esc(td.error || "unknown error")}</span>`;
    }
    await refresh();
    $("notifyCurrent").textContent = "Currently saved: " + (webhookMasked || "(saved)");
    $("notifyClear").style.display = "";
  } catch { out.innerHTML = '<span class="none">Could not save — see server logs.</span>'; }
};

$("notifyClear").onclick = async () => {
  try {
    await saveWebhook("");
    await refresh();
    $("notifyUrl").value = "";
    $("notifyResult").innerHTML = "";
    fillNotify();
    toast("Webhook removed — notifications off");
  } catch { toast("Could not remove the webhook"); }
};

// Opens a new GitHub issue with the version details already filled in, so a
// report arrives with the one fact that's always needed and always forgotten.
// The Feedback address, shared by the header link and Tools > Feedback: a new
// issue on this build's own tracker (it follows Bamf:UpdateRepo, so a fork's
// reports go to the fork), pre-filled with a skeleton, the version and build
// stamp, and the browser. Deliberately carries nothing about the network being
// monitored - no device names, addresses or counts.
function feedbackHref() {
  const base = repoUrl || "https://github.com/rhc52980/BAMF_Network_Monitor";
  const body = [
    "**What happened?**", "", "",
    "**What did you expect instead?**", "", "",
    "**Steps to reproduce**", "", "",
    "---",
    `BAMF ${appVersion || "?"}${buildDate ? ` (built ${buildDate} UTC)` : ""}`,
    navigator.userAgent,
  ].join("\n");
  return `${base}/issues/new?body=${encodeURIComponent(body)}`;
}
$("feedback").onclick = () => window.open(feedbackHref(), "_blank", "noopener");

// Settings → Alerts → Test: the saved main webhook, without pasting it again.
$("notifyTest").onclick = async () => {
  const btn = $("notifyTest");
  btn.disabled = true;
  btn.textContent = "Sending\u2026";
  try {
    const r = await fetch("/api/webhook/test", { method: "POST" });
    const data = await r.json();
    toast(data.ok ? "Test sent to the main webhook" : "Webhook test failed: " + (data.error || "unknown error"));
  } catch (e) {
    toast("Webhook test failed \u2014 see server logs");
  }
  btn.disabled = false;
  btn.textContent = "Test";
};

$("exportCsv").onclick = () => {
  const rows = applySort(visible());
  const cols = ["name","dnsHostname","ip","mac","vendor","deviceGuess","deviceType","mapIcon","network","online","known","ignored","firstSeen","lastSeen","switch","switchPort","portLocation"];
  const cell = v => {
    v = String(v ?? "");
    return /[",\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v;
  };
  const lines = [cols.join(",")];
  for (const h of rows) {
    lines.push([
      dispName(h), h.hostname === "\u2014" ? "" : h.hostname, h.ip, h.mac, h.vendor,
      h.osGuess || "", h.typeName || "", h.deviceType ? typeLabel(h.deviceType) : "", h.subnet, h.online, h.known, h.ignored, h.firstSeen, h.lastSeen,
      switches.find(s => s.id === h.switchId)?.name || "", h.switchId && h.switchPort ? h.switchPort : "",
      h.switchId && h.switchPort ? portLabel(switches.find(s => s.id === h.switchId), h.switchPort) : ""
    ].map(cell).join(","));
  }
  const blob = new Blob([lines.join("\r\n")], { type: "text/csv" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = "bamf-hosts-" + new Date().toISOString().slice(0, 10) + ".csv";
  a.click();
  URL.revokeObjectURL(a.href);
};

document.addEventListener("click", (e) => {
  if (!e.target.closest(".more-wrap"))
    document.querySelectorAll(".row-menu.show").forEach(m => m.classList.remove("show"));
});

// Row menus are position: fixed so the table can't clip them. Open below the
// ⋯ button, right-aligned to it; flip above when there's no room below.
function placeRowMenu(menu, btn) {
  const r = btn.getBoundingClientRect();
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  const vw = document.documentElement.clientWidth, vh = window.innerHeight, pad = 8;
  let left = r.right - mw;
  if (left < pad) left = r.left;
  left = Math.max(pad, Math.min(left, vw - mw - pad));
  let top = r.bottom + 4;
  if (top + mh > vh - pad) {
    const above = r.top - 4 - mh;
    top = above >= pad ? above : Math.max(pad, vh - mh - pad);
  }
  menu.style.left = left + "px";
  menu.style.top = top + "px";
}
// A fixed menu would drift off its row, so close it when anything scrolls
// (capture catches the table's sideways scroll too) or the window resizes.
const closeRowMenus = (e) => {
  if (e && e.target instanceof Element && e.target.closest(".row-menu")) return;
  document.querySelectorAll(".row-menu.show").forEach(m => m.classList.remove("show"));
};
window.addEventListener("scroll", closeRowMenus, true);
window.addEventListener("resize", closeRowMenus);

document.querySelectorAll(".stat.clickable").forEach(s => s.onclick = () => {
  filter = s.dataset.jump;
  document.querySelectorAll(".tab").forEach(x => x.classList.toggle("active", x.dataset.filter === filter));
  showView("devices");
});

let toolsOpen = false;
$("toolsBtn").onclick = (e) => {
  e.stopPropagation();
  if (scanOpen) closeScanMenu();
  toolsOpen = !toolsOpen;
  $("toolsMenu").classList.toggle("show", toolsOpen);
};
document.addEventListener("click", (e) => {
  if (toolsOpen && !e.target.closest(".tools-menu-wrap")) {
    toolsOpen = false;
    $("toolsMenu").classList.remove("show");
  }
});

// ---------- Settings ----------
// Loaded on demand rather than folded into the 10s poll: it is read rarely, and
// refreshing it under someone mid-edit would overwrite what they were typing.
let settingsData = null;

function setMsg(text, kind) {
  for (const id of ["setResult", "setResultSys"]) {
    const el = $(id);
    el.textContent = text;
    el.className = "set-result" + (kind ? " " + kind : "");
  }
}

async function loadSettings() {
  setMsg("");
  try {
    const r = await fetch("/api/settings");
    if (!r.ok) throw new Error("HTTP " + r.status);
    settingsData = await r.json();
  } catch (e) {
    setMsg("Couldn't load settings: " + e.message, "err");
    return;
  }
  const e = settingsData.editable, ro = settingsData.readOnly;
  loadAlertBehaviour();
  loadTidy();
  loadHeartbeat();
  loadDiskAlertSettings();
  renderHttps(e.https);
  $("setInterval").value = e.scanIntervalSeconds;
  $("setInterval").min = settingsData.minIntervalSeconds;
  $("setConcurrency").value = e.pingConcurrency;
  $("setRetention").value = e.historyRetentionDays;
  $("setMisses").value = e.offlineAfterMissedScans;
  const mdnsBtn = $("setMdns");
  mdnsBtn.classList.toggle("on", !!e.mdnsListen);
  mdnsBtn.setAttribute("aria-pressed", e.mdnsListen ? "true" : "false");
  const ms = e.mdnsStatus || {};
  $("setMdnsStatus").textContent = !e.mdnsListen
    ? "Off."
    : ms.running
      ? `Listening on ${(ms.interfaces || []).join(", ")} · ${ms.packets || 0} announcements heard.`
      : (ms.error ? `Not listening: ${ms.error}` : "Starting on the next scan.");

  fillReport(e.report || {});
  rulesData = e.rules || null;
  fillRules();
  if (typeof e.arpWatch === "boolean") arpWatch = e.arpWatch;
  if (typeof e.certWatch === "boolean") certWatch = e.certWatch;
  if (typeof e.ipv6Watch === "boolean") ipv6Watch = e.ipv6Watch;
  renderArpWatchToggle();
  renderCertWatchToggle();
  renderIpv6WatchToggle();
  loadGreyNoise().then(renderGreyNoiseToggle);
  loadWan(true).then(renderWanToggle);
  loadSpeed().then(renderSpeedSetting);
  $("setNewDays").value = newDays;
  loadRouterImport();
  loadAuth();

  // The instant-apply toggles and the notifications card draw from the last
  // /api/hosts poll rather than this endpoint, so refresh them here too.
  renderArpToggle();
  renderRandToggle();
  renderLatencyToggle();
  renderTrafficToggle();
  renderHolidayToggle();
  renderUpdateToggle();
  fillNotify();

  // As they are now, not as of the last scan, so one just added shows at once.
  const nets = (e.networks && e.networks.list) || ro.subnets || [];
  const tbl = $("setNetTable");
  tbl.innerHTML = "";
  if (!nets.length) {
    $("setNetNote").textContent = "No networks configured.";
  } else {
    $("setNetNote").textContent = "";
    const paused = new Set(e.disabledSubnets || []);
    nets.forEach(net => {
      const override = e.subnetIntervalOverrides ? e.subnetIntervalOverrides[net] : undefined;
      const effective = e.subnetIntervalSeconds ? e.subnetIntervalSeconds[net] : undefined;
      const isOn = !paused.has(net);
      const tr = document.createElement("tr");
      tr.classList.toggle("set-net-paused", !isOn);

      // Same pill switch as the header toggles, so "on" reads the same way
      // everywhere. Only flips local state; nothing is sent until Save.
      const tdOn = document.createElement("td");
      const sw = document.createElement("button");
      sw.type = "button";
      sw.className = "arp-toggle set-net-toggle" + (isOn ? " on" : "");
      sw.dataset.net = net;
      sw.title = isOn ? "Scanning - click to pause this network" : "Paused - click to resume scanning";
      sw.setAttribute("aria-pressed", isOn ? "true" : "false");
      sw.innerHTML = '<span class="pill"></span>';
      tdOn.appendChild(sw);
      tr.appendChild(tdOn);

      const tdNet = document.createElement("td");
      tdNet.className = "set-net";
      tdNet.textContent = net;
      tr.appendChild(tdNet);

      const tdEff = document.createElement("td");
      tdEff.className = "set-eff";
      const noIf = isOn && scanModes[net] === "skipped";
      tdEff.textContent = !isOn ? "paused" : (effective === undefined ? "" : "every " + effective + "s");
      if (noIf) {
        tdEff.appendChild(document.createTextNode(" \u00b7 no interface on this machine"));
        tdEff.title = "This machine has no address on this network, so scanning it finds nothing. Remove it from Bamf:Subnets, or add a NIC on it.";
        tr.classList.add("set-net-paused");
      }
      if (isOn && !noIf && subnetNextDue[net]) {
        // Ticks along with the header countdown; see the interval near it.
        const due = document.createElement("span");
        due.dataset.dueNet = net; due.dataset.dueFmt = "in";
        due.textContent = dueText(subnetNextDue[net], "in");
        tdEff.appendChild(document.createTextNode(" \u00b7 "));
        tdEff.appendChild(due);
      }
      tr.appendChild(tdEff);

      const tdIn = document.createElement("td");
      const input = document.createElement("input");
      input.className = "modal-input set-net-input";
      input.type = "number";
      input.min = settingsData.minIntervalSeconds;
      input.step = "1";
      input.placeholder = "default";
      input.dataset.net = net;
      input.disabled = !isOn;
      if (override !== undefined) input.value = override;
      tdIn.appendChild(input);
      tr.appendChild(tdIn);

      const tdRm = document.createElement("td");
      tdRm.className = "set-net-rm-cell";
      if (nets.length > 1) {
        const rm = document.createElement("button");
        rm.type = "button";
        rm.className = "set-net-rm";
        rm.textContent = "\u00d7";
        rm.title = `Stop scanning ${net} and take it off the list. Its devices stay, with their history.`;
        rm.setAttribute("aria-label", `Remove ${net}`);
        rm.onclick = () => saveNetworks(nets.filter(n => n !== net), `Removed ${net}.`);
        tdRm.appendChild(rm);
      }
      tr.appendChild(tdRm);

      sw.onclick = () => {
        const nowOn = !sw.classList.contains("on");
        sw.classList.toggle("on", nowOn);
        sw.setAttribute("aria-pressed", nowOn ? "true" : "false");
        sw.title = nowOn ? "Scanning - click to pause this network" : "Paused - click to resume scanning";
        tr.classList.toggle("set-net-paused", !nowOn);
        input.disabled = !nowOn;
        tdEff.textContent = nowOn
          ? (effective === undefined ? "" : "now every " + effective + "s")
          : "paused";
      };

      tbl.appendChild(tr);
    });
  }

  renderNetworkSource(e.networks);
  renderIntegrations(e, ro);
  renderBackup(e.backup);
  loadUnusualSetting();

  const roEl = $("setReadOnly");
  roEl.innerHTML = "";
  [
    ["Networks", (ro.subnets || []).join(", ") || "—"],
    ["Listening on", ro.urls || "—"],
    ["Database", ro.databasePath || "—"],
    ["OUI auto-download", ro.autoDownloadOui ? "on" : "off"],
    ["Update repo", ro.updateRepo || "—"],
  ].forEach(([k, v]) => {
    const dk = document.createElement("div");
    dk.className = "set-ro-k";
    dk.textContent = k;
    const dv = document.createElement("div");
    dv.className = "set-ro-v";
    dv.textContent = v;
    roEl.appendChild(dk);
    roEl.appendChild(dv);
  });
}

async function saveSettings() {
  const body = {
    scanIntervalSeconds: Number($("setInterval").value),
    pingConcurrency: Number($("setConcurrency").value),
    historyRetentionDays: Number($("setRetention").value),
    offlineAfterMissedScans: Number($("setMisses").value),
    mdnsListen: $("setMdns").classList.contains("on"),
    subnetIntervalSeconds: {},
  };
  // Sent as a whole map, so clearing a box removes that override rather than
  // leaving the previously saved value in place.
  document.querySelectorAll(".set-net-input").forEach(i => {
    const v = i.value.trim();
    if (v !== "") body.subnetIntervalSeconds[i.dataset.net] = Number(v);
  });
  // Likewise the paused list is sent whole: switching a network back on means
  // it is absent from the list, not present with a flag.
  body.disabledSubnets = [...document.querySelectorAll(".set-net-toggle")]
    .filter(b => !b.classList.contains("on"))
    .map(b => b.dataset.net);

  setMsg("Saving…");
  try {
    const r = await fetch("/api/settings/scan", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      setMsg(d.error || ("Save failed (HTTP " + r.status + ")"), "err");
      return;
    }
    await loadSettings();
    setMsg("Saved — applies on the next scan.", "ok");
  } catch (e) {
    setMsg("Save failed: " + e.message, "err");
  }
}

// ---- The first-run setup ----
// Shown once, on a new install: which networks, and a password. The server
// only offers it when BAMF made its database at this start.
let setupNets = [];   // { network, iface, problem, on }
async function checkSetup() {
  let s;
  try {
    const r = await fetch("/api/setup");
    if (!r.ok) return;
    s = await r.json();
  } catch { return; }
  if (!s.pending) return;
  const n = s.networks;
  setupNets = (n.found || []).map(f => ({ network: f.network, iface: f.interface, problem: f.problem, on: !f.problem && n.list.includes(f.network) }));
  for (const net of n.list) if (!setupNets.some(x => x.network === net)) setupNets.push({ network: net, iface: "", problem: null, on: true });
  renderSetupNets();
  $("setupPwAsk").hidden = !!s.password;
  $("setupPwSet").hidden = !s.password;
  $("setupPw").placeholder = `At least ${s.minLength} characters`;
  $("setupModal").hidden = false;
  (s.password ? $("setupGo") : $("setupPw")).focus();
}
function renderSetupNets() {
  const box = $("setupNets");
  box.innerHTML = "";
  if (!setupNets.length) {
    box.innerHTML = '<p class="modal-help">BAMF didn\'t find a network it can watch on this machine. Add one below.</p>';
    return;
  }
  for (const n of setupNets) {
    const row = document.createElement("label");
    row.className = "setup-net" + (n.problem ? " off" : "");
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = n.on;
    cb.disabled = !!n.problem;
    cb.onchange = () => { n.on = cb.checked; };
    const name = document.createElement("span");
    name.className = "mono";
    name.textContent = n.network;
    row.append(cb, name);
    if (n.iface || n.problem) {
      const why = document.createElement("span");
      why.className = "if";
      why.textContent = n.problem ? "Can't be watched from here: " + n.problem : n.iface;
      row.appendChild(why);
    }
    box.appendChild(row);
  }
}
function setupMsg(text, kind) {
  const el = $("setupMsg");
  el.textContent = text;
  el.className = "set-result" + (kind ? " " + kind : "");
}
$("setupNetAdd").onclick = () => {
  const v = $("setupNetNew").value.trim();
  if (!v) return;
  if (!setupNets.some(n => n.network === v)) setupNets.push({ network: v, iface: "", problem: null, on: true });
  $("setupNetNew").value = "";
  renderSetupNets();
  setupMsg("");
};
$("setupNetNew").onkeydown = e => { if (e.key === "Enter") $("setupNetAdd").click(); };
async function finishSetup(skip) {
  const body = { skip };
  if (!skip) {
    body.networks = setupNets.filter(n => n.on).map(n => n.network);
    if (!body.networks.length) { setupMsg("Tick at least one network.", "err"); return; }
    if (!$("setupPwAsk").hidden) {
      body.open = $("setupOpen").checked;
      if (!body.open) {
        if ($("setupPw").value !== $("setupPw2").value) { setupMsg("The two passwords aren't the same.", "err"); $("setupPw2").focus(); return; }
        body.password = $("setupPw").value;
      }
    }
  }
  setupMsg("Saving…");
  try {
    const r = await fetch("/api/setup", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const d = await r.json().catch(() => ({}));
    if (!r.ok && r.status !== 409) { setupMsg(d.error || "Couldn't save (HTTP " + r.status + ")", "err"); return; }
  } catch (e) {
    setupMsg("Couldn't save: " + e.message, "err");
    return;
  }
  $("setupModal").hidden = true;
  if (!skip) toast("BAMF is set up and scanning.");
  refresh();
}
$("setupGo").onclick = () => finishSetup(false);
$("setupSkip").onclick = () => finishSetup(true);
$("setupOpen").onchange = () => {
  const open = $("setupOpen").checked;
  $("setupPw").disabled = $("setupPw2").disabled = open;
};

// ---- Settings → Scanning → Networks: adding and removing ----
// Networks saved here replace appsettings.json's list; only private ones no
// bigger than a sweep covers can be added (the server says why if not).
function renderNetworkSource(n) {
  if (!n) return;
  const found = $("setNetFound");
  const extra = (n.found || []).filter(f => !f.problem && !n.list.includes(f.network));
  found.hidden = !extra.length;
  found.innerHTML = "";
  if (extra.length) {
    found.append("On this machine: ");
    for (const f of extra) {
      const b = document.createElement("button");
      b.type = "button";
      b.textContent = `+ ${f.network} (${f.interface})`;
      b.title = `Scan ${f.network} too: this machine is on it, through ${f.interface}.`;
      b.onclick = () => saveNetworks([...n.list, f.network], `Added ${f.network}.`);
      found.appendChild(b);
    }
  }
  const src = $("setNetSource");
  if (n.source === "settings") {
    src.innerHTML = "These networks are saved here. ";
    const back = document.createElement("button");
    back.type = "button";
    back.textContent = "Use appsettings.json's instead";
    back.onclick = async () => {
      try { await fetch("/api/settings/networks/reset", { method: "POST" }); } catch { }
      await loadSettings();
    };
    src.appendChild(back);
  } else {
    src.innerHTML = n.source === "file"
      ? "These networks come from <span class=\"mono\">appsettings.json</span>. Adding or removing one here saves the list here instead."
      : "No networks are listed in <span class=\"mono\">appsettings.json</span>, so BAMF scans every network this machine is on.";
  }
}
function netMsg(text, kind) {
  const el = $("setNetResult");
  el.textContent = text;
  el.className = "set-result" + (kind ? " " + kind : "");
}
async function saveNetworks(list, done) {
  netMsg("Saving…");
  try {
    const r = await fetch("/api/settings/networks", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ networks: list }),
    });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { netMsg(d.error || "Couldn't save (HTTP " + r.status + ")", "err"); return false; }
    await loadSettings();
    netMsg(done + " Scanning now.", "ok");
    return true;
  } catch (e) {
    netMsg("Couldn't save: " + e.message, "err");
    return false;
  }
}
async function addNetwork() {
  const box = $("setNetNew");
  const v = box.value.trim();
  if (!v) { netMsg("Type a network first, like 192.168.1.0/24.", "err"); box.focus(); return; }
  const list = (settingsData?.editable?.networks?.list) || [];
  if (await saveNetworks([...list, v], `Added ${v}.`)) box.value = "";
}
// ---- Settings → Security → HTTPS ----
// A certificate BAMF makes itself; HTTPS starts with it at the next restart.
function renderHttps(h) {
  if (!h) return;
  const st = $("httpsStatus"), c = h.certificate;
  const addr = `https://${location.hostname}:${h.port}/`;
  const restart = ` ${esc(h.restart)}`;
  st.classList.toggle("bad", h.state === "problem");
  st.innerHTML =
    h.state === "file" ? "<b>On</b>, set in <span class=\"mono\">appsettings.json</span> with its own certificate."
    : h.state === "on" ? `<b>On</b> at <a href="${esc(addr)}">${esc(addr)}</a>, beside plain HTTP.` + (location.protocol === "https:" ? "" : " You're on HTTP now; that link is the encrypted way in.")
    : h.state === "problem" ? `<b>Not on:</b> ${esc(h.problem || "")}`
    : h.state === "restart" && c ? `<b>Ready.</b> HTTPS starts on port ${h.port} the next time BAMF starts.` + restart + " If the machine has a firewall, let that port through too."
    : h.state === "restart" ? `<b>Turned off.</b> HTTPS stops the next time BAMF starts.` + restart
    : "<b>Off.</b> The dashboard is served over plain HTTP.";
  const info = $("httpsCert");
  info.hidden = !c || h.state === "file";
  if (c) {
    const fp = c.fingerprint.match(/.{2}/g).join(":");
    info.innerHTML = `For <span class="mono">${c.names.map(esc).join(", ")}</span><br>`
      + `Good until ${esc(new Date(c.expires).toLocaleDateString())}${httpsExpiry(c.expires)}<br>`
      + `SHA-256 fingerprint, to check against the browser's warning: <span class="mono">${esc(fp)}</span>`;
  }
  $("httpsCreate").hidden = h.state === "file";
  $("httpsCreate").textContent = c ? "New certificate" : "Make a certificate and turn HTTPS on";
  $("httpsDownload").hidden = !c || h.state === "file";
  $("httpsRemove").hidden = !c || h.state === "file";
}
// Within a month of the end, or past it: time for a new one.
function httpsExpiry(iso) {
  const days = Math.ceil((new Date(iso) - Date.now()) / 86400000);
  return days <= 0 ? `<b class="https-late">, and it has passed. Make a new certificate.</b>`
    : days <= 30 ? `<b class="https-late">, ${days} day${days === 1 ? "" : "s"} from now. Make a new certificate soon.</b>` : "";
}
async function httpsAction(action, done) {
  intMsgHttps("Working…");
  try {
    const r = await fetch("/api/settings/https", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ action }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(d.error || "HTTP " + r.status);
    renderHttps(d);
    intMsgHttps(done, "ok");
  } catch (err) { intMsgHttps(err.message, "err"); }
}
function intMsgHttps(text, kind) {
  const el = $("httpsResult");
  el.textContent = text;
  el.className = "set-result" + (kind ? " " + kind : "");
}
$("httpsCreate").onclick = () => {
  const had = settingsData?.editable?.https?.certificate;
  if (had && !confirm("Make a new certificate? It replaces this one at the next restart, and a device that trusted the old one will warn again.")) return;
  httpsAction("create", "Made.");
};
$("httpsRemove").onclick = () => {
  if (!confirm("Turn HTTPS off? It stops when BAMF restarts, and the https:// address stops working then.")) return;
  httpsAction("remove", "Removed.");
};
$("setNetAdd").onclick = addNetwork;
$("setNetNew").onkeydown = e => { if (e.key === "Enter") addNetwork(); };

// ---- Settings → System: MQTT, other BAMF servers, the webhooks' token ----
// Each saved here replaces appsettings.json's. Passwords and the token are
// write-only: the page is only ever told whether one is saved.
let remotesList = [];
function intMsg(id, text, kind) {
  const el = $(id);
  el.textContent = text;
  el.className = "set-result" + (kind ? " " + kind : "");
}
const pill = (id, on) => { const b = $(id); b.classList.toggle("on", !!on); b.setAttribute("aria-pressed", on ? "true" : "false"); };
["mqttTls", "mqttDiscovery"].forEach(id => { $(id).onclick = () => pill(id, !$(id).classList.contains("on")); });
function renderIntegrations(e, ro, statusOnly) {
  const m = e.mqtt;
  if (m && !statusOnly) {
    $("mqttServer").value = m.server || "";
    $("mqttPort").value = m.server ? m.port : "";
    $("mqttUser").value = m.username || "";
    $("mqttPass").value = "";
    $("mqttPass").placeholder = m.password ? "Saved; type to change" : "None";
    pill("mqttTls", m.tls);
    pill("mqttDiscovery", m.discovery);
    $("mqttPrefix").value = m.topicPrefix || "bamf";
    $("mqttReset").hidden = m.source !== "settings";
  }
  if (m) {
    const st = $("mqttStatus");
    const from = m.source === "file" ? " Set in <span class=\"mono\">appsettings.json</span>; saving here replaces it." : "";
    st.classList.toggle("bad", !!(m.server && !m.connected && m.error));
    st.innerHTML = !m.server ? "Off." + from
      : m.connected ? `<b>Connected</b> to ${esc(m.server)}:${m.port} · ${Number(m.published || 0).toLocaleString()} published` + (m.lastPublish ? `, last ${fmtAgo(m.lastPublish)}` : "") + "." + from
      : `<b>Not connected</b> to ${esc(m.server)}:${m.port}` + (m.error ? ": " + esc(m.error) : ", trying") + "." + from;
  }
  const r = e.remotes;
  if (r) {
    remotesList = r.list.map(x => ({ name: x.name, url: x.url, password: x.password }));
    const status = new Map((ro.remotes || []).map(s => [s.name, s]));
    const tbl = $("remotesTable");
    tbl.innerHTML = "";
    if (!remotesList.length) {
      tbl.innerHTML = `<tr><td class="rm-none">None yet.</td></tr>`;
    }
    for (const x of remotesList) {
      const s = status.get(x.name);
      const tr = document.createElement("tr");
      const state = !s || s.error === "not fetched yet" ? "not read yet" : s.ok ? `${s.hosts} devices, ${s.version || "?"}, fetched ${fmtAgo(s.fetchedUtc)}` : "unreachable" + (s.error ? ": " + s.error : "");
      tr.innerHTML = `<td><b>${esc(x.name)}</b><div class="rm-url">${esc(x.url)}</div></td>
        <td class="set-eff">${esc(state)}${x.password ? " · password saved" : ""}</td><td class="set-net-rm-cell"></td>`;
      const rm = document.createElement("button");
      rm.type = "button";
      rm.className = "set-net-rm";
      rm.textContent = "\u00d7";
      rm.title = `Stop reading ${x.name}; its devices go from this dashboard.`;
      rm.setAttribute("aria-label", `Remove ${x.name}`);
      rm.onclick = () => saveRemotes(remotesList.filter(y => y !== x), `Removed ${x.name}.`);
      tr.lastElementChild.appendChild(rm);
      tbl.appendChild(tr);
    }
    $("remotesReset").hidden = r.source !== "settings";
  }
  const h = e.hookToken;
  if (h) {
    $("hookStatus").innerHTML = h.source === "settings" ? "<b>A token is set</b>, here."
      : h.source === "file" ? "<b>A token is set</b>, in <span class=\"mono\">appsettings.json</span>. Making a new one here replaces it."
      : "<b>No token.</b> The webhooks are as open as the dashboard: with a password, they need it; without one, anyone can call them.";
    $("hookReset").hidden = h.source !== "settings";
  }
}
// A few seconds after a save, once BAMF has tried the broker or the servers.
function refreshIntegrationsSoon() {
  setTimeout(async () => {
    if (view !== "settings") return;
    try {
      const r = await fetch("/api/settings");
      if (r.ok) { const s = await r.json(); renderIntegrations(s.editable, s.readOnly, true); }
    } catch { /* the next visit shows it */ }
  }, 4000);
}
async function postSetting(url, body) {
  const r = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  const d = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(d.error || "HTTP " + r.status);
  return d;
}
// ---- Heartbeat ----
let heartbeatCache = null;
function renderHeartbeat() {
  const d = heartbeatCache;
  if (!d) return;
  const on = $("hbEnabled");
  on.classList.toggle("on", d.enabled);
  on.setAttribute("aria-pressed", d.enabled ? "true" : "false");
  $("hbMinutes").value = d.minutes;
  $("hbUrl").value = "";
  $("hbUrl").placeholder = d.configured ? `Saved: ${d.masked}` : "https://hc-ping.com/\u2026";
  const last = d.last || {};
  $("hbStatus").textContent = !d.enabled ? "Off."
    : last.ok === null || last.ok === undefined ? "On. The first visit is about to go."
    : last.ok ? `On. Last visit ${fmtAgo(last.at)}, every ${d.minutes} minute${d.minutes === 1 ? "" : "s"}.`
    : `On, but the last visit failed: ${last.error}`;
  $("hbClear").hidden = !d.configured;
}
async function loadHeartbeat() {
  try { const r = await fetch("/api/settings/heartbeat"); if (r.ok) { heartbeatCache = await r.json(); renderHeartbeat(); } } catch { /* leave it */ }
}
async function saveHeartbeat(patch) {
  const d = heartbeatCache || {};
  const body = { enabled: d.enabled, minutes: Number($("hbMinutes").value) || d.minutes, ...patch };
  if (!("url" in body) && $("hbUrl").value.trim()) body.url = $("hbUrl").value.trim();
  const out = $("hbResult");
  try {
    const r = await fetch("/api/settings/heartbeat", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const j = await r.json().catch(() => ({}));
    if (!r.ok) { out.textContent = j.error || `Couldn't save (HTTP ${r.status}).`; out.className = "set-result err"; return false; }
    heartbeatCache = j; renderHeartbeat();
    out.textContent = ""; out.className = "set-result";
    loadConnections().then(renderServices);
    return true;
  } catch { out.textContent = "Couldn't reach BAMF."; return false; }
}
$("hbEnabled").onclick = async () => {
  const next = !(heartbeatCache && heartbeatCache.enabled);
  if (await saveHeartbeat({ enabled: next })) toast(next ? "Heartbeat on" : "Heartbeat off");
};
$("hbSave").onclick = async () => { if (await saveHeartbeat({})) toast("Heartbeat saved"); };
$("hbClear").onclick = async () => {
  if (!confirm("Forget the saved heartbeat address? The heartbeat stops.")) return;
  if (await saveHeartbeat({ url: "", enabled: false })) toast("Heartbeat address cleared");
};
$("hbTest").onclick = async () => {
  const out = $("hbResult"), b = $("hbTest");
  if ($("hbUrl").value.trim() && !(await saveHeartbeat({}))) return;
  b.disabled = true; out.textContent = "Visiting\u2026"; out.className = "set-result";
  try {
    const r = await fetch("/api/heartbeat/test", { method: "POST" });
    const j = await r.json().catch(() => ({}));
    out.textContent = !r.ok ? (j.error || `HTTP ${r.status}`) : j.ok ? "It answered. Check that the service shows the visit." : (j.error || "It didn't answer.");
    out.className = "set-result" + (r.ok && j.ok ? " ok" : " err");
    loadHeartbeat();
  } catch { out.textContent = "Couldn't reach BAMF."; }
  finally { b.disabled = false; }
};

$("mqttSave").onclick = async () => {
  intMsg("mqttResult", "Saving…");
  try {
    await postSetting("/api/settings/mqtt", {
      server: $("mqttServer").value.trim(),
      port: $("mqttPort").value ? Number($("mqttPort").value) : 1883,
      username: $("mqttUser").value.trim(),
      password: $("mqttPass").value === "" ? null : $("mqttPass").value,
      tls: $("mqttTls").classList.contains("on"),
      discovery: $("mqttDiscovery").classList.contains("on"),
      topicPrefix: $("mqttPrefix").value.trim() || "bamf",
    });
    await loadSettings();
    intMsg("mqttResult", "Saved.", "ok");
    refreshIntegrationsSoon();
  } catch (err) { intMsg("mqttResult", err.message, "err"); }
};
$("mqttReset").onclick = async () => {
  try { await postSetting("/api/settings/mqtt/reset"); await loadSettings(); intMsg("mqttResult", "Back to appsettings.json's.", "ok"); }
  catch (err) { intMsg("mqttResult", err.message, "err"); }
};
async function saveRemotes(list, done) {
  intMsg("remotesResult", "Saving…");
  try {
    await postSetting("/api/settings/remotes", { remotes: list.map(x => ({ name: x.name, url: x.url, password: x.newPassword ?? null })) });
    await loadSettings();
    intMsg("remotesResult", done, "ok");
    refreshIntegrationsSoon();
    return true;
  } catch (err) { intMsg("remotesResult", err.message, "err"); return false; }
}
$("remoteAdd").onclick = async () => {
  const name = $("remoteName").value.trim(), url = $("remoteUrl").value.trim(), pass = $("remotePass").value;
  if (!name || !url) { intMsg("remotesResult", "Give it a name and the address it opens at.", "err"); return; }
  // Adding one with a name already there replaces it, password and all.
  const list = remotesList.filter(x => x.name.toLowerCase() !== name.toLowerCase());
  list.push({ name, url, newPassword: pass });
  if (await saveRemotes(list, `Saved ${name}. Reading it now.`)) { $("remoteName").value = $("remoteUrl").value = $("remotePass").value = ""; }
};
$("remotesReset").onclick = async () => {
  try { await postSetting("/api/settings/remotes/reset"); await loadSettings(); intMsg("remotesResult", "Back to appsettings.json's.", "ok"); }
  catch (err) { intMsg("remotesResult", err.message, "err"); }
};
$("hookNew").onclick = async () => {
  if (settingsData?.editable?.hookToken?.source && !confirm("Make a new token? Anything using the current one stops working until it's given the new one.")) return;
  try {
    const d = await postSetting("/api/settings/hooktoken", { action: "generate" });
    $("hookToken").textContent = d.token;
    $("hookShow").hidden = false;
    await loadSettings();
    intMsg("hookResult", "Saved.", "ok");
  } catch (err) { intMsg("hookResult", err.message, "err"); }
};
$("hookReset").onclick = async () => {
  try { await postSetting("/api/settings/hooktoken", { action: "reset" }); $("hookShow").hidden = true; await loadSettings(); intMsg("hookResult", "Back to appsettings.json's.", "ok"); }
  catch (err) { intMsg("hookResult", err.message, "err"); }
};

// ---- Settings → Security → Sign-in ----
let authInfo = null;
async function loadAuth() {
  try {
    const r = await fetch("/api/auth");
    if (r.ok) authInfo = await r.json();
  } catch { }
  renderAuth();
}
function renderAuth() {
  const a = authInfo;
  if (!a) return;
  const where = s => s === "file" ? "Set in <span class=\"mono\">appsettings.json</span>. Saving one here replaces it." : "Set here.";
  $("authStatus").innerHTML = a.required
    ? "BAMF asks for a password." + (a.role === "viewer" ? " You're signed in with the view-only one." : "")
    : "<b>Anyone who can reach BAMF can open it and change things.</b> Set a main password and it will ask for one.";
  $("authCurrentRow").hidden = !a.required;
  $("authMainState").innerHTML = a.main ? where(a.main) : "Not set.";
  $("authViewerState").innerHTML = !a.main ? "Needs a main password first." : a.viewer ? where(a.viewer) : "Not set.";
  $("authViewer").disabled = $("authViewerSave").disabled = !a.main;
  $("authMainRemove").hidden = a.main !== "settings";
  $("authViewerRemove").hidden = a.viewer !== "settings";
  $("authSignOut").hidden = !a.session;
  $("authMain").placeholder = a.main ? "New password" : `At least ${a.minLength} characters`;
  $("authViewer").placeholder = a.viewer ? "New password" : `At least ${a.minLength} characters`;
}
function authMsg(text, kind) {
  const el = $("authResult");
  el.textContent = text;
  el.className = "set-result" + (kind ? " " + kind : "");
}
async function savePassword(role, remove) {
  const box = $(role === "viewer" ? "authViewer" : "authMain");
  if (!remove && !box.value) { authMsg("Type the new password first.", "err"); box.focus(); return; }
  if (remove && role === "admin" && !confirm("Remove the main password? Anyone who can reach BAMF will be able to open it and change things, and the view-only password goes too.")) return;
  authMsg("Saving…");
  try {
    const r = await fetch("/api/settings/password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ role, current: $("authCurrent").value, password: remove ? "" : box.value }),
    });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { authMsg(d.error || "Couldn't save (HTTP " + r.status + ")", "err"); return; }
    box.value = "";
    $("authCurrent").value = "";
    await loadAuth();
    authMsg(remove ? "Removed." : role === "viewer" ? "View-only password saved." : "Saved. This browser is signed in with it.", "ok");
  } catch (e) {
    authMsg("Couldn't save: " + e.message, "err");
  }
}
$("authMainSave").onclick = () => savePassword("admin", false);
$("authMainRemove").onclick = () => savePassword("admin", true);
$("authViewerSave").onclick = () => savePassword("viewer", false);
$("authViewerRemove").onclick = () => savePassword("viewer", true);
$("authMain").onkeydown = e => { if (e.key === "Enter") savePassword("admin", false); };
$("authViewer").onkeydown = e => { if (e.key === "Enter") savePassword("viewer", false); };
$("authSignOut").onclick = async () => {
  try { await fetch("/api/signout", { method: "POST" }); } catch { }
  location.href = "/signin";
};

$("setSave").onclick = saveSettings;
// History retention sits under System, but it's saved with the scanning
// settings, so its Save is the same one.
$("setSaveSys").onclick = saveSettings;
$("setMdns").onclick = () => {
  const b = $("setMdns");
  const on = !b.classList.contains("on");
  b.classList.toggle("on", on);
  b.setAttribute("aria-pressed", on ? "true" : "false");
};
$("setReset").onclick = async () => {
  if (!confirm("Discard the settings saved here and go back to appsettings.json?")) return;
  setMsg("Resetting…");
  try {
    const r = await fetch("/api/settings/scan/reset", { method: "POST" });
    if (!r.ok) throw new Error("HTTP " + r.status);
    await loadSettings();
    setMsg("Reset — appsettings.json is authoritative again.", "ok");
  } catch (e) {
    setMsg("Reset failed: " + e.message, "err");
  }
};

// ---- Settings sections ----
// One section at a time, picked from the list down the side. Each has an
// address of its own, /#settings/internet and so on, so a link or the alerts
// banner can open the right one. Typing in "Find a setting" shows every
// section at once, cut down to the settings that match.
const SET_SECS = [...document.querySelectorAll("#setNav [data-sec]")].map(b => b.dataset.sec);
let setSec = SET_SECS[0];
function showSetSec(sec) {
  if (!SET_SECS.includes(sec)) sec = SET_SECS[0];
  setSec = sec;
  if ($("setFind").value) { $("setFind").value = ""; findSetting(""); }
  document.querySelectorAll(".set-sec").forEach(x => x.classList.toggle("show", x.dataset.sec === sec));
  document.querySelectorAll("#setNav [data-sec]").forEach(b => b.classList.toggle("active", b.dataset.sec === sec));
  if (sec === "appearance") renderThemeLib();
}
function findSetting(text) {
  const q = text.trim().toLowerCase();
  $("settingsWrap").classList.toggle("searching", !!q);
  let any = false;
  document.querySelectorAll(".set-sec").forEach(sec => {
    let hit = false;
    sec.querySelectorAll(".set-card").forEach(card => {
      if (!q) { card.classList.remove("set-miss"); card.querySelectorAll(".set-miss").forEach(r => r.classList.remove("set-miss")); return; }
      const title = (card.querySelector(".set-card-title")?.textContent || "").toLowerCase().includes(q);
      const rows = card.querySelectorAll(".set-row");
      let found = title;
      if (rows.length) rows.forEach(r => { const m = title || r.textContent.toLowerCase().includes(q); r.classList.toggle("set-miss", !m); found ||= m; });
      else found = card.textContent.toLowerCase().includes(q);
      card.classList.toggle("set-miss", !found);
      hit ||= found;
    });
    // The Save and Reset under the networks go with the cards above them, so
    // they show only while one of those does.
    sec.querySelectorAll(":scope > .set-actions").forEach(x => {
      let keep = false;
      for (let el = x.previousElementSibling; el; el = el.previousElementSibling)
        if (el.classList.contains("set-card") && !el.classList.contains("set-miss")) keep = true;
      x.classList.toggle("set-miss", !!q && !keep);
    });
    sec.classList.toggle("show", q ? hit : sec.dataset.sec === setSec);
    any ||= hit;
  });
  document.querySelectorAll("#setNav [data-sec]").forEach(b => b.classList.toggle("active", !q && b.dataset.sec === setSec));
  // System's Save is for history retention, the row just above it.
  $("setSaveSys").parentElement.classList.toggle("set-miss", !!q && $("setRetention").closest(".set-row").classList.contains("set-miss"));
  $("setNoMatch").hidden = !q || any;
}
document.querySelectorAll("#setNav [data-sec]").forEach(b => b.onclick = () => showView("settings/" + b.dataset.sec));
$("setFind").oninput = e => findSetting(e.target.value);
$("setFind").onkeydown = e => { if (e.key === "Escape" && e.target.value) { e.stopPropagation(); e.target.value = ""; findSetting(""); } };
showSetSec(setSec);
