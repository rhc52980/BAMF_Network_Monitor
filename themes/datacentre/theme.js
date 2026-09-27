// Data Centre: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// The room: rows of racks with their lights on, cable tray overhead, fibre
// running between them, a cooling unit breathing at the side, and a crash cart
// with a monitor on it. Drawn rather than loaded, and every light is its own
// element so a scan can light the lot at once.
const DC_RACK = (x, y, w, h, units, seed) => {
  let out = `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="4" fill="#111923" stroke="#28384a" stroke-width="3"/>` +
    `<rect x="${x + 4}" y="${y + 4}" width="${w - 8}" height="${h - 8}" rx="2" fill="#0b1219"/>`;
  const uh = (h - 16) / units;
  for (let i = 0; i < units; i++) {
    const uy = y + 8 + i * uh, kind = (seed + i) % 7;
    out += `<rect x="${x + 8}" y="${uy + 1}" width="${w - 16}" height="${uh - 3}" rx="1.5" fill="${kind === 3 ? "#16202b" : "#18232f"}" stroke="#223040"/>`;
    if (kind === 3) {
      // A patch panel: a row of ports.
      for (let k = 0; k < 12; k++)
        out += `<rect x="${x + 14 + k * ((w - 30) / 12)}" y="${uy + uh * .3}" width="${(w - 30) / 12 - 2}" height="${uh * .38}" fill="#0a1017" stroke="#2b3a4a" stroke-width=".6"/>`;
    } else if (kind === 5) {
      // A switch: a bank of link lights.
      for (let k = 0; k < 10; k++)
        out += `<rect class="dc-led" x="${x + 14 + k * ((w - 30) / 10)}" y="${uy + uh * .35}" width="3" height="${Math.max(2, uh * .22)}" fill="${k % 3 ? "#35e08a" : "#35c8e0"}" color="${k % 3 ? "#35e08a" : "#35c8e0"}" style="animation-delay:-${((seed + i + k) % 9) * .21}s"/>`;
    } else {
      // A server: a drive bay and two lights.
      out += `<rect x="${x + 13}" y="${uy + uh * .25}" width="${(w - 26) * .55}" height="${uh * .5}" fill="#101a24" stroke="#223040" stroke-width=".8"/>`;
      out += `<circle class="dc-led" cx="${x + w - 18}" cy="${uy + uh * .5}" r="${Math.min(2.6, uh * .16)}" fill="#35e08a" color="#35e08a" style="animation-delay:-${((seed + i) % 7) * .29}s"/>`;
      out += `<circle class="dc-led" cx="${x + w - 11}" cy="${uy + uh * .5}" r="${Math.min(2.6, uh * .16)}" fill="${(seed + i) % 5 ? "#35c8e0" : "#ffb020"}" color="${(seed + i) % 5 ? "#35c8e0" : "#ffb020"}" style="animation-delay:-${((seed + i) % 11) * .17}s"/>`;
    }
  }
  return out;
};

