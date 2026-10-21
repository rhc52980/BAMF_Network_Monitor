// The 3D view's data side, with nothing to draw in it: which model a device gets, what state it's in,
// which network it sits on, and where everything goes. Plain functions, so they're tested on their own.

// Every model there is, and its size: the longest side on the platform (the files are all normalised to 1).
// A device's Map kind picks one of the first group; its name, vendor and type then pick a more specific one
// from the second where they say what it really is (a Roku is a streamer, not just a "tv").
export const MODELS = {
  router: 1.8, switch: 1.6, ap: 1.4, nas: 1.3, server: 1.7, desktop: 1.4, laptop: 1.5, printer: 1.4, tv: 2.0, speaker: 1.2,
  camera: 1.3, pi: 1.0, phone: 0.85, tablet: 1.1, iot: 1.0, game: 1.5, plug: 0.9, light: 1.0, vm: 1.6, vpn: 1.3, device: 1.1,
  streamer: 1.2, soundbar: 1.8, display: 1.3, doorbell: 1.1, dome: 1.1, thermostat: 1.0, lock: 1.3, vacuum: 1.2, sprinkler: 1.3,
  garage: 1.6, fridge: 1.9, washer: 1.7, purifier: 1.6, ac: 1.7, charger: 1.4, car: 2.4, vr: 1.2, watch: 1.0, handheld: 1.1,
  projector: 1.4, printer3d: 1.5, ereader: 1.0, modem: 1.4, mesh: 1.4, minipc: 1.1,
};
const ALIAS = { vswitch: "switch", net: "router" };

const SMALL = ["iot", "device"];
// [model, what its text says, the Map kinds it may replace (anything if left out)], first match wins.
export const REFINE = [
  ["doorbell", /doorbell|nest hello|video ?bell|\bring ?(pro|video|doorbell|chime)/, ["camera", ...SMALL]],
  ["dome", /\bdome\b|\bptz\b|turret/, ["camera", ...SMALL]],
  ["display", /nest hub|echo show|echo spot|smart display|google home hub|\bportal\b|smart clock/, ["speaker", "tv", "tablet", "phone", ...SMALL]],
  ["mesh", /\beero\b|\borbi\b|\bdeco\b|nest ?wi-?fi|google ?wi-?fi|\bvelop\b|amplifi|\bmesh\b|extender|repeater|\bplume\b|wi-?fi ?point/, ["ap", "router", "net", "switch", ...SMALL]],
  ["soundbar", /sound ?bar|sonos (beam|arc|ray|playbar|playbase)|\bhw-[a-z0-9]+|\byas-\d+/, ["speaker", "tv", ...SMALL]],
  ["streamer", /\broku\b|chromecast|apple ?tv|fire ?(tv|stick|cube)|firestick|\bshield\b|google tv|streaming|tivo|\bmi box\b|media ?player/, ["tv", "speaker", "game", ...SMALL]],
  ["projector", /projector|optoma|\bepson eb|benq (ht|w\d)/, ["tv", ...SMALL]],
  ["printer3d", /3d ?printer|prusa|creality|\bender-?\d|bambu|octoprint|octopi|klipper|anycubic|elegoo|voron|mainsail|fluidd/, ["printer", "server", "desktop", ...SMALL]],
  ["vacuum", /vacuum|roomba|irobot|roborock|ecovacs|\bneato\b|dreame|robovac|deebot|narwal|robot ?clean/, SMALL],
  ["lock", /smart ?lock|deadbolt|\bnuki\b|schlage|kwikset|\byale\b|august (home|lock)|\block\b/, SMALL],
  ["thermostat", /thermostat|ecobee|honeywell home|\btado\b|\bnest\b(?!.*\b(hub|mini|audio|wi-?fi|point|cam|protect|doorbell|hello)\b)/, SMALL],
  ["sprinkler", /sprinkler|rachio|rain ?bird|irrigat|b-?hyve|hydrawise|\blawn\b|\bturf\b|\bzone controller/, SMALL],
  ["garage", /garage|\bmyq\b|chamberlain|liftmaster|\bopener\b/, ["camera", ...SMALL]],
  ["fridge", /fridge|refrigerator|freezer|family hub|sub-?zero/, ["tv", ...SMALL]],
  ["washer", /washer|dryer|dishwasher|laundry|\boven\b|microwave|\brange\b|cooktop|appliance|whirlpool|maytag|electrolux|\bmiele\b|thinq/, SMALL],
  ["purifier", /purifier|humidifier|dehumidifier|blueair|levoit|\bcoway\b|\bhepa\b|\bfan\b|air ?quality|\bdyson\b/, ["plug", ...SMALL]],
  ["ac", /air ?condition|mini-?split|\bhvac\b|\bdaikin\b|mitsubishi (electric|comfort)|heat ?pump|\bcielo\b|\bsensibo\b|\bac unit\b/, SMALL],
  ["charger", /charger|wallbox|chargepoint|\bevse\b|juicebox|wall connector|\bemporia\b/, ["plug", ...SMALL]],
  ["car", /tesla|rivian|polestar|vehicle|\bmodel ?[3sxy]\b|\bford\b|chevrolet|\bbmw\b|\bvolvo\b|hyundai|\bkia\b|nissan|\bhonda\b|toyota|\blucid\b|volkswagen|subaru|mercedes/, ["tv", "speaker", "phone", "tablet", "game", ...SMALL]],
  ["vr", /\bquest\b|oculus|\bvr\b|\bvive\b|psvr|\bpico ?4\b|headset|valve index/, ["game", "phone", "tv", ...SMALL]],
  ["watch", /apple ?watch|\bwatch\b|fitbit|garmin|whoop|\boura\b|wearable/, ["phone", "tablet", ...SMALL]],
  ["handheld", /steam ?deck|switch lite|rog ally|ayaneo|handheld|nintendo/, ["game", "tablet", ...SMALL]],
  ["ereader", /kindle|\bkobo\b|e-?reader|remarkable|boox/, ["tablet", ...SMALL]],
  ["modem", /\bmodem\b|\bont\b|surfboard|\barris\b|\bcm\d{3,4}\b|\bmb\d{4}\b|docsis/, ["router", "net", "switch", ...SMALL]],
  ["minipc", /\bnuc\b|mini ?pc|beelink|minisforum|home ?assistant|hassio|odroid|zima(board|cube)?|rock ?pi|orange ?pi|banana ?pi|mac ?mini|elitedesk|prodesk|thinkcentre tiny/, ["server", "desktop", ...SMALL]],
  ["pi", /raspberry|pi-?hole|pihole|\brpi\b|raspbian/, ["server", "desktop", ...SMALL]],
];

