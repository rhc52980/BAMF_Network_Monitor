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

// When a device is usually online, from its History panel: seven rows of
// 24 hours, darker the more of that hour it was online.
function buildPresence(p) {
  if (!p || !p.grid || !p.grid.some(row => row.some(v => v !== null))) return "";
  const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
  const hour = h => new Date(2000, 0, 1, h).toLocaleTimeString([], { hour: "numeric" });
  let html = `<div class="history-sub">When it's online<span class="pz-note">last ${p.weeks} weeks, by hour, in the server's time</span></div><div class="pz"><span></span>`;
  for (let h = 0; h < 24; h++) html += `<span class="hr">${h % 6 === 0 ? esc(hour(h)) : ""}</span>`;
  p.grid.forEach((row, d) => {
    html += `<span class="d">${days[d]}</span>`;
    row.forEach((v, h) => {
      const tip = `${days[d]} ${hour(h)}: ${v === null ? "not seen yet" : `online ${Math.round(v * 100)}% of the time`}`;
      html += v === null ? `<i class="na" title="${esc(tip)}"></i>` : `<i style="--v:${v}" title="${esc(tip)}"></i>`;
    });
  });
  return html + `</div>`;
}

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
  speaker: "a speaker", phone: "a phone", tablet: "a tablet", game: "a game console", iot: "a smart home device", light: "a light", plug: "a smart plug" };
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
    if (p.has(3389)) add(["desktop", "laptop", "server", "vm"].includes(kind) ? "low" : "medium", h, "Remote Desktop is open (port 3389)",
      "Fine if you use it. If not, turn it off; if you do, keep Network Level Authentication on.");
    if ((p.has(445) || p.has(139)) && !["nas", "server", "desktop", "laptop", "vm"].includes(kind))
      add("medium", h, `File sharing (SMB) is open on ${HY_NOUN[kind] || "a device that isn't a computer"}`,
        "Usually only computers and file servers share files. Check what this one shares, and turn it off if nothing needs it.");
    const web = p.has(80) || p.has(8080), tls = p.has(443) || p.has(8443) || p.has(5001);
    if (web && !tls) {
      const admin = ["router", "switch", "ap", "camera", "printer", "nas"].includes(kind);
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
  const body = $("watchBody");
  const trust = (kind, ip, trusted) => `<button type="button" class="toggle" data-kind="${kind}" data-ip="${esc(ip)}" data-trusted="${trusted ? 0 : 1}" title="${trusted ? "Forget this server, so it alerts if seen again" : "Trust this server, so it never alerts"}">${trusted ? "Forget" : "Trust"}</button>`;
  let html = "";
  html += `<div class="sub" style="margin-top:8px">DHCP servers</div><div class="watch-list">` +
    ((t.dhcp || []).map(d => `<div class="watch-row"><span class="mono">${esc(d.ip)}</span><span class="meta">${esc(d.name && d.name !== d.mac ? d.name + " · " : "")}${esc(d.mac)} · ${d.offers} offer${d.offers === 1 ? "" : "s"} · last ${esc(fmtAgo(d.lastSeen))}</span>${trust("dhcp", d.ip, d.trusted)}</div>`).join("")
      || `<div class="watch-note">${st.running ? "No DHCP offer seen yet. One shows up the next time a device asks for an address." : "Not watching."}</div>`) + `</div>`;
  html += `<div class="sub" style="margin-top:10px">DNS servers</div><div class="watch-list">` +
    ((t.dns || []).map(d => `<div class="watch-row"><span class="mono">${esc(d.ip)}</span><span class="meta">${esc(d.name ? d.name + " · " : "")}${d.clients} device${d.clients === 1 ? "" : "s"} · ${Number(d.queries).toLocaleString()} queries · last ${esc(fmtAgo(d.lastSeen))}</span>${trust("dns", d.ip, d.trusted)}</div>`).join("")
      || `<div class="watch-note">${st.running ? "No DNS query seen yet." : "Not watching."}</div>`) + `</div>`;
  body.innerHTML = html;
  body.querySelectorAll("button[data-kind]").forEach(b => b.onclick = async () => {
    await fetch("/api/traffic/trust", { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ kind: b.dataset.kind, ip: b.dataset.ip, trusted: b.dataset.trusted === "1" }) });
    toast(b.dataset.trusted === "1" ? `${esc(b.dataset.ip)} is trusted: it won't alert` : `${esc(b.dataset.ip)} forgotten: it alerts if seen again`);
    try { trafficCache = await loadTraffic(); } catch { }
    renderTrafficCards();
  });
}

