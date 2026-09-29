// RGB: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- RGB ----
// The dashboard as a gaming rig with every light on. Most of it is CSS: the
// frame round the window, the flowing strips, the spinning card borders, the
// rainbow text. This adds the frame's strips, the room behind the page (dark,
// with slow colour drifting through it and a lit floor grid), and the lights'
// response to the network: a scan runs the rig flat out, a new device flashes
// it white, a device coming back flashes it green, one dropping off red, and
// while a watched device is down the frame breathes red rather than cycling.
//
// A new device nobody has marked known is an intruder: an unknown stick
// plugged into the rig. Every light on the rig turns red, the frame strobing,
// and the room goes red; the stick sits in the bottom of the frame, its light pulsing red, tagged, until
// the device is marked known. Then its light goes green, it ejects, and the
// rig flashes white for the device.
const RGB_STICK = `<svg viewBox="0 0 40 78" width="40" height="78"><defs><linearGradient id="rgbStripe" x1="0" x2="0" y1="0" y2="1">
  <stop offset="0" stop-color="#ff0040"/><stop offset=".25" stop-color="#ffe600"/><stop offset=".5" stop-color="#2bff5a"/><stop offset=".75" stop-color="#00e5ff"/><stop offset="1" stop-color="#c43dff"/></linearGradient></defs>
  <rect x="10" y="52" width="20" height="24" rx="1.5" fill="#b8bcc8" stroke="#6d7282"/><rect x="14" y="58" width="4" height="4" fill="#4a4e5a"/><rect x="22" y="58" width="4" height="4" fill="#4a4e5a"/>
  <rect x="3" y="2" width="34" height="52" rx="6" fill="#14141e" stroke="#2c2c3c" stroke-width="1.5"/>
  <rect x="7" y="10" width="3" height="36" rx="1.5" fill="url(#rgbStripe)"/>
  <circle class="rgb-led" cx="24" cy="13" r="3.4"/>
  <text x="24" y="40" text-anchor="middle" font-family="Space Grotesk, sans-serif" font-weight="800" font-size="15" fill="#ff5a78">?</text></svg>`;

