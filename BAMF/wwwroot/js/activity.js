// The Activity tab: the feed and the events, alerts, the log of settings
// changes, top talkers, network hygiene, what changed, and devices only on IPv6.


async function loadFeed() {
  const r = await fetch("/api/events");
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}

// Where a device's traffic figure comes from, for its tooltip.
function trafficSince(t) {
  if (t && t.source === "switch") return `counted by ${t.where || "the switch"} since BAMF started reading it`;
  if (t && t.source === "router") return `counted by ${t.where || "the router"} since BAMF started reading it`;
  return "since the monitor started";
}
async function loadTraffic() {
  const r = await fetch("/api/traffic");
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}
// The Activity tab's two cards: who's moving the most bytes, and the DHCP
// and DNS servers in use with any alerts about them.
// The Activity tab's log of settings changes.
// ---- What is new on Activity since this browser last opened it ----
// The number on the tab. "Seen" is a server time kept in this browser; opening Activity moves it to now.
const ACTIVITY_SEEN_KEY = "bamf-activity-seen", ACTIVITY_BADGE_KEY = "bamf-activity-badge";
function activityBadgeOn() { try { return localStorage.getItem(ACTIVITY_BADGE_KEY) !== "off"; } catch { return true; } }
function activitySeenAt() { try { return localStorage.getItem(ACTIVITY_SEEN_KEY); } catch { return null; } }
function renderActivityBadge(n) {
  const el = $("activityCount");
  if (!el) return;
  el.hidden = !n || !activityBadgeOn() || view === "activity";
  if (!el.hidden) {
    el.textContent = n > 99 ? "99+" : n;
    el.title = `${n} new on Activity since you last looked`;
  }
}
async function loadUnseen() {
  try {
    const since = activitySeenAt();
    // Looking at Activity now, or never looked: nothing is new; just note the time.
    const open = view === "activity" || !since;
    const r = await fetch("/api/activity/unseen" + (open ? "" : "?since=" + encodeURIComponent(since)));
    if (!r.ok) return;
    const d = await r.json();
    if (open) { try { localStorage.setItem(ACTIVITY_SEEN_KEY, d.now); } catch { /* private window */ } renderActivityBadge(0); }
    else renderActivityBadge((d.alerts || 0) + (d.problems || 0));
  } catch { /* the next refresh tries again */ }
}

// ---- Disk and database: how much room BAMF has ----
let healthCache = null;
async function loadHealth() {
  try { const r = await fetch("/api/health"); if (r.ok) { healthCache = await r.json(); renderHealth(); } } catch { /* keep the last */ }
}
function renderHealth() {
  const d = healthCache;
  if (!d || !$("healthBody")) return;
  const low = (d.volumes || []).some(v => v.low);
  $("healthSub").textContent = low ? "A drive BAMF writes to is running low. When a disk fills, backups fail and BAMF can stop recording."
    : d.alert.enabled ? `Fine. BAMF says so when a drive has under ${d.alert.percent}% (or half a gigabyte) free.` : "The low disk space alert is off (Settings \u2192 System).";
  const rows = [
    `<div class="health-row"><span>Database</span><b>${esc(fmtBytes(d.databaseBytes))}</b><span class="sub">${Number(d.historyRows).toLocaleString()} history events</span></div>`,
    `<div class="health-row"><span>Backups</span><b>${esc(fmtBytes(d.backupBytes))}</b><span class="sub">${d.backups} kept</span></div>`,
  ];
  for (const v of d.volumes || []) {
    const used = Math.max(0, Math.min(100, 100 - v.freePercent));
    rows.push(`<div class="health-vol${v.low ? " low" : ""}"><div class="health-row"><span>${esc(v.role)}</span><b>${esc(fmtBytes(v.freeBytes))} free</b><span class="sub">${v.freePercent}% of ${esc(fmtBytes(v.totalBytes))}</span></div>` +
      `<div class="health-bar"><i style="width:${used}%"></i></div></div>`);
  }
  $("healthBody").innerHTML = rows.join("");
}

