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
  let t = 0, wave = -1, alarm = false;
  const blobs = Array.from({ length: 4 }, (_, i) => ({ x: Math.random(), y: .2 + Math.random() * .6, r: .35 + Math.random() * .25, h: HUES[i * 2 % HUES.length], sx: rnd(-.02, .02), sy: rnd(-.015, .015) }));
  let room = null;
  const draw = () => {
    const { ctx, W, H } = room;
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
  festiveHooks.rendered = () => { setAlarm(); keys(); };
  festiveHooks.rendered();
  if (calm) return;

  festiveHooks.scanDone = () => { briefly("surge", 1600); wave = 0; };
  festiveHooks.newDevice = () => briefly("flash-white", 900);
  festiveHooks.netChange = (off, back) => {
    if (off.length) briefly("flash-red", 1100);
    else if (back.length) briefly("flash-green", 1100);
    setAlarm();
  };
}

BAMF.registerTheme("rgb", ctx => buildRgb(ctx.root, ctx.switched));
})();