function buildRgb(root) {
  const calm = calmMotion();
  const rig = document.createElement("div");
  rig.className = "rgb-rig";
  rig.style.cssText = "position:absolute;inset:0;pointer-events:none";
  rig.innerHTML = ["t", "r", "b", "l"].map(e => `<i class="rgb-edge ${e}"></i><i class="rgb-edge glow ${e}"></i>`).join("");
  root.appendChild(rig);
  // One mood at a time on the frame; a flash or a surge, then back.
  const briefly = (cls, ms) => {
    if (document.hidden) return;
    rig.classList.remove("surge", "flash-white", "flash-green", "flash-red");
    void rig.offsetWidth;   // restart the animation if it's the same one again
    rig.classList.add(cls);
    festiveTimers.push(setTimeout(() => rig.classList.remove(cls), ms));
  };

  // The room behind the page.
  const HUES = [340, 30, 55, 135, 190, 230, 280];
  let t = 0, wave = -1, alarm = false, redUntil = 0;
  const blobs = Array.from({ length: 4 }, (_, i) => ({ x: Math.random(), y: .2 + Math.random() * .6, r: .35 + Math.random() * .25, h: HUES[i * 2 % HUES.length], sx: rnd(-.02, .02), sy: rnd(-.015, .015) }));
  let room = null;
  const draw = () => {
    const { ctx, W, H } = room;
    const alarm = alarmNow();
    ctx.fillStyle = "#05050a";
    ctx.fillRect(0, 0, W, H);
    // Colour drifting through the dark, redder while something's down.
    for (const b of blobs) {
      const g = ctx.createRadialGradient(b.x * W, b.y * H, 0, b.x * W, b.y * H, b.r * Math.max(W, H));
      const hue = alarm ? 350 : (b.h + t * 12) % 360;
      g.addColorStop(0, `hsla(${hue.toFixed(0)}, 100%, 55%, .11)`);
      g.addColorStop(1, "hsla(0, 0%, 0%, 0)");
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, W, H);
    }
    // A lit floor grid running away from you, its lines cycling through the
    // rainbow; a scan sends a bright wave down it.
    const horizon = H * .72, vx = W / 2;
    ctx.lineWidth = 1;
    for (let i = -14; i <= 14; i++) {
      const hue = alarm ? 350 : (i * 18 + t * 40 + 720) % 360;
      ctx.strokeStyle = `hsla(${hue.toFixed(0)}, 100%, 60%, .16)`;
      ctx.beginPath(); ctx.moveTo(vx + i * 14, horizon); ctx.lineTo(vx + i * W / 9, H); ctx.stroke();
    }
    for (let k = 0; k < 9; k++) {
      const f = ((k + (calm ? 0 : t * .6)) % 9) / 9, y = horizon + (H - horizon) * f * f;
      const lit = wave >= 0 ? Math.max(0, 1 - Math.abs(f - wave) * 6) : 0;
      const hue = alarm ? 350 : (k * 40 + t * 40) % 360;
      ctx.strokeStyle = `hsla(${hue.toFixed(0)}, 100%, ${60 + lit * 25}%, ${(.1 + f * .12 + lit * .5).toFixed(2)})`;
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke();
    }
  };
  const step = dt => {
    t += dt;
    for (const b of blobs) {
      b.x += b.sx * dt; b.y += b.sy * dt;
      if (b.x < -.1 || b.x > 1.1) b.sx *= -1;
      if (b.y < .05 || b.y > .95) b.sy *= -1;
    }
    if (wave >= 0) { wave += dt * .9; if (wave > 1.3) wave = -1; }
  };
  // Red while a watched device is down, or an intruder's alarm is on.
  const alarmNow = () => alarm || Date.now() < redUntil;
  room = seasonCanvas(draw, step);
  // Reduced motion: one still frame of the room, and nothing moves after it.
  if (calm) draw(); else room.start();

  // The frame breathes red while a watched device is down - one that dropped
  // in the last hour. Something that's been off for days is in the table
  // already; it shouldn't keep the whole rig red for good.
  const setAlarm = () => {
    const hourAgo = Date.now() - 3600e3;
    const down = hosts.some(h => h.watched && !h.online && !h.ignored && !h.forgotten && Date.parse(h.lastSeen) > hourAgo);
    alarm = down;
    rig.classList.toggle("alarm", down);
  };
  // Number the keys for the wave across the hosts-on-segment strip.
  const keys = () => {
    document.querySelectorAll(".portstrip .port").forEach((p, i) => p.style.setProperty("--rgb-i", String(i % 40)));
  };
  festiveStops.push(() => document.querySelectorAll(".portstrip .port").forEach(p => p.style.removeProperty("--rgb-i")));
  // ---- intruders: unknown sticks in the rig ----
  const W = innerWidth;
  const watchIn = intruderWatch();
  const sticks = new Map();             // id -> { h, el, tag, alarm, gone }
  const spacer = document.createElement("div");
  spacer.setAttribute("aria-hidden", "true");
  document.body.appendChild(spacer);
  festiveStops.push(() => { spacer.remove(); document.documentElement.classList.remove("rgb-red"); });
  const slotX = k => Math.round(W < 700 ? W * .6 : W * .7 - k * 260);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...sticks.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeStick = h => {
    const el = document.createElement("div");
    el.className = "rgb-stick";
    el.innerHTML = RGB_STICK;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const s = { h, el, tag, alarm: 0, gone: false };
    sticks.set(h.id, s);
    return s;
  };
  const settle = () => {
    const list = order();
    spacer.style.height = list.some(s => !s.gone) ? "60px" : "0";
    list.forEach((s, k) => {
      s.el.hidden = k > (W < 700 ? 0 : 1);
      s.el.style.left = slotX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!sticks.has(h.id)) makeStick(h);
    for (const h of held) { const s = sticks.get(h.id); if (s && !s.gone) { s.h = h; if (!s.alarm || Date.now() - s.alarm > 9000) intruderTagEl(h, "held", s.tag); } }
    for (const h of cleared) {
      const s = sticks.get(h.id); if (!s || s.gone) continue;
      s.gone = true;
      const done = () => { s.el.remove(); sticks.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: its light goes green, it ejects, and the rig flashes white.
      intruderTagEl(s.h, "cleared", s.tag);
      s.el.classList.add("cleared");
      festiveTimers.push(setTimeout(() => s.el.classList.add("eject"), 2200));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) briefly("flash-white", 900); }, 3200));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const s = sticks.get(h.id) || makeStick(h);
    if (calm || document.hidden) { settle(); return; }
    s.alarm = Date.now();
    intruderTagEl(h, "alarm", s.tag);
    settle();
    // Plugged in: the frame strobes red, and the room goes red.
    s.el.classList.remove("plug"); void s.el.offsetWidth; s.el.classList.add("plug");
    redUntil = Date.now() + 8000;
    rig.classList.add("intruder");
    // Every light on the rig goes red with it, not just the frame.
    document.documentElement.classList.add("rgb-red");
    festiveTimers.push(setTimeout(() => {
      if (Date.now() < redUntil - 50) return;
      rig.classList.remove("intruder");
      document.documentElement.classList.remove("rgb-red");
    }, 8000));
    festiveTimers.push(setTimeout(() => { if (!s.gone) intruderTagEl(s.h, "held", s.tag); }, 9000));
  };

  festiveHooks.rendered = () => { setAlarm(); keys(); syncIntruders(); };
  festiveHooks.rendered();
  if (calm) return;

  festiveHooks.scanDone = () => { briefly("surge", 1600); wave = 0; };
  festiveHooks.netChange = (off, back) => {
    if (off.length) briefly("flash-red", 1100);
    else if (back.length) briefly("flash-green", 1100);
    setAlarm();
  };
}

BAMF.registerTheme("rgb", ctx => buildRgb(ctx.root, ctx.switched));
})();
