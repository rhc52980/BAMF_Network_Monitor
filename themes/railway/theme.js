// Model Railway: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Model Railway: a train set run round the edges of the page. Along the
// bottom is a baseboard with the main line on it, and a station for each
// network, named for it, with a loop beside the platform so a train calling
// there is off the main line. Along the top runs the line back, where the
// trains go round smaller, on their way to start again. Behind the page is
// the painted backscene every layout has: hills, a village, a viaduct.
//
// Each network has its train, in its own livery, and every device up on
// that network is a wagon on it, lettered with the last part of its address.
// A watched device going down puts its station's exit signal to red, and the
// train waits in the platform until it's back; any device dropping off
// holds it for a moment. A finished scan has every train whistle and put on
// speed, and a new device is a wagon coupled on.
function buildRailway(root) {
  const calm = calmMotion();
  const TAU = Math.PI * 2;

  // The stations come from the networks, which arrive with the first list of
  // devices; a theme chosen before then is laid out again when they do.
  const netsNow = () => (subnets.length ? subnets : [...new Set(hosts.map(h => h.subnet).filter(Boolean))]);
  const builtFor = netsNow().join("|");

  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const fr = frontCanvas(root);
  fr.globalAlpha = 1;
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const S = Math.max(.72, Math.min(1.15, W / 1400));
  const BAND = Math.round(80 * S);
  const FY = H - BAND;
  const MY = H - 12 * S;          // the main line's rail
  const LY = MY - 18 * S;         // the loop's rail, at the stations
  const TY = 16 * S;              // the line along the top
  const TOP = .45;                // how small the trains are up there

  const room = document.createElement("div");
  room.style.height = `${BAND}px`;
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());

  const layer = () => {
    const cv = document.createElement("canvas");
    cv.width = W * dpr; cv.height = H * dpr;
    const g = cv.getContext("2d");
    g.scale(dpr, dpr);
    return [cv, g];
  };
  const LIVERY = [
    { body: "#2f6b3a", trim: "#d9b54a" },
    { body: "#7a1f2b", trim: "#e0c068" },
    { body: "#1f4f8a", trim: "#e8e2d0" },
    { body: "#2a2b2f", trim: "#c9463d" },
  ];
  const VANS = ["#8a5a3a", "#6d7a86", "#9a8a5a", "#5f7a5a", "#8a4a4a", "#5a6a8a"];
  const ipNum = h => (h.ip || "").split(".").reduce((a, o) => a * 256 + (Number(o) || 0), 0);

  // ---- The layout ----
  // A platform takes the longest train, seven wagons, clear of the main
  // line; as many stations as fit side by side with their loops apart.
  const PL = 304 * S, RAMP = 44 * S;
  const nets = netsNow().slice(0, Math.max(1, Math.min(4, 1 + Math.floor(W * .6 / (400 * S)))));
  const stations = (nets.length ? nets : ["BAMF"]).map((net, i, all) => {
    const cx = all.length === 1 ? W * .5 : W * (.2 + i * .6 / (all.length - 1));
    return { net, x0: cx - PL / 2, x1: cx + PL / 2, sig: RAMP * .5 + cx + PL / 2, held: false, holdUntil: 0 };
  });
  const smooth = u => u <= 0 ? 0 : u >= 1 ? 1 : u * u * (3 - 2 * u);
  const loopUp = (st, x) => st ? smooth((x - (st.x0 - RAMP)) / RAMP) * smooth((st.x1 + RAMP - x) / RAMP) : 0;
  const red = (st, now) => st.held || now < st.holdUntil;

  // ---- The shapes, each facing right, standing on its rail at the origin ----
  const wheel = (g, x, y, r, ang, rim) => {
    g.fillStyle = "#16171a"; g.beginPath(); g.arc(x, y, r, 0, TAU); g.fill();
    g.strokeStyle = rim; g.lineWidth = .8; g.beginPath(); g.arc(x, y, r - .6, 0, TAU); g.stroke();
    g.strokeStyle = "rgba(200, 200, 200, .55)"; g.lineWidth = .6;
    for (let k = 0; k < 3; k++) { const a = ang + k * TAU / 3; g.beginPath(); g.moveTo(x, y); g.lineTo(x + Math.cos(a) * (r - .8), y + Math.sin(a) * (r - .8)); g.stroke(); }
  };
  function loco(g, liv, ang) {
    g.fillStyle = "#1b1c1f"; g.fillRect(-23, -10, 46, 3);
    g.fillStyle = liv.body; g.fillRect(-8, -20, 30, 11);
    g.fillStyle = liv.trim; g.fillRect(-8, -15, 28, 1);
    g.fillStyle = "#1b1c1f"; g.fillRect(20, -21, 4, 12); g.fillRect(16, -28, 5, 8); g.fillRect(15, -29, 7, 2);
    g.fillStyle = liv.trim; g.beginPath(); g.arc(8, -20, 3.4, Math.PI, 0); g.fill();
    g.fillStyle = liv.body; g.fillRect(-23, -27, 15, 18);
    g.fillStyle = "#1b1c1f"; g.fillRect(-24, -29, 17, 2.4);
    g.fillStyle = "rgba(255, 214, 140, .85)"; g.fillRect(-19, -24, 6, 5);
    g.fillStyle = "#c43a2e"; g.fillRect(22, -10, 3, 3);
    for (const x of [-8, 2, 12]) wheel(g, x, -5, 5, ang, liv.trim);
    wheel(g, 19, -3, 3, ang * 1.6, liv.trim);
    const crank = (x) => [x + Math.cos(ang) * 2.8, -5 + Math.sin(ang) * 2.8];
    const [ax, ay] = crank(-8), [bx, by] = crank(12);
    g.strokeStyle = "#b9bcc2"; g.lineWidth = 1.2; g.beginPath(); g.moveTo(ax, ay); g.lineTo(bx, by); g.stroke();
  }
  function tender(g, liv, ang) {
    g.fillStyle = "#1b1c1f"; g.fillRect(-10, -9, 20, 2);
    g.fillStyle = liv.body; g.fillRect(-10, -19, 20, 10);
    g.fillStyle = "#101113"; g.fillRect(-9, -21, 18, 3);
    g.fillStyle = liv.trim; g.fillRect(-10, -15, 20, 1);
    for (const x of [-5, 5]) wheel(g, x, -4, 4, ang, liv.trim);
  }
  function van(g, col, label, ang, glow) {
    g.fillStyle = "#1b1c1f"; g.fillRect(-15, -8, 30, 2);
    g.fillStyle = col; g.fillRect(-15, -19, 30, 11);
    g.fillStyle = "rgba(255, 255, 255, .18)"; g.fillRect(-15, -21, 30, 2.4);
    g.strokeStyle = "rgba(0, 0, 0, .3)"; g.lineWidth = .7;
    g.beginPath(); g.moveTo(-5, -19); g.lineTo(-5, -8); g.moveTo(5, -19); g.lineTo(5, -8); g.stroke();
    if (glow > 0) { g.fillStyle = `rgba(255, 236, 160, ${glow.toFixed(2)})`; g.fillRect(-15, -21, 30, 13); }
    if (label) {
      g.fillStyle = "#f2ecd8"; g.font = `600 7px "IBM Plex Mono", monospace`; g.textAlign = "center"; g.textBaseline = "middle";
      g.fillText(label, 0, -13.5);
    }
    for (const x of [-9, 9]) wheel(g, x, -4, 4, ang, "#9a9a9a");
  }

  // ---- Behind the page: the painted backscene ----
  const [land, lc] = layer();
  {
    const HZ = H * .46;
    const sky = lc.createLinearGradient(0, 0, 0, HZ);
    sky.addColorStop(0, "#bcd5e8"); sky.addColorStop(1, "#eef4f1");
    lc.fillStyle = sky; lc.fillRect(0, 0, W, H);
    const hills = (base, amp, col, seed) => {
      lc.fillStyle = col; lc.beginPath(); lc.moveTo(0, H);
      for (let x = 0; x <= W + 10; x += 10) lc.lineTo(x, base - amp * (.55 + .3 * Math.sin(x / 260 + seed) + .15 * Math.sin(x / 90 + seed * 3)));
      lc.lineTo(W, H); lc.closePath(); lc.fill();
    };
    hills(HZ, H * .09, "#d7e3d0", 1.1);
    hills(HZ + H * .05, H * .07, "#c8d9bd", 3.7);
    // A viaduct striding across the valley, over on the left.
    const vx = W * .08, vw = W * .28, vy = HZ + H * .02, vh = H * .09;
    lc.fillStyle = "#dcd2c4"; lc.fillRect(vx, vy - 6, vw, 6);
    for (let k = 0; k < 7; k++) {
      const ax = vx + k * vw / 7, aw = vw / 7;
      lc.fillRect(ax, vy, aw * .18, vh);
      lc.beginPath(); lc.moveTo(ax + aw * .18, vy); lc.lineTo(ax + aw, vy); lc.lineTo(ax + aw, vy + vh * .25);
      lc.arc(ax + aw * .59, vy + vh * .25, aw * .41, 0, Math.PI, true); lc.closePath(); lc.fill();
    }
    hills(HZ + H * .12, H * .06, "#bccfae", 6.2);
    // A village: cottages and a church.
    const vil = W * .64;
    for (let k = 0; k < 7; k++) {
      const hx = vil + k * 34 * S + rnd(-6, 6), hy = HZ + H * .1, hw = rnd(20, 28) * S, hh = rnd(12, 17) * S;
      lc.fillStyle = pick(["#f2eadb", "#e8ddc8", "#f5efe3"]); lc.fillRect(hx, hy - hh, hw, hh);
      lc.fillStyle = pick(["#dcc6be", "#d6c2bb", "#cfccd2"]);
      lc.beginPath(); lc.moveTo(hx - 2, hy - hh); lc.lineTo(hx + hw / 2, hy - hh - 10 * S); lc.lineTo(hx + hw + 2, hy - hh); lc.closePath(); lc.fill();
      lc.fillStyle = "#c3ccd4"; lc.fillRect(hx + 4, hy - hh + 4, 4, 4);
    }
    const ch = vil - 30 * S, cy = HZ + H * .1;
    lc.fillStyle = "#e3dac9"; lc.fillRect(ch, cy - 22 * S, 12 * S, 22 * S); lc.fillRect(ch + 12 * S, cy - 14 * S, 26 * S, 14 * S);
    lc.fillStyle = "#cfced3"; lc.beginPath(); lc.moveTo(ch - 1, cy - 22 * S); lc.lineTo(ch + 6 * S, cy - 46 * S); lc.lineTo(ch + 12 * S + 1, cy - 22 * S); lc.closePath(); lc.fill();
    // Trees, dotted about the hillsides.
    for (let k = 0; k < 26; k++) {
      const tx = rnd(0, W), ty = HZ + rnd(H * .02, H * .16), tr = rnd(7, 15) * S;
      lc.fillStyle = "#c6d0ba"; lc.fillRect(tx - 1.2, ty, 2.4, tr * .9);
      lc.fillStyle = pick(["#b9cfa8", "#c1d6b0", "#bdd2ac"]); lc.beginPath(); lc.arc(tx, ty, tr, 0, TAU); lc.fill();
    }
    lc.fillStyle = "#c3d4b6"; lc.fillRect(0, HZ + H * .17, W, H);
  }
  const clouds = Array.from({ length: 5 }, () => ({ x: rnd(0, W), y: rnd(H * .05, H * .3), sc: rnd(.7, 1.4), v: rnd(3, 8) }));

  // ---- In front of the page: the baseboard, drawn once ----
  const [board, bc] = layer();
  const track = (g, x0, x1, yAt) => {
    for (let x = x0; x < x1; x += 7) {
      const y = yAt(x);
      g.fillStyle = "#6b675f"; g.fillRect(x - 1, y - 1, 9, 6);
      g.fillStyle = "#3b2b20"; g.fillRect(x, y, 4, 3.4);
    }
    g.strokeStyle = "#a4a9ae"; g.lineWidth = 1.6;
    g.beginPath(); for (let x = x0; x <= x1; x += 3) g.lineTo(x, yAt(x)); g.stroke();
  };
  {
    const grass = bc.createLinearGradient(0, FY, 0, H);
    grass.addColorStop(0, "#44713a"); grass.addColorStop(1, "#2e5228");
    bc.fillStyle = grass; bc.fillRect(0, FY, W, BAND);
    bc.fillStyle = "#3b2d22"; bc.fillRect(0, FY, W, 3);
    for (let k = 0; k < W / 6; k++) { bc.fillStyle = pick(["#3f6a36", "#4d7a40", "#38602f"]); bc.fillRect(rnd(0, W), rnd(FY + 4, H), 2, 2); }
    // Stations: the building and platform behind the loop, and a nameboard.
    for (const st of stations) {
      bc.fillStyle = "#b8ab94"; bc.fillRect(st.x0, LY - 13 * S, st.x1 - st.x0, 10 * S);
      bc.fillStyle = "#d9cfb9"; bc.fillRect(st.x0, LY - 13 * S, st.x1 - st.x0, 2);
      const bx = st.x0 + 14 * S, bw = 64 * S, by = LY - 13 * S;
      bc.fillStyle = "#9c5a44"; bc.fillRect(bx, by - 30 * S, bw, 30 * S);
      bc.fillStyle = "#4b4a55"; bc.beginPath(); bc.moveTo(bx - 4, by - 30 * S); bc.lineTo(bx + bw / 2, by - 44 * S); bc.lineTo(bx + bw + 4, by - 30 * S); bc.closePath(); bc.fill();
      bc.fillStyle = "rgba(255, 220, 150, .8)";
      for (let k = 0; k < 3; k++) bc.fillRect(bx + 8 * S + k * 19 * S, by - 22 * S, 8 * S, 10 * S);
      // The nameboard stands at the far end, the canopy between it and the building.
      const nw = Math.max(70, 12 + st.net.length * 6.2), nx = st.x1 - nw / 2 - 8 * S;
      bc.fillStyle = "#3a3d44"; bc.fillRect(bx + bw, by - 24 * S, nx - nw / 2 - 10 * S - bx - bw, 3);
      for (let x = bx + bw + 20 * S; x < nx - nw / 2 - 12 * S; x += 34 * S) bc.fillRect(x, by - 24 * S, 2, 24 * S);
      bc.fillStyle = "#26272b"; bc.fillRect(nx - nw / 2 - 1, by - 21 * S, 2, 21 * S); bc.fillRect(nx + nw / 2 - 1, by - 21 * S, 2, 21 * S);
      bc.fillStyle = "#f4efe2"; bc.fillRect(nx - nw / 2, by - 21 * S - 12, nw, 12);
      bc.strokeStyle = "#26272b"; bc.lineWidth = 1; bc.strokeRect(nx - nw / 2, by - 21 * S - 12, nw, 12);
      bc.fillStyle = "#1c2230"; bc.font = `600 9px "IBM Plex Mono", monospace`; bc.textAlign = "center"; bc.textBaseline = "middle";
      bc.fillText(st.net, nx, by - 21 * S - 6);
      track(bc, st.x0 - RAMP, st.x1 + RAMP, x => MY - (MY - LY) * loopUp(st, x));
      bc.fillStyle = "#26272b"; bc.fillRect(st.sig - 1, LY - 42 * S, 2.4, 42 * S);
      bc.fillRect(st.sig - 3.5, LY - 50 * S, 7, 14 * S);
    }
    track(bc, -10, W + 10, () => MY);
    // The line along the top, from edge to edge.
    bc.strokeStyle = "#8a8f94"; bc.lineWidth = 1.2;
    bc.beginPath(); bc.moveTo(0, TY); bc.lineTo(W, TY); bc.stroke();
    bc.fillStyle = "#3b2b20";
    for (let x = 0; x < W; x += 5) bc.fillRect(x, TY, 2, 2);
  }

  // ---- The trains ----
  const trains = stations.map((st, i) => ({ st, liv: LIVERY[i % 4], devs: [], vans: 0, line: "queue", x: 0, v: 0, dwell: 0, done: false, dist: 0, boost: 0, glow: 0 }));
  if (trains.length === 1) trains.push({ st: null, liv: LIVERY[3], devs: [], vans: 5, goods: true, line: "queue", x: 0, v: 0, dwell: 0, done: false, dist: 0, boost: 0, glow: 0 });
  const units = t => 46 + 3 + 20 + t.vans * 33;
  const puffs = [];
  const recount = () => {
    for (const t of trains) {
      if (t.goods) continue;
      t.devs = hosts.filter(h => h.subnet === t.st.net && h.online && !h.ignored && !h.forgotten).sort((a, b) => ipNum(a) - ipNum(b));
      t.vans = Math.min(7, t.devs.length);
      t.st.held = hosts.some(h => h.subnet === t.st.net && h.watched && !h.online && !h.ignored && !h.forgotten);
    }
  };
  recount();
  const vanLabel = (t, k) => {
    if (t.goods) return "";
    if (k === t.vans - 1 && t.devs.length > t.vans) return `+${t.devs.length - t.vans + 1}`;
    const d = t.devs[k];
    return d ? "." + (d.ip || "").split(".").pop() : "";
  };
  const extent = t => t.line === "top" ? [t.x, t.x + units(t) * TOP * S] : [t.x - units(t) * S, t.x];
  const onMainNear = (me, a, b) => trains.some(o => o !== me && o.line === "bottom" && !(o.st && o.dwelling) && (() => { const [l, r] = extent(o); return r > a && l < b; })());

  function step(dt, now) {
    for (const cl of clouds) { cl.x += cl.v * dt; if (cl.x > W + 120) cl.x = -120; }
    for (const t of trains) {
      t.boost = Math.max(0, t.boost - dt);
      t.glow = Math.max(0, t.glow - dt * .6);
      const vmax = 72 * S * (t.boost > 0 ? 1.35 : 1);
      if (t.line === "queue") {
        // In at the left once the start of the line is clear.
        if (!trains.some(o => o !== t && o.line === "bottom" && !o.dwelling && extent(o)[0] < 110 * S)) { t.line = "bottom"; t.x = 0; t.v = vmax * .6; t.done = false; t.dwell = 0; t.dwelling = false; }
        continue;
      }
      if (t.line === "bottom") {
        let limit = vmax;
        if (t.st && !t.done) {
          const stop = t.st.x1 - 3 * S;
          if (t.x >= stop - .5) {
            t.x = stop; t.v = 0; t.dwelling = true; t.dwell += dt;
            // Away once it's stood long enough, its signal is clear, and the
            // main line where it joins is free.
            if (t.dwell > 4 && !red(t.st, now) && !onMainNear(t, t.st.x1 - 260 * S, t.st.x1 + RAMP + 110 * S)) { t.done = true; t.dwelling = false; }
            continue;
          }
          limit = Math.min(limit, Math.sqrt(2 * 55 * S * Math.max(0, stop - t.x)) + 4);
        }
        // Keep a gap to anything ahead on the main line.
        for (const o of trains) {
          if (o === t || o.line !== "bottom" || o.dwelling) continue;
          const gap = extent(o)[0] - t.x;
          if (gap > 0 && gap < 60 * S) limit = Math.min(limit, gap < 14 * S ? 0 : o.v);
        }
        t.v = t.v < limit ? Math.min(limit, t.v + 60 * S * dt) : Math.max(limit, t.v - 90 * S * dt);
        t.x += t.v * dt;
        t.dist += t.v * dt;
        if (t.dist > 16) { t.dist = 0; if (t.v > 5) puff(t, 1); }
        if (extent(t)[0] > W + 20) { t.line = "top"; t.x = W + 20; t.v = vmax * .7; }
      } else if (t.line === "top") {
        t.x -= 52 * S * dt;
        t.dist += 52 * S * dt;
        if (t.dist > 20) { t.dist = 0; puff(t, .45); }
        if (extent(t)[1] < -20) t.line = "queue";
      }
    }
    for (let i = puffs.length - 1; i >= 0; i--) {
      const p = puffs[i];
      p.life -= dt / 1.5; p.x += p.vx * dt; p.y += p.vy * dt; p.r += 7 * S * dt * p.sc;
      if (p.life <= 0) puffs.splice(i, 1);
    }
  }
  const chimney = t => t.line === "top" ? [t.x + 4.5 * TOP * S, TY - 29 * TOP * S] : [t.x - 4.5 * S, (t.st ? MY - (MY - LY) * loopUp(t.st, t.x - 4.5 * S) : MY) - 29 * S];
  function puff(t, sc) {
    const [x, y] = chimney(t);
    puffs.push({ x, y, r: 2.5 * S * sc, sc, life: 1, vx: rnd(-6, 6), vy: -rnd(10, 18) * S * sc });
  }

  function drawTrain(t, now, ang0) {
    const top = t.line === "top", sc = (top ? TOP : 1) * S, dir = top ? -1 : 1;
    const list = [{ k: "loco", len: 46 }, { k: "tender", len: 20 }];
    for (let v = 0; v < t.vans; v++) list.push({ k: "van", len: 30, i: v });
    let off = 0;
    const ang = ang0 + t.x / (5 * sc) * (top ? -1 : 1);
    for (const u of list) {
      const cx = t.x - dir * (off + u.len / 2) * sc;
      const y = top ? TY : (t.st ? MY - (MY - LY) * loopUp(t.st, cx) : MY);
      fr.save(); fr.translate(cx, y); fr.scale(dir * sc, sc);
      if (u.k === "loco") loco(fr, t.liv, ang);
      else if (u.k === "tender") tender(fr, t.liv, ang);
      else van(fr, VANS[(u.i + (t.st ? stations.indexOf(t.st) : 3)) % VANS.length], top ? "" : vanLabel(t, u.i), ang, u.i === t.vans - 1 ? t.glow : 0);
      fr.restore();
      off += u.len + 3;
    }
  }

  function draw(now) {
    const t = (now || 0) / 1000;
    ctx.drawImage(land, 0, 0, W, H);
    for (const cl of clouds) {
      ctx.fillStyle = "rgba(255, 255, 255, .85)";
      for (const [dx, dy, r] of [[0, 0, 18], [20, 4, 14], [-18, 5, 13], [8, -8, 13]]) { ctx.beginPath(); ctx.arc(cl.x + dx * cl.sc, cl.y + dy * cl.sc, r * cl.sc, 0, TAU); ctx.fill(); }
    }
    fr.clearRect(0, 0, W, H);
    fr.drawImage(board, 0, 0, W, H);
    // Each station's exit signal.
    for (const st of stations) {
      const r = red(st, now || performance.now());
      fr.fillStyle = r ? "#ff3b2e" : "#2ee06a";
      fr.beginPath(); fr.arc(st.sig, LY - (r ? 46 : 40) * S, 2.2 * S, 0, TAU); fr.fill();
      fr.fillStyle = "rgba(0, 0, 0, .55)";
      fr.beginPath(); fr.arc(st.sig, LY - (r ? 40 : 46) * S, 2.2 * S, 0, TAU); fr.fill();
    }
    for (const tr of trains) if (tr.line === "top") drawTrain(tr, now, 0);
    // Trains in a platform loop are further off than those on the main line.
    for (const tr of trains) if (tr.line === "bottom" && tr.dwelling) drawTrain(tr, now, 0);
    for (const tr of trains) if (tr.line === "bottom" && !tr.dwelling) drawTrain(tr, now, 0);
    for (const p of puffs) {
      fr.fillStyle = `rgba(236, 236, 232, ${(p.life * .55).toFixed(2)})`;
      fr.beginPath(); fr.arc(p.x, p.y, p.r, 0, TAU); fr.fill();
    }
  }

  festiveHooks.rendered = () => {
    // Networks known at last, or changed: lay the railway out again for them.
    if (netsNow().join("|") !== builtFor) { setTimeout(() => { if (festiveTheme === "railway") festive("railway"); }); return; }
    recount();
    if (calm) { park(); draw(0); }
  };
  // Standing at their stations, for a still picture.
  function park() {
    let x = W * .9;
    for (const t of trains) {
      if (t.st) { t.line = "bottom"; t.x = t.st.x1 - 3 * S; t.dwelling = true; }
      else { t.line = "bottom"; t.x = x; t.dwelling = false; x -= units(t) * S + 40; }
    }
  }

  if (calm) {
    park();
    puffs.push({ x: trains[0].x - 4.5 * S, y: LY - 34 * S, r: 5 * S, sc: 1, life: .8, vx: 0, vy: 0 });
    draw(0);
    return;
  }
  // Start them spaced out along the round, rather than queued at the start.
  trains.forEach((t, i) => { if (i === 0) { t.line = "bottom"; t.x = W * .06; } else { t.line = "top"; t.x = W * (.25 + i * .3); } });
  c.start();
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    for (const t of trains) { t.boost = 3; if (t.line !== "queue") for (let k = 0; k < 5; k++) setTimeout(() => puff(t, 1.6), k * 90); }
  };
  festiveHooks.newDevice = h => {
    const t = trains.find(o => o.st && h && o.st.net === h.subnet);
    if (t) t.glow = 1.6;
  };
  festiveHooks.netChange = (wentOff) => {
    const now = performance.now();
    for (const id of wentOff) {
      const h = hosts.find(x => x.id === id);
      const st = h && stations.find(s => s.net === h.subnet);
      if (st) st.holdUntil = now + 20000;
    }
  };
}

BAMF.registerTheme("railway", ctx => buildRailway(ctx.root, ctx.switched));
})();