/** The words a device gives about itself: what BAMF guessed it is, what it's called, its vendor and its type. */
export function textOf(h) {
  return [h.osGuess, h.customName, h.hostname === "—" ? "" : h.hostname, h.mdnsName, h.vendor, h.typeName].filter(Boolean).join(" ").toLowerCase();
}

/** The model for a device: its Map kind, made more specific by what its name, vendor and type say. */
export function modelFor(kind, vendor = "", text = "") {
  let k = ALIAS[kind] || kind;
  const t = `${text || ""} ${vendor || ""}`.toLowerCase();
  for (const [model, re, kinds] of REFINE) if ((!kinds || kinds.includes(k)) && re.test(t)) return model;
  if (k === "server" && /raspberry/i.test(vendor || "")) k = "pi";
  return Object.hasOwn(MODELS, k) ? k : "device";
}

/** odd (something unusual is open), off, unk (online but not yet approved), or on. */
export function stateOf(host, unusualOpen) {
  if (unusualOpen) return "odd";
  if (!host.online) return "off";
  return host.known ? "on" : "unk";
}

const subnetKey = h => h.subnet || "";

/**
 * Hosts into what the scene draws: one platform per network, each device on its network's, the
 * gateway at the centre. Ignored and forgotten devices aren't drawn.
 *  input: { hosts, kindOf(h), nameOf(h), unusual: [{hostId, kind, open, title, detail}], gatewayIp(subnet), rateOf(h),
 *           servicesOf(h) -> [{name, state, uptime, ms}] (the service watch's rows for that device),
 *           tagsOf(h) -> [tag], parentOf(h) -> the id of the device it is plugged into (a switch or access point), or null,
 *           trafficOf(h) -> {rx, tx} in bytes a second where it is measured, webOf(h) -> {url} where it has a web page,
 *           exposureOf(h) -> what exposureOf() below says about it (risky ports, a certificate, UPnP, its jobs on the network), or null }
 */