// ---- Problems: BAMF's own warnings and errors since it started ----
let problemsCache = null;
async function loadProblems() {
  try { const r = await fetch("/api/problems"); if (r.ok) { problemsCache = await r.json(); renderProblems(); } } catch { /* keep the last */ }
}
// "ScannerService" -> "Scanner", "RemoteService" -> "Remote", "WanWatch" -> "Wan watch"
function problemSource(name) {
  return String(name).replace(/Service$/, "").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, c => c.toUpperCase()).replace(/ ([A-Z])/g, (m, c) => " " + c.toLowerCase());
}
function renderProblems() {
  const d = problemsCache;
  if (!d || !$("problemsBody")) return;
  const rows = d.rows || [];
  $("problemsSub").textContent = rows.length
    ? `What went wrong inside BAMF since it started ${fmtAgo(d.since)}, newest first. A problem that repeats is one row with a count.`
    : `Nothing has gone wrong inside BAMF since it started ${fmtAgo(d.since)}. A webhook that won't take an alert, a router that refuses a password and a scan that fails would show here.`;
  $("problemsClear").hidden = !rows.length || role === "viewer";
  $("problemsBody").innerHTML = rows.slice(0, 25).map(r =>
    `<div class="problem-row"><span class="wan-dot ${r.level === "error" ? "down" : "slow"}"></span><span class="problem-main">` +
    `<span class="problem-msg">${esc(r.message)}</span>` +
    `<span class="problem-sub">${esc(problemSource(r.source))} \u00b7 ${esc(fmtAgo(r.last))}${r.count > 1 ? ` \u00b7 ${r.count} times, since ${esc(fmtAgo(r.first))}` : ""}</span></span></div>`).join("") +
    (rows.length > 25 ? `<div class="sub" style="margin-top:6px">And ${rows.length - 25} older.</div>` : "");
}
$("problemsClear").onclick = async () => {
  try { await fetch("/api/problems/clear", { method: "POST" }); } catch { /* the next load says */ }
  loadProblems();
};

