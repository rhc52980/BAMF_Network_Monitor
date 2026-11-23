// ---- network map ----
// One card per network, drawn around the network itself. BAMF learns from ARP
// who is present on a segment, never how anything is cabled, so a line here
// means "on this network" and nothing more. Two things it does know for
// certain are marked: the default gateway, from this machine's routing table,
// and this machine itself, from its own address on the network.
const SVGNS = "http://www.w3.org/2000/svg";
function svgEl(tag, attrs) {
  const e = document.createElementNS(SVGNS, tag);
  for (const k in attrs) e.setAttribute(k, attrs[k]);
  return e;
}

const MAP = {
  SPACING: 26,      // arc length between neighbours on a single ring
  MIN_R: 120,       // smallest device ring
  NODE_R: 7, SPECIAL_R: 10, HUB_R: 22,
  INNER: 56,        // gateway above the hub, this machine below it
  LABEL_PAD: 124,   // room outside the ring for rotated names
  MAX_SINGLE: 60,   // beyond this, several rings and names move to tooltips
  WEDGE: 20,        // degrees kept clear either side of the gateway and this machine
  WEDGE_SW: 30,     // wider when switches are drawn, so none crowds the gateway
  SW_R0: 92,        // ring of switches plugged into the router (or not recorded)
  SW_STEP: 44,      // each switch further down a chain sits this much further out
  SW_GAP: 78,       // room between the outermost switch and its devices
};

// Structure of the map as last drawn. As with the network strip, the drawing
// is rebuilt only when that changes; status and tooltips update in place, so
// a poll never redraws the map under the pointer.
let mapSig = null;

function mapClusters() {
  // A remote site's network has no map here: its own BAMF draws it.
  const nets = network.includes(" \u00b7 ") ? []
    : network === "all"
    // A network nothing is on any more (its devices forgotten) isn't drawn, unless it's one still being scanned.
    ? [...new Set([...subnets, ...hosts.filter(h => !h.remote && !h.forgotten).map(h => h.subnet).filter(Boolean)])]
        .sort((a, b) => ipNum(a.split("/")[0]) - ipNum(b.split("/")[0]))
    : [network];
  return nets.map(subnet => {
    const place = networkPlaces[subnet] || {};
    // A device on several networks is drawn on each, at its address there.
    const members = hosts.filter(h => !h.remote && onNet(h, subnet) && !h.ignored && !h.forgotten && tagOk(h)).map(h => onNetView(h, subnet));
    const selfMac = (place.selfMac || "").toUpperCase();
    let self = selfMac ? members.find(h => macsOf(h).some(m => (m || "").toUpperCase() === selfMac)) || null : null;
    // The gateway you declared, or the routing table's.
    const gwAt = gatewayOf(subnet);
    let gw = !gwAt ? null
      : gwAt.hostId ? members.find(h => h.id === gwAt.hostId) || null
      : members.filter(h => h.ip === gwAt.ip).sort((a, b) => Number(b.online) - Number(a.online))[0] || null;
    if (gw && self && gw.id === self.id) gw = null;   // this machine is the router: show it once
    // Known from the machine itself even when no scan has recorded it: a ping
    // sweep never sees its own host, and a gateway can be slow to answer.
    if (!self && place.selfIp) self = { synthetic: true, ip: place.selfIp, mac: place.selfMac || "" };
    if (!gw && gwAt && gwAt.ip !== place.selfIp) gw = { synthetic: true, ip: gwAt.ip };
    // The switch layout the user recorded. A switch is drawn on its own
    // network, and on any other network with a device recorded on it; every
    // switch above a drawn one is drawn too, so no chain floats unattached.
    const swById = new Map(switches.map(s => [s.id, s]));
    // BAMF recorded on a switch is drawn there, under that switch, rather than
    // on its own beside the gateway.
    const selfPlaced = !!self && !self.synthetic && swById.has(self.switchId);
    const placed = members.filter(h => (h !== self || selfPlaced) && h !== gw && swById.has(h.switchId));
    const shown = new Set(switches.filter(s => s.subnet === subnet).map(s => s.id));
    placed.forEach(h => shown.add(h.switchId));
    // A wireless SSID is drawn with the access point that broadcasts it.
    for (const id of [...shown]) {
      const sw = swById.get(id);
      if (sw && sw.kind === "ssid" && swById.has(sw.runsOn)) shown.add(sw.runsOn);
    }
    for (const id of [...shown]) {
      let sw = swById.get(id);
      // A VPN drawn for its clients on another network stands alone there:
      // it joins no networks together, so nothing above it comes along.
      if (sw && sw.kind === "vpn" && sw.subnet !== subnet) continue;
      while (sw && sw.uplink === "switch" && swById.has(sw.uplinkSwitch) && !shown.has(sw.uplinkSwitch)) {
        shown.add(sw.uplinkSwitch);
        sw = swById.get(sw.uplinkSwitch);
      }
    }
    const sws = switches.filter(s => shown.has(s.id));
    const swHosts = new Set(sws.map(s => s.hostId).filter(Boolean));
    const ring = members.filter(h => h !== self && h !== gw && !placed.includes(h) && !swHosts.has(h.id)).sort((a, b) => {
      const fa = typeFamily(a), fb = typeFamily(b);
      if ((fa === null) !== (fb === null)) return fa === null ? 1 : -1;
      return (fa || "").localeCompare(fb || "") || dispName(a).localeCompare(dispName(b)) || ipNum(a.ip) - ipNum(b.ip);
    });
    return { subnet, place, members, self: selfPlaced ? null : self, selfIds: self && !self.synthetic ? [self.id] : [],
      gw, gwDeclared: !!(gwAt && gwAt.declared), ring, placed, switches: sws, mode: scanModes[subnet] || "" };
  });
}

// The whole-network view: every network as one diagram, drawn by the cabling
// you recorded rather than by network. Each device appears once, at its main
// address, and carries its network's colour; BAMF and a router with an
// address on every network appear once too.
const WHOLE = "*";
const NET_COLORS = ["#4fb3d9", "#e0a040", "#b58af0", "#58c98a", "#e56f8f", "#8fa3b8", "#d4c04a", "#5fc4bd"];
let mapWhole = false;
try { mapWhole = localStorage.getItem("bamf-map-whole") === "1"; } catch { }
function wholeActive(clusters) { return mapWhole && mapStyle === "topology" && network === "all" && clusters.length > 1; }
// Also the map of the networks one router is declared the gateway of, with
// opts.gateway its device id: only those networks, and only their switches.
function wholeCluster(clusters, opts = {}) {
  const nets = clusters.map(c => c.subnet);
  const netColors = Object.fromEntries(nets.map((n, i) => [n, NET_COLORS[i % NET_COLORS.length]]));
  const real = h => h && !h.synthetic ? hosts.find(x => x.id === h.id) || h : h;
  const members = hosts.filter(h => !h.ignored && !h.forgotten && tagOk(h) && nets.some(n => onNet(h, n)));
  // BAMF, once, even with an address (and maybe its own NIC) on every network.
  // Recorded on a switch, it's drawn there instead, and a record it has on
  // another network (a second network card) isn't drawn again.
  const allSelfIds = [...new Set(clusters.flatMap(c => c.selfIds))];
  const selfPlaced = clusters.some(c => c.selfIds.length && !c.self);
  const selves = selfPlaced ? [] : clusters.map(c => c.self).filter(Boolean);
  const self = real(selves.find(h => !h.synthetic) || selves[0] || null);
  const selfIds = new Set(selves.filter(h => !h.synthetic).map(h => h.id));
  const tops = clusters.map(c => ({ net: c.subnet, gw: real(c.gw) }));
  const gwIds = new Set(tops.filter(t => t.gw && !t.gw.synthetic).map(t => t.gw.id));
  const swById = new Map(switches.map(s => [s.id, s]));
  // A gateway recorded on a switch stays there: it's a second router, and its
  // network hangs from it.
  const placed = members.filter(h => !selfIds.has(h.id) && swById.has(h.switchId));
  const swHosts = new Set(switches.map(s => s.hostId).filter(Boolean));
  const ring = members.filter(h => !allSelfIds.includes(h.id) && !gwIds.has(h.id) && !placed.includes(h) && !swHosts.has(h.id)).sort((a, b) => {
    const fa = typeFamily(a), fb = typeFamily(b);
    if ((fa === null) !== (fb === null)) return fa === null ? 1 : -1;
    return (fa || "").localeCompare(fb || "") || dispName(a).localeCompare(dispName(b)) || ipNum(a.ip) - ipNum(b.ip);
  });
  const boxes = nets.map(net => ({ net, hosts: ring.filter(h => (nets.includes(h.subnet) ? h.subnet : nets[0]) === net) }));
  const sws = opts.gateway ? switches.filter(s => clusters.some(c => c.switches.includes(s))) : switches.slice();
  return { subnet: opts.gateway ? "gw:" + opts.gateway : WHOLE, whole: true, group: !!opts.gateway,
    place: {}, members, self, selfIds: allSelfIds, gw: tops[0].gw, tops, boxes, ring, placed,
    switches: sws, mode: "", nets, netColors };
}

// Networks that one router is declared the gateway of are drawn as one map,
// all hanging off that router, in place of a card each.
function gatewayGroups(perNet) {
  if (network !== "all" || mapStyle !== "topology") return perNet;
  const byGw = new Map();
  for (const c of perNet) {
    const d = declaredGw(c.subnet);
    if (d && hosts.some(h => h.id === d.hostId)) {
      if (!byGw.has(d.hostId)) byGw.set(d.hostId, []);
      byGw.get(d.hostId).push(c);
    }
  }
  const out = [];
  for (const c of perNet) {
    const d = declaredGw(c.subnet);
    const group = d && byGw.get(d.hostId);
    if (!group || group.length < 2) { out.push(c); continue; }
    if (group[0] === c) out.push(wholeCluster(group, { gateway: d.hostId }));
  }
  return out;
}

function mapSignature(clusters) {
  const key = n => n ? (n.synthetic ? "~" + n.ip : n.id) : null;
  return JSON.stringify([network, mapStyle, tagFilter, clusters.map(c => [
    c.subnet, c.mode, key(c.self), c.selfIds, key(c.gw),
    c.whole ? [c.tops.map(t => [t.net, key(t.gw)]), c.boxes.map(b => [b.net, b.hosts.map(h => h.id)])] : null,
    c.ring.map(h => [h.id, typeFamily(h), deviceKind(h), dispName(h), h.ip]),
    c.switches.map(s => [s.id, s.name, s.kind, s.subnet, s.ports, s.hostId, s.uplink, s.uplinkSwitch, s.uplinkPort, s.portLabels]),
    c.placed.map(h => [h.id, h.switchId, h.switchPort, deviceKind(h), dispName(h), h.ip]),
    c.members.some(hasSignal),
    mapStyle === "topology" ? mapPositions[c.subnet] || null : null,
  ])]);
}

function mapNodeClass(h, role) {
  if (h.synthetic) return "node synthetic " + role;
  return "node " + role + (h.online ? " on" : " off") + (h.known ? "" : " unknown") + (h.watched ? " watched" : "");
}

// How well a Wi-Fi device hears its access point, for the Signal colours switch: "excellent", "good", "fair" or "weak", or "" when the
// switch is off, the device is offline (a reading from when it left says nothing) or the controller gave no reading.
function hasSignal(h) { return !!(h && h.wifi && h.wifi.signal != null && h.wifi.quality); }
function sigLevel(h) { return mapSignal && h && h.online && hasSignal(h) ? h.wifi.quality : ""; }
// Put the level on an element's class list, replacing whatever it had.
function sigApply(el, h) {
  for (const c of [...el.classList]) if (c.startsWith("sig-")) el.classList.remove(c);
  const lv = sigLevel(h);
  if (lv) el.classList.add("sig-" + lv);
  return lv;
}
// The little bar glyph (like a phone's) at a device tile's corner: made when a device has a level, dropped when it hasn't.
function sigGlyph(g, lv) {
  let s = g.querySelector(":scope > .t-sig");
  if (!lv) { if (s) s.remove(); return; }
  if (!s) {
    s = svgEl("g", { class: "t-sig", transform: `translate(${TOPO.T - 15},${-TOPO.T - 3})` });
    for (let i = 0; i < 4; i++) s.appendChild(svgEl("rect", { x: i * 3.8, y: -(3 + i * 2.4), width: 2.6, height: 3 + i * 2.4, rx: .6 }));
    g.appendChild(s);
  }
}

function mapTitle(h, role) {
  if (h.synthetic) {
    return role === "self"
      ? `This machine, running BAMF\n${h.ip}`
      : `Gateway\n${h.ip}\nNot recorded by a scan yet`;
  }
  const lines = [dispName(h) === "—" ? h.ip : dispName(h), `${h.ip} · ${h.mac}`];
  const others = curAddrs(h).filter(a => a.ip !== h.ip).map(a => a.ip);
  if (others.length) lines.push("Also at " + others.join(", "));
  if (h.interfaces) lines.push(`${h.interfaces.length + 1} network cards: ${macsOf(h).join(", ")}`);
  if (h.vendor) lines.push(h.vendor);
  lines.push(h.typeName ? "Type: " + h.typeName + " (yours)" : h.osGuess ? "Guess: " + h.osGuess : "No guess yet");
  const sw = h.switchId ? switches.find(s => s.id === h.switchId) : null;
  if (sw) lines.push(`Plugged into ${sw.name}` + (h.switchPort ? `, ${portText(sw, h.switchPort)}` : "") + (h.switchAuto ? ` (from ${h.switchAuto === "unifi" ? "your UniFi controller" : "the switch's own table"})` : " (your record)"));
  if (h.wifi && h.wifi.signal != null) lines.push(`Wi-Fi ${h.wifi.signal} dBm (${h.wifi.quality}${h.wifi.weak ? ", it may struggle" : ""}) on ${h.wifi.ap}`);
  if (role === "gw" || gwNetsOf(h).length) {
    const nets = gwNetsOf(h);
    lines.push(nets.length ? `Gateway of ${nets.join(", ")} (you declared it)` : "Default gateway for this network (from BAMF's routing table)");
  }
  if (role === "self") lines.push("This machine, running BAMF");
  lines.push((h.online ? "Online" : "Offline, last seen " + fmtAgo(h.lastSeen)) + (h.known ? "" : " · unknown device"));
  return lines.join("\n");
}

function mapLabel(h) {
  const n = h.synthetic ? "" : dispName(h);
  const s = n && n !== "—" ? n : h.ip;
  return s.length > 18 ? s.slice(0, 17) + "…" : s;
}

