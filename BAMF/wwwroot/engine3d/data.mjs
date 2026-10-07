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
 *  input: { hosts, kindOf(h), nameOf(h), unusual: [{hostId, kind, open, title, detail}], gatewayIp(subnet), rateOf(h) }
 */
export function buildScene({ hosts, kindOf, nameOf, unusual = [], gatewayIp = () => null, rateOf = () => 2 }) {
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
    };
    net.devices.push(d); devices.push(d);
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
