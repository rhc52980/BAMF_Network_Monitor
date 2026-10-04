function renderHome() {
  // The board shows watched devices, or every device carrying one tag.
  const sel = $("homeGroup");
  const tags = allTags();
  const want = "" + (sel.value || "");
  sel.innerHTML = `<option value="">Watched devices</option>` + tags.map(t => `<option value="${esc(t)}">Tagged ${esc(t)}</option>`).join("");
  sel.value = tags.some(t => t === want) ? want : "";
  const group = sel.value;
  const watched = hosts.filter(x => (group ? hasTag(x, group) : x.watched) && !x.forgotten && !x.ignored && inNetwork(x));
  const grid = $("homeGrid");
  grid.innerHTML = "";
  const he = $("homeEmpty");
  he.hidden = watched.length > 0;
  if (watched.length === 0)
    he.innerHTML = !loadedOnce ? `<span class="empty-scan">Scanning your network…</span>`
      : group ? `No device is tagged ${esc(group)} on this network.`
      : "No watched devices yet. Tap the camera on a device to watch it — it'll show up here as home/away.";
  // online first, then by name
  watched.sort((a, b) => (b.online - a.online) || dispName(a).localeCompare(dispName(b)));
  for (const x of watched) {
    const card = document.createElement("div");
    card.className = "home-card" + (x.online ? " here" : "");
    card.innerHTML = `
      <div class="halo">${x.online ? "\u25cf" : "\u25cb"}</div>
      <div class="hn">${esc(dispName(x))}</div>
      <div class="hs">${x.online ? "home" : "away"}</div>`;
    grid.appendChild(card);
  }
}

function buildSpark(host) {
  const wrap = document.createElement("span");
  wrap.className = "spark";
  wrap.title = "Activity over the last 24 hours";
  // derive hourly online coverage from this host's events in the feed
  const now = Date.now();
  const events = feedCache
    .filter(e => e.hostId === host.id)
    .map(e => ({ type: e.type, t: new Date(e.at).getTime() }))
    .sort((a, b) => a.t - b.t);

  // reconstruct state across 24 buckets
  const buckets = new Array(24).fill(null);
  const start = now - 24 * 36e5;
  // starting state: if currently online and no events, assume on the whole time
  let state = host.online;
  // walk hours; for each hour see if any event flips within, else carry state
  // simpler: for each event set state; sample state at each hour boundary end
  let ei = 0;
  // find state at 'start' by replaying events before start
  for (const e of events) { if (e.t <= start) state = (e.type === "online"); }
  for (let hbar = 0; hbar < 24; hbar++) {
    const hEnd = start + (hbar + 1) * 36e5;
    let onDuring = state;
    while (ei < events.length && events[ei].t <= hEnd) {
      if (events[ei].t >= start && events[ei].type === "online") onDuring = true;
      state = (events[ei].type === "online");
      ei++;
    }
    buckets[hbar] = onDuring || state;
  }
  for (let i = 0; i < 24; i++) {
    const bar = document.createElement("i");
    const on = buckets[i];
    bar.className = on ? "on" : "";
    bar.style.height = on ? "14px" : "5px";
    wrap.appendChild(bar);
  }
  return wrap;
}

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
  ["iot", "Smart home"], ["light", "Light"], ["plug", "Smart plug"],
  ["streamer", "Streamer"], ["soundbar", "Soundbar"], ["display", "Smart display"], ["projector", "Projector"], ["doorbell", "Doorbell"],
  ["dome", "Dome camera"], ["thermostat", "Thermostat"], ["lock", "Smart lock"], ["garage", "Garage door"], ["sprinkler", "Sprinkler"],
  ["vacuum", "Robot vacuum"], ["fridge", "Fridge"], ["washer", "Washer / appliance"], ["purifier", "Air purifier"], ["ac", "Air conditioner"],
  ["charger", "EV charger"], ["car", "Car"], ["vr", "VR headset"], ["watch", "Watch"], ["handheld", "Handheld"], ["ereader", "E-reader"],
  ["printer3d", "3D printer"], ["modem", "Modem"], ["mesh", "Mesh node"], ["minipc", "Mini PC"], ["pi", "Raspberry Pi"], ["device", "Other"],
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