function buildClusterSvg(c) {
  // Slots round the outer ring, in order. Devices recorded on a switch come
  // first, switch by switch in port order, so each switch's devices form one
  // arc with the switch sitting inside it. Then the rest in type order, with
  // an empty slot between two types so each type reads as its own arc.
  const slots = [];                 // { h, sw } a device; { stub } an empty switch's place; null a gap
  const swSpan = new Map();         // switch id -> [first, last] slot of everything under it
  const swDepth = new Map();        // switch id -> 0 for a top switch, 1 below that, ...
  if (c.switches.length) {
    const shownIds = new Set(c.switches.map(s => s.id));
    const under = id => [
      ...c.placed.filter(h => h.switchId === id).map(h => ({ port: h.switchPort, name: dispName(h), h })),
      ...c.switches.filter(s => s.uplink === "switch" && s.uplinkSwitch === id).map(s => ({ port: s.uplinkPort, name: s.name, s })),
    ].sort((a, b) => (a.port || 1e9) - (b.port || 1e9) || a.name.localeCompare(b.name));
    const visit = (sw, depth) => {
      if (swDepth.has(sw.id)) return;   // the server refuses loops; this is only a backstop
      swDepth.set(sw.id, depth);
      const first = slots.length;
      for (const k of under(sw.id)) {
        if (k.h) slots.push({ h: k.h, sw });
        else visit(k.s, depth + 1);
      }
      if (slots.length === first) slots.push({ stub: sw });   // a switch with nothing recorded still needs a place
      swSpan.set(sw.id, [first, slots.length - 1]);
    };
    c.switches.filter(s => !(s.uplink === "switch" && shownIds.has(s.uplinkSwitch)))
      .forEach((sw, i) => { if (i) slots.push(null); visit(sw, 0); });
    if (c.ring.length) slots.push(null);
  }
  let prev;
  c.ring.forEach((h, i) => {
    const f = typeFamily(h);
    if (i > 0 && f !== prev) slots.push(null);
    slots.push({ h });
    prev = f;
  });
  // Keep a clear wedge above the hub for the gateway and below it for this
  // machine, so no device's line runs through either of them or their labels.
  const wedge = c.switches.length ? MAP.WEDGE_SW : MAP.WEDGE;
  const wedgeTop = c.gw ? wedge : 0, wedgeBottom = c.self ? wedge : 0;
  const availDeg = 360 - 2 * wedgeTop - 2 * wedgeBottom;
  const availRad = availDeg * Math.PI / 180;
  const angleAt = (k, count) => {
    let a = -90 + wedgeTop + (k + 0.5) * availDeg / count;
    if (wedgeBottom && a >= 90 - wedgeBottom) a += 2 * wedgeBottom;   // step over the bottom wedge
    return a * Math.PI / 180;
  };
  const swR = depth => MAP.SW_R0 + depth * MAP.SW_STEP;
  const minR = c.switches.length ? Math.max(MAP.MIN_R, swR(Math.max(...swDepth.values())) + MAP.SW_GAP) : MAP.MIN_R;
  const single = slots.length <= MAP.MAX_SINGLE;
  const at = [];   // per slot: [radius, angle]
  if (single) {
    const r = Math.max(minR, slots.length * MAP.SPACING / availRad);
    slots.forEach((_, i) => at.push([r, angleAt(i, slots.length)]));
  } else if (!c.switches.length) {
    // Too many to name legibly on one ring: stack rings, names in tooltips.
    let r = MAP.MIN_R;
    for (let i = 0; i < slots.length; r += 30) {
      const cap = Math.max(8, Math.floor(availRad * r / (MAP.SPACING * 0.8)));
      const n = Math.min(cap, slots.length - i);
      for (let k = 0; k < n; k++) at.push([r, angleAt(k, n)]);
      i += n;
    }
  } else {
    // Too many for one ring, and each switch's devices have to stay in its
    // own arc: so the slots keep a single order round the circle and step in
    // and out across a few rings instead of filling one ring after another.
    const k = Math.min(4, Math.ceil(slots.length / 40));
    const r = Math.max(minR, slots.length * MAP.SPACING * 0.8 / (availRad * k));
    slots.forEach((_, i) => at.push([r + (i % k) * 30, angleAt(i, slots.length)]));
  }
  const outer = at.length ? Math.max(...at.map(p => p[0])) : minR;
  const half = outer + MAP.NODE_R + (single ? MAP.LABEL_PAD : 30);
  const svg = svgEl("svg", {
    viewBox: `${-half} ${-half} ${2 * half} ${2 * half}`, class: "map-svg",
    role: "group", "aria-label": `Network ${c.subnet}`,
  });
  const spokes = svgEl("g", { class: "spokes" });
  const hub = svgEl("g", { class: "hub-g" });
  const nodes = svgEl("g", { class: "nodes" });
  svg.append(spokes, hub, nodes);

  const on = c.members.filter(h => h.online).length;
  hub.appendChild(svgEl("circle", { r: MAP.HUB_R, class: "hub" }));
  const hubTxt = svgEl("text", { class: "hub-txt", x: 0, y: 0 });
  hubTxt.textContent = `${on}/${c.members.length}`;
  hub.appendChild(hubTxt);

  const addNode = (h, x, y, role, labelMode, angle, from = null) => {
    const line = svgEl("line", {
      x1: from ? from[0].toFixed(1) : 0, y1: from ? from[1].toFixed(1) : 0, x2: x.toFixed(1), y2: y.toFixed(1),
      // Dashed means "not confirmed right now": an offline device, or a
      // gateway no scan has recorded. This machine is always present. A line
      // from a switch rather than the hub is a cable the user recorded.
      class: "spoke" + (from ? " cable" : "") + ((h.synthetic ? role !== "self" : !h.online) ? " off" : ""),
    });
    const g = svgEl("g", { class: mapNodeClass(h, role), transform: `translate(${x.toFixed(1)},${y.toFixed(1)})` });
    if (!h.synthetic) {
      line.setAttribute("data-id", h.id);
      g.setAttribute("data-id", h.id);
      g.setAttribute("tabindex", "0");
      g.setAttribute("role", "button");
      g.setAttribute("aria-label", mapTitle(h, role).split("\n")[0]);
      // A device opens its "Plugged into" dialog, which is where the map is
      // edited; the gateway and this machine still jump to the device list.
      const open = () => {
        if (role !== "dev" && !from) return jumpToHost(h.id);
        openPlugDialog(hosts.find(x => x.id === h.id) || h);
      };
      g.addEventListener("click", open);
      g.addEventListener("keydown", e => {
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); open(); }
      });
    }
    spokes.appendChild(line);
    const title = svgEl("title", {});
    title.textContent = mapTitle(h, role);
    g.appendChild(title);
    if (role === "gw") {
      g.appendChild(svgEl("rect", { x: -8, y: -8, width: 16, height: 16, transform: "rotate(45)", class: "shape gw-shape" }));
    } else if (role === "self") {
      g.appendChild(svgEl("circle", { r: MAP.SPECIAL_R, class: "shape self-shape" }));
      g.appendChild(svgEl("circle", { r: 4, class: "self-core" }));
    } else {
      g.appendChild(svgEl("circle", { r: MAP.NODE_R + 8, class: "blink-ring" }));
      g.appendChild(svgEl("circle", { r: MAP.NODE_R + 4, class: "watch" }));
      g.appendChild(svgEl("circle", { r: MAP.NODE_R, class: "shape dot" }));
    }
    if (labelMode === "radial") {
      // Names point outward along their spoke; on the left half they are
      // turned the other way round so none read upside down.
      const deg = angle * 180 / Math.PI;
      const right = Math.cos(angle) >= 0;
      const t = svgEl("text", {
        class: "lbl", x: right ? MAP.NODE_R + 8 : -(MAP.NODE_R + 8), y: 0,
        "text-anchor": right ? "start" : "end", "dominant-baseline": "central",
        transform: `rotate(${(right ? deg : deg + 180).toFixed(1)})`,
      });
      t.textContent = role === "self" ? "BAMF" : mapLabel(h);
      g.appendChild(t);
    } else if (labelMode === "above" || labelMode === "below") {
      const t = svgEl("text", {
        class: "lbl special", x: 0,
        y: labelMode === "above" ? -(MAP.SPECIAL_R + 8) : MAP.SPECIAL_R + 15, "text-anchor": "middle",
      });
      t.textContent = role === "self" ? "BAMF" : "gateway";
      g.appendChild(t);
    }
    nodes.appendChild(g);
  };

  // Switches sit inside the arc of everything under them, further out the
  // further down a chain they are.
  const swPos = new Map();
  for (const sw of c.switches) {
    const span = swSpan.get(sw.id);
    if (!span) continue;
    const a = angleAt((span[0] + span[1]) / 2, slots.length);
    const r = swR(swDepth.get(sw.id));
    swPos.set(sw.id, [r * Math.cos(a), r * Math.sin(a)]);
  }
  const gwPos = c.gw ? [0, -MAP.INNER] : null;
  for (const sw of c.switches) {
    const p = swPos.get(sw.id);
    if (!p) continue;
    // Its uplink: a cable to the switch above it or to the router, or the
    // plain "on this network" line to the hub when nothing is recorded.
    const up = sw.uplink === "switch" ? swPos.get(sw.uplinkSwitch)
      : sw.uplink === "router" ? (gwPos || [0, 0]) : null;
    spokes.appendChild(svgEl("line", {
      x1: (up ? up[0] : 0).toFixed(1), y1: (up ? up[1] : 0).toFixed(1), x2: p[0].toFixed(1), y2: p[1].toFixed(1),
      class: "spoke" + (up ? " cable" : ""),
    }));
    const host = sw.hostId ? hosts.find(h => h.id === sw.hostId) : null;
    const g = svgEl("g", {
      class: host ? mapNodeClass(host, "sw") : "node sw", transform: `translate(${p[0].toFixed(1)},${p[1].toFixed(1)})`,
      tabindex: "0", role: "button", "data-sw": sw.id, "aria-label": "Switch " + sw.name,
    });
    if (host) g.setAttribute("data-id", host.id);
    const title = svgEl("title", {});
    title.textContent = switchTitle(sw);
    g.appendChild(title);
    g.appendChild(svgEl("rect", { x: -14, y: -8, width: 28, height: 16, rx: 3, class: "shape sw-shape" }));
    for (const dx of [-8, -2, 4]) g.appendChild(svgEl("rect", { x: dx, y: -2, width: 4, height: 4, class: "sw-port" }));
    const t = svgEl("text", { class: "lbl sw-lbl", x: 0, y: 20, "text-anchor": "middle" });
    t.textContent = sw.name.length > 18 ? sw.name.slice(0, 17) + "…" : sw.name;
    g.appendChild(t);
    // A switch opens its Ports dialog: every port and what's on it, in one place.
    const open = () => openPortsDialog(switches.find(s => s.id === sw.id) || sw);
    g.addEventListener("click", open);
    g.addEventListener("keydown", e => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); open(); }
    });
    nodes.appendChild(g);
  }

  slots.forEach((slot, i) => {
    if (!slot || !slot.h) return;   // a gap, or an empty switch's place
    const [r, a] = at[i];
    addNode(slot.h, r * Math.cos(a), r * Math.sin(a), c.selfIds.includes(slot.h.id) ? "self" : "dev", single ? "radial" : "none", a,
      slot.sw ? swPos.get(slot.sw.id) : null);
  });
  if (c.gw) addNode(c.gw, 0, -MAP.INNER, "gw", "above");
  if (c.self) addNode(c.self, 0, MAP.INNER, "self", "below");
  return svg;
}

// Switches, routers and access points are one kind of record with a type.
// Kinds that live inside something else and have no cable of their own.
function innerKind(kind) { return kind === "virtual" || kind === "ssid"; }
// Kinds whose devices have no port: VMs, Wi-Fi clients, and a VPN's clients.
function memberKind(kind) { return innerKind(kind) || kind === "vpn"; }
// The access point a wireless SSID is broadcast by.
function apOf(sw) { return sw && sw.kind === "ssid" && sw.runsOn ? switches.find(s => s.id === sw.runsOn && s.kind === "ap") || null : null; }
function kindNoun(kind) {
  return kind === "router" ? "router" : kind === "ap" ? "access point" : kind === "virtual" ? "virtual switch"
    : kind === "ssid" ? "wireless SSID" : kind === "vpn" ? "VPN" : "switch";
}
// The machine a virtual switch runs on.
function runsOnHost(sw) { return sw && sw.kind === "virtual" && sw.runsOn ? hosts.find(h => h.id === sw.runsOn) || null : null; }
// "(router, 4 ports)", "(8 ports)" for a plain switch, "(virtual switch on proxmox)".
function kindPorts(sw) {
  if (sw.kind === "virtual") { const on = runsOnHost(sw); return `(virtual switch${on ? " on " + nameOrIp(on) : ""})`; }
  if (sw.kind === "ssid") { const ap = apOf(sw); return `(wireless SSID${ap ? " on " + ap.name : ""})`; }
  if (sw.kind === "ap") return "(access point)";
  if (sw.kind === "vpn") return "(VPN)";
  return `(${sw.kind && sw.kind !== "switch" ? kindNoun(sw.kind) + ", " : ""}${sw.ports} ports)`;
}
// Hypervisors give virtual NICs well-known MAC prefixes; the same list as the server's.
const VM_PREFIXES = [["BC2411", "Proxmox"], ["525400", "QEMU/KVM"], ["005056", "VMware"], ["000C29", "VMware"],
  ["000569", "VMware"], ["001C14", "VMware"], ["00155D", "Hyper-V"], ["080027", "VirtualBox"], ["00163E", "Xen"],
  ["001C42", "Parallels"], ["0242", "Docker"]];
function vmPlatform(mac) {
  const c = (mac || "").replace(/[:-]/g, "").toUpperCase();
  const hit = VM_PREFIXES.find(([p]) => c.startsWith(p));
  return hit ? hit[1] : null;
}
// How BAMF knows a network's gateway: the one you declared, or else this
// machine's routing table. The routing table only knows the default gateway
// of networks this machine routes through, so on others only you can say.
function declaredGw(subnet) { return declaredGws.find(g => g.subnet === subnet) || null; }
function gatewayOf(subnet) {
  const d = declaredGw(subnet);
  if (d) return { ip: d.ip, hostId: d.hostId, declared: true };
  const ip = (networkPlaces[subnet] || {}).gateway;
  return ip ? { ip, hostId: null, declared: false } : null;
}
// The networks a device is declared the gateway of.
function gwNetsOf(h) { return h ? declaredGws.filter(g => g.hostId === h.id).map(g => g.subnet) : []; }
// The device a VPN record is, if it is one: a VPN is never a gateway.
function vpnOfHost(h) { return h ? switches.find(s => s.kind === "vpn" && s.hostId === h.id) || null : null; }
// A network's gateway, by what you declared, BAMF's routing table, or the device's own guess.
function isGatewayHost(h) {
  if (vpnOfHost(h)) return false;
  if (gwNetsOf(h).length) return true;
  const g = gatewayOf(h.subnet);
  return !!(g && !g.declared && g.ip === h.ip) || /router|gateway/i.test(h.osGuess || "");
}

// "port 3 · Living Room", or "port 3" when the port has no location.
function portLabel(sw, port) { return (sw && sw.portLabels && sw.portLabels[port]) || ""; }
function portText(sw, port) {
  const where = portLabel(sw, port);
  return `port ${port}` + (where ? ` · ${where}` : "");
}

function uplinkText(sw) {
  if (sw.kind === "virtual") { const on = runsOnHost(sw); return on ? `Runs on ${nameOrIp(on)}` : "Machine not recorded"; }
  if (sw.kind === "ssid") { const ap = apOf(sw); return ap ? `Broadcast by ${ap.name}` : "Access point not recorded"; }
  if (sw.uplink === "router") return "Plugged into the router";
  if (sw.uplink === "switch") {
    const up = switches.find(s => s.id === sw.uplinkSwitch);
    return up ? `Plugged into ${up.name}` + (sw.uplinkPort ? `, ${portText(up, sw.uplinkPort)}` : "") : "Uplink not recorded";
  }
  return "Uplink not recorded";
}

function switchTitle(sw) {
  const host = sw.hostId ? hosts.find(h => h.id === sw.hostId) : null;
  const n = hosts.filter(h => h.switchId === sw.id && !h.forgotten).length;
  if (sw.kind === "virtual") {
    return [sw.name, "Virtual switch · your record", uplinkText(sw), `${n} VM${n === 1 ? "" : "s"} recorded on it`,
      "Click to choose its VMs"].join("\n");
  }
  if (sw.kind === "ssid") {
    return [sw.name, "Wireless SSID · your record", uplinkText(sw), sw.subnet || "no network",
      `${n} device${n === 1 ? "" : "s"} on it`, "Click to choose its devices"].join("\n");
  }
  if (sw.kind === "vpn") {
    const also = host ? alsoAt(host).map(a => a.ip) : [];
    return [sw.name, "VPN · your record", host ? `${host.ip} · ${host.mac}` : "No address BAMF sees",
      also.length ? "Also at " + also.join(", ") : null, uplinkText(sw),
      `${n} client${n === 1 ? "" : "s"} on it`, "Never a gateway; joins no networks together", "Click to choose its clients"]
      .filter(Boolean).join("\n");
  }
  if (sw.kind === "ap") {
    const ssids = switches.filter(x => x.kind === "ssid" && x.runsOn === sw.id);
    return [sw.name, "Access point · your record", host ? `${host.ip} · ${host.mac}` : "No address BAMF sees",
      uplinkText(sw), ssids.length ? `SSIDs: ${ssids.map(x => x.name).join(", ")}` : "No SSIDs yet",
      "Click to add a wireless SSID"].join("\n");
  }
  const lines = [sw.name, `${sw.ports}-port ${kindNoun(sw.kind)} · your record`];
  if (host) {
    lines.push(`${host.ip} · ${host.mac}`);
    lines.push(host.online ? "Online" : "Offline, last seen " + fmtAgo(host.lastSeen));
  } else {
    lines.push("No address BAMF sees");
  }
  lines.push(uplinkText(sw));
  lines.push(`${n} device${n === 1 ? "" : "s"} recorded on it`);
  lines.push("Click to fill in its ports");
  return lines.join("\n");
}

// ---- topology map ----
// The Map drawn as a network diagram: the router at the top, the switches
// the user recorded under it in their chain, each switch's devices listed
// below it in port order, and everything not recorded in an "On this network"
// box. Anything can be dragged; where it's dropped is saved on the server,
// per network, and whatever hasn't been moved by hand follows the node it
// hangs from. Dropping a device on a switch records it there, dropping it in
// the box unplugs it, and dropping a switch on a switch or the router sets
// what it's plugged into.
let mapStyle = "topology";
try { if (localStorage.getItem("bamf-map-style") === "radial") mapStyle = "radial"; } catch { }
const mapViews = {};        // subnet -> [x, y, w, h]: zoom and pan, kept across redraws
const topoModels = {};      // subnet -> the drawn model, for the card's buttons
let mapDragging = false;
// Compact Wi-Fi: an access point's SSIDs under it, the empty ones as tags, instead of each SSID beside it. Off until turned on.
let mapWifiCompact = false;
try { mapWifiCompact = localStorage.getItem("bamf-map-wifi-compact") === "1"; } catch { }
// Signal colours: each Wi-Fi device's tile, line and a small bar glyph show how well it hears its access point. Off until turned on.
let mapSignal = false;
try { mapSignal = localStorage.getItem("bamf-map-signal") === "1"; } catch { }
// Slide into place: an arrange, an undo or a put-back glides each thing to its new spot instead of redrawing the map in one go.
let mapSlide = false;
try { mapSlide = localStorage.getItem("bamf-map-slide") === "1"; } catch { }

const TOPO = {
  T: 16,               // half a tile
  ROW: 46,             // one device per row under a switch
  TIER: 140,           // router to the first row of switches
  PORT_INDENT: 124,    // a switch's device column sits this far right, leaving room for "3 · Living Room"
  COL_GAP: 70,         // between switch regions
  SELF_DX: 250,        // this machine sits beside the router
  BOX_COL: 220, BOX_ROWS: 10, BOX_PAD: 18,
  CHAR: 6.7,           // estimated width of one character of a label
  VS_DX: 250,          // a virtual switch sits this far right of the machine it runs on
};

