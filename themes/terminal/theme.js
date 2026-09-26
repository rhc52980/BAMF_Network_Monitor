// Terminal: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Terminal: Battlezone, the 1980 arcade tank game, in green vectors behind
// the page. Mountains ring the horizon with the volcano and the moon, and the
// view slowly turns. Wireframe tanks prowl, one for each unknown device (a few
// at most), and a saucer drifts over now and then. A finished scan fires a
// shell at the nearest tank; a new device brings a tank in with ENEMY IN
// RANGE; a device going offline cracks the glass, as a hit did in the arcade.
// A radar sweeps in the header, with the score beside it.
const BZ_TANK = (() => {
  // Prisms as 4 bottom + 4 top corners, [x, y, z]; z is forward.
  const prism = (b, t) => [...b, ...t];
  const parts = [
    prism([[-1.5, 0, -2], [1.5, 0, -2], [1.5, 0, 2.1], [-1.5, 0, 2.1]], [[-1.3, .8, -1.8], [1.3, .8, -1.8], [1.3, .8, 1.5], [-1.3, .8, 1.5]]),
    prism([[-.8, .8, -1.1], [.8, .8, -1.1], [.8, .8, .6], [-.8, .8, .6]], [[-.55, 1.45, -.9], [.55, 1.45, -.9], [.55, 1.45, .2], [-.55, 1.45, .2]]),
    prism([[-.12, 1.02, .3], [.12, 1.02, .3], [.12, 1.02, 2.8], [-.12, 1.02, 2.8]], [[-.12, 1.24, .3], [.12, 1.24, .3], [.12, 1.24, 2.8], [-.12, 1.24, 2.8]]),
  ];
  const segs = [];
  for (const v of parts) for (const [a, b] of [[0, 1], [1, 2], [2, 3], [3, 0], [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]]) segs.push([v[a], v[b]]);
  return segs;
})();

const BZ_SAUCER = (() => {
  const segs = [], ring = [];
  for (let i = 0; i < 8; i++) { const a = i / 8 * Math.PI * 2; ring.push([Math.cos(a) * 2, 3.2, Math.sin(a) * 2]); }
  ring.forEach((p, i) => { segs.push([p, ring[(i + 1) % 8]], [p, [0, 4.1, 0]], [p, [0, 2.6, 0]]); });
  return segs;
})();

