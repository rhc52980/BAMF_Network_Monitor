// Spring: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Spring: a soft morning. Clouds and birds cross, a cherry tree in blossom
// leans in at the top right with a kite flying beside it, and blossom drifts
// down over everything. Along the bottom is a meadow on a floor of its own:
// grass, tulips and daffodils, a picket fence with a birdhouse, a snail on
// its way somewhere, butterflies, and now and then a rabbit.
//
// A scan shakes a fresh handful of blossom off the tree and sends the rabbit
// hopping across; a new device comes up as a flower in the meadow.
function buildSpring(root) {
  const calm = calmMotion();
  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const S = Math.max(.7, Math.min(1.15, W / 1400));
  const bed = floorBand(root, Math.round(92 * S));
  const f = bed.ctx, BH = bed.H;
  const HB = document.querySelector("header")?.getBoundingClientRect().bottom || 58;

  // The tree at the top right, in the open band under the header.
  const treeX = W * .9, treeY = HB + 70 * S;
  const canopy = Array.from({ length: 26 }, (_, i) => ({
    x: treeX + (Math.cos(i * 2.3) * 120 - 30) * S * (.4 + (i % 5) * .15), y: treeY - 20 * S + Math.sin(i * 1.7) * 38 * S,
    r: (18 + (i * 7) % 16) * S, col: ["#f5b8cc", "#f8c9d8", "#efa6bf", "#fbd9e4"][i % 4] }));

  const PETAL = ["#eea5bf", "#f6c8d8", "#fbeef3", "#e9b3c9"];
  const make = top => ({
    x: top ? rnd(treeX - 260 * S, W) : rnd(0, W), y: top ? rnd(treeY - 20, treeY + 40) : rnd(0, H), sz: rnd(4, 7) * S,
    r: rnd(0, 6.3), spin: rnd(-1.2, 1.2), vy: rnd(16, 36), vx: rnd(-26, -6), sway: rnd(12, 32), phase: rnd(0, 6.3),
    col: PETAL[Math.floor(Math.random() * PETAL.length)],
  });
  const petals = Array.from({ length: Math.min(70, Math.round(W / 20)) }, () => make(false));
  const clouds = Array.from({ length: 3 }, (_, i) => ({ x: rnd(0, W), y: HB + rnd(14, 60) * S, sc: rnd(.55, .95) * S, v: rnd(5, 11), i }));
  const birds = Array.from({ length: 4 }, (_, i) => ({ x: rnd(-W, W), y: HB + rnd(12, 70) * S, v: rnd(26, 48), sc: rnd(.7, 1.1) * S, t: i }));
  const kite = { x: W * .66, y: HB + 42 * S };

  // The meadow.
  const blooms = [];
  for (let x = rnd(10, 50) * S; x < W - 10; x += rnd(40, 110) * S) {
    if (x > W * .28 && x < W * .28 + 150 * S) continue;          // the fence
    blooms.push({ x, h: rnd(26, 46) * S, kind: Math.random() < .55 ? "tulip" : "daff", phase: rnd(0, 6.3), grow: 1,
      col: pick(["#e0648c", "#d9534f", "#f2c14e", "#f7f3ea", "#a974c4"]) });
  }
  const grass = [];
  for (let x = 0; x < W; x += 6 * S) grass.push({ x, h: rnd(8, 20) * S, phase: rnd(0, 6.3), dark: Math.random() < .4 });
  const fenceX = W * .28, groundY = BH - 22 * S;
  let rabbit = null, rabbitAt = 0, snail = { x: W * .55 }, bfly = [], bflyAt = 0, birdIn = 0, birdAt = 0;

  function drawBloom(g, b, t) {
    const hh = b.h * Math.min(1, b.grow);
    const sway = Math.sin(t / 1400 + b.phase) * 4 * S;
    const baseY = BH - 6 * S, tipX = b.x + sway, tipY = baseY - hh;
    g.strokeStyle = "#4e7f3f"; g.lineWidth = 2.6 * S;
    g.beginPath(); g.moveTo(b.x, baseY); g.quadraticCurveTo(b.x + sway * .4, baseY - hh * .5, tipX, tipY); g.stroke();
    g.fillStyle = "#5a8c46";
    g.beginPath(); g.ellipse(b.x + sway * .3 - 6 * S, baseY - hh * .45, 7 * S, 3 * S, -.5, 0, Math.PI * 2); g.fill();
    if (b.grow < .7) return;
    g.fillStyle = b.col;
    if (b.kind === "tulip") {
      g.beginPath();
      g.moveTo(tipX - 6 * S, tipY + 2 * S);
      g.quadraticCurveTo(tipX, tipY - 14 * S, tipX + 6 * S, tipY + 2 * S);
      g.quadraticCurveTo(tipX, tipY + 8 * S, tipX - 6 * S, tipY + 2 * S);
      g.fill();
    } else {
      for (let i = 0; i < 6; i++) {
        const a = i / 6 * Math.PI * 2;
        g.beginPath(); g.ellipse(tipX + Math.cos(a) * 5 * S, tipY + Math.sin(a) * 5 * S, 4.4 * S, 3 * S, a, 0, Math.PI * 2); g.fill();
      }
      g.fillStyle = "#e89a1c";
      g.beginPath(); g.arc(tipX, tipY, 3 * S, 0, Math.PI * 2); g.fill();
    }
  }
  function drawFence(g, t) {
    const x0 = fenceX, pw = 12 * S, gap = 8 * S, n = 7;
    g.fillStyle = "#efe9dc"; g.strokeStyle = "#b9ae98"; g.lineWidth = 1;
    g.fillRect(x0 - 4 * S, groundY - 30 * S, n * (pw + gap) + 4 * S, 5 * S);
    g.fillRect(x0 - 4 * S, groundY - 14 * S, n * (pw + gap) + 4 * S, 5 * S);
    for (let i = 0; i < n; i++) {
      const x = x0 + i * (pw + gap);
      g.beginPath(); g.moveTo(x, groundY + 6 * S); g.lineTo(x, groundY - 40 * S); g.lineTo(x + pw / 2, groundY - 46 * S); g.lineTo(x + pw, groundY - 40 * S); g.lineTo(x + pw, groundY + 6 * S); g.closePath();
      g.fill(); g.stroke();
    }
    // The birdhouse on a post at the end, and its tenant looking out.
    const bx = x0 + n * (pw + gap) + 18 * S, by = groundY - 56 * S;
    g.fillStyle = "#8a6a4a"; g.fillRect(bx - 2 * S, by + 16 * S, 4 * S, 46 * S);
    g.fillStyle = "#c97b4a"; g.fillRect(bx - 12 * S, by - 2 * S, 24 * S, 20 * S);
    g.fillStyle = "#8e4a2e"; g.beginPath(); g.moveTo(bx - 15 * S, by); g.lineTo(bx, by - 14 * S); g.lineTo(bx + 15 * S, by); g.closePath(); g.fill();
    g.fillStyle = "#3b2618"; g.beginPath(); g.arc(bx, by + 8 * S, 4.2 * S, 0, Math.PI * 2); g.fill();
    if (birdIn > 0) {
      g.fillStyle = "#4f7fb8"; g.beginPath(); g.arc(bx, by + 7 * S, 3.6 * S, 0, Math.PI * 2); g.fill();
      g.fillStyle = "#f2b33d"; g.beginPath(); g.moveTo(bx + 3 * S, by + 7 * S); g.lineTo(bx + 6 * S, by + 8 * S); g.lineTo(bx + 3 * S, by + 9 * S); g.fill();
    }
  }
  function drawRabbit(g, r, t) {
    const hop = Math.abs(Math.sin(r.t * 5)) * 16 * S;
    const x = r.x, y = groundY + 2 * S - hop;
    g.save(); g.translate(x, y); g.scale(r.dir, 1);
    g.fillStyle = "#b8a48e";
    g.beginPath(); g.ellipse(0, -9 * S, 12 * S, 8 * S, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.arc(10 * S, -16 * S, 6 * S, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(9 * S, -27 * S, 2.4 * S, 8 * S, -.2, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(13 * S, -27 * S, 2.4 * S, 8 * S, .2, 0, Math.PI * 2); g.fill();
    g.fillStyle = "#fbf7f0"; g.beginPath(); g.arc(-12 * S, -10 * S, 4 * S, 0, Math.PI * 2); g.fill();
    g.fillStyle = "#2b2118"; g.beginPath(); g.arc(12.5 * S, -17 * S, 1.2 * S, 0, Math.PI * 2); g.fill();
    g.restore();
  }
  function drawSnail(g, s) {
    const x = s.x, y = groundY + 3 * S;
    g.fillStyle = "#c9b27a"; g.beginPath(); g.ellipse(x, y, 11 * S, 3 * S, 0, 0, Math.PI * 2); g.fill();
    g.fillStyle = "#a0673d"; g.beginPath(); g.arc(x - 2 * S, y - 7 * S, 7 * S, 0, Math.PI * 2); g.fill();
    g.strokeStyle = "#7a4a28"; g.lineWidth = 1.4 * S; g.beginPath(); g.arc(x - 2 * S, y - 7 * S, 3.4 * S, 0, Math.PI * 1.6); g.stroke();
    g.strokeStyle = "#c9b27a"; g.beginPath(); g.moveTo(x + 8 * S, y - 1 * S); g.lineTo(x + 11 * S, y - 8 * S); g.stroke();
  }
  function drawButterfly(g, b, t) {
    const w = Math.abs(Math.sin(t / 90 + b.p)) * 6 * S + 2 * S;
    g.save(); g.translate(b.x, b.y);
    g.fillStyle = b.col;
    g.beginPath(); g.ellipse(-w * .6, -2 * S, w, 5 * S, -.4, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(w * .6, -2 * S, w, 5 * S, .4, 0, Math.PI * 2); g.fill();
    g.fillStyle = "#3b2a22"; g.beginPath(); g.ellipse(0, 0, 1.6 * S, 5.5 * S, 0, 0, Math.PI * 2); g.fill();
    g.restore();
  }

  function step(dt, now) {
    for (const cl of clouds) { cl.x += cl.v * dt; if (cl.x > W + 120 * cl.sc) cl.x = -120 * cl.sc; }
    for (const b of birds) { b.x += b.v * dt; if (b.x > W + 30) { b.x = -30; b.y = HB + rnd(12, 70) * S; } }
    for (const p of petals) {
      p.y += p.vy * dt; p.x += p.vx * dt * .4; p.r += p.spin * dt;
      if (p.y > H + 12 || p.x < -20) Object.assign(p, make(true));
    }
    for (const b of blooms) if (b.grow < 1) b.grow = Math.min(1, b.grow + dt * .6);
    snail.x += 2.2 * S * dt; if (snail.x > W + 20) snail.x = -20;
    if (!rabbit && now > rabbitAt) rabbit = { x: -30, dir: 1, v: 90 * S, t: 0 };
    if (rabbit) { rabbit.t += dt; rabbit.x += rabbit.dir * rabbit.v * dt; if (rabbit.x > W + 40 || rabbit.x < -40) { rabbit = null; rabbitAt = now + rnd(40e3, 80e3); } }
    if (now > bflyAt && bfly.length < 2) { bfly.push({ x: rnd(0, W), y: BH - rnd(40, 70) * S, dir: Math.random() < .5 ? 1 : -1, p: rnd(0, 6), col: pick(["#f39c3d", "#f7d54a", "#9ec9f0"]) }); bflyAt = now + rnd(6000, 14000); }
    for (const b of bfly) { b.x += b.dir * 32 * S * dt; b.y = Math.max(8 * S, Math.min(BH - 30 * S, b.y + Math.sin(now / 400 + b.p) * 20 * S * dt)); }
    bfly = bfly.filter(b => b.x > -30 && b.x < W + 30);
    if (now > birdAt) { birdIn = birdIn > 0 ? 0 : 1; birdAt = now + (birdIn ? rnd(4000, 9000) : rnd(8000, 16000)); }
  }

  function draw(now) {
    const t = now || 0;
    // The sky, soft rather than white.
    const sky = ctx.createLinearGradient(0, 0, 0, H);
    sky.addColorStop(0, "#d8e9ef"); sky.addColorStop(.55, "#e5eee0"); sky.addColorStop(1, "#dfe9d6");
    ctx.fillStyle = sky; ctx.fillRect(0, 0, W, H);
    // Hills, far to near, behind the page.
    for (const [base, amp, col, k] of [[H * .62, 26, "#cfe0c2", .004], [H * .72, 34, "#bfd6ae", .006], [H * .82, 30, "#afcb9b", .009]]) {
      ctx.fillStyle = col; ctx.beginPath(); ctx.moveTo(0, H);
      for (let x = 0; x <= W; x += 20) ctx.lineTo(x, base + Math.sin(x * k + base) * amp * S);
      ctx.lineTo(W, H); ctx.closePath(); ctx.fill();
    }
    for (const cl of clouds) {
      ctx.fillStyle = "rgba(255,255,255,.75)";
      for (const [dx, dy, r] of [[0, 0, 22], [20, 5, 16], [-20, 5, 15], [7, -10, 15]]) { ctx.beginPath(); ctx.arc(cl.x + dx * cl.sc, cl.y + dy * cl.sc, r * cl.sc, 0, Math.PI * 2); ctx.fill(); }
    }
    for (const b of birds) {
      const flap = Math.sin(t / 220 + b.t) * 5 * b.sc;
      ctx.strokeStyle = "rgba(60, 80, 70, .5)"; ctx.lineWidth = 2 * b.sc;
      ctx.beginPath(); ctx.moveTo(b.x - 9 * b.sc, b.y + flap); ctx.quadraticCurveTo(b.x, b.y - 4 * b.sc, b.x + 9 * b.sc, b.y + flap); ctx.stroke();
    }
    // The kite, on a long string down past the edge of the page.
    const kx = kite.x + Math.sin(t / 1700) * 16 * S, ky = kite.y + Math.sin(t / 1100) * 8 * S;
    ctx.strokeStyle = "rgba(90, 90, 90, .45)"; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(kx, ky + 22 * S); ctx.quadraticCurveTo(kx - 60 * S, ky + 160 * S, kx - 180 * S, H); ctx.stroke();
    ctx.save(); ctx.translate(kx, ky); ctx.rotate(Math.sin(t / 900) * .12);
    ctx.fillStyle = "#e0648c"; ctx.beginPath(); ctx.moveTo(0, -20 * S); ctx.lineTo(14 * S, 0); ctx.lineTo(0, 22 * S); ctx.lineTo(-14 * S, 0); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#f2c14e"; ctx.beginPath(); ctx.moveTo(0, -20 * S); ctx.lineTo(14 * S, 0); ctx.lineTo(0, 0); ctx.closePath(); ctx.fill();
    ctx.beginPath(); ctx.moveTo(0, 22 * S); ctx.lineTo(-14 * S, 0); ctx.lineTo(0, 0); ctx.closePath(); ctx.fill();
    ctx.strokeStyle = "#8a4b62"; ctx.lineWidth = 1.2;
    ctx.beginPath(); ctx.moveTo(0, 22 * S);
    for (let i = 1; i <= 5; i++) ctx.lineTo(Math.sin(t / 300 + i) * 5 * S, 22 * S + i * 8 * S);
    ctx.stroke();
    ctx.restore();
    // The cherry tree, leaning in from the right.
    ctx.strokeStyle = "#6e4a38"; ctx.lineCap = "round";
    ctx.lineWidth = 14 * S; ctx.beginPath(); ctx.moveTo(W + 10, H * .55); ctx.quadraticCurveTo(treeX + 40 * S, treeY + 90 * S, treeX, treeY + 10 * S); ctx.stroke();
    ctx.lineWidth = 6 * S; ctx.beginPath(); ctx.moveTo(treeX + 20 * S, treeY + 50 * S); ctx.quadraticCurveTo(treeX - 60 * S, treeY + 30 * S, treeX - 120 * S, treeY); ctx.stroke();
    for (const b of canopy) {
      ctx.fillStyle = b.col; ctx.globalAlpha = .72;
      ctx.beginPath(); ctx.arc(b.x + Math.sin(t / 2000 + b.x) * 2, b.y, b.r, 0, Math.PI * 2); ctx.fill();
    }
    ctx.globalAlpha = 1;
    for (const p of petals) {
      const x = p.x + Math.sin(t / 1000 + p.phase) * p.sway;
      ctx.save(); ctx.translate(x, p.y); ctx.rotate(p.r);
      ctx.fillStyle = p.col; ctx.beginPath(); ctx.ellipse(0, 0, p.sz, p.sz * .58, 0, 0, Math.PI * 2); ctx.fill();
      ctx.restore();
    }

    // The meadow, on its own floor.
    const gr = f.createLinearGradient(0, 0, 0, BH);
    gr.addColorStop(0, "#cfe0c2"); gr.addColorStop(.35, "#9fc486"); gr.addColorStop(1, "#6f9e59");
    f.fillStyle = gr; f.fillRect(0, 0, W, BH);
    f.fillStyle = "#88b570";
    f.beginPath(); f.moveTo(0, BH);
    for (let x = 0; x <= W; x += 16) f.lineTo(x, groundY - 8 * S + Math.sin(x / 70) * 4 * S);
    f.lineTo(W, BH); f.closePath(); f.fill();
    drawFence(f, t);
    drawSnail(f, snail);
    for (const b of blooms) drawBloom(f, b, t);
    for (const g of grass) {
      const lean = Math.sin(t / 1300 + g.phase) * 3 * S;
      f.strokeStyle = g.dark ? "#4f7f3f" : "#6a9c53"; f.lineWidth = 1.8 * S;
      f.beginPath(); f.moveTo(g.x, BH); f.quadraticCurveTo(g.x + lean * .5, BH - g.h * .6, g.x + lean, BH - g.h); f.stroke();
    }
    if (rabbit) drawRabbit(f, rabbit, t);
    for (const b of bfly) drawButterfly(f, b, t);
  }

  if (calm) {
    // Reduced motion: blossom already fallen and lying still, the meadow still.
    for (const p of petals) { p.y = H - rnd(2, 40); p.sway = 0; }
    draw(0);
    return;
  }
  rabbitAt = performance.now() + rnd(8000, 16000);
  c.start();
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    for (const p of petals.slice(0, 20)) Object.assign(p, make(true), { vy: rnd(40, 80) });
    if (!rabbit) rabbit = { x: -30, dir: 1, v: 110 * S, t: 0 };
  };
  festiveHooks.newDevice = () => {
    if (document.hidden) return;
    blooms.push({ x: rnd(20, W - 20), h: rnd(28, 46) * S, kind: Math.random() < .5 ? "tulip" : "daff", phase: rnd(0, 6.3), grow: 0,
      col: pick(["#e0648c", "#d9534f", "#f2c14e", "#a974c4"]) });
  };
}

BAMF.registerTheme("spring", ctx => buildSpring(ctx.root, ctx.switched));
})();
