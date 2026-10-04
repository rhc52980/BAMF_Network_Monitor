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
let restartAlertEnabled = true;  // BAMF says so when it starts again after stopping unexpectedly
let pauseEnabled = true;       // the Pause alerts control is offered
let alertsPausedUntil = null;  // when a pause on every alert ends, or null
let networkToolsEnabled = true;  // Ping, Trace route and DNS lookup are offered in a device's menu
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

function toast(msg) {
  const t = $("toast");
  t.innerHTML = msg;
  t.classList.add("show");
  setTimeout(() => t.classList.remove("show"), 8000);
}

async function refresh() {
  // Don't redraw while a rename is in progress: the input lives in a row that
  // render() rebuilds, so a redraw mid-word would throw away what was typed.
  // Notes no longer need this - they are edited in a dialog outside the table.
  if (document.querySelector(".name-input")) return;
  try {
    const data = await loadHosts();
    $("connDot").classList.remove("err");

    scanInterval = data.scanIntervalSeconds || 60;
    if (lastScan && data.lastScan && data.lastScan !== lastScan) { festiveHooks.scanDone?.(); watchtower?.event("scan"); }
    lastScan = data.lastScan;
    subnets = data.subnets || [];
    scanModes = data.scanModes || {};
    subnetIntervals = data.subnetIntervalSeconds || {};
    subnetNextDue = data.subnetNextDue || {};
    networkPlaces = data.networkPlaces || {};
    declaredGws = data.gateways || [];
    switches = data.switches || [];
    mapPositions = data.mapPositions || {};
    typeIcons = data.typeIcons || {};
    syncBlink(data.blink, data.serverTime);
    activeArp = data.activeArp || activeArp;
    autoIgnoreRandom = !!data.autoIgnoreRandom;
    if (typeof data.latencyProbe === "boolean") latencyProbe = data.latencyProbe;
    trafficStatus = data.traffic || {};
    document.body.classList.toggle("has-traffic", !!trafficStatus.running);
    if (typeof data.holidaySpirit === "boolean" && data.holidaySpirit !== holidaySpirit) {
      holidaySpirit = data.holidaySpirit;
      try { localStorage.setItem("bamf-holiday-spirit", holidaySpirit ? "1" : "0"); } catch {}
    }
    if (data.night && typeof data.night === "object") {
      nightMode = { ...nightMode, ...data.night };
      try { localStorage.setItem("bamf-night", JSON.stringify(nightMode)); } catch {}
    }
    updateInfo = data.update || null;
    appVersion = data.version || appVersion;
    if (data.role && data.role !== role) {
      role = data.role;
      document.body.classList.toggle("viewer", role === "viewer");
      $("viewOnly").hidden = role !== "viewer";
    }
    buildDate = data.buildDate || buildDate;
    webhookConfigured = !!data.webhookConfigured;
    alertsConfigured = data.alertsConfigured ?? webhookConfigured;
    alertsNudgeOff = !!data.alertsNudgeOff;
    networkToolsEnabled = data.networkToolsEnabled !== false;
    { const nb = $("networkToolsEnabled"); if (nb) nb.checked = networkToolsEnabled; }
    bulkEnabled = data.bulkEnabled !== false;
    pauseEnabled = data.pauseEnabled !== false;
    restartAlertEnabled = data.restartAlertEnabled !== false;
    { const rb = $("restartAlertEnabled"); if (rb) rb.checked = restartAlertEnabled; }
    alertsPausedUntil = data.alertsPausedUntil || null;
    if (data.newDays) newDays = data.newDays;
    renderAlertsOff();
    renderPause();
    loadUnseen();
    webhookMasked = data.webhookMasked || null;
    webhookFormat = data.webhookFormat || "auto";
    repoUrl = data.repoUrl || repoUrl;
    $("notifyTest").style.display = webhookConfigured ? "" : "none";
    renderArpToggle();
    renderRandToggle();
    renderLatencyToggle();
    renderTrafficToggle();
    renderHolidayToggle();
    applyHolidaySpirit();
    applyNightMode();
    renderUpdateToggle();
    $("subnetLabel").textContent = subnets.length === 1
      ? subnets[0]
      : subnets.length + " networks";
    if (data.version) {
      const el = $("version");
      el.textContent = "v" + data.version + (data.buildDate ? " · " + data.buildDate.slice(0, 10) : "");
      el.title = "BAMF " + data.version + (data.buildDate ? " — built " + data.buildDate + " UTC" : "") + ". Click for what's new.";
      checkNews();
    }
    $("feedbackLink").href = feedbackHref();

    const raw = data.hosts || [];

    // toast on newly-appearing unknown hosts (after first load)
    const arrivals = [];
    if (knownMacs) {
      for (const h of raw) {
        if (!knownMacs.has(h.mac) && !h.known && !h.ignored && !h.forgotten) {
          toast(`New host on <span class="mono">${esc(h.subnet)}</span>: <span class="mono">${esc(h.mac)}</span> at <span class="mono">${esc(h.ip)}</span>`);
          arrivals.push(h);
          festiveHooks.newDevice?.(h);
          if (saverStyle.startsWith("watch")) { watchAlert(h); watchtower?.redraw(); }
        }
      }
    }
    knownMacs = new Set(raw.map(h => h.mac));
    const incoming = foldInterfaces(raw);
    // Other sites' devices ride along, read-only, under networks named after the site.
    for (const rh of (data.remotes || [])) { rh.remote = rh.remote || "remote"; rh.tags = rh.tags || []; rh.addresses = rh.addresses || []; incoming.push(rh); }
    remoteStatus = data.remoteStatus || [];

    // For the Matrix glitch: devices that just went offline or moved address.
    const before = new Map(hosts.map(h => [h.id, h]));
    glitchIds = loadedOnce ? incoming.filter(h => { const b = before.get(h.id); return b && ((b.online && !h.online) || b.ip !== h.ip); }).map(h => h.id) : [];
    const watching = h => !h.ignored && !h.forgotten;
    wentOffIds = loadedOnce ? incoming.filter(h => { const b = before.get(h.id); return b && watching(h) && b.online && !h.online; }).map(h => h.id) : [];
    const cameBack = loadedOnce ? incoming.filter(h => { const b = before.get(h.id); return b && !b.online && h.online; }).map(h => h.id) : [];
    if (wentOffIds.length || cameBack.length) { festiveHooks.netChange?.(wentOffIds, cameBack); watchtower?.event("change", { off: wentOffIds, back: cameBack }); }
    hosts = incoming;
    loadedOnce = true;
    // To a theme with an intruder scene, each of them is an intruder.
    for (const a of arrivals) { try { festiveHooks.intruder?.(hosts.find(x => x.id === a.id) || a); } catch (e) { console.error("theme intruder failed", e); } }
    try { feedCache = await loadFeed(); } catch { /* keep old feed */ }
    render();
    renderSwitchList();
    renderTypeIconList();
  } catch (e) {
    $("connDot").classList.add("err");
    console.error("refresh failed", e);
  }
}

