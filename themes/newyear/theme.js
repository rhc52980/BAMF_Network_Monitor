// New Year: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// New Year: fireworks over a skyline. Rockets climb, burst, and the sparks
// fall and fade; a finished scan sets one off. Nothing runs while the tab is
// in the background, and reduced motion gets the sky at one moment, still.
//
// A new device nobody has marked known is an intruder: a gatecrasher. The
// fireworks stop and the sky bursts red, NOT ON THE LIST flashes up, and it's
// held behind the velvet rope along the bottom, party hat, disguise and all,
// tagged, until the device is marked known. Then the rope's unhooked, in it
// goes, and a volley goes up for the device.
const NY_CRASHER = `<svg viewBox="0 0 56 92" width="56" height="92" aria-hidden="true">
  <path d="M19 60l-3 30h7l5-22 5 22h7l-3-30z" fill="#141828"/>
  <path d="M14 38c0-6 6-9 14-9s14 3 14 9v24H14z" fill="#1d2238" stroke="#3a4270" stroke-width="1"/>
  <path d="M24 29l4 12 4-12z" fill="#f2f0ff"/><path d="M24.5 31l3.5 2-3.5 2zM31.5 31l-3.5 2 3.5 2z" fill="#ffd166"/>
  <circle cx="28" cy="20" r="9.5" fill="#e8b98e"/>
  <path d="M19.5 17.5h17" stroke="#141828" stroke-width="2.4" stroke-linecap="round"/>
  <circle cx="24" cy="19.5" r="3" fill="none" stroke="#141828" stroke-width="1.4"/><circle cx="32" cy="19.5" r="3" fill="none" stroke="#141828" stroke-width="1.4"/>
  <path d="M28 20v5" stroke="#c9936a" stroke-width="3" stroke-linecap="round"/><path d="M23 26q5-3 10 0q-5 2-10 0z" fill="#141828"/>
  <g transform="rotate(14 28 12)"><path d="M21 12L28 -6l7 18z" fill="#ff5fa2"/><path d="M23.4 6h9.2M25.6 0.5h4.8" stroke="#ffd166" stroke-width="2"/><circle cx="28" cy="-6" r="2.4" fill="#ffd166"/></g>
  <path d="M35 26l14-4" stroke="#ffd166" stroke-width="3" stroke-linecap="round"/><path d="M49 22l4-3-1 5z" fill="#7ee0ff"/>
  <path d="M40 40l8-12" stroke="#1d2238" stroke-width="5" stroke-linecap="round"/></svg>`;

const NY_ROPE = `<svg viewBox="0 0 170 60" width="170" height="60" aria-hidden="true">
  <path class="ny-cord" d="M16 14Q85 44 154 14" fill="none" stroke="#b0122a" stroke-width="6" stroke-linecap="round"/>
  <path class="ny-cord" d="M16 14Q85 44 154 14" fill="none" stroke="#ff4a62" stroke-width="1.6" stroke-linecap="round" opacity=".6" transform="translate(0 -1.5)"/>
  ${[16, 154].map(x => `<g><rect x="${x - 2.5}" y="12" width="5" height="44" fill="#d4a93a"/><circle cx="${x}" cy="10" r="5" fill="#f2cf5c"/>
    <ellipse cx="${x}" cy="57" rx="11" ry="3" fill="#b88a26"/></g>`).join("")}</svg>`;

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

  // ---- intruders: gatecrashers at the rope ----
  let next = 0;
  const watchIn = intruderWatch();
  const crashers = new Map();           // id -> { h, el, tag, alarm, gone }
  const spacer = document.createElement("div");
  spacer.setAttribute("aria-hidden", "true");
  document.body.appendChild(spacer);
  festiveStops.push(() => spacer.remove());
  const slotX = k => Math.round(W < 700 ? W * .56 : W * .68 - k * 260);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...crashers.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeCrasher = h => {
    const el = document.createElement("div");
    el.className = "ny-door";
    el.innerHTML = `<div class="ny-crasher">${NY_CRASHER}</div>${NY_ROPE}`;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const c = { h, el, tag, alarm: 0, gone: false };
    crashers.set(h.id, c);
    return c;
  };
  const settle = () => {
    const list = order();
    spacer.style.height = list.some(c => !c.gone) ? "108px" : "0";
    list.forEach((c, k) => {
      c.el.hidden = k > (W < 700 ? 0 : 1);
      c.el.style.left = slotX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!crashers.has(h.id)) makeCrasher(h);
    for (const h of held) { const c = crashers.get(h.id); if (c && !c.gone) { c.h = h; if (!c.alarm || Date.now() - c.alarm > 9000) intruderTagEl(h, "held", c.tag); } }
    for (const h of cleared) {
      const c = crashers.get(h.id); if (!c || c.gone) continue;
      c.gone = true;
      const done = () => { c.el.remove(); crashers.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: the rope's unhooked, in it goes, and a volley goes up.
      intruderTagEl(c.h, "cleared", c.tag);
      festiveTimers.push(setTimeout(() => c.el.classList.add("open"), 1400));
      festiveTimers.push(setTimeout(() => c.el.classList.add("gone"), 2600));
      festiveTimers.push(setTimeout(() => {
        done();
        if (!h.test && !document.hidden) [.3, .5, .7].forEach((f, i) => festiveTimers.push(setTimeout(() => launch(W * f), i * 250)));
      }, 3800));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const c = crashers.get(h.id) || makeCrasher(h);
    if (calm || document.hidden) { settle(); return; }
    c.alarm = Date.now();
    intruderTagEl(h, "alarm", c.tag);
    settle();
    c.el.classList.remove("caught"); void c.el.offsetWidth; c.el.classList.add("caught");
    // The fireworks stop, the sky bursts red, and the list comes out.
    rockets.length = 0;
    next = performance.now() + 7000;
    [[.2, .22], [.5, .14], [.8, .24], [.35, .34], [.65, .3]].forEach(([fx, fy], i) =>
      festiveTimers.push(setTimeout(() => { if (!document.hidden) burst(W * fx, H * fy, i % 2 ? "#ff7b7b" : "#ff3b3b"); }, i * 260)));
    const b = document.createElement("div");
    b.className = "ny-list";
    b.style.top = ((document.querySelector("header")?.getBoundingClientRect().bottom || 60) + 16) + "px";
    b.innerHTML = `NOT ON THE LIST<small>${esc(nameOrIp(h))}</small>`;
    root.appendChild(b);
    festiveTimers.push(setTimeout(() => b.remove(), 4600));
    festiveTimers.push(setTimeout(() => { if (!c.gone) intruderTagEl(c.h, "held", c.tag); }, 9000));
  };
  festiveHooks.rendered = syncIntruders;
  syncIntruders();

  if (calm) {
    // Reduced motion: three bursts hanging over the city, holding still.
    ctx.fillStyle = "#060916";
    ctx.fillRect(0, 0, W, H);
    for (const [fx, fy, c] of [[W * .25, H * .3, "#ffd166"], [W * .55, H * .2, "#7ee0ff"], [W * .78, H * .36, "#ff7b7b"]]) burst(fx, fy, c);
    for (const p of sparks) { p.x += p.vx * .5; p.y += p.vy * .5; }
    draw();
    return;
  }

  let raf = 0, last = 0;
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