function buildTerminal(root) {
  const calm = calmMotion();
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg bz-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = "<canvas></canvas>";
  document.body.prepend(bg);
  const cv = bg.querySelector("canvas"), ctx = cv.getContext("2d");
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);
  const HZ = Math.round(H * .62), F = W * .55, CAM = 2.2, RING = Math.round(2 * Math.PI * F);
  const G = "#33ff66";

  // The horizon's ring of mountains, and where the volcano and moon stand on it.
  const ridge = [];
  for (let x = 0; x <= RING; x += 50) ridge.push([x, x % RING === 0 ? 30 : 12 + Math.abs(Math.sin(x * .013) * 46 + Math.sin(x * .031) * 22) + (x / 50 % 3 === 0 ? 14 : 0)]);
  const VOLCANO = 850, MOON = 1900;
  let heading = 0;
  const lava = [];

  // The radar and score in the header.
  const radar = document.createElement("canvas");
  radar.className = "bz-radar";
  radar.width = radar.height = 60;
  const score = document.createElement("span");
  score.className = "bz-score";
  const track = document.querySelector(".scanline .scan-track");
  track?.parentElement.insertBefore(radar, track);
  radar.after(score);
  festiveStops.push(() => { radar.remove(); score.remove(); });
  let kills = 0;
  const showScore = () => { score.textContent = `SCORE ${String(kills * 1000).padStart(5, "0")}`; };
  showScore();

  const enemies = [];
  const shells = [];
  const bits = [];
  let crack = null;
  const want = () => Math.min(4, 1 + hosts.filter(h => !h.known && !h.ignored && !h.forgotten).length);
  const spawn = (type = "tank") => {
    // Somewhere ahead, off to one side, heading across the view.
    const a = heading / RING * Math.PI * 2 + rnd(-.7, .7), d = rnd(45, 90);
    enemies.push({ type, x: Math.sin(a) * d, z: Math.cos(a) * d, dir: rnd(0, Math.PI * 2), v: type === "saucer" ? 5 : rnd(1.5, 3), turnAt: 0, spin: 0 });
  };
  for (let i = 0; i < want(); i++) spawn();

  // World to screen, turned by the view's heading.
  const view = (x, y, z) => {
    const a = -heading / RING * Math.PI * 2, c = Math.cos(a), s2 = Math.sin(a);
    const xr = x * c + z * s2, zr = -x * s2 + z * c;
    if (zr < 3) return null;
    return [W / 2 + xr / zr * F, HZ + (CAM - y) / zr * F];
  };
  const drawModel = (segs, e, yaw, lift = 0) => {
    const c = Math.cos(yaw), s2 = Math.sin(yaw);
    ctx.beginPath();
    for (const [p, q] of segs) {
      const A = view(e.x + p[0] * c + p[2] * s2, p[1] + lift, e.z - p[0] * s2 + p[2] * c);
      const B = view(e.x + q[0] * c + q[2] * s2, q[1] + lift, e.z - q[0] * s2 + q[2] * c);
      if (!A || !B) continue;
      ctx.moveTo(A[0], A[1]); ctx.lineTo(B[0], B[1]);
    }
    ctx.stroke();
  };

  const draw = now => {
    ctx.clearRect(0, 0, W, H);
    ctx.strokeStyle = G; ctx.fillStyle = G; ctx.lineWidth = 1.3;
    ctx.shadowColor = G; ctx.shadowBlur = 4;
    // Horizon, mountains, volcano and moon, scrolling as the view turns.
    ctx.beginPath(); ctx.moveTo(0, HZ); ctx.lineTo(W, HZ); ctx.stroke();
    const off = -(heading % RING);
    for (let k = -1; k <= Math.ceil(W / RING) + 1; k++) {
      const base = off + k * RING;
      if (base > W || base + RING < 0) continue;
      ctx.beginPath();
      ridge.forEach(([x, h], i) => { const px = base + x, py = HZ - h; i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); });
      ctx.stroke();
      const vx = base + VOLCANO;
      ctx.beginPath(); ctx.moveTo(vx - 150, HZ); ctx.lineTo(vx - 26, HZ - 118); ctx.lineTo(vx + 26, HZ - 118); ctx.lineTo(vx + 150, HZ);
      ctx.moveTo(vx - 26, HZ - 118); ctx.lineTo(vx - 8, HZ - 106); ctx.lineTo(vx + 8, HZ - 110); ctx.lineTo(vx + 26, HZ - 118); ctx.stroke();
      const mx = base + MOON, my = HZ - 190;
      ctx.beginPath(); ctx.arc(mx, my, 26, -Math.PI * .6, Math.PI * .6, true); ctx.arc(mx + 12, my, 22, Math.PI * .55, -Math.PI * .55); ctx.stroke();
    }
    // The volcano spits vector lava.
    if (!calm && Math.random() < .08) lava.push({ x: VOLCANO + rnd(-14, 14), y: 118, vx: rnd(-1.4, 1.4), vy: rnd(3, 5.5) });
    ctx.beginPath();
    for (let i = lava.length - 1; i >= 0; i--) {
      const l = lava[i];
      l.x += l.vx; l.y += l.vy; l.vy -= .16;
      if (l.y < 70) { lava.splice(i, 1); continue; }
      for (let k = -1; k <= 1; k++) { const px = off + k * RING + l.x; ctx.moveTo(px, HZ - l.y); ctx.lineTo(px - l.vx * 2, HZ - l.y + l.vy * 2); }
    }
    ctx.stroke();
    // Enemies, far ones first.
    const t = now / 1000;
    enemies.sort((a, b) => Math.hypot(b.x, b.z) - Math.hypot(a.x, a.z));
    for (const e of enemies) {
      if (e.type === "saucer") drawModel(BZ_SAUCER, e, e.spin, 1 + Math.sin(t * 2) * .4);
      else drawModel(BZ_TANK, e, e.dir);
    }
    // Shells, and the pieces of what they hit.
    ctx.beginPath();
    for (const sh of shells) {
      const A = view(sh.x, 1.1, sh.z), B = view(sh.x - sh.vx * .3, 1.1, sh.z - sh.vz * .3);
      if (A && B) { ctx.moveTo(A[0], A[1]); ctx.lineTo(B[0], B[1]); }
    }
    for (const b of bits) {
      const A = view(b.x, b.y, b.z), B = view(b.x + b.dx, b.y + b.dy, b.z + b.dz);
      if (A && B) { ctx.moveTo(A[0], A[1]); ctx.lineTo(B[0], B[1]); }
    }
    ctx.stroke();
    // The gunsight.
    ctx.shadowBlur = 0; ctx.globalAlpha = .55;
    const cx = W / 2, cy = HZ + 40;
    ctx.beginPath();
    ctx.moveTo(cx - 40, cy - 18); ctx.lineTo(cx - 40, cy - 28); ctx.lineTo(cx - 12, cy - 28);
    ctx.moveTo(cx + 40, cy - 18); ctx.lineTo(cx + 40, cy - 28); ctx.lineTo(cx + 12, cy - 28);
    ctx.moveTo(cx - 40, cy + 18); ctx.lineTo(cx - 40, cy + 28); ctx.lineTo(cx - 12, cy + 28);
    ctx.moveTo(cx + 40, cy + 18); ctx.lineTo(cx + 40, cy + 28); ctx.lineTo(cx + 12, cy + 28);
    ctx.moveTo(cx, cy - 28); ctx.lineTo(cx, cy - 44); ctx.moveTo(cx, cy + 28); ctx.lineTo(cx, cy + 44);
    ctx.stroke();
    ctx.globalAlpha = 1;
    // A hit: cracks across the glass, fading.
    if (crack) {
      const age = now - crack.born;
      if (age > 2600) crack = null;
      else {
        ctx.globalAlpha = 1 - age / 2600;
        ctx.strokeStyle = "#b8ffc8"; ctx.lineWidth = 1.6;
        ctx.beginPath();
        for (const line of crack.lines) line.forEach(([x, y], i) => i ? ctx.lineTo(x, y) : ctx.moveTo(x, y));
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
    }
  };

  // The radar: a sweep, with a blip for each enemy.
  const rctx = radar.getContext("2d");
  const drawRadar = now => {
    rctx.clearRect(0, 0, 60, 60);
    rctx.strokeStyle = G; rctx.fillStyle = G; rctx.lineWidth = 1.5;
    rctx.beginPath(); rctx.arc(30, 30, 27, 0, Math.PI * 2); rctx.stroke();
    rctx.beginPath(); rctx.moveTo(30, 5); rctx.lineTo(30, 10); rctx.stroke();
    const a = calm ? -Math.PI / 2 : now / 900 % (Math.PI * 2);
    rctx.beginPath(); rctx.moveTo(30, 30); rctx.lineTo(30 + Math.cos(a) * 26, 30 + Math.sin(a) * 26); rctx.stroke();
    const h = -heading / RING * Math.PI * 2;
    for (const e of enemies) {
      const xr = e.x * Math.cos(h) + e.z * Math.sin(h), zr = -e.x * Math.sin(h) + e.z * Math.cos(h);
      const d = Math.hypot(xr, zr);
      if (d > 110) continue;
      rctx.fillRect(30 + xr / 110 * 26 - 1.5, 30 - zr / 110 * 26 - 1.5, 3, 3);
    }
  };

  const message = text => {
    const hd = document.querySelector("header");
    const m = document.createElement("div");
    m.className = "bz-msg";
    m.textContent = text;
    m.style.top = ((hd?.getBoundingClientRect().bottom || 60) + 14) + "px";
    root.appendChild(m);
    festiveTimers.push(setTimeout(() => m.remove(), 2700));
  };
  const explode = e => {
    for (const [p, q] of BZ_TANK.filter((_, i) => i % 2 === 0))
      bits.push({ x: e.x + p[0], y: p[1], z: e.z + p[2], dx: q[0] - p[0], dy: q[1] - p[1], dz: q[2] - p[2], vx: rnd(-6, 6), vy: rnd(3, 9), vz: rnd(-6, 6), life: 1.6 });
  };

  if (calm) {
    // Reduced motion: the battlefield, still.
    draw(0); drawRadar(0);
  } else {
    let raf = 0, last = 0;
    const frame = now => {
      raf = requestAnimationFrame(frame);
      if (now - last < 33) return;
      const dt = Math.min(.1, (now - last) / 1000);
      last = now;
      heading = (heading + dt * 14) % RING;
      for (const e of enemies) {
        if (e.type === "saucer") { e.spin += dt * 2; e.x += Math.cos(e.dir) * e.v * dt; e.z += Math.sin(e.dir) * e.v * dt; continue; }
        if (now > e.turnAt) { e.dir += rnd(-.9, .9); e.turnAt = now + rnd(3000, 7000); }
        e.x += Math.sin(e.dir) * e.v * dt; e.z += Math.cos(e.dir) * e.v * dt;
      }
      // Anything wandered out of range comes back somewhere else.
      for (let i = enemies.length - 1; i >= 0; i--) {
        const d = Math.hypot(enemies[i].x, enemies[i].z);
        if (d > 140 || d < 8) { const was = enemies[i].type; enemies.splice(i, 1); if (was === "tank") spawn(); }
      }
      while (enemies.filter(e => e.type === "tank").length < want()) spawn();
      for (let i = shells.length - 1; i >= 0; i--) {
        const sh = shells[i];
        sh.x += sh.vx * dt; sh.z += sh.vz * dt; sh.life -= dt;
        const hit = enemies.find(e => e.type === "tank" && Math.hypot(e.x - sh.x, e.z - sh.z) < 3);
        if (hit) {
          explode(hit);
          enemies.splice(enemies.indexOf(hit), 1);
          shells.splice(i, 1);
          kills++; showScore();
          festiveTimers.push(setTimeout(() => spawn(), 4000));
        } else if (sh.life <= 0) shells.splice(i, 1);
      }
      for (let i = bits.length - 1; i >= 0; i--) {
        const b = bits[i];
        b.x += b.vx * dt; b.y = Math.max(0, b.y + b.vy * dt); b.z += b.vz * dt; b.vy -= 12 * dt; b.life -= dt;
        if (b.life <= 0) bits.splice(i, 1);
      }
      draw(now); drawRadar(now);
    };
    raf = requestAnimationFrame(frame);
    festiveStops.push(() => cancelAnimationFrame(raf));
    // A saucer passes over every so often.
    festiveTimers.push(setInterval(() => { if (!document.hidden && !enemies.some(e => e.type === "saucer")) spawn("saucer"); }, 45000));
  }

  festiveHooks.scanDone = () => {
    if (calm || document.hidden) return;
    // Fire at the nearest tank in front, or straight ahead.
    const a = heading / RING * Math.PI * 2;
    const ahead = enemies.filter(e => e.type === "tank").sort((p, q) => Math.hypot(p.x, p.z) - Math.hypot(q.x, q.z))[0];
    const tx = ahead ? ahead.x : Math.sin(a) * 60, tz = ahead ? ahead.z : Math.cos(a) * 60, d = Math.hypot(tx, tz) || 1;
    shells.push({ x: 0, z: 0, vx: tx / d * 40, vz: tz / d * 40, life: 3 });
  };
  festiveHooks.newDevice = () => {
    message("ENEMY IN RANGE");
    if (!calm) spawn();
  };
  festiveHooks.netChange = off => {
    if (!off.length || calm || document.hidden) return;
    const x0 = rnd(W * .25, W * .75), y0 = rnd(H * .3, H * .7), lines = [];
    for (let k = 0; k < 7; k++) {
      let x = x0, y = y0, ang = k / 7 * Math.PI * 2 + rnd(-.3, .3);
      const line = [[x, y]];
      for (let j = 0; j < 5; j++) { ang += rnd(-.5, .5); const len = rnd(30, 90); x += Math.cos(ang) * len; y += Math.sin(ang) * len; line.push([x, y]); }
      lines.push(line);
    }
    crack = { lines, born: performance.now() };
  };
}

BAMF.registerTheme("terminal", ctx => buildTerminal(ctx.root, ctx.switched));
})();
