// Thanksgiving: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Thanksgiving: leaves coming down, tumbling as they go, and a gust of them
// when a scan finishes. Reduced motion lays them on the ground instead.
const TG_LEAF = ["#d1452f", "#e8873d", "#e8a33d", "#a7642f", "#8c6b2f", "#a7c04f"];

function buildThanksgiving(root) {
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

  // One leaf: a rounded blade with a stem, drawn at the origin and rotated.
  const leaf = (x, y, r, sz, col, flat) => {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(r);
    ctx.scale(1, flat);
    ctx.fillStyle = col;
    ctx.beginPath();
    ctx.moveTo(0, -sz);
    ctx.bezierCurveTo(sz * .9, -sz * .5, sz * .75, sz * .6, 0, sz);
    ctx.bezierCurveTo(-sz * .75, sz * .6, -sz * .9, -sz * .5, 0, -sz);
    ctx.fill();
    ctx.strokeStyle = "rgba(60, 30, 10, .45)";
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, -sz); ctx.lineTo(0, sz * 1.15); ctx.stroke();
    ctx.restore();
  };
  const make = (top) => ({
    x: rnd(0, W), y: top ? rnd(-H * .3, -10) : rnd(0, H),
    sz: rnd(6, 13), r: rnd(0, 6.3), spin: rnd(-1.6, 1.6),
    vy: rnd(24, 62), sway: rnd(10, 34), phase: rnd(0, 6.3),
    col: TG_LEAF[Math.floor(Math.random() * TG_LEAF.length)],
  });
  const leaves = Array.from({ length: Math.min(70, Math.round(W / 22)) }, () => make(false));

  // The harvest along the bottom: pumpkins, a couple of gourds and some corn,
  // drawn once and kept, so they sit there like they were put out on the step.
  const patch = [];
  for (let x = rnd(30, 90); x < W - 40; x += rnd(150, 320)) {
    const r = rnd(16, 30);
    patch.push({ x, r, kind: Math.random() < .68 ? "pumpkin" : "gourd", col: Math.random() < .5 ? "#e8873d" : "#d1652f", tilt: rnd(-.2, .2) });
  }
  const gourd = (g) => {
    ctx.save();
    ctx.translate(g.x, H - 6 - g.r * .7);
    ctx.rotate(g.tilt);
    if (g.kind === "pumpkin") {
      ctx.fillStyle = g.col;
      for (const [dx, rx] of [[-g.r * .5, g.r * .6], [g.r * .5, g.r * .6], [0, g.r]]) {
        ctx.beginPath(); ctx.ellipse(dx, 0, rx, g.r * .82, 0, 0, Math.PI * 2); ctx.fill();
      }
      ctx.strokeStyle = "rgba(120, 55, 15, .35)";
      ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.ellipse(0, 0, g.r * .45, g.r * .8, 0, 0, Math.PI * 2); ctx.stroke();
    } else {
      ctx.fillStyle = "#c7a23f";
      ctx.beginPath(); ctx.ellipse(0, g.r * .2, g.r * .55, g.r * .62, 0, 0, Math.PI * 2); ctx.fill();
      ctx.beginPath(); ctx.ellipse(0, -g.r * .5, g.r * .3, g.r * .4, 0, 0, Math.PI * 2); ctx.fill();
    }
    ctx.strokeStyle = "#5a3d1b";
    ctx.lineWidth = 4;
    ctx.lineCap = "round";
    ctx.beginPath(); ctx.moveTo(0, -g.r * .78); ctx.lineTo(g.r * .15, -g.r * 1.1); ctx.stroke();
    ctx.restore();
  };

  const draw = (t) => {
    ctx.clearRect(0, 0, W, H);
    for (const g of patch) gourd(g);
    for (const l of leaves) leaf(l.x + Math.sin(t / 1000 + l.phase) * l.sway, l.y, l.r, l.sz, l.col, Math.cos(l.r * .7) * .6 + .4);
  };

  if (calm) {
    // Reduced motion: fallen leaves, scattered along the bottom.
    for (const l of leaves) { l.y = H - rnd(4, 46); l.sway = 0; }
    draw(0);
    return;
  }

  let raf = 0, last = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000);
    last = now;
    for (const l of leaves) {
      l.y += l.vy * dt;
      l.r += l.spin * dt;
      if (l.y > H + 20) Object.assign(l, make(true));
    }
    draw(now);
  };
  const run = () => { if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); } };
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };
  const vis = () => document.hidden ? stop() : run();
  document.addEventListener("visibilitychange", vis);
  run();
  festiveStops.push(() => { stop(); document.removeEventListener("visibilitychange", vis); });
  // A turkey comes through every so often, tail up, pecking as he goes.
  const turkey = () => {
    if (document.hidden || document.querySelector(".tg-bird")) return;
    const el = document.createElement("div");
    el.className = "tg-bird" + (Math.random() < .5 ? " rtl" : "");
    el.style.animationDuration = rnd(34, 58).toFixed(0) + "s";
    el.innerHTML = TG_TURKEY;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    root.appendChild(el);
  };
  const again = () => festiveTimers.push(setTimeout(() => { turkey(); again(); }, rnd(45e3, 95e3)));
  festiveTimers.push(setTimeout(() => { turkey(); again(); }, rnd(8e3, 20e3)));

  // A finished scan blows a handful across the screen, and puffs him up.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    for (const l of leaves.slice(0, 14)) { l.x = rnd(-40, W * .2); l.y = rnd(0, H * .7); l.spin = rnd(2, 4.5); l.vy = rnd(50, 90); }
    const t = document.querySelector(".tg-bird");
    if (t) { t.classList.add("proud"); festiveTimers.push(setTimeout(() => t.classList.remove("proud"), 1600)); }
  };
}