// 24×24 line drawings, one per kind of device. Plain paths, no library.
const TOPO_ICONS = {
  router: "M3 13h18v7H3z M7 13V6 M17 13V6 M6.5 16.5h2 M10.5 16.5h2",
  vpn: "M12 2.5l7.5 3v5.5c0 4.6-3.2 8.3-7.5 10.5-4.3-2.2-7.5-5.9-7.5-10.5V5.5z M9.5 11V9.2a2.5 2.5 0 0 1 5 0V11 M8.5 11h7v5h-7z",
  switch: "M2 8h20v8H2z M5 12h1.5 M8.5 12h1.5 M12 12h1.5 M15.5 12h1.5",
  ap: "M12 19.5h.01 M8.5 16a5 5 0 0 1 7 0 M5.5 13a9 9 0 0 1 13 0 M2.5 10a13 13 0 0 1 19 0",
  camera: "M3 7h12v10H3z M15 10l6-3v10l-6-3z",
  printer: "M6 9V3h12v6 M4 9h16v8H4z M7 14h10v7H7z",
  tv: "M3 5h18v12H3z M8 21h8 M12 17v4",
  speaker: "M7 3h10v18H7z M9 15a3 3 0 1 0 6 0a3 3 0 1 0 -6 0 M12 7h.01",
  phone: "M8 2h8v20H8z M11 18h2",
  tablet: "M5 3h14v18H5z M11 17h2",
  laptop: "M5 5h14v10H5z M2 19h20",
  desktop: "M3 4h18v12H3z M8 20h8 M12 16v4",
  server: "M4 4h16v6H4z M4 14h16v6H4z M7.5 7h.01 M7.5 17h.01",
  iot: "M8 8h8v8H8z M10 4v4 M14 4v4 M10 16v4 M14 16v4 M4 10h4 M4 14h4 M16 10h4 M16 14h4",
  net: "M7 18h10a4 4 0 0 0 .5-7.97A6 6 0 0 0 6 11a3.5 3.5 0 0 0 1 7z",
  device: "M4 12a8 8 0 1 0 16 0a8 8 0 1 0 -16 0 M12 12h.01",
  vm: "M3 5h18v14H3z M3 9h18 M6 7h.01 M9 7h.01 M8 14h8",
  nas: "M5 3h14v18H5z M8 7h8 M8 11h8 M8 15h5 M16 17.5h.01",
  game: "M6 9h12a4 4 0 0 1 4 4v1a3 3 0 0 1-5.5 1.7L15 14H9l-1.5 1.7A3 3 0 0 1 2 14v-1a4 4 0 0 1 4-4z M7 11.5v2 M6 12.5h2 M16 12h.01 M18 13.5h.01",
  light: "M9 18h6 M10 21h4 M12 3a6 6 0 0 0-3.5 10.9V16h7v-2.1A6 6 0 0 0 12 3z",
  plug: "M8 3v5 M16 3v5 M6 8h12v4a6 6 0 0 1-12 0z M12 18v3",
  streamer: "M12 4a8 8 0 1 0 0 16a8 8 0 0 0 0-16z M10 9l5 3-5 3z",
  soundbar: "M2 9h20v6H2z M4 12a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0 M17 12a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0 M10 12h4",
  display: "M4 3h16v11H4z M5 14h14v6H5z M10 17h4",
  doorbell: "M8 2h8v20H8z M10.5 8a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0 M10.5 17a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0",
  dome: "M3 5h18 M5 5v3a7 7 0 0 0 14 0V5 M12 12h.01",
  thermostat: "M3 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0 M12 7v6 M10.5 15.5a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0",
  lock: "M6 11h12v9H6z M8.5 11V8a3.5 3.5 0 0 1 7 0v3 M12 15v2",
  vacuum: "M3 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0 M5.5 8a8 8 0 0 1 13 0 M10.5 14.5a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0",
  sprinkler: "M12 20v-6 M9 14h6 M12 10V4 M7 8L5 5 M17 8l2-3 M4 11h.01 M20 11h.01",
  garage: "M3 21V9l9-6 9 6v12 M7 21v-8h10v8 M7 15.5h10 M7 18h10",
  fridge: "M6 2h12v20H6z M6 9h12 M9 5v1.5 M9 12v3",
  washer: "M5 2h14v20H5z M5 7h14 M8 4.5h.01 M11 4.5h.01 M8 14a4 4 0 1 0 8 0a4 4 0 1 0 -8 0",
  purifier: "M4 12a8 8 0 1 0 16 0a8 8 0 1 0 -16 0 M12 12V4.5 M12 12l6.5 3.75 M12 12l-6.5 3.75",
  ac: "M3 4h18v8H3z M3 9h18 M7 16c0 2 1 2 1 4 M12 16v4 M17 16c0 2-1 2-1 4",
  charger: "M7 3h10v18H7z M13 7l-3 5h4l-3 5",
  car: "M3 13l2-6h14l2 6 M2 13h20v5H2z M6 18v2 M18 18v2 M6 15.5h.01 M18 15.5h.01",
  vr: "M2 8h20v9h-6l-2-3h-4l-2 3H2z",
  watch: "M8 7h8v10H8z M9 7l1-4h4l1 4 M9 17l1 4h4l1-4 M12 12h.01",
  handheld: "M2 7h20v10H2z M7 9.5h10v5H7z M4.5 12h.01 M19.5 12h.01",
  projector: "M3 9h18v8H3z M14 13a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0 -5 0 M6 17v2 M18 17v2 M6 13h4",
  printer3d: "M4 3h16v18H4z M7 7h10 M12 7v4 M10 11h4 M6 17h12",
  ereader: "M5 2h14v20H5z M8 5h8v11H8z M11 19h2",
  modem: "M3 9h18v10H3z M7 14h.01 M10 14h.01 M13 14h.01 M16 14h2 M12 9V5 M10 5h4",
  mesh: "M4 17a8 3.5 0 1 0 16 0a8 3.5 0 1 0 -16 0 M4 17v-3 M20 17v-3 M9 10.5a4.5 4.5 0 0 1 6 0 M12 7.5h.01 M6.5 7.5a8 8 0 0 1 11 0",
  minipc: "M4 9h16v9H4z M7 13.5h5 M17 13.5h.01",
  pi: "M3 6h18v12H3z M6 9h4v3H6z M13 9h5 M13 12h5 M6 15h.01 M9 15h.01 M12 15h.01 M15 15h.01 M18 15h.01",
  vswitch: "M2 9h20v7H2z M5 12.5h1.5 M8.5 12.5h1.5 M12 12.5h1.5 M15.5 12.5h1.5 M5 5.5h14",
};
// Which drawing, from the device guess, then its names and vendor. First match wins.
const TOPO_KINDS = [
  ["vm", /virtual machine|container \(docker/],
  ["router", /router|gateway/],
  ["switch", /switch|network gear/],
  ["ap", /access point|\bap\b|wi-?fi|\bwap\b/],
  ["camera", /camera|\bcam\b|ipcam|\bnvr\b|doorbell/],
  ["printer", /printer|scanner|\bmfc\b/],
  ["tv", /\btv\b|roku|chromecast|television|media device|shield/],
  ["speaker", /sonos|speaker|homepod|airplay|spotify|\becho\b/],
  ["phone", /iphone|android|phone|pixel|galaxy/],
  ["tablet", /ipad|tablet|kindle/],
  ["laptop", /macbook|laptop|notebook|chromebook/],
  ["desktop", /windows|desktop|workstation|imac|\bpc\b/],
  ["nas", /\bnas\b|synology|qnap|truenas|unraid|file server/],
  ["game", /xbox|playstation|nintendo|steam ?deck/],
  ["server", /server|proxmox|raspberry|linux|ssh host|home assistant|pihole/],
  ["iot", /esp32|esp8266|esphome|\biot\b|hue|homekit|matter|nest|thermostat|plug|bulb|amazon device/],
];
// What a device's own words can say beyond that: a Roku is a streamer, not just a TV. [kind, what its text says,
// the kinds it may replace], first match wins. A name only ever makes a plain kind more specific; it never
// turns a router into a thermostat.
const SMALL_KINDS = ["iot", "device"];
const TOPO_REFINE = [
  ["doorbell", /doorbell|nest hello|video ?bell|\bring ?(pro|video|doorbell|chime)/, ["camera", ...SMALL_KINDS]],
  ["dome", /\bdome\b|\bptz\b|turret/, ["camera", ...SMALL_KINDS]],
  ["display", /nest hub|echo show|echo spot|smart display|google home hub|\bportal\b|smart clock/, ["speaker", "tv", "tablet", "phone", ...SMALL_KINDS]],
  ["mesh", /\beero\b|\borbi\b|\bdeco\b|nest ?wi-?fi|google ?wi-?fi|\bvelop\b|amplifi|\bmesh\b|extender|repeater|\bplume\b|wi-?fi ?point/, ["ap", "router", "net", "switch", ...SMALL_KINDS]],
  ["soundbar", /sound ?bar|sonos (beam|arc|ray|playbar|playbase)|\bhw-[a-z0-9]+|\byas-\d+/, ["speaker", "tv", ...SMALL_KINDS]],
  ["streamer", /\broku\b|chromecast|apple ?tv|fire ?(tv|stick|cube)|firestick|\bshield\b|google tv|streaming|tivo|\bmi box\b|media ?player/, ["tv", "speaker", "game", ...SMALL_KINDS]],
  ["projector", /projector|optoma|\bepson eb|benq (ht|w\d)/, ["tv", ...SMALL_KINDS]],
  ["printer3d", /3d ?printer|prusa|creality|\bender-?\d|bambu|octoprint|octopi|klipper|anycubic|elegoo|voron|mainsail|fluidd/, ["printer", "server", "desktop", ...SMALL_KINDS]],
  ["vacuum", /vacuum|roomba|irobot|roborock|ecovacs|\bneato\b|dreame|robovac|deebot|narwal|robot ?clean/, SMALL_KINDS],
  ["lock", /smart ?lock|deadbolt|\bnuki\b|schlage|kwikset|\byale\b|august (home|lock)|\block\b/, SMALL_KINDS],
  ["thermostat", /thermostat|ecobee|honeywell home|\btado\b|\bnest\b(?!.*\b(hub|mini|audio|wi-?fi|point|cam|protect|doorbell|hello)\b)/, SMALL_KINDS],
  ["sprinkler", /sprinkler|rachio|rain ?bird|irrigat|b-?hyve|hydrawise|\blawn\b|\bturf\b|\bzone controller/, SMALL_KINDS],
  ["garage", /garage|\bmyq\b|chamberlain|liftmaster|\bopener\b/, ["camera", ...SMALL_KINDS]],
  ["fridge", /fridge|refrigerator|freezer|family hub|sub-?zero/, ["tv", ...SMALL_KINDS]],
  ["washer", /washer|dryer|dishwasher|laundry|\boven\b|microwave|\brange\b|cooktop|appliance|whirlpool|maytag|electrolux|\bmiele\b|thinq/, SMALL_KINDS],
  ["purifier", /purifier|humidifier|dehumidifier|blueair|levoit|\bcoway\b|\bhepa\b|\bfan\b|air ?quality|\bdyson\b/, ["plug", ...SMALL_KINDS]],
  ["ac", /air ?condition|mini-?split|\bhvac\b|\bdaikin\b|mitsubishi (electric|comfort)|heat ?pump|\bcielo\b|\bsensibo\b|\bac unit\b/, SMALL_KINDS],
  ["charger", /charger|wallbox|chargepoint|\bevse\b|juicebox|wall connector|\bemporia\b/, ["plug", ...SMALL_KINDS]],
  ["car", /tesla|rivian|polestar|vehicle|\bmodel ?[3sxy]\b|\bford\b|chevrolet|\bbmw\b|\bvolvo\b|hyundai|\bkia\b|nissan|\bhonda\b|toyota|\blucid\b|volkswagen|subaru|mercedes/, ["tv", "speaker", "phone", "tablet", "game", ...SMALL_KINDS]],
  ["vr", /\bquest\b|oculus|\bvr\b|\bvive\b|psvr|\bpico ?4\b|headset|valve index/, ["game", "phone", "tv", ...SMALL_KINDS]],
  ["watch", /apple ?watch|\bwatch\b|fitbit|garmin|whoop|\boura\b|wearable/, ["phone", "tablet", ...SMALL_KINDS]],
  ["handheld", /steam ?deck|switch lite|rog ally|ayaneo|handheld/, ["game", "tablet", ...SMALL_KINDS]],
  ["ereader", /kindle|\bkobo\b|e-?reader|remarkable|boox/, ["tablet", ...SMALL_KINDS]],
  ["modem", /\bmodem\b|\bont\b|surfboard|\barris\b|\bcm\d{3,4}\b|\bmb\d{4}\b|docsis/, ["router", "net", "switch", ...SMALL_KINDS]],
  ["minipc", /\bnuc\b|mini ?pc|beelink|minisforum|odroid|zima(board|cube)?|rock ?pi|orange ?pi|banana ?pi|mac ?mini|elitedesk|prodesk|thinkcentre tiny/, ["server", "desktop", ...SMALL_KINDS]],
  ["pi", /raspberry|pi-?hole|pihole|\brpi\b|raspbian/, ["server", "desktop", ...SMALL_KINDS]],
];
function refineKind(kind, text) {
  for (const [refined, re, kinds] of TOPO_REFINE) if (kinds.includes(kind) && re.test(text)) return refined;
  return kind;
}
function deviceKind(h) {
  if (!h || h.synthetic) return "device";
  if (h.deviceType && TOPO_ICONS[h.deviceType]) return h.deviceType;
  return autoKind(h);
}
// What the icon would be without a Map icon picked for the device itself:
// the icon its device type names ("NAS", "Printer"), then its guessed type's.
function autoKind(h) {
  if (!h || h.synthetic) return "device";
  if (h.typeName) {
    const named = TYPE_DEFS.find(([k, label]) => label.toLowerCase() === h.typeName.toLowerCase() || k === h.typeName.toLowerCase());
    if (named && TOPO_ICONS[named[0]]) return named[0];
  }
  const fam = guessFamily(h.osGuess);
  if (fam && typeIcons[fam] && TOPO_ICONS[typeIcons[fam]]) return typeIcons[fam];
  return topoKind(h);
}
function topoKind(h) {
  if (!h || h.synthetic) return "device";
  const text = [h.osGuess, h.customName, h.hostname === "—" ? "" : h.hostname, h.mdnsName, h.vendor]
    .filter(Boolean).join(" ").toLowerCase();
  const base = TOPO_KINDS.find(([, re]) => re.test(text));
  return refineKind(base ? base[0] : "device", [text, h.typeName].filter(Boolean).join(" ").toLowerCase());
}
function topoClip(text, n) { return text.length > n ? text.slice(0, n - 1) + "…" : text; }

// Where everything goes before anything is dragged. Positions are node
// centres; each node also knows the node it hangs from, so a node that hasn't
// been dragged can follow its parent.
function topoModel(c) {
  const nodes = new Map();
  const add = (key, o) => { const n = Object.assign({ key, parent: null, edge: null }, o); nodes.set(key, n); return n; };
  const shown = new Set(c.switches.map(s => s.id));
  const saved = mapPositions[c.subnet] || {};
  const y1 = TOPO.TIER;
  let x = 0;
  const seen = new Set();
  const byPort = (a, b) => (a.switchPort || 1e9) - (b.switchPort || 1e9) || nameOrIp(a).localeCompare(nameOrIp(b));

  // The top of the diagram. A router the user recorded and linked to the
  // gateway is the top, with its own ports; with no gateway known, a
  // top-level router is. Matched on the gateway's address rather than on one
  // device record, since two records can share it (an old MAC, a virtual
  // adapter), and on any of the router's addresses, since it has one per
  // network. One network has one top; the whole-network view has one per
  // network, merged where networks share a router.
  const recHas = (s, ip) => { const h = s.hostId ? hosts.find(x => x.id === s.hostId) : null; return !!h && curAddrs(h).some(a => a.ip === ip); };
  const tops = [];
  for (const t of c.tops || [{ net: c.subnet, gw: c.gw }]) {
    const gwIp = t.gw ? t.gw.ip : null;
    const rec = (gwIp ? c.switches.find(s => s.kind === "router" && (recHas(s, gwIp) || (t.gw.id && s.hostId === t.gw.id))) : null)
      || (!t.gw ? c.switches.find(s => s.kind === "router" && (!c.whole || s.subnet === t.net) && !(s.uplink === "switch" && shown.has(s.uplinkSwitch))) : null)
      || null;
    const key = rec ? "s:" + rec.id : t.gw ? (c.whole ? "gw:" + (t.gw.synthetic ? t.gw.ip : t.gw.id) : "gw") : "net";
    const had = tops.find(o => o.key === key);
    if (had) had.nets.push(t.net);
    else tops.push({ key, rec, gw: t.gw, nets: [t.net] });
  }
  // Networks with no router or gateway at all hang from the first real top.
  if (tops.length > 1 && tops.some(t => t.key === "net")) {
    const bare = tops.find(t => t.key === "net");
    tops.splice(tops.indexOf(bare), 1);
    tops[0].nets.push(...bare.nets);
  }
  // A second router plugged into a switch of the first is drawn where it's
  // plugged in, and its network hangs from it there.
  for (const t of tops) {
    t.nested = c.whole && (t.rec ? t.rec.uplink === "switch" && shown.has(t.rec.uplinkSwitch)
      : !!t.gw && !t.gw.synthetic && c.placed.some(h => h.id === t.gw.id));
    if (t.nested && !t.rec) t.key = "h:" + t.gw.id;
  }
  if (!tops.some(t => !t.nested)) tops[0].nested = false;
  for (const t of tops) if (t.rec && !t.nested) seen.add(t.rec.id);
  const topRecs = new Set(tops.filter(t => t.rec).map(t => t.rec.id));
  const ownerOf = net => tops.find(t => t.nets.includes(net)) || tops.find(t => !t.nested);
  const main = tops.find(t => !t.nested);

  // Virtual switches hang to the right of the machine they run on, their VMs
  // listed under them. col.bottom keeps two on one column from overlapping.
  // Returns how far right of the machine they reach.
  const vsAll = c.switches.filter(s => s.kind === "virtual");
  const hung = new Set();
  const hang = (hostId, hostKey, hx, hy, col) => {
    let reach = 0;
    for (const vs of vsAll.filter(s => s.runsOn === hostId && !hung.has(s.id))) {
      hung.add(vs.id);
      const vy = Math.max(hy, col.bottom + 30);
      const key = "s:" + vs.id;
      add(key, { type: "switch", sw: vs, host: null, parent: hostKey, auto: [hx + TOPO.VS_DX, vy], edge: { style: "vside", vlink: true } });
      const vms = c.placed.filter(h => h.switchId === vs.id).sort(byPort);
      vms.forEach((h, j) => add("h:" + h.id, { type: "dev", host: h, parent: key,
        auto: [hx + TOPO.VS_DX + TOPO.PORT_INDENT, vy + 64 + j * TOPO.ROW],
        edge: { style: "list", cable: false, sw: vs, port: 0 } }));
      col.bottom = vy + 40 + vms.length * TOPO.ROW;
      reach = Math.max(reach, TOPO.VS_DX + (vms.length ? TOPO.PORT_INDENT + 2 * TOPO.T + 190 : 2 * TOPO.T + 30 + vs.name.length * TOPO.CHAR));
    }
    return reach;
  };

  // A wireless SSID hangs to the right of its access point, the way a virtual
  // switch hangs off its machine, and its devices connect with a lightning bolt.
  const ssidAll = c.switches.filter(s => s.kind === "ssid");
  const hangSsids = (ap, apKey, hx, hy, col, dx) => {
    let reach = 0;
    for (const ss of ssidAll.filter(s => s.runsOn === ap.id && !hung.has(s.id))) {
      hung.add(ss.id);
      const vy = Math.max(hy, col.bottom + 30);
      const key = "s:" + ss.id;
      add(key, { type: "switch", sw: ss, host: null, parent: apKey, auto: [hx + dx, vy], edge: { style: "vside", vlink: true } });
      const devs = c.placed.filter(h => h.switchId === ss.id).sort(byPort);
      devs.forEach((h, j) => add("h:" + h.id, { type: "dev", host: h, parent: key,
        auto: [hx + dx + TOPO.PORT_INDENT, vy + 64 + j * TOPO.ROW],
        edge: { style: "bolt", cable: false, sw: ss, port: 0 } }));
      col.bottom = vy + 40 + devs.length * TOPO.ROW;
      reach = Math.max(reach, dx + (devs.length ? TOPO.PORT_INDENT + 2 * TOPO.T + 190 : 2 * TOPO.T + 30 + ss.name.length * TOPO.CHAR));
    }
    return reach;
  };

  // Compact Wi-Fi: the access point's SSIDs stacked under it. One that has devices is a node with its devices listed beneath on a
  // spine, as a switch's are; one with none is only a tag on the access point. Returns how far right of hx it reaches, and the tags.
  const stackSsids = (ap, apKey, hx, hy) => {
    const tags = [];
    let reach = 0, y = hy;
    for (const ss of ssidAll.filter(s => s.runsOn === ap.id && !hung.has(s.id))) {
      hung.add(ss.id);
      const devs = c.placed.filter(h => h.switchId === ss.id).sort(byPort);
      if (!devs.length) { tags.push(ss.name); continue; }
      const key = "s:" + ss.id, x = hx + 40;
      add(key, { type: "switch", sw: ss, host: null, parent: apKey, auto: [x, y], edge: { style: "list", vlink: true, cable: false } });
      devs.forEach((h, j) => add("h:" + h.id, { type: "dev", host: h, parent: key,
        auto: [x + 44, y + 50 + j * TOPO.ROW], edge: { style: "list", wifi: true, cable: false } }));
      reach = Math.max(reach, 40 + 44 + TOPO.T + 190, 40 + TOPO.T + 12 + ss.name.length * TOPO.CHAR);
      y += 70 + devs.length * TOPO.ROW;
    }
    return { reach, tags };
  };

  const place = (sw, left, parentKey, edge) => {   // returns the region's width
    if (seen.has(sw.id)) return 0;
    seen.add(sw.id);
    const key = "s:" + sw.id;
    add(key, { type: "switch", sw, host: sw.hostId ? hosts.find(h => h.id === sw.hostId) : null,
      parent: parentKey, auto: [left + TOPO.T, y1], edge });
    const devs = c.placed.filter(h => h.switchId === sw.id).sort(byPort);
    // A VPN's clients reach it down a tunnel, not a cable.
    const vpn = sw.kind === "vpn";
    devs.forEach((h, i) => add("h:" + h.id, { type: "dev", host: h, parent: key,
      auto: [left + TOPO.T + TOPO.PORT_INDENT, y1 + 70 + i * TOPO.ROW],
      edge: vpn ? { style: "list", tunnel: true, cable: false, sw, port: 0 } : { style: "list", cable: true, sw, port: h.switchPort } }));
    const col = { bottom: -1e9 };
    let vsReach = 0;
    devs.forEach((h, i) => {
      vsReach = Math.max(vsReach, hang(h.id, "h:" + h.id, left + TOPO.T + TOPO.PORT_INDENT, y1 + 70 + i * TOPO.ROW, col));
    });
    // An access point's SSIDs, clear of any devices recorded straight on it.
    let ssidReach = 0;
    if (sw.kind === "ap" && mapWifiCompact) {
      const st = stackSsids(sw, key, left + TOPO.T, y1 + 70 + devs.length * TOPO.ROW);
      ssidReach = st.reach;
      if (st.tags.length) {
        const tr = arrTagRows(st.tags), nd = nodes.get(key);
        nd.tags = st.tags; nd.tagRows = tr.rows.length;
        ssidReach = Math.max(ssidReach, TOPO.T + 8 + tr.width - TOPO.T);   // the tags run on from the name, T + 8 along
      }
    } else if (sw.kind === "ap") {
      ssidReach = hangSsids(sw, key, left + TOPO.T, y1, { bottom: -1e9 }, devs.length ? TOPO.PORT_INDENT + 2 * TOPO.T + 210 : TOPO.VS_DX);
    }
    // Room for the name, or the line under it ("16 ports · 192.168.30.104"), whichever is longer.
    const label = Math.max(sw.name.length, memberKind(sw.kind) ? (sw.kind === "vpn" ? 24 : 0) : (`${sw.ports} ports`.length + (sw.hostId ? 18 : 0)));
    let width = Math.max(devs.length ? TOPO.PORT_INDENT + 2 * TOPO.T + 190 : 0, 2 * TOPO.T + 30 + label * TOPO.CHAR,
      vsReach ? TOPO.PORT_INDENT + TOPO.T + vsReach + 20 : 0, ssidReach ? TOPO.T + ssidReach + 20 : 0);
    c.switches.filter(s => !innerKind(s.kind) && s.uplink === "switch" && s.uplinkSwitch === sw.id && shown.has(s.id))
      .sort((a, b) => (a.uplinkPort || 1e9) - (b.uplinkPort || 1e9))
      .forEach(k => {
        const w = place(k, left + width + TOPO.COL_GAP, key, { style: "chain", cable: true, sw, port: k.uplinkPort });
        if (w) width += TOPO.COL_GAP + w;
      });
    return width;
  };

  // Everything under one top, left to right: the router's own ports, the row
  // of switches plugged into it (or with no uplink recorded), virtual switches
  // with no machine drawn above them, then the "On this network" box.
  const region = t => {
    const left = x;
    const rootDevs = t.rec && !t.nested ? c.placed.filter(h => h.switchId === t.rec.id).sort(byPort) : [];
    // The router's own ports hang in a column under it, so the switch row starts
    // to the right of that column (and of any virtual switches off it).
    if (rootDevs.length) {
      const col = { bottom: -1e9 };
      let reach = 0;
      rootDevs.forEach((h, i) => { reach = Math.max(reach, hang(h.id, "h:" + h.id, left + TOPO.T + TOPO.PORT_INDENT, 70 + i * TOPO.ROW, col)); });
      x = left + TOPO.PORT_INDENT + 2 * TOPO.T + Math.max(190, reach) + TOPO.COL_GAP;
    }
    const onRoot = s => t.rec && s.uplink === "switch" && s.uplinkSwitch === t.rec.id;
    const mine = s => onRoot(s) || (!(s.uplink === "switch" && shown.has(s.uplinkSwitch)) && (!c.whole || ownerOf(s.subnet) === t));
    // A VPN on another network's card is there for its clients on this one:
    // no cable to it, just the tunnel they reach it by.
    const away = s => s.kind === "vpn" && !c.whole && s.subnet !== c.subnet;
    c.switches.filter(s => !topRecs.has(s.id) && !innerKind(s.kind) && mine(s))
      .sort((a, b) => (onRoot(b) || b.uplink === "router") - (onRoot(a) || a.uplink === "router") || (a.uplinkPort || 1e9) - (b.uplinkPort || 1e9))
      .forEach(sw => {
        const edge = away(sw) ? { style: "down", tunnel: true, cable: false }
          : onRoot(sw)
          ? { style: "down", cable: true, sw: t.rec, port: sw.uplinkPort }
          : { style: "down", cable: sw.uplink === "router" };
        const w = place(sw, x, t.key, edge);
        if (w) x += w + TOPO.COL_GAP * 1.4;
      });

    // Virtual switches whose machine isn't in a switch's list (it's in the box,
    // it's the router or BAMF, or it isn't on this network): their own place at
    // the end of the switch row, with a dashed link back to the machine.
    // Likewise an SSID whose access point isn't on this card.
    const leftover = [...vsAll, ...ssidAll].filter(vs => !hung.has(vs.id) && (!c.whole || ownerOf(vs.subnet) === t));
    for (const vs of leftover) {
      hung.add(vs.id);
      const key = "s:" + vs.id;
      const ssid = vs.kind === "ssid";
      add(key, { type: "switch", sw: vs, host: null, parent: null,
        ...(ssid ? { pendingSwitch: vs.runsOn } : { pendingParent: vs.runsOn }), auto: [x + TOPO.T, y1], edge: { style: "vlink", vlink: true } });
      const vms = c.placed.filter(h => h.switchId === vs.id).sort(byPort);
      vms.forEach((h, j) => add("h:" + h.id, { type: "dev", host: h, parent: key,
        auto: [x + TOPO.T + TOPO.PORT_INDENT, y1 + 70 + j * TOPO.ROW],
        edge: { style: ssid ? "bolt" : "list", cable: false, sw: vs, port: 0 } }));
      x += Math.max(vms.length ? TOPO.PORT_INDENT + 2 * TOPO.T + 190 : 0, 2 * TOPO.T + 30 + vs.name.length * TOPO.CHAR) + TOPO.COL_GAP;
    }

    // Not recorded on a switch: a grid in the "On this network" box, one per
    // network in the whole-network view. Devices dragged out of it keep their
    // own place and leave no gap in the grid.
    const boxes = c.boxes ? c.boxes.filter(b => t.nets.includes(b.net)) : [{ net: c.subnet, hosts: c.ring }];
    boxes.filter(b => b.hosts.length).forEach((b, bi) => {
      if (bi) x += TOPO.COL_GAP;
      const bkey = c.whole ? "box:" + b.net : "box";
      const inGrid = b.hosts.filter(h => !saved["h:" + h.id]);
      const rows = Math.max(1, Math.min(TOPO.BOX_ROWS, inGrid.length));
      const cols = Math.max(1, Math.ceil(inGrid.length / TOPO.BOX_ROWS));
      const box = add(bkey, { type: "box", parent: t.key, auto: [x, y1 - 24], net: c.whole ? b.net : null,
        size: [cols * TOPO.BOX_COL + TOPO.BOX_PAD, rows * TOPO.ROW + 40], count: b.hosts.length,
        edge: { style: "box" } });
      let i = 0;
      for (const h of b.hosts) {
        const inBox = !saved["h:" + h.id];
        const k = inBox ? i++ : 0;
        add("h:" + h.id, { type: "dev", host: h, parent: bkey, inBox,
          auto: [x + TOPO.BOX_PAD + TOPO.T + Math.floor(k / TOPO.BOX_ROWS) * TOPO.BOX_COL, y1 + 34 + (k % TOPO.BOX_ROWS) * TOPO.ROW] });
      }
      x += box.size[0];
    });

    // A router drawn where it's plugged in already has its node.
    if (t.nested) return;
    const width = Math.max(x - left, 300);
    const rootAuto = rootDevs.length ? [left + TOPO.T, 0] : [left + Math.max(0, width / 2 - 120), 0];
    if (t.rec) {
      add(t.key, { type: "switch", isRoot: true, sw: t.rec,
        host: t.rec.hostId ? hosts.find(h => h.id === t.rec.hostId) || null : null, auto: rootAuto });
      rootDevs.forEach((h, i) => add("h:" + h.id, { type: "dev", host: h, parent: t.key,
        auto: [rootAuto[0] + TOPO.PORT_INDENT, 70 + i * TOPO.ROW],
        edge: { style: "list", cable: true, sw: t.rec, port: h.switchPort } }));
    } else {
      add(t.key, { type: "root", host: t.gw, auto: rootAuto });
    }
  };
  // Each network's top side by side, then anything hanging from a router
  // that's itself plugged in further down.
  tops.filter(t => !t.nested).forEach((t, i) => { if (i) x = Math.max(x, 300) + TOPO.COL_GAP * 2; region(t); });
  tops.filter(t => t.nested).forEach(t => { x += TOPO.COL_GAP * 2; region(t); });

  if (c.self) add("self", { type: "self", host: c.self, parent: main.key,
    auto: [nodes.get(main.key).auto[0] + TOPO.SELF_DX, 0], edge: { style: "under" } });
  // Now every node exists, link each leftover virtual switch to its machine
  // (wherever that is on this card), or to the top if it isn't on it.
  for (const n of nodes.values()) {
    if (n.pendingSwitch !== undefined) {
      // An SSID away from its access point links to the AP if it's on the card, or the top.
      n.parent = nodes.has("s:" + n.pendingSwitch) ? "s:" + n.pendingSwitch : (c.whole ? ownerOf(n.sw.subnet) : main).key;
      delete n.pendingSwitch;
      continue;
    }
    if (n.pendingParent === undefined) continue;
    const id = n.pendingParent;
    const top = tops.find(t => (t.rec && t.rec.hostId === id) || (t.gw && !t.gw.synthetic && t.gw.id === id));
    n.parent = nodes.has("h:" + id) ? "h:" + id
      : top && nodes.has(top.key) ? top.key
      : c.self && !c.self.synthetic && c.self.id === id ? "self"
      : (c.whole ? ownerOf(n.sw.subnet) : main).key;
    delete n.pendingParent;
  }
  return { subnet: c.subnet, c, nodes, rootKey: main.key, saved, override: {}, final: new Map(), els: new Map(), edgeEls: [] };
}

// Final positions: where the user put it, or where the layout put it relative
// to its parent's final position.
function topoResolve(m) {
  m.final.clear();
  const get = key => {
    if (m.final.has(key)) return m.final.get(key);
    const n = m.nodes.get(key);
    let p = m.override[key] || m.saved[key];
    if (!p) {
      const par = n.parent && m.nodes.get(n.parent);
      if (par) {
        const pp = get(n.parent);
        p = [pp[0] + n.auto[0] - par.auto[0], pp[1] + n.auto[1] - par.auto[1]];
      } else {
        p = n.auto;
      }
    }
    m.final.set(key, p);
    return p;
  };
  for (const key of m.nodes.keys()) get(key);
}

function topoPath(style, a, b, fromLabelW, trunk) {
  const T = TOPO.T, f = v => v.toFixed(1);
  // A block on a lower row (after Arrange) is joined by a line that runs down the left of the rows above it, then along the gap
  // over its own row, so it never crosses a device list on the way.
  if (trunk != null && style === "down") return `M${f(a[0])} ${f(a[1] + T)} V${f(a[1] + T + 24)} H${f(trunk)} V${f(b[1] - T - 26)} H${f(b[0])} V${f(b[1] - T)}`;
  if (trunk != null && style === "box") return `M${f(a[0])} ${f(a[1] + T)} V${f(a[1] + T + 24)} H${f(trunk)} V${f(b[1] - 22)} H${f(b[0] + 40)} V${f(b[1])}`;
  // Machine to its virtual switch: out past the machine's label, then across.
  if (style === "vside") {
    const sx = a[0] + T + 12 + (fromLabelW || 120);
    const mx = Math.max(sx + 10, b[0] - T - 14);
    return `M${f(sx)} ${f(a[1])} H${f(mx)} V${f(b[1])} H${f(b[0] - T)}`;
  }
  if (style === "vlink") return `M${f(a[0])} ${f(a[1] + T)} L${f(b[0])} ${f(b[1] - T)}`;
  // Wireless: a lightning bolt from the SSID to the device.
  if (style === "bolt") {
    const x0 = a[0] + T * .5, y0 = a[1] + T, x1 = b[0] - T - 3, y1 = b[1];
    const dx = x1 - x0, dy = y1 - y0, len = Math.hypot(dx, dy) || 1, nx = -dy / len, ny = dx / len;
    const amp = Math.min(9, len / 7);
    const pts = [[x0, y0], ...[[.3, 1], [.42, -1], [.58, 1], [.7, -1]].map(([t, sg]) => [x0 + dx * t + nx * amp * sg, y0 + dy * t + ny * amp * sg]), [x1, y1]];
    return "M" + pts.map(q => `${f(q[0])} ${f(q[1])}`).join("L");
  }
  if (style === "list") return `M${f(a[0])} ${f(a[1] + T)} V${f(b[1])} H${f(b[0] - T)}`;
  if (style === "chain") {
    const top = Math.min(a[1], b[1]) - T - 26;
    return `M${f(a[0])} ${f(a[1] - T)} V${f(top)} H${f(b[0])} V${f(b[1] - T)}`;
  }
  if (style === "under") {
    const low = Math.max(a[1], b[1]) + T + 14;
    return `M${f(a[0])} ${f(a[1] + T)} V${f(low)} H${f(b[0])} V${f(b[1] + T)}`;
  }
  if (style === "box") {
    const tx = b[0] + 40, mid = Math.min(a[1] + T + 24, (a[1] + T + b[1]) / 2);
    return `M${f(a[0])} ${f(a[1] + T)} V${f(mid)} H${f(tx)} V${f(b[1])}`;
  }
  // Router to a switch: turn just under the router, clear of the arch a
  // switch-to-switch cable makes above the switch row.
  const mid = Math.min(a[1] + T + 24, (a[1] + T + b[1] - T) / 2);
  return `M${f(a[0])} ${f(a[1] + T)} V${f(mid)} H${f(b[0])} V${f(b[1] - T)}`;
}

// Blocks under one router that sit on more than one row: those on the lower rows get a trunk, a line down the left of the lot.
function topoTrunks(m) {
  const by = new Map();
  for (const e of m.edgeEls) {
    e.trunk = null;
    if (e.style !== "down" && e.style !== "box") continue;
    if (!by.has(e.from)) by.set(e.from, []);
    by.get(e.from).push(e);
  }
  for (const es of by.values()) {
    const ys = es.map(e => m.final.get(e.to)[1]), top = Math.min(...ys);
    if (!ys.some(y => y > top + 100)) continue;
    const left = Math.min(...es.map(e => m.final.get(e.to)[0])) - 44;
    es.forEach((e, i) => { if (ys[i] > top + 100) e.trunk = left; });
  }
}

// Put every drawn node and line where topoResolve says.
function topoPlace(m) {
  topoResolve(m);
  topoTrunks(m);
  for (const [key, el] of m.els) {
    const p = m.final.get(key);
    el.setAttribute("transform", `translate(${p[0].toFixed(1)},${p[1].toFixed(1)})`);
  }
  for (const e of m.edgeEls) {
    const a = m.final.get(e.from), b = m.final.get(e.to);
    e.path.setAttribute("d", topoPath(e.style, a, b, (m.nodes.get(e.from) || {}).labelW, e.trunk));
    if (e.pkt) e.pkt.setAttribute("d", e.path.getAttribute("d"));
    if (e.label) {
      if (e.style === "list") { e.label.setAttribute("x", (b[0] - TOPO.T - 6).toFixed(1)); e.label.setAttribute("y", (b[1] - 5).toFixed(1)); }
      else if (e.style === "down") { e.label.setAttribute("x", (b[0] + 6).toFixed(1)); e.label.setAttribute("y", (b[1] - TOPO.T - 8).toFixed(1)); }
      else { e.label.setAttribute("x", (b[0] + 6).toFixed(1)); e.label.setAttribute("y", (Math.min(a[1], b[1]) - TOPO.T - 30).toFixed(1)); }
    }
  }
}

function topoBounds(m) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const [key, n] of m.nodes) {
    const p = m.final.get(key);
    if (n.type === "box") {
      x0 = Math.min(x0, p[0] - 20); y0 = Math.min(y0, p[1]); x1 = Math.max(x1, p[0] + n.size[0] + 20); y1 = Math.max(y1, p[1] + n.size[1] + 20);
    } else {
      const w = n.labelW || 120;
      x0 = Math.min(x0, p[0] - 50); y0 = Math.min(y0, p[1] - 60); x1 = Math.max(x1, p[0] + TOPO.T + 12 + w); y1 = Math.max(y1, p[1] + 40);
    }
  }
  // A small network shouldn't be blown up to fill the frame: never zoom in
  // past a sensible minimum, centred on what's there.
  const w = Math.max(x1 - x0, 1000), h = Math.max(y1 - y0, 420);
  return [(x0 + x1) / 2 - w / 2, (y0 + y1) / 2 - h / 2, w, h];
}