export function buildScene({ hosts, kindOf, nameOf, unusual = [], gatewayIp = () => null, rateOf = () => 2, servicesOf = () => [],
  tagsOf = () => [], parentOf = () => null, trafficOf = () => null, webOf = () => null, exposureOf: exposureFn = () => null }) {
  const open = new Map();
  for (const u of unusual) if (u.open && !open.has(u.hostId)) open.set(u.hostId, u);
  const shown = hosts.filter(h => !h.ignored && !h.forgotten);
  const order = [...new Set(shown.map(subnetKey))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  const nets = order.map(cidr => ({ id: cidr, cidr, gateway: null, devices: [] }));
  const byNet = new Map(nets.map(n => [n.id, n]));
  const devices = [];
  for (const h of shown) {
    const net = byNet.get(subnetKey(h));
    const odd = open.get(h.id) || null;
    const kind = kindOf(h);
    const d = {
      key: String(h.id), id: h.id, name: nameOf(h), ip: h.ip, mac: h.mac, vendor: h.vendor || "", kind,
      model: modelFor(kind, h.vendor, textOf(h)), state: stateOf(h, !!odd), online: !!h.online, known: !!h.known, watched: !!h.watched,
      ms: h.latencyMs ?? null, rate: h.online ? Math.max(1, Math.min(9, rateOf(h))) : 0, net: net.id,
      odd: odd ? { title: odd.title, detail: odd.detail } : null, gw: false,
      strain: strainOf(h.online ? h.latencyMs ?? null : null),
      services: servicesOf(h).map(x => ({ name: x.name, state: x.state, uptime: x.uptime ?? null, ms: x.ms ?? null })),
      tags: (tagsOf(h) || []).map(String), parent: null, parentId: parentOf(h) ?? null,
      rx: trafficOf(h)?.rx ?? null, tx: trafficOf(h)?.tx ?? null, web: webOf(h) || null,
      watching: !!h.watched, snoozed: !!h.snoozedUntil, remote: !!h.remote, exposure: exposureFn(h) || null,
    };
    net.devices.push(d); devices.push(d);
  }
  // What each device is plugged into, where it was recorded and the device that is that switch or access point is on the same network:
  // its parent. A loop in what was recorded is cut rather than followed.
  const byId = new Map(devices.map(d => [d.id, d]));
  for (const d of devices) {
    const p = d.parentId != null ? byId.get(d.parentId) : null;
    if (!p || p === d || p.net !== d.net) continue;
    let up = p, hops = 0, loop = false;
    while (up && hops++ < 24) { if (up === d) { loop = true; break; } up = up.parentId != null ? byId.get(up.parentId) : null; }
    if (!loop) d.parent = p.key;
  }
  for (const net of nets) {
    // The gateway: the device at the address BAMF knows the gateway to be, else the first router on the network.
    const ip = gatewayIp(net.cidr);
    const gw = net.devices.find(d => ip && d.ip === ip) || net.devices.find(d => d.model === "router") || null;
    if (gw) { gw.gw = true; net.gateway = gw.key; }
  }
  return { nets, devices };
}

/** How many devices fit on each ring round a gateway, and how far out it is. */
export function ringsFor(count) {
  const rings = [];
  let left = count, k = 0;
  while (left > 0) {
    const r = 3.4 + 2.6 * k;
    const cap = Math.max(5, Math.floor((2 * Math.PI * r) / 2.4));
    const n = Math.min(cap, left);
    rings.push({ r, n }); left -= n; k++;
  }
  return rings;
}

/** A device's place on its network's platform, relative to the gateway: grouped by kind, round the rings. */
export function slotsFor(devices) {
  const others = devices.filter(d => !d.gw).sort((a, b) => a.model.localeCompare(b.model) || a.ip.localeCompare(b.ip, undefined, { numeric: true }));
  const rings = ringsFor(others.length), slots = new Map();
  let i = 0;
  rings.forEach((ring, ri) => {
    for (let j = 0; j < ring.n; j++, i++) {
      const a = (j / ring.n) * Math.PI * 2 + ri * 0.5;
      slots.set(others[i].key, { x: Math.cos(a) * ring.r, z: Math.sin(a) * ring.r });
    }
  });
  const radius = (rings.length ? rings[rings.length - 1].r : 2) + 1.7;
  return { slots, radius };
}

/**
 * Where each network's platform goes. Side by side they sit on a circle (a row for two), far enough
 * apart not to touch; stacked they're floors, the biggest at the bottom.
 */
export function placeNetworks(radii, mode) {
  const n = radii.length;
  if (n === 0) return { centers: [], reach: 10 };
  if (mode === "stack") {
    const order = radii.map((r, i) => i).sort((a, b) => radii[b] - radii[a]);
    let y = 0; const centers = new Array(n);
    order.forEach((i, k) => { if (k) y += 6.5; centers[i] = { x: 0, y, z: 0 }; });
    return { centers, reach: Math.max(...radii) + 3, height: y };
  }
  if (n === 1) return { centers: [{ x: 0, y: 0, z: 0 }], reach: radii[0] + 3 };
  // Round a circle wide enough that every platform clears the next one along.
  let need = 0;
  if (n > 2) for (let i = 0; i < n; i++) need = Math.max(need, (radii[i] + radii[(i + 1) % n] + 1.5) / (2 * Math.sin(Math.PI / n)));
  const R = Math.max(need, Math.max(...radii) + (n === 2 ? Math.min(...radii) + 0.75 : 2));
  const centers = radii.map((_, i) => {
    const a = n === 2 ? (i ? 0 : Math.PI) : (i / n) * Math.PI * 2 + Math.PI / 2;
    return { x: Math.cos(a) * R, y: 0, z: n === 2 ? 0 : Math.sin(a) * R };
  });
  return { centers, reach: R + Math.max(...radii) + 3 };
}

/** One line for the canvas's text alternative. */
export function describeScene(scene) {
  const d = scene.devices.length, n = scene.nets.length, odd = scene.devices.filter(x => x.state === "odd").length;
  return `A 3D map of ${d} device${d === 1 ? "" : "s"} on ${n} network${n === 1 ? "" : "s"}` +
    (odd ? `, ${odd} doing something unusual` : "") + ". The Devices tab has the same list as text.";
}

// ---------------------------------------------------------------- strain: how hard a device is working to answer

/** 0 to 1: how strained a device's answer time is. Under 40 ms is easy, and 400 ms or more is as strained as it is drawn. */
export function strainOf(ms) {
  if (ms == null || !(ms > 40)) return 0;
  return Math.min(1, Math.log10(ms / 40));
}

// ---------------------------------------------------------------- services

const SERVICE_RANK = { down: 3, slow: 2, off: 1, waiting: 0, up: 0 };
/** The state a device's services come to as a whole: down beats slow beats the rest; null with nothing watched on it. */
export function worstService(services) {
  if (!services || !services.length) return null;
  let worst = "up";
  for (const s of services) if ((SERVICE_RANK[s.state] ?? 0) > (SERVICE_RANK[worst] ?? 0)) worst = s.state;
  return worst;
}

// ---------------------------------------------------------------- outside destinations, as satellites round the internet

/**
 * Where each destination sits as it orbits the internet node: the busier or more recently used the closer in, spread round by a
 * golden angle so a lot of them never line up. `dests` come from /api/flows/map, newest first. `nowMs` is the clock.
 * Returns [{ net, radius, tilt, phase, speed, hot, fresh }]: hot when it was used in the last ten minutes, fresh when it's new.
 */
export function satelliteSlots(dests, nowMs, max = 24) {
  const list = dests.slice(0, max);
  return list.map((d, i) => {
    const last = Date.parse(d.lastSeen) || 0;
    const ageMin = Math.max(0, (nowMs - last) / 60000);
    const radius = 3.1 + Math.min(ageMin / 120, 1) * 2.0 + (i % 4) * 0.6;
    return {
      net: d.net, sample: d.sample, radius, tilt: ((i * 0.9) % 1.4) - 0.7, phase: (i * 2.399963) % (Math.PI * 2),
      speed: 0.18 + (i % 5) * 0.035, hot: ageMin <= 10, fresh: !!d.isNew, devices: d.devices || [],
    };
  });
}

// ---------------------------------------------------------------- the day, replayed

/**
 * What the network looked like at a moment, from /api/timeline: which devices existed, which were online, whether the internet
 * was down, and which had something unusual open. Pure. `tl` is { from, to, hosts: [{id, first, online0}],
 * events: [{h, type, at}] oldest first, outages: [{start, end}], unusual: [{hostId, at, resolvedAt}] }; `tMs` is epoch ms.
 */
export function stateAt(tl, tMs) {
  const present = new Set(), online = new Map();
  const first = new Map(tl.hosts.map(h => [h.id, Date.parse(h.first) || 0]));
  for (const h of tl.hosts) {
    if ((first.get(h.id) || 0) <= tMs) present.add(h.id);
    online.set(h.id, !!h.online0);
  }
  for (const e of tl.events) {
    const at = Date.parse(e.at);
    if (!(at <= tMs)) break;
    online.set(e.h, e.type === "online");
    present.add(e.h);
  }
  const internetDown = (tl.outages || []).some(o => Date.parse(o.start) <= tMs && (!o.end || Date.parse(o.end) > tMs));
  const unusualOpen = new Set((tl.unusual || []).filter(u => Date.parse(u.at) <= tMs && (!u.resolvedAt || Date.parse(u.resolvedAt) > tMs)).map(u => u.hostId));
  return { present, online, internetDown, unusualOpen };
}

// ---------------------------------------------------------------- the house: a drawn plan standing up

/** Scene units to a foot: a two-foot square is one unit, so a room of twelve by fourteen feet is six by seven. */
export const FEET_PER_UNIT = 2;
/** The height of a wall, in scene units: drawn a little short of eight feet so the rooms can be seen into. */
export const WALL_HEIGHT = 1.9;
export const FLOOR_GAP = 4.6;

/** A plan's pixels into scene units, from its own scale (step pixels to perStep of its unit, in feet or metres). */
export function planScale(plan) {
  const feet = (plan.unit === "m" ? 3.28084 : 1) * (plan.perStep || 1);
  return feet / (plan.step || 40) / FEET_PER_UNIT;     // scene units per pixel
}

/**
 * A plan as what to build: walls, door and window marks, labels, and the box it all sits in, in scene units about its own
 * centre. Positions are x right, z down the page, so the plan reads the same from above as it was drawn.
 */
export function houseOf(plan) {
  const k = planScale(plan);
  const bx = (plan.width || 1200) / 2, by = (plan.height || 800) / 2;
  const pt = p => [(p[0] - bx) * k, (p[1] - by) * k];
  const walls = [], doors = [], windows = [], labels = [];
  let min = [Infinity, Infinity], max = [-Infinity, -Infinity];
  const grow = q => { min = [Math.min(min[0], q[0]), Math.min(min[1], q[1])]; max = [Math.max(max[0], q[0]), Math.max(max[1], q[1])]; };
  for (const it of plan.items || []) {
    if (it.k === "wall" || it.k === "door" || it.k === "window") {
      const a = pt(it.a), b = pt(it.b);
      (it.k === "wall" ? walls : it.k === "door" ? doors : windows).push({ a, b });
      grow(a); grow(b);
    } else if (it.k === "label" && it.p) {
      const p = pt(it.p); labels.push({ p, t: String(it.t ?? "") }); grow(p);
    }
  }
  if (!isFinite(min[0])) { min = [-4, -4]; max = [4, 4]; }
  return { walls, doors, windows, labels, min, max, scale: k, size: [max[0] - min[0], max[1] - min[1]], center: [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2], centerPx: [bx, by] };
}

/**
 * A device's spot in the house, from where it was placed on its plan. A placement is a fraction of the plan's width and height;
 * the result is scene units on the same footing as houseOf, with the floor's height added.
 */
export function placeInHouse(place, plan, floorIndex) {
  const k = planScale(plan);
  const x = (place.x * plan.width - plan.width / 2) * k, z = (place.y * plan.height - plan.height / 2) * k;
  return { x, z, y: floorIndex * FLOOR_GAP };
}

// ---------------------------------------------------------------- finding things

/** The chips that narrow what is shown. More than one is "any of these". */
export const FILTERS = [
  { key: "off", label: "Offline", test: d => d.state === "off" },
  { key: "unk", label: "Not approved", test: d => d.state === "unk" },
  { key: "odd", label: "Unusual", test: d => !!d.odd },
  { key: "svc", label: "Service down", test: d => worstService(d.services) === "down" },
  { key: "watched", label: "Watched", test: d => !!d.watching },
  { key: "slow", label: "Slow", test: d => (d.strain || 0) >= 0.5 && d.online },
];

const hay = d => [d.name, d.ip, d.mac, d.vendor, d.kind, d.model, ...(d.tags || [])].filter(Boolean).join(" ").toLowerCase();

/**
 * Whether a device is shown by a filter: { q: text, on: [chip keys], tag }. Every word of the text must be somewhere in its name, address,
 * vendor, kind or tags; a tag, if one is picked, must be one of its tags; and if any chips are on it must be one of them.
 */
export function matchesFilter(d, f) {
  if (!f) return true;
  const words = String(f.q || "").toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length) { const h = hay(d); if (!words.every(w => h.includes(w))) return false; }
  if (f.tag && !(d.tags || []).some(t => t.toLowerCase() === String(f.tag).toLowerCase())) return false;
  const on = f.on ? [...f.on] : [];
  if (on.length && !FILTERS.some(c => on.includes(c.key) && c.test(d))) return false;
  return true;
}