function render() {
  // The 10s auto-refresh rebuilds the table, which used to destroy any open ⋯
  // menu — it looked like the menu closed itself on a timer. Remember which
  // row's menu is open and reopen it after the rebuild.
  const openMenuRow = document.querySelector(".row-menu.show:not(.addr-menu)")?.closest("tr")?.dataset.id ?? null;
  const openAddrRow = document.querySelector(".addr-menu.show")?.closest("tr")?.dataset.id ?? null;
  renderNetTabs();
  renderBulk();
  const showTable = view === "devices" || view === "forgotten";
  $("tableWrap").hidden = !showTable;
  $("feedWrap").hidden = view !== "activity";
  $("homeWrap").hidden = view !== "home";
  $("floorWrap").hidden = view !== "floor";
  $("settingsWrap").hidden = view !== "settings";
  $("mapWrap").hidden = view !== "map";
  $("statusFilters").style.display = view === "devices" ? "" : "none";
  $("tableWrap").classList.toggle("bare", view === "forgotten");
  renderGuessChips();
  renderTagChips();
  const newCount = hosts.filter(h => inNetwork(h) && !h.ignored && !h.forgotten && isNew(h)).length;
  $("newCount").textContent = newCount;
  $("newCount").hidden = newCount === 0;
  if (view === "activity") {
    // The cards want the traffic answer; fetch it with each refresh while the tab is open.
    loadTraffic().then(t => { trafficCache = t; renderTrafficCards(); }).catch(() => {});
    fetch("/api/alerts").then(r => r.json()).then(a => { alertsCache = a; renderAlertsCard(); renderHygiene(); }).catch(() => {});
    loadSettingsLog();
    loadUnusual();
    loadSecurity().then(() => { renderHygiene(); renderServices(); });
    loadGreyNoise().then(() => { renderHygiene(); renderServices(); });
    loadWan(true).then(() => { renderWanCard(); renderServices(); });
    loadConnections().then(renderServices);
    loadSpeed().then(renderSpeed);
    loadChanges();
    loadIpv6();
    renderFeed();
  }
  if (view === "home") renderHome();
  if (view === "floor") renderFloor();
  if (view === "map") renderMap();
  document.querySelectorAll("thead th[data-sort]").forEach(th => {
    th.classList.toggle("sorted", th.dataset.sort === sortKey);
    th.classList.toggle("desc", th.dataset.sort === sortKey && sortDir === -1);
  });
  const scoped = hosts.filter(h => inNetwork(h) && !h.ignored && !h.forgotten);
  $("statTotal").textContent = scoped.length;
  $("statOnline").textContent = scoped.filter(h => h.online).length;
  $("statUnknown").textContent = scoped.filter(h => !h.known).length;
  $("statLast").textContent = lastScan
    ? new Date(lastScan).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : "—";
  // A paused network has no scan mode of its own; leave it out so one paused
  // network doesn't turn a perfectly uniform "ping sweep" into "mixed".
  const modeVals = [...new Set(Object.values(scanModes).filter(m => m !== "paused"))];
  $("statLastLbl").textContent = "Last scan" +
    (modeVals.length === 1 ? " · " + modeVals[0] : modeVals.length > 1 ? " · mixed" : "");

  const strip = $("portstrip");
  strip.innerHTML = "";
  scoped.forEach(h => {
    const b = document.createElement("button");
    b.className = "port " + (!h.known ? "warn" : h.online ? "on" : "");
    b.title = `${dispName(h)} — ${h.ip}`;
    b.setAttribute("aria-label", `${dispName(h)}, ${h.ip}, ${!h.known ? "unknown" : h.online ? "online" : "offline"}`);
    b.onclick = () => {
      const row = document.querySelector(`tr[data-id="${h.id}"]`);
      if (row) {
        row.scrollIntoView({ behavior: "smooth", block: "center" });
        row.style.background = "var(--panel-2)";
        setTimeout(() => row.style.background = "", 1200);
      }
    };
    strip.appendChild(b);
  });

  const rows = applySort(visible());
  const tb = $("tbody");
  tb.innerHTML = "";
  const emptyEl = $("empty");
  emptyEl.hidden = rows.length > 0;
  if (rows.length === 0) {
    if (!loadedOnce) emptyEl.innerHTML = `<span class="empty-scan">Scanning your network…</span>`;
    else if (view === "forgotten") emptyEl.textContent = "Nothing forgotten. Devices you Forget land here, ready to restore.";
    else if (query) emptyEl.textContent = `No devices match “${query}”.`;
    else if (filter === "unknown") emptyEl.textContent = "No unknown devices — you're all clear. ✓";
    else if (filter === "new") emptyEl.textContent = `Nothing new. A device shows here for ${newDays} day${newDays === 1 ? "" : "s"} after BAMF first sees it, until you mark it known.`;
    else if (filter === "ignored") emptyEl.textContent = "Nothing ignored. Randomized phone MACs and devices you hide show up here.";
    else if (filter === "offline") emptyEl.textContent = "Every known device is online right now.";
    else if (filter === "online") emptyEl.textContent = "No devices online at the moment.";
    else emptyEl.textContent = "No devices yet — the first scan will populate this shortly.";
  }
  rows.forEach(h => {
    const tr = document.createElement("tr");
    tr.dataset.id = h.id;
    if (!h.known && !h.ignored) tr.classList.add("is-unknown");
    if (h.ignored) tr.classList.add("is-ignored");
    // Under a custom name, show what the network calls the device: its resolved
    // hostname if there is one, else the name it announced over mDNS.
    const subName = (h.hostname && h.hostname !== "—") ? h.hostname : (h.mdnsName || "");
    const showDns = h.customName && subName && subName !== h.customName;
    tr.innerHTML = `
      <td class="led-cell">${h.remote ? "" : `<input type="checkbox" class="sel-box" aria-label="Select this device">`}<span class="led ${h.online ? "on" : ""}" title="${h.online ? "online" : "offline"}"></span></td>
      <td class="hostname" title="Click to rename">
        <span class="name-text">${esc(dispName(h))}</span>
        ${h.note ? `<span class="note-flag" title="${esc(h.note)}">\uD83D\uDCDD</span>` : ""}
        ${h.snoozedUntil ? `<span class="snooze-chip" title="Alerts snoozed until ${esc(fmtExact(h.snoozedUntil))}. Click to change.">\uD83D\uDCA4 ${esc(snoozeClock(h.snoozedUntil))}</span>` : ""}
        ${showDns ? `<span class="dns">${esc(subName)}</span>` : ""}
        ${(h.tags || []).length ? `<span class="tag-row">${h.tags.map(t => `<span class="tag-chip">${esc(t)}</span>`).join("")}</span>` : ""}
      </td>
      <td class="ip" data-label="IP"><a href="${esc(h.linkUrl || ("http://" + h.ip))}" target="_blank" rel="noopener" title="Open ${esc(h.linkUrl || ("http://" + h.ip))}${h.link ? " (custom link)" : ""}">${esc(h.ip)}${h.link ? '<span class="link-flag" title="Custom link">⇗</span>' : ""}</a>${alsoAt(h).length
        ? `<span class="more-wrap"><button class="ip-more" title="Also answers on ${esc(alsoAt(h).map(a => a.ip).join(", "))}">+${alsoAt(h).length}</button><div class="row-menu addr-menu">${addrMenuHtml(h)}</div></span>` : ""}${(h.ipv6 || []).length
        ? `<span class="v6-chip" title="IPv6: ${esc(h.ipv6.join(", "))}">v6</span>` : ""}</td>
      <td class="mac" data-label="MAC"><span class="mac-text">${esc(h.mac)}</span></td>
      <td class="vendor" data-label="Vendor">${esc(h.vendor)}${h.typeName
        ? `<span class="os-guess type-set" title="Device type you set${h.osGuess ? " — BAMF guessed " + esc(h.osGuess) : ""}. Click to change.">${esc(h.typeName)} · your type</span>`
        : h.osGuess ? `<span class="os-guess type-set" title="BAMF's guess, from ${esc(h.osGuess)}. Click to set the device type yourself.">${esc(h.osGuess)}</span>` : ""}</td>
      <td class="ip" data-label="Network">${esc(h.subnet || "—")}</td>
      <td class="seen" data-label="First seen" title="${esc(fmtExact(h.firstSeen))}">${fmtAgo(h.firstSeen)}</td>
      <td class="seen" data-label="Last seen" title="${esc(fmtExact(h.lastSeen))}">${fmtAgo(h.lastSeen)}</td>
      <td class="spark-cell" data-label="24h"></td>
      <td class="traf traffic-col${h.traffic && (h.traffic.rx || h.traffic.tx) ? "" : " quiet"}" data-label="Traffic" title="${h.traffic ? `In ${fmtBytes(h.traffic.rxTotal)} · out ${fmtBytes(h.traffic.txTotal)} ${trafficSince(h.traffic)}` : "Nothing seen from this device yet"}">${h.traffic ? `↓<b>${fmtRate(h.traffic.rx)}</b> ↑<b>${fmtRate(h.traffic.tx)}</b>` : "—"}</td>
      <td class="lat ${h.latencyMs == null ? "none" : h.latencyMs < 20 ? "fast" : h.latencyMs < 100 ? "" : h.latencyMs < 300 ? "slow" : "bad"}" data-label="Latency" title="${h.latencyMs == null ? (h.online ? "No reply to the last echo" : "Offline") : "Round-trip time of the last echo"}">${h.latencyMs == null ? (h.online ? "no reply" : "—") : h.latencyMs + " ms"}</td>
      <td class="status-cell"><span class="badge ${h.known ? "known" : "unknown"}">${h.known ? "known" : "unknown"}</span></td>
      <td class="actions-cell"></td>`;

    const typeLine = tr.querySelector(".type-set");
    if (typeLine && !h.remote && role !== "viewer") typeLine.onclick = () => openKindDialog(h);
    const nameCell = tr.querySelector(".hostname");
    if (h.remote) tr.classList.add("is-remote");
    const snoozeChip = nameCell.querySelector(".snooze-chip");
    if (snoozeChip) snoozeChip.onclick = e => { e.stopPropagation(); if (role !== "viewer") openSnoozeDialog(h); };
    nameCell.onclick = () => {
      if (h.remote) { toast(`${esc(dispName(h))} is on ${esc(h.remote)}: open that BAMF to change it`); return; }
      if (role === "viewer") return;
      if (nameCell.querySelector("input")) return;
      const input = document.createElement("input");
      input.className = "name-input";
      input.value = h.customName || "";
      input.placeholder = h.hostname === "—" ? "Name this device" : h.hostname;
      input.maxLength = 60;
      nameCell.innerHTML = "";
      nameCell.appendChild(input);
      input.focus();
      input.select();

      let done = false;
      const finish = async (save) => {
        if (done) return;
        done = true;
        const value = input.value;
        input.remove(); // so the refresh guard doesn't block the redraw
        if (save) await setName(h, value); // empty clears back to DNS name
        await refresh();
      };
      input.onkeydown = e => {
        if (e.key === "Enter") finish(true);
        if (e.key === "Escape") finish(false);
      };
      input.onblur = () => finish(true);
      input.onclick = e => e.stopPropagation();
    };
    const cell = tr.lastElementChild;
    if (h.remote) {
      const chip = document.createElement("span");
      chip.className = "remote-chip";
      chip.textContent = h.remote;
      chip.title = "Seen by the BAMF at " + h.remote + " (read-only here)";
      cell.appendChild(chip);
      const sparkCellR = tr.querySelector(".spark-cell");
      if (sparkCellR) sparkCellR.textContent = "";
      tb.appendChild(tr);
      return;
    }
    cell.className = "actions";

    const star = document.createElement("button");
    star.className = "watch-star" + (h.watched ? " on" : "");
    star.innerHTML = h.watched
      ? `<span class="cam"><img src="watch-on.png" alt="" class="cam-dot" draggable="false"><img src="watch-on-off.png" alt="" class="cam-nodot" draggable="false"></span>`
      : `<img src="watch-idle.png" alt="" draggable="false">`;
    star.title = h.watched
      ? "Watching — you'll get a Discord alert if this host goes offline. Click to stop watching."
      : "Watch this host — get a Discord alert if it goes offline";
    star.onclick = async () => {
      await fetch(`/api/hosts/${h.id}/watch`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ watched: !h.watched }),
      });
      toast(h.watched ? `No longer watching ${esc(dispName(h))}` : `Now watching ${esc(dispName(h))} for downtime`);
      await refresh();
    };
    cell.appendChild(star);

    const hist = document.createElement("button");
    hist.className = "toggle";
    // History is exactly what the panel holds now. Notes moved to their own
    // dialog, reached from the ⋯ menu, so this button no longer has to hint at
    // an editable field hiding inside a read-only-sounding label.
    hist.textContent = expandedId === h.id ? "Hide" : "History";
    hist.title = "Online/offline session history for this device";
    hist.onclick = async () => {
      if (expandedId === h.id) { expandedId = null; render(); return; }   // toggle closed
      await openHistory(h);
    };
    cell.appendChild(hist);

    // Everything else lives in a ⋯ overflow menu to keep the row calm.
    const moreWrap = document.createElement("span");
    moreWrap.className = "more-wrap";
    const more = document.createElement("button");
    more.className = "toggle more-btn";
    more.innerHTML = "⋯";
    more.title = "More actions";
    const menu = document.createElement("div");
    menu.className = "row-menu";

    const addItem = (label, fn, opts = {}) => {
      const b = document.createElement("button");
      b.textContent = label;
      if (opts.danger) b.className = "danger";
      b.onclick = async (e) => { e.stopPropagation(); menu.classList.remove("show"); await fn(); };
      menu.appendChild(b);
      return b;
    };

    if (h.forgotten) {
      addItem("Unforget", async () => { await forgetHost(h, false); await refresh(); });
      addItem("Delete permanently", async () => {
        if (confirm(`Permanently delete ${dispName(h)} and its history? This cannot be undone.`)) {
          await deleteHostPermanent(h); await refresh();
        }
      }, { danger: true });
    } else {
      // Grouped, so the item you want is found by its heading rather than by
      // reading down twenty lines: what the device is, how it's wired, looking
      // closer at it, and what BAMF does about it.
      const addHead = text => {
        const d = document.createElement("div");
        d.className = "rm-h";
        d.textContent = text;
        menu.appendChild(d);
      };
      addHead("About this device");
      addItem(h.note ? "Edit note…" : "Add note…", () => openNoteDialog(h));
      addItem((h.tags || []).length ? `Tags (${h.tags.length})…` : "Tags…", () => openTagsDialog(h));
      addItem(h.typeName ? `Device type: ${h.typeName}…` : "Device type…", () => openKindDialog(h));
      addItem("Map icon…", () => openTypeDialog(h));
      addItem(h.link ? "Change link…" : "Set link…", () => setHostLink(h));

      const ownSwitch = switches.find(s => s.hostId === h.id);
      if (ownSwitch) {
        // A device recorded as a switch, router, access point or hypervisor
        // gets its own group for that, where "Plugged into" would be.
        addHead("This " + kindNoun(ownSwitch.kind));
        if (ownSwitch.kind === "ap") addItem("Add a wireless SSID…", () => openSwitchDialog(null, null, null, { kind: "ssid", runsOn: ownSwitch.id }));
        else addItem(ownSwitch.kind === "virtual" ? "VMs…" : ownSwitch.kind === "vpn" ? "Clients…" : "Ports…", () => openPortsDialog(ownSwitch));
        if (ownSwitch.kind === "switch" || ownSwitch.kind === "router") addItem("Traffic counters…", () => openSnmpDialog(ownSwitch));
        addItem(`Edit ${kindNoun(ownSwitch.kind)}…`, () => openSwitchDialog(ownSwitch));
      }
      addHead("Wiring");
      if (!ownSwitch) {
        addItem(h.switchId ? "Change plugged into…" : "Plugged into…", () => openPlugDialog(h));
        addItem("Find port…", () => { openPlugDialog(h); startBlink(h, "plugBlink"); });
      }
      addItem(h.interfaces ? `Network cards (${h.interfaces.length + 1})…` : "Network cards…", () => openCardsDialog(h));
      if (!vpnOfHost(h)) addItem(gwNetsOf(h).length ? "Gateway ✓…" : "Gateway…", () => openGatewayDialog(h));
      if (!ownSwitch) {
        addItem(isGatewayHost(h) ? "Make this a router…" : "Make this a switch or router…", () => openSwitchDialog(null, h));
        if (!vmPlatform(h.mac)) addItem("Add a virtual switch on this…", () => openSwitchDialog(null, null, null, { kind: "virtual", runsOn: h.id }));
      }

      addHead("Look closer");
      addItem("Identify device", () => identifyHost(h));
      addItem("Scan ports", () => scanPorts(h, more, null));
      addItem("Scan custom ports…", () => {
        const spec = prompt("Ports to scan on " + dispName(h) + " (e.g. 22,80,443,8000-8100):", "");
        if (spec) scanPorts(h, more, spec);
      });

      addHead("Status");
      addItem(h.snoozedUntil ? `Snoozed until ${snoozeClock(h.snoozedUntil)}…` : "Snooze alerts…", () => openSnoozeDialog(h));
      addItem(h.known ? "Mark unknown" : "Mark known", async () => { await setKnown(h, !h.known); await refresh(); });
      addItem(h.ignored ? "Unignore" : "Ignore", async () => { await setIgnored(h, !h.ignored); await refresh(); });
      if (!h.online) {
        addItem("Wake (WoL)", async () => {
          try {
            const r = await fetch(`/api/hosts/${h.id}/wake`, { method: "POST" });
            const data = await r.json();
            toast(data.ok ? `Magic packet sent to ${esc(dispName(h))}` : "Wake failed: " + esc(data.error || "unknown"));
          } catch { toast("Wake failed — see server logs"); }
        });
        addItem("Forget", async () => {
          if (confirm(`Move ${dispName(h)} to the Forgotten view? (reversible)`)) {
            await forgetHost(h, true); await refresh();
          }
        }, { danger: true });
      }
    }

    more.onclick = (e) => {
      e.stopPropagation();
      document.querySelectorAll(".row-menu.show").forEach(m => { if (m !== menu) m.classList.remove("show"); });
      if (menu.classList.toggle("show")) placeRowMenu(menu, more);
    };
    moreWrap.appendChild(more);
    moreWrap.appendChild(menu);
    cell.appendChild(moreWrap);
    if (openMenuRow !== null && String(h.id) === openMenuRow) {
      menu.classList.add("show");
      requestAnimationFrame(() => placeRowMenu(menu, more));
    }
    const ipMore = tr.querySelector(".ip-more");
    if (ipMore) {
      const addrMenu = ipMore.nextElementSibling;
      ipMore.onclick = (e) => {
        e.stopPropagation();
        document.querySelectorAll(".row-menu.show").forEach(m => { if (m !== addrMenu) m.classList.remove("show"); });
        if (addrMenu.classList.toggle("show")) placeRowMenu(addrMenu, ipMore);
      };
      if (openAddrRow !== null && String(h.id) === openAddrRow) {
        addrMenu.classList.add("show");
        requestAnimationFrame(() => placeRowMenu(addrMenu, ipMore));
      }
    }
    // 24h sparkline built from the network-wide feed we already fetch
    const sparkCell = tr.querySelector(".spark-cell");
    if (sparkCell) sparkCell.appendChild(buildSpark(h));
    bulkWireRow(tr, h);

    tb.appendChild(tr);

    if (expandedId === h.id) {
      const detail = document.createElement("tr");
      detail.className = "history-row";
      const td = document.createElement("td");
      td.colSpan = 13;

      const sessions = buildSessions(eventsCache[h.id] || []);
      let inner = `<div class="history-title"><span>Recent sessions — ${esc(dispName(h))}</span>` +
        `<button type="button" class="history-close" title="Close (Esc)">Close ✕</button></div>`;
      inner += buildTimeline(sessions, h.online);
      // Uptime over the last week and month, from the same events.
      if (h.uptime7 != null || h.uptime30 != null) {
        inner += `<div class="lat-sum">Uptime: ` +
          (h.uptime7 != null ? `<b>${h.uptime7}%</b> over 7 days` : "") +
          (h.uptime7 != null && h.uptime30 != null ? " · " : "") +
          (h.uptime30 != null ? `<b>${h.uptime30}%</b> over 30 days` : "") +
          (h.uptime30 != null && h.uptime30 < 100 ? ` <span title="Time offline over the last 30 days">(down ${(100 - h.uptime30).toFixed(1)}% of the month)</span>` : "") + `</div>`;
      }
      inner += buildPresence(presenceCache[h.id]);
      if ((h.ipv6 || []).length)
        inner += `<div class="lat-sum">IPv6: ${h.ipv6.map(a => `<span class="mono">${esc(a)}</span>`).join(" · ")}</div>`;
      // The device's story, newest first: sessions, moves, ports and alerts in one list.
      const tl = timelineCache[h.id] || [];
      if (tl.length > 1) {
        inner += `<div class="history-sub">Timeline</div><div class="tl">` + tl.slice(0, 60).map(t =>
          `<div class="tl-item k-${esc(t.kind.startsWith("alert") ? "alert" : t.kind)}"><span class="when">${esc(fmtExact(t.at))}</span><span class="ico"></span><span>${esc(t.text)}</span></div>`).join("") + `</div>`;
      }
      inner += buildLatencyChart(latencyCache[h.id] || []);
      if (h.traffic) {
        const tt = (trafficCache && trafficCache.top || []).find(x => x.hostId === h.id);
        inner += `<div class="lat-sum">Traffic: <b>${fmtBytes(h.traffic.rxTotal)}</b> in · <b>${fmtBytes(h.traffic.txTotal)}</b> out ${esc(trafficSince(h.traffic))}` +
          ` · now ↓${fmtRate(h.traffic.rx)} ↑${fmtRate(h.traffic.tx)}</div>`;
        if (tt) { const smax = Math.max(1, ...tt.strip); inner += `<div class="traf-strip" title="Bytes per ten seconds over the last five minutes">${tt.strip.map(v => `<i style="height:${Math.max(4, Math.round(100 * v / smax))}%"></i>`).join("")}</div>`; }
      }
      // Ports found open, now or once, from the last scans.
      const pl = portsCache[h.id] || [];
      if (pl.length) {
        const open = pl.filter(x => x.open), was = pl.filter(x => !x.open);
        inner += `<div class="lat-sum">Ports: <b>${open.length}</b> open at the last scan${open.length ? " (" + esc(fmtAgo(open[0].lastSeen)) + ")" : ""}` +
          (was.length ? ` · ${was.length} closed since an earlier scan` : "") + `</div><div class="port-hist">` +
          open.map(x => `<span class="port-chip" title="Open since ${esc(fmtExact(x.firstSeen))}">${x.port}${x.service ? " " + esc(x.service) : ""}</span>`).join("") +
          was.map(x => `<span class="port-chip was" title="Last seen open ${esc(fmtExact(x.lastSeen))}">${x.port}${x.service ? " " + esc(x.service) : ""}</span>`).join("") + `</div>`;
      }
      // Bytes per hour over the last week, from the traffic monitor's history.
      const wk = weekCache[h.id] || [];
      if (wk.length) {
        const total = wk.reduce((a, r) => a + r.rx + r.tx, 0), rx = wk.reduce((a, r) => a + r.rx, 0);
        const byHour = new Map(wk.map(r => [r.hour, r.rx + r.tx]));
        const bars = []; const now = new Date(); now.setUTCMinutes(0, 0, 0);
        for (let i = 167; i >= 0; i--) { const d = new Date(now.getTime() - i * 36e5); bars.push({ v: byHour.get(d.toISOString().slice(0, 13) + ":00:00Z") || 0, day: d.getUTCHours() === 0 }); }
        const max = Math.max(1, ...bars.map(b => b.v));
        inner += `<div class="lat-sum">Traffic this week: <b>${fmtBytes(total)}</b> (${fmtBytes(rx)} in, ${fmtBytes(total - rx)} out), by hour</div>` +
          `<div class="traf-week" title="Bytes per hour over the last 7 days">${bars.map(b => `<i class="${b.day ? "day" : ""}" style="height:${Math.max(2, Math.round(100 * b.v / max))}%"></i>`).join("")}</div>`;
      }
      if (h.dns && h.dns.length) inner += `<div class="lat-sum">DNS: asks <b>${esc(h.dns[0])}</b>${h.dns.length > 1 ? " (also " + esc(h.dns.slice(1).join(", ")) + ")" : ""}</div>`;
      if (sessions.length === 0) {
        inner += `<div class="history-empty">No history recorded yet. Sessions appear as this host comes and goes.</div>`;
      } else {
        inner += `<div class="sessions">` + sessions.slice(0, 15).map(s => {
          const from = fmtExact(s.from);
          if (s.to === null) {
            return `<div class="session"><span class="dot on"></span>${esc(from)} → <span class="ongoing">still online</span><span class="dur">(${fmtDur(Date.now() - new Date(s.from))})</span></div>`;
          }
          return `<div class="session"><span class="dot off"></span>${esc(from)} → ${esc(fmtExact(s.to))}<span class="dur">(${fmtDur(new Date(s.to) - new Date(s.from))})</span></div>`;
        }).join("") + `</div>`;
      }
      // A device answering on several addresses at once lists them all.
      const now = curAddrs(h);
      if (now.length > 1) {
        inner += `<div class="history-sub">Answers on ${now.length} addresses now</div><div class="sessions">` +
          now.map(a => `<div class="session"><span class="dot on"></span><span class="mono">${esc(a.ip)}</span> ` +
            `<span class="dur">${esc(a.subnet || "")}${a.ip === h.ip ? " · main" : ""} · since ${esc(fmtExact(a.firstSeen || h.firstSeen))}</span></div>`).join("") +
          `</div>`;
      }
      // Addresses: only worth showing once a device has actually moved. Most
      // never do, and a one-line "same address since first seen" is noise.
      const ips = ipsCache[h.id] || [];
      if (ips.length > 1) {
        inner += `<div class="history-sub">Addresses — newest first</div><div class="sessions">` +
          ips.slice().reverse().map(a => {
            const cur = a.to === null;
            return `<div class="session"><span class="dot ${cur ? "on" : "off"}"></span>` +
              `<span class="mono">${esc(a.ip)}</span> ` +
              (cur
                ? `<span class="ongoing">since ${esc(fmtExact(a.from))}</span>`
                : `${esc(fmtExact(a.from))} → ${esc(fmtExact(a.to))}` +
                  `<span class="dur">(${fmtDur(new Date(a.to) - new Date(a.from))})</span>`) +
              `</div>`;
          }).join("") + `</div>`;
      }
      td.innerHTML = inner;
      detail.appendChild(td);
      tb.appendChild(detail);
      td.querySelector(".history-close").onclick = () => { expandedId = null; render(); };
    }
  });
  festiveHooks.rendered?.();
}

document.querySelectorAll("thead th[data-sort]").forEach(th => {
  th.onclick = () => {
    const key = th.dataset.sort;
    if (sortKey === key) {
      if (sortDir === 1) sortDir = -1;
      else { sortKey = null; sortDir = 1; } // third click restores default order
    } else { sortKey = key; sortDir = 1; }
    render();
  };
});