function topoApplyView(m) {
  const v = mapViews[m.subnet] || topoBounds(m);
  m.view = v.slice();
  m.svg.setAttribute("viewBox", m.view.map(n => n.toFixed(1)).join(" "));
  topoLabelZoom(m);
}

// A big network drawn whole is squeezed into the frame, and every name with it. The names of the gateway, switches and access
// points, the skeleton of the map, grow as the view zooms out so they stay readable, up to twice their size; devices keep theirs,
// and zooming in brings everything back to normal. Done to the text alone, so a theme's own look is left as it is.
const TOPO_LABEL_FROM = .8, TOPO_LABEL_MAX = 2, TOPO_LABEL_ROOM = 210;
function topoLabelZoom(m) {
  // The frame changes size with the window, and has none while its tab is hidden: look again whenever it does.
  if (!m.labelWatch && typeof ResizeObserver === "function") { m.labelWatch = new ResizeObserver(() => topoLabelZoom(m)); m.labelWatch.observe(m.svg); }
  const box = m.svg.getBoundingClientRect();
  if (!box.width || !box.height) return;
  const k = Math.min(box.width / m.view[2], box.height / m.view[3]);        // pixels per unit of drawing
  const z = k >= TOPO_LABEL_FROM ? 1 : Math.min(TOPO_LABEL_MAX, TOPO_LABEL_FROM / k);
  if (z === m.labelZoom) return;
  m.labelZoom = z;
  const at = TOPO.T + 8;                                                    // where a node's names start
  const put = (t, f) => { if (f > 1.05) t.setAttribute("transform", `translate(${at} 0) scale(${f.toFixed(2)}) translate(${-at} 0)`); else t.removeAttribute("transform"); };
  // A name grows only as far as there is room beside it (the machine next to the router is 250 along), and the line under it, which is
  // longer and matters less, grows half as far.
  const room = text => Math.max(1, TOPO_LABEL_ROOM / (text.length * TOPO.CHAR));
  for (const el of m.els.values()) {
    if (!el.hasAttribute("data-gear")) continue;
    const name = el.querySelector(".t-name"), sub = el.querySelector(".t-sub");
    if (name) put(name, Math.min(z, room(name.textContent)));
    if (sub) put(sub, Math.min(1 + (z - 1) / 2, room(sub.textContent)));
  }
}