/** How well a device answers a search, for choosing the one to fly to: 0 is no match, and a name is worth more than anything else. */
export function searchRank(d, q) {
  const text = String(q || "").trim().toLowerCase();
  if (!text) return 0;
  const name = String(d.name || "").toLowerCase();
  if (name === text) return 100;
  if (d.ip === text) return 95;
  if (name.startsWith(text)) return 80;
  if (name.includes(text)) return 60;
  if (String(d.ip || "").startsWith(text)) return 55;
  if (matchesFilter(d, { q: text })) return 30;
  return 0;
}

// ---------------------------------------------------------------- what needs looking at

/**
 * What is wrong now, most urgent first, as a list to work through: watched devices that are down, services down, things doing
 * something unusual, a device scanning the network, unapproved devices online, and devices answering slowly.
 * `scanning` is the set of device keys the flow watch says are scanning.
 */
export function problemsOf(devices, scanning = new Set()) {
  const out = [];
  for (const d of devices) {
    const who = d.name && d.name !== "—" ? d.name : d.ip;
    if (d.watching && d.state === "off") out.push({ key: d.key, kind: "down", rank: 100, title: `${who} is down`, detail: "A watched device that isn't answering." });
    if (worstService(d.services) === "down") {
      const names = d.services.filter(s => s.state === "down").map(s => s.name);
      out.push({ key: d.key, kind: "service", rank: 90, title: `${names.join(", ")} on ${who} is down`, detail: `${who} itself ${d.online ? "is answering" : "isn't answering"}.` });
    }
    if (scanning.has(d.key)) out.push({ key: d.key, kind: "scan", rank: 85, title: `${who} is scanning the network`, detail: "It has reached many addresses or ports inside a minute." });
    if (d.odd) out.push({ key: d.key, kind: "odd", rank: 80, title: d.odd.title || `${who} is doing something unusual`, detail: d.odd.detail || "" });
    if (d.state === "unk" && d.online) out.push({ key: d.key, kind: "new", rank: 40, title: `${who} is online and not approved`, detail: "Mark it known if you know what it is." });
    if ((d.strain || 0) >= 0.75 && d.online && d.ms != null) out.push({ key: d.key, kind: "slow", rank: 30, title: `${who} is answering slowly`, detail: `${Math.round(d.ms)} ms.` });
  }
  return out.sort((a, b) => b.rank - a.rank || a.title.localeCompare(b.title));
}

