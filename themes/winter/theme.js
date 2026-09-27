// Winter: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Winter: snow falling past frosted edges, settling into a drift along the
// bottom. A finished scan brings a gust through.
//
// A new device nobody has marked known is an intruder: a yeti. The frost at
// the edges burns red, the snow whips round, and it stomps in from the right,
// leaving its footprints in the drift; it stands there, knee-deep, eyes red,
// tagged, until the device is marked known. Then it trudges back off into the
// snow, and a gust blows through.
function buildWinter(root) {
  const calm = calmMotion();
  const flakes = [];
  let gust = 0;
  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const front = frontCanvas(root);
  const make = (top) => ({
    x: rnd(0, W), y: top ? rnd(-H * .3, -6) : rnd(0, H), sz: rnd(1.2, 3.4),
    vy: rnd(22, 70), sway: rnd(8, 26), phase: rnd(0, 6.3), a: rnd(.35, .95),
  });
  for (let i = 0; i < Math.min(180, Math.round(W / 7)); i++) flakes.push(make(false));
  // Frost around the edges, drawn once: spikes reaching in from each side.
  const frost = document.createElement("canvas");
  const dpr = Math.min(devicePixelRatio || 1, 2);
  frost.width = W * dpr; frost.height = H * dpr;
  const fx = frost.getContext("2d");
  fx.scale(dpr, dpr);
  const spike = (x, y, dx, dy, len, w) => {
    fx.strokeStyle = `rgba(200, 235, 255, ${rnd(.1, .3).toFixed(2)})`;
    fx.lineWidth = w;
    fx.beginPath();
    fx.moveTo(x, y);
    fx.lineTo(x + dx * len, y + dy * len);
    fx.stroke();
    for (let i = 1; i <= 3; i++) {
      const t = i / 4, bx = x + dx * len * t, by = y + dy * len * t, bl = len * .28 * (1 - t);
      fx.beginPath(); fx.moveTo(bx, by); fx.lineTo(bx + dy * bl - dx * bl * .3, by + dx * bl - dy * bl * .3); fx.stroke();
      fx.beginPath(); fx.moveTo(bx, by); fx.lineTo(bx - dy * bl - dx * bl * .3, by - dx * bl - dy * bl * .3); fx.stroke();
    }
  };
  for (let x = 0; x < W; x += 26) {
    spike(x, 0, rnd(-.25, .25), 1, rnd(20, 70), rnd(.6, 1.4));
    spike(x, H, rnd(-.25, .25), -1, rnd(16, 56), rnd(.6, 1.4));
  }
  for (let y = 0; y < H; y += 26) {
    spike(0, y, 1, rnd(-.25, .25), rnd(18, 60), rnd(.6, 1.4));
    spike(W, y, -1, rnd(-.25, .25), rnd(18, 60), rnd(.6, 1.4));
  }

  // ---- intruders: yetis in the drift ----
  const S = Math.max(.75, Math.min(1.1, W / 1400));
  const watchIn = intruderWatch();
  const yetis = new Map();              // id -> { h, x, alarm, clearAt, out, walk, side, lastPrint }
  const prints = [];                    // { x, side, born }
  let redFrom = -1e9;
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const slotX = k => W < 700 ? W * .68 : W * .78 - k * 260;
  // One stood down keeps its place until it has gone, so none walks into it.
  const order = () => [...yetis.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const newYeti = h => ({ h, x: null, alarm: 0, clearAt: 0, out: false, walk: 0, side: 0, lastPrint: null });
  const footprint = (y, now) => {
    if (y.lastPrint != null && Math.abs(y.x - y.lastPrint) < 20 * S) return;
    y.lastPrint = y.x; y.side ^= 1;
    prints.push({ x: y.x, side: y.side, born: now });
    if (prints.length > 80) prints.shift();
  };
  function stepYetis(dt, now) {
    order().forEach((y, k) => {
      if (y.out) {
        y.x += 70 * S * dt; y.walk += dt; footprint(y, now);
        if (y.x > W + 80 * S) yetis.delete(y.h.id);
        return;
      }
      const want = slotX(Math.min(k, 1));
      if (y.x == null) y.x = want;
      const d = want - y.x;
      if (Math.abs(d) < 1) { y.walk = 0; return; }
      const sp = (y.alarm && now - y.alarm < 8000 ? 150 : 60) * S;
      y.x += Math.sign(d) * Math.min(Math.abs(d), sp * dt); y.walk += dt; footprint(y, now);
    });
  }
  // A yeti: shaggy and white, long arms, a slate-blue face. Its eyes burn red
  // while it's an intruder.
  const fur = Array.from({ length: 30 }, (_, i) => 1 + .08 * Math.sin(i * 7.3) + .07 * Math.sin(i * 3.1 + 1));
  function drawYeti(g, x, y, t, walking, red) {
    const s = S, sw = walking ? Math.sin(t * 8) : 0, bob = walking ? Math.abs(sw) * 3 * s : 0;
    g.save(); g.translate(x, y - bob);
    g.fillStyle = "#e4eef5"; g.strokeStyle = "#8fa9bf"; g.lineWidth = 1.2 * s;
    for (const [lx, ph] of [[-10, sw], [10, -sw]]) { g.beginPath(); g.ellipse((lx + ph * 4) * s, -10 * s, 8 * s, 12 * s, 0, 0, Math.PI * 2); g.fill(); g.stroke(); }
    for (const [ax, ph] of [[-23, -sw], [23, sw]]) {
      g.save(); g.translate(ax * s, -60 * s); g.rotate(ph * .35 + (ax < 0 ? .14 : -.14));
      g.beginPath(); g.ellipse(0, 21 * s, 8 * s, 25 * s, 0, 0, Math.PI * 2); g.fill(); g.stroke(); g.restore();
    }
    g.fillStyle = "#f1f7fb";
    g.beginPath();
    fur.forEach((r, i) => { const a = i / fur.length * Math.PI * 2, px = Math.cos(a) * 24 * s * r, py = -46 * s + Math.sin(a) * 36 * s * r; i ? g.lineTo(px, py) : g.moveTo(px, py); });
    g.closePath(); g.fill(); g.stroke();
    g.beginPath(); g.arc(0, -86 * s, 15 * s, 0, Math.PI * 2); g.fill(); g.stroke();
    g.fillStyle = "#56697d"; g.beginPath(); g.ellipse(0, -83 * s, 9.5 * s, 8.5 * s, 0, 0, Math.PI * 2); g.fill();
    g.fillStyle = red ? "#ff4a3a" : "#e6f1fa";
    if (red) { g.shadowColor = "#ff3a2a"; g.shadowBlur = 8 * s; }
    for (const ex of [-4, 4]) { g.beginPath(); g.arc(ex * s, -86 * s, 1.9 * s, 0, Math.PI * 2); g.fill(); }
    g.shadowBlur = 0;
    g.strokeStyle = "#1f2a36"; g.lineWidth = 1.5 * s;
    g.beginPath(); g.arc(0, -78.5 * s, 3.5 * s, 1.15 * Math.PI, 1.85 * Math.PI); g.stroke();
    g.restore();
  }
  // The frost at the edges, burning red while the alarm's on.
  function redFrost(k) {
    const d = 80;
    for (const [x0, y0, x1, y1, rx, ry, rw, rh] of [[0, 0, 0, d, 0, 0, W, d], [0, H, 0, H - d, 0, H - d, W, d], [0, 0, d, 0, 0, 0, d, H], [W, 0, W - d, 0, W - d, 0, d, H]]) {
      const gr = front.createLinearGradient(x0, y0, x1, y1);
      gr.addColorStop(0, `rgba(255, 64, 56, ${(.5 * k).toFixed(3)})`); gr.addColorStop(1, "rgba(255, 64, 56, 0)");
      front.fillStyle = gr; front.fillRect(rx, ry, rw, rh);
    }
  }
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    const now = performance.now();
    for (const h of added) if (!yetis.has(h.id)) yetis.set(h.id, newYeti(h));
    for (const h of held) { const y = yetis.get(h.id); if (y) y.h = h; }
    for (const h of cleared) {
      const y = yetis.get(h.id); if (!y || y.clearAt) continue;
      if (calm || document.hidden) { yetis.delete(h.id); continue; }
      // Stood down: it trudges back off into the snow, and a gust blows through.
      y.clearAt = now;
      festiveTimers.push(setTimeout(() => { y.out = true; if (!h.test) gust = 1; }, 2400));
    }
    room.style.height = [...yetis.values()].some(y => !y.clearAt) ? "124px" : "0";
    if (calm) draw(0);
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let y = yetis.get(h.id);
    if (!y) { y = newYeti(h); yetis.set(h.id, y); }
    if (calm) { draw(0); return; }
    if (document.hidden) return;
    const now = performance.now();
    Object.assign(y, { x: W + 60 * S, alarm: now, clearAt: 0, out: false, lastPrint: null });
    redFrom = now; gust = 1.4;
  };
  festiveHooks.rendered = syncIntruders;

  function step(dt, now) {
    stepYetis(dt, now);
    for (const f of flakes) {
      f.y += f.vy * dt * (1 + gust);
      f.x += gust * 90 * dt;
      if (f.y > H + 6 || f.x > W + 10) Object.assign(f, make(true));
    }
    if (gust > 0) gust = Math.max(0, gust - dt * .6);
  }
  function draw(now) {
    const t = (now || 0) / 1000;
    ctx.clearRect(0, 0, W, H);
    ctx.drawImage(frost, 0, 0, W, H);
    // The drift banks up against the bottom of the screen, in front of the page,
    // over the feet of any yeti standing in it.
    front.clearRect(0, 0, W, H);
    const list = order();
    list.forEach((y, k) => {
      if (k > (W < 700 ? 0 : 1) && !y.clearAt) return;
      if (calm || y.x == null) y.x = slotX(Math.min(k, 1));
      drawYeti(front, y.x, H - 6, t, y.walk > 0 && !calm, !y.clearAt);
    });
    front.fillStyle = "rgba(226, 242, 255, .72)";
    front.beginPath();
    front.moveTo(0, H);
    for (let x = 0; x <= W; x += 24) front.lineTo(x, H - 14 - Math.sin(x / 120) * 7);
    front.lineTo(W, H);
    front.closePath();
    front.fill();
    // Its footprints, filling in with snow.
    for (const pr of prints) {
      const k = Math.max(0, 1 - ((now || 0) - pr.born) / 40000);
      if (k <= 0) continue;
      front.fillStyle = `rgba(110, 140, 170, ${(.55 * k).toFixed(3)})`;
      front.beginPath(); front.ellipse(pr.x + (pr.side ? 5 : -5) * S, H - 7 + (pr.side ? -1.5 : 1.5), 5 * S, 2.2 * S, 0, 0, Math.PI * 2); front.fill();
    }
    list.forEach((y, k) => {
      if (y.x == null || y.out || (!y.clearAt && k > (W < 700 ? 0 : 1))) return;
      const n = now || 0;
      const state = y.clearAt ? "cleared" : y.alarm && n - y.alarm < 8000 ? "alarm" : "held";
      const fade = y.clearAt ? Math.max(0, 1 - Math.max(0, n - y.clearAt - 1500) / 900) : 1;
      intruderTag(front, y.x, H - 112 * S, y.h, { s: .85 * Math.max(S, .9), state, k: fade });
    });
    const age = (now || 0) - redFrom;
    if (!calm && age < 8000) redFrost((1 - age / 8000) * (.75 + .25 * Math.sin(age / 140)));
    for (const f of flakes) {
      ctx.globalAlpha = f.a;
      ctx.fillStyle = "#eaf6ff";
      ctx.beginPath();
      ctx.arc(f.x + Math.sin(t + f.phase) * f.sway, f.y, f.sz, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  if (calm) {
    // Reduced motion: settled snow and frost, holding still.
    for (const f of flakes) { f.y = Math.min(H - rnd(2, 30), f.y); f.sway = 0; }
    syncIntruders();
    return;
  }
  syncIntruders();
  c.start();
  festiveHooks.scanDone = () => { if (!document.hidden) gust = 1; };
}

BAMF.registerTheme("winter", ctx => buildWinter(ctx.root, ctx.switched));
})();