function topoFit(subnet) {
  delete mapViews[subnet];
  const m = topoModels[subnet];
  if (m && m.glide) m.glide();
  if (m) topoApplyView(m);
}

function topoZoomBy(subnet, factor, center) {
  const m = topoModels[subnet];
  if (!m) return;
  if (m.glide) m.glide();
  const [x, y, w, h] = m.view;
  const nw = Math.min(40000, Math.max(160, w * factor)), k = nw / w;
  const cx = center ? center[0] : x + w / 2, cy = center ? center[1] : y + h / 2;
  m.view = [cx - (cx - x) * k, cy - (cy - y) * k, w * k, h * k];
  mapViews[subnet] = m.view.slice();
  m.svg.setAttribute("viewBox", m.view.map(n => n.toFixed(1)).join(" "));
  topoLabelZoom(m);
}

// Forget everywhere things were put and let the automatic layout decide. There's no question asked: Undo (the toast, the menu,
// Ctrl+Z) puts every place back.
async function topoAutoArrange(subnet) {
  const saved = mapPositions[subnet] || {};
  if (!Object.keys(saved).length) { toast("Already where the automatic layout puts things."); return; }
  const before = {}, from = topoBefore(subnet);
  for (const [k, p] of Object.entries(saved)) before[k] = p.slice();
  await fetch("/api/map/positions?subnet=" + encodeURIComponent(subnet), { method: "DELETE" });
  delete mapPositions[subnet];
  delete mapViews[subnet];
  mapUndoPush(subnet, before, "Reset to automatic");
  mapSig = null;
  await refresh();
  topoSettle(subnet, from);
  toast(`Back to the automatic layout. <a href="#" id="arrUndoLink">Undo</a>`);
  const link = $("arrUndoLink");
  if (link) link.onclick = e => { e.preventDefault(); topoUndo(subnet); };
}

async function saveMapPositions(subnet, positions) {
  const local = mapPositions[subnet] || (mapPositions[subnet] = {});
  for (const [k, p] of Object.entries(positions)) { if (p) local[k] = p; else delete local[k]; }
  try {
    const r = await fetch("/api/map/positions", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ subnet, positions }),
    });
    if (!r.ok) {
      const d = await r.json().catch(() => ({}));
      toast("Couldn't save the map: " + esc(d.error || "HTTP " + r.status));
    }
  } catch { toast("Couldn't reach BAMF to save the map."); }
}

function buildTopologySvg(c) {
  const m = topoModel(c);
  topoModels[c.subnet] = m;
  const svg = svgEl("svg", { class: "map-svg topo" + (mapSelectMode ? " selecting" : ""), role: "group", "aria-label": `Network ${c.subnet}`, preserveAspectRatio: "xMidYMid meet" });
  const gBox = svgEl("g", {}), gEdges = svgEl("g", {}), gLabels = svgEl("g", {}), gNodes = svgEl("g", {});
  const gGuides = svgEl("g", { class: "t-guides" });
  m.guideX = svgEl("line", { class: "t-guide" }); m.guideY = svgEl("line", { class: "t-guide" });
  gGuides.append(m.guideX, m.guideY);
  svg.append(gBox, gEdges, gLabels, gNodes, gGuides);
  m.svg = svg;

  for (const [key, n] of m.nodes) {
    if (n.edge && n.parent) {
      const path = svgEl("path", { class: "spoke" + (n.edge.cable ? " cable" : "") + (n.edge.vlink ? " vlink" : "") + (n.edge.style === "bolt" || n.edge.wifi ? " bolt" : "") + (n.edge.tunnel ? " tunnel" : "") + (n.type === "dev" && !n.host.online ? " off" : "") });
      if (n.type === "dev") path.setAttribute("data-id", n.host.id);
      if (n.type === "dev") sigApply(path, n.host);
      gEdges.appendChild(path);
      // Shown only by some themes: something flowing along a recorded cable.
      // It carries the device's id and state, so a theme can stop the flow
      // to a device that's offline.
      const pkt = n.edge.cable ? svgEl("path", { class: "pkt" + (n.type === "dev" && !n.host.online ? " off" : "") }) : null;
      if (pkt && n.type === "dev") pkt.setAttribute("data-id", n.host.id);
      if (pkt) gEdges.appendChild(pkt);
      let label = null;
      if (n.edge.sw && !n.edge.tunnel && (n.edge.style === "list" || n.edge.port)) {
        // (a "down" cable from a recorded router carries its port too)
        const port = n.edge.port;
        // An access point's devices with no port are its Wi-Fi clients.
        const text = port ? topoClip(portText(n.edge.sw, port).replace(/^port /, ""), n.edge.style === "list" ? 17 : 26)
          : n.edge.sw.kind === "ap" ? "Wi-Fi" : n.edge.sw.kind === "virtual" ? "VM" : "port ?";
        label = svgEl("text", { class: "t-port", "text-anchor": n.edge.style === "list" ? "end" : "start" });
        label.textContent = n.edge.style === "list" ? text : (port ? "port " + text : text);
        gLabels.appendChild(label);
      }
      m.edgeEls.push({ from: n.parent, to: key, style: n.edge.style, path, label, pkt });
    }
    if (n.type === "box") {
      const g = svgEl("g", { class: "tbox" });
      const rect = svgEl("rect", { class: "t-box", x: 0, y: 0, width: n.size[0], height: n.size[1], rx: 12 });
      const title = svgEl("text", { class: "t-box-title", x: n.net ? 28 : 14, y: 20 });
      title.textContent = n.net ? `${n.net} · ${n.count}` : `On this network · ${n.count}`;
      const t2 = svgEl("title", {});
      t2.textContent = (n.net ? `Devices on ${n.net} not recorded on a switch` : "Devices not recorded on a switch") +
        " - Wi-Fi ones, or wired ones not filled in yet. Drop a device here to unplug it.";
      g.setAttribute("data-key", key);
      g.appendChild(t2);
      g.append(rect, title);
      if (n.net) g.appendChild(svgEl("circle", { class: "t-net", cx: 17, cy: 16, r: 4.5, fill: m.c.netColors[n.net] }));
      gBox.appendChild(g);
      m.els.set(key, g);
      n.rect = rect;
      continue;
    }
    gNodes.appendChild(topoNodeEl(m, key, n));
  }
  topoPlace(m);
  topoApplyView(m);
  topoWire(m);
  return svg;
}

function topoNodeEl(m, key, n) {
  const h = n.host;
  let cls, kind, name, sub, tip;
  if (n.type === "switch") {
    cls = h ? mapNodeClass(h, "sw") : "node sw";
    kind = n.sw.kind === "router" ? "router" : n.sw.kind === "ap" || n.sw.kind === "ssid" ? "ap" : n.sw.kind === "virtual" ? "vswitch"
      : n.sw.kind === "vpn" ? "vpn" : "switch";
    name = n.sw.name;
    if (n.sw.kind === "virtual") {
      const on = runsOnHost(n.sw);
      cls += " virtual";
      sub = "virtual switch" + (on ? " · on " + nameOrIp(on) : "");
    } else if (n.sw.kind === "ssid") {
      const ap = apOf(n.sw);
      cls += " virtual";
      sub = "wireless SSID" + (ap ? " · on " + ap.name : "");
    } else if (n.sw.kind === "ap") {
      sub = "access point" + (h ? " · " + h.ip : "");
    } else if (n.sw.kind === "vpn") {
      const more = h ? alsoAt(h).length : 0;
      sub = !m.c.whole && n.sw.subnet && n.sw.subnet !== m.subnet ? "VPN · on " + n.sw.subnet
        : "VPN" + (h ? " · " + h.ip + (more ? ` +${more}` : "") : "");
    } else {
      sub = (n.sw.kind && n.sw.kind !== "switch" ? kindNoun(n.sw.kind) + " · " : "") + `${n.sw.ports} ports` + (h ? " · " + h.ip : "");
    }
    tip = switchTitle(n.sw);
  } else if (n.type === "root") {
    if (h) {
      cls = mapNodeClass(h, "gw"); kind = "router";
      name = h.synthetic ? "Gateway" : nameOrIp(h); sub = h.ip; tip = mapTitle(h, "gw");
      // A router that's the gateway of every network on this map: its address on each.
      if (m.c.group && !h.synthetic) sub = declaredGws.filter(g => g.hostId === h.id && m.c.nets.includes(g.subnet)).map(g => g.ip).join(" · ") || h.ip;
    } else {
      cls = "node net"; kind = "net";
      name = m.c.whole ? "Your network" : m.subnet; sub = m.c.whole ? "no gateway known" : "this network";
      tip = m.c.whole ? "No router or gateway known for these networks" : `Network ${m.subnet}`;
    }
  } else if (n.type === "self") {
    cls = mapNodeClass(h, "self"); kind = "server"; name = "BAMF"; sub = h.ip; tip = mapTitle(h, "self");
  } else if (m.c.selfIds.includes(h.id)) {
    // BAMF, recorded on a switch: drawn with that switch's devices.
    cls = mapNodeClass(h, "self"); kind = "server"; name = "BAMF"; sub = h.ip; tip = mapTitle(h, "self") + "\n" + TOPO_DEV_HINT;
  } else {
    cls = mapNodeClass(h, "dev") + (sigLevel(h) ? " sig-" + sigLevel(h) : ""); kind = deviceKind(h); name = nameOrIp(h); tip = topoDevTip(h);
    // An unnamed device is already labelled by its address: say what it is instead.
    sub = name === h.ip ? (guessFamily(h.osGuess) || h.vendor || "") : h.ip;
  }
  name = topoClip(name, 24);
  n.labelW = Math.max(name.length, sub.length) * TOPO.CHAR;
  const g = svgEl("g", { class: "tnode " + cls, "data-key": key, tabindex: "0", role: "button", "aria-label": name });
  if (h && !h.synthetic) g.setAttribute("data-id", h.id);
  if (n.type === "switch") g.setAttribute("data-sw", n.sw.id);
  if (n.type === "switch" || n.type === "root") g.setAttribute("data-gear", "");
  const title = svgEl("title", {});
  title.textContent = tip;
  g.appendChild(title);
  if (n.type === "dev") g.appendChild(svgEl("circle", { r: 26, class: "blink-ring" }));
  g.appendChild(svgEl("rect", { x: -20, y: -20, width: 40, height: 40, rx: 11, class: "t-watch" }));
  g.appendChild(svgEl("rect", { x: -TOPO.T, y: -TOPO.T, width: 2 * TOPO.T, height: 2 * TOPO.T, rx: 8, class: "t-tile shape" }));
  const icon = svgEl("path", { d: TOPO_ICONS[kind] || TOPO_ICONS.device, class: "t-icon", transform: "translate(-12,-12)" });
  g.appendChild(icon);
  const t1 = svgEl("text", { class: "t-name", x: TOPO.T + 8, y: -2 });
  t1.textContent = name;
  const t2 = svgEl("text", { class: "t-sub", x: TOPO.T + 8, y: 12 });
  t2.textContent = sub;
  g.append(t1, t2);
  if (n.type === "dev") sigGlyph(g, sigLevel(h));
  // An access point's SSIDs that have no devices, as small tags under its name.
  if (n.tags && n.tags.length) {
    const tr = arrTagRows(n.tags), gt = svgEl("g", { class: "t-tags", transform: `translate(${TOPO.T + 8},22)` });
    tr.rows.forEach((row, ri) => row.forEach(t => {
      const tg = svgEl("g", { class: "t-tag", transform: `translate(${t.x},${ri * 16})` });
      const tt = svgEl("title", {});
      tt.textContent = t.title + (t.text[0] === "+" ? "" : " · wireless SSID with no devices recorded");
      const tx = svgEl("text", { x: 6, y: 9.5 });
      tx.textContent = t.text;
      tg.append(tt, svgEl("rect", { width: t.w, height: 13, rx: 6.5 }), tx);
      gt.appendChild(tg);
    }));
    g.appendChild(gt);
    n.labelW = Math.max(n.labelW, tr.width - 4);
  }
  // In the whole-network view, a dot per network the device is on.
  if (m.c.whole && h && !h.synthetic) {
    const nets = [...new Set([h.subnet, ...curAddrs(h).map(a => a.subnet)])].filter(v => m.c.netColors[v]).slice(0, 4);
    nets.forEach((v, i) => g.appendChild(svgEl("circle", { class: "t-net", cx: -TOPO.T + 2 + i * 8, cy: -TOPO.T - 5, r: 3.5, fill: m.c.netColors[v] })));
  }
  // A theme may add its own touches to the node: inserted before .t-watch they
  // sit under the tile, before .t-name over the icon.
  try { festiveHooks.decorateNode?.(g, n); } catch (e) { console.error("theme decorateNode failed", e); }
  m.els.set(key, g);
  return g;
}

// What a click on a node does: the same dialogs as everywhere else.
// Where a click lands on a device decides what opens: its icon tile opens
// the Map icon picker, anywhere else (its name) opens Plugged into.
// .t-deco-icon: anything a theme adds that should count as the icon.
const TOPO_ICON_PARTS = ".t-tile, .t-icon, .t-ghost, .t-gift, .t-hat, .t-pk, .t-watch, .blink-ring, .t-mustard, .t-hd, .t-deco-icon";
const TOPO_DEV_HINT = "Click the icon to change its type · click the name to record where it's plugged in";
function topoDevTip(h) { return mapTitle(h, "dev") + "\n" + TOPO_DEV_HINT; }
function topoActivate(m, key, target = null) {
  const n = m.nodes.get(key);
  if (!n) return;
  if (n.type === "dev") {
    const h = hosts.find(x => x.id === n.host.id) || n.host;
    if (target && target.closest && target.closest(TOPO_ICON_PARTS)) openTypeDialog(h);
    else openPlugDialog(h);
  }
  else if (n.type === "switch") openPortsDialog(switches.find(s => s.id === n.sw.id) || n.sw);
  else if ((n.type === "root" || n.type === "self") && n.host && !n.host.synthetic) jumpToHost(n.host.id);
}

