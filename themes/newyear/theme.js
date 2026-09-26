// New Year: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// New Year: fireworks over a skyline. Rockets climb, burst, and the sparks
// fall and fade; a finished scan sets one off. Nothing runs while the tab is
// in the background, and reduced motion gets the sky at one moment, still.
function buildNewYear(root) {
  const calm = calmMotion();
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = "<canvas></canvas>";
  document.body.prepend(bg);
  const cv = bg.querySelector("canvas"), ctx = cv.getContext("2d");
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);
  const COLORS = ["#ffd166", "#ff7b7b", "#7ee0ff", "#c2a7ff", "#8cff9e", "#ffffff"];

  // The skyline is drawn once and stamped back over the fade each frame.
  const sky = document.createElement("canvas");
  sky.width = W * dpr; sky.height = H * dpr;
  const sx = sky.getContext("2d");
  sx.scale(dpr, dpr);
  const roof = H * .82;
  for (let x = 0, i = 0; x < W; i++) {
    const w = rnd(40, 95), h = rnd(H * .06, H * .17);
    sx.fillStyle = i % 2 ? "#080c1c" : "#0b1024";
    sx.fillRect(x, roof - h, w, H - roof + h);
    for (let wy = roof - h + 8; wy < H - 10; wy += 13)
      for (let wx = x + 6; wx < x + w - 6; wx += 11)
        if (Math.random() < .28) { sx.fillStyle = Math.random() < .8 ? "rgba(255, 209, 102, .5)" : "rgba(126, 224, 255, .45)"; sx.fillRect(wx, wy, 4, 6); }
    x += w + rnd(2, 10);
  }

  const rockets = [], sparks = [];
  const burst = (x, y, col) => {
    const n = calm ? 70 : Math.round(rnd(48, 86)), spd = rnd(90, 190);
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2 + rnd(-.05, .05), v = spd * rnd(.45, 1);
      sparks.push({ x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v, life: rnd(1, 1.9), col });
    }
  };
  const launch = (x) => rockets.push({
    x: x ?? rnd(W * .12, W * .88), y: H * .92, vy: -rnd(H * .34, H * .52),
    col: COLORS[Math.floor(Math.random() * COLORS.length)],
  });

  const draw = () => {
    ctx.fillStyle = "rgba(6, 9, 22, .26)";
    ctx.fillRect(0, 0, W, H);
    ctx.drawImage(sky, 0, 0, W, H);
    for (const r of rockets) {
      ctx.fillStyle = r.col;
      ctx.fillRect(r.x - 1, r.y - 4, 2, 8);
    }
    for (const p of sparks) {
      ctx.globalAlpha = Math.max(0, Math.min(1, p.life));
      ctx.fillStyle = p.col;
      ctx.fillRect(p.x - 1.2, p.y - 1.2, 2.4, 2.4);
    }
    ctx.globalAlpha = 1;
  };

  if (calm) {
    // Reduced motion: three bursts hanging over the city, holding still.
    ctx.fillStyle = "#060916";
    ctx.fillRect(0, 0, W, H);
    for (const [fx, fy, c] of [[W * .25, H * .3, "#ffd166"], [W * .55, H * .2, "#7ee0ff"], [W * .78, H * .36, "#ff7b7b"]]) burst(fx, fy, c);
    for (const p of sparks) { p.x += p.vx * .5; p.y += p.vy * .5; }
    draw();
    return;
  }

  let raf = 0, last = 0, next = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000);
    last = now;
    if (now > next) { launch(); next = now + rnd(1100, 2600); }
    for (let i = rockets.length - 1; i >= 0; i--) {
      const r = rockets[i];
      r.y += r.vy * dt; r.vy += 150 * dt;
      if (r.vy > -60) { burst(r.x, r.y, r.col); rockets.splice(i, 1); }
    }
    for (let i = sparks.length - 1; i >= 0; i--) {
      const p = sparks[i];
      p.x += p.vx * dt; p.y += p.vy * dt; p.vy += 52 * dt;
      p.vx *= .985; p.vy *= .985; p.life -= dt * .62;
      if (p.life <= 0) sparks.splice(i, 1);
    }
    draw();
  };
  const run = () => { if (!raf) { last = performance.now(); next = last + 600; raf = requestAnimationFrame(frame); } };
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };
  const vis = () => document.hidden ? stop() : run();
  document.addEventListener("visibilitychange", vis);
  run();
  festiveStops.push(() => { stop(); document.removeEventListener("visibilitychange", vis); });
  festiveHooks.scanDone = () => { if (!document.hidden) launch(); };
}

BAMF.registerTheme("newyear", ctx => buildNewYear(ctx.root, ctx.switched));
})();
