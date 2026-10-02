// The 3D view's data side, with nothing to draw in it: which model a device gets, what state it's in,
// which network it sits on, and where everything goes. Plain functions, so they're tested on their own.

// Every kind BAMF's Map can give a device, and the model that stands for it. `size` is the model's
// longest side on the platform; the models themselves are all normalised to 1.
export const MODELS = {
  router: 1.8, switch: 1.6, ap: 1.4, nas: 1.3, server: 1.7, desktop: 1.4, laptop: 1.5, printer: 1.4, tv: 2.0, speaker: 1.2,
  camera: 1.3, pi: 1.0, phone: 0.85, tablet: 1.1, iot: 1.0, game: 1.5, plug: 0.9, light: 1.0, vm: 1.2, vpn: 1.3, device: 1.1,
};
const ALIAS = { vswitch: "switch", net: "router" };

/** The model for a device: its Map kind, with a Raspberry Pi told apart from other servers. */
export function modelFor(kind, vendor = "") {
  let k = ALIAS[kind] || kind;
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
      model: modelFor(kind, h.vendor), state: stateOf(h, !!odd), online: !!h.online, known: !!h.known, watched: !!h.watched,
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