// The node a dragged node would be dropped onto, if any: a switch, the
// router, or the box.
function topoDropTarget(m, key) {
  const n = m.nodes.get(key), p = m.final.get(key);
  if (n.type !== "dev" && n.type !== "switch") return null;
  if (n.type === "switch" && innerKind(n.sw.kind)) return null;   // it lives inside its machine or access point
  const below = new Set();
  if (n.type === "switch") {
    const walk = k => { for (const [kk, nn] of m.nodes) if (nn.parent === k && nn.type === "switch" && !below.has(kk)) { below.add(kk); walk(kk); } };
    walk(key);
  }
  for (const [k, t] of m.nodes) {
    if (k === key || below.has(k)) continue;
    if (n.type === "switch" && t.type === "switch" && memberKind(t.sw.kind)) continue;
    if (t.type === "switch" || (t.type === "root" && n.type === "switch")) {
      const q = m.final.get(k);
      if (Math.hypot(q[0] - p[0], q[1] - p[1]) < 30) return { key: k, node: t };
    }
  }
  if (n.type === "dev") {
    for (const [k, t] of m.nodes) {
      if (t.type !== "box") continue;
      const b = m.final.get(k), size = t.size;
      if (p[0] >= b[0] && p[0] <= b[0] + size[0] && p[1] >= b[1] && p[1] <= b[1] + size[1]) return { key: k, node: t };
    }
  }
  return null;
}

async function topoDrop(m, key) {
  const n = m.nodes.get(key), p = m.final.get(key);
  mapViews[m.subnet] = m.view.slice();   // keep the view where it is once this is saved and redrawn
  const target = topoDropTarget(m, key);
  const subnet = m.subnet;
  if (n.type === "dev" && target && target.node.type === "switch") {
    // Plug it in: the usual dialog, with that switch picked. It then lines up
    // under the switch, so any place it was dragged to is forgotten.
    delete m.override[key];
    topoPlace(m);
    openPlugDialog(hosts.find(x => x.id === n.host.id) || n.host, target.node.sw.id);
    plugAfterSave = () => saveMapPositions(subnet, { [key]: null });
    return;
  }
  if (n.type === "dev" && target && target.node.type === "box") {
    const h = hosts.find(x => x.id === n.host.id) || n.host;
    if (h.switchId) {
      const sw = switches.find(s => s.id === h.switchId);
      await fetch(`/api/hosts/${h.id}/plug`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ switchId: 0, port: 0 }),
      });
      toast(`${esc(nameOrIp(h))} is no longer recorded on ${esc(sw ? sw.name : "a switch")}`);
    }
    await saveMapPositions(subnet, { [key]: null });
    mapSig = null;
    await refresh();
    return;
  }
  if (n.type === "switch" && target) {
    // Plug a switch into another switch, or into the router: the switch
    // dialog, with that uplink picked.
    delete m.override[key];
    await saveMapPositions(subnet, { [key]: null });
    m.saved = mapPositions[subnet] || {};
    topoPlace(m);
    openSwitchDialog(switches.find(s => s.id === n.sw.id) || n.sw);
    const want = target.node.type === "switch" ? "sw:" + target.node.sw.id : "router";
    if ([...$("swUp").options].some(o => o.value === want)) { $("swUp").value = want; syncSwitchDialog(0); }
    return;
  }
  mapUndoPush(subnet, { [key]: (mapPositions[subnet] || {})[key] ? mapPositions[subnet][key].slice() : null }, "Moved");
  await saveMapPositions(subnet, { [key]: [Math.round(p[0]), Math.round(p[1])] });
  m.saved = mapPositions[subnet] || {};
  delete m.override[key];
}

// ---- Arranging ----
// The layouts themselves are in maparrange.js. Here: a snap switch, the lines that show what a dragged node lined up with, and
// undo for every move and every arrange, in this browser session.
let mapSnap = false;
try { mapSnap = localStorage.getItem("bamf-map-snap") === "1"; } catch { }
const mapUndo = [], mapRedo = [];

function topoGuides(m, sn) {
  const [x, y, w, h] = m.view;
  const put = (el, a) => { if (!a) { el.removeAttribute("x1"); el.removeAttribute("x2"); el.removeAttribute("y1"); el.removeAttribute("y2"); } else for (const [k, v] of Object.entries(a)) el.setAttribute(k, v.toFixed(1)); };
  put(m.guideX, sn && sn.guideX !== null ? { x1: sn.guideX, x2: sn.guideX, y1: y, y2: y + h } : null);
  put(m.guideY, sn && sn.guideY !== null ? { x1: x, x2: x + w, y1: sn.guideY, y2: sn.guideY } : null);
}

function mapUndoButtons() {
  document.querySelectorAll(".arr-undo").forEach(b => { b.disabled = !mapUndo.some(u => u.subnet === b.dataset.subnet); });
  document.querySelectorAll(".arr-redo").forEach(b => { b.disabled = !mapRedo.some(u => u.subnet === b.dataset.subnet); });
}

// A new move or arrange: it can be undone, and whatever was waiting to be redone on this map no longer makes sense.
function mapUndoPush(subnet, before, label) {
  mapUndo.push({ subnet, before, label });
  if (mapUndo.length > 40) mapUndo.shift();
  for (let i = mapRedo.length - 1; i >= 0; i--) if (mapRedo[i].subnet === subnet) mapRedo.splice(i, 1);
  mapUndoButtons();
}

// Undo, or with `redo` put back what an undo took away. Either way the other direction gets what is there now, for the same
// nodes, so the two can be stepped through as often as you like.
async function topoUndo(subnet, redo) {
  const from = redo ? mapRedo : mapUndo, to = redo ? mapUndo : mapRedo;
  let i = from.length - 1;
  while (i >= 0 && from[i].subnet !== subnet) i--;
  if (i < 0) return;
  const [u] = from.splice(i, 1);
  const now = {}, here = mapPositions[subnet] || {}, was = topoBefore(subnet);
  for (const k of Object.keys(u.before)) now[k] = here[k] ? here[k].slice() : null;
  to.push({ subnet, before: now, label: u.label });
  mapUndoButtons();
  await saveMapPositions(subnet, u.before);
  mapSig = null;
  renderMap();
  topoSettle(subnet, was);
  toast(`${redo ? "Redid" : "Undid"}: ${esc(u.label.toLowerCase())}. <a href="#" id="arrUndoLink">${redo ? "Undo" : "Redo"}</a>`);
  const link = $("arrUndoLink");
  if (link) link.onclick = e => { e.preventDefault(); topoUndo(subnet, !redo); };
}

// Where everything is drawn now, taken just before a redraw so that Slide into place can start from it. Mid-slide this is where
// things are in the air, so a second arrange carries on from there.
function topoBefore(subnet) {
  const m = topoModels[subnet];
  if (!m || !m.svg || !m.view) return null;
  return { final: new Map([...m.final].map(([k, p]) => [k, p.slice()])), view: m.view.slice() };
}

// The things that have to move, as [key, from, to], between two sets of places; a thing in only one of them stays where it is put.
function slideMoves(from, to) {
  const out = [];
  for (const [k, b] of to) {
    const a = from.get(k);
    if (a && (Math.abs(a[0] - b[0]) > .5 || Math.abs(a[1] - b[1]) > .5)) out.push([k, a, b.slice()]);
  }
  return out;
}

// How far along a slide is at time t (0 to 1): quick off the mark, easing in to land.
function slideEase(t) { return 1 - Math.pow(1 - Math.min(1, Math.max(0, t)), 3); }

// Between the two views (x, y, width, height), e of the way.
function slideView(a, b, e) { return a.map((n, i) => n + (b[i] - n) * e); }

const SLIDE_MS = 420;

// Glide every moved thing, and the view if it changed, from where it was to where the new drawing has it. The lines follow, as they
// are drawn from where things are. Pressing, zooming or the keyboard on the map lets go and lands it at once. Returns false when
// there is nothing to glide, so the caller can fall back to the fade.
function topoSlide(m, before) {
  const moves = slideMoves(before.final, m.final), v0 = before.view, v1 = m.view.slice();
  const viewMoves = v0.some((n, i) => Math.abs(n - v1[i]) > .5);
  if (!moves.length && !viewMoves) return false;
  const svg = m.svg, t0 = performance.now();
  let raf = 0;
  const end = () => {
    cancelAnimationFrame(raf);
    for (const ev of ["pointerdown", "wheel", "keydown"]) svg.removeEventListener(ev, end, true);
    m.glide = null;
    for (const [k] of moves) delete m.override[k];
    topoPlace(m);
    topoApplyView(m);
  };
  const frame = now => {
    if (topoModels[m.subnet] !== m || !svg.isConnected) { m.glide = null; return; }   // redrawn under us: nothing left to land
    const t = (now - t0) / SLIDE_MS, e = slideEase(t);
    for (const [k, a, b] of moves) m.override[k] = [a[0] + (b[0] - a[0]) * e, a[1] + (b[1] - a[1]) * e];
    topoPlace(m);
    m.view = slideView(v0, v1, e);
    svg.setAttribute("viewBox", m.view.map(n => n.toFixed(1)).join(" "));
    if (t >= 1) end(); else raf = requestAnimationFrame(frame);
  };
  m.glide = end;
  for (const ev of ["pointerdown", "wheel", "keydown"]) svg.addEventListener(ev, end, true);
  frame(t0);   // the new drawing is already in place: put things back where they were for the first frame
  return true;
}