function renderFeed() {
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
async function loadLatency(id) {
  const r = await fetch(`/api/hosts/${id}/latency?hours=24`);
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}
// The last 24 hours of round-trip times as a small chart, with a red tick
// where an echo went unanswered.
function buildLatencyChart(samples) {
  if (!samples.length) return latencyProbe ? "" : `<div class="lat-sum">Latency isn't measured: turn on <b>Measure latency</b> in Settings → Scanning.</div>`;
  const W = 640, H = 56, pad = 4;
  const vals = samples.map(x => x.ms).filter(v => v != null);
  const max = Math.max(10, ...vals), min = vals.length ? Math.min(...vals) : 0;
  const avg = vals.length ? Math.round(vals.reduce((a, b) => a + b, 0) / vals.length) : null;
  const x = i => pad + (samples.length === 1 ? 0 : i / (samples.length - 1) * (W - 2 * pad));
  const y = v => H - pad - (v / max) * (H - 2 * pad);
  let d = "", misses = "";
  samples.forEach((sm, i) => {
    if (sm.ms == null) { misses += `<line class="miss" x1="${x(i).toFixed(1)}" y1="${pad}" x2="${x(i).toFixed(1)}" y2="${H - pad}"/>`; return; }
    d += (d ? "L" : "M") + `${x(i).toFixed(1)} ${y(sm.ms).toFixed(1)}`;
  });
  const first = samples.findIndex(sm => sm.ms != null), last = samples.length - 1 - [...samples].reverse().findIndex(sm => sm.ms != null);
  const fill = d ? d + `L${x(last).toFixed(1)} ${H - pad}L${x(first).toFixed(1)} ${H - pad}Z` : "";
  return `<div class="lat-sum">Latency, last 24 h: ` + (avg != null ? `<b>${avg} ms</b> average · ${min}–${Math.round(max)} ms` : "no replies") +
    (samples.length - vals.length ? ` · <b>${samples.length - vals.length}</b> unanswered` : "") + `</div>` +
    `<svg class="lat-spark" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-label="Latency over the last 24 hours">` +
    (fill ? `<path class="fill" d="${fill}"/>` : "") + (d ? `<path class="line" d="${d}"/>` : "") + misses + `</svg>`;
}
async function loadIps(id) {
  const r = await fetch(`/api/hosts/${id}/ips`);
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}

// Renders a 24h bar: green segments where the host was online.
function buildTimeline(sessions, onlineNow) {
  const now = Date.now();
  const start = now - 24 * 36e5;
  // collect [from,to] overlapping the window, oldest first
  const spans = [];
  for (const s of [...sessions].reverse()) {
    const a = new Date(s.from).getTime();
    const b = s.to === null ? now : new Date(s.to).getTime();
    if (b < start) continue;
    spans.push([Math.max(a, start), Math.min(b, now)]);
  }
  let cells = "", cursor = start;
  const pct = ms => ((ms / (24 * 36e5)) * 100).toFixed(2);
  for (const [a, b] of spans) {
    if (a > cursor) cells += `<div style="width:${pct(a - cursor)}%"></div>`;
    cells += `<div class="seg-on" style="width:${pct(b - a)}%" title="${fmtExact(new Date(a).toISOString())} \u2192 ${fmtExact(new Date(b).toISOString())}"></div>`;
    cursor = b;
  }
  if (cursor < now) cells += `<div style="width:${pct(now - cursor)}%"></div>`;
  return `<div class="timeline">${cells}</div>
    <div class="timeline-lbl"><span>24 h ago</span><span>now</span></div>`;
}

// Pair events (newest-first) into sessions: online..offline spans.
function buildSessions(events) {
  const asc = [...events].reverse(); // oldest first
  const sessions = [];
  let openAt = null;
  for (const e of asc) {
    if (e.type === "online") {
      openAt = e.at;
    } else if (e.type === "offline" && openAt) {
      sessions.push({ from: openAt, to: e.at });
      openAt = null;
    }
  }
  if (openAt) sessions.push({ from: openAt, to: null }); // still online
  return sessions.reverse(); // newest first
}

const $ = id => document.getElementById(id);

function fmtAgo(iso) {
  if (!iso) return "—";
  const s = Math.max(0, (Date.now() - new Date(iso)) / 1000);
  if (s < 90) return "just now";
  if (s < 3600) return Math.round(s / 60) + " min ago";
  if (s < 86400) return Math.round(s / 3600) + " h ago";
  return Math.round(s / 86400) + " d ago";
}

function esc(s) {
  return String(s).replace(/[&<>"']/g, c =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function loadHosts() {
  const r = await fetch("/api/hosts");
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}

async function setKnown(host, known) {
  await fetch(`/api/hosts/${host.id}/known`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ known }),
  });
}

