// Laser Show: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Laser Show: a club rig in a dark room. Fan beams sweep out of two emitters
// through the haze, a projector traces a figure that leaves a glowing trail,
// and a grid runs back to the horizon. A finished scan snaps every beam into a
// starburst; a new device sends one hard beam across the room.
//
// A new device nobody has marked known is an intruder: a rogue droid in
// neither squad's colours, beamed into no-man's-land. Every beam in the room
// turns red and swings onto it, the fight stops, and both squads stand up and
// hold it in their sights; it stands there, tagged, until the device is marked
// known. Then it beams out, the squads drop back into cover, and a hard beam
// goes across the room for the device.
const LZ_COLORS = [[0, 229, 255], [255, 47, 209], [157, 255, 61], [255, 213, 74]];

function buildLaser(root, switched) {
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
  const horizon = H * .62;

  // Two rigs up in the corners, each throwing a fan of beams.
  const rigs = [
    { x: W * .08, y: 8, dir: 1, spread: .5, phase: 0, speed: .55, col: 0 },
    { x: W * .92, y: 8, dir: -1, spread: .5, phase: 2.1, speed: .43, col: 1 },
  ];
  let flare = 0, shot = null, figure = 0, figureAt = 0;

  const beam = (x, y, ang, len, rgb, width, alpha) => {
    const x2 = x + Math.cos(ang) * len, y2 = y + Math.sin(ang) * len;
    const g = ctx.createLinearGradient(x, y, x2, y2);
    g.addColorStop(0, `rgba(${rgb}, ${(alpha * .95).toFixed(3)})`);
    g.addColorStop(.75, `rgba(${rgb}, ${(alpha * .22).toFixed(3)})`);
    g.addColorStop(1, `rgba(${rgb}, 0)`);
    ctx.strokeStyle = g;
    ctx.lineWidth = width;
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.lineTo(x2, y2);
    ctx.stroke();
  };

  const draw = (now) => {
    const t = (now || 0) / 1000;
    // The room, and last frame's light fading out of the haze.
    ctx.globalCompositeOperation = "source-over";
    ctx.fillStyle = "rgba(5, 6, 10, .34)";
    ctx.fillRect(0, 0, W, H);
    ctx.globalCompositeOperation = "lighter";

    // Grid running back to the horizon.
    ctx.strokeStyle = "rgba(0, 229, 255, .1)";
    ctx.lineWidth = 1;
    for (let i = -14; i <= 14; i++) {
      ctx.beginPath();
      ctx.moveTo(W / 2 + i * (W / 12), H);
      ctx.lineTo(W / 2 + i * 14, horizon);
      ctx.stroke();
    }
    for (let i = 1; i <= 12; i++) {
      const y = horizon + Math.pow(i / 12, 2.1) * (H - horizon);
      const off = calm ? 0 : (t * .25 % 1) * (Math.pow((i + 1) / 12, 2.1) - Math.pow(i / 12, 2.1)) * (H - horizon);
      ctx.beginPath();
      ctx.moveTo(0, y + off);
      ctx.lineTo(W, y + off);
      ctx.stroke();
    }

    // The fans; red, and every beam on the rogue, while an intruder's alarm is on.
    const alert = rogueAlert(now || 0);
    for (const r of rigs) {
      const swing = calm ? .35 : Math.sin(t * r.speed + r.phase) * .55;
      const rgb = alert ? "255, 59, 48" : LZ_COLORS[r.col].join(", ");
      const onIt = alert ? Math.atan2(H - 40 - r.y, alert.x - r.x) : 0;
      for (let i = -4; i <= 4; i++) {
        const ang = alert ? onIt + i * .012 * r.dir : Math.PI / 2 + swing + i * (r.spread / 4) * r.dir + (flare ? i * flare * .18 : 0);
        beam(r.x, r.y, ang, H * 1.25, rgb, 2 + Math.abs(i) * .25, (.5 - Math.abs(i) * .045) * (1 + flare));
      }
      // The lens itself.
      ctx.fillStyle = `rgba(${rgb}, .9)`;
      ctx.beginPath();
      ctx.arc(r.x, r.y, 4 + flare * 3, 0, Math.PI * 2);
      ctx.fill();
    }

    // The projector: a Lissajous figure traced in light, redrawn each frame so
    // the trail lives in the haze rather than in a list of points.
    const [a, b] = [[3, 2], [5, 4], [3, 4], [5, 3]][figure];
    const cx = W / 2, cy = horizon - Math.min(H, W) * .19, rx = Math.min(W * .19, 230), ry = Math.min(H * .13, 125);
    const rgbF = LZ_COLORS[2].join(", ");
    const at = u => [cx + Math.sin(a * u + (calm ? 0 : t * .3)) * rx, cy + Math.sin(b * u) * ry];
    // Drawn as short segments so the tail can fade behind the head, the way a
    // real projector's dot lingers in the haze.
    ctx.lineWidth = 2;
    const head = calm ? 0 : (t * 1.5) % (Math.PI * 2), step = .03;
    for (let u = 0; u < Math.PI * 2; u += step) {
      const behind = calm ? .55 : Math.max(0, 1 - ((head - u + Math.PI * 2) % (Math.PI * 2)) / 2.4);
      if (behind <= .02) continue;
      const [x1, y1] = at(u), [x2, y2] = at(u + step);
      ctx.strokeStyle = `rgba(${rgbF}, ${(behind * (.5 + flare * .4)).toFixed(3)})`;
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2, y2);
      ctx.stroke();
    }

    // One hard beam across the room when a device turns up.
    if (shot) {
      const p = Math.min(1, (now - shot.at) / 900);
      const rgbS = LZ_COLORS[3].join(", ");
      beam(shot.x0, shot.y0, Math.atan2(shot.y1 - shot.y0, shot.x1 - shot.x0), Math.hypot(shot.x1 - shot.x0, shot.y1 - shot.y0) * p, rgbS, 3, .9 * (1 - p * .5));
      if (p >= 1) shot = null;
    }
    ctx.globalCompositeOperation = "source-over";
  };


  // ---- The droids ----
  // Two squads dug in along the floor behind barricades, cyan on the left and
  // magenta on the right, popping up to trade fire across the room. Every
  // bolt comes out a colour of its own. They're on a canvas in front of the
  // page, since anything that low on the back one is behind the device table.
  //
  // The network runs the fight. A finished scan or a new device starts a
  // firefight. A device dropping off takes a droid down and keeps it down,
  // and the device coming back gets that droid up again.
  const fx = frontCanvas(root);
  const S = Math.max(.7, Math.min(1.15, W / 1400));
  const GY = H - 3;
  // They fight on a floor of their own along the bottom. It's solid, so a
  // droid never stands on top of a row of the table and garbles it, and the
  // page gets that much more room at the bottom so the last rows still
  // scroll clear of it.
  const BAND = Math.round(66 * S);
  // The room is a spacer after everything else, not padding: the body is the
  // height of the window and the page overflows it, so padding on the body
  // would land mid-page and make no room at all.
  const room = document.createElement("div");
  room.style.height = `${BAND}px`;
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const perSide = W < 700 ? 2 : W < 1200 ? 3 : 4;
  const TEAM = ["0, 229, 255", "255, 47, 209"];
  const droids = [], bolts = [], sparks = [];
  for (let side = 0; side < 2; side++) {
    for (let i = 0; i < perSide; i++) {
      const f = .04 + i * (.22 / Math.max(1, perSide - 1));
      droids.push({ side, dir: side ? -1 : 1, x: side ? W * (1 - f) : W * f,
        rise: 0, want: 0, state: "cover", next: 0, shots: 0, shotAt: 0, aim: 0,
        tilt: 0, downUntil: 0, pinned: null, target: null, flash: 0 });
    }
  }
  let heat = .25, skirmishAt = 0;
  // Where each piece sits, in screen space.
  const liftOf = d => (1 - d.rise) * 28 * S;
  // The barricade: its front well out ahead of the droid, and its back just
  // past the droid's own back, so nothing of a crouched one shows.
  const cover = d => { const f = d.x + d.dir * 19 * S, r = d.x - d.dir * 10 * S;
    return { x0: Math.min(f, r), x1: Math.max(f, r), y0: GY - 28 * S, y1: GY }; };
  const body = d => ({ x0: d.x - 9 * S, x1: d.x + 9 * S, y0: GY + liftOf(d) - 50 * S, y1: GY + liftOf(d) - 15 * S });
  const inBox = (x, y, b) => x >= b.x0 && x <= b.x1 && y >= b.y0 && y <= b.y1;
  const shoulder = d => [d.x + d.dir * 4 * S, GY + liftOf(d) - 31 * S];
  const rr = (c, x, y, w, h, r) => {
    c.beginPath();
    c.moveTo(x + r, y); c.arcTo(x + w, y, x + w, y + h, r); c.arcTo(x + w, y + h, x, y + h, r);
    c.arcTo(x, y + h, x, y, r); c.arcTo(x, y, x + w, y, r); c.closePath();
  };
  const burst = (x, y, rgb, n) => {
    for (let i = 0; i < n; i++) {
      const a = rnd(0, Math.PI * 2), v = rnd(60, 260) * S;
      sparks.push({ x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v - 90 * S, life: 1, rgb: Math.random() < .35 ? "255, 255, 255" : rgb });
    }
  };
  const knock = (d, now) => {
    d.state = "down"; d.want = 0; d.shots = 0;
    d.downUntil = now + rnd(2600, 4600);
    burst(d.x, GY + liftOf(d) - 34 * S, TEAM[d.side], 16);
  };
  const enemiesOf = d => droids.filter(e => e.side !== d.side && e.state !== "down");

  function fire(d) {
    const e = d.target;
    if (!e || e.state === "down") return;
    const [sx, sy] = shoulder(d);
    const tx = e.x, ty = GY + liftOf(e) - 32 * S + rnd(-9, 9) * S;
    // Muzzle at the end of the blaster, along the aim.
    const mx = sx + d.dir * Math.cos(d.aim) * 24 * S, my = sy + Math.sin(d.aim) * 24 * S;
    const len = Math.hypot(tx - mx, ty - my) || 1, v = 1100 * S;
    bolts.push({ x: mx, y: my, vx: (tx - mx) / len * v, vy: (ty - my) / len * v, hue: Math.floor(rnd(0, 360)), side: d.side });
    d.flash = 1;
  }

  function fight(dt, now) {
    if (now > skirmishAt) { heat = 1; skirmishAt = now + rnd(25e3, 45e3); }
    heat = Math.max(.25, heat - dt / 9);
    const rogue = standoff();
    for (const d of droids) {
      // While there's a rogue on the floor, the fight's off: everyone who's
      // standing holds it in their sights.
      if (rogue && d.state !== "down") { d.state = "hold"; d.want = 1; d.target = null; aimAt(d, rogue, dt); }
      else if (d.state === "hold") { d.state = "cover"; d.next = now + rnd(600, 2400); }
      if (d.state === "down") {
        d.tilt = Math.min(1.35, d.tilt + dt * 6);
        if (!d.pinned && now > d.downUntil) { d.state = "cover"; d.next = now + rnd(700, 1800); }
      } else {
        d.tilt = Math.max(0, d.tilt - dt * 4);
      }
      if (d.state === "cover") {
        d.want = 0;
        d.aim += (-1.2 - d.aim) * Math.min(1, dt * 8);
        if (now > d.next) {
          const foes = enemiesOf(d);
          if (foes.length) {
            d.state = "up"; d.target = pick(foes);
            d.shots = 1 + Math.floor(rnd(0, 2 + heat * 3));
            d.shotAt = now + rnd(220, 480);
          } else d.next = now + 1500;
        }
      }
      if (d.state === "up") {
        d.want = 1;
        if (d.target) {
          const [sx, sy] = shoulder(d);
          const want = Math.atan2(GY + liftOf(d.target) - 32 * S - sy, (d.target.x - sx) * d.dir);
          d.aim += (want - d.aim) * Math.min(1, dt * 10);
        }
        if (d.rise > .85 && now > d.shotAt) {
          if (d.shots > 0 && d.target && d.target.state !== "down") { fire(d); d.shots--; d.shotAt = now + rnd(160, 380); }
          else { d.state = "cover"; d.next = now + rnd(1200, 5500) * (1 - .7 * heat); }
        }
      }
      d.rise += (d.want - d.rise) * Math.min(1, dt * 9);
      d.flash = Math.max(0, d.flash - dt * 8);
    }
    // Bolts, stepped in short hops so a fast one can't skip over a droid.
    for (let i = bolts.length - 1; i >= 0; i--) {
      const b = bolts[i];
      const hops = Math.max(1, Math.ceil(Math.hypot(b.vx, b.vy) * dt / (5 * S)));
      let dead = false;
      for (let h = 0; h < hops && !dead; h++) {
        b.x += b.vx * dt / hops; b.y += b.vy * dt / hops;
        for (const e of droids) {
          if (e.side === b.side) continue;
          if (inBox(b.x, b.y, cover(e))) { burst(b.x, b.y, `${hslRgb(b.hue)}`, 7); e.flash = Math.max(e.flash, .5); dead = true; break; }
          if (e.state !== "down" && e.rise > .5 && inBox(b.x, b.y, body(e))) { knock(e, now); dead = true; break; }
        }
      }
      if (b.y > GY) { burst(b.x, GY, hslRgb(b.hue), 5); dead = true; }
      if (dead || b.x < -60 || b.x > W + 60 || b.y < -60) bolts.splice(i, 1);
    }
    for (let i = sparks.length - 1; i >= 0; i--) {
      const p = sparks[i];
      p.vy += 900 * S * dt; p.x += p.vx * dt; p.y += p.vy * dt; p.life -= dt * 2.2;
      if (p.life <= 0) sparks.splice(i, 1);
    }
    drawFight();
  }

  // hsl(h, 100%, 60%) as an "r, g, b" string, for the rgba() the sparks use.
  function hslRgb(h) {
    const f = n => { const k = (n + h / 30) % 12; return Math.round(255 * (.6 - .4 * Math.max(-1, Math.min(k - 3, 9 - k, 1)))); };
    return `${f(0)}, ${f(8)}, ${f(4)}`;
  }

  function drawDroid(d) {
    const glow = d.state === "down" ? .22 : .95;
    fx.save();
    fx.translate(d.x, GY + liftOf(d));
    if (d.tilt) fx.rotate(-d.dir * d.tilt);
    fx.scale(d.dir * S, S);
    fx.fillStyle = "#171c29";
    fx.fillRect(-6, -16, 4, 16); fx.fillRect(2, -16, 4, 16);
    fx.fillRect(-8, -3, 7, 3); fx.fillRect(1, -3, 7, 3);
    fx.fillStyle = "#232b3d";
    rr(fx, -9, -36, 18, 21, 4); fx.fill();
    fx.fillStyle = `rgba(${TEAM[d.side]}, ${glow * .85})`;
    fx.fillRect(-9, -27, 18, 2);
    fx.fillStyle = "#2c3549";
    rr(fx, -7, -49, 14, 12, 5); fx.fill();
    fx.fillStyle = `rgba(${TEAM[d.side]}, ${glow})`;
    fx.fillRect(0, -45, 7, 3);
    fx.strokeStyle = "#3b4661"; fx.lineWidth = 1.5;
    fx.beginPath(); fx.moveTo(-3, -49); fx.lineTo(-5, -56); fx.stroke();
    fx.beginPath(); fx.arc(-5, -57, 1.7, 0, Math.PI * 2); fx.fill();
    fx.save();
    fx.translate(4, -31);
    fx.rotate(d.state === "down" ? .6 : d.aim);
    fx.fillStyle = "#171c29"; fx.fillRect(0, -2, 10, 4);
    fx.fillStyle = "#3a445c"; fx.fillRect(8, -3.5, 13, 6);
    fx.fillStyle = "#0e1119"; fx.fillRect(19, -2, 5, 3);
    if (d.flash > 0) {
      fx.globalCompositeOperation = "lighter";
      fx.fillStyle = `rgba(255, 255, 255, ${(d.flash * .9).toFixed(2)})`;
      fx.beginPath(); fx.arc(25, -.5, 4 * d.flash + 1, 0, Math.PI * 2); fx.fill();
      fx.globalCompositeOperation = "source-over";
    }
    fx.restore();
    fx.restore();
  }

  function drawCover(d) {
    const c = cover(d);
    fx.fillStyle = "#141926";
    fx.fillRect(c.x0, c.y0, c.x1 - c.x0, c.y1 - c.y0);
    fx.strokeStyle = `rgba(${TEAM[d.side]}, .55)`;
    fx.lineWidth = 2;
    fx.beginPath(); fx.moveTo(c.x0, c.y0 + 1); fx.lineTo(c.x1, c.y0 + 1); fx.stroke();
    fx.strokeStyle = "rgba(255, 213, 74, .18)";
    fx.lineWidth = 3 * S;
    for (let k = 0; k < 3; k++) {
      const x = c.x0 + (k + .5) * (c.x1 - c.x0) / 3;
      fx.beginPath(); fx.moveTo(x - 4 * S, c.y1 - 4 * S); fx.lineTo(x + 4 * S, c.y0 + 8 * S); fx.stroke();
    }
    if (d.flash > 0 && d.state !== "up") {
      fx.fillStyle = `rgba(255, 255, 255, ${(d.flash * .25).toFixed(2)})`;
      fx.fillRect(c.x0, c.y0, c.x1 - c.x0, c.y1 - c.y0);
    }
  }

  function drawFloor() {
    const y0 = H - BAND;
    fx.save();
    fx.globalAlpha = 1;
    const g = fx.createLinearGradient(0, y0, 0, H);
    g.addColorStop(0, "#0b0d17"); g.addColorStop(1, "#05060a");
    fx.fillStyle = g;
    fx.fillRect(0, y0, W, BAND);
    // A grid on it, running back like the one behind the page.
    fx.strokeStyle = "rgba(0, 229, 255, .09)";
    fx.lineWidth = 1;
    for (let i = -10; i <= 10; i++) {
      fx.beginPath(); fx.moveTo(W / 2 + i * W / 9, H); fx.lineTo(W / 2 + i * W / 15, y0); fx.stroke();
    }
    // Its edge, lit end to end in the two squads' colours.
    const e = fx.createLinearGradient(0, 0, W, 0);
    e.addColorStop(0, "rgba(0, 229, 255, .95)");
    e.addColorStop(.5, "rgba(170, 120, 255, .55)");
    e.addColorStop(1, "rgba(255, 47, 209, .95)");
    fx.fillStyle = e;
    fx.fillRect(0, y0, W, 2);
    fx.globalAlpha = .16;
    fx.fillRect(0, y0 - 4, W, 10);
    fx.restore();
  }

  function drawFight() {
    fx.clearRect(0, 0, W, H);
    drawFloor();
    fx.save();
    fx.beginPath(); fx.rect(0, 0, W, GY); fx.clip();
    for (const d of droids) drawDroid(d);
    fx.restore();
    for (const d of droids) drawCover(d);
    drawRogues();
    fx.globalCompositeOperation = "lighter";
    fx.lineCap = "round";
    for (const b of bolts) {
      const sp = Math.hypot(b.vx, b.vy) || 1, L = 26 * S;
      const x2 = b.x - b.vx / sp * L, y2 = b.y - b.vy / sp * L;
      fx.strokeStyle = `hsla(${b.hue}, 100%, 60%, .45)`;
      fx.lineWidth = 6 * S;
      fx.beginPath(); fx.moveTo(b.x, b.y); fx.lineTo(x2, y2); fx.stroke();
      fx.strokeStyle = `hsl(${b.hue}, 100%, 88%)`;
      fx.lineWidth = 2 * S;
      fx.beginPath(); fx.moveTo(b.x, b.y); fx.lineTo(x2, y2); fx.stroke();
    }
    for (const p of sparks) {
      fx.strokeStyle = `rgba(${p.rgb}, ${Math.max(0, p.life).toFixed(2)})`;
      fx.lineWidth = 1.5 * S;
      fx.beginPath(); fx.moveTo(p.x, p.y); fx.lineTo(p.x - p.vx * .02, p.y - p.vy * .02); fx.stroke();
    }
    fx.globalCompositeOperation = "source-over";
  }

  // ---- intruders: rogue droids in no-man's-land ----
  const watchIn = intruderWatch();
  const rogues = new Map();             // id -> { h, x, alarm, bornAt, clearAt, outAt }
  const slotX = k => W < 700 ? W * .5 : W * (k ? .41 : .57);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...rogues.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const newRogue = h => ({ h, x: null, alarm: 0, bornAt: 0, clearAt: 0, outAt: 0 });
  const standoff = () => order().find(r => !r.clearAt && r.x != null) || null;
  function rogueAlert(now) {
    const r = order().find(q => !q.clearAt && q.alarm && now - q.alarm < 8000);
    return r && r.x != null ? r : null;
  }
  function aimAt(d, r, dt) {
    const [sx, sy] = shoulder(d);
    const want = Math.atan2(GY - 34 * S - sy, (r.x - sx) * d.dir);
    d.aim += (want - d.aim) * (dt ? Math.min(1, dt * 10) : 1);
  }
  function drawRogue(r, now) {
    const k = Math.min(r.bornAt ? (now - r.bornAt) / 500 : 1, r.outAt ? 1 - (now - r.outAt) / 500 : 1);
    if (k <= 0) return;
    const red = !r.clearAt, pulse = calm ? 1 : .7 + .3 * Math.sin(now / 160);
    fx.save(); fx.globalAlpha = Math.min(1, k);
    fx.translate(r.x, GY); fx.scale(S * 1.1, S * 1.1);
    fx.shadowColor = red ? "rgba(255, 59, 48, .9)" : "rgba(79, 224, 160, .8)"; fx.shadowBlur = 12;
    fx.fillStyle = "#2a1518";
    fx.fillRect(-6, -16, 4, 16); fx.fillRect(2, -16, 4, 16);
    rr(fx, -10, -37, 20, 22, 4); fx.fill();
    rr(fx, -8, -51, 16, 13, 5); fx.fill();
    fx.fillRect(-13, -34, 3, 16); fx.fillRect(10, -34, 3, 16);
    fx.shadowBlur = 0;
    fx.fillStyle = red ? `rgba(255, 59, 48, ${pulse.toFixed(2)})` : "rgba(79, 224, 160, .9)";
    fx.fillRect(-6, -47, 12, 3);
    fx.fillRect(-10, -28, 20, 2);
    fx.restore();
  }
  function drawRogues() {
    const now = calm ? 0 : performance.now();
    const list = order(), first = standoff();
    list.forEach((r, k) => {
      if (!r.clearAt && k > (W < 700 ? 0 : 1)) return;
      // Each glides to its place as the others come and go.
      const want = slotX(Math.min(k, 1));
      r.x = calm || r.x == null ? want : r.x + (want - r.x) * .12;
      drawRogue(r, now);
    });
    // The squads' sights, all on the first of them.
    if (first) {
      fx.save(); fx.globalCompositeOperation = "lighter";
      fx.strokeStyle = "rgba(255, 59, 48, .5)"; fx.fillStyle = "rgba(255, 90, 80, .9)"; fx.lineWidth = 1.2 * S;
      const ty = GY - 34 * S;
      for (const d of droids) {
        if (d.state !== "hold" || d.rise < .6) continue;
        const [sx, sy] = shoulder(d);
        const mx = sx + d.dir * Math.cos(d.aim) * 24 * S, my = sy + Math.sin(d.aim) * 24 * S;
        fx.beginPath(); fx.moveTo(mx, my); fx.lineTo(first.x, ty + (d.side ? 3 : -3) * S); fx.stroke();
        fx.beginPath(); fx.arc(first.x, ty + (d.side ? 3 : -3) * S, 1.8 * S, 0, Math.PI * 2); fx.fill();
      }
      fx.restore();
    }
    list.forEach((r, k) => {
      if (r.x == null || r.outAt || (!r.clearAt && k > (W < 700 ? 0 : 1))) return;
      const state = r.clearAt ? "cleared" : r.alarm && now - r.alarm < 8000 ? "alarm" : "held";
      const fade = r.clearAt ? Math.max(0, 1 - Math.max(0, now - r.clearAt - 1500) / 900) : 1;
      intruderTag(fx, r.x, GY - 66 * S, r.h, { s: .8 * Math.max(S, .9), state, k: fade });
    });
  }
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    const now = performance.now();
    for (const h of added) if (!rogues.has(h.id)) rogues.set(h.id, newRogue(h));
    for (const h of held) { const r = rogues.get(h.id); if (r) r.h = h; }
    for (const h of cleared) {
      const r = rogues.get(h.id); if (!r || r.clearAt) continue;
      if (calm || document.hidden) { rogues.delete(h.id); continue; }
      // Stood down: it beams out, and a hard beam goes across the room for the device.
      r.clearAt = now;
      festiveTimers.push(setTimeout(() => {
        r.outAt = performance.now();
        burst(r.x, GY - 30 * S, "79, 224, 160", 18);
        festiveTimers.push(setTimeout(() => { rogues.delete(h.id); if (!h.test) welcome(); }, 520));
      }, 2400));
    }
    room.style.height = `${BAND + ([...rogues.values()].some(r => !r.clearAt) ? 64 : 0)}px`;
    if (calm) {
      const first = standoff();
      if (first) droids.forEach(d => { if (d.state !== "down") { d.state = "hold"; d.rise = d.want = 1; aimAt(d, first, 0); } });
      drawFight();
    }
  }
  const welcome = () => {
    if (document.hidden) return;
    heat = Math.max(heat, .8);
    const r = rigs[Math.floor(Math.random() * rigs.length)];
    shot = { at: performance.now(), x0: r.x, y0: r.y, x1: rnd(W * .1, W * .9), y1: rnd(horizon, H) };
  };
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let r = rogues.get(h.id);
    if (!r) { r = newRogue(h); rogues.set(h.id, r); }
    if (calm) { syncIntruders(); return; }
    if (document.hidden) return;
    const now = performance.now();
    Object.assign(r, { alarm: now, bornAt: now, clearAt: 0, outAt: 0 });
    // It beams in: sparks, the squads up, every beam on it.
    const list = order();
    r.x = slotX(Math.min(list.indexOf(r), 1));
    burst(r.x, GY - 30 * S, "255, 59, 48", 30);
    bolts.length = 0;
    flare = 1;
  };
  festiveHooks.rendered = syncIntruders;

  if (calm) {
    // Reduced motion: the rig lit, everything holding still. The fight is a
    // still of itself: a droid or two up, a few bolts caught mid-air.
    ctx.fillStyle = "#05060a";
    ctx.fillRect(0, 0, W, H);
    draw(0);
    droids.forEach((d, i) => { d.rise = i % 2 ? 1 : .15; d.want = d.rise; d.state = i % 2 ? "up" : "cover"; d.aim = i % 2 ? -.05 : -1.2; });
    for (let k = 0; k < 3; k++) {
      const y = GY - rnd(30, 44) * S, x = W * rnd(.38, .62), dir = k % 2 ? -1 : 1;
      bolts.push({ x, y, vx: dir * 1100 * S, vy: 0, hue: Math.floor(rnd(0, 360)), side: k % 2 });
    }
    drawFight();
    syncIntruders();
    return;
  }
  syncIntruders();

  let raf = 0, last = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000);
    last = now;
    if (flare > 0) flare = Math.max(0, flare - dt * 1.6);
    if (now > figureAt) { figure = (figure + 1) % 4; figureAt = now + 14000; }
    draw(now);
    fight(dt, now);
  };
  const run = () => { if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); } };
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };
  const vis = () => document.hidden ? stop() : run();
  document.addEventListener("visibilitychange", vis);
  figureAt = performance.now() + 14000;
  run();
  festiveStops.push(() => { stop(); document.removeEventListener("visibilitychange", vis); });
  // Switching to the theme opens with a flare, like the rig warming up.
  if (switched) flare = 1;

  skirmishAt = performance.now() + rnd(6000, 12000);
  // Staggered, so the squads don't all stand up on the first frame.
  droids.forEach(d => { d.next = performance.now() + rnd(500, 4500); });
  festiveHooks.scanDone = () => { if (!document.hidden) { flare = 1; heat = 1; } };
  festiveHooks.netChange = (wentOff, cameBack) => {
    const now = performance.now();
    for (const id of wentOff) {
      // Always leave each squad someone standing, or the fight stops.
      const free = droids.filter(d => !d.pinned && droids.filter(o => o.side === d.side && !o.pinned).length > 1);
      if (!free.length) break;
      const d = pick(free);
      d.pinned = id;
      knock(d, now);
    }
    for (const id of cameBack) {
      const d = droids.find(o => o.pinned === id);
      if (d) { d.pinned = null; d.downUntil = now + 500; }
    }
  };
}

BAMF.registerTheme("laser", ctx => buildLaser(ctx.root, ctx.switched));
})();
