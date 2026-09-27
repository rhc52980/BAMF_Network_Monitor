// Thanksgiving: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Thanksgiving: leaves coming down, tumbling as they go, and a gust of them
// when a scan finishes. Reduced motion lays them on the ground instead.
//
// A new device nobody has marked known is an intruder: a raccoon at the pie.
// The edges of the page glow red, the leaves whip up and it's GOBBLE GOBBLE
// GOBBLE; it scurries in and sits by the pie along the bottom, a slice in its
// paws, tagged, until the device is marked known. Then it scurries off, the
// leaves blow through, and a turkey comes by to see who's new.
const TG_RACCOON = `<svg viewBox="0 0 76 74" width="76" height="74" aria-hidden="true">
  <path d="M44 62c14 4 28-2 30-16 1-8-4-12-8-10 3 4 2 10-4 14s-12 4-18 3z" fill="#7a7570"/>
  <path d="M61 57l4-9M67 52l3-10M55 60l2-7" stroke="#2a2624" stroke-width="4" stroke-linecap="round"/>
  <ellipse cx="32" cy="50" rx="17" ry="19" fill="#8a8580"/><ellipse cx="32" cy="54" rx="10" ry="13" fill="#b5b0aa"/>
  <ellipse cx="22" cy="70" rx="7" ry="3.5" fill="#5a5652"/><ellipse cx="42" cy="70" rx="7" ry="3.5" fill="#5a5652"/>
  <path d="M19 14l-2-11 10 6zM45 14l2-11-10 6z" fill="#6e6a66"/>
  <ellipse cx="32" cy="22" rx="15" ry="13" fill="#9a958f"/>
  <path d="M18 23q5-8 14-4 9-4 14 4-5 6-14 3-9 3-14-3z" fill="#1d1a19"/>
  <circle cx="25" cy="22" r="2.2" fill="#fff"/><circle cx="39" cy="22" r="2.2" fill="#fff"/><circle cx="25.5" cy="22" r="1" fill="#000"/><circle cx="38.5" cy="22" r="1" fill="#000"/>
  <path d="M27 29q5 4 10 0" fill="#e9e5df"/><circle cx="32" cy="29" r="2" fill="#1d1a19"/>
  <path d="M20 42l18-6-4 12z" fill="#e8a33d" stroke="#b2641f" stroke-width="1.2" stroke-linejoin="round"/><path d="M20 42l18-6" stroke="#c77a2a" stroke-width="3" stroke-linecap="round"/>
  <ellipse cx="21" cy="44" rx="4" ry="3" fill="#6e6a66"/><ellipse cx="36" cy="41" rx="4" ry="3" fill="#6e6a66"/></svg>`;

const TG_PIE = `<svg viewBox="0 0 90 34" width="90" height="34" aria-hidden="true">
  <path d="M4 14h82l-6 18H10z" fill="#a9b0b8" stroke="#6b7079" stroke-width="1.2"/>
  <path d="M6 14q39-12 78 0z" fill="#d99a4a"/><path d="M6 14h78" stroke="#b2641f" stroke-width="4" stroke-linecap="round"/>
  <path d="M20 9l8 5M34 6l10 8M50 6l10 8M64 8l8 6M28 7l-6 7M46 5l-8 9M62 6l-8 8M74 9l-6 5" stroke="#b2641f" stroke-width="2" stroke-linecap="round"/>
  <path d="M60 14l10-8 14 8z" fill="#3a2410"/><path d="M60 14l10-8" stroke="#e8a33d" stroke-width="2"/></svg>`;

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

  // ---- intruders: raccoons at the pie ----
  let turkey = null;                    // the stroll, once it's ready
  const watchIn = intruderWatch();
  const coons = new Map();              // id -> { h, el, tag, alarm, gone }
  const spacer = document.createElement("div");
  spacer.setAttribute("aria-hidden", "true");
  document.body.appendChild(spacer);
  festiveStops.push(() => spacer.remove());
  const slotX = k => Math.round(W < 700 ? W * .58 : W * .66 - k * 260);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...coons.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeCoon = h => {
    const el = document.createElement("div");
    el.className = "tg-thief";
    el.innerHTML = `${TG_PIE}<div class="tg-coon">${TG_RACCOON}</div>`;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const c = { h, el, tag, alarm: 0, gone: false };
    coons.set(h.id, c);
    return c;
  };
  const settle = () => {
    const list = order();
    spacer.style.height = list.some(c => !c.gone) ? "92px" : "0";
    list.forEach((c, k) => {
      c.el.hidden = k > (W < 700 ? 0 : 1);
      c.el.style.left = slotX(k) + "px";
    });
  };
  // Leaves whipped up across the screen.
  const gust = () => { for (const l of leaves.slice(0, 18)) { l.x = rnd(-40, W * .2); l.y = rnd(0, H * .7); l.spin = rnd(2, 4.5); l.vy = rnd(50, 90); } };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!coons.has(h.id)) makeCoon(h);
    for (const h of held) { const c = coons.get(h.id); if (c && !c.gone) { c.h = h; if (!c.alarm || Date.now() - c.alarm > 9000) intruderTagEl(h, "held", c.tag); } }
    for (const h of cleared) {
      const c = coons.get(h.id); if (!c || c.gone) continue;
      c.gone = true;
      const done = () => { c.el.remove(); coons.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: it scurries off, the leaves blow through, and a turkey
      // comes by to see who's new.
      intruderTagEl(c.h, "cleared", c.tag);
      festiveTimers.push(setTimeout(() => c.el.classList.add("off"), 1800));
      festiveTimers.push(setTimeout(() => c.el.classList.add("gone"), 2800));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) { gust(); turkey?.(); } }, 4000));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const c = coons.get(h.id) || makeCoon(h);
    if (calm || document.hidden) { settle(); return; }
    c.alarm = Date.now();
    intruderTagEl(h, "alarm", c.tag);
    settle();
    // It scurries in; the edges glow red, the leaves whip up, and the gobbling starts.
    c.el.classList.remove("in"); void c.el.offsetWidth; c.el.classList.add("in");
    gust();
    const glow = document.createElement("div");
    glow.className = "tg-alarm";
    root.appendChild(glow);
    festiveTimers.push(setTimeout(() => glow.remove(), 6000));
    const g = document.createElement("div");
    g.className = "tg-gobble";
    g.style.top = ((document.querySelector("header")?.getBoundingClientRect().bottom || 60) + 16) + "px";
    g.innerHTML = `GOBBLE GOBBLE GOBBLE!<small>${esc(nameOrIp(h))} is at the pie</small>`;
    root.appendChild(g);
    festiveTimers.push(setTimeout(() => g.remove(), 4600));
    const bird = document.querySelector(".tg-bird");
    if (bird) { bird.classList.add("proud"); festiveTimers.push(setTimeout(() => bird.classList.remove("proud"), 3000)); }
    festiveTimers.push(setTimeout(() => { if (!c.gone) intruderTagEl(c.h, "held", c.tag); }, 9000));
  };
  festiveHooks.rendered = syncIntruders;
  syncIntruders();

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
  turkey = () => {
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
    gust();
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
