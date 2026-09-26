// Winter: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Winter: snow falling past frosted edges, settling into a drift along the
// bottom. A finished scan brings a gust through.
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

  function step(dt) {
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
    // The drift banks up against the bottom of the screen, in front of the page.
    front.clearRect(0, 0, W, H);
    front.fillStyle = "rgba(226, 242, 255, .72)";
    front.beginPath();
    front.moveTo(0, H);
    for (let x = 0; x <= W; x += 24) front.lineTo(x, H - 14 - Math.sin(x / 120) * 7);
    front.lineTo(W, H);
    front.closePath();
    front.fill();
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
    draw(0);
    return;
  }
  c.start();
  festiveHooks.scanDone = () => { if (!document.hidden) gust = 1; };
}

BAMF.registerTheme("winter", ctx => buildWinter(ctx.root, ctx.switched));
})();