async function loadSettingsLog() {
  try {
    const r = await fetch("/api/settings/log");
    if (r.ok) renderSettingsLog(await r.json());
  } catch { /* the next refresh tries again */ }
}
// What isn't normal for a device, learned from its history; each with
// "That's normal", which stops that being flagged for it for 30 days.
async function loadUnusual() {
  try {
    const r = await fetch("/api/unusual");
    if (r.ok) renderUnusual(await r.json());
  } catch { /* the next refresh tries again */ }
}
function renderUnusual(d) {
  $("unusualSub").textContent = !d.enabled ? "Off. Settings \u2192 Alerts switches it on."
    : `Watching ${d.watching} device${d.watching === 1 ? "" : "s"} for what isn't normal for them` +
      (d.learning ? `; ${d.learning} still learning (each needs ${d.learnDays} days of history).` : ".");
  const body = $("unusualBody");
  body.innerHTML = "";
  if (!d.items.length) {
    body.innerHTML = `<div class="watch-note">${d.enabled ? "Nothing unusual in the last day." : ""}</div>`;
    return;
  }
  for (const u of d.items) {
    const row = document.createElement("div");
    row.className = "alert-item k-unusual" + (u.open ? " open" : "");
    row.innerHTML = `<div><b>${esc(u.title)}</b><span class="when">${u.open ? "now" : esc(fmtAgo(u.at))}</span></div><div class="det">${esc(u.detail)}</div>`;
    if (u.normal) {
      const note = document.createElement("span");
      note.className = "unusual-ok marked";
      note.textContent = "Marked normal";
      row.appendChild(note);
      body.appendChild(row);
      continue;
    }
    const ok = document.createElement("button");
    ok.type = "button";
    ok.className = "toggle unusual-ok";
    ok.textContent = "That's normal";
    ok.title = "Don't flag this device for this again for 30 days";
    ok.onclick = async () => {
      ok.disabled = true;
      try {
        const res = await fetch(`/api/unusual/${u.id}/normal`, { method: "POST" });
        if (res.ok) { toast("Noted: not flagged for that again for 30 days"); loadUnusual(); }
        else { ok.disabled = false; toast("Couldn't save that"); }
      } catch { ok.disabled = false; toast("Couldn't reach BAMF"); }
    };
    row.appendChild(ok);
    body.appendChild(row);
  }
}
// The network score: one number, the word for it, why it isn't 100, and which way it's going.
let scoreCache = null;
async function loadScore() {
  try { const r = await fetch("/api/score"); if (r.ok) { scoreCache = await r.json(); renderScore(); } } catch { /* keep the last */ }
}
function renderScore() {
  const d = scoreCache;
  if (!d || !$("scoreBody")) return;
  if (!d.enabled) { $("scoreSub").textContent = "Off. Settings → Alerts switches it on."; $("scoreBody").innerHTML = ""; return; }
  const hist = d.history || [];
  const today = new Date().toISOString().slice(0, 10);
  const earlier = hist.filter(h => h.date !== today);
  const prev = earlier.length ? earlier[earlier.length - 1] : null;
  const trend = prev ? (d.value > prev.value ? ` · up from ${prev.value}` : d.value < prev.value ? ` · down from ${prev.value}` : " · same as before") : "";
  $("scoreSub").textContent = `From what BAMF knows now: starts at 100 and loses points for each thing worth fixing${hist.length > 1 ? `, one reading a day kept for ${hist.length} days` : ""}.`;
  const cls = d.value >= 90 ? "up" : d.value >= 75 ? "" : d.value >= 50 ? "slow" : "down";
  const reasons = (d.reasons || []).map(r => `<div class="health-row"><span>${esc(r.text)}</span><b>−${r.points}</b></div>`).join("");
  $("scoreBody").innerHTML = `<div class="wan-state"><span class="wan-dot ${cls}"></span><span class="wan-now score-num">${d.value}</span><span class="wan-ms">${esc(d.word)}${esc(trend)}</span></div>` +
    (reasons || `<div class="watch-note">Nothing is taking points off.</div>`);
}
function renderSettingsLog(list) {
  $("setLogBody").innerHTML = list.length ? list.slice(0, 15).map(c =>
    `<div class="alert-item k-settings"><div><b>${esc(c.what)}</b><span class="when">${esc(fmtAgo(c.at))}</span></div>`
    + `<div class="det">With ${esc(c.who)}${c.address ? ", from " + esc(c.address) : ""}</div></div>`).join("")
    : `<div class="watch-note">Nothing changed yet. Each change made in Settings is listed here.</div>`;
}
function renderAlertsCard() {
  const body = $("alertsBody");
  const list = alertsCache || [];
  body.innerHTML = list.length ? list.slice(0, 12).map(a =>
    `<div class="alert-item k-${esc(a.kind)}"><div><b>${esc(a.title)}</b><span class="when">${esc(fmtAgo(a.at))}</span></div><div class="det">${esc(a.detail)}</div></div>`).join("")
    : `<div class="watch-note">Nothing yet. Rules, the port watch and the DHCP/DNS watch put their alerts here.</div>`;
}

// ---- Only on IPv6 ----
let ipv6Cache = null, ipv6At = 0;
function loadIpv6() {
  if (ipv6Cache && Date.now() - ipv6At < 60e3) { renderIpv6(); return; }
  fetch("/api/ipv6").then(r => r.ok ? r.json() : null).then(d => { if (d) { ipv6Cache = d; ipv6At = Date.now(); } renderIpv6(); }).catch(() => {});
}
function renderIpv6() {
  const d = ipv6Cache, card = document.getElementById("v6Card");
  if (!d || !card) return;
  card.hidden = !d.enabled || !d.onlyIpv6.length;
  $("v6Body").innerHTML = d.onlyIpv6.slice(0, 12).map(x =>
    `<div class="chg-item"><span class="mono">${esc(x.mac)}</span><span class="det">${esc(x.vendor || "")}</span><span class="when">${esc(fmtAgo(x.lastSeen))}</span></div>` +
    `<div class="chg-more mono">${[...x.addresses].map(esc).join(" · ")}</div>`).join("");
}