/** For each device with others plugged into it: how many are behind it, how many of those are offline, and which. */
export function behindOf(devices) {
  const kids = new Map();
  for (const d of devices) if (d.parent) { if (!kids.has(d.parent)) kids.set(d.parent, []); kids.get(d.parent).push(d); }
  const byKey = new Map(devices.map(d => [d.key, d]));
  const out = new Map();
  const walk = (key, seen) => {
    const keys = [];
    for (const c of kids.get(key) || []) { if (seen.has(c.key)) continue; seen.add(c.key); keys.push(c.key, ...walk(c.key, seen)); }
    return keys;
  };
  for (const key of kids.keys()) {
    const keys = walk(key, new Set([key]));
    out.set(key, { count: keys.length, offline: keys.filter(k => byKey.get(k)?.state === "off").length, keys });
  }
  return out;
}

// ---------------------------------------------------------------- traffic as it is measured

/** A measured rate in bytes a second as how busy its link is drawn, 1 to 9 on a log scale (1 kB/s is 1, 1 MB/s is 9); 0 when there is none. */
export function rateScale(bytesPerSec) {
  if (!(bytesPerSec > 0)) return 0;
  return Math.max(1, Math.min(9, Math.round(1 + Math.log10(Math.max(1, bytesPerSec / 1000)) * 2.6)));
}

