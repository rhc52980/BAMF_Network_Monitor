// Ant Farm: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- Ant Farm ----
// A glass ant farm, the classic kind: green frame, a little farm on top, and
// sand behind the glass. The dashboard sits in the earth. Under the header is
// the surface, the barn and silo and a windmill turning, a sugar cube, and
// ants carrying crumbs home to the mound and down the tunnels beside the page.
// Along the bottom is the nest. The queen has the first chamber, and every
// device has one of its own off the main gallery: eggs in it while it's online
// and known, honey hanging in it while it's online and unknown (these are
// honeypot ants), and it's fallen in while it's offline. A watched device that
// drops flickers red and ants rush to it. A new device is a chamber being dug,
// sand flying; a scan sends a stream of ants out through every tunnel. With
// sound on there are crickets up top, a scratch of digging, and a patter for a
// scan.
function buildAntFarm(root) {
  const calm = calmMotion();
  let amb = null;
  const ambOn = on => { if (on && !amb && !document.hidden) amb = antCrickets(); if (!on && amb) { amb.stop(); amb = null; } };
  const soundOn = themeSoundButton("bamf-ant-sound", "Ant farm sounds on: click to mute",
    "Ant farm sounds off: click for crickets, digging and the patter of feet", on => { ambOn(on); if (on) antPatter(.5); });
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
  const bed = floorBand(root, Math.round(134 * S));
  const f = bed.ctx, BH = bed.H;
  const HB = document.querySelector("header")?.getBoundingClientRect().bottom || 58;
  const surf = HB + 60 * S;                 // where the grass meets the earth
  const moundX = W - 70 * S, foodX = W * .6, farmX = W * .74;
  const tunL = 12 * S, tunR = W - 12 * S;   // the tunnels down the page's sides
  const FRAME = "#3f8a3a";

  // Earth for the page to sit in: dark, so the labels on it read; and sand
  // for the nest, the pale kind an ant farm is filled with.
  function earth(g, x0, y0, w, h, sand) {
    const gr = g.createLinearGradient(0, y0, 0, y0 + h);
    if (sand) { gr.addColorStop(0, "#d2ae74"); gr.addColorStop(.5, "#c29c62"); gr.addColorStop(1, "#ad8751"); }
    else { gr.addColorStop(0, "#3e2c1d"); gr.addColorStop(.5, "#33241a"); gr.addColorStop(1, "#261a11"); }
    g.fillStyle = gr; g.fillRect(x0, y0, w, h);
    for (let i = 0; i < 6; i++) {
      const yy = y0 + h * (i + .5) / 6;
      g.strokeStyle = i % 2 ? (sand ? "rgba(255,245,220,.14)" : "rgba(255,220,170,.035)") : (sand ? "rgba(120,80,40,.12)" : "rgba(0,0,0,.12)");
      g.lineWidth = rnd(3, 9) * S;
      g.beginPath();
      for (let x = x0; x <= x0 + w; x += 20) { const v = yy + Math.sin(x / 90 + i * 2) * 5 * S; x === x0 ? g.moveTo(x, v) : g.lineTo(x, v); }
      g.stroke();
    }
    const grit = Math.round(w * h / ((sand ? 60 : 200) * S * S));
    for (let i = 0; i < grit; i++) {
      g.fillStyle = Math.random() < .5 ? (sand ? "rgba(255,250,235,.35)" : "rgba(255,230,190,.07)") : (sand ? "rgba(110,75,35,.25)" : "rgba(0,0,0,.18)");
      g.fillRect(x0 + Math.random() * w, y0 + Math.random() * h, rnd(.6, 1.5) * S, rnd(.6, 1.5) * S);
    }
    for (let i = 0; i < w * h / ((sand ? 5000 : 14000) * S * S); i++) {
      const x = x0 + Math.random() * w, y = y0 + Math.random() * h, r = rnd(1.5, sand ? 4 : 5) * S;
      g.fillStyle = sand ? pick(["#b08a5a", "#9c7a50", "#c9ad85", "#8d7d6c"]) : pick(["#4e4034", "#453629", "#57483a"]);
      g.beginPath(); g.ellipse(x, y, r * 1.3, r, rnd(0, 3), 0, Math.PI * 2); g.fill();
      g.fillStyle = sand ? "rgba(255,255,255,.3)" : "rgba(255,255,255,.06)";
      g.beginPath(); g.ellipse(x - r * .4, y - r * .35, r * .5, r * .3, 0, 0, Math.PI * 2); g.fill();
    }
  }
  // A tunnel carved through: a shadowed floor and a darker edge.
  function tunnel(g, pts, width, sand) {
    g.lineCap = "round"; g.lineJoin = "round";
    const layers = sand
      ? [["#7f6036", width + 2 * S], ["#a17d4b", width], ["#b08b58", width * .5]]
      : [["rgba(255,215,160,.10)", width + 3 * S], ["#1f140b", width], ["#3a2818", width * .55]];
    for (const [col, wd] of layers) {
      g.strokeStyle = col; g.lineWidth = wd; g.beginPath();
      pts.forEach((p, i) => i ? g.lineTo(p[0], p[1]) : g.moveTo(p[0], p[1])); g.stroke();
    }
  }
  const wiggle = (x0, y0, x1, y1, amp, n = 16) => Array.from({ length: n + 1 }, (_, i) => {
    const k = i / n; return [x0 + (x1 - x0) * k + (i && i < n ? Math.sin(k * 9 + x0) * amp : 0), y0 + (y1 - y0) * k];
  });

  // ---- the surface and the sides, drawn once ----
  const back = sceneLayer(W, H);
  const sidePathL = wiggle(tunL, surf, tunL, H + 10, 5 * S, 30), sidePathR = wiggle(tunR, surf, tunR, H + 10, 5 * S, 30);
  const mill = { x: farmX + 92 * S, y: surf - 44 * S };
  {
    const g = back.g;
    const sky = g.createLinearGradient(0, 0, 0, surf);
    sky.addColorStop(0, "#111a15"); sky.addColorStop(Math.max(0, (HB - 6) / surf), "#18251d"); sky.addColorStop(1, "#26372a");
    g.fillStyle = sky; g.fillRect(0, 0, W, surf);
    earth(g, 0, surf, W, H - surf, false);
    tunnel(g, sidePathL, 9 * S, false); tunnel(g, sidePathR, 9 * S, false);
    tunnel(g, [[moundX, surf - 2 * S], [moundX + 14 * S, surf + 30 * S], [tunR, surf + 60 * S]], 9 * S, false);
    // Roots from the grass down into the earth.
    g.strokeStyle = "rgba(200,170,120,.16)"; g.lineWidth = 1.1 * S;
    for (let i = 0; i < W / 80; i++) {
      let x = rnd(0, W), y = surf; g.beginPath(); g.moveTo(x, y);
      for (let k = 0; k < 6; k++) { x += rnd(-6, 6) * S; y += rnd(6, 14) * S; g.lineTo(x, y); }
      g.stroke();
    }
    // The farm on top, the way the toy has it: a barn, a silo, a fence; the
    // windmill's sails are drawn live so they turn.
    const bx = farmX, by = surf;
    g.fillStyle = "#8d2a22"; g.fillRect(bx, by - 22 * S, 34 * S, 22 * S);
    g.fillStyle = "#6e1f19"; g.beginPath(); g.moveTo(bx - 3 * S, by - 22 * S); g.lineTo(bx + 17 * S, by - 36 * S); g.lineTo(bx + 37 * S, by - 22 * S); g.closePath(); g.fill();
    g.strokeStyle = "#e8dcc4"; g.lineWidth = 1.2 * S; g.strokeRect(bx + 11 * S, by - 14 * S, 12 * S, 14 * S);
    g.beginPath(); g.moveTo(bx + 11 * S, by - 14 * S); g.lineTo(bx + 23 * S, by); g.moveTo(bx + 23 * S, by - 14 * S); g.lineTo(bx + 11 * S, by); g.stroke();
    g.fillStyle = "#ffd78a"; g.fillRect(bx + 14 * S, by - 30 * S, 6 * S, 4 * S);
    g.fillStyle = "#9aa3a8"; g.fillRect(bx + 38 * S, by - 40 * S, 12 * S, 40 * S);
    g.fillStyle = "#b8c0c4"; g.beginPath(); g.arc(bx + 44 * S, by - 40 * S, 6 * S, Math.PI, 0); g.fill();
    g.fillStyle = "rgba(0,0,0,.2)"; for (let k = 1; k < 5; k++) g.fillRect(bx + 38 * S, by - 40 * S + k * 8 * S, 12 * S, .8 * S);
    g.strokeStyle = "#6b5a44"; g.lineWidth = 3 * S; g.beginPath(); g.moveTo(mill.x - 6 * S, surf); g.lineTo(mill.x, mill.y); g.lineTo(mill.x + 6 * S, surf); g.stroke();
    g.strokeStyle = "#cdbb96"; g.lineWidth = 1.2 * S;
    for (let x = bx - 50 * S; x < bx - 4 * S; x += 9 * S) { g.beginPath(); g.moveTo(x, surf); g.lineTo(x, surf - 9 * S); g.stroke(); }
    g.beginPath(); g.moveTo(bx - 52 * S, surf - 6 * S); g.lineTo(bx - 4 * S, surf - 6 * S); g.moveTo(bx - 52 * S, surf - 3 * S); g.lineTo(bx - 4 * S, surf - 3 * S); g.stroke();
    // The mound over the entrance, and the sugar cube the foragers are after.
    g.fillStyle = "#6e4e30";
    g.beginPath(); g.ellipse(moundX, surf + 1 * S, 32 * S, 10 * S, 0, Math.PI, 0); g.fill();
    for (let i = 0; i < 40; i++) { g.fillStyle = pick(["#8d6640", "#5e4128", "#a07650"]); g.fillRect(moundX + rnd(-28, 28) * S, surf - rnd(0, 8) * S, 1.6 * S, 1.6 * S); }
    g.fillStyle = "#1a1109"; g.beginPath(); g.ellipse(moundX, surf - 6 * S, 4 * S, 2.2 * S, 0, 0, Math.PI * 2); g.fill();
    g.fillStyle = "#f3eee3"; g.fillRect(foodX - 9 * S, surf - 15 * S, 16 * S, 15 * S);
    g.fillStyle = "#d4cdbd"; g.fillRect(foodX + 7 * S, surf - 13 * S, 3 * S, 13 * S);
    g.fillStyle = "rgba(255,255,255,.8)"; for (let i = 0; i < 12; i++) g.fillRect(foodX + rnd(-8, 6) * S, surf - rnd(1, 14) * S, 1.2 * S, 1.2 * S);
    for (let i = 0; i < 8; i++) { g.fillStyle = "#f3eee3"; g.fillRect(foodX + rnd(-30, 30) * S, surf - 1.5 * S, 1.8 * S, 1.5 * S); }
    // The frame down each side of the glass.
    g.fillStyle = FRAME; g.fillRect(0, surf - 2 * S, 4 * S, H); g.fillRect(W - 4 * S, surf - 2 * S, 4 * S, H);
  }
  // Grass is drawn live so it moves in the breeze; it keeps clear of the farm.
  const clear = x => Math.abs(x - moundX) > 34 * S && Math.abs(x - foodX) > 12 * S && (x < farmX - 56 * S || x > mill.x + 10 * S);
  const blades = Array.from({ length: Math.round(W / (4 * S)) }, (_, i) => ({ x: i * 4 * S + rnd(-1.5, 1.5) * S, h: rnd(5, 14) * S, p: rnd(0, 6.3), c: pick(["#3f6a2e", "#4d7c38", "#335a26", "#5a8a42"]) }))
    .filter(b => clear(b.x));
  const flowers = Array.from({ length: Math.round(W / 240) }, () => ({ x: rnd(W * .32, W * .95), h: rnd(16, 24) * S, c: pick(["#e8c64a", "#efe9dc", "#d98aa8"]), p: rnd(0, 6) }))
    .filter(fl => clear(fl.x) && Math.abs(fl.x - foodX) > 24 * S);

  // ---- the nest along the bottom ----
  let list = [], cap = 0, cells = [], queen = null, gy = 0, entrance = null, store = null;
  const nest = sceneLayer(W, BH);
  const sandLayer = sceneLayer(W, BH);
  earth(sandLayer.g, 0, 0, W, BH, true);
  const digAt = {}, alarmUntil = {}, relitAt = {};
  let dugCount = -1;
  function layout() {
    cap = Math.max(24, Math.ceil((list.length + 6) / 8) * 8);     // room to grow before anything moves
    const cols = Math.ceil(cap / 2);
    const x0 = 112 * S, span = Math.max(120 * S, W - x0 - 96 * S);
    const sx = span / cols;
    const rx = Math.max(4 * S, Math.min(16 * S, sx * .38)), ry = Math.max(3.5 * S, Math.min(9.5 * S, rx * .62));
    gy = BH * .55;
    cells = Array.from({ length: cap }, (_, i) => {
      const col = Math.floor(i / 2), up = i % 2 === 0;
      const cx = x0 + sx * (col + .5) + (up ? -sx * .18 : sx * .18);
      const cy = up ? BH * .25 + Math.sin(col * 1.7) * 4 * S : BH * .83 + Math.cos(col * 1.3) * 4 * S;
      return { cx, cy, rx, ry, gx: cx };
    });
    entrance = { x: 26 * S, y: -4 };
    queen = { cx: 66 * S, cy: gy + 2 * S, rx: 27 * S, ry: 13 * S };
    store = { cx: W - 52 * S, cy: gy + 1 * S, rx: 24 * S, ry: 12 * S };
    dugCount = -1;
  }
  const galleryY = x => gy + Math.sin(x / (44 * S)) * 5 * S;
  // The sand with the tunnels in it, redrawn when a chamber is added.
  function staticNest() {
    const g = nest.g, n = Math.min(list.length, cells.length);
    g.clearRect(0, 0, W, BH);
    g.drawImage(sandLayer.cv, 0, 0, W, BH);
    tunnel(g, wiggle(entrance.x, entrance.y, 32 * S, gy, 4 * S, 10), 9 * S, true);
    const gal = []; for (let x = 32 * S; x <= store.cx; x += 6 * S) gal.push([x, galleryY(x)]);
    tunnel(g, gal, 10 * S, true);
    for (let i = 0; i < n; i++) { const cl = cells[i]; tunnel(g, [[cl.gx, galleryY(cl.gx)], [cl.cx + (cl.cy < gy ? 2 : -2) * S, cl.cy]], 6 * S, true); }
    const chamber = (ch) => {
      g.fillStyle = "#7f6036"; g.beginPath(); g.ellipse(ch.cx, ch.cy, ch.rx + 1.6 * S, ch.ry + 1.6 * S, 0, 0, Math.PI * 2); g.fill();
      g.fillStyle = "#8e6c3f"; g.beginPath(); g.ellipse(ch.cx, ch.cy, ch.rx, ch.ry, 0, 0, Math.PI * 2); g.fill();
      g.fillStyle = "#a07d4b"; g.beginPath(); g.ellipse(ch.cx, ch.cy + ch.ry * .45, ch.rx * .92, ch.ry * .45, 0, 0, Math.PI * 2); g.fill();
    };
    chamber(queen); chamber(store);
    // The store: seeds heaped up.
    for (let k = 0; k < 22; k++) {
      g.fillStyle = pick(["#5e3a1c", "#7a4e26", "#4a2e16"]);
      const a = rnd(0, Math.PI), r = rnd(0, 1);
      g.beginPath(); g.ellipse(store.cx + Math.cos(a) * store.rx * .8 * r, store.cy + store.ry * .5 - Math.sin(a) * store.ry * .9 * r, 2.2 * S, 1.4 * S, rnd(0, 3), 0, Math.PI * 2); g.fill();
    }
    // The glass: faint highlights across the front, and the frame along the top.
    for (const [x, w] of [[W * .1, 50], [W * .46, 20], [W * .8, 70]]) {
      const gl = g.createLinearGradient(x, 0, x + w * S, 0);
      gl.addColorStop(0, "rgba(255,255,255,0)"); gl.addColorStop(.5, "rgba(255,255,255,.12)"); gl.addColorStop(1, "rgba(255,255,255,0)");
      g.fillStyle = gl; g.save(); g.translate(x, 0); g.transform(1, 0, -.4, 1, 0, 0); g.fillRect(0, 0, w * S, BH); g.restore();
    }
    g.fillStyle = FRAME; g.fillRect(0, 0, W, 4 * S);
    g.fillStyle = "rgba(255,255,255,.18)"; g.fillRect(0, 0, W, 1 * S);
    g.fillStyle = FRAME; g.fillRect(0, 0, 4 * S, BH); g.fillRect(W - 4 * S, 0, 4 * S, BH);
    dugCount = n;
  }
  function refresh() {
    list = sceneHosts();
    if (!cells.length || list.length > cap) layout();
    if (dugCount !== Math.min(list.length, cells.length)) staticNest();
  }

  // Ants: each walks a path of points, there and back, at its own pace.
  let ants = [];
  const pathTo = i => {
    const cl = cells[i];
    const pts = wiggle(entrance.x, entrance.y, 32 * S, gy, 4 * S, 6);
    for (let x = 38 * S; x < cl.gx; x += 14 * S) pts.push([x, galleryY(x)]);
    pts.push([cl.gx, galleryY(cl.gx)], [cl.cx, cl.cy]);
    return pts;
  };
  const toRoom = rm => {
    const pts = wiggle(entrance.x, entrance.y, 32 * S, gy, 4 * S, 6);
    if (rm === store) for (let x = 38 * S; x < store.cx - store.rx; x += 14 * S) pts.push([x, galleryY(x)]);
    pts.push([rm.cx + (rm === store ? -6 : -8) * S, rm.cy + 2 * S]);
    return pts;
  };
  function withLengths(pts) {
    const len = [0];
    for (let i = 1; i < pts.length; i++) len.push(len[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
    return { pts, len, total: len[len.length - 1] };
  }
  function at(p, d) {
    let i = 1;
    while (i < p.len.length - 1 && p.len[i] < d) i++;
    const a = p.pts[i - 1], b = p.pts[i], seg = (p.len[i] - p.len[i - 1]) || 1, k = Math.max(0, Math.min(1, (d - p.len[i - 1]) / seg));
    return [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k, Math.atan2(b[1] - a[1], b[0] - a[0])];
  }
  function spawnNest(target, fast) {
    const now = performance.now();
    const live = list.map((h, i) => i).filter(i => i < cells.length && (!digAt[list[i].id] || now - digAt[list[i].id] > 4000));
    const roll = Math.random();
    const path = withLengths(target != null ? pathTo(target) : roll < .72 && live.length ? pathTo(pick(live)) : roll < .86 ? toRoom(store) : toRoom(queen));
    const carry = Math.random() < .55 ? pick(["egg", "crumb", "leaf", "seed"]) : null;
    ants.push({ path, d: 0, dir: 1, v: rnd(24, 36) * S * (fast ? 1.8 : 1), wait: 0, carry, p: rnd(0, 6), done: false });
  }
  // On the surface and down the sides: foragers between the sugar and the mound.
  let topAnts = [];
  function spawnTop() {
    const home = Math.random() < .6;
    const path = home
      ? withLengths([[foodX - 4 * S, surf - 2 * S], [moundX, surf - 3 * S], [moundX + 14 * S, surf + 30 * S], ...sidePathR.slice(4)])
      : withLengths([...sidePathL.slice().reverse(), [tunL, surf - 2 * S], [foodX - 12 * S, surf - 2 * S]]);
    topAnts.push({ path, d: home ? 0 : rnd(0, 200) * S, v: rnd(22, 32) * S, carry: home ? "crumb" : null, p: rnd(0, 6) });
  }

  function drawAnt(g, x, y, a, t, carry, sz = 1, col = "#2a1208", halo = true) {
    const s = S * sz;
    g.save(); g.translate(x, y); g.rotate(a);
    // A faint light round each ant, so it reads against dark earth as well as sand.
    if (halo) { g.fillStyle = "rgba(255,226,180,.22)"; g.beginPath(); g.ellipse(-.8 * s, 0, 8.5 * s, 4.6 * s, 0, 0, Math.PI * 2); g.fill(); }
    const gait = t / 60;
    g.strokeStyle = col; g.lineWidth = .9 * s; g.lineCap = "round";
    for (let i = 0; i < 3; i++) for (const side of [-1, 1]) {
      const sw = Math.sin(gait + i * 2.1 + (side > 0 ? Math.PI : 0)) * .45;
      const bx = (1 - i) * 1.6 * s, ang = side * (1.25 + (i - 1) * .5) + sw;
      g.beginPath(); g.moveTo(bx, 0);
      g.lineTo(bx + Math.cos(ang) * 2 * s, Math.sin(ang) * 2 * s);
      g.lineTo(bx + Math.cos(ang - side * .5) * 3.8 * s, Math.sin(ang - side * .5) * 3.8 * s + side * .3 * s);
      g.stroke();
    }
    g.fillStyle = col;
    g.beginPath(); g.ellipse(-4.6 * s, 0, 3.1 * s, 2.3 * s, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(-1.6 * s, 0, .8 * s, .6 * s, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(-.1 * s, 0, 1.8 * s, 1.1 * s, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(3 * s, 0, 1.7 * s, 1.5 * s, 0, 0, Math.PI * 2); g.fill();
    g.fillStyle = "rgba(255,190,150,.4)"; g.beginPath(); g.ellipse(-5.2 * s, -.9 * s, 1.4 * s, .6 * s, 0, 0, Math.PI * 2); g.fill();
    g.fillStyle = "rgba(255,190,150,.35)"; g.beginPath(); g.arc(3.3 * s, -.6 * s, .5 * s, 0, Math.PI * 2); g.fill();
    const wv = Math.sin(t / 110) * .3;
    g.beginPath(); g.moveTo(4.2 * s, -.6 * s); g.lineTo(5.6 * s, -2.4 * s); g.lineTo(7.4 * s, -2.2 * s + wv * s);
    g.moveTo(4.2 * s, .6 * s); g.lineTo(5.6 * s, 2.4 * s); g.lineTo(7.4 * s, 2.2 * s - wv * s); g.stroke();
    if (carry) {
      g.fillStyle = carry === "leaf" ? "#6fae4a" : carry === "seed" ? "#6a4220" : carry === "egg" ? "#fff8ea" : "#f6f1e6";
      g.beginPath();
      carry === "leaf" ? g.ellipse(6.6 * s, 0, 3.4 * s, 1.8 * s, .45, 0, Math.PI * 2) : g.ellipse(6 * s, 0, 2 * s, 1.5 * s, 0, 0, Math.PI * 2);
      g.fill();
    }
    g.restore();
  }

  let surge = 0, sand = [];
  function step(dt, now) {
    surge = Math.max(0, surge - dt * .18);
    const want = Math.min(48, 10 + Math.round(list.length / 3)) + Math.round(surge * 28);
    if (ants.length < want && Math.random() < dt * (3 + surge * 22)) spawnNest(undefined, surge > .3);
    for (const a of ants) {
      if (a.wait > 0) { a.wait -= dt; continue; }
      a.d += a.dir * a.v * dt;
      if (a.d >= a.path.total) {
        a.d = a.path.total; a.dir = -1; a.wait = rnd(.6, 2.2);
        a.carry = a.carry ? null : Math.random() < .5 ? pick(["egg", "seed"]) : null;   // drop it off, or pick something up
      }
      if (a.d <= 0 && a.dir < 0) a.done = true;
    }
    ants = ants.filter(a => !a.done);
    if (topAnts.length < 18 && Math.random() < dt * 1.8) spawnTop();
    for (const a of topAnts) a.d += a.v * dt;
    topAnts = topAnts.filter(a => a.d < a.path.total);
    for (const p of sand) { p.x += p.vx * dt; p.y += p.vy * dt; p.vy += 90 * S * dt; p.life -= dt; }
    sand = sand.filter(p => p.life > 0);
    list.forEach((h, i) => {
      const t0 = digAt[h.id];
      if (t0 && now - t0 < 4000 && cells[i] && Math.random() < dt * 14) {
        const cl = cells[i];
        sand.push({ x: cl.cx + rnd(-cl.rx, cl.rx) * .6, y: cl.cy, vx: rnd(-40, 40) * S, vy: rnd(-70, -30) * S, life: rnd(.4, .9), c: pick(["#b08a5a", "#e2c592", "#8e6c3f"]) });
      }
    });
  }

  function draw(now) {
    const t = now || 0;
    ctx.drawImage(back.cv, 0, 0, W, H);
    // The windmill turning, the grass and flowers in the breeze.
    ctx.save(); ctx.translate(mill.x, mill.y); ctx.rotate(t / 1600);
    ctx.fillStyle = "#e9dfc9";
    for (let k = 0; k < 4; k++) { ctx.rotate(Math.PI / 2); ctx.fillRect(1 * S, -1.6 * S, 15 * S, 3.2 * S); }
    ctx.restore();
    ctx.fillStyle = "#5a4a36"; ctx.beginPath(); ctx.arc(mill.x, mill.y, 2 * S, 0, Math.PI * 2); ctx.fill();
    for (const b of blades) {
      const lean = Math.sin(t / 1100 + b.p + b.x / 200) * 2.4 * S;
      ctx.strokeStyle = b.c; ctx.lineWidth = 1.5 * S;
      ctx.beginPath(); ctx.moveTo(b.x, surf + 1); ctx.quadraticCurveTo(b.x + lean * .4, surf - b.h * .5, b.x + lean, surf - b.h); ctx.stroke();
    }
    for (const fl of flowers) {
      const lean = Math.sin(t / 1300 + fl.p) * 2.4 * S, tx = fl.x + lean, ty = surf - fl.h;
      ctx.strokeStyle = "#3f6a2e"; ctx.lineWidth = 1.3 * S; ctx.beginPath(); ctx.moveTo(fl.x, surf); ctx.quadraticCurveTo(fl.x, surf - fl.h * .5, tx, ty); ctx.stroke();
      ctx.fillStyle = fl.c; for (let k = 0; k < 6; k++) { const an = k / 6 * Math.PI * 2; ctx.beginPath(); ctx.ellipse(tx + Math.cos(an) * 2.8 * S, ty + Math.sin(an) * 2.8 * S, 2.1 * S, 1.3 * S, an, 0, Math.PI * 2); ctx.fill(); }
      ctx.fillStyle = "#d8962a"; ctx.beginPath(); ctx.arc(tx, ty, 1.6 * S, 0, Math.PI * 2); ctx.fill();
    }
    for (const a of topAnts) { const [x, y, an] = at(a.path, a.d); drawAnt(ctx, x, y, an, t + a.p * 1000, a.carry, 1.15); }
    drawNest(t);
  }

  function drawNest(t) {
    const g = f;
    g.drawImage(nest.cv, 0, 0, W, BH);
    // The queen, big and slow, with eggs about her and a nurse in attendance.
    const q = queen;
    for (let k = 0; k < 8; k++) { g.fillStyle = "#fff8ea"; g.beginPath(); g.ellipse(q.cx + 9 * S + (k % 4) * 3.6 * S, q.cy + 5 * S - Math.floor(k / 4) * 3.2 * S, 1.7 * S, 1.2 * S, .3, 0, Math.PI * 2); g.fill(); }
    drawAnt(g, q.cx - 7 * S, q.cy + 1 * S, Math.sin(t / 2600) * .15, t / 4, null, 2, "#3a1508", false);
    drawAnt(g, q.cx + 15 * S, q.cy - 4 * S, Math.PI + Math.sin(t / 900) * .4, t / 2, "egg", 1.2, "#2a1208", false);
    // Every device's chamber, by how the device is doing.
    list.forEach((h, i) => {
      const cl = cells[i]; if (!cl) return;
      const st = sceneState(h), dug = digAt[h.id] ? Math.min(1, (t - digAt[h.id]) / 4000) : 1;
      const rx = cl.rx * dug, ry = cl.ry * dug;
      if (rx < .5) return;
      g.fillStyle = "#7f6036"; g.beginPath(); g.ellipse(cl.cx, cl.cy, rx + 1.6 * S, ry + 1.6 * S, 0, 0, Math.PI * 2); g.fill();
      g.fillStyle = "#8e6c3f"; g.beginPath(); g.ellipse(cl.cx, cl.cy, rx, ry, 0, 0, Math.PI * 2); g.fill();
      const alarm = h.watched && !h.online && (alarmUntil[h.id] || 0) > t;
      if (dug < 1) {
        // Still being dug: just the hole, growing.
      } else if (st === "off") {
        // Fallen in: loose sand heaped in it, nothing living there.
        g.fillStyle = "#b99463"; g.beginPath(); g.ellipse(cl.cx, cl.cy + ry * .25, rx * .96, ry * .78, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = "rgba(90,60,30,.35)"; for (let k = 0; k < 5; k++) g.fillRect(cl.cx + (k - 2.5) * rx * .35, cl.cy + ry * .1 + (k % 2) * 2 * S, 1.3 * S, 1.3 * S);
      } else if (st === "up") {
        g.fillStyle = "#a07d4b"; g.beginPath(); g.ellipse(cl.cx, cl.cy + ry * .45, rx * .92, ry * .45, 0, 0, Math.PI * 2); g.fill();
        const n = Math.max(2, Math.min(7, Math.round(rx / (2.2 * S))));
        for (let k = 0; k < n; k++) { g.fillStyle = "#fff8ea"; g.beginPath(); g.ellipse(cl.cx - rx * .6 + (k + .5) * rx * 1.2 / n, cl.cy + ry * .35 - (k % 2) * 2 * S, Math.max(.9, 1.5 * S), Math.max(.7, 1.05 * S), .3, 0, Math.PI * 2); g.fill(); }
        // A green lamp, the table's colour for online and known.
        g.fillStyle = "rgba(127,211,107,.35)"; g.beginPath(); g.arc(cl.cx, cl.cy - ry * .45, 3.4 * S, 0, Math.PI * 2); g.fill();
        g.fillStyle = "#6fd05a"; g.beginPath(); g.arc(cl.cx, cl.cy - ry * .45, 1.5 * S, 0, Math.PI * 2); g.fill();
      } else {
        // Honey, hanging in drops from the chamber's roof.
        g.fillStyle = "#a07d4b"; g.beginPath(); g.ellipse(cl.cx, cl.cy + ry * .45, rx * .92, ry * .45, 0, 0, Math.PI * 2); g.fill();
        const n = Math.max(1, Math.min(4, Math.round(rx / (3.5 * S))));
        for (let k = 0; k < n; k++) {
          const x = cl.cx - rx * .5 + (k + .5) * rx / n, r = Math.max(1.3, (2.3 + Math.sin(t / 700 + k + i) * .3) * S), y = cl.cy - ry * .1;
          g.fillStyle = "rgba(242,179,61,.35)"; g.beginPath(); g.arc(x, y, r * 1.9, 0, Math.PI * 2); g.fill();
          g.fillStyle = "#f0a82a"; g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
          g.fillStyle = "rgba(255,255,255,.65)"; g.fillRect(x - r * .45, y - r * .5, r * .5, r * .4);
        }
      }
      if (alarm && Math.sin(t / 150) > 0) {
        g.strokeStyle = "rgba(220,50,40,.95)"; g.lineWidth = 1.8 * S; g.beginPath(); g.ellipse(cl.cx, cl.cy, rx + 2.4 * S, ry + 2.4 * S, 0, 0, Math.PI * 2); g.stroke();
      }
      const lit = relitAt[h.id];
      if (lit && t - lit < 1500) {
        const k = (t - lit) / 1500;
        g.strokeStyle = `rgba(80,170,70,${.9 * (1 - k)})`; g.lineWidth = 1.5 * S;
        g.beginPath(); g.ellipse(cl.cx, cl.cy, rx + 6 * S * k, ry + 6 * S * k, 0, 0, Math.PI * 2); g.stroke();
      }
    });
    for (const p of sand) { g.fillStyle = p.c; g.fillRect(p.x, p.y, 1.8 * S, 1.8 * S); }
    for (const a of ants) { const [x, y, an] = at(a.path, a.d); drawAnt(g, x, y, a.dir > 0 ? an : an + Math.PI, t + a.p * 1000, a.carry, 1.25, "#2a1208", false); }
  }

  refresh();
  if (calm) {
    // Reduced motion: a still colony, the ants wherever they happen to be.
    for (let i = 0; i < 16; i++) { spawnNest(); const a = ants[ants.length - 1]; a.d = rnd(0, a.path.total); }
    for (let i = 0; i < 9; i++) { spawnTop(); const a = topAnts[topAnts.length - 1]; a.d = rnd(0, a.path.total * .9); }
    draw(0);
    festiveHooks.rendered = () => { const n = list.length; refresh(); if (n !== list.length) draw(0); };
    return;
  }
  festiveHooks.rendered = () => refresh();
  // Start with the colony already about its business, spread through the
  // tunnels, rather than all coming in at the entrance.
  for (let i = 0; i < Math.min(40, 10 + Math.round(list.length / 3)); i++) { spawnNest(); const a = ants[ants.length - 1]; a.d = rnd(0, a.path.total); a.dir = Math.random() < .5 ? 1 : -1; }
  for (let i = 0; i < 10; i++) { spawnTop(); const a = topAnts[topAnts.length - 1]; a.d = rnd(0, a.path.total * .9); }
  c.start();
  // A new device: its chamber is dug out, sand flying, ants hard at it.
  festiveHooks.newDevice = h => {
    if (!h) return;
    refresh();
    const i = list.findIndex(x => x.id === h.id);
    if (i < 0 || i >= cells.length || document.hidden) return;
    digAt[h.id] = performance.now();
    for (let k = 0; k < 4; k++) spawnNest(i, true);
    if (soundOn()) antDig();
  };
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    refresh();
    surge = 1;
    if (soundOn()) antPatter(.8);
  };
  festiveHooks.netChange = (off, back) => {
    refresh();
    if (document.hidden) return;
    const now = performance.now();
    for (const id of off) {
      alarmUntil[id] = now + 30e3;
      const i = list.findIndex(x => x.id === id);
      if (i >= 0 && i < cells.length && list[i].watched) for (let k = 0; k < 5; k++) spawnNest(i, true);
    }
    for (const id of back) { relitAt[id] = now; delete alarmUntil[id]; }
    if (off.some(id => hosts.find(h => h.id === id)?.watched) && soundOn()) antAlarm();
  };
}

// ---- The ant farm's sounds. Off unless switched on. ----
// Crickets up top: short chirps in threes, from two of them.
function antCrickets() {
  const ctx = waterCtx(); if (!ctx) return null;
  const chirp = (hz, when) => {
    for (let k = 0; k < 3; k++) {
      const t = when + k * .07, o = ctx.createOscillator(), g = ctx.createGain();
      o.type = "sine"; o.frequency.value = hz;
      g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.012, t + .01); g.gain.exponentialRampToValueAtTime(.0005, t + .05);
      o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .06);
    }
  };
  const a = setInterval(() => { if (!document.hidden) chirp(4300, ctx.currentTime + .02); }, 1300);
  const b = setInterval(() => { if (!document.hidden && Math.random() < .7) chirp(3900, ctx.currentTime + .4); }, 1700);
  return { stop() { clearInterval(a); clearInterval(b); } };
}

// Digging: soft scratches of high noise.
function antDig() {
  const ctx = waterCtx(); if (!ctx) return;
  for (let k = 0; k < 6; k++) {
    const t = ctx.currentTime + .02 + k * .18 + Math.random() * .06;
    const src = ctx.createBufferSource(), hp = ctx.createBiquadFilter(), g = ctx.createGain();
    src.buffer = noiseBuffer(ctx, .12, false); hp.type = "highpass"; hp.frequency.value = 2400;
    g.gain.setValueAtTime(.05, t); g.gain.exponentialRampToValueAtTime(.001, t + .1);
    src.connect(hp).connect(g).connect(ctx.destination); src.start(t); src.stop(t + .12);
  }
}

// A scan: a patter of many small feet.
function antPatter(vol = 1) {
  const ctx = waterCtx(); if (!ctx) return;
  for (let k = 0; k < 18; k++) {
    const t = ctx.currentTime + .02 + k * .045 + Math.random() * .03, o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "triangle"; o.frequency.value = rnd(1800, 2600);
    g.gain.setValueAtTime(.018 * vol, t); g.gain.exponentialRampToValueAtTime(.0005, t + .025);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .03);
  }
}

// A watched device dropping: quick clicks, like mandibles.
function antAlarm() {
  const ctx = waterCtx(); if (!ctx) return;
  for (let k = 0; k < 8; k++) {
    const t = ctx.currentTime + .02 + k * .07, o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "square"; o.frequency.value = k % 2 ? 900 : 1300;
    g.gain.setValueAtTime(.03, t); g.gain.exponentialRampToValueAtTime(.001, t + .03);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .04);
  }
}

BAMF.registerTheme("antfarm", ctx => buildAntFarm(ctx.root, ctx.switched));
})();
