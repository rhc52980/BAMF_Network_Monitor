// Summer: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Summer: a hot afternoon at the beach. The sun hangs over the open band
// under the header, clouds and gulls cross, and a hot-air balloon drifts over
// now and then. Along the bottom, on a floor of its own, is the shore: the sea
// with a sailing boat going along it, and the sand in front with a parasol
// planted in it, a towel, a sandcastle with its flag, a bucket and spade, a
// starfish, a beach ball and a crab minding its own business.
//
// A scan sends a bigger set of waves in with a surfer riding one; a new device
// bounces the beach ball.
// A new device nobody has marked known is an intruder: a shark. Its fin cuts
// in along the water, the sea flashes red, and the lifeguard runs up the red
// flag with a blast on the whistle; the fin circles offshore, tagged, the flag
// flying, until the device is marked known. Then it heads out to sea, the
// flag comes down, and the beach ball goes up.
function buildSummer(root) {
  const calm = calmMotion();
  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const S = Math.max(.7, Math.min(1.15, W / 1400));
  const bed = floorBand(root, Math.round(104 * S));
  const f = bed.ctx, BH = bed.H;
  const HB = document.querySelector("header")?.getBoundingClientRect().bottom || 58;
  const sunX = W * .86, sunY = HB + 46 * S;
  const clouds = Array.from({ length: 4 }, () => ({ x: rnd(0, W), y: HB + rnd(10, 80) * S, sc: rnd(.55, 1.1) * S, v: rnd(6, 14) }));
  const gulls = Array.from({ length: 3 }, (_, i) => ({ x: rnd(-W, W), y: HB + rnd(14, 80) * S, v: rnd(18, 36), sc: rnd(.8, 1.2) * S, t: i }));
  let balloon = null, balloonAt = 0, surge = 0, surfer = null, ball = { y: 0, vy: 0 }, crab = { x: W * .7, dir: 1 };
  const boat = { x: rnd(0, W), v: rnd(12, 20) * S };
  // The shore, in the floor's own coordinates: the sea across the top of it,
  // the sand below, and where the water meets the sand.
  const seaTop = 6 * S, shore = 44 * S;
  const umbX = W * .14, castleX = W * .36, towelX = umbX + 30 * S, bucketX = W * .44, starX = W * .58, ballX = W * .52;
  // ---- intruders: fins offshore ----
  const watchIn = intruderWatch();
  const fins = new Map();               // id -> { h, x, v, alarm, clearAt, out }
  const flagX = W * .92;
  let redFrom = 0, whistleAt = 0;
  const finBase = k => W * .64 - k * 230 * S;
  const circling = () => [...fins.values()].filter(fn => !fn.out).sort((a, b) => (b.alarm || 0) - (a.alarm || 0));

  function step(dt, now) {
    for (const cl of clouds) { cl.x += cl.v * dt; if (cl.x > W + 120 * cl.sc) { cl.x = -120 * cl.sc; } }
    for (const g of gulls) { g.x += g.v * dt; if (g.x > W + 30) { g.x = -30; g.y = HB + rnd(14, 80) * S; } }
    boat.x += boat.v * dt; if (boat.x > W + 90 * S) boat.x = -90 * S;
    if (!balloon && now > balloonAt) balloon = { x: -60, y: HB + rnd(20, 60) * S, v: rnd(10, 16) };
    if (balloon) { balloon.x += balloon.v * dt; if (balloon.x > W + 60) { balloon = null; balloonAt = now + rnd(60e3, 120e3); } }
    if (surge > 0) surge = Math.max(0, surge - dt * .4);
    if (surfer) { surfer.x += 150 * S * dt; if (surfer.x > W + 60) surfer = null; }
    if (ball.y > 0 || ball.vy !== 0) { ball.vy -= 600 * S * dt; ball.y += ball.vy * dt; if (ball.y <= 0) { ball.y = 0; ball.vy = Math.abs(ball.vy) > 60 * S ? -ball.vy * .55 : 0; } }
    crab.x += crab.dir * 14 * S * dt; if (crab.x > W * .95 || crab.x < W * .62) crab.dir *= -1;
    // The fins: in fast from the right, then circling their patch of sea.
    circling().forEach((fn, k) => {
      const base = finBase(Math.min(k, 1)), want = base + Math.sin(now / 2600 + k * 2) * 70 * S;
      if (fn.x == null) fn.x = want;
      const d = want - fn.x, sp = fn.alarm && now - fn.alarm < 4000 ? 260 : 40;
      fn.v = Math.sign(d) * Math.min(Math.abs(d), sp * S * dt) / Math.max(dt, .001);
      fn.x += fn.v * dt;
    });
    for (const [id, fn] of fins) if (fn.out) {
      fn.v = -160 * S; fn.x += fn.v * dt;
      if (fn.x < -80 * S) { fins.delete(id); if (!fn.h.test) ball.vy = 260 * S; }
    }
  }
  const cloud = (x, y, sc) => {
    ctx.fillStyle = "rgba(255,255,255,.78)";
    for (const [dx, dy, r] of [[0, 0, 24], [21, 6, 18], [-21, 6, 16], [8, -11, 16]]) { ctx.beginPath(); ctx.arc(x + dx * sc, y + dy * sc, r * sc, 0, Math.PI * 2); ctx.fill(); }
  };
  function drawBalloon(b, t) {
    const x = b.x, y = b.y + Math.sin(t / 1600) * 6 * S, r = 22 * S;
    const cols = ["#e5533d", "#f2c14e", "#3d8fd1", "#f2f2f2"];
    for (let i = 0; i < 4; i++) {
      ctx.fillStyle = cols[i];
      ctx.beginPath(); ctx.ellipse(x, y, r * (1 - i * .24), r * 1.15, 0, 0, Math.PI * 2); ctx.fill();
    }
    ctx.strokeStyle = "rgba(80,60,40,.7)"; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(x - 10 * S, y + r * 1.05); ctx.lineTo(x - 5 * S, y + r * 1.6); ctx.moveTo(x + 10 * S, y + r * 1.05); ctx.lineTo(x + 5 * S, y + r * 1.6); ctx.stroke();
    ctx.fillStyle = "#8a5a36"; ctx.fillRect(x - 6 * S, y + r * 1.6, 12 * S, 8 * S);
  }
  // A proper beach parasol: the pole planted in the sand, a striped canopy
  // shaped like one, with a scalloped rim and a finial on top.
  function drawParasol(g, x, t) {
    const topY = 6 * S, rim = 26 * S, halfW = 46 * S, lean = .12;
    g.save(); g.translate(x, BH - 8 * S); g.rotate(-lean);
    g.strokeStyle = "#7d5a3c"; g.lineWidth = 3.5 * S;
    g.beginPath(); g.moveTo(0, 0); g.lineTo(0, -(BH - 8 * S - topY)); g.stroke();
    const top = -(BH - 8 * S - topY), base = top + rim;
    for (let i = 0; i < 8; i++) {
      const x0 = -halfW + i * (halfW * 2 / 8), x1 = x0 + halfW * 2 / 8;
      g.fillStyle = i % 2 ? "#f4efe2" : "#e5533d";
      g.beginPath(); g.moveTo(0, top); g.lineTo(x0, base); g.quadraticCurveTo((x0 + x1) / 2, base + 7 * S, x1, base); g.closePath(); g.fill();
    }
    g.fillStyle = "#7d5a3c"; g.beginPath(); g.arc(0, top - 2 * S, 3 * S, 0, Math.PI * 2); g.fill();
    g.restore();
  }
  function drawBoat(g, t) {
    const bob = Math.sin(t / 900) * 2.4 * S, lean = Math.sin(t / 1500) * .05;
    const x = boat.x, y = shore - 12 * S + bob;
    g.save(); g.translate(x, y); g.rotate(lean);
    g.strokeStyle = "#5b4a3a"; g.lineWidth = 2 * S;
    g.beginPath(); g.moveTo(0, -2 * S); g.lineTo(0, -40 * S); g.stroke();
    g.fillStyle = "#fbfaf5"; g.beginPath(); g.moveTo(-2 * S, -38 * S); g.lineTo(-2 * S, -6 * S); g.lineTo(-24 * S, -6 * S); g.closePath(); g.fill();
    g.fillStyle = "#e5533d"; g.beginPath(); g.moveTo(2 * S, -36 * S); g.lineTo(18 * S, -6 * S); g.lineTo(2 * S, -6 * S); g.closePath(); g.fill();
    g.fillStyle = "#2f4f66"; g.beginPath(); g.moveTo(-26 * S, -4 * S); g.lineTo(28 * S, -4 * S); g.lineTo(19 * S, 6 * S); g.lineTo(-18 * S, 6 * S); g.closePath(); g.fill();
    g.fillStyle = "#f4efe2"; g.fillRect(-18 * S, -2 * S, 36 * S, 2 * S);
    g.restore();
  }

  function draw(now) {
    const t = now || 0;
    // The sky: warm, and a shade softer than it was.
    const sky = ctx.createLinearGradient(0, 0, 0, H);
    sky.addColorStop(0, "#cfe3f1"); sky.addColorStop(.6, "#dde9f0"); sky.addColorStop(1, "#e8e4d8");
    ctx.fillStyle = sky; ctx.fillRect(0, 0, W, H);
    const g = ctx.createRadialGradient(sunX, sunY, 4, sunX, sunY, 110 * S);
    g.addColorStop(0, "rgba(255, 206, 110, .8)"); g.addColorStop(.35, "rgba(255, 186, 90, .26)"); g.addColorStop(1, "rgba(255, 186, 90, 0)");
    ctx.fillStyle = g; ctx.fillRect(sunX - 120 * S, sunY - 120 * S, 240 * S, 240 * S);
    ctx.save(); ctx.translate(sunX, sunY); ctx.rotate(t / 1000 * .06);
    ctx.strokeStyle = "rgba(245, 180, 90, .4)"; ctx.lineWidth = 3 * S;
    for (let i = 0; i < 12; i++) { ctx.rotate(Math.PI / 6); ctx.beginPath(); ctx.moveTo(38 * S, 0); ctx.lineTo(56 * S, 0); ctx.stroke(); }
    ctx.restore();
    ctx.fillStyle = "#f7c257"; ctx.beginPath(); ctx.arc(sunX, sunY, 28 * S, 0, Math.PI * 2); ctx.fill();
    for (const cl of clouds) cloud(cl.x, cl.y, cl.sc);
    if (balloon) drawBalloon(balloon, t);
    for (const gl of gulls) {
      const flap = Math.sin(t / 250 + gl.t) * 4 * gl.sc;
      ctx.strokeStyle = "rgba(70, 90, 105, .55)"; ctx.lineWidth = 2 * gl.sc;
      ctx.beginPath(); ctx.moveTo(gl.x - 10 * gl.sc, gl.y + flap); ctx.quadraticCurveTo(gl.x, gl.y - 5 * gl.sc, gl.x + 10 * gl.sc, gl.y + flap); ctx.stroke();
    }

    // The shore, on its own floor: the sea, the boat on it, then the sand.
    const sea = f.createLinearGradient(0, 0, 0, shore + 10 * S);
    sea.addColorStop(0, "#5fa9cf"); sea.addColorStop(1, "#2f86b3");
    f.fillStyle = sea; f.fillRect(0, 0, W, shore + 12 * S);
    f.fillStyle = "#cfe3f1"; f.fillRect(0, 0, W, seaTop);
    drawBoat(f, t);
    const amp = (3 + surge * 7) * S;
    for (const [i, col] of [[0, "rgba(255,255,255,.35)"], [1, "rgba(255,255,255,.5)"]]) {
      f.strokeStyle = col; f.lineWidth = 2 * S; f.beginPath();
      for (let x = 0; x <= W; x += 10) {
        const y = seaTop + (12 + i * 14) * S + Math.sin(x / 60 + t / 700 * (1 + i * .3) + i) * amp;
        x ? f.lineTo(x, y) : f.moveTo(x, y);
      }
      f.stroke();
    }
    if (surfer) {
      const y = shore - 16 * S + Math.sin(surfer.x / 40) * 3 * S;
      f.fillStyle = "#f2c14e"; f.beginPath(); f.ellipse(surfer.x, y + 10 * S, 20 * S, 3 * S, -.1, 0, Math.PI * 2); f.fill();
      f.strokeStyle = "#3a2a20"; f.lineWidth = 3 * S;
      f.beginPath(); f.moveTo(surfer.x - 5 * S, y + 8 * S); f.lineTo(surfer.x, y - 6 * S); f.lineTo(surfer.x + 5 * S, y + 8 * S);
      f.moveTo(surfer.x, y - 4 * S); f.lineTo(surfer.x - 10 * S, y - 10 * S); f.moveTo(surfer.x, y - 4 * S); f.lineTo(surfer.x + 10 * S, y - 2 * S); f.stroke();
      f.fillStyle = "#3a2a20"; f.beginPath(); f.arc(surfer.x, y - 10 * S, 3.5 * S, 0, Math.PI * 2); f.fill();
    }
    // The fins, cutting the water, with a wake behind each.
    drawFins(t);
    // Foam where the water runs up the sand, then the sand.
    const run = Math.sin(t / 1300) * 6 * S + surge * 8 * S;
    f.fillStyle = "#e9dcc0";
    f.beginPath(); f.moveTo(0, BH);
    for (let x = 0; x <= W; x += 14) f.lineTo(x, shore + run + Math.sin(x / 45 + t / 900) * 3 * S);
    f.lineTo(W, BH); f.closePath(); f.fill();
    f.strokeStyle = "rgba(255,255,255,.8)"; f.lineWidth = 2 * S; f.beginPath();
    for (let x = 0; x <= W; x += 14) { const y = shore + run + Math.sin(x / 45 + t / 900) * 3 * S; x ? f.lineTo(x, y) : f.moveTo(x, y); }
    f.stroke();
    const sand = f.createLinearGradient(0, shore, 0, BH);
    sand.addColorStop(0, "rgba(233,220,192,0)"); sand.addColorStop(1, "rgba(206,186,146,.9)");
    f.fillStyle = sand; f.fillRect(0, shore, W, BH - shore);
    // What's on the sand.
    const sy = BH - 10 * S;
    f.fillStyle = "#3d8fd1"; f.fillRect(towelX, sy - 6 * S, 70 * S, 12 * S);
    f.fillStyle = "#f4efe2"; for (let i = 0; i < 4; i++) f.fillRect(towelX + 8 * S + i * 16 * S, sy - 6 * S, 6 * S, 12 * S);
    drawParasol(f, umbX, t);
    // The sandcastle, with its flag.
    f.fillStyle = "#d8bf8a";
    f.fillRect(castleX, sy - 18 * S, 44 * S, 20 * S);
    f.fillRect(castleX + 12 * S, sy - 32 * S, 20 * S, 16 * S);
    for (let i = 0; i < 4; i++) f.fillRect(castleX + i * 12 * S, sy - 22 * S, 8 * S, 5 * S);
    f.strokeStyle = "#7d5a3c"; f.lineWidth = 1.5 * S; f.beginPath(); f.moveTo(castleX + 22 * S, sy - 32 * S); f.lineTo(castleX + 22 * S, sy - 48 * S); f.stroke();
    f.fillStyle = "#e5533d"; f.beginPath(); f.moveTo(castleX + 22 * S, sy - 48 * S); f.lineTo(castleX + 22 * S + (12 + Math.sin(t / 300) * 2) * S, sy - 44 * S); f.lineTo(castleX + 22 * S, sy - 40 * S); f.fill();
    // Bucket and spade.
    f.fillStyle = "#f2c14e"; f.beginPath(); f.moveTo(bucketX, sy - 16 * S); f.lineTo(bucketX + 18 * S, sy - 16 * S); f.lineTo(bucketX + 15 * S, sy + 2 * S); f.lineTo(bucketX + 3 * S, sy + 2 * S); f.closePath(); f.fill();
    f.strokeStyle = "#3d8fd1"; f.lineWidth = 2.5 * S; f.beginPath(); f.moveTo(bucketX + 24 * S, sy + 2 * S); f.lineTo(bucketX + 34 * S, sy - 20 * S); f.stroke();
    // A palm, leaning out over the sand, its fronds stirring.
    const px = W * .8, lean = 26 * S, topX = px - lean, topY = 12 * S;
    f.strokeStyle = "#8a6a45"; f.lineWidth = 6 * S; f.lineCap = "round";
    f.beginPath(); f.moveTo(px, sy + 2 * S); f.quadraticCurveTo(px - 4 * S, (sy + topY) / 2, topX, topY); f.stroke();
    f.strokeStyle = "rgba(90,62,38,.5)"; f.lineWidth = 1.2 * S;
    for (let i = 1; i < 7; i++) {
      const u = i / 7, bx = (1 - u) * (1 - u) * px + 2 * u * (1 - u) * (px - 4 * S) + u * u * topX, byy = (1 - u) * (1 - u) * (sy + 2 * S) + 2 * u * (1 - u) * (sy + topY) / 2 + u * u * topY;
      f.beginPath(); f.moveTo(bx - 3 * S, byy); f.lineTo(bx + 3 * S, byy - 2 * S); f.stroke();
    }
    f.strokeStyle = "#3f8a4a"; f.lineWidth = 4 * S;
    for (const [a, len] of [[-2.6, 40], [-2.1, 34], [-1.2, 30], [-.5, 38], [.1, 34], [2.9, 32]]) {
      const w = a + Math.sin(t / 1100 + a) * .06;
      const ex = topX + Math.cos(w) * len * S, ey = topY + Math.sin(w) * len * S * .6 + 12 * S;
      f.beginPath(); f.moveTo(topX, topY); f.quadraticCurveTo((topX + ex) / 2, Math.min(topY, ey) - 6 * S, ex, ey); f.stroke();
    }
    f.fillStyle = "#6b4a2a";
    f.beginPath(); f.arc(topX - 3 * S, topY + 4 * S, 3 * S, 0, Math.PI * 2); f.arc(topX + 3 * S, topY + 5 * S, 3 * S, 0, Math.PI * 2); f.fill();
    f.lineCap = "butt";
    // The starfish, and the ball.
    f.save(); f.translate(starX, sy - 2 * S); f.fillStyle = "#e5853d"; f.beginPath();
    for (let i = 0; i < 10; i++) { const a = -Math.PI / 2 + i * Math.PI / 5, r = (i % 2 ? 4 : 10) * S; f.lineTo(Math.cos(a) * r, Math.sin(a) * r); }
    f.closePath(); f.fill(); f.restore();
    const by = sy - 9 * S - ball.y;
    for (const [i, col] of [[0, "#e5533d"], [1, "#f4efe2"], [2, "#3d8fd1"], [3, "#f2c14e"]]) {
      f.fillStyle = col; f.beginPath(); f.moveTo(ballX, by); f.arc(ballX, by, 9 * S, i * Math.PI / 2 + t / 800 * (ball.vy ? 4 : 0), (i + 1) * Math.PI / 2 + t / 800 * (ball.vy ? 4 : 0)); f.closePath(); f.fill();
    }
    // The crab, sidling up and down its bit of beach.
    const cx = crab.x, cy = sy - 2 * S, legs = Math.sin(t / 90) * 2 * S;
    f.strokeStyle = "#c2462b"; f.lineWidth = 1.6 * S;
    for (const d of [-1, 1]) for (let k = 0; k < 3; k++) { f.beginPath(); f.moveTo(cx + d * 4 * S, cy); f.lineTo(cx + d * (9 + k * 3) * S, cy + 5 * S + (k % 2 ? legs : -legs)); f.stroke(); }
    f.fillStyle = "#e0563b"; f.beginPath(); f.ellipse(cx, cy - 2 * S, 9 * S, 6 * S, 0, 0, Math.PI * 2); f.fill();
    f.beginPath(); f.arc(cx - 11 * S, cy - 6 * S, 3.5 * S, 0, Math.PI * 2); f.arc(cx + 11 * S, cy - 6 * S, 3.5 * S, 0, Math.PI * 2); f.fill();
    drawFlagAndTags(t);
  }
  function drawFins(t) {
    const red = redFrom && t - redFrom < 6500 ? 1 - (t - redFrom) / 6500 : 0;
    if (red > 0) { f.fillStyle = `rgba(255, 45, 35, ${((.14 + .14 * Math.sin((t - redFrom) / 220)) * red).toFixed(3)})`; f.fillRect(0, seaTop, W, shore + 10 * S - seaTop); }
    const list = circling();
    list.forEach((fn, i) => { if (fn.x == null) fn.x = finBase(Math.min(i, 1)); });
    for (const fn of fins.values()) {
      if (fn.x == null || (!fn.out && list.indexOf(fn) > 1)) continue;
      const dir = fn.v < 0 ? -1 : 1, y = shore - 14 * S + Math.sin(t / 500 + fn.x / 90) * 1.2 * S;
      // The wake: white streaks trailing behind.
      f.strokeStyle = "rgba(255, 255, 255, .7)"; f.lineWidth = 1.6 * S;
      for (let k = 1; k <= 3; k++) { f.beginPath(); f.moveTo(fn.x - dir * (8 + k * 9) * S, y + 1 * S); f.lineTo(fn.x - dir * (14 + k * 9) * S, y + (1 + k) * S); f.stroke(); }
      f.fillStyle = "#3d4a55";
      f.beginPath(); f.moveTo(fn.x - dir * 12 * S, y); f.quadraticCurveTo(fn.x - dir * 4 * S, y - 10 * S, fn.x + dir * 4 * S, y - 22 * S);
      f.quadraticCurveTo(fn.x + dir * 6 * S, y - 10 * S, fn.x + dir * 12 * S, y); f.closePath(); f.fill();
      f.fillStyle = "rgba(255, 255, 255, .5)"; f.fillRect(fn.x - 13 * S, y, 26 * S, 1.4 * S);
    }
  }
  // The lifeguard's red flag, up while a shark's about, and each fin's tag on
  // the sand below it.
  function drawFlagAndTags(t) {
    const list = circling();
    if (list.length) {
      const top = 4 * S, base = BH - 8 * S;
      f.strokeStyle = "#e9e4d8"; f.lineWidth = 2.5 * S;
      f.beginPath(); f.moveTo(flagX, base); f.lineTo(flagX, top); f.stroke();
      const wave = calm ? 0 : Math.sin(t / 180) * 3 * S;
      f.fillStyle = "#e0301e";
      f.beginPath(); f.moveTo(flagX, top); f.quadraticCurveTo(flagX + 14 * S, top + 2 * S + wave, flagX + 28 * S, top + wave * .6);
      f.lineTo(flagX + 28 * S, top + 18 * S + wave * .6); f.quadraticCurveTo(flagX + 14 * S, top + 20 * S + wave, flagX, top + 18 * S); f.closePath(); f.fill();
      if (t < whistleAt) {
        f.font = `700 ${12 * S}px "Space Grotesk", sans-serif`; f.textAlign = "right"; f.fillStyle = "#ffffff";
        f.strokeStyle = "rgba(160, 20, 10, .9)"; f.lineWidth = 3 * S;
        f.strokeText("TWEEEET! OUT OF THE WATER!", flagX - 8 * S, 22 * S); f.fillText("TWEEEET! OUT OF THE WATER!", flagX - 8 * S, 22 * S);
        f.textAlign = "left";
      }
    }
    // Each tag stands on the sand below its fin's patch of sea, so two fins
    // circling close never put one tag over the other.
    for (const fn of fins.values()) {
      const i = list.indexOf(fn);
      if (fn.x == null || (!fn.clearAt && i > 1)) continue;
      const state = fn.clearAt ? "cleared" : fn.alarm && t - fn.alarm < 8000 ? "alarm" : "held";
      const k = fn.clearAt ? Math.max(0, 1 - Math.max(0, t - fn.clearAt - 1500) / 1000) : 1;
      const x = fn.clearAt ? (fn.tagX ?? fn.x) : (fn.tagX = finBase(Math.min(i, 1)));
      intruderTag(f, x, BH - 4 * S, fn.h, { s: .8 * S, state, k });
    }
  }
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    const now = performance.now();
    for (const h of added) if (!fins.has(h.id)) fins.set(h.id, { h, x: null, v: -1, alarm: 0, clearAt: 0, out: false });
    for (const h of held) { const fn = fins.get(h.id); if (fn) fn.h = h; }
    for (const h of cleared) {
      const fn = fins.get(h.id); if (!fn || fn.clearAt) continue;
      if (calm || document.hidden) { fins.delete(h.id); continue; }
      fn.clearAt = now;
      festiveTimers.push(setTimeout(() => { fn.out = true; }, 2400));
    }
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let fn = fins.get(h.id);
    if (!fn) { fn = { h, x: null, v: -1, alarm: 0, clearAt: 0, out: false }; fins.set(h.id, fn); }
    if (calm) { draw(0); return; }
    if (document.hidden) return;
    const now = performance.now();
    Object.assign(fn, { x: W + 40 * S, alarm: now, clearAt: 0, out: false });
    redFrom = now; whistleAt = now + 4500; surge = 1;
  };
  festiveHooks.rendered = () => { syncIntruders(); if (calm) draw(0); };
  syncIntruders();

  if (calm) { draw(0); return; }
  balloonAt = performance.now() + rnd(15e3, 30e3);
  c.start();
  festiveHooks.scanDone = () => { if (document.hidden) return; surge = 1; if (!surfer) surfer = { x: -40 }; };
}

BAMF.registerTheme("summer", ctx => buildSummer(ctx.root, ctx.switched));
})();