// Countdown to the next scan. The scanner reports when each network is next
// due, so this is its schedule, not a guess from the default interval - which
// went wrong the moment networks had intervals of their own. The header shows
// the selected network if one is, otherwise whichever is soonest, and names
// it when there's more than one to choose from. Each network tab and the
// Settings table carry their own figure from the same source.
function secondsUntil(iso) {
  return Math.max(0, Math.round((new Date(iso) - Date.now()) / 1000));
}
function dueText(iso, fmt) {
  const s = secondsUntil(iso);
  if (fmt === "in") return s === 0 ? "scanning now" : "next in " + s + "s";
  return s === 0 ? "now" : "~" + s + "s";
}
setInterval(() => {
  // A network with no local interface is re-checked on its interval in case
  // one appears, but that isn't a scan worth counting down to.
  const nets = Object.keys(subnetNextDue).filter(n => scanModes[n] !== "skipped");
  if (!nets.length && Object.keys(subnetNextDue).length) {
    $("nextScan").textContent = "nothing to scan";
    $("nextScan").title = "Every configured network is paused or has no interface on this machine";
    $("scanFill").style.width = "0%";
    return;
  }
  if (!nets.length) {
    // Older backend, or nothing scheduled yet: the old estimate.
    if (!lastScan) return;
    const elapsed = (Date.now() - new Date(lastScan)) / 1000;
    const left = Math.max(0, Math.round(scanInterval - (elapsed % scanInterval)));
    $("nextScan").textContent = "next scan ~" + left + "s";
    $("scanFill").style.width = (100 * (1 - left / scanInterval)) + "%";
    return;
  }
  let pick = (network !== "all" && subnetNextDue[network]) ? network : null;
  if (!pick) pick = nets.reduce((a, b) => new Date(subnetNextDue[a]) <= new Date(subnetNextDue[b]) ? a : b);
  const left = secondsUntil(subnetNextDue[pick]);
  const interval = subnetIntervals[pick] || scanInterval;
  const who = nets.length > 1 ? " · " + pick : "";
  $("nextScan").textContent = (left === 0 ? "scanning now" : "next scan ~" + left + "s") + who;
  $("nextScan").title = nets.length > 1
    ? nets.map(n => n + ": " + secondsUntil(subnetNextDue[n]) + "s").join("\n")
    : "Next scan";
  $("scanFill").style.width = (100 * (1 - Math.min(left, interval) / interval)) + "%";
  document.querySelectorAll("[data-due-net]").forEach(el => {
    const due = subnetNextDue[el.dataset.dueNet];
    el.textContent = due ? dueText(due, el.dataset.dueFmt) : "";
  });
}, 1000);

// Shared by the three instant-apply toggles in Settings: pill colour, the
// word beside it, and the state for screen readers.
function setToggleState(t, on) {
  t.classList.toggle("on", on);
  t.setAttribute("aria-pressed", on ? "true" : "false");
  const w = t.querySelector(".tstate");
  if (w) w.textContent = on ? "On" : "Off";
}
