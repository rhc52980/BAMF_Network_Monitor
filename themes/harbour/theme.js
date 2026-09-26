// Harbour: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- Harbour ----
// A harbour at dusk. Under the header the sun is going down behind the hills
// with the town lit along them, and a lighthouse on the breakwater sweeps the
// sky. Along the bottom, on a quay of its own, every device is a container in
// the stacks, its lamp green when it's online, amber when it's unknown and dark
// when it's off; a watched device that drops blinks red. A new device is lifted
// off the ship at the berth by the gantry crane and set down on the stacks. A
// scan sends the tug across and the lighthouse flashes; with sound on, the
// water laps, gulls call, the ship sounds its horn for a scan, and the harbour
// bell rings when something drops.
const HB_BOXES = ["#a9432c", "#2c6a98", "#377f55", "#c98326", "#56657a", "#853a6a", "#b89a2a", "#3d7d86"];

function buildHarbour(root) {
  const calm = calmMotion();
  let amb = null;
  const ambOn = on => { if (on && !amb && !document.hidden) amb = harbourSea(); if (!on && amb) { amb.stop(); amb = null; } };
  const soundOn = themeSoundButton("bamf-harbour-sound", "Harbour sounds on: click to mute",
    "Harbour sounds off: click for the water, the gulls, the horn and the bell", on => { ambOn(on); if (on) shipHorn(.5); });
  if (soundOn()) {
    const wake = () => ambOn(soundOn());
    document.addEventListener("pointerdown", wake, { once: true });
    festiveStops.push(() => document.removeEventListener("pointerdown", wake));
  }
  const vis = () => ambOn(soundOn() && !document.hidden);
  document.addEventListener("visibilitychange", vis);
  festiveStops.push(() => { document.removeEventListener("visibilitychange", vis); ambOn(false); });

  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const S = Math.max(.75, Math.min(1.2, W / 1400));
  const bed = floorBand(root, Math.round(124 * S));
  const f = bed.ctx, BH = bed.H;
  const HB = document.querySelector("header")?.getBoundingClientRect().bottom || 58;
  const horizon = HB + 70 * S;           // in the open band under the header, where it can be seen
  const sunX = W * .7, lhX = W - 64 * S, lampY = horizon - 50 * S;

  // ---- the view under the header, drawn once ----
  const sky = sceneLayer(W, H);
  {
    const g = sky.g;
    // Dark sky, and the sunset gathered round the sun rather than across the
    // whole width, so the labels on the left stay on dark.
    const gr = g.createLinearGradient(0, 0, 0, horizon);
    gr.addColorStop(0, "#0a1826"); gr.addColorStop(Math.max(0, (HB - 10) / horizon), "#10263a"); gr.addColorStop(1, "#27344c");
    g.fillStyle = gr; g.fillRect(0, 0, W, horizon);
    g.save(); g.beginPath(); g.rect(0, 0, W, horizon); g.clip();
    g.translate(sunX, horizon); g.scale(1.9, 1);
    const glow = g.createRadialGradient(0, 0, 4, 0, 0, 230 * S);
    glow.addColorStop(0, "rgba(255,196,120,.95)"); glow.addColorStop(.18, "rgba(246,150,100,.7)"); glow.addColorStop(.5, "rgba(170,96,110,.35)"); glow.addColorStop(1, "rgba(80,60,100,0)");
    g.fillStyle = glow; g.fillRect(-240 * S, -240 * S, 480 * S, 240 * S);
    g.restore();
    g.fillStyle = "#ffd58a"; g.beginPath(); g.arc(sunX, horizon + 4 * S, 22 * S, Math.PI, 0); g.fill();
    // Hills, far then near, and the town along the near ones.
    const ridge = (base, amp, k, ph, col) => {
      g.fillStyle = col; g.beginPath(); g.moveTo(W * .3, horizon + 1);
      for (let x = W * .3; x <= W; x += 8) g.lineTo(x, base - Math.max(0, Math.sin(x * k + ph) * amp + Math.sin(x * k * 2.3 + ph * 2) * amp * .35) * Math.min(1, (x - W * .3) / 160));
      g.lineTo(W, horizon + 1); g.closePath(); g.fill();
    };
    ridge(horizon, 26 * S, .0062, 1.3, "#3a4460");
    ridge(horizon, 14 * S, .011, 4.1, "#1d2638");
    g.fillStyle = "#1a2231";
    for (let x = W * .42; x < W * .66; x += rnd(9, 16) * S) {
      const bw = rnd(7, 13) * S, bh = rnd(6, 18) * S;
      g.fillRect(x, horizon - bh, bw, bh);
    }
    // Water: darker the nearer it is.
    const sea = g.createLinearGradient(0, horizon, 0, H);
    sea.addColorStop(0, "#26344b"); sea.addColorStop(.05, "#18283b"); sea.addColorStop(.4, "#11202f"); sea.addColorStop(1, "#08131e");
    g.fillStyle = sea; g.fillRect(0, horizon, W, H - horizon);
    // The sky's colour in the water right under the sun.
    g.save(); g.translate(sunX, horizon); g.scale(2.6, 1);
    const sheen = g.createRadialGradient(0, 0, 2, 0, 0, 70 * S);
    sheen.addColorStop(0, "rgba(255,170,110,.45)"); sheen.addColorStop(1, "rgba(255,170,110,0)");
    g.fillStyle = sheen; g.fillRect(-80 * S, 0, 160 * S, 70 * S); g.restore();
    // The breakwater, and the lighthouse on it.
    g.fillStyle = "#141b26";
    g.beginPath(); g.moveTo(W - 150 * S, horizon + 6 * S);
    for (let x = W - 150 * S; x <= W; x += 10 * S) g.lineTo(x, horizon - 3 * S - Math.abs(Math.sin(x * .09)) * 5 * S);
    g.lineTo(W, horizon + 8 * S); g.closePath(); g.fill();
    const tb = horizon - 2 * S, tt = lampY + 9 * S;
    for (let i = 0; i < 5; i++) {
      const y0 = tb - (tb - tt) * i / 5, y1 = tb - (tb - tt) * (i + 1) / 5;
      const w0 = 9 * S - 3 * S * i / 5, w1 = 9 * S - 3 * S * (i + 1) / 5;
      g.fillStyle = i % 2 ? "#e9e2d6" : "#b8322e";
      g.beginPath(); g.moveTo(lhX - w0, y0); g.lineTo(lhX + w0, y0); g.lineTo(lhX + w1, y1); g.lineTo(lhX - w1, y1); g.closePath(); g.fill();
    }
    g.fillStyle = "#20262f"; g.fillRect(lhX - 9 * S, tt - 2 * S, 18 * S, 3 * S);
    g.fillStyle = "#20262f"; g.beginPath(); g.moveTo(lhX - 7 * S, lampY - 7 * S); g.lineTo(lhX, lampY - 14 * S); g.lineTo(lhX + 7 * S, lampY - 7 * S); g.closePath(); g.fill();
  }
  const town = Array.from({ length: Math.round(46 * S) }, () => ({ x: rnd(W * .42, W * .9), y: horizon - rnd(2, 16) * S, p: rnd(0, 6.3), s: rnd(.6, 1.4) }));
  const stars = Array.from({ length: 50 }, () => ({ x: rnd(0, W), y: rnd(0, horizon * .55), p: rnd(0, 6.3), r: rnd(.4, 1.1) }));
  const clouds = Array.from({ length: 4 }, (_, i) => ({ x: rnd(0, W), y: horizon - rnd(26, 70) * S, w: rnd(90, 200) * S, v: rnd(2, 5) }));
  const gulls = Array.from({ length: 4 }, (_, i) => ({ x: rnd(0, W), y: HB + rnd(14, 60) * S, v: rnd(14, 30) * (i % 2 ? 1 : -1), s: rnd(.7, 1.1) * S, p: rnd(0, 6) }));
  const sails = [{ x: W * .35, v: 3.2, kind: "sail" }, { x: W * .82, v: -1.6, kind: "ship" }];

  // ---- the quay along the bottom ----
  const quayTop = BH - 26 * S, water = BH - 16 * S;
  let list = [], placed = new Set(), hidden = new Set(), lastLen = -1;
  let yard = null;                       // container layout, redone when the count changes
  const flashUntil = {}, backAt = {};
  function layout() {
    const n = Math.max(1, list.length);
    const rows = n > 120 ? 5 : 4;
    const cols = Math.ceil(n / rows);
    const ch = 15 * S;
    const maxYard = Math.max(150 * S, Math.min(W * .46, W - 640 * S));
    const cw = Math.max(8 * S, Math.min(34 * S, maxYard / cols - 3 * S));
    const x0 = 14 * S, pitch = cw + 3 * S;
    const yardEnd = x0 + cols * pitch;
    const craneX = yardEnd + 34 * S;
    const quayEnd = craneX + 36 * S;
    const shipX = quayEnd + 8 * S, shipW = Math.min(250 * S, Math.max(120 * S, W - shipX - 150 * S));
    // The marina beyond the ship: a pontoon with boats along it, and the tug's
    // berth at the far end. Left out where the screen hasn't the room.
    const shipEnd = shipX + shipW;
    const tugBerth = W - 62 * S;
    const mx0 = shipEnd + 96 * S, mx1 = tugBerth - 60 * S;
    const boats = [];
    if (mx1 - mx0 > 110 * S) for (let x = mx0 + 26 * S, k = 0; x < mx1 - 20 * S; x += 88 * S, k++) boats.push({ x, kind: ["sail", "launch", "fishing"][k % 3], p: rnd(0, 6.3) });
    const buoys = [shipEnd + 34 * S, shipEnd + 66 * S].filter(b => b < tugBerth - 50 * S);
    yard = { rows, cols, ch, cw, x0, pitch, yardEnd, craneX, quayEnd, shipX, shipW, shipEnd, tugBerth, mx0, mx1, boats, buoys,
      slot: i => ({ x: x0 + Math.floor(i / rows) * pitch, y: quayTop - (i % rows + 1) * ch }) };
    // The crane waits just past its own legs; when the stacks grow it moves up with them.
    crane.home = craneX + 60 * S;
    if (!crane.step && !crane.jobs.length) crane.tx = crane.home;
    staticQuay();
  }
  // The quay, the crane's frame and the ship's hull don't move, so they're
  // drawn once per layout.
  const still = sceneLayer(W, BH);
  function staticQuay() {
    const g = still.g, y = yard;
    g.clearRect(0, 0, W, BH);
    const wa = g.createLinearGradient(0, 0, 0, BH);
    wa.addColorStop(0, "#1b3048"); wa.addColorStop(.55, "#122438"); wa.addColorStop(1, "#0a1522");
    g.fillStyle = wa; g.fillRect(0, 0, W, BH);
    // The quay: a concrete apron with a face down to the water, and fenders.
    g.fillStyle = "#56616c"; g.fillRect(0, quayTop, y.quayEnd, BH - quayTop);
    g.fillStyle = "#7c8894"; g.fillRect(0, quayTop, y.quayEnd, 2 * S);
    g.fillStyle = "#3e4852"; g.fillRect(0, quayTop + 9 * S, y.quayEnd, BH);
    for (let x = 30 * S; x < y.quayEnd; x += 46 * S) { g.fillStyle = "#2b333b"; g.fillRect(x, quayTop + 9 * S, 2 * S, BH); }
    g.fillStyle = "#e0b43a"; for (let x = 0; x < y.quayEnd; x += 22 * S) g.fillRect(x, quayTop + 3 * S, 10 * S, 1.3 * S);
    g.fillStyle = "#1c1f23";
    for (let yy = quayTop + 12 * S; yy < BH; yy += 9 * S) { g.beginPath(); g.arc(y.quayEnd, yy, 3.4 * S, 0, Math.PI * 2); g.fill(); }
    // Bollards along the edge.
    for (const bx of [y.quayEnd - 12 * S, y.craneX - 44 * S]) {
      g.fillStyle = "#2f363d"; g.fillRect(bx - 3 * S, quayTop - 5 * S, 6 * S, 5 * S);
      g.beginPath(); g.ellipse(bx, quayTop - 5 * S, 4 * S, 1.6 * S, 0, 0, Math.PI * 2); g.fill();
    }
    // The crane's legs and the portal: the boom and trolley are drawn live.
    const cx = y.craneX, legH = quayTop - 30 * S;
    g.strokeStyle = "#3a5d7a"; g.lineWidth = 4 * S; g.lineCap = "round";
    g.beginPath();
    g.moveTo(cx - 26 * S, quayTop); g.lineTo(cx - 20 * S, legH);
    g.moveTo(cx + 26 * S, quayTop); g.lineTo(cx + 20 * S, legH);
    g.moveTo(cx - 24 * S, quayTop - 22 * S); g.lineTo(cx + 24 * S, quayTop - 22 * S);
    g.stroke();
    g.lineWidth = 1.5 * S; g.strokeStyle = "#4c7898";
    g.beginPath(); g.moveTo(cx - 24 * S, quayTop - 2 * S); g.lineTo(cx + 22 * S, legH + 4 * S); g.moveTo(cx + 24 * S, quayTop - 2 * S); g.lineTo(cx - 22 * S, legH + 4 * S); g.stroke();
    g.fillStyle = "#2c4863"; g.fillRect(cx - 24 * S, legH - 6 * S, 48 * S, 8 * S);
    g.fillStyle = "#1c1f23"; for (const wx of [cx - 26 * S, cx + 26 * S]) g.fillRect(wx - 5 * S, quayTop - 3 * S, 10 * S, 3 * S);
    // The machinery house on top of the portal, a window lit.
    g.fillStyle = "#e9eef2"; g.fillRect(cx - 10 * S, legH - 16 * S, 20 * S, 10 * S);
    g.fillStyle = "#ffcf6e"; g.fillRect(cx - 6 * S, legH - 13 * S, 5 * S, 3.4 * S);
    // The ship's hull at the berth; its deck cargo and bridge lights are live.
    const sx = y.shipX, sw = y.shipW, deck = water - 12 * S;
    g.fillStyle = "#15191f";
    g.beginPath(); g.moveTo(sx, deck); g.lineTo(sx + sw, deck); g.lineTo(sx + sw - 16 * S, BH + 2); g.lineTo(sx + 6 * S, BH + 2); g.closePath(); g.fill();
    g.fillStyle = "#7a2622"; g.fillRect(sx + 5 * S, water + 1 * S, sw - 18 * S, BH - water);
    g.fillStyle = "#eee7da"; g.fillRect(sx, deck, sw - 6 * S, 1.4 * S);
    // The bridge at the stern.
    const bx = sx + sw - 44 * S;
    g.fillStyle = "#e6e2da"; g.fillRect(bx, deck - 34 * S, 30 * S, 34 * S);
    g.fillStyle = "#cfc9be"; g.fillRect(bx - 4 * S, deck - 30 * S, 38 * S, 4 * S);
    g.fillStyle = "#2a3440"; g.fillRect(bx + 20 * S, deck - 46 * S, 8 * S, 12 * S);
    g.fillStyle = "#c23a2c"; g.fillRect(bx + 20 * S, deck - 44 * S, 8 * S, 3 * S);
    if (y.boats.length) {
      for (let x = y.mx0; x < y.mx1; x += 40 * S) { g.fillStyle = "#3b2d20"; g.fillRect(x, water - 6 * S, 3 * S, BH); }
      g.fillStyle = "#7a5d3e"; g.fillRect(y.mx0 - 6 * S, water - 7 * S, y.mx1 - y.mx0 + 12 * S, 4 * S);
      g.fillStyle = "rgba(0,0,0,.28)"; for (let x = y.mx0; x < y.mx1; x += 7 * S) g.fillRect(x, water - 7 * S, 1 * S, 4 * S);
      g.fillStyle = "#4e3b28"; g.fillRect(y.mx0 - 6 * S, water - 3 * S, y.mx1 - y.mx0 + 12 * S, 1.6 * S);
      for (const lx of pierLamps(y)) { g.fillStyle = "#2a2f36"; g.fillRect(lx - .8 * S, water - 33 * S, 1.6 * S, 26 * S); }
    }
    // The tug's berth: a short jetty on piles.
    g.fillStyle = "#3b2d20"; for (const x of [y.tugBerth + 26 * S, y.tugBerth + 52 * S]) g.fillRect(x, water - 6 * S, 3 * S, BH);
    g.fillStyle = "#7a5d3e"; g.fillRect(y.tugBerth + 20 * S, water - 7 * S, W - y.tugBerth, 4 * S);
    // The ship's own cargo, stacked forward of the bridge.
    for (let i = 0; i < 14; i++) {
      const col = i % 7, row = Math.floor(i / 7);
      const cxx = sx + 12 * S + col * 25 * S;
      if (cxx + 22 * S > bx - 6 * S) continue;
      containerAt(g, cxx, deck - (row + 1) * 11 * S, 22 * S, 11 * S, HB_BOXES[(i * 5 + 3) % HB_BOXES.length], null, 1);
    }
  }
  const pierLamps = y => { const out = []; for (let x = y.mx0 + 4 * S; x < y.mx1; x += 132 * S) out.push(x); return out; };
  // A light's reflection: short strokes down the water, each wobbling.
  function reflect(g, x, from, rgb, a, t, w = 7) {
    for (let k = 0; k < 7; k++) {
      const yy = from + k * 3.6 * S; if (yy > BH) break;
      const ww = (w - k * .7) * S * (.55 + .45 * Math.sin(t / 260 + k * 1.9 + x));
      g.fillStyle = `rgba(${rgb},${a * (1 - k / 7)})`; g.fillRect(x - ww / 2 + Math.sin(t / 500 + k) * 1.4 * S, yy, ww, 1.3 * S);
    }
  }
  function boat(g, b, t) {
    const bob = Math.sin(t / 700 + b.p) * 1.3 * S, roll = Math.sin(t / 900 + b.p) * .03;
    const x = b.x, y = water + 1 * S + bob;
    g.save(); g.translate(x, y); g.rotate(roll);
    if (b.kind === "sail") {
      g.fillStyle = "#eef0ee"; g.beginPath(); g.moveTo(-22 * S, -6 * S); g.lineTo(22 * S, -6 * S); g.quadraticCurveTo(18 * S, 2 * S, 12 * S, 3 * S); g.lineTo(-17 * S, 3 * S); g.closePath(); g.fill();
      g.fillStyle = "#23405f"; g.fillRect(-18 * S, -1 * S, 36 * S, 1.2 * S);
      g.fillStyle = "#d9d5cc"; g.fillRect(-9 * S, -11 * S, 14 * S, 5 * S);
      g.fillStyle = "#ffd88a"; g.fillRect(-6 * S, -9.5 * S, 2 * S, 1.6 * S); g.fillRect(-1 * S, -9.5 * S, 2 * S, 1.6 * S);
      g.strokeStyle = "#c8ccd2"; g.lineWidth = 1 * S; g.beginPath(); g.moveTo(2 * S, -6 * S); g.lineTo(2 * S, -52 * S); g.moveTo(2 * S, -14 * S); g.lineTo(-16 * S, -12 * S); g.moveTo(2 * S, -52 * S); g.lineTo(-20 * S, -7 * S); g.moveTo(2 * S, -52 * S); g.lineTo(21 * S, -6 * S); g.stroke();
      g.fillStyle = "#e6e2d6"; g.fillRect(-16 * S, -14 * S, 18 * S, 2.4 * S);
      g.fillStyle = "#fff6dc"; g.beginPath(); g.arc(2 * S, -53 * S, 1.4 * S, 0, Math.PI * 2); g.fill();
    } else if (b.kind === "launch") {
      g.fillStyle = "#1f3b5c"; g.beginPath(); g.moveTo(-17 * S, -5 * S); g.lineTo(18 * S, -5 * S); g.lineTo(14 * S, 2 * S); g.lineTo(-15 * S, 2 * S); g.closePath(); g.fill();
      g.fillStyle = "#e9e5da"; g.fillRect(-17 * S, -6 * S, 35 * S, 1.4 * S);
      g.fillStyle = "#c9c2b3"; g.fillRect(-8 * S, -13 * S, 14 * S, 7 * S);
      g.fillStyle = "rgba(160,210,240,.7)"; g.fillRect(3 * S, -12 * S, 3 * S, 5 * S);
      g.fillStyle = "#b8322e"; g.fillRect(-10 * S, -14 * S, 18 * S, 1.8 * S);
    } else {
      g.fillStyle = "#2f7a78"; g.beginPath(); g.moveTo(-20 * S, -7 * S); g.lineTo(20 * S, -8 * S); g.lineTo(15 * S, 2 * S); g.lineTo(-16 * S, 2 * S); g.closePath(); g.fill();
      g.fillStyle = "#f0e9d8"; g.fillRect(-19 * S, -8 * S, 38 * S, 1.4 * S);
      g.fillStyle = "#efe9dc"; g.fillRect(4 * S, -19 * S, 11 * S, 11 * S);
      g.fillStyle = "#ffcf7a"; g.fillRect(6 * S, -17 * S, 3 * S, 3 * S); g.fillRect(10.5 * S, -17 * S, 3 * S, 3 * S);
      g.strokeStyle = "#d6d0c4"; g.lineWidth = 1 * S; g.beginPath(); g.moveTo(-8 * S, -8 * S); g.lineTo(-8 * S, -30 * S); g.lineTo(-24 * S, -14 * S); g.moveTo(-8 * S, -30 * S); g.lineTo(8 * S, -19 * S); g.stroke();
    }
    g.restore();
  }
  function containerAt(g, x, y, w, h, col, lamp, lit, t = 0) {
    g.fillStyle = col; g.fillRect(x, y, w, h);
    g.fillStyle = "rgba(0,0,0,.22)";
    for (let k = x + 3 * S; k < x + w - 2 * S; k += 3 * S) g.fillRect(k, y + 1.5 * S, 1 * S, h - 3 * S);
    g.fillStyle = "rgba(255,255,255,.18)"; g.fillRect(x, y, w, 1 * S);
    g.fillStyle = "rgba(0,0,0,.35)"; g.fillRect(x + w - 1 * S, y, 1 * S, h); g.fillRect(x, y + h - 1 * S, w, 1 * S);
    if (lit < 1) { g.fillStyle = `rgba(8,14,22,${(1 - lit) * .62})`; g.fillRect(x, y, w, h); }
    if (!lamp) return;
    const lx = x + w - 3.4 * S, ly = y + 3.2 * S, rr = Math.max(1.3, 1.9 * S);
    if (lamp === "off") { g.fillStyle = "#18222c"; g.beginPath(); g.arc(lx, ly, rr, 0, Math.PI * 2); g.fill(); return; }
    const colr = lamp === "up" ? "79,224,160" : lamp === "un" ? "255,179,71" : "255,93,93";
    g.fillStyle = `rgba(${colr},.28)`; g.beginPath(); g.arc(lx, ly, rr * 2.4, 0, Math.PI * 2); g.fill();
    g.fillStyle = `rgb(${colr})`; g.beginPath(); g.arc(lx, ly, rr, 0, Math.PI * 2); g.fill();
  }

  // The crane: where the trolley is along the boom, how far down the hook,
  // and whether it's carrying a container. A new device is a job for it.
  const crane = { home: null, tx: null, hook: 0, carry: null, jobs: [], step: null };
  function craneJob(h, index) {
    const y = yard, slot = y.slot(index);
    const shipPick = y.shipX + rnd(30, Math.max(40, y.shipW - 80 * S) / S) * S;
    const deckTop = water - 12 * S - 22 * S;
    const col = HB_BOXES[sceneHash(h.mac || h.id) % HB_BOXES.length];
    return [
      { to: { tx: shipPick }, secs: 2.2 },
      { to: { hook: deckTop - boomY() - 6 * S }, secs: 1.3 },
      { run: () => { crane.carry = col; } , secs: .4 },
      { to: { hook: 8 * S }, secs: 1.2 },
      { to: { tx: slot.x + y.cw / 2 }, secs: 2.6 },
      { to: { hook: slot.y - boomY() - 6 * S }, secs: 1.4 },
      { run: () => { crane.carry = null; hidden.delete(h.id); backAt[h.id] = performance.now(); if (soundOn()) quayClunk(); }, secs: .5 },
      { to: { hook: 8 * S }, secs: 1.1 },
      { to: { tx: crane.home }, secs: 2 },
    ];
  }
  const boomY = () => 10 * S;
  const tug = { mode: "berth", x: 0 };
  let beamBoost = 0, wave = -1;

  function refreshList() {
    list = sceneHosts();
    if (list.length !== lastLen) { lastLen = list.length; layout(); }
  }

  function step(dt, now) {
    for (const cl of clouds) { cl.x += cl.v * dt; if (cl.x > W + cl.w) cl.x = -cl.w; }
    for (const g of gulls) { g.x += g.v * dt; if (g.x > W + 30) g.x = -30; if (g.x < -30) g.x = W + 30; }
    for (const s of sails) { s.x += s.v * dt * S; if (s.x > W * .95) s.x = W * .3; if (s.x < W * .3) s.x = W * .92; }
    beamBoost = Math.max(0, beamBoost - dt * .3);
    if (tug.mode === "out") { tug.x -= 80 * S * dt; if (tug.x < -90 * S) { tug.mode = "return"; tug.x = W + 90 * S; } }
    else if (tug.mode === "return") { tug.x -= 45 * S * dt; if (tug.x <= yard.tugBerth) tug.mode = "berth"; }
    if (tug.mode === "berth") tug.x = yard.tugBerth;
    if (wave >= 0) { wave += dt * 26; if (wave > yard.cols + 6) wave = -1; }
    // The crane works through its jobs one step at a time.
    if (!crane.step && crane.jobs.length) {
      const job = crane.jobs[0];
      if (!job.steps) job.steps = craneJob(job.h, job.index);
      const s = job.steps.shift();
      if (!s) crane.jobs.shift();
      else crane.step = { ...s, from: { tx: crane.tx, hook: crane.hook }, t: 0 };
    }
    if (crane.step) {
      const s = crane.step;
      s.t += dt / s.secs;
      const k = easeInOut(Math.min(1, s.t));
      if (s.to) for (const key in s.to) crane[key] = s.from[key] + (s.to[key] - s.from[key]) * k;
      if (s.t >= 1) { if (s.run) s.run(); crane.step = null; }
    }
  }

  function draw(now) {
    const t = now || 0;
    // Under the header: the view, then what moves in it.
    ctx.drawImage(sky.cv, 0, 0, W, H);
    for (const s of stars) { ctx.fillStyle = `rgba(230,236,255,${.25 + .35 * (.5 + .5 * Math.sin(t / 900 + s.p))})`; ctx.fillRect(s.x, s.y, s.r * S * 1.4, s.r * S * 1.4); }
    for (const cl of clouds) {
      const g = ctx.createLinearGradient(0, cl.y - 6 * S, 0, cl.y + 6 * S);
      g.addColorStop(0, "rgba(110,90,120,0)"); g.addColorStop(.6, "rgba(240,150,120,.35)"); g.addColorStop(1, "rgba(110,90,120,0)");
      ctx.fillStyle = g; ctx.beginPath(); ctx.ellipse(cl.x, cl.y, cl.w / 2, 5 * S, 0, 0, Math.PI * 2); ctx.fill();
    }
    for (const b of town) { ctx.fillStyle = `rgba(255,205,120,${.45 + .45 * (.5 + .5 * Math.sin(t / (600 * b.s) + b.p))})`; ctx.fillRect(b.x, b.y, 1.6 * S, 1.6 * S); }
    // The sun's road across the water.
    for (let i = 0; i < 14; i++) {
      const yy = horizon + 5 * S + i * 5 * S, w = (34 - i * 1.6) * S * (.7 + .3 * Math.sin(t / 400 + i * 1.7));
      ctx.fillStyle = `rgba(255,190,110,${.42 - i * .026})`; ctx.fillRect(sunX - w / 2 + Math.sin(t / 700 + i) * 4 * S, yy, w, 1.6 * S);
    }
    for (const s of sails) {
      const x = s.x, y = horizon + 1 * S;
      ctx.fillStyle = "#1a2331";
      if (s.kind === "sail") { ctx.beginPath(); ctx.moveTo(x, y - 14 * S); ctx.lineTo(x + 8 * S, y - 2 * S); ctx.lineTo(x, y - 2 * S); ctx.closePath(); ctx.fill(); ctx.fillRect(x - 6 * S, y - 2 * S, 16 * S, 2.4 * S); }
      else { ctx.fillRect(x - 26 * S, y - 5 * S, 52 * S, 5 * S); ctx.fillRect(x + 14 * S, y - 11 * S, 8 * S, 6 * S); ctx.fillStyle = "rgba(255,210,140,.8)"; ctx.fillRect(x + 16 * S, y - 9 * S, 1.4 * S, 1.4 * S); }
    }
    for (const g of gulls) {
      const flap = Math.sin(t / 180 + g.p) * 4 * g.s;
      ctx.strokeStyle = "rgba(20,26,36,.75)"; ctx.lineWidth = 1.6 * g.s;
      ctx.beginPath(); ctx.moveTo(g.x - 8 * g.s, g.y - flap); ctx.quadraticCurveTo(g.x - 3 * g.s, g.y - 3 * g.s, g.x, g.y); ctx.quadraticCurveTo(g.x + 3 * g.s, g.y - 3 * g.s, g.x + 8 * g.s, g.y - flap); ctx.stroke();
    }
    // The lighthouse: two beams turning, long when they point sideways, and a
    // flash when one swings round to face you.
    const a = t / 1000 * (.9 + beamBoost * 3);
    for (const off of [0, Math.PI]) {
      const ang = a + off, dx = Math.cos(ang), toward = Math.max(0, Math.sin(ang));
      const len = (60 + 520 * Math.abs(dx)) * S, spread = (.07 + .05 * toward) * len;
      const bg = ctx.createLinearGradient(lhX, lampY, lhX + dx * len, lampY);
      bg.addColorStop(0, `rgba(255,236,170,${.30 + .25 * toward + beamBoost * .2})`); bg.addColorStop(1, "rgba(255,236,170,0)");
      ctx.fillStyle = bg;
      ctx.beginPath(); ctx.moveTo(lhX, lampY); ctx.lineTo(lhX + dx * len, lampY - spread); ctx.lineTo(lhX + dx * len, lampY + spread * .8); ctx.closePath(); ctx.fill();
      if (toward > .92) {
        const fl = ctx.createRadialGradient(lhX, lampY, 0, lhX, lampY, 40 * S);
        fl.addColorStop(0, `rgba(255,245,210,${(toward - .92) * 9})`); fl.addColorStop(1, "rgba(255,245,210,0)");
        ctx.fillStyle = fl; ctx.fillRect(lhX - 40 * S, lampY - 40 * S, 80 * S, 80 * S);
      }
    }
    ctx.fillStyle = "#fff1c2"; ctx.beginPath(); ctx.arc(lhX, lampY, 3.4 * S, 0, Math.PI * 2); ctx.fill();

    drawQuay(t);
  }

  function drawQuay(t) {
    const y = yard, g = f;
    g.drawImage(still.cv, 0, 0, W, BH);
    // Ripples on the water, and the lights in it.
    g.strokeStyle = "rgba(160,200,230,.12)"; g.lineWidth = 1;
    for (let i = 0; i < 7; i++) {
      const yy = 8 * S + i * 11 * S; g.beginPath();
      for (let x = y.quayEnd; x <= W; x += 14) { const v = yy + Math.sin(x / 38 + t / 900 + i) * 1.4 * S; x === y.quayEnd ? g.moveTo(x, v) : g.lineTo(x, v); }
      g.stroke();
    }
    // Yard lights: masts behind the stacks, their warm pools laid over them below.
    const masts = [y.x0 + (y.yardEnd - y.x0) * .3, y.x0 + (y.yardEnd - y.x0) * .78];
    for (const mx of masts) {
      g.fillStyle = "#39434d"; g.fillRect(mx - 1 * S, 6 * S, 2 * S, quayTop - 6 * S);
      g.fillStyle = "#ffe2a0"; g.fillRect(mx - 5 * S, 5 * S, 10 * S, 2.4 * S);
    }
    // The containers, one a device.
    list.forEach((h, i) => {
      if (hidden.has(h.id)) return;
      const s = y.slot(i), st = sceneState(h);
      const alarm = h.watched && !h.online && (flashUntil[h.id] || 0) > t;
      const lamp = alarm ? (Math.sin(t / 160) > 0 ? "alarm" : "off") : st;
      const inWave = wave >= 0 && Math.abs(Math.floor(i / y.rows) - wave) < 1.2;
      containerAt(g, s.x, s.y, y.cw, y.ch, HB_BOXES[sceneHash(h.mac || h.id) % HB_BOXES.length], lamp === "off" && inWave ? "up" : lamp, st === "off" ? .35 : 1, t);
      const back = backAt[h.id];
      if (back && t - back < 1600) {
        const k = (t - back) / 1600;
        g.strokeStyle = `rgba(79,224,160,${.8 * (1 - k)})`; g.lineWidth = 1.5 * S;
        g.strokeRect(s.x - 3 * S * k, s.y - 3 * S * k, y.cw + 6 * S * k, y.ch + 6 * S * k);
      }
    });
    for (const mx of masts) {
      const pool = g.createLinearGradient(0, 7 * S, 0, quayTop);
      pool.addColorStop(0, "rgba(255,214,140,.16)"); pool.addColorStop(1, "rgba(255,214,140,.02)");
      g.fillStyle = pool; g.beginPath(); g.moveTo(mx - 4 * S, 7 * S); g.lineTo(mx + 4 * S, 7 * S); g.lineTo(mx + 60 * S, quayTop); g.lineTo(mx - 60 * S, quayTop); g.closePath(); g.fill();
    }
    // The crane's boom, trolley, cable and spreader.
    const by = boomY(), cx = y.craneX, left = cx - 40 * S, right = y.shipX + y.shipW - 30 * S;
    g.fillStyle = "#3f6687"; g.fillRect(Math.min(left, y.x0), by - 3 * S, right - Math.min(left, y.x0), 6 * S);
    g.strokeStyle = "#5b86a8"; g.lineWidth = 1 * S; g.beginPath();
    for (let x = Math.min(left, y.x0); x < right; x += 10 * S) { g.moveTo(x, by - 3 * S); g.lineTo(x + 5 * S, by + 3 * S); g.lineTo(x + 10 * S, by - 3 * S); }
    g.stroke();
    g.strokeStyle = "#3a5d7a"; g.lineWidth = 2 * S;
    g.beginPath(); g.moveTo(cx, quayTop - 30 * S); g.lineTo(cx, by - 14 * S); g.lineTo(right, by - 2 * S); g.moveTo(cx, by - 14 * S); g.lineTo(Math.min(left, y.x0) + 20 * S, by - 2 * S); g.stroke();
    g.fillStyle = Math.sin(t / 500) > 0 ? "#ff4d3d" : "#5a1c16"; g.beginPath(); g.arc(cx, by - 15 * S, 2 * S, 0, Math.PI * 2); g.fill();
    if (crane.tx == null) crane.tx = crane.home;
    const tx = crane.tx, sway = Math.sin(t / 700) * .8 * S;
    g.fillStyle = "#e6c14a"; g.fillRect(tx - 7 * S, by - 4 * S, 14 * S, 8 * S);
    const hy = by + 4 * S + crane.hook;
    g.strokeStyle = "#1c2128"; g.lineWidth = 1 * S;
    g.beginPath(); g.moveTo(tx - 3 * S, by + 4 * S); g.lineTo(tx - 3 * S + sway, hy); g.moveTo(tx + 3 * S, by + 4 * S); g.lineTo(tx + 3 * S + sway, hy); g.stroke();
    g.fillStyle = "#e6c14a"; g.fillRect(tx - y.cw / 2 + sway, hy, y.cw, 2.5 * S);
    if (crane.carry) containerAt(g, tx - y.cw / 2 + sway, hy + 2.5 * S, y.cw, y.ch, crane.carry, "up", 1, t);
    // The bridge's lit windows, and the ship riding the swell.
    const bx = y.shipX + y.shipW - 44 * S, deck = water - 12 * S;
    for (let k = 0; k < 4; k++) { g.fillStyle = `rgba(255,214,140,${.75 + .25 * Math.sin(t / 1500 + k)})`; g.fillRect(bx + 4 * S + k * 6.5 * S, deck - 26 * S, 3.5 * S, 3 * S); }
    g.fillStyle = Math.sin(t / 800) > .3 ? "#4fe0a0" : "#1b3a2c"; g.beginPath(); g.arc(y.shipX + y.shipW - 8 * S, deck - 4 * S, 1.8 * S, 0, Math.PI * 2); g.fill();
    // The marina: the boats along the pontoon, the pier lamps and their light in the water.
    for (const b of y.boats) { boat(g, b, t); reflect(g, b.x + 2 * S, water + 5 * S, "255,240,210", .25, t, 4); }
    for (const lx of pierLamps(y)) {
      const gl = g.createRadialGradient(lx, water - 34 * S, 0, lx, water - 34 * S, 22 * S);
      gl.addColorStop(0, "rgba(255,210,140,.5)"); gl.addColorStop(1, "rgba(255,210,140,0)");
      g.fillStyle = gl; g.fillRect(lx - 22 * S, water - 56 * S, 44 * S, 44 * S);
      g.fillStyle = "#ffe0a0"; g.beginPath(); g.arc(lx, water - 34 * S, 2.2 * S, 0, Math.PI * 2); g.fill();
      reflect(g, lx, water + 2 * S, "255,205,130", .55, t, 9);
    }
    reflect(g, y.shipX + y.shipW - 30 * S, water + 3 * S, "255,214,140", .4, t, 10);
    // The tug: at its berth, or crossing after a scan with its wake behind it.
    {
      const x = tug.x, yy = water - 1 * S + Math.sin(t / 300) * .8 * S, moving = tug.mode !== "berth";
      if (moving) {
        g.strokeStyle = "rgba(230,240,255,.45)"; g.lineWidth = 1.2 * S;
        for (let k = 1; k <= 5; k++) { g.beginPath(); g.moveTo(x + 18 * S + k * 10 * S, yy + 2 * S); g.lineTo(x + 26 * S + k * 10 * S, yy + 3.5 * S); g.stroke(); }
      }
      g.fillStyle = "#b8322e"; g.beginPath(); g.moveTo(x - 16 * S, yy - 5 * S); g.lineTo(x + 18 * S, yy - 5 * S); g.lineTo(x + 14 * S, yy + 3 * S); g.lineTo(x - 12 * S, yy + 3 * S); g.closePath(); g.fill();
      g.fillStyle = "#1b1f25"; g.fillRect(x - 12 * S, yy - 7 * S, 28 * S, 2.4 * S);
      g.fillStyle = "#eae4d8"; g.fillRect(x - 6 * S, yy - 15 * S, 12 * S, 8 * S);
      g.fillStyle = "#ffd98a"; g.fillRect(x - 4 * S, yy - 13 * S, 3 * S, 2.4 * S); g.fillRect(x + 1 * S, yy - 13 * S, 3 * S, 2.4 * S);
      g.fillStyle = "#1b1f25"; g.fillRect(x + 7 * S, yy - 19 * S, 4 * S, 7 * S);
      reflect(g, x, water + 4 * S, "255,217,138", .35, t, 6);
      for (let k = 0; k < (moving ? 4 : 2); k++) {
        const age = ((t / (moving ? 600 : 1400) + k * .25) % 1);
        g.fillStyle = `rgba(200,205,215,${.35 * (1 - age)})`; g.beginPath(); g.arc(x + 9 * S + age * 20 * S, yy - 21 * S - age * 12 * S, (2 + age * 4) * S, 0, Math.PI * 2); g.fill();
      }
    }
    // Buoys riding the water: red to port, green to starboard, each blinking.
    for (const [bx2, colr, ph] of y.buoys.map((b, k) => [b, k ? "#3fb56f" : "#d8453b", k * 1.3])) {
      const yy = water - 2 * S + Math.sin(t / 650 + ph) * 1.4 * S;
      g.fillStyle = colr; g.beginPath(); g.moveTo(bx2 - 5 * S, yy); g.lineTo(bx2 + 5 * S, yy); g.lineTo(bx2 + 3 * S, yy - 11 * S); g.lineTo(bx2 - 3 * S, yy - 11 * S); g.closePath(); g.fill();
      g.fillStyle = "#1a1f26"; g.fillRect(bx2 - .7 * S, yy - 17 * S, 1.4 * S, 6 * S);
      const on = Math.sin(t / 420 + ph * 2) > .6;
      if (on) { g.fillStyle = colr; g.beginPath(); g.arc(bx2, yy - 18 * S, 4 * S, 0, Math.PI * 2); g.globalAlpha = .35; g.fill(); g.globalAlpha = 1; }
      g.fillStyle = on ? "#fff2d8" : "#3a3f46"; g.beginPath(); g.arc(bx2, yy - 18 * S, 1.6 * S, 0, Math.PI * 2); g.fill();
    }
  }

  refreshList();
  festiveHooks.rendered = () => { const n = list.length; refreshList(); if (calm && n !== list.length) draw(0); };
  if (calm) { draw(0); return; }
  c.start();

  // A new device: the crane brings it ashore. Until it's set down, its slot is empty.
  festiveHooks.newDevice = h => {
    if (!h) return;
    refreshList();
    const index = list.findIndex(x => x.id === h.id);
    if (index < 0 || document.hidden) return;
    hidden.add(h.id);
    crane.jobs.push({ h, index });
  };
  // A scan: the tug crosses, the lighthouse flares, and a ripple of lamps runs the yard.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    refreshList();
    if (tug.mode === "berth") tug.mode = "out";
    beamBoost = 1;
    wave = 0;
    if (soundOn()) shipHorn(.8);
  };
  festiveHooks.netChange = (off, back) => {
    refreshList();
    if (document.hidden) return;
    const now = performance.now();
    for (const id of off) flashUntil[id] = now + 30e3;
    for (const id of back) { backAt[id] = now; delete flashUntil[id]; }
    if (off.some(id => hosts.find(h => h.id === id)?.watched) && soundOn()) harbourBell();
    else if (back.length && soundOn()) shipHorn(.35, true);
  };
}