async function setName(host, name) {
  await fetch(`/api/hosts/${host.id}/name`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name }),
  });
}

async function setNote(host, note) {
  await fetch(`/api/hosts/${host.id}/note`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ note }),
  });
}

async function setIgnored(host, ignored) {
  await fetch(`/api/hosts/${host.id}/ignore`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ignored }),
  });
}

async function forgetHost(host, forgotten) {
  await fetch(`/api/hosts/${host.id}/forget`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ forgotten }),
  });
}
async function deleteHostPermanent(host) {
  await fetch(`/api/hosts/${host.id}`, { method: "DELETE" });
}

function inNetwork(h) {
  return network === "all" ? !h.remote : onNet(h, network);
}

// The addresses a device answers on now, main first. Usually one; a router
// with an address on each network, or a server with a second IP, has more.
function curAddrs(h) {
  const list = (h.addresses || []).filter(a => a.current);
  return list.length ? list : [{ ip: h.ip, subnet: h.subnet, linkUrl: h.linkUrl, lastSeen: h.lastSeen, current: true }];
}
function alsoAt(h) { return curAddrs(h).filter(a => a.ip !== h.ip); }

// Network cards you combined into one device. Each card is its own record on
// the server; here they fold into the device they belong to, which answers on
// all their addresses, carries all their MACs, and is online if any card is.
function foldInterfaces(list) {
  const byId = new Map(list.map(h => [h.id, h]));
  const cards = list.filter(h => h.interfaceOf && byId.has(h.interfaceOf) && !h.forgotten && !byId.get(h.interfaceOf).forgotten);
  if (!cards.length) return list;
  const out = list.filter(h => !cards.includes(h)).map(h => ({ ...h }));
  const outById = new Map(out.map(h => [h.id, h]));
  const own = h => (h.addresses && h.addresses.length ? h.addresses
    : [{ ip: h.ip, subnet: h.subnet, linkUrl: h.linkUrl, lastSeen: h.lastSeen, current: true }]);
  for (const k of cards) {
    const d = outById.get(k.interfaceOf);
    if (!d) continue;
    if (!d.interfaces) { d.interfaces = []; d.macs = [d.mac]; d.addresses = own(d); }
    d.interfaces.push(k);
    d.macs.push(k.mac);
    d.addresses = [...d.addresses, ...own(k).filter(a => !d.addresses.some(x => x.ip === a.ip))
      .map(a => ({ ...a, mac: k.mac, cardId: k.id }))];
    d.online = d.online || k.online;
    if (k.traffic) d.traffic = d.traffic ? { rx: d.traffic.rx + k.traffic.rx, tx: d.traffic.tx + k.traffic.tx, rxTotal: d.traffic.rxTotal + k.traffic.rxTotal, txTotal: d.traffic.txTotal + k.traffic.txTotal } : k.traffic;
    if (k.dns && k.dns.length) d.dns = [...new Set([...(d.dns || []), ...k.dns])];
    if ((k.lastSeen || "") > (d.lastSeen || "")) d.lastSeen = k.lastSeen;
  }
  return out;
}
// Every MAC a device has: its own, and its combined cards'.
function macsOf(h) { return h.macs || [h.mac]; }
function onNet(h, net) { return h.subnet === net || curAddrs(h).some(a => a.subnet === net); }
// The device as one network sees it, at its address there. For the Map.
function onNetView(h, net) {
  if (h.subnet === net) return h;
  const a = curAddrs(h).find(x => x.subnet === net);
  return a ? { ...h, ip: a.ip, subnet: net, linkUrl: a.linkUrl, mainIp: h.ip } : h;
}
// More than a handful of addresses on one MAC is rarely a real device; it's
// usually a router answering for others (proxy ARP).
const PROXY_ARP_HINT = 6;
function addrMenuHtml(h) {
  const list = curAddrs(h);
  const rows = list.map(a => `<div class="addr-row">
      <a class="mono" href="${esc(a.linkUrl || ("http://" + a.ip))}" target="_blank" rel="noopener" title="Open ${esc(a.linkUrl || ("http://" + a.ip))}">${esc(a.ip)}</a>
      <span class="addr-net">${esc(a.subnet || "—")}${a.mac ? `<br><span class="mono" title="On a network card you combined into this device">${esc(a.mac)}</span>` : ""}</span>
      <span class="addr-seen">${a.ip === h.ip ? "main" : "seen " + fmtAgo(a.lastSeen)}${declaredGws.some(g => g.hostId === h.id && g.ip === a.ip) ? " · gateway" : ""}</span>
    </div>`).join("");
  return `<div class="addr-head">Answers on ${list.length} addresses${h.interfaces ? ` · ${h.interfaces.length + 1} network cards` : ""}</div>${rows}` +
    (list.length >= PROXY_ARP_HINT
      ? `<div class="addr-note">That many usually means proxy ARP: a router answering on behalf of other devices.</div>` : "");
}