/** Bytes a second as the bits a line is sold in: "340 kbps", "1.2 Mbps". */
export function formatRate(bytesPerSec) {
  const b = (bytesPerSec || 0) * 8;
  if (b >= 1e9) return (b / 1e9).toFixed(1) + " Gbps";
  if (b >= 1e6) return (b / 1e6).toFixed(b >= 1e7 ? 0 : 1) + " Mbps";
  if (b >= 1e3) return Math.round(b / 1e3) + " kbps";
  return Math.round(b) + " bps";
}

// ---------------------------------------------------------------- placing a device by clicking the house

/** The other way round from placeInHouse: a point on the floor, in scene units, as the fraction of the plan a placement is saved as. */
export function placementFromPoint(x, z, plan) {
  const k = planScale(plan);
  const fx = (x / k + plan.width / 2) / plan.width, fy = (z / k + plan.height / 2) / plan.height;
  return { x: Math.min(1, Math.max(0, fx)), y: Math.min(1, Math.max(0, fy)) };
}

// ---------------------------------------------------------------- what if it died

/**
 * What would stop if a device died, from what is drawn: the device itself, and who depends on it. A switch or access point takes with it
 * everything plugged into it, recorded on the Map (they lose the network); the gateway takes nothing down but leaves every device on its
 * network without the internet. Any other device is only itself. Returns null for a key that isn't there.
 *   { key, name, reason: "network" | "internet" | "alone", cut: [keys], internet: bool,
 *     counts: { cut, cutOnline, services, watched }, serviceNames: [..] }
 * `cut` is the devices that lose the network (reason "network") or the internet (reason "internet"); services and watched count the device
 * itself and, for "network", the ones behind it that would go dark: services not already down, and watched devices that are online now.
 */
export function outageOf(devices, key) {
  const byKey = new Map(devices.map(d => [d.key, d]));
  const t = byKey.get(key);
  if (!t) return null;
  const kids = new Map();
  for (const d of devices) if (d.parent) { if (!kids.has(d.parent)) kids.set(d.parent, []); kids.get(d.parent).push(d.key); }
  let cut = [], reason = "alone";
  if (t.gw) { cut = devices.filter(d => d.net === t.net && d.key !== t.key).map(d => d.key); reason = cut.length ? "internet" : "alone"; }
  else {
    const seen = new Set([t.key]), walk = k => { for (const c of kids.get(k) || []) if (!seen.has(c)) { seen.add(c); cut.push(c); walk(c); } };
    walk(t.key);
    if (cut.length) reason = "network";
  }
  const dark = reason === "network" ? [t, ...cut.map(k => byKey.get(k))] : [t];
  const serviceNames = [];
  let services = 0, watched = 0;
  for (const d of dark) {
    for (const s of d.services || []) if (s.state !== "down") { services++; serviceNames.push(`${s.name} on ${d.name}`); }
    if (d.watching && d.online) watched++;
  }
  return {
    key: t.key, name: t.name && t.name !== "—" ? t.name : t.ip, reason, cut, internet: !!t.gw, net: t.net,
    counts: { cut: cut.length, cutOnline: cut.filter(k => byKey.get(k).online).length, services, watched }, serviceNames: serviceNames.slice(0, 3),
  };
}

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
/** The outage as sentences: what would stop, in the order it matters. */
export function outageText(o) {
  if (!o) return "";
  const lines = [];
  if (o.reason === "internet") lines.push(`${plural(o.counts.cut, "device", "devices")} on ${o.net} would lose the internet. They'd still reach each other.`);
  else if (o.reason === "network") lines.push(`${plural(o.counts.cut, "device", "devices")} would lose the network${o.counts.cutOnline < o.counts.cut ? ` (${o.counts.cutOnline} of them online now)` : ""}.`);
  else lines.push("Nothing else depends on it.");
  if (o.counts.services) lines.push(`${plural(o.counts.services, "watched service", "watched services")} would go down${o.serviceNames.length ? `, such as ${o.serviceNames[0]}` : ""}.`);
  if (o.counts.watched) lines.push(`${plural(o.counts.watched, "watched device", "watched devices")} would show offline and alert.`);
  return lines.join(" ");
}

// ---------------------------------------------------------------- usage: how much each device has moved

/** The windows the usage skyline can show. The history is kept by the hour, so two hours is the shortest. */
export const USAGE_WINDOWS = [
  { hours: 2, label: "About 2 hours" },
  { hours: 24, label: "24 hours" },
  { hours: 168, label: "7 days" },
];