// ---- The harbour's sounds, made in the browser. Off unless switched on. ----
// The sea: brown noise, low-passed, swelling slowly; and now and then a gull.
function harbourSea() {
  const ctx = waterCtx(); if (!ctx) return null;
  const src = ctx.createBufferSource(), lp = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, 6, true); src.loop = true;
  lp.type = "lowpass"; lp.frequency.value = 520;
  const lfo = ctx.createOscillator(), lg = ctx.createGain();
  lfo.frequency.value = .09; lg.gain.value = .018; lfo.connect(lg).connect(g.gain);
  g.gain.setValueAtTime(0, ctx.currentTime); g.gain.linearRampToValueAtTime(.035, ctx.currentTime + 2);
  src.connect(lp).connect(g).connect(ctx.destination); src.start(); lfo.start();
  const gulls = setInterval(() => { if (!document.hidden && Math.random() < .5) gullCry(); }, 14000);
  return { stop() { clearInterval(gulls); try { g.gain.setTargetAtTime(0, ctx.currentTime, .4); src.stop(ctx.currentTime + 1.5); lfo.stop(ctx.currentTime + 1.5); } catch { } } };
}

function gullCry() {
  const ctx = waterCtx(); if (!ctx) return;
  let t = ctx.currentTime + .02;
  for (let i = 0; i < 3; i++) {
    const o = ctx.createOscillator(), g = ctx.createGain(), bp = ctx.createBiquadFilter();
    o.type = "sawtooth"; bp.type = "bandpass"; bp.frequency.value = 1500; bp.Q.value = 3;
    o.frequency.setValueAtTime(1900, t); o.frequency.exponentialRampToValueAtTime(1150, t + .22);
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.03, t + .03); g.gain.exponentialRampToValueAtTime(.001, t + .26);
    o.connect(bp).connect(g).connect(ctx.destination); o.start(t); o.stop(t + .3);
    t += .3;
  }
}