function dispName(h) {
  // After a custom name and a resolved hostname come the name the router
  // knows it by (when router import is set up), then one it announced over mDNS.
  return h.customName || (h.hostname && h.hostname !== "—" ? h.hostname : "") || h.routerName || h.mdnsName || "—";
}

// A device guess reads "Apple device (vendor)" or "Windows (TTL 128, SMB)":
// the name, then the evidence it rests on. The name alone is the type, and
// grouping by it costs nothing - no new taxonomy, no second list to maintain.
// Devices with no guess share the null family and get their own chip.
// The types a user can set on a device, each with its Map icon. Same keys as the server's.
const TYPE_DEFS = [
  ["router", "Router"], ["switch", "Switch"], ["ap", "Access point"], ["camera", "Camera"], ["printer", "Printer"],
  ["tv", "TV / media"], ["speaker", "Speaker"], ["phone", "Phone"], ["tablet", "Tablet"], ["laptop", "Laptop"],
  ["desktop", "Desktop"], ["server", "Server"], ["nas", "NAS"], ["vm", "Virtual machine"], ["game", "Game console"],
  ["iot", "Smart home"], ["light", "Light"], ["plug", "Smart plug"], ["device", "Other"],
];
function typeLabel(key) { const d = TYPE_DEFS.find(([k]) => k === key); return d ? d[1] : key; }
// A device's type family for the type chips: the device type the user wrote, or the guess.
function typeFamily(h) { return h.typeName || guessFamily(h.osGuess); }

function guessFamily(osGuess) {
  if (!osGuess) return null;
  const cut = osGuess.indexOf(" (");
  return (cut > 0 ? osGuess.slice(0, cut) : osGuess).trim() || null;
}