// ---- What changed ----
// Arrivals, departures, moved addresses, ports that opened or closed, and
// the alerts raised, over the last day, week or month. Fetched at most once
// a minute while Activity is open, or when the period changes.
let changesCache = null, changesAt = 0;
function loadChanges(force) {
  if (!force && changesCache && Date.now() - changesAt < 60e3 && changesCache.days == document.getElementById("chgDays").value) { renderChanges(); return; }
  fetch("/api/changes?days=" + document.getElementById("chgDays").value).then(r => r.ok ? r.json() : null)
    .then(c => { if (c) { changesCache = c; changesAt = Date.now(); } renderChanges(); }).catch(() => {});
}
function renderChanges() {
  const c = changesCache, body = $("chgBody");
  if (!c || !body) return;
  const port = x => `${x.port}${x.service ? " " + x.service : ""}`;
  const secs = [
    ["Arrived", c.arrived, x => x.detail],
    ["Left", c.left, () => "last seen"],
    ["Moved address", c.moved, x => x.detail],
    ["Ports opened", c.opened, port],
    ["Ports closed", c.closed, port],
  ].filter(([, list]) => list.length);
  const kinds = { rule: "rules", port: "port", security: "security", cert: "certificate", dhcp: "DHCP", dns: "DNS", wake: "wake" };
  const alerts = Object.entries(c.alerts || {}).filter(([, n]) => n > 0);
  const total = secs.reduce((n, [, l]) => n + l.length, 0);
  $("chgSub").textContent = !total && !alerts.length ? "Nothing changed." : secs.map(([t, l]) => `${l.length} ${t.toLowerCase()}`).join(" · ");
  body.innerHTML = "";
  for (const [title, list, detail] of secs) {
    const sec = document.createElement("div");
    sec.className = "chg-sec";
    sec.innerHTML = `<h4>${esc(title)} (${list.length})</h4>`;
    for (const x of list.slice(0, 5)) {
      const row = document.createElement("div");
      row.className = "chg-item" + (x.hostId ? " link" : "");
      row.innerHTML = `<span>${esc(x.name || x.ip)}</span><span class="det">${esc(detail(x) || "")}</span><span class="when">${esc(fmtAgo(x.at))}</span>`;
      if (x.hostId) { row.title = "Show this device"; row.onclick = () => jumpToHost(x.hostId); }
      sec.appendChild(row);
    }
    if (list.length > 5) sec.insertAdjacentHTML("beforeend", `<div class="chg-more">and ${list.length - 5} more</div>`);
    body.appendChild(sec);
  }
  if (alerts.length) body.insertAdjacentHTML("beforeend", `<div class="chg-sec"><h4>Alerts</h4><div class="chg-item"><span>${
    alerts.map(([k, n]) => `${n} ${esc(kinds[k] || k)}`).join(" · ")}</span></div></div>`);
}
document.getElementById("chgDays").onchange = () => loadChanges(true);

// ---- Network hygiene ----
// Findings from what BAMF already knows: the ports found open on each device
// (by any port scan, the port watch or Check now), the certificates on their
// HTTPS ports, routers that answered a UPnP search, and the ARP watch's
// alerts from the last week. Nothing here scans by itself; Check now does,
// when asked.
function loadSecurity() {
  return fetch("/api/security").then(r => r.ok ? r.json() : null).then(sec => { if (sec) securityCache = sec; return sec; }).catch(() => null);
}
const HY_ORDER = { high: 0, medium: 1, low: 2 };
let hygieneAll = false;
const HY_NOUN = { router: "a router", switch: "a switch", ap: "an access point", camera: "a camera", printer: "a printer", tv: "a TV",
  speaker: "a speaker", phone: "a phone", tablet: "a tablet", game: "a game console", iot: "a smart home device", light: "a light", plug: "a smart plug",
  streamer: "a streaming box", soundbar: "a soundbar", display: "a smart display", projector: "a projector", doorbell: "a doorbell", dome: "a camera",
  thermostat: "a thermostat", lock: "a smart lock", garage: "a garage door opener", sprinkler: "a sprinkler controller", vacuum: "a robot vacuum",
  fridge: "a fridge", washer: "an appliance", purifier: "an air purifier", ac: "an air conditioner", charger: "an EV charger", car: "a car",
  vr: "a VR headset", watch: "a watch", handheld: "a handheld console", ereader: "an e-reader", printer3d: "a 3D printer", modem: "a modem", mesh: "a mesh node" };