// A turkey, facing right: tail fanned behind him, wattle, and a head that
// dips to peck as he walks.
const TG_TURKEY = `<svg width="150" height="120" viewBox="0 0 150 120">
  <g class="fan">
    <path d="M18 78C2 60 6 26 30 12c-8 18-6 40 6 54z" fill="#8c4a21"/>
    <path d="M30 80C16 56 26 22 52 12c-12 18-14 44-6 62z" fill="#b25c26"/>
    <path d="M44 82C36 54 50 22 76 16c-16 18-22 44-16 64z" fill="#d1652f"/>
    <path d="M60 84C58 56 74 26 98 24c-18 16-26 40-24 58z" fill="#e8a33d"/>
    <path d="M76 86C80 60 96 36 118 36c-18 14-28 32-28 50z" fill="#c7a23f"/>
  </g>
  <ellipse cx="86" cy="86" rx="32" ry="24" fill="#5a3218"/>
  <ellipse cx="94" cy="92" rx="22" ry="15" fill="#6e401f"/>
  <g class="head">
    <path d="M106 70c8-10 22-12 28-4" fill="none" stroke="#5a3218" stroke-width="11" stroke-linecap="round"/>
    <circle cx="130" cy="62" r="11" fill="#7d4a24"/>
    <path d="M139 60l10 4-10 5z" fill="#e8a33d"/>
    <path d="M128 72q4 10-2 14" fill="none" stroke="#d1452f" stroke-width="5" stroke-linecap="round"/>
    <circle cx="133" cy="58" r="2.4" fill="#1d0e04"/>
  </g>
  <g stroke="#e8a33d" stroke-width="5" stroke-linecap="round"><path d="M80 108v8M96 108v8"/></g>
  <g stroke="#e8a33d" stroke-width="4" stroke-linecap="round"><path d="M74 116h12M90 116h12"/></g>
</svg>`;

BAMF.registerTheme("thanksgiving", ctx => buildThanksgiving(ctx.root, ctx.switched));
})();