/** Bytes as people read them: "840 kB", "12.4 GB". */
export function formatBytes(n) {
  const b = Math.max(0, Number(n) || 0);
  if (b >= 1e12) return (b / 1e12).toFixed(1) + " TB";
  if (b >= 1e9) return (b / 1e9).toFixed(b >= 1e10 ? 0 : 1) + " GB";
  if (b >= 1e6) return (b / 1e6).toFixed(b >= 1e7 ? 0 : 1) + " MB";
  if (b >= 1e3) return Math.round(b / 1e3) + " kB";
  return Math.round(b) + " B";
}

/** 0 to 1: how tall a tower for this many bytes, against the most any device moved. A square root, so a modest device is still a visible stub. */
export function towerHeight(bytes, max) {
  if (!(bytes > 0) || !(max > 0)) return 0;
  return Math.max(0.05, Math.min(1, Math.sqrt(bytes / max)));
}

/**
 * The usage as the scene needs it: for each device (by key) its bytes in and out and its tower height, and the biggest few to list.
 * `items` is /api/traffic/usage's devices: [{ hostId, rx, tx }].
 */
export function usageOf(devices, items, top = 5) {
  const byId = new Map(devices.map(d => [d.id, d]));
  const rows = [];
  for (const it of items || []) {
    const d = byId.get(it.hostId);
    if (!d) continue;
    const rx = it.rx || 0, tx = it.tx || 0;
    if (rx + tx > 0) rows.push({ key: d.key, name: d.name && d.name !== "—" ? d.name : d.ip, rx, tx, total: rx + tx });
  }
  rows.sort((a, b) => b.total - a.total);
  const max = rows.length ? rows[0].total : 0;
  const byKey = new Map(rows.map(r => [r.key, { ...r, height: towerHeight(r.total, max), upShare: r.total ? r.tx / r.total : 0 }]));
  return { byKey, top: rows.slice(0, top), max, total: rows.reduce((s, r) => s + r.total, 0) };
}

// ---------------------------------------------------------------- exposure: what is open, expiring or serving

/** The ports whose being open is worth a flag on a device: no reason to leave one open on a home network. */
export const RISKY_PORTS = { 21: "FTP", 23: "Telnet", 3389: "Remote Desktop", 5900: "VNC" };

/**
 * What to flag on a device. `ports` the ports found open, `cert` { days } for a certificate that has run out (negative) or soon will,
 * `upnp` whether it answers UPnP, `roles` the jobs it does for the network ("DHCP", "DNS"). Returns
 * { risks: [{ kind, label, sev: "high"|"med" }], roles, sev: "high"|"med"|"" }, with the worst first.
 */
export function exposureOf({ ports = [], cert = null, upnp = false, roles = [] } = {}) {
  const risks = [];
  for (const p of [...ports].sort((a, b) => a - b)) if (RISKY_PORTS[p]) risks.push({ kind: "port", label: `${RISKY_PORTS[p]} is open (port ${p})`, sev: "high" });
  if (cert && Number.isFinite(cert.days)) {
    if (cert.days < 0) risks.push({ kind: "cert", label: `Its certificate expired ${-cert.days} day${cert.days === -1 ? "" : "s"} ago`, sev: "high" });
    else if (cert.days <= 14) risks.push({ kind: "cert", label: `Its certificate expires in ${cert.days} day${cert.days === 1 ? "" : "s"}`, sev: "med" });
  }
  if (upnp) risks.push({ kind: "upnp", label: "Answers UPnP: any device can ask it to open ports to the internet", sev: "high" });
  risks.sort((a, b) => (a.sev === b.sev ? 0 : a.sev === "high" ? -1 : 1));
  return { risks, roles: [...roles], sev: risks.some(r => r.sev === "high") ? "high" : risks.length ? "med" : "" };
}

// ---------------------------------------------------------------- the briefing: a flight through what happened

/**
 * The stops of a flight through the day, worst first: { key (a device's key, "internet" or null for the whole scene), title, detail, kind, rank }.
 *   devices  the scene's devices   problems  problemsOf(...)   timeline  /api/timeline   alerts  /api/alerts (newest first)
 * New devices, devices that went down (and whether they came back), what is wrong now, alerts that aren't about one device, and internet
 * outages. At most `max` stops; an empty list when nothing happened.
 */