function hygieneFindings() {
  const out = [];
  const add = (sev, h, title, fix) => out.push({ sev, h, title, fix });
  const live = hosts.filter(h => !h.remote && !h.ignored && !h.forgotten);
  for (const h of live) {
    const p = new Set(h.openPorts || []);
    if (!p.size) continue;
    const kind = deviceKind(h);
    if (p.has(23)) add("high", h, "Telnet is open (port 23)",
      "Telnet sends its password in plain text. Turn it off and use SSH or the device's web page instead.");
    if (p.has(21)) add("medium", h, "FTP is open (port 21)",
      "FTP sends its password in plain text. Use SFTP or file sharing instead, or turn it off if nothing uses it.");
    if (p.has(5900)) add("medium", h, "VNC is open (port 5900)",
      "VNC is often unencrypted and weakly protected. Give it a strong password, or reach it over SSH or a VPN.");
    if (p.has(3389)) add(["desktop", "laptop", "server", "vm", "minipc", "pi"].includes(kind) ? "low" : "medium", h, "Remote Desktop is open (port 3389)",
      "Fine if you use it. If not, turn it off; if you do, keep Network Level Authentication on.");
    if ((p.has(445) || p.has(139)) && !["nas", "server", "desktop", "laptop", "vm", "minipc", "pi"].includes(kind))
      add("medium", h, `File sharing (SMB) is open on ${HY_NOUN[kind] || "a device that isn't a computer"}`,
        "Usually only computers and file servers share files. Check what this one shares, and turn it off if nothing needs it.");
    const web = p.has(80) || p.has(8080), tls = p.has(443) || p.has(8443) || p.has(5001);
    if (web && !tls) {
      const admin = ["router", "switch", "ap", "camera", "printer", "nas", "modem", "mesh", "doorbell", "dome", "printer3d"].includes(kind);
      add(admin ? "medium" : "low", h, admin ? "Settings page over plain HTTP only" : "Web page over plain HTTP only",
        admin ? "Its settings page has no HTTPS, so the password you log in with crosses the network readable. Turn on HTTPS if it has it."
              : "Anything typed into it crosses the network readable. Fine for a status page; not for a login.");
    }
    if (p.has(1883)) add("low", h, "Unencrypted MQTT (port 1883)",
      "Messages, and any MQTT password, cross the network readable. Use port 8883 with TLS if the broker supports it.");
  }
  const sec = securityCache || {};
  for (const c of sec.certs || []) {
    const h = hosts.find(x => x.id === c.hostId);
    if (!h || h.remote || !(h.openPorts || []).includes(c.port) || !c.notAfter) continue;
    const days = Math.floor((new Date(c.notAfter) - Date.now()) / 864e5);
    const date = new Date(c.notAfter).toLocaleDateString([], { year: "numeric", month: "short", day: "numeric" });
    if (days < 0) add("high", h, `Certificate expired on port ${c.port}`,
      `${c.subject || "It"} expired on ${date}. Browsers warn, and anything that checks certificates refuses to connect.`);
    else if (days <= 30) add(days <= 7 ? "high" : "medium", h, `Certificate expires in ${days} day${days === 1 ? "" : "s"} on port ${c.port}`,
      `${c.subject || "It"} runs out on ${date}. Renew it before then.`);
  }
  for (const u of (sec.upnp && sec.upnp.devices) || []) {
    const h = hosts.find(x => x.id === u.hostId) || { ip: u.ip };
    add("high", h, "UPnP port forwarding is on",
      "Any device on your network can open ports on this router to the internet without asking. Turn UPnP off in the router unless a game console or app needs it.");
  }
  const gr = greynoiseCache && greynoiseCache.enabled && greynoiseCache.result;
  if (gr && gr.noise)
    add("high", null, `Your public address ${gr.ip} has been seen scanning the internet`,
      "Something behind it may be infected: often a camera, NAS or router in a botnet. Check the traffic monitor's top talkers, and update or reset anything you don't recognise. With a shared provider address it may be a neighbour's.");
  const week = Date.now() - 7 * 864e5;
  for (const a of (alertsCache || []).filter(a => a.kind === "security" && new Date(a.at) > week))
    add("high", null, a.title, a.detail);
  return out.sort((a, b) => HY_ORDER[a.sev] - HY_ORDER[b.sev]);
}
function loadGreyNoise() {
  return fetch("/api/greynoise").then(r => r.ok ? r.json() : null).then(g => { if (g) greynoiseCache = g; return g; }).catch(() => null);
}
// One line about the daily GreyNoise check, for the hygiene card and Settings.
function greyNoiseLine() {
  const g = greynoiseCache;
  if (!g || !g.enabled) return { text: "The daily check is off: switch it on in Settings → Security to have BAMF look once a day.", bad: false };
  const r = g.result;
  if (!r) return { text: "Daily GreyNoise check: on, not run yet.", bad: false };
  if (r.error) return { text: `Daily GreyNoise check: ${r.error} (${fmtAgo(r.checkedAt)})`, bad: false };
  return r.noise
    ? { text: `Daily GreyNoise check: ${r.ip} HAS been seen scanning the internet${r.lastSeen ? ", last on " + r.lastSeen : ""}. Checked ${fmtAgo(r.checkedAt)}.`, bad: true }
    : { text: `Daily GreyNoise check: ${r.ip} not seen scanning the internet. Checked ${fmtAgo(r.checkedAt)}.`, bad: false };
}
function renderHygiene() {
  const body = $("hygieneBody");
  if (!body) return;
  const gl = greyNoiseLine();
  $("gnStatus").textContent = gl.text;
  $("gnStatus").classList.toggle("bad", gl.bad);
  const list = hygieneFindings();
  const sec = securityCache || {};
  const known = hosts.filter(h => !h.remote && (h.openPorts || []).length).length;
  const counts = ["high", "medium", "low"].map(sv => [sv, list.filter(f => f.sev === sv).length]).filter(([, n]) => n);
  $("hygieneSub").textContent = (counts.length ? counts.map(([sv, n]) => `${n} ${sv}`).join(" · ") + ". " : "")
    + (known ? `From the open ports BAMF knows on ${known} device${known === 1 ? "" : "s"}` : "No open ports known yet")
    + (sec.checkedAt ? `; last checked ${fmtAgo(sec.checkedAt)}.` : ". Never checked.");
  body.innerHTML = "";
  if (!list.length) {
    body.innerHTML = `<div class="watch-note">${known ? "Nothing to fix among the ports, certificates and gateways BAMF knows about." : "Nothing to look over yet: no port scan has found anything open."}</div>`;
    return;
  }
  const shown = hygieneAll ? list : list.slice(0, 8);
  for (const f of shown) {
    const row = document.createElement("div");
    row.className = `hy-item ${f.sev}` + (f.h && f.h.id ? " link" : "");
    const who = f.h ? `<span class="who">${esc(nameOrIp(f.h))}${f.h.ip && nameOrIp(f.h) !== f.h.ip ? " · " + esc(f.h.ip) : ""}</span>` : "";
    row.innerHTML = `<span class="hy-sev">${f.sev}</span><div><b>${esc(f.title)}</b>${who}<div class="det">${esc(f.fix)}</div></div>`;
    if (f.h && f.h.id) { row.title = "Show this device"; row.onclick = () => jumpToHost(f.h.id); }
    body.appendChild(row);
  }
  if (list.length > 8) {
    const more = document.createElement("button");
    more.type = "button";
    more.className = "linkish hy-more";
    more.textContent = hygieneAll ? "Show the worst eight" : `Show all ${list.length}`;
    more.onclick = () => { hygieneAll = !hygieneAll; renderHygiene(); };
    body.appendChild(more);
  }
}
// The health check, from the Scan menu: the common ports of every online known
// device, a UPnP search for the router, and every HTTPS certificate.
let healthRunning = false;
async function runHealthCheck() {
  if (healthRunning) return;
  healthRunning = true;
  toast("Health check started: about a minute");
  try {
    const r = await fetch("/api/security/check", { method: "POST" });
    if (r.status === 409) toast("A health check is already running");
    else if (!r.ok) throw new Error("HTTP " + r.status);
    else {
      securityCache = await r.json();
      await refresh();
      const n = hygieneFindings().filter(f => f.sev === "high" || f.sev === "medium").length;
      toast(n ? `Health check done: ${n} thing${n === 1 ? "" : "s"} to look at under Activity` : "Health check done: nothing to fix");
    }
  } catch (e) { console.error(e); toast("The health check failed - see the server log"); }
  finally { healthRunning = false; renderHygiene(); }
}