function visible(opts) {
  if (view === "home" || view === "activity") return [];
  const ignoreGuess = !!(opts && opts.ignoreGuess);
  return hosts.filter(h => {
    if (!inNetwork(h)) return false;
    if (!ignoreGuess && guessFilter !== null && typeFamily(h) !== (guessFilter === NO_GUESS ? null : guessFilter)) return false;
    if (!(opts && opts.ignoreTag) && !tagOk(h)) return false;
    if (view === "forgotten") { if (!h.forgotten) return false; }
    else {
      if (h.forgotten) return false;
      if (filter === "ignored") { if (!h.ignored) return false; }
      else {
        if (h.ignored) return false;
        if (filter === "online" && !h.online) return false;
        if (filter === "offline" && h.online) return false;
        if (filter === "unknown" && h.known) return false;
        if (filter === "new" && !isNew(h)) return false;
      }
    }
    return matchesQuery(h, query);
  });
}

// Plain substring by default. If the query contains * or ?, treat it as a glob
// and match it against each field on its own — so "*.245" finds any IP ending
// in .245, and "192.168.2.*" finds a whole subnet.
function matchesQuery(h, query) {
  if (!query) return true;
  const fields = [h.customName, h.hostname, h.mdnsName, h.routerName, h.mdnsServices, h.ip, h.mac, h.vendor, h.note, h.osGuess, ...(h.tags || []),
    h.typeName, ...(h.addresses || []).map(a => a.ip), ...(h.ipv6 || [])].filter(Boolean);
  if (/[*?]/.test(query)) {
    let rx;
    try {
      const pattern = [...query].map(c =>
        c === "*" ? ".*" : c === "?" ? "." : c.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")
      ).join("");
      rx = new RegExp("^" + pattern + "$", "i");
    } catch { return false; }
    return fields.some(f => rx.test(String(f)));
  }
  return fields.join(" ").toLowerCase().includes(query.toLowerCase());
}

// Sentinel for "devices BAMF hasn't guessed at", which is a type worth
// filtering to - it's the list of things to point Identify at.
const NO_GUESS = "\u0000none";

function renderGuessChips() {
  const bar = $("guessChips");
  // Counted from what the network and status filters already leave, but
  // before this filter - so the numbers stay put as you move between chips
  // instead of collapsing to one row the moment you pick something.
  const pool = visible({ ignoreGuess: true });
  const counts = new Map();
  for (const h of pool) {
    const key = typeFamily(h) ?? NO_GUESS;
    counts.set(key, (counts.get(key) || 0) + 1);
  }

  if (!(view === "devices" || view === "forgotten")) {
    bar.hidden = true;
    bar.innerHTML = "";
    return;
  }

  // The chosen type can vanish - the last iPhone goes offline and is filtered
  // out, or you switch to a network without one. Drop back to every type
  // rather than showing an empty table with no way to tell why.
  if (guessFilter !== null && !counts.has(guessFilter)) guessFilter = null;

  // A chip bar with one chip says nothing the table doesn't - unless a chip is
  // in use, in which case hiding the bar would strand the filter with nothing
  // on screen to explain or undo it.
  if (counts.size < 2 && guessFilter === null) {
    bar.hidden = true;
    bar.innerHTML = "";
    return;
  }

  const families = [...counts.entries()]
    .sort((a, b) => (a[0] === NO_GUESS) - (b[0] === NO_GUESS) || b[1] - a[1] || a[0].localeCompare(b[0]));

  bar.hidden = false;
  bar.innerHTML = "";
  const label = document.createElement("span");
  label.className = "chipbar-label";
  label.textContent = "Device type";
  bar.appendChild(label);

  const mk = (key, text, count, title) => {
    const b = document.createElement("button");
    const active = guessFilter === key;
    b.type = "button";
    b.className = "chip-btn" + (active ? " active" : "") + (key === NO_GUESS ? " none" : "");
    b.setAttribute("aria-pressed", active ? "true" : "false");
    if (title) b.title = title;
    b.textContent = text;
    if (count !== null) {
      const c = document.createElement("span");
      c.className = "chip-cnt";
      c.textContent = count;
      b.appendChild(c);
    }
    // Clicking the chip already in use clears it, so the filter never needs
    // hunting for an off switch.
    b.onclick = () => { guessFilter = (key !== null && guessFilter === key) ? null : key; render(); };
    bar.appendChild(b);
  };

  mk(null, "All", pool.length, "Every device type");
  for (const [key, n] of families) {
    if (key === NO_GUESS) mk(NO_GUESS, "no guess", n, "Devices BAMF has no guess for - Identify one from its \u22ef menu");
    else mk(key, key, n, "Only " + key + " devices");
  }
}