// After an arrange the drawing is redrawn in one go. With Slide into place on, and the places before it known, things glide there;
// otherwise a short fade makes the change read as one rather than a flicker.
function topoSettle(subnet, before) {
  const m = topoModels[subnet];
  if (!m || !m.svg || window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  if (mapSlide && before && topoSlide(m, before)) return;
  m.svg.classList.remove("arr-settle");
  void m.svg.getBoundingClientRect();
  m.svg.classList.add("arr-settle");
}

// Tidy up, Compact or Snap to grid on one map, saved and drawn, with a way back.
async function topoArrange(subnet, kind) {
  const m = topoModels[subnet];
  if (!m) return;
  const box = m.svg.getBoundingClientRect();
  const ratio = box.width && box.height ? Math.min(2.6, Math.max(1.3, box.width / box.height * .9)) : ARR.RATIO;
  const label = { tidy: "Tidied up", compact: "Made compact", grid: "Snapped to the grid" }[kind];
  const positions = arrArrange(m, kind, ratio);
  if (!Object.keys(positions).length) { toast(kind === "grid" ? "Everything is already on the grid." : "Nothing to move: it's already laid out that way."); return; }
  await topoCommit(subnet, positions, label, false);
}

// Save a set of new places as one undoable step, redraw, and say so. keepView leaves the view where it is, for something done to a
// few things you're looking at; an arrange of the whole map re-fits it.
async function topoCommit(subnet, positions, label, keepView) {
  const m = topoModels[subnet];
  const before = {}, from = topoBefore(subnet);
  for (const k of Object.keys(positions)) before[k] = m.saved[k] ? m.saved[k].slice() : null;
  await saveMapPositions(subnet, positions);
  mapUndoPush(subnet, before, label);
  if (keepView) mapViews[subnet] = m.view.slice(); else delete mapViews[subnet];
  mapSig = null;
  renderMap();
  topoSettle(subnet, from);
  toast(`${label}. <a href="#" id="arrUndoLink">Undo</a>`);
  const link = $("arrUndoLink");
  if (link) link.onclick = e => { e.preventDefault(); topoUndo(subnet); };
}

// ---- Selecting ----
// Shift-click or Shift-drag (or the Select button, which makes a plain click and drag do it) picks things on a map; the bar that
// appears lines them up or spaces them out, and dragging one of them takes the lot. The pick is kept per map and survives a redraw.
const mapSel = {};
let mapSelectMode = false;

function topoSelSet(m) { return mapSel[m.subnet] || (mapSel[m.subnet] = new Set()); }

// how: "set" replaces the pick, "add" adds to it, "toggle" flips each.
function topoSelect(m, keys, how) {
  const sel = topoSelSet(m);
  if (how === "set") sel.clear();
  for (const k of keys) {
    if (!m.nodes.has(k)) continue;
    if (how === "toggle" && sel.has(k)) sel.delete(k); else sel.add(k);
  }
  topoSelApply(m);
}

// Draw the pick: a ring on each picked thing, and the bar with what can be done to this many.
function topoSelApply(m) {
  const sel = topoSelSet(m);
  for (const k of [...sel]) if (!m.nodes.has(k)) sel.delete(k);
  for (const [k, el] of m.els) el.classList.toggle("sel", sel.has(k));
  const bar = m.svg.parentElement && m.svg.parentElement.querySelector(".sel-bar");
  if (!bar) return;
  const movers = arrPicked(m, [...sel]).length;
  bar.classList.toggle("show", sel.size > 0);
  bar.querySelector(".sel-n").textContent = sel.size === 1 ? "1 selected" : sel.size + " selected";
  bar.querySelectorAll("[data-need]").forEach(b => { b.disabled = movers < Number(b.dataset.need); });
}

const SEL_TOOLS = [
  ["left", "Align left edges", "M2 1v14M4 3h9v4H4zM4 9h5v4H4z", 2],
  ["hcenter", "Centre across", "M8 1v14M3 3h10v4H3zM5 9h6v4H5z", 2],
  ["right", "Align right edges", "M14 1v14M3 3h9v4H3zM7 9h5v4H7z", 2],
  null,
  ["top", "Align tops", "M1 2h14M3 4v9h4V4zM9 4v5h4V4z", 2],
  ["vmiddle", "Centre down", "M1 8h14M3 3v10h4V3zM9 5v6h4V5z", 2],
  ["bottom", "Align bottoms", "M1 14h14M3 5v9h4V5zM9 9v5h4V9z", 2],
  null,
  ["dh", "Space evenly across (three or more)", "M2 1v14M14 1v14M6 4v8h4V4z", 3],
  ["dv", "Space evenly down (three or more)", "M1 2h14M1 14h14M4 6h8v4H4z", 3],
];

function selBar(c) {
  const bar = document.createElement("div");
  bar.className = "sel-bar edit-only";
  bar.setAttribute("role", "toolbar");
  bar.setAttribute("aria-label", "Selected things");
  const n = document.createElement("span");
  n.className = "sel-n";
  bar.appendChild(n);
  for (const t of SEL_TOOLS) {
    if (!t) { const s = document.createElement("span"); s.className = "sel-sep"; bar.appendChild(s); continue; }
    const [how, title, path, need] = t;
    const b = document.createElement("button");
    b.type = "button"; b.title = title; b.setAttribute("aria-label", title); b.dataset.need = need;
    b.innerHTML = `<svg viewBox="0 0 16 16" aria-hidden="true"><path d="${path}"/></svg>`;
    b.onclick = () => topoAlign(c.subnet, how);
    bar.appendChild(b);
  }
  const s = document.createElement("span");
  s.className = "sel-sep";
  const clear = document.createElement("button");
  clear.type = "button"; clear.textContent = "Clear"; clear.title = "Let go of the selection (Esc)";
  clear.onclick = () => { const m = topoModels[c.subnet]; if (m) topoSelect(m, [], "set"); };
  bar.append(s, clear);
  return bar;
}

const SEL_LABEL = { left: "Aligned left", hcenter: "Centred across", right: "Aligned right", top: "Aligned tops", vmiddle: "Centred down", bottom: "Aligned bottoms", dh: "Spaced evenly across", dv: "Spaced evenly down" };
async function topoAlign(subnet, how) {
  const m = topoModels[subnet], sel = mapSel[subnet];
  if (!m || !sel || !sel.size) return;
  const keys = [...sel];
  const positions = how === "dh" ? arrDistribute(m, keys, "h") : how === "dv" ? arrDistribute(m, keys, "v") : arrAlign(m, keys, how);
  if (!Object.keys(positions).length) { toast(how === "dh" || how === "dv" ? "Already evenly spaced." : "Already lined up that way."); return; }
  await topoCommit(subnet, positions, SEL_LABEL[how], true);
}

// A group dragged by one of its members: each thing in it, and what you'd placed inside, is saved where it ended up.
async function topoDropGroup(m, g) {
  const subnet = m.subnet, out = {}, before = {}, saved = mapPositions[subnet] || {};
  for (const k of g.starts.keys()) {
    const p = m.final.get(k);
    out[k] = [Math.round(p[0]), Math.round(p[1])];
    before[k] = saved[k] ? saved[k].slice() : null;
  }
  mapViews[subnet] = m.view.slice();
  mapUndoPush(subnet, before, "Moved a selection");
  await saveMapPositions(subnet, out);
  m.saved = mapPositions[subnet] || {};
  for (const k of g.starts.keys()) delete m.override[k];
}

// Ctrl+Z on the Map puts back the last move or arrange; Ctrl+Shift+Z or Ctrl+Y does it again.
document.addEventListener("keydown", e => {
  const key = e.key.toLowerCase();
  if (!(e.ctrlKey || e.metaKey) || e.altKey || (key !== "z" && key !== "y")) return;
  const redo = key === "y" ? !e.shiftKey : e.shiftKey;
  if (key === "y" && e.shiftKey) return;
  const t = e.target;
  if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
  const wrap = $("mapClusters");
  if (!wrap || !wrap.offsetParent || role === "viewer") return;
  const stack = redo ? mapRedo : mapUndo;
  for (let i = stack.length - 1; i >= 0; i--) {
    if (wrap.querySelector(`.map-card[data-subnet="${CSS.escape(stack[i].subnet)}"]`)) { e.preventDefault(); topoUndo(stack[i].subnet, redo); return; }
  }
});

// Pointer handling for one card: drag a node, drag the background to pan,
// wheel or pinch to zoom. A press that doesn't move is a click.
function topoWire(m) {
  const svg = m.svg;
  const pointers = new Map();
  let mode = null, st = null, pinch = null, lastTarget = null;
  const toSvg = e => {
    const p = svg.createSVGPoint();
    p.x = e.clientX; p.y = e.clientY;
    const q = p.matrixTransform(svg.getScreenCTM().inverse());
    return [q.x, q.y];
  };
  const markTarget = key => {
    const t = key ? topoDropTarget(m, key) : null;
    const el = t ? (t.node.type === "box" ? t.node.rect : m.els.get(t.key)) : null;
    if (lastTarget && lastTarget !== el) lastTarget.classList.remove("drop-target");
    if (el) el.classList.add("drop-target");
    lastTarget = el;
  };
  svg.addEventListener("pointerdown", e => {
    if (e.button !== 0 && e.pointerType === "mouse") return;
    try { svg.setPointerCapture(e.pointerId); } catch { /* not every pointer can be captured */ }
    pointers.set(e.pointerId, [e.clientX, e.clientY]);
    if (pointers.size === 2) {
      if (mode === "node" && st.moved) {
        delete m.override[st.key];
        if (st.group) for (const k of st.group.starts.keys()) delete m.override[k];
        topoPlace(m); markTarget(null); topoGuides(m, null);
      }
      if (mode === "marquee" && st.rect) { st.rect.remove(); mapDragging = false; }
      const [a, b] = [...pointers.values()];
      mode = "pinch";
      pinch = { dist: Math.hypot(a[0] - b[0], a[1] - b[1]), view: m.view.slice() };
      return;
    }
    // View only: nothing moves, so a drag anywhere pans.
    const g = role === "viewer" ? null : e.target.closest(".tnode, .tbox");
    // Picking: Shift, Ctrl or Cmd held, or the Select button on. A press on a node then picks it on release; on the background it
    // starts a box.
    const pick = role !== "viewer" && (e.shiftKey || e.ctrlKey || e.metaKey || mapSelectMode);
    if (g) {
      const key = g.getAttribute("data-key");
      mode = "node";
      st = { key, el: g, target: e.target, sx: e.clientX, sy: e.clientY, start: m.final.get(key).slice(), p0: toSvg(e), moved: false, pick };
    } else if (pick) {
      mode = "marquee";
      st = { sx: e.clientX, sy: e.clientY, p0: toSvg(e), add: e.shiftKey || e.ctrlKey || e.metaKey, moved: false, rect: null };
    } else {
      mode = "pan";
      st = { sx: e.clientX, sy: e.clientY, view: m.view.slice() };
      svg.classList.add("panning");
    }
  });
  svg.addEventListener("pointermove", e => {
    if (!pointers.has(e.pointerId)) return;
    pointers.set(e.pointerId, [e.clientX, e.clientY]);
    if (mode === "pinch" && pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      const d = Math.hypot(a[0] - b[0], a[1] - b[1]);
      if (d > 0) {
        m.view = pinch.view.slice();
        const mid = toSvg({ clientX: (a[0] + b[0]) / 2, clientY: (a[1] + b[1]) / 2 });
        topoZoomBy(m.subnet, pinch.dist / d, mid);
      }
      return;
    }
    if (mode === "node") {
      if (!st.moved && Math.hypot(e.clientX - st.sx, e.clientY - st.sy) < 5) return;
      if (!st.moved) {
        st.moved = true; mapDragging = true; st.el.classList.add("dragging");
        // Dragging one of several picked things takes them all, each with what hangs from it.
        const sel = mapSel[m.subnet], movers = sel && sel.has(st.key) ? arrPicked(m, [...sel]) : [];
        if (movers.length > 1) {
          const starts = new Map(), skip = new Set();
          for (const b of movers) for (const k of b.keys) { skip.add(k); if (k === b.key || m.saved[k]) starts.set(k, m.final.get(k).slice()); }
          st.group = { starts, skip };
        }
      }
      const p = toSvg(e);
      let at = [st.start[0] + p[0] - st.p0[0], st.start[1] + p[1] - st.p0[1]];
      // Snap on (Alt held lets one drag go free): line up with a neighbour's row or column, else land on the grid.
      if (mapSnap && !e.altKey && m.nodes.get(st.key).type !== "box") {
        const box = svg.getBoundingClientRect();
        const k = Math.min(box.width / m.view[2], box.height / m.view[3]) || 1;
        const sn = arrSnapDrag(m, st.key, at, 10 / k, st.group && st.group.skip);
        at = sn.p;
        topoGuides(m, sn);
      } else topoGuides(m, null);
      if (st.group) {
        const dx = at[0] - st.start[0], dy = at[1] - st.start[1];
        for (const [k, s0] of st.group.starts) m.override[k] = [s0[0] + dx, s0[1] + dy];
        topoPlace(m);
      } else {
        m.override[st.key] = at;
        topoPlace(m);
        markTarget(st.key);
      }
    } else if (mode === "marquee") {
      if (!st.moved && Math.hypot(e.clientX - st.sx, e.clientY - st.sy) < 5) return;
      if (!st.moved) { st.moved = true; mapDragging = true; st.rect = svgEl("rect", { class: "t-marquee" }); svg.appendChild(st.rect); }
      const p = toSvg(e);
      st.rect.setAttribute("x", Math.min(st.p0[0], p[0]).toFixed(1)); st.rect.setAttribute("y", Math.min(st.p0[1], p[1]).toFixed(1));
      st.rect.setAttribute("width", Math.abs(p[0] - st.p0[0]).toFixed(1)); st.rect.setAttribute("height", Math.abs(p[1] - st.p0[1]).toFixed(1));
    } else if (mode === "pan") {
      const k = st.view[2] / svg.clientWidth;
      const kk = Math.max(k, st.view[3] / svg.clientHeight);
      m.view = [st.view[0] - (e.clientX - st.sx) * kk, st.view[1] - (e.clientY - st.sy) * kk, st.view[2], st.view[3]];
      svg.setAttribute("viewBox", m.view.map(n => n.toFixed(1)).join(" "));
    }
  });
  const end = async e => {
    if (!pointers.has(e.pointerId)) return;
    pointers.delete(e.pointerId);
    if (mode === "pinch") { if (pointers.size === 0) mode = null; mapViews[m.subnet] = m.view.slice(); return; }
    if (mode === "pan") {
      svg.classList.remove("panning");
      if (Math.hypot(e.clientX - st.sx, e.clientY - st.sy) > 3) mapViews[m.subnet] = m.view.slice();
      else if (topoSelSet(m).size) topoSelect(m, [], "set");   // a click on the background lets go
    } else if (mode === "marquee") {
      const s = st;
      if (s.rect) {
        // Whatever the box touches; a group box (which is big, and holds what's in it) only if the box takes in all of it.
        const p = toSvg(e), x0 = Math.min(s.p0[0], p[0]), x1 = Math.max(s.p0[0], p[0]), y0 = Math.min(s.p0[1], p[1]), y1 = Math.max(s.p0[1], p[1]);
        s.rect.remove();
        const hit = [...m.nodes].filter(([k, n]) => {
          if (n.type === "root") return false;
          const r = arrExtent(m, k);
          return n.type === "box" ? r[0] >= x0 && r[2] <= x1 && r[1] >= y0 && r[3] <= y1 : r[0] < x1 && r[2] > x0 && r[1] < y1 && r[3] > y0;
        }).map(([k]) => k);
        mapDragging = false;
        topoSelect(m, hit, s.add ? "add" : "set");
      } else if (!s.add) topoSelect(m, [], "set");
    } else if (mode === "node") {
      const s = st;
      s.el.classList.remove("dragging");
      topoGuides(m, null);
      markTarget(null);
      if (!s.moved) { if (s.pick) topoSelect(m, [s.key], "toggle"); else topoActivate(m, s.key, s.target); }
      else {
        try { await (s.group ? topoDropGroup(m, s.group) : topoDrop(m, s.key)); } finally { mapDragging = false; }
      }
    }
    mode = null;
  };
  svg.addEventListener("pointerup", end);
  svg.addEventListener("pointercancel", end);
  // A plain wheel scrolls the page, so scrolling down past several maps
  // doesn't get caught zooming each one. Ctrl (or Cmd) + wheel zooms, and so
  // does a trackpad pinch, which browsers report as Ctrl + wheel.
  let hintTimer = null;
  svg.addEventListener("wheel", e => {
    if (!e.ctrlKey && !e.metaKey) {
      const hint = svg.parentElement && svg.parentElement.querySelector(".topo-hint");
      if (hint) {
        hint.classList.add("show");
        clearTimeout(hintTimer);
        hintTimer = setTimeout(() => hint.classList.remove("show"), 1200);
      }
      return;
    }
    e.preventDefault();
    topoZoomBy(m.subnet, e.deltaY < 0 ? 1 / 1.15 : 1.15, toSvg(e));
  }, { passive: false });
  svg.addEventListener("keydown", e => {
    const g = e.target.closest && e.target.closest(".tnode");
    if (g && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      if (e.shiftKey && role !== "viewer") topoSelect(m, [g.getAttribute("data-key")], "toggle"); else topoActivate(m, g.getAttribute("data-key"));
    }
    // Esc lets go of a selection; Ctrl+A picks everything but the router.
    if (e.key === "Escape" && topoSelSet(m).size) { e.preventDefault(); e.stopPropagation(); topoSelect(m, [], "set"); }
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === "a" && role !== "viewer") {
      e.preventDefault();
      topoSelect(m, [...m.nodes].filter(([, n]) => n.type !== "root").map(([k]) => k), "set");
    }
    // T on a focused device: its type / icon.
    if (g && (e.key === "t" || e.key === "T") && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const n = m.nodes.get(g.getAttribute("data-key"));
      if (n && n.type === "dev") { e.preventDefault(); openTypeDialog(hosts.find(x => x.id === n.host.id) || n.host); }
    }
  });
}

document.querySelectorAll(".map-style button[data-style]").forEach(b => b.onclick = () => {
  mapStyle = b.dataset.style;
  try { localStorage.setItem("bamf-map-style", mapStyle); } catch { }
  mapSig = null;
  renderMap();
});
// Per network, or everything as one diagram. The whole view is a topology
// drawing, so picking it switches Radial back to Topology.
document.querySelectorAll("#mapScope button").forEach(b => b.onclick = () => {
  mapWhole = b.dataset.scope === "whole";
  if (mapWhole && mapStyle !== "topology") {
    mapStyle = "topology";
    try { localStorage.setItem("bamf-map-style", mapStyle); } catch { }
  }
  try { localStorage.setItem("bamf-map-whole", mapWhole ? "1" : "0"); } catch { }
  mapSig = null;
  renderMap();
});

// The Arrange menu: a few ways to straighten a map once it's roughly where you want it.
function arrangeMenu(c) {
  const wrap = document.createElement("span");
  wrap.className = "tools-menu-wrap arr-wrap edit-only";
  const open = document.createElement("button");
  open.className = "toggle"; open.type = "button"; open.textContent = "Arrange ▾";
  open.title = "Straighten the layout, or fit it to the screen";
  open.setAttribute("aria-haspopup", "menu"); open.setAttribute("aria-expanded", "false");
  const menu = document.createElement("div");
  menu.className = "tools-menu arr-menu";
  menu.setAttribute("role", "menu");
  const close = back => { menu.classList.remove("show"); open.setAttribute("aria-expanded", "false"); if (back) open.focus(); };
  const item = (title, text, fn, extra) => {
    const b = document.createElement("button");
    b.type = "button"; b.className = "arr-item" + (extra ? " " + extra : "");
    b.setAttribute("role", "menuitem");
    b.innerHTML = "<b></b><span></span>";
    b.children[0].textContent = title; b.children[1].textContent = text;
    b.onclick = () => { close(false); fn(b); };
    menu.appendChild(b);
    return b;
  };
  item("Tidy up", "Straighten what you've placed: things that sit about level become a row, spaced evenly, nothing overlapping.", () => topoArrange(c.subnet, "tidy"));
  item("Compact", "Reflow it into rows the shape of the screen, instead of one long strip. Best in full screen.", () => topoArrange(c.subnet, "compact"));
  item("Snap to grid", "Put everything on a grid.", () => topoArrange(c.subnet, "grid"));
  const undo = item("Undo", "Put back the last move or arrange (Ctrl+Z).", () => topoUndo(c.subnet), "arr-undo");
  undo.dataset.subnet = c.subnet;
  undo.disabled = !mapUndo.some(u => u.subnet === c.subnet);
  const redo = item("Redo", "Do again what you just undid (Ctrl+Shift+Z).", () => topoUndo(c.subnet, true), "arr-redo");
  redo.dataset.subnet = c.subnet;
  redo.disabled = !mapRedo.some(u => u.subnet === c.subnet);
  item("Reset to automatic", c.whole ? "Forget everywhere you put things on this map. Undo brings them back." : "Forget everywhere you put things on this network. Undo brings them back.", () => topoAutoArrange(c.subnet), "arr-reset");
  open.onclick = e => {
    e.stopPropagation();
    document.querySelectorAll(".arr-menu.show").forEach(x => { if (x !== menu) x.classList.remove("show"); });
    const on = menu.classList.toggle("show");
    open.setAttribute("aria-expanded", on ? "true" : "false");
    if (on && e.detail === 0) { const first = menu.querySelector(".arr-item:not(:disabled)"); if (first) first.focus(); }   // opened from the keyboard
  };
  // Arrow keys move down the list, Escape closes it and gives focus back to the button.
  wrap.addEventListener("keydown", e => {
    if (!menu.classList.contains("show")) return;
    if (e.key === "Escape") { e.preventDefault(); close(true); return; }
    const items = [...menu.querySelectorAll(".arr-item:not(:disabled)")];
    if (!items.length || !/^Arrow(Down|Up)$|^(Home|End)$/.test(e.key)) return;
    e.preventDefault();
    const i = items.indexOf(document.activeElement);
    const next = e.key === "Home" ? 0 : e.key === "End" ? items.length - 1 : e.key === "ArrowDown" ? (i + 1) % items.length : (i <= 0 ? items.length - 1 : i - 1);
    items[next].focus();
  });
  wrap.append(open, menu);
  return wrap;
}
document.addEventListener("click", e => {
  if (e.target.closest(".arr-wrap")) return;
  document.querySelectorAll(".arr-menu.show").forEach(x => { x.classList.remove("show"); const b = x.previousElementSibling; if (b) b.setAttribute("aria-expanded", "false"); });
});