function renderTrafficCards() {
  const wrap = $("actCards");
  renderAlertsCard();
  const t = trafficCache;
  if (!t) { wrap.hidden = true; return; }
  wrap.hidden = false;
  const st = t.status || {};
  const talkers = $("talkers");
  talkers.innerHTML = "";
  const top = (t.top || []).filter(x => x.rxTotal + x.txTotal > 0);
  const counting = (t.sources || []).filter(s => s.ok);
  const also = counting.length ? `Counted by ${counting.map(s => s.name + (s.kind === "switch" ? " (switch)" : " (router)")).join(", ")}` : "";
  $("talkersSub").textContent = (counting.length && (!st.available || !st.enabled || !st.running)) ? also + "."
    : !st.available ? "Needs the Npcap driver, which isn't installed."
    : !st.enabled ? "Off. Turn on the traffic monitor in Settings → Scanning."
    : !st.running ? "Not running" + (st.error ? ": " + st.error : ".")
    : `Since ${fmtAgo(st.since).replace(" ago", "")} ago on ${(st.interfaces || []).join(", ")} · ${Number(st.frames || 0).toLocaleString()} frames` + (also ? " \u00b7 " + also : "");
  const max = Math.max(1, ...top.map(x => x.rxTotal + x.txTotal));
  for (const x of top) {
    const row = document.createElement("div");
    row.className = "talker";
    const smax = Math.max(1, ...x.strip);
    row.innerHTML = `<span class="nm" title="${esc(x.mac)}${x.cards > 1 ? ` and ${x.cards - 1} more network card${x.cards > 2 ? "s" : ""}, added together` : ""}">${esc(x.name)}<small>${esc(x.ip || x.mac)}${x.cards > 1 ? ` \u00b7 ${x.cards} cards` : ""}</small></span>` +
      `<span class="amt" title="In ${fmtBytes(x.rxTotal)} · out ${fmtBytes(x.txTotal)}"><b>${fmtBytes(x.rxTotal + x.txTotal)}</b><br>${fmtRate(x.rx + x.tx)}</span>` +
      `<span class="strip" title="${x.source && x.source !== "capture" ? `Bytes per minute over the last half hour, from ${esc(x.where || x.source)}` : "Bytes per ten seconds over the last five minutes"}">${x.strip.map(v => `<i style="height:${Math.max(4, Math.round(100 * v / smax))}%"></i>`).join("")}</span>`;
    if (x.hostId) { row.style.cursor = "pointer"; row.onclick = () => jumpToHost(x.hostId); }
    talkers.appendChild(row);
  }
  if ((st.running || counting.length) && !top.length) talkers.innerHTML = `<div class="watch-note">Nothing counted yet.</div>`;
  $("talkersNote").textContent = counting.length
    ? "Devices a switch or the router counts show everything they send and get. The rest are what this machine can see: on a switched network, its own traffic plus broadcast."
    : st.running
    ? "What this machine can see. On a switched network: its own traffic, plus broadcast and multicast, so the DHCP and DNS watch covers this machine and broadcast offers. On a mirrored (SPAN) switch port: everything, every device included. A managed switch's Counters (Settings → Your network) count every device without one."
    : "";
  renderServices();
}