const DC_SCENE = () => {
  const racks = [[80, 250, 150, 430, 12, 1], [250, 230, 150, 450, 13, 4], [420, 250, 150, 430, 11, 2],
                 [830, 240, 150, 440, 12, 6], [1000, 260, 150, 420, 12, 3], [1170, 235, 150, 445, 13, 5]];
  let out = `<svg viewBox="0 0 1400 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
    <rect x="0" y="0" width="1400" height="900" fill="none"/>
    <!-- raised floor, in perspective -->
    <g stroke="#182432" stroke-width="2" opacity=".8">
      ${Array.from({ length: 11 }, (_, i) => `<path d="M${-200 + i * 180} 900L${430 + i * 60} 680"/>`).join("")}
      ${[700, 760, 830, 900].map((y, i) => `<path d="M0 ${y}h1400" opacity="${.5 - i * .1}"/>`).join("")}
    </g>
    <!-- cable tray across the ceiling, with bundles dropping into the rows -->
    <rect x="0" y="120" width="1400" height="16" fill="#1b2634"/>
    <rect x="0" y="136" width="1400" height="5" fill="#0e151d"/>
    ${[150, 320, 490, 900, 1070, 1240].map((x, i) => `<path d="M${x} 141c0 40 ${i % 2 ? 26 : -26} 60 ${i % 2 ? 26 : -26} 100" fill="none" stroke="${["#2f6fb3", "#b34a2f", "#2fb37a", "#b3992f"][i % 4]}" stroke-width="5" opacity=".75"/>`).join("")}
    <!-- the racks -->
    ${racks.map(r => DC_RACK(...r)).join("")}
    <!-- fibre running between the rows, with light chasing along it -->
    <path d="M230 470C420 520 640 560 700 640C760 560 980 520 1170 470" fill="none" stroke="#12202c" stroke-width="7"/>
    <path class="dc-fibre" d="M230 470C420 520 640 560 700 640C760 560 980 520 1170 470" stroke="#35c8e0" stroke-width="4"/>
    <path class="dc-fibre" style="animation-delay:-1.7s" d="M230 470C420 520 640 560 700 640C760 560 980 520 1170 470" stroke="#8ef0c4" stroke-width="3"/>
    <!-- the cooling unit, breathing -->
    <rect x="620" y="300" width="160" height="330" rx="6" fill="#111923" stroke="#28384a" stroke-width="3"/>
    <circle cx="700" cy="380" r="46" fill="#0b1219" stroke="#22303f" stroke-width="3"/>
    <g class="dc-fan"><path d="M700 344c14 10 14 26 0 36s-14-26 0-36z" fill="#2b3a4a"/>
      <path d="M736 380c-10 14-26 14-36 0s26-14 36 0z" fill="#2b3a4a"/>
      <path d="M700 416c-14-10-14-26 0-36s14 26 0 36z" fill="#2b3a4a"/>
      <path d="M664 380c10-14 26-14 36 0s-26 14-36 0z" fill="#2b3a4a"/>
      <circle cx="700" cy="380" r="7" fill="#35c8e0"/></g>
    <circle cx="700" cy="520" r="34" fill="#0b1219" stroke="#22303f" stroke-width="3"/>
    <g class="dc-fan slow"><path d="M700 494c10 7 10 19 0 26s-10-19 0-26z" fill="#2b3a4a"/>
      <path d="M726 520c-7 10-19 10-26 0s19-10 26 0z" fill="#2b3a4a"/>
      <path d="M700 546c-10-7-10-19 0-26s10 19 0 26z" fill="#2b3a4a"/>
      <path d="M674 520c7-10 19-10 26 0s-19 10-26 0z" fill="#2b3a4a"/></g>
    <!-- a crash cart with a monitor still logged in -->
    <rect x="530" y="660" width="120" height="70" rx="4" fill="#101a24" stroke="#28384a" stroke-width="2"/>
    <rect x="540" y="670" width="100" height="50" rx="2" fill="#07121a"/>
    <g opacity=".85">${Array.from({ length: 6 }, (_, i) => `<rect class="dc-led" x="546" y="${676 + i * 8}" width="${20 + (i * 17) % 70}" height="3" fill="#35e08a" color="#35e08a" style="animation-delay:-${i * .4}s"/>`).join("")}</g>
    <circle cx="545" cy="742" r="7" fill="#1b2634"/><circle cx="635" cy="742" r="7" fill="#1b2634"/>
  </svg>`;
  return out;
};

// The room's effects layer, on a canvas over the drawing and behind the page.
// In the open band under the header a hologram turns over a projector: a
// wireframe globe with a point on it for every device, green, amber or red by
// the same rule as the ports, with packets arcing between them, and a HUD
// beside it. Light runs both ways along the cable tray in the ceiling, and a
// maintenance shuttle patrols the tray's rail; when a scan lands it stops and
// sweeps the racks with its laser. The readouts on the patch panel are drawn
// here too, so the whole room runs off one frame loop that stops with the tab.
function dcEffects(bg, panel, live, state, calm, heldIds = () => new Set()) {
  const cv = document.createElement("canvas");
  cv.className = "dc-fx";
  bg.appendChild(cv);
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  const ctx = cv.getContext("2d");
  ctx.scale(dpr, dpr);
  const S = Math.max(.75, Math.min(1.2, W / 1400));
  const HB = document.querySelector("header")?.getBoundingClientRect().bottom || 58;
  // Where the drawing's cable tray lands on screen: the SVG is sliced to cover
  // the window, so scale and offset the same way.
  const k = Math.max(W / 1400, H / 900), oy = (H - 900 * k) / 2;
  const trayY = oy + 128 * k;
  // The hologram, and the HUD to its left.
  const gr = 30 * S, gx = W - 96 * S, gy = HB + 38 * S;
  const TOP = Math.max(gy + gr + 30 * S, trayY + 40 * k);   // the band redrawn every frame
  // The tray runs behind the "Hosts on segment" label, so the light on it
  // starts after the label's last word rather than striking through it, and
  // the shuttle patrols the open stretch between the label and the HUD.
  const lab = document.querySelector(".portstrip-label")?.lastElementChild;
  const labelEnd = lab ? lab.getBoundingClientRect().right + 16 : 0;
  const hudLeft = gx - gr * 1.6 - 12 * S - 320 * S;
  const lo = Math.max(40, labelEnd + 40), hi = hudLeft - lo > 160 ? hudLeft - 30 : W - 40;

  // The panel's readouts.
  const lcd = document.createElement("div");
  lcd.className = "dc-lcd";
  const scope = document.createElement("canvas");
  scope.className = "dc-scope";
  scope.width = 188 * dpr; scope.height = 30 * dpr;
  const sc = scope.getContext("2d");
  sc.scale(dpr, dpr);
  panel.querySelector(".bar").append(lcd, scope);
  const trace = Array.from({ length: 95 }, () => .3);

  const packets = Array.from({ length: 16 }, (_, i) => ({ x: Math.random() * W, dir: i % 2 ? 1 : -1, v: (120 + Math.random() * 260) * k,
    lane: (i % 3) * 4 * k, col: ["#35c8e0", "#8ef0c4", "#b18cff"][i % 3] }));
  const shuttle = { x: (lo + hi) / 2, dir: 1, v: 38 * S, laser: 0 };
  const arcs = [];
  let yaw = .6, surge = 0, alarm = 0, lastLcd = 0, temp = 21.4, intrusion = 0;

  // Devices spread over the globe on a golden-angle spiral, so they never bunch.
  const nodes = () => {
    const list = live().slice(0, 60), n = Math.max(1, list.length);
    return list.map((h, i) => {
      const y = 1 - (i + .5) / n * 2, r = Math.sqrt(1 - y * y), a = i * 2.39996;
      return { x: Math.cos(a) * r, y, z: Math.sin(a) * r, st: state(h), id: h.id };
    });
  };
  let pts = nodes();
  const tilt = .38, ct = Math.cos(tilt), st = Math.sin(tilt);
  const proj = (x, y, z) => {
    const cy = Math.cos(yaw), sy = Math.sin(yaw);
    const x1 = x * cy + z * sy, z1 = -x * sy + z * cy;
    const y2 = y * ct - z1 * st, z2 = y * st + z1 * ct;
    return [gx + x1 * gr, gy - y2 * gr, z2];
  };
  const COL = { up: "53,224,138", un: "255,176,32", down: "255,77,77" };

  function drawGlobe(t) {
    // The projector, and the light it throws up.
    const cone = ctx.createLinearGradient(0, gy + gr + 14 * S, 0, gy - gr);
    cone.addColorStop(0, "rgba(53,200,224,.28)"); cone.addColorStop(1, "rgba(53,200,224,0)");
    ctx.fillStyle = cone;
    ctx.beginPath(); ctx.moveTo(gx - 9 * S, gy + gr + 14 * S); ctx.lineTo(gx + 9 * S, gy + gr + 14 * S);
    ctx.lineTo(gx + gr * 1.25, gy - gr * .4); ctx.lineTo(gx - gr * 1.25, gy - gr * .4); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#16222e"; ctx.strokeStyle = "#35c8e0"; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.ellipse(gx, gy + gr + 16 * S, 18 * S, 4 * S, 0, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
    const halo = ctx.createRadialGradient(gx, gy, gr * .2, gx, gy, gr * 1.3);
    halo.addColorStop(0, `rgba(53,200,224,${.1 + surge * .15})`); halo.addColorStop(1, "rgba(53,200,224,0)");
    ctx.fillStyle = halo; ctx.fillRect(gx - gr * 1.4, gy - gr * 1.4, gr * 2.8, gr * 2.8);
    // The wireframe: meridians and parallels, the back half fainter.
    ctx.lineWidth = 1;
    const line = f => {
      let prev = null;
      for (let i = 0; i <= 32; i++) {
        const q = proj(...f(i / 32 * Math.PI * 2));
        if (prev) {
          ctx.strokeStyle = `rgba(110,230,250,${prev[2] > 0 && q[2] > 0 ? .55 : .14})`;
          ctx.beginPath(); ctx.moveTo(prev[0], prev[1]); ctx.lineTo(q[0], q[1]); ctx.stroke();
        }
        prev = q;
      }
    };
    for (let m = 0; m < 6; m++) { const a = m / 6 * Math.PI; line(u => [Math.cos(a) * Math.sin(u), Math.cos(u), Math.sin(a) * Math.sin(u)]); }
    for (const lat of [-.6, -.3, 0, .3, .6]) { const r = Math.cos(lat), y = Math.sin(lat); line(u => [Math.cos(u) * r, y, Math.sin(u) * r]); }
    // Packets arcing between devices, a little above the surface.
    for (const a of arcs) {
      const p0 = pts[a.from], p1 = pts[a.to];
      if (!p0 || !p1) continue;
      ctx.strokeStyle = `rgba(142,246,255,${.5 * (1 - a.t)})`; ctx.lineWidth = 1.2;
      ctx.beginPath();
      let head = null;
      for (let i = 0; i <= 12; i++) {
        const f = i / 12 * Math.min(1, a.t * 1.6);
        const x = p0.x + (p1.x - p0.x) * f, y = p0.y + (p1.y - p0.y) * f, z = p0.z + (p1.z - p0.z) * f;
        const m = Math.hypot(x, y, z) || 1, lift = (1 + Math.sin(f * Math.PI) * .25) / m;
        head = proj(x * lift, y * lift, z * lift);
        i ? ctx.lineTo(head[0], head[1]) : ctx.moveTo(head[0], head[1]);
      }
      ctx.stroke();
      if (head && a.t < .62) { ctx.fillStyle = "#e6fdff"; ctx.beginPath(); ctx.arc(head[0], head[1], 1.8, 0, Math.PI * 2); ctx.fill(); }
    }
    // The devices, back ones first.
    const held = heldIds();
    const drawn = pts.map(p => ({ q: proj(p.x, p.y, p.z), st: p.st, held: held.has(p.id) })).sort((a, b) => a.q[2] - b.q[2]);
    for (const d of drawn) {
      // An intruder: red, with a reticle closing on it.
      if (d.held) {
        const r = 7 + 3 * Math.sin(t / 220);
        ctx.strokeStyle = "rgba(255,77,61,.95)"; ctx.lineWidth = 1.4;
        ctx.beginPath(); ctx.arc(d.q[0], d.q[1], r, 0, Math.PI * 2); ctx.stroke();
        for (let q = 0; q < 4; q++) { const a = q * Math.PI / 2; ctx.beginPath(); ctx.moveTo(d.q[0] + Math.cos(a) * (r + 2), d.q[1] + Math.sin(a) * (r + 2)); ctx.lineTo(d.q[0] + Math.cos(a) * (r + 7), d.q[1] + Math.sin(a) * (r + 7)); ctx.stroke(); }
        ctx.fillStyle = "#ff4d3d"; ctx.beginPath(); ctx.arc(d.q[0], d.q[1], 3, 0, Math.PI * 2); ctx.fill();
        continue;
      }
      const front = d.q[2] > 0, c = COL[d.st], blink = d.st === "down" ? .5 + .5 * Math.sin(t / 160) : 1;
      ctx.fillStyle = `rgba(${c},${(front ? .95 : .35) * blink})`;
      ctx.beginPath(); ctx.arc(d.q[0], d.q[1], front ? 2.6 : 1.7, 0, Math.PI * 2); ctx.fill();
      if (front) { ctx.fillStyle = `rgba(${c},${.18 * blink})`; ctx.beginPath(); ctx.arc(d.q[0], d.q[1], 6, 0, Math.PI * 2); ctx.fill(); }
    }
    // A ring orbiting the equator, with a tick running round it.
    const rx = gr * 1.35, ry = gr * .32, rot = -.12, ta = t / 900;
    ctx.strokeStyle = "rgba(142,246,255,.35)";
    ctx.beginPath(); ctx.ellipse(gx, gy, rx, ry, rot, 0, Math.PI * 2); ctx.stroke();
    const ex = Math.cos(ta) * rx, ey = Math.sin(ta) * ry;
    ctx.fillStyle = "#bdf6ff";
    ctx.beginPath(); ctx.arc(gx + ex * Math.cos(rot) - ey * Math.sin(rot), gy + ex * Math.sin(rot) + ey * Math.cos(rot), 2, 0, Math.PI * 2); ctx.fill();
  }

  function drawHud(t) {
    const list = live(), up = list.filter(h => h.online).length, down = list.length - up;
    const x = gx - gr * 1.6 - 12 * S, y = gy - 16 * S;
    ctx.textAlign = "right"; ctx.textBaseline = "alphabetic";
    ctx.font = `600 ${9 * S}px "IBM Plex Mono", monospace`;
    ctx.fillStyle = "rgba(126,143,163,.9)";
    ctx.fillText(`HALL A · ROWS 1–6 · ${new Date().toTimeString().slice(0, 8)}`, x, y);
    ctx.font = `600 ${15 * S}px "IBM Plex Mono", monospace`;
    ctx.fillStyle = alarm > 0 ? `rgba(255,120,110,${.6 + .4 * Math.sin(t / 150)})` : "#8ef6ff";
    ctx.fillText(`${up}/${list.length} LINKED`, x, y + 18 * S);
    ctx.font = `600 ${9 * S}px "IBM Plex Mono", monospace`;
    const q = heldIds().size;
    ctx.fillStyle = intrusion > 0 ? `rgba(255,120,110,${.6 + .4 * Math.sin(t / 120)})` : q ? "rgba(255,140,130,.95)" : down ? "rgba(255,160,150,.9)" : "rgba(142,240,196,.85)";
    ctx.fillText(intrusion > 0 ? "INTRUSION DETECTED · LOCKDOWN" : q ? `${q} QUARANTINED · VLAN 666` : down ? `${down} DARK · CHECK PATCH` : "ALL PATHS NOMINAL", x, y + 31 * S);
    // A throughput trace to the left of the words.
    const w = 150 * S, x0 = x - 170 * S - w, yb = y + 28 * S, hh = 26 * S;
    ctx.strokeStyle = "rgba(53,200,224,.18)"; ctx.lineWidth = 1;
    for (let i = 0; i <= 3; i++) { ctx.beginPath(); ctx.moveTo(x0, yb - i * hh / 3); ctx.lineTo(x0 + w, yb - i * hh / 3); ctx.stroke(); }
    ctx.strokeStyle = "rgba(142,246,255,.8)"; ctx.lineWidth = 1.4;
    ctx.beginPath();
    for (let i = 0; i < trace.length; i++) { const px = x0 + i / (trace.length - 1) * w, py = yb - trace[i] * hh; i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); }
    ctx.stroke();
    ctx.textAlign = "left"; ctx.font = `600 ${8 * S}px "IBM Plex Mono", monospace`; ctx.fillStyle = "rgba(126,143,163,.85)";
    ctx.fillText("CORE UPLINK", x0, yb - hh - 4 * S);
  }

  function drawTray(t) {
    // Light running along the tray, both ways, as short bright dashes.
    for (const p of packets) {
      const y = trayY - 4 * k + p.lane, len = 22 * k;
      if (Math.min(p.x, p.x - p.dir * len) < labelEnd) continue;
      ctx.strokeStyle = p.col; ctx.globalAlpha = .25; ctx.lineWidth = 5 * k;
      ctx.beginPath(); ctx.moveTo(p.x, y); ctx.lineTo(p.x - p.dir * len, y); ctx.stroke();
      ctx.globalAlpha = .95; ctx.lineWidth = 1.6 * k;
      ctx.beginPath(); ctx.moveTo(p.x, y); ctx.lineTo(p.x - p.dir * len, y); ctx.stroke();
    }
    ctx.globalAlpha = 1;
    // The shuttle, running on top of the tray on two wheels.
    const sx = shuttle.x, sy = trayY - 8 * k - 17 * k;
    ctx.fillStyle = "#0e151d";
    ctx.beginPath(); ctx.arc(sx - 11 * k, trayY - 11 * k, 3 * k, 0, Math.PI * 2); ctx.arc(sx + 11 * k, trayY - 11 * k, 3 * k, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = "#1b2634"; ctx.strokeStyle = "#35c8e0"; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.roundRect(sx - 20 * k, sy, 40 * k, 13 * k, 4 * k); ctx.fill(); ctx.stroke();
    ctx.fillStyle = Math.sin(t / 240) > 0 ? "#35e08a" : "#12402a";
    ctx.beginPath(); ctx.arc(sx - shuttle.dir * 12 * k, sy + 6.5 * k, 2 * k, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = "rgba(53,200,224,.5)";
    ctx.fillRect(sx - 4 * k, sy + 4 * k, 12 * k, 3 * k);
    const lx = sx + shuttle.dir * 20 * k, ly = sy + 9 * k;
    ctx.fillStyle = shuttle.laser > 0 ? "#ff5a5a" : "#35c8e0";
    ctx.beginPath(); ctx.arc(lx, ly, 3 * k, 0, Math.PI * 2); ctx.fill();
    // The laser: a fan swept across the racks below.
    if (shuttle.laser > 0) {
      const f = 1 - shuttle.laser;
      const a = -.7 + f * 1.4, reach = H - ly, spread = .1;
      const g = ctx.createLinearGradient(0, ly, 0, H);
      g.addColorStop(0, "rgba(255,70,70,.35)"); g.addColorStop(1, "rgba(255,70,70,0)");
      ctx.fillStyle = g;
      ctx.beginPath(); ctx.moveTo(lx, ly); ctx.lineTo(lx + Math.tan(a - spread) * reach, H); ctx.lineTo(lx + Math.tan(a + spread) * reach, H); ctx.closePath(); ctx.fill();
      ctx.strokeStyle = "rgba(255,120,120,.9)"; ctx.lineWidth = 1.4;
      ctx.beginPath(); ctx.moveTo(lx, ly); ctx.lineTo(lx + Math.tan(a) * reach, H); ctx.stroke();
    }
  }

  function drawPanel() {
    const list = live(), up = list.filter(h => h.online).length, down = list.length - up;
    lcd.innerHTML = `<b>DC-01</b> INLET <span class="${temp > 23 ? "hot" : ""}">${temp.toFixed(1)}°C</span><br>` +
      `${(1.1 + up * .14).toFixed(1)} kW · PUE 1.2${down ? ` · ${down} DARK` : ""}`;
  }
  function drawScope() {
    sc.clearRect(0, 0, 188, 30);
    sc.strokeStyle = "rgba(53,200,224,.2)"; sc.lineWidth = 1;
    for (let x = 0; x < 188; x += 23.5) { sc.beginPath(); sc.moveTo(x, 0); sc.lineTo(x, 30); sc.stroke(); }
    sc.strokeStyle = "#7fe9f7"; sc.lineWidth = 1.3;
    sc.beginPath();
    for (let i = 0; i < trace.length; i++) { const x = i * 2, y = 27 - trace[i] * 24; i ? sc.lineTo(x, y) : sc.moveTo(x, y); }
    sc.stroke();
  }

  function step(dt, now) {
    yaw += dt * .45;
    for (const p of packets) { p.x += p.dir * p.v * dt * (1 + surge * 2); if (p.x > W + 40) p.x = -40; if (p.x < -40) p.x = W + 40; }
    if (shuttle.laser > 0) shuttle.laser = Math.max(0, shuttle.laser - dt / 1.6);
    else {
      shuttle.x += shuttle.dir * shuttle.v * dt;
      if (shuttle.x > hi) shuttle.dir = -1;
      if (shuttle.x < lo) shuttle.dir = 1;
    }
    for (const a of arcs) a.t += dt * .8;
    while (arcs.length && arcs[0].t >= 1) arcs.shift();
    if (pts.length > 1 && Math.random() < dt * (1.4 + surge * 6)) {
      const from = Math.floor(Math.random() * pts.length);
      arcs.push({ from, to: (from + 1 + Math.floor(Math.random() * (pts.length - 1))) % pts.length, t: 0 });
    }
    surge = Math.max(0, surge - dt * .5);
    alarm = Math.max(0, alarm - dt);
    intrusion = Math.max(0, intrusion - dt);
    // While the alarm's on, the shuttle keeps sweeping the racks.
    if (intrusion > 0 && shuttle.laser <= 0) shuttle.laser = 1;
    trace.shift();
    trace.push(Math.min(1, .25 + surge * .55 + Math.random() * .22 + Math.sin(now / 700) * .08));
    if (now - lastLcd > 2000) { lastLcd = now; temp = Math.max(20.2, Math.min(24.5, temp + (Math.random() - .5) * .3 + surge * .4)); drawPanel(); }
  }
  // Only the top band changes, except while the laser is sweeping and the
  // frame after it stops.
  let wide = false;
  function draw(now) {
    const t = now || 0;
    if (shuttle.laser > 0 || wide) { ctx.clearRect(0, 0, W, H); wide = shuttle.laser > 0; }
    else ctx.clearRect(0, 0, W, TOP);
    drawTray(t);
    drawGlobe(t);
    drawHud(t);
    drawScope();
  }

  drawPanel();
  if (calm) { draw(0); return { repaint() { pts = nodes(); ctx.clearRect(0, 0, W, H); draw(0); drawPanel(); } }; }
  let raf = 0, last = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000);
    last = now;
    step(dt, now);
    draw(now);
  };
  const run = () => { if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); } };
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };
  const vis = () => document.hidden ? stop() : run();
  document.addEventListener("visibilitychange", vis);
  run();
  festiveStops.push(() => { stop(); document.removeEventListener("visibilitychange", vis); });
  return {
    repaint() { pts = nodes(); drawPanel(); },
    scan() { surge = 1; shuttle.laser = 1; },
    alarm() { alarm = 4; },
    intrusion() { intrusion = 9; alarm = 9; surge = 1; shuttle.laser = 1; },
  };
}

// Data Centre: the room your network would have if it were racked and
// patched. Every device gets a port on the panel along the bottom, lit by
// whether it's up; the racks flash through a sweep when a scan lands.
// A new device nobody has marked known is an intruder on the network. The cage
// alarm goes: every light in the racks turns red, a red beacon sweeps the
// room, the HUD reads INTRUSION DETECTED, the hologram puts a reticle on it and
// the shuttle sweeps the racks with its laser. Then its port on the patch
// panel is caged in red, quarantined, tagged, until the device is marked
// known; then it's linked, sliding in as a blade with its name on it.
function buildDataCentre(root) {
  const calm = calmMotion();
  let hum = null;
  const humOn = on => { if (on && !hum && !document.hidden) hum = roomHum(); if (!on && hum) { hum.stop(); hum = null; } };
  const soundOn = themeSoundButton("bamf-dc-sound", "Room sounds on: click to mute",
    "Room sounds off: click for the fans, the link chirp and the alarm", on => { humOn(on); if (on) linkChirp(); });
  if (soundOn()) {
    const wake = () => humOn(soundOn());
    document.addEventListener("pointerdown", wake, { once: true });
    festiveStops.push(() => document.removeEventListener("pointerdown", wake));
  }
  const vis = () => humOn(soundOn() && !document.hidden);
  document.addEventListener("visibilitychange", vis);
  festiveStops.push(() => { document.removeEventListener("visibilitychange", vis); humOn(false); });

  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg dc-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = DC_SCENE();
  document.body.prepend(bg);

  // The link panel in the header.
  const link = document.createElement("div");
  link.className = "dc-link";
  link.innerHTML = `<span class="ports"></span><span class="n"></span>`;
  $("themeToggle")?.before(link);
  festiveStops.push(() => link.remove());

  // The patch panel: a port per device, in the order the table shows them.
  const panel = document.createElement("div");
  panel.className = "dc-panel";
  panel.innerHTML = `<div class="bar"></div><div class="dc-ports"></div>`;
  root.appendChild(panel);
  const ports = panel.querySelector(".dc-ports");
  // The page gets the panel's height back at the bottom, so the last rows
  // scroll clear of it.
  const room = document.createElement("div");
  room.style.height = "48px";
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());

  let blade = () => {};
  const live = () => hosts.filter(h => !h.ignored && !h.forgotten && !h.remote)
    .sort((a, b) => nameOrIp(a).localeCompare(nameOrIp(b)));
  const state = h => !h.online ? "down" : h.known ? "up" : "un";
  const paint = () => {
    const list = live().slice(0, 28);
    // The header panel: one light per device, up to a rack's worth.
    link.querySelector(".ports").innerHTML = list.slice(0, 16).map(h => `<i class="${state(h)}"></i>`).join("");
    const up = list.filter(h => h.online).length;
    link.querySelector(".n").textContent = `LINK ${up}/${list.length}`;
    link.title = `${up} of ${list.length} devices answering`;
    // The patch panel along the bottom.
    if (ports.children.length !== list.length) {
      ports.innerHTML = list.map(() => `<span class="dc-port"><i></i><i class="act"></i><span class="tip"></span></span>`).join("");
    }
    list.forEach((h, i) => {
      const el = ports.children[i];
      if (!el) return;
      el.className = "dc-port " + state(h);
      el.title = `${nameOrIp(h)} · ${h.ip}`;
      el.querySelector(".tip").textContent = nameOrIp(h);
      el.dataset.id = h.id;
    });
  };
  // The light along the header's edge.
  const bar = document.createElement("div");
  bar.className = "dc-scanbar";
  bar.style.top = `${(document.querySelector("header")?.getBoundingClientRect().bottom || 58) - 2}px`;
  bar.innerHTML = "<i></i>";
  root.appendChild(bar);
  // ---- intruders: quarantined ports ----
  const watchIn = intruderWatch();
  const quar = new Map();               // id -> { h, tag, alarm, gone }
  const heldIds = () => new Set([...quar.values()].filter(q => !q.gone).map(q => q.h.id));
  const fx = dcEffects(bg, panel, live, state, calm, heldIds);
  // Each held device's tag stands over its port; one whose port isn't on the
  // panel (it shows the first 28) stands at the panel's right-hand end.
  const placeTags = () => {
    const list = [...quar.values()].filter(q => !q.gone).sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
    room.style.height = list.length ? "130px" : "48px";
    list.forEach((q, k) => {
      const port = ports.querySelector(`.dc-port[data-id="${q.h.id}"]`);
      const pr = panel.getBoundingClientRect(), r = port?.getBoundingClientRect();
      const x = r ? r.left + r.width / 2 - pr.left : pr.width - 150;
      q.tag.style.left = Math.max(110, Math.min(pr.width - 110, x)) + "px";
      q.tag.style.bottom = `${50 + k * 54}px`;
      q.tag.hidden = k > 1;
    });
  };
  const markPorts = () => {
    const ids = heldIds();
    for (const el of ports.children) el.classList.toggle("quar", ids.has(Number(el.dataset.id)) || ids.has(el.dataset.id));
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!quar.has(h.id)) {
      const tag = intruderTagEl(h, "held");
      tag.classList.add("dc-qtag");
      panel.appendChild(tag);
      quar.set(h.id, { h, tag, alarm: 0, gone: false });
    }
    for (const h of held) { const q = quar.get(h.id); if (q && !q.gone) { q.h = h; if (!q.alarm || Date.now() - q.alarm > 9000) intruderTagEl(h, "held", q.tag); } }
    for (const h of cleared) {
      const q = quar.get(h.id); if (!q || q.gone) continue;
      q.gone = true;
      const done = () => { q.tag.remove(); quar.delete(h.id); markPorts(); placeTags(); };
      if (calm || document.hidden) { done(); continue; }
      // Released from quarantine: linked, and in it slides as a blade.
      intruderTagEl(q.h, "cleared", q.tag);
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) blade(h); }, 2600));
    }
    markPorts();
    placeTags();
  }
  // The alarm: the whole room goes to lockdown for a few seconds.
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    if (!quar.has(h.id)) { const tag = intruderTagEl(h, "held"); tag.classList.add("dc-qtag"); panel.appendChild(tag); quar.set(h.id, { h, tag, alarm: 0, gone: false }); markPorts(); placeTags(); }
    if (calm || document.hidden) { fx.repaint(); return; }
    const q = quar.get(h.id);
    q.alarm = Date.now();
    intruderTagEl(h, "alarm", q.tag);
    placeTags();
    bg.classList.add("dc-intrusion"); link.classList.add("alarm"); bar.classList.add("alarm");
    festiveTimers.push(setTimeout(() => { bg.classList.remove("dc-intrusion"); link.classList.remove("alarm"); bar.classList.remove("alarm"); }, 9000));
    festiveTimers.push(setTimeout(() => { if (!q.gone) intruderTagEl(q.h, "held", q.tag); }, 9000));
    fx.intrusion();
    if (soundOn()) { linkAlarm(); festiveTimers.push(setTimeout(linkAlarm, 700)); festiveTimers.push(setTimeout(linkAlarm, 1400)); }
  };

  // The panel is repainted whenever the table is, which is also how it fills
  // in once the first scan lands. It has to be set before reduced motion
  // takes the early way out, or the ports would stay empty.
  festiveHooks.rendered = () => { paint(); fx.repaint(); syncIntruders(); };
  paint();
  syncIntruders();
  if (calm) return; // Reduced motion: the room is lit, nothing moves.

  // A scan: every light in the room comes on, and the fibre runs fast.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    bg.classList.add("dc-sweep");
    festiveTimers.push(setTimeout(() => bg.classList.remove("dc-sweep"), 900));
    fx.scan();
    if (soundOn()) linkChirp(.5);
  };
  // A device slides in as a blade, with its name on the faceplate: once it's
  // been marked known, since until then it's in quarantine.
  blade = h => {
    if (document.hidden) return;
    const el = document.createElement("div");
    el.className = "dc-blade";
    el.innerHTML = `<b></b>${esc(h ? nameOrIp(h) : "new device")} linked`;
    el.addEventListener("animationend", () => el.remove());
    panel.appendChild(el);
    if (soundOn()) linkChirp();
  };
  // Something drops: its port goes red and says its name for a moment.
  festiveHooks.netChange = (off, back) => {
    paint();
    if (document.hidden) return;
    for (const id of off) {
      const el = ports.querySelector(`.dc-port[data-id="${id}"]`);
      if (!el) continue;
      el.classList.add("said");
      festiveTimers.push(setTimeout(() => el.classList.remove("said"), 6000));
    }
    if (off.length) {
      fx.alarm();
      bar.classList.add("alarm");
      festiveTimers.push(setTimeout(() => bar.classList.remove("alarm"), 4000));
    }
    if (off.length && soundOn()) linkAlarm();
    if (back.length && soundOn()) linkChirp(.7);
  };
}

// The room: fans and the hum of a rack, quieter than the factory.
function roomHum() {
  const ctx = waterCtx(); if (!ctx) return null;
  const src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, 4, false); src.loop = true;
  f.type = "bandpass"; f.frequency.value = 480; f.Q.value = .7;
  g.gain.setValueAtTime(0, ctx.currentTime); g.gain.linearRampToValueAtTime(.03, ctx.currentTime + 1.5);
  src.connect(f).connect(g).connect(ctx.destination); src.start();
  const o = ctx.createOscillator(), og = ctx.createGain();
  o.type = "sine"; o.frequency.value = 120; og.gain.value = .01;
  o.connect(og).connect(ctx.destination); o.start();
  return { stop() { try { g.gain.setTargetAtTime(0, ctx.currentTime, .3); og.gain.setTargetAtTime(0, ctx.currentTime, .3); src.stop(ctx.currentTime + 1.2); o.stop(ctx.currentTime + 1.2); } catch { } } };
}

// A port coming up: two quick notes, like a switch chirping at you.
function linkChirp(vol = 1) {
  const ctx = waterCtx(); if (!ctx) return;
  let t = ctx.currentTime + .01;
  for (const f0 of [1320, 1760]) {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "square"; o.frequency.value = f0;
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.035 * vol, t + .01); g.gain.exponentialRampToValueAtTime(.001, t + .09);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .1);
    t += .1;
  }
}

// A port going down: the lower, slower version of the same.
function linkAlarm() {
  const ctx = waterCtx(); if (!ctx) return;
  let t = ctx.currentTime + .01;
  for (const f0 of [740, 520, 380]) {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "square"; o.frequency.value = f0;
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.05, t + .02); g.gain.exponentialRampToValueAtTime(.001, t + .16);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .18);
    t += .17;
  }
}

BAMF.registerTheme("datacentre", ctx => buildDataCentre(ctx.root, ctx.switched));
})();