// The ship's horn: two low reeds together, rising in and dying away.
function shipHorn(vol = 1, short = false) {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .02, len = short ? .45 : 1.6;
  const lp = ctx.createBiquadFilter(), g = ctx.createGain();
  lp.type = "lowpass"; lp.frequency.value = 700;
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.09 * vol, t + .18); g.gain.setValueAtTime(.09 * vol, t + len); g.gain.exponentialRampToValueAtTime(.001, t + len + .7);
  for (const hz of short ? [174, 261] : [98, 147]) {
    const o = ctx.createOscillator(); o.type = "sawtooth"; o.frequency.value = hz; o.connect(lp); o.start(t); o.stop(t + len + .8);
  }
  lp.connect(g).connect(ctx.destination);
}

// The harbour bell: struck twice, ringing on.
function harbourBell() {
  const ctx = waterCtx(); if (!ctx) return;
  for (const at of [0, .9]) {
    const t = ctx.currentTime + .02 + at;
    for (const [m, a] of [[1, .07], [2.76, .03], [5.4, .015]]) {
      const o = ctx.createOscillator(), g = ctx.createGain();
      o.type = "sine"; o.frequency.value = 520 * m;
      g.gain.setValueAtTime(a, t); g.gain.exponentialRampToValueAtTime(.0005, t + 2.6 / m);
      o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + 2.8);
    }
  }
}

// A container set down on the stack: a dull steel knock.
function quayClunk() {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, o = ctx.createOscillator(), g = ctx.createGain();
  o.type = "triangle"; o.frequency.setValueAtTime(160, t); o.frequency.exponentialRampToValueAtTime(70, t + .18);
  g.gain.setValueAtTime(.12, t); g.gain.exponentialRampToValueAtTime(.001, t + .3);
  o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .32);
}

BAMF.registerTheme("harbour", ctx => buildHarbour(ctx.root, ctx.switched));
})();