function renderFeed() {
  renderSites();
  renderTrafficCards();
  const items = feedCache.filter(e => network === "all" || e.subnet === network);
  const wrap = $("feed");
  wrap.innerHTML = "";
  const fe = $("feedEmpty");
  fe.hidden = items.length > 0;
  if (items.length === 0)
    fe.innerHTML = loadedOnce
      ? "No activity yet. Device online/offline events will appear here as they happen."
      : `<span class="empty-scan">Scanning your network…</span>`;
  let lastDay = "";
  for (const e of items) {
    const d = new Date(e.at);
    const day = d.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" });
    if (day !== lastDay) {
      lastDay = day;
      const hdr = document.createElement("div");
      hdr.className = "feed-day";
      hdr.textContent = day;
      wrap.appendChild(hdr);
    }
    const row = document.createElement("div");
    row.className = "feed-item";
    const on = e.type === "online";
    row.innerHTML = `
      <span class="dot ${on ? "on" : "off"}"></span>
      <span class="who">${esc(e.name)}</span>
      <span class="what">${on ? "came online" : "went offline"}</span>
      <span class="meta"><span>${esc(e.ip)}</span><span>${esc(e.subnet)}</span><span>${d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span></span>`;
    wrap.appendChild(row);
  }
}

async function loadEvents(id) {
  const r = await fetch(`/api/hosts/${id}/events`);
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}