export function briefingOf({ devices, problems = [], timeline = null, alerts = [], nowMs = Date.now(), max = 9 }) {
  const stops = [], byId = new Map(devices.map(d => [d.id, d])), taken = new Set();
  const who = d => (d.name && d.name !== "—" ? d.name : d.ip);
  const clock = ms => new Date(ms).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  const add = s => { stops.push(s); if (s.key && s.key !== "internet") taken.add(s.key); };
  for (const p of problems) if (!taken.has(p.key)) add({ key: p.key, title: p.title, detail: p.detail, kind: p.kind, rank: p.rank });
  if (timeline) {
    const from = Date.parse(timeline.from) || 0, last = new Map(), downs = new Map();
    for (const e of timeline.events || []) {
      const at = Date.parse(e.at);
      if (e.type === "offline" && !downs.has(e.h)) downs.set(e.h, at);
      last.set(e.h, { type: e.type, at });
    }
    for (const [id, at] of downs) {
      const d = byId.get(id); if (!d || taken.has(d.key)) continue;
      const l = last.get(id), back = l.type === "online";
      add({ key: d.key, kind: "down", rank: 60, title: back ? `${who(d)} dropped off and came back` : `${who(d)} went offline`,
        detail: back ? `Down from ${clock(at)} to ${clock(l.at)}.` : `Since ${clock(at)}.` });
    }
    for (const h of timeline.hosts || []) {
      const d = byId.get(h.id); if (!d || taken.has(d.key)) continue;
      if (Date.parse(h.first) > from + 1000) add({ key: d.key, kind: "new", rank: 50, title: `${who(d)} joined the network`, detail: `First seen at ${clock(Date.parse(h.first))}${d.known ? "." : ", and not approved yet."}` });
    }
    for (const o of timeline.outages || []) {
      const s = Date.parse(o.start), e = o.end ? Date.parse(o.end) : nowMs, min = Math.max(1, Math.round((e - s) / 60000));
      if (e < from) continue;
      add({ key: "internet", kind: "internet", rank: 70, title: `The internet was down for ${min} minute${min === 1 ? "" : "s"}`, detail: `From ${clock(s)}${o.end ? ` to ${clock(e)}` : ", and it still is"}${o.local ? "" : ". The router was answering."}` });
    }
  }
  const day = nowMs - 24 * 3600 * 1000;
  for (const a of alerts) {
    if (!["dhcp", "dns", "ra", "security", "port", "cert"].includes(a.kind)) continue;
    if (!(Date.parse(a.at) >= day)) continue;
    add({ key: null, kind: "alert", rank: 65, title: a.title, detail: String(a.detail || "").slice(0, 160) });
  }
  stops.sort((a, b) => b.rank - a.rank || a.title.localeCompare(b.title));
  return stops.slice(0, max);
}

// ---------------------------------------------------------------- weather: the network's health as a sky

/**
 * The sky for a network score: 90 and up is clear, 75 fair, 50 overcast with some rain, and under that a storm. Everything is 0 to 1
 * except `tint`, the colour the sky and the distance are multiplied by. A score that is off (null) is a fair sky.
 */
export function weatherOf(score) {
  if (score == null || !Number.isFinite(Number(score))) score = 80;
  const s = Math.max(0, Math.min(100, Number(score)));
  const key = s >= 90 ? "clear" : s >= 75 ? "fair" : s >= 50 ? "overcast" : "storm";
  const cloud = Math.max(0.04, Math.min(1, (96 - s) / 46));
  const rain = s >= 75 ? 0 : Math.max(0.15, Math.min(1, (80 - s) / 55 + (s < 50 ? 0.25 : 0)));
  const storm = s < 50 ? Math.min(1, 0.4 + (50 - s) / 70) : 0;
  const fog = 0.011 + (1 - s / 100) * 0.012;
  const k = s / 100;
  const tint = [0.62 + k * 0.44, 0.66 + k * 0.4, 0.78 + k * 0.3];
  const label = key === "clear" ? "Clear" : key === "fair" ? "Fair" : key === "overcast" ? "Overcast" : "Stormy";
  return { key, label, score: s, cloud, rain, storm, fog, tint };
}

// ---------------------------------------------------------------- replay: longer ranges, and what happened when

/** How far back the replay can go. */
export const REPLAY_RANGES = [
  { hours: 24, label: "Last day" },
  { hours: 72, label: "3 days" },
  { hours: 168, label: "7 days" },
  { hours: 720, label: "30 days" },
];

/** How fast to play a range: the whole of it in this many seconds. */
export const REPLAY_SECONDS = [{ s: 12, label: "In 12 seconds" }, { s: 60, label: "In a minute" }, { s: 180, label: "In three minutes" }];
/** Milliseconds of the past to move through for each second of watching. */
export function replaySpeed(rangeMs, seconds) { return Math.max(1, rangeMs / Math.max(1, seconds)); }

/**
 * Marks along the replay's slider: where in the range (0 to 1) an internet outage began, something unusual began, or a device first
 * appeared. Oldest first. Each is { at, kind: "outage" | "unusual" | "arrive", label }.
 */
export function markersOf(tl) {
  const from = Date.parse(tl.from), to = Date.parse(tl.to), span = to - from;
  if (!(span > 0)) return [];
  const f = ms => (ms - from) / span, out = [];
  for (const o of tl.outages || []) { const s = Date.parse(o.start); if (s >= from && s <= to) out.push({ at: f(s), kind: "outage", label: "Internet down" }); }
  for (const u of tl.unusual || []) { const s = Date.parse(u.at); if (s >= from && s <= to) out.push({ at: f(s), kind: "unusual", label: u.title || "Something unusual" }); }
  for (const h of tl.hosts || []) { const s = Date.parse(h.first); if (s > from + 1000 && s <= to) out.push({ at: f(s), kind: "arrive", label: "A device arrived" }); }
  return out.sort((a, b) => a.at - b.at);
}