// Tags in use, as a chip bar like the device-type one. Hidden until a tag exists.
function allTags() {
  const seen = new Map();
  for (const h of hosts) if (!h.forgotten) for (const t of h.tags || []) { const k = t.toLowerCase(); if (!seen.has(k)) seen.set(k, t); }
  return [...seen.values()].sort((a, b) => a.localeCompare(b));
}
function renderTagChips() {
  const bar = $("tagChips");
  const showTable = view === "devices" || view === "forgotten" || view === "map";
  const pool = showTable ? visible({ ignoreGuess: true, ignoreTag: true }) : [];
  const tags = allTags();
  if (!showTable || (tags.length === 0 && tagFilter === null)) { bar.hidden = true; bar.innerHTML = ""; return; }
  bar.hidden = false;
  bar.innerHTML = "";
  const label = document.createElement("span");
  label.className = "chipbar-label";
  label.textContent = "Tag";
  bar.appendChild(label);
  const mk = (key, text, count, title) => {
    const b = document.createElement("button");
    const active = tagFilter === key;
    b.type = "button";
    b.className = "chip-btn" + (active ? " active" : "");
    b.setAttribute("aria-pressed", active ? "true" : "false");
    if (title) b.title = title;
    b.textContent = text;
    if (count !== null) { const c = document.createElement("span"); c.className = "chip-cnt"; c.textContent = count; b.appendChild(c); }
    b.onclick = () => { tagFilter = (key !== null && tagFilter === key) ? null : key; render(); };
    bar.appendChild(b);
  };
  mk(null, "All", pool.length, "Every device");
  for (const t of tags) mk(t, t, pool.filter(h => hasTag(h, t)).length, "Only devices tagged " + t);
  if (tagFilter !== null && !tags.some(t => t.toLowerCase() === tagFilter.toLowerCase())) mk(tagFilter, tagFilter, 0, "No device has this tag any more");
}

// What a network's chip looks like, as opposed to what it says. Only a change
// here needs new DOM; counts and countdowns are just text on the existing one.
//   paused: switched off in Settings - devices keep their last known state
//   noif:   configured, but this machine has no interface on it, so a scan can
//           never find anything. No countdown: counting down to a scan that
//           does nothing misleads.
function netTabKind(value) {
  if (value === "all") return "all";
  if (scanModes[value] === "paused") return "paused";
  if (scanModes[value] === "skipped") return "noif";
  return subnetNextDue[value] ? "due" : "plain";
}

function netTabTitle(net, kind) {
  if (kind === "paused") return "Paused in Settings - not being scanned, devices keep their last known state";
  if (kind === "noif") return "No interface on this machine for this network, so nothing can be found here. Remove it from Bamf:Subnets, or add a NIC on it.";
  if (net === "all") return "";
  const bits = [];
  if (subnetIntervals[net]) bits.push("every " + subnetIntervals[net] + "s");
  if (scanModes[net]) bits.push("via " + scanModes[net]);
  return bits.join(" · ");
}

// Structure of the strip as last built. The whole strip used to be thrown away
// and rebuilt on every 10s poll, which with a dozen networks was a visible
// redraw - and worse, the rebuilt countdown slots came back empty, so the
// seconds vanished and reappeared on the next tick, reflowing every row a
// second after it had settled. The DOM is now rebuilt only when the networks
// or their states actually change.
let netTabSig = null;

