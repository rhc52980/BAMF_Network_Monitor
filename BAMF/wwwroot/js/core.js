const POLL_MS = 10000;

let hosts = [];
let filter = "all";       // status filter within Devices view
let guessFilter = null;   // device-type chip, e.g. "Apple device"; null = every type
let tagFilter = null;     // a tag chip; null = every device
let latencyProbe = true;
let trafficStatus = {};     // { enabled, available, running, interfaces, error, since, frames }
let trafficCache = null;    // the last /api/traffic answer, for the Activity cards
// With the view-only password, the server refuses any change with a 403 and
// an X-BAMF-ViewOnly header. Say so once per refusal, whichever control it was.
{
  const plainFetch = window.fetch.bind(window);
  // And a sign-in that has run out (or a password changed elsewhere) sends
  // the page to sign in again, and back here after.
  let signingIn = false;
  window.fetch = async (...args) => {
    const r = await plainFetch(...args);
    if (r.status === 403 && r.headers.get("X-BAMF-ViewOnly")) {
      try { toast("View only: this password can look at everything but not change anything"); } catch { }
    }
    if (r.status === 401 && r.headers.get("X-BAMF-SignIn") && !signingIn) {
      signingIn = true;
      const here = location.pathname + location.search + location.hash;
      location.href = "/signin" + (here === "/" ? "" : "?next=" + encodeURIComponent(here));
    }
    return r;
  };
}
let alertsCache = null;     // the last /api/alerts answer
let securityCache = null;   // the last /api/security answer: certificates, UPnP, gateway MACs
const presenceCache = {};   // device id -> its week of hours, for the History panel
let arpWatch = true, certWatch = true, ipv6Watch = true;
let greynoiseCache = null;  // the last /api/greynoise answer: { enabled, result }
let role = "open";          // "admin", "viewer" (the view-only password) or "open" (no password set)
let rulesData = null;       // { rules, quiet, portWatch } from /api/settings
let remoteStatus = [];      // other BAMF servers: [{ name, url, ok, error, hosts, ... }]
const fmtBytes = n => n == null ? "—" : n < 1024 ? n + " B" : n < 1048576 ? (n / 1024).toFixed(n < 10240 ? 1 : 0) + " kB" : n < 1073741824 ? (n / 1048576).toFixed(1) + " MB" : (n / 1073741824).toFixed(2) + " GB";
const fmtRate = n => n == null ? "—" : n < 1024 ? Math.round(n) + " B/s" : n < 1048576 ? (n / 1024).toFixed(n < 10240 ? 1 : 0) + " kB/s" : (n / 1048576).toFixed(1) + " MB/s";
let newDays = 7;          // the New tab: devices first seen this recently (Settings → Your network)
// New means not dealt with yet: first seen lately, and not marked known.
const isNew = h => !h.known && h.firstSeen && (Date.now() - new Date(h.firstSeen).getTime()) < newDays * 864e5;
const hasTag = (h, t) => (h.tags || []).some(x => x.toLowerCase() === t.toLowerCase());
const tagOk = h => tagFilter === null || hasTag(h, tagFilter);
let view = "devices";     // devices | home | activity | forgotten
let query = "";
let network = "all";      // selected network, or "all"
let subnets = [];         // networks reported by the scanner
let scanModes = {};       // subnet -> 'active ARP' | 'ping sweep'
let subnetIntervals = {};  // subnet -> effective interval in seconds
let subnetNextDue = {};    // subnet -> ISO time the scanner will next run it (paused: absent)
let networkPlaces = {};    // subnet -> { selfIp, selfMac, gateway }: what the map knows for certain
let declaredGws = [];      // [{ subnet, hostId, ip }]: gateways the user declared, one per network
let mapPositions = {};     // topology Map: subnet -> node key -> [x, y] the user dragged to
let typeIcons = {};        // guessed type -> icon the user chose for all of them: { "Linux": "server" }
let switches = [];         // the switch layout the user recorded: [{ id, name, ports, subnet, hostId, uplink, uplinkSwitch, uplinkPort }]
let activeArp = { enabled: false, npcapAvailable: false };
let autoIgnoreRandom = false;
// Holiday Spirit, from the server. Remembered in this browser too, so the
// holiday theme is up from the first paint rather than after the first poll.
let holidaySpirit = false;
try { holidaySpirit = localStorage.getItem("bamf-holiday-spirit") === "1"; } catch {}
// Night mode: between two clock times every dashboard wears the night theme.
// Cached here so the first paint is right before the first poll answers.
let nightMode = { enabled: false, from: "21:00", to: "06:00", theme: "nightstreet" };
try { const n = JSON.parse(localStorage.getItem("bamf-night") || "null"); if (n && typeof n === "object") nightMode = { ...nightMode, ...n }; } catch {}
let webhookConfigured = false;
let alertsConfigured = false;   // any destination, the main webhook or another, takes any alerts
let alertsNudgeOff = false;     // "Don't remind me" on the alerts-off banner
let webhookMasked = null;   // masked form only; the server never sends the full URL
let webhookFormat = "auto"; // auto | ntfy | gotify | json
let repoUrl = "";           // project page, for the feedback link
let updateInfo = null;      // { enabled, available, latest, url } from /api/hosts
let appVersion = "";
let buildDate = "";        // UTC build stamp, shown in the header and feedback report
let loadedOnce = false;
let sortKey = null;       // null = server order (network, then IP)
let sortDir = 1;
let scanInterval = 60;
let lastScan = null;
let knownMacs = null; // for client-side "new host" toast
let expandedId = null;       // host whose history is open
let eventsCache = {};        // hostId -> events array
let ipsCache = {};           // hostId -> [{ip, from, to}] oldest first
let latencyCache = {};       // hostId -> [{at, ms}] oldest first, last 24 h
let portsCache = {};         // hostId -> [{port, service, firstSeen, lastSeen, open}]
let timelineCache = {};      // hostId -> [{at, kind, text}] newest first
let weekCache = {};          // hostId -> [{hour, rx, tx}] over 7 days
let feedCache = [];          // network-wide activity feed

function fmtExact(iso) {
  if (!iso) return "";
  return new Date(iso).toLocaleString([], {
    month: "short", day: "numeric", hour: "2-digit", minute: "2-digit"
  });
}

function fmtDur(ms) {
  const m = Math.round(ms / 60000);
  if (m < 1) return "<1 min";
  if (m < 60) return m + " min";
  const h = Math.floor(m / 60);
  if (h < 24) return h + " h " + (m % 60) + " min";
  return Math.floor(h / 24) + " d " + (h % 24) + " h";
}

async function loadFeed() {
  const r = await fetch("/api/events");
  if (!r.ok) throw new Error("API " + r.status);
  return r.json();
}

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
function renderAlertsCard() {
  const body = $("alertsBody");
  const list = alertsCache || [];
  body.innerHTML = list.length ? list.slice(0, 12).map(a =>
    `<div class="alert-item k-${esc(a.kind)}"><div><b>${esc(a.title)}</b><span class="when">${esc(fmtAgo(a.at))}</span></div><div class="det">${esc(a.detail)}</div></div>`).join("")
    : `<div class="watch-note">Nothing yet. Rules, the port watch and the DHCP/DNS watch put their alerts here.</div>`;
}