function buildClusterCard(c) {
  const card = document.createElement("section");
  card.className = "map-card";
  card.setAttribute("data-subnet", c.subnet);
  const head = document.createElement("div");
  head.className = "map-head";
  const name = document.createElement("span");
  name.className = "map-net";
  const gwHost = c.group ? hosts.find(h => h.id === Number(c.subnet.slice(3))) : null;
  name.textContent = c.group ? `Networks behind ${gwHost ? nameOrIp(gwHost) : "the gateway"}` : c.whole ? "Whole network" : c.subnet;
  const cnt = document.createElement("span");
  cnt.className = "map-cnt";
  const tag = document.createElement("span");
  tag.className = "map-tag";
  tag.textContent = c.whole ? `${c.nets.length} networks`
    : c.mode === "skipped" ? "no interface" : c.mode === "paused" ? "paused" : c.mode ? "via " + c.mode : "";
  head.append(name, cnt, tag);
  // A combined gateway map names its networks and their colours.
  if (c.group) {
    const nets = document.createElement("span");
    nets.className = "map-nets";
    nets.innerHTML = c.nets.map(n => `<span><i style="background:${c.netColors[n]}"></i>${esc(n)}</span>`).join("");
    head.appendChild(nets);
  }
  card.appendChild(head);
  const empty = !c.members.length && !c.self && !c.gw && !c.switches.length;
  const tools = document.createElement("span");
  tools.className = "map-tools";
  const btn = (label, title, fn) => {
    const b = document.createElement("button");
    b.className = "toggle"; b.type = "button"; b.textContent = label; b.title = title;
    b.onclick = fn;
    tools.appendChild(b);
  };
  // A shortcut to the same dialog as Settings → Your network → Switches and routers, with
  // this network already picked.
  btn("+ Switch / router", c.whole ? "Add a switch, router or access point" : "Add a switch, router or access point on this network",
    () => openSwitchDialog(null, null, null, c.whole ? {} : { subnet: c.subnet }));
  tools.lastChild.classList.add("edit-only");
  if (mapStyle === "topology" && !empty) {
    btn("−", "Zoom out", () => topoZoomBy(c.subnet, 1.25));
    btn("+", "Zoom in", () => topoZoomBy(c.subnet, 0.8));
    btn("Fit", "Show everything", () => topoFit(c.subnet));
    tools.appendChild(arrangeMenu(c));
    const snap = document.createElement("button");
    snap.className = "toggle edit-only arr-snap" + (mapSnap ? " active" : ""); snap.type = "button"; snap.textContent = "Snap";
    snap.title = "Snap what you drag to a neighbour's row or column, or to a grid. Hold Alt to drag one thing free.";
    snap.onclick = () => {
      mapSnap = !mapSnap;
      try { localStorage.setItem("bamf-map-snap", mapSnap ? "1" : "0"); } catch { }
      document.querySelectorAll(".arr-snap").forEach(b => b.classList.toggle("active", mapSnap));
    };
    tools.appendChild(snap);
    const pick = document.createElement("button");
    pick.className = "toggle edit-only arr-pick" + (mapSelectMode ? " active" : ""); pick.type = "button"; pick.textContent = "Select";
    pick.title = "Pick several things to move, line up or space out: drag a box round them, or click them one by one. Shift-click and Shift-drag do the same without this on.";
    pick.onclick = () => {
      mapSelectMode = !mapSelectMode;
      document.querySelectorAll(".arr-pick").forEach(b => b.classList.toggle("active", mapSelectMode));
      document.querySelectorAll(".map-svg.topo").forEach(s => s.classList.toggle("selecting", mapSelectMode));
    };
    tools.appendChild(pick);
    if (c.switches.some(s => s.kind === "ap")) {
      const wf = document.createElement("button");
      wf.className = "toggle arr-wifi" + (mapWifiCompact ? " active" : ""); wf.type = "button"; wf.textContent = "Compact Wi-Fi";
      wf.title = "Draw each access point's SSIDs under it, the ones with no devices as small tags and the rest with their devices in one list. Much narrower than SSIDs beside every access point.";
      wf.onclick = () => {
        mapWifiCompact = !mapWifiCompact;
        try { localStorage.setItem("bamf-map-wifi-compact", mapWifiCompact ? "1" : "0"); } catch { }
        for (const k of Object.keys(mapViews)) delete mapViews[k];
        mapSig = null;
        renderMap();
      };
      tools.appendChild(wf);
    }
    if (c.members.some(hasSignal)) {
      const sg = document.createElement("button");
      sg.className = "toggle arr-signal" + (mapSignal ? " active" : ""); sg.type = "button"; sg.textContent = "Signal colours";
      sg.title = "Colour each Wi-Fi device, and the line to it, by how well it hears its access point: green is good, amber fair, red weak. Four small bars say the same without colour.";
      sg.onclick = () => {
        mapSignal = !mapSignal;
        try { localStorage.setItem("bamf-map-signal", mapSignal ? "1" : "0"); } catch { }
        document.querySelectorAll(".arr-signal").forEach(b => b.classList.toggle("active", mapSignal));
        document.querySelectorAll(".sig-key").forEach(k => k.hidden = !mapSignal);
        renderMap();
      };
      tools.appendChild(sg);
      const key = document.createElement("span");
      key.className = "sig-key"; key.hidden = !mapSignal;
      key.title = "Wi-Fi signal: strong is -67 dBm or better, fair down to -75, weak below that";
      key.innerHTML = `<span class="sig-good"><i></i>strong</span><span class="sig-fair"><i></i>fair</span><span class="sig-weak"><i></i>weak</span>`;
      tools.appendChild(key);
    }
    const sl = document.createElement("button");
    sl.className = "toggle edit-only map-slide" + (mapSlide ? " active" : ""); sl.type = "button"; sl.textContent = "Slide into place";
    sl.title = "When Tidy up, Compact, Snap to grid, Align, Undo or Put back moves things, glide each one to its new place instead of redrawing the map in one go. Off, the map fades in. Nothing moves if your system asks for reduced motion.";
    sl.onclick = () => {
      mapSlide = !mapSlide;
      try { localStorage.setItem("bamf-map-slide", mapSlide ? "1" : "0"); } catch { }
      document.querySelectorAll(".map-slide").forEach(b => b.classList.toggle("active", mapSlide));
    };
    tools.appendChild(sl);
    const rm = document.createElement("button");
    rm.className = "toggle map-rc" + (mapMenuOn ? " active" : ""); rm.type = "button"; rm.textContent = "Right-click menu";
    rm.title = "Right-click a device, switch or access point (or press the menu key on it) for what you can do to it: where it's plugged in, its icon and type, a note, snooze, ping, copy its address, and put it back in its automatic place. Off, the browser's own menu appears.";
    rm.onclick = () => {
      mapMenuOn = !mapMenuOn;
      try { localStorage.setItem("bamf-map-menu", mapMenuOn ? "1" : "0"); } catch { }
      document.querySelectorAll(".map-rc").forEach(b => b.classList.toggle("active", mapMenuOn));
      if (!mapMenuOn) mapMenuClose();
    };
    tools.appendChild(rm);
    btn("Print", "A clean white drawing of this map on one page, for the closet door", () => printTopoMap(c.subnet));
    btn(mapExpanded === c.subnet ? "Exit full screen" : "⛶ Full screen",
      mapExpanded === c.subnet ? "Back to the page (Esc)" : "Fill the screen with this map", () => toggleMapExpanded(c.subnet));
  }
  if (c.mode !== "skipped") head.appendChild(tools);
  if (empty) {
    const e = document.createElement("div");
    e.className = "map-empty";
    e.textContent = c.mode === "skipped"
      ? "This machine has no interface on this network, so nothing on it can be seen."
      : "No devices seen on this network yet.";
    card.appendChild(e);
  } else if (mapStyle === "topology") {
    const frame = document.createElement("div");
    frame.className = "topo-frame";
    frame.appendChild(buildTopologySvg(c));
    // How tall the drawing is for its width, so the frame can grow to fit it.
    const b = topoBounds(topoModels[c.subnet]);
    if (b && b[2] > 0) frame.dataset.ratio = (b[3] / b[2]).toFixed(3);
    const hint = document.createElement("div");
    hint.className = "topo-hint";
    hint.textContent = /Mac|iPhone|iPad/.test(navigator.platform) ? "Hold ⌘ or Ctrl and scroll to zoom" : "Hold Ctrl and scroll to zoom";
    frame.appendChild(hint);
    frame.appendChild(selBar(c));
    topoSelApply(topoModels[c.subnet]);
    card.appendChild(frame);
  } else {
    card.appendChild(buildClusterSvg(c));
  }
  if (mapExpanded === c.subnet) card.classList.add("expanded");
  return card;
}

// A topology frame is as tall as its drawing needs for its width: from a
// short strip for a small network up to nearly the whole window for a big one.
function sizeTopoFrames(wrap) {
  if (innerWidth <= 700) return;
  wrap.querySelectorAll(".topo-frame[data-ratio]").forEach(f => {
    const want = f.clientWidth * Number(f.dataset.ratio) + 24;
    f.style.height = Math.round(Math.max(360, Math.min(want, innerHeight * 0.92))) + "px";
  });
}

// ---- Print ----
// A topology map as a clean white page: the drawing fitted to the paper with
// black lines and text, a title, the date, and a legend. Nothing a theme adds
// comes along. Opens in its own window and asks the browser to print it.
function buildPrintHtml(subnet) {
  const m = topoModels[subnet];
  if (!m || !m.svg) return null;
  const svg = m.svg.cloneNode(true);
  // Tight bounds round what's drawn, so the page holds all of it.
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const [key, n] of m.nodes) {
    const q = m.final.get(key);
    if (n.type === "box") { x0 = Math.min(x0, q[0]); y0 = Math.min(y0, q[1]); x1 = Math.max(x1, q[0] + n.size[0]); y1 = Math.max(y1, q[1] + n.size[1]); }
    else { x0 = Math.min(x0, q[0] - TOPO.T - 4); y0 = Math.min(y0, q[1] - TOPO.T - 30); x1 = Math.max(x1, q[0] + TOPO.T + 12 + (n.labelW || 120)); y1 = Math.max(y1, q[1] + TOPO.T + 6); }
  }
  const pad = 30;
  svg.setAttribute("viewBox", `${(x0 - pad).toFixed(0)} ${(y0 - pad).toFixed(0)} ${(x1 - x0 + 2 * pad).toFixed(0)} ${(y1 - y0 + 2 * pad).toFixed(0)}`);
  svg.setAttribute("class", "print-map");
  svg.removeAttribute("style");
  svg.querySelectorAll(".sel").forEach(e => e.classList.remove("sel"));
  svg.setAttribute("preserveAspectRatio", "xMidYMid meet");
  svg.querySelectorAll(".pkt, .t-hd, .t-mustard, .t-cog, .t-tophat, .t-paperhat, .t-drip, .blink-ring, .t-ghost, .t-pk, .t-hat, .t-gift, .ww-tag, .aq-shell, .t-deco-icon, .t-watch, title").forEach(e => e.remove());
  svg.querySelectorAll("[tabindex]").forEach(e => e.removeAttribute("tabindex"));
  const c = m.c;
  const gwHost = c.group ? hosts.find(h => h.id === Number(subnet.slice(3))) : null;
  const title = c.group ? `Networks behind ${gwHost ? nameOrIp(gwHost) : "the gateway"}` : c.whole ? "Whole network" : subnet;
  const count = c.members.length;
  const nets = c.whole ? c.nets.map(n => `<span><i style="background:${c.netColors[n]}"></i>${esc(n)}</span>`).join("") : "";
  const css = `
@page { size: landscape; margin: 10mm; }
html, body { margin: 0; background: #fff; color: #111; font-family: "Segoe UI", system-ui, sans-serif; }
body { padding: 6mm 8mm; box-sizing: border-box; min-height: 100vh; display: flex; flex-direction: column; }
header { display: flex; justify-content: space-between; align-items: baseline; border-bottom: 2px solid #111; padding-bottom: 4px; margin-bottom: 6px; }
h1 { font-size: 18px; margin: 0; } .meta { font-size: 12px; color: #444; }
.print-map { flex: 1; width: 100%; min-height: 0; height: calc(100vh - 34mm); }
footer { display: flex; flex-wrap: wrap; gap: 6px 18px; font-size: 11px; color: #333; padding-top: 6px; border-top: 1px solid #999; }
footer span { display: inline-flex; align-items: center; gap: 6px; } footer i { display: inline-block; width: 10px; height: 10px; border-radius: 50%; }
footer svg { width: 26px; height: 12px; }
svg text { fill: #111; font-family: Consolas, "IBM Plex Mono", monospace; }
.t-name { font-weight: 700; font-size: 12px; } .t-sub { font-size: 10px; fill: #555; } .t-port { font-size: 9px; fill: #555; } .t-tag rect { fill: #fff; stroke: #b36b00; stroke-width: .8; } .t-tag text { font-size: 8px; fill: #444; }
.t-tile { fill: #fff; stroke: #222; stroke-width: 1.6; } .t-icon { fill: none; stroke: #111; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.node.off .t-tile { stroke: #999; } .node.off text { fill: #777; } .node.unknown .t-tile { stroke: #b36b00; stroke-dasharray: 3 2; }
.spoke { fill: none; stroke: #aaa; stroke-width: 1; } .spoke.cable { stroke: #222; stroke-width: 2; } .spoke.off { stroke-dasharray: 4 3; }
.spoke.sig-excellent, .spoke.sig-good { stroke: #1b8a4b; stroke-width: 1.8; } .spoke.sig-fair { stroke: #b36b00; stroke-width: 1.8; } .spoke.sig-weak { stroke: #c62828; stroke-width: 1.8; }
.t-sig rect { fill: #ccc; } .sig-excellent .t-sig rect:nth-child(-n+4), .sig-good .t-sig rect:nth-child(-n+3) { fill: #1b8a4b; } .sig-fair .t-sig rect:nth-child(-n+2) { fill: #b36b00; } .sig-weak .t-sig rect:nth-child(-n+1) { fill: #c62828; }
.spoke.vlink { stroke: #666; stroke-dasharray: 4 3; } .spoke.bolt { stroke: #b36b00; stroke-width: 1.8; } .spoke.tunnel { stroke: #2f5fa8; stroke-width: 1.6; stroke-dasharray: 1 4; stroke-linecap: round; }
.tbox rect, rect.t-boxrect { fill: #f7f7f7; stroke: #888; stroke-dasharray: 6 4; }
.t-net { stroke: #fff; stroke-width: 1; }
@media print { body { min-height: 0; } .print-map { height: 168mm; } }`;
  return `<!doctype html><html><head><meta charset="utf-8"><title>BAMF map — ${esc(title)}</title><style>${css}</style></head><body>` +
    `<header><h1>${esc(title)}</h1><div class="meta">BAMF · ${esc(new Date().toLocaleString())} · ${count} device${count === 1 ? "" : "s"}</div></header>` +
    svg.outerHTML +
    `<footer><span><svg viewBox="0 0 26 12"><line x1="1" y1="6" x2="25" y2="6" stroke="#222" stroke-width="2"/></svg>cable you recorded</span>` +
    `<span><svg viewBox="0 0 26 12"><line x1="1" y1="6" x2="25" y2="6" stroke="#aaa"/></svg>on this network</span>` +
    `<span><svg viewBox="0 0 26 12"><line x1="1" y1="6" x2="25" y2="6" stroke="#222" stroke-width="2" stroke-dasharray="4 3"/></svg>offline</span>` +
    `<span><svg viewBox="0 0 26 12"><path d="M1 6l6-4 4 6 5-6 4 6 5-4" fill="none" stroke="#b36b00" stroke-width="1.8"/></svg>wireless</span>` +
    `<span><svg viewBox="0 0 26 12"><line x1="1" y1="6" x2="25" y2="6" stroke="#2f5fa8" stroke-width="1.6" stroke-dasharray="1 4" stroke-linecap="round"/></svg>VPN tunnel</span>` +
    (svg.querySelector(".t-sig") ? `<span><i style="background:#1b8a4b"></i>strong Wi-Fi signal</span><span><i style="background:#b36b00"></i>fair</span><span><i style="background:#c62828"></i>weak</span>` : "") +
    `<span><svg viewBox="0 0 26 12"><rect x="2" y="1" width="10" height="10" rx="2" fill="#fff" stroke="#b36b00" stroke-dasharray="3 2"/></svg>unknown device</span>` +
    nets + `</footer></body></html>`;
}
function printTopoMap(subnet) {
  const html = buildPrintHtml(subnet);
  if (!html) { toast("Switch the map to Topology to print it"); return; }
  const w = window.open("", "_blank");
  if (!w) { toast("The browser blocked the print window. Allow pop-ups for BAMF and try again."); return; }
  w.document.open(); w.document.write(html); w.document.close();
  w.focus();
  setTimeout(() => { try { w.print(); } catch { } }, 400);
}

// One map at a time can fill the screen. The browser's own full screen is
// asked for too, when it allows it; leaving that leaves this as well.
let mapExpanded = null;
function toggleMapExpanded(subnet) {
  mapExpanded = mapExpanded === subnet ? null : subnet;
  document.body.classList.toggle("map-expanded", !!mapExpanded);
  try {
    if (mapExpanded && !document.fullscreenElement) document.documentElement.requestFullscreen?.().catch(() => {});
    if (!mapExpanded && document.fullscreenElement) document.exitFullscreen?.().catch(() => {});
  } catch { }
  mapSig = null;
  renderMap();
}
document.addEventListener("fullscreenchange", () => {
  if (!document.fullscreenElement && mapExpanded) toggleMapExpanded(mapExpanded);
});

function renderMap() {
  const wrap = $("mapClusters");
  const perNet = mapClusters();
  const whole = wholeActive(perNet);
  document.querySelectorAll(".map-style button[data-style]").forEach(b => b.classList.toggle("active", b.dataset.style === mapStyle));
  $("mapScope").hidden = !(network === "all" && perNet.length > 1);
  document.querySelectorAll("#mapScope button").forEach(b => b.classList.toggle("active", (b.dataset.scope === "whole") === whole));
  const netLegend = $("mapNetLegend");
  netLegend.hidden = !whole;
  if (mapDragging) return;   // a poll mustn't redraw the map out from under a drag
  const clusters = whole ? [wholeCluster(perNet)] : gatewayGroups(perNet);
  if (whole) {
    const colors = clusters[0].netColors;
    const html = Object.entries(colors).map(([n, col]) =>
      `<span><svg viewBox="-8 -8 16 16"><circle r="5" fill="${col}"/></svg>${esc(n)}</span>`).join("");
    if (netLegend.innerHTML !== html) netLegend.innerHTML = html;
  }
  wrap.classList.toggle("single", clusters.length === 1 || mapStyle === "topology");
  wrap.classList.toggle("topo", mapStyle === "topology");
  const sig = mapSignature(clusters);
  if (sig !== mapSig) {
    mapSig = sig;
    wrap.innerHTML = "";
    clusters.forEach(c => wrap.appendChild(buildClusterCard(c)));
    $("mapEmpty").hidden = clusters.length > 0;
    // A map that was full screen and has gone (the view changed) lets go of it.
    if (mapExpanded && !clusters.some(c => c.subnet === mapExpanded)) toggleMapExpanded(mapExpanded);
    sizeTopoFrames(wrap);
  }
  // What changes between polls without moving anything: status, tooltips,
  // the dashed spoke of an offline device, and the counts.
  const byId = new Map(hosts.map(h => [String(h.id), h]));
  wrap.querySelectorAll(".node[data-id]").forEach(g => {
    const h = byId.get(g.getAttribute("data-id"));
    if (!h) return;
    const swId = g.getAttribute("data-sw");
    const sw = swId ? switches.find(s => String(s.id) === swId) : null;
    const role = sw ? "sw" : g.classList.contains("gw") ? "gw" : g.classList.contains("self") ? "self" : "dev";
    const cls = mapNodeClass(h, role), lv = role === "dev" ? sigLevel(h) : "";
    g.setAttribute("class", g.classList.contains("tnode") ? "tnode " + cls + (lv ? " sig-" + lv : "") : cls);
    if (g.classList.contains("tnode") && role === "dev") sigGlyph(g, lv);
    g.querySelector("title").textContent = sw ? switchTitle(sw)
      : role === "dev" && g.classList.contains("tnode") ? topoDevTip(h)
      : role === "self" && g.getAttribute("data-key") !== "self" && g.classList.contains("tnode") ? mapTitle(h, role) + "\n" + TOPO_DEV_HINT
      : mapTitle(h, role);
  });
  wrap.querySelectorAll(".spoke[data-id], .pkt[data-id]").forEach(l => {
    const h = byId.get(l.getAttribute("data-id"));
    if (h) { l.classList.toggle("off", !h.online); if (l.classList.contains("spoke")) sigApply(l, h); }
  });
  clusters.forEach(c => {
    const card = wrap.querySelector(`.map-card[data-subnet="${CSS.escape(c.subnet)}"]`);
    if (!card) return;
    const on = c.members.filter(h => h.online).length;
    card.querySelector(".map-cnt").textContent = `${on} of ${c.members.length} online`;
    const hubTxt = card.querySelector(".hub-txt");
    if (hubTxt) hubTxt.textContent = `${on}/${c.members.length}`;
  });
}

// From the map: show the device in the list. If a filter would hide it, clear
// the filters rather than land on a list it isn't in.
function jumpToHost(id) {
  showView("devices");
  const find = () => document.querySelector(`tr[data-id="${id}"]`);
  let row = find();
  if (!row) {
    network = "all"; filter = "all"; guessFilter = null; query = "";
    $("search").value = "";
    document.querySelectorAll(".tab").forEach(x => x.classList.toggle("active", x.dataset.filter === "all"));
    render();
    row = find();
  }
  if (!row) return;
  row.scrollIntoView({ behavior: "smooth", block: "center" });
  row.style.background = "var(--panel-2)";
  setTimeout(() => { row.style.background = ""; }, 1200);
}
