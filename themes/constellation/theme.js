// Constellation: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Constellation: the night sky, with your network written in it. Every
// device is a star at its own place in the sky (from its MAC), and they're
// joined by faint constellation lines; unknown devices shine red and offline
// ones dim. The moon's phase is how much of the network is up: full when
// everything is. Behind them, the Milky Way, nebulae, a slowly turning spiral
// galaxy and a ringed planet; stars twinkle, a satellite crosses now and then
// and shooting stars streak by. A finished scan brings a meteor shower, a new
// device arrives as a bright shooting star and becomes a star of its own, and
// a device going offline collapses in a flash.
function buildConstellation(root) {
  const calm = calmMotion();
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg cs-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = "<canvas></canvas>";
  document.body.prepend(bg);
  const cv = bg.querySelector("canvas"), ctx = cv.getContext("2d");
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);
  const layer = (w, h) => { const c = document.createElement("canvas"); c.width = w * dpr; c.height = h * dpr; const x = c.getContext("2d"); x.scale(dpr, dpr); return [c, x]; };

  // The still sky, drawn once: nebulae, the Milky Way and a ringed planet.
  const [sky, sx] = layer(W, H);
  for (const [x, y, r, col] of [[W * .72, H * .3, W * .32, "rgba(150, 70, 190, .16)"], [W * .2, H * .72, W * .28, "rgba(40, 150, 170, .13)"], [W * .55, H * .85, W * .22, "rgba(200, 80, 130, .09)"]]) {
    const g = sx.createRadialGradient(x, y, 0, x, y, r);
    g.addColorStop(0, col); g.addColorStop(1, "rgba(0, 0, 0, 0)");
    sx.fillStyle = g; sx.fillRect(0, 0, W, H);
  }
  sx.save();
  sx.translate(W / 2, H / 2); sx.rotate(-.5);
  const band = sx.createLinearGradient(0, -H * .22, 0, H * .22);
  band.addColorStop(0, "rgba(180, 190, 255, 0)"); band.addColorStop(.5, "rgba(190, 200, 255, .09)"); band.addColorStop(1, "rgba(180, 190, 255, 0)");
  sx.fillStyle = band; sx.fillRect(-W, -H * .22, W * 2, H * .44);
  for (let i = 0; i < 2600; i++) {
    const x = rnd(-W, W), y = (Math.random() + Math.random() + Math.random() - 1.5) * H * .16;
    sx.fillStyle = `rgba(220, 225, 255, ${rnd(.08, .45).toFixed(2)})`;
    sx.fillRect(x, y, rnd(.5, 1.3), rnd(.5, 1.3));
  }
  sx.restore();
  // A ringed planet low on the left.
  const px = W * .09, py = H * .8, pr = Math.min(46, W * .035);
  const ring = (front) => {
    sx.save(); sx.translate(px, py); sx.rotate(-.35);
    sx.beginPath(); sx.ellipse(0, 0, pr * 2.1, pr * .55, 0, front ? 0 : Math.PI, front ? Math.PI : Math.PI * 2);
    sx.strokeStyle = "rgba(230, 200, 150, .7)"; sx.lineWidth = 4; sx.stroke();
    sx.strokeStyle = "rgba(200, 170, 120, .45)"; sx.lineWidth = 2; sx.beginPath(); sx.ellipse(0, 0, pr * 1.7, pr * .42, 0, front ? 0 : Math.PI, front ? Math.PI : Math.PI * 2); sx.stroke();
    sx.restore();
  };
  ring(false);
  const pg = sx.createRadialGradient(px - pr * .4, py - pr * .4, pr * .1, px, py, pr);
  pg.addColorStop(0, "#f2d9a6"); pg.addColorStop(.6, "#c69a5e"); pg.addColorStop(1, "#5a3f24");
  sx.fillStyle = pg; sx.beginPath(); sx.arc(px, py, pr, 0, Math.PI * 2); sx.fill();
  sx.save(); sx.beginPath(); sx.arc(px, py, pr, 0, Math.PI * 2); sx.clip();
  for (let k = -3; k <= 3; k++) { sx.fillStyle = `rgba(120, 80, 40, ${k % 2 ? .18 : .1})`; sx.fillRect(px - pr, py + k * pr * .25, pr * 2, pr * .1); }
  sx.restore();
  ring(true);

  // A spiral galaxy, drawn once and turned slowly.
  const GS = 300;
  const [gal, gx] = layer(GS, GS);
  const core = gx.createRadialGradient(GS / 2, GS / 2, 0, GS / 2, GS / 2, GS * .18);
  core.addColorStop(0, "rgba(255, 245, 220, .95)"); core.addColorStop(1, "rgba(255, 220, 180, 0)");
  gx.fillStyle = core; gx.fillRect(0, 0, GS, GS);
  for (let i = 0; i < 3000; i++) {
    // Two arms winding out from the core, loosest at the edge.
    const arm = i % 2 ? 0 : Math.PI, t = .06 + .94 * Math.pow(Math.random(), .9), r = t * GS * .47;
    const th = arm + Math.log(1 + t * 12) * 2.6 + rnd(-.22, .22);
    gx.fillStyle = i % 7 ? `rgba(200, 210, 255, ${(.8 - t * .55).toFixed(2)})` : `rgba(255, 160, 220, ${(.7 - t * .4).toFixed(2)})`;
    gx.fillRect(GS / 2 + Math.cos(th) * r + rnd(-2, 2), GS / 2 + Math.sin(th) * r + rnd(-2, 2), 1.4, 1.4);
  }
  const GX = W * .34, GY = H * .24;

  // Background stars, twinkling.
  const stars = Array.from({ length: Math.round(Math.min(700, W * H / 3000)) }, () => ({
    x: rnd(0, W), y: rnd(0, H), r: Math.random() < .08 ? rnd(1.2, 1.9) : rnd(.4, 1.1), ph: rnd(0, 6.3), sp: rnd(.6, 2.2),
    tint: pick(["255,255,255", "200,215,255", "255,236,210", "210,200,255"]) }));

  const MX = W * .87, MY = H * .17, MR = Math.min(40, W * .03);
  // The network's constellation: each device at a place of its own in the sky,
  // clear of the moon.
  const spot = h => {
    let a = 2166136261;
    for (const ch of (h.mac || String(h.id))) { a ^= ch.charCodeAt(0); a = Math.imul(a, 16777619); }
    const u = ((a >>> 0) % 10000) / 10000, v = (Math.floor((a >>> 0) / 10000) % 10000) / 10000;
    const x = W * (.06 + .88 * u), y = H * (.1 + .8 * v);
    return Math.hypot(x - MX, y - MY) < MR * 2.6 ? [x, y + MR * 4] : [x, y];
  };
  let net = [], links = [];
  const flares = [];
  const layout = () => {
    net = hosts.filter(h => !h.ignored && !h.forgotten).slice(0, 80).map(h => ({ h, p: spot(h) }));
    // Constellation lines: a minimum spanning tree over the stars, drawn short.
    links = [];
    if (net.length > 1) {
      const inTree = [0], rest = net.map((_, i) => i).slice(1);
      while (rest.length) {
        let best = null;
        for (const i of inTree) for (const j of rest) {
          const d = Math.hypot(net[i].p[0] - net[j].p[0], net[i].p[1] - net[j].p[1]);
          if (!best || d < best[2]) best = [i, j, d];
        }
        if (best[2] < Math.min(W, H) * .35) links.push([best[0], best[1]]);
        inTree.push(best[1]); rest.splice(rest.indexOf(best[1]), 1);
      }
    }
  };
  layout();
  const phase = () => {
    const live = hosts.filter(h => !h.ignored && !h.forgotten);
    return live.length ? live.filter(h => h.online).length / live.length : 1;
  };

  // The moon, lit by how much of the network is up.
  const drawMoon = () => {
    const p = Math.max(.02, Math.min(1, phase()));
    ctx.save();
    const glow = ctx.createRadialGradient(MX, MY, MR, MX, MY, MR * 3.2);
    glow.addColorStop(0, `rgba(230, 230, 255, ${(.12 * p).toFixed(3)})`); glow.addColorStop(1, "rgba(0, 0, 0, 0)");
    ctx.fillStyle = glow; ctx.fillRect(MX - MR * 4, MY - MR * 4, MR * 8, MR * 8);
    ctx.fillStyle = "#1c2140"; ctx.beginPath(); ctx.arc(MX, MY, MR, 0, Math.PI * 2); ctx.fill();
    ctx.beginPath();
    ctx.arc(MX, MY, MR, -Math.PI / 2, Math.PI / 2);
    ctx.ellipse(MX, MY, MR * Math.abs(2 * p - 1), MR, 0, Math.PI / 2, -Math.PI / 2, p < .5);
    ctx.closePath();
    ctx.fillStyle = "#e9e6d6"; ctx.fill();
    ctx.clip();
    for (const [dx, dy, r] of [[-.3, -.25, .18], [.25, .1, .13], [-.05, .4, .1], [.35, -.35, .08], [-.45, .2, .07]]) {
      ctx.fillStyle = "rgba(160, 155, 140, .45)"; ctx.beginPath(); ctx.arc(MX + dx * MR, MY + dy * MR, r * MR, 0, Math.PI * 2); ctx.fill();
    }
    ctx.restore();
  };

  const meteors = [];
  const meteor = (bright = false) => {
    const fromLeft = Math.random() < .5, ang = rnd(.35, .6);
    const speed = bright ? 17 : rnd(10, 15);
    meteors.push({ x: fromLeft ? rnd(-50, W * .5) : rnd(W * .5, W + 50), y: rnd(-20, H * .35), vx: (fromLeft ? 1 : -1) * Math.cos(ang) * speed, vy: Math.sin(ang) * speed,
      life: 1, len: bright ? 26 : rnd(12, 20), bright });
  };
  let sat = null;

  const draw = now => {
    ctx.clearRect(0, 0, W, H);
    ctx.drawImage(sky, 0, 0, W, H);
    // The galaxy, turning.
    ctx.save(); ctx.translate(GX, GY); ctx.scale(1, .42); ctx.rotate(calm ? .6 : now / 60000);
    ctx.globalAlpha = .85; ctx.drawImage(gal, -GS / 2, -GS / 2, GS, GS); ctx.restore();
    // Stars.
    const t = now / 1000;
    for (const st of stars) {
      const a = calm ? .7 : .45 + .55 * Math.abs(Math.sin(st.ph + t * st.sp));
      ctx.fillStyle = `rgba(${st.tint},${a.toFixed(2)})`;
      ctx.fillRect(st.x, st.y, st.r, st.r);
    }
    drawMoon();
    // The network's constellation.
    ctx.strokeStyle = "rgba(160, 180, 255, .22)"; ctx.lineWidth = 1;
    ctx.beginPath();
    for (const [i, j] of links) { ctx.moveTo(net[i].p[0], net[i].p[1]); ctx.lineTo(net[j].p[0], net[j].p[1]); }
    ctx.stroke();
    for (const { h, p } of net) {
      const on = h.online, col = !h.known ? "255, 130, 140" : "220, 230, 255";
      const r = on ? (h.watched ? 2.6 : 2) : 1.1;
      const tw = calm ? 1 : .8 + .2 * Math.sin(t * 2 + h.id);
      if (on) {
        const g = ctx.createRadialGradient(p[0], p[1], 0, p[0], p[1], r * 5);
        g.addColorStop(0, `rgba(${col}, ${(.45 * tw).toFixed(2)})`); g.addColorStop(1, `rgba(${col}, 0)`);
        ctx.fillStyle = g; ctx.fillRect(p[0] - r * 5, p[1] - r * 5, r * 10, r * 10);
      }
      ctx.fillStyle = `rgba(${col}, ${on ? tw.toFixed(2) : ".3"})`;
      ctx.beginPath(); ctx.arc(p[0], p[1], r, 0, Math.PI * 2); ctx.fill();
    }
    // A star collapsing or catching light: an expanding ring.
    for (let i = flares.length - 1; i >= 0; i--) {
      const f = flares[i], age = (now - f.born) / 1400;
      if (age > 1) { flares.splice(i, 1); continue; }
      ctx.strokeStyle = `rgba(${f.col}, ${(1 - age).toFixed(2)})`; ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.arc(f.x, f.y, 3 + age * 26, 0, Math.PI * 2); ctx.stroke();
    }
    // Shooting stars.
    for (let i = meteors.length - 1; i >= 0; i--) {
      const m = meteors[i];
      m.x += m.vx; m.y += m.vy; m.life -= .018;
      if (m.life <= 0 || m.y > H + 40) { meteors.splice(i, 1); continue; }
      const tx = m.x - m.vx * m.len / 4, ty = m.y - m.vy * m.len / 4;
      const g = ctx.createLinearGradient(m.x, m.y, tx, ty);
      g.addColorStop(0, `rgba(255, 255, 255, ${m.life.toFixed(2)})`); g.addColorStop(1, "rgba(180, 200, 255, 0)");
      ctx.strokeStyle = g; ctx.lineWidth = m.bright ? 2.4 : 1.4;
      ctx.beginPath(); ctx.moveTo(m.x, m.y); ctx.lineTo(tx, ty); ctx.stroke();
    }
    // A satellite, blinking as it goes.
    if (sat) {
      sat.x += sat.v;
      if (sat.x < -20 || sat.x > W + 20) sat = null;
      else if (Math.floor(now / 700) % 2) { ctx.fillStyle = "rgba(255, 255, 255, .9)"; ctx.fillRect(sat.x, sat.y + sat.x * sat.slope, 2, 2); }
    }
  };

  if (calm) {
    draw(0);
    festiveHooks.rendered = () => { layout(); draw(0); };
    return;
  }
  let raf = 0, last = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    last = now;
    draw(now);
  };
  raf = requestAnimationFrame(frame);
  festiveStops.push(() => cancelAnimationFrame(raf));
  const nextMeteor = () => festiveTimers.push(setTimeout(() => { if (!document.hidden) meteor(); nextMeteor(); }, rnd(6000, 16000)));
  nextMeteor();
  festiveTimers.push(setInterval(() => {
    if (!document.hidden && !sat && Math.random() < .5) sat = { x: Math.random() < .5 ? -10 : W + 10, y: rnd(H * .1, H * .5), slope: rnd(-.08, .08), v: 0 };
    if (sat && !sat.v) sat.v = sat.x < 0 ? .7 : -.7;
  }, 40000));

  festiveHooks.rendered = layout;
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    for (let k = 0; k < 7; k++) festiveTimers.push(setTimeout(meteor, k * rnd(120, 320)));
  };
  festiveHooks.newDevice = h => {
    if (document.hidden) return;
    meteor(true);
    if (!h) return;
    const [x, y] = spot(h);
    festiveTimers.push(setTimeout(() => {
      flares.push({ x, y, col: "185, 164, 255", born: performance.now() });
      const n = document.createElement("div");
      n.className = "cs-name";
      n.textContent = "✦ " + nameOrIp(h);
      n.style.cssText = `left:${Math.round(Math.min(x + 10, W - 200))}px;top:${Math.round(y - 8)}px`;
      root.appendChild(n);
      festiveTimers.push(setTimeout(() => n.remove(), 4100));
    }, 900));
  };
  festiveHooks.netChange = (off, back) => {
    const byId = new Map(hosts.map(h => [h.id, h]));
    for (const id of off) { const h = byId.get(id); if (h) { const [x, y] = spot(h); flares.push({ x, y, col: "255, 140, 120", born: performance.now() }); } }
    for (const id of back) { const h = byId.get(id); if (h) { const [x, y] = spot(h); flares.push({ x, y, col: "140, 240, 190", born: performance.now() }); } }
  };
}

BAMF.registerTheme("constellation", ctx => buildConstellation(ctx.root, ctx.switched));
})();