function renderNetTabs() {
  // Networks = what the scanner reports, plus any subnet still present on stored hosts.
  const cidrKey = c => {
    // A remote site's network ("Cabin · 10.0.0.0/24") sorts after the local ones.
    if (c.includes(" \u00b7 ")) { const [site, cidr] = c.split(" \u00b7 "); return 1e15 + ([...site].reduce((a, ch) => a * 31 + ch.charCodeAt(0), 0) % 1e6) * 1e9 + (cidrKey(cidr) % 1e9); }
    const [ip, pfx] = c.split("/");
    const octets = ip.split(".").map(Number);
    // pack octets into a sortable number, then prefix as tiebreaker
    return octets.reduce((a, o) => a * 256 + (o || 0), 0) * 100 + (Number(pfx) || 0);
  };
  const all = [...new Set([...subnets, ...hosts.map(h => h.subnet).filter(Boolean)])]
    .sort((a, b) => cidrKey(a) - cidrKey(b));
  if (network !== "all" && !all.includes(network)) network = "all";

  const counted = hosts.filter(h => !h.ignored && !h.forgotten);
  const rows = [{ net: "all", label: "All networks", count: counted.filter(h => !h.remote).length }];
  for (const n of all)
    rows.push({ net: n, label: n, count: counted.filter(h => onNet(h, n)).length });
  for (const r of rows) r.kind = netTabKind(r.net);

  const wrap = $("nettabs");
  const sig = rows.map(r => r.net + "\u0001" + r.kind).join("\u0002");
  if (sig !== netTabSig) {
    netTabSig = sig;
    wrap.innerHTML = "";
    for (const r of rows) {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "nettab";
      b.dataset.net = r.net;
      const nm = document.createElement("span");
      nm.textContent = r.label;
      const cnt = document.createElement("span");
      cnt.className = "cnt";
      b.append(nm, cnt);
      if (r.net.includes(" \u00b7 ")) {
        const tag = document.createElement("span");
        tag.className = "remote-tag";
        const st = remoteStatus.find(x => r.net.startsWith(x.name + " \u00b7 "));
        tag.textContent = st && !st.ok ? "remote · stale" : "remote";
        b.appendChild(tag);
      } else if (r.kind === "paused" || r.kind === "noif") {
        const tag = document.createElement("span");
        tag.className = r.kind === "paused" ? "paused-tag" : "noif-tag";
        tag.textContent = r.kind === "paused" ? "paused" : "no interface";
        b.appendChild(tag);
      } else if (r.kind === "due") {
        const d = document.createElement("span");
        d.className = "due";
        d.dataset.dueNet = r.net;
        b.appendChild(d);
      }
      b.onclick = () => { network = r.net; render(); };
      wrap.appendChild(b);
    }
  }

  // Everything that changes without changing the structure. The countdown is
  // filled here as well as by the one-second ticker, so a strip that has just
  // been rebuilt already carries its seconds instead of showing a blank slot
  // until the next tick.
  rows.forEach((r, i) => {
    const b = wrap.children[i];
    if (!b) return;
    b.classList.toggle("active", network === r.net);
    b.classList.toggle("paused", r.kind === "paused" || r.kind === "noif");
    b.querySelector(".cnt").textContent = r.count;
    b.title = netTabTitle(r.net, r.kind);
    const d = b.querySelector(".due");
    if (d && subnetNextDue[r.net]) d.textContent = dueText(subnetNextDue[r.net]);
  });
}

const ipNum = ip => ip.split(".").reduce((a, o) => (a << 8) + (+o || 0), 0) >>> 0;

function applySort(rows) {
  if (!sortKey) return rows;
  const val = h => {
    switch (sortKey) {
      case "name":   return dispName(h).toLowerCase();
      case "ip":     return ipNum(h.ip);
      case "mac":    return h.mac;
      case "vendor": return (h.vendor || "").toLowerCase();
      case "subnet": return h.subnet || "";
      case "firstSeen": return h.firstSeen || "";
      case "lastSeen":  return h.lastSeen || "";
      case "known":  return h.known ? 1 : 0;
      case "online": return h.online ? 1 : 0;
      case "latency": return h.latencyMs == null ? 1e9 : h.latencyMs;
      case "traffic": return h.traffic ? -(h.traffic.rx + h.traffic.tx) : 1;
      default: return "";
    }
  };
  return [...rows].sort((a, b) => {
    const va = val(a), vb = val(b);
    return (va < vb ? -1 : va > vb ? 1 : 0) * sortDir;
  });
}

