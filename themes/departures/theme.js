// Departures: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Departures: the dashboard as an airport departures board. Every device is a
// flight - the vendor for the airline, the last part of its address for the
// number - and its status clacks round on split-flap letters: ON TIME while
// it's up, BOARDING while it's up but not yet known, DELAYED when it's just gone
// quiet, CANCELLED once it's been gone half an hour, blinking if it's a watched
// one. A board in the header keeps the time and the totals. Behind the page
// the sun is down over the airfield: runway lights, the tower's beacon, and
// every so often a plane rolling out and climbing away. A scan clacks every
// row over and sends a plane off; a new device is a new flight flipping onto
// the board.
// A new device nobody has marked known is an intruder: an unscheduled
// arrival. The board's title flaps to SECURITY ALERT and every flight to HOLD,
// the plane on its take-off run aborts and the runway lights flash red; then
// the arrival's own row flaps to SECURITY, its flight number unknown, until
// the device is marked known, when it goes on the board like any other flight.
function buildDepartures(root, switched) {
  const calm = calmMotion();
  const TAU = Math.PI * 2;
  // The intruders the board is holding, and until when every flight holds.
  const watchIn = intruderWatch();
  let securityIds = new Set(), holdAll = 0;

  // ---- The board ----
  const head = document.createElement("div");
  head.className = "df-head";
  head.innerHTML = `<span class="df-title">\u2708 DEPARTURES</span><span class="df-flap df-clock"></span><span class="df-sum"></span>`;
  $("themeToggle")?.before(head);
  festiveStops.push(() => head.remove());
  // Switching theme doesn't redraw the table, so the flaps come off here.
  festiveStops.push(() => document.querySelectorAll("#tableWrap .df-flap").forEach(e => e.remove()));
  const clock = head.querySelector(".df-clock"), sum = head.querySelector(".df-sum");

  const CH = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
  const spinning = new Set();
  const cellsOf = (el, n) => { if (el.children.length !== n) el.innerHTML = "<i></i>".repeat(n); return [...el.children]; };
  const show = (c, ch) => { c.textContent = ch === " " ? "" : ch; };
  // Puts letters on a flap. Moving, each tile starts from what it showed and
  // clatters through a few letters before it lands, left to right, the way a
  // board settles; still, it just changes.
  function flap(el, to, from) {
    const cs = cellsOf(el, to.length);
    cs.forEach((c, i) => {
      if (calm || document.hidden || from === undefined) { show(c, to[i]); delete c.dataset.spin; return; }
      show(c, (from[i] ?? " "));
      if (from[i] === to[i] && !c.dataset.spin) return;
      c.dataset.want = to[i];
      c.dataset.spin = String(2 + i + Math.floor(Math.random() * 4));
      spinning.add(c);
    });
  }
  if (!calm) {
    // One ticker for every tile in motion. Alternating between two identical
    // animations restarts the flip each tick without forcing a layout.
    festiveTimers.push(setInterval(() => {
      if (document.hidden || !spinning.size) return;
      for (const c of spinning) {
        if (!c.isConnected) { spinning.delete(c); continue; }
        c.className = c.className === "fa" ? "fb" : "fa";
        const left = Number(c.dataset.spin) - 1;
        if (left <= 0) { show(c, c.dataset.want); delete c.dataset.spin; spinning.delete(c); }
        else { c.dataset.spin = String(left); c.textContent = CH[Math.floor(Math.random() * CH.length)]; }
      }
    }, 60));
  }

  const status = h => {
    if (securityIds.has(h.id)) return ["SECURITY", "sec"];
    if (Date.now() < holdAll) return ["HOLD", "hold"];
    if (h.ignored) return ["DIVERTED", "div"];
    if (h.online) return h.known ? ["ON TIME", "on"] : ["BOARDING", "board"];
    return (Date.now() - new Date(h.lastSeen)) / 60000 < 30 ? ["DELAYED", "delay"] : ["CANCELLED", "cancel"];
  };
  const flightNo = h => {
    if (securityIds.has(h.id)) return "?? ???";
    const air = ((h.vendor || "").toUpperCase().replace(/[^A-Z]/g, "").slice(0, 2) || "BM").padEnd(2, "X");
    return `${air} ${String(Number((h.ip || "").split(".").pop()) || 0).padStart(3, "0")}`;
  };
  let depart = null;               // set once the airfield is built
  const shown = new Map();         // host id -> the status last on the board
  const fresh = new Set();         // new devices, to flip on from blank
  let clackAll = !!switched && !calm;
  let lastClock = "";
  const tickClock = () => {
    const d = new Date(), t = `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
    if (t !== lastClock) { flap(clock, t, lastClock || undefined); lastClock = t; }
  };
  tickClock();
  festiveTimers.push(setInterval(tickClock, 5000));

  // What the New tab holds: SECURITY on its rows. One cleared goes on the
  // board like a new flight, flipping on from blank, and a plane goes.
  const syncIntruders = () => {
    const { held, cleared } = watchIn();
    securityIds = new Set(held.map(h => h.id));
    for (const h of cleared) { fresh.add(h.id); shown.delete(h.id); if (!calm && !document.hidden && !h.test) depart?.(); }
    head.classList.toggle("security", securityIds.size > 0);
  };
  festiveHooks.rendered = () => {
    syncIntruders();
    const byId = new Map(hosts.map(h => [String(h.id), h]));
    const clack = clackAll;
    clackAll = false;
    const inView = tr => { const r = tr.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight; };
    for (const tr of document.querySelectorAll("#tableWrap tbody tr[data-id]")) {
      const h = byId.get(tr.dataset.id);
      const cell = tr.querySelector(".status-cell");
      if (!h || !cell) continue;
      const [text, cls] = status(h), word = text.padEnd(9);
      cell.querySelector(".df-flap")?.remove();
      const el = document.createElement("span");
      el.className = `df-flap ${cls}${h.watched && cls === "cancel" ? " watch" : ""}`;
      cell.prepend(el);
      const was = shown.get(h.id), isNew = fresh.has(h.id), loud = clack && inView(tr);
      flap(el, word, isNew ? " ".repeat(9) : was && was !== word ? was : loud ? word.split("").map(() => " ").join("") : undefined);
      shown.set(h.id, word);
      const name = tr.querySelector(".hostname");
      if (name) {
        name.querySelector(".df-code")?.remove();
        const code = document.createElement("span");
        code.className = "df-flap df-code";
        name.prepend(code);
        flap(code, flightNo(h), isNew || loud ? "      " : undefined);
      }
      fresh.delete(h.id);
    }
    // The totals, over everything rather than just the rows on screen.
    const n = { on: 0, board: 0, delay: 0, cancel: 0 };
    for (const h of hosts) if (!h.ignored && !h.forgotten || securityIds.has(h.id)) { const k = status(h)[1]; n[k] = (n[k] || 0) + 1; }
    n.sec = n.sec || 0; n.hold = n.hold || 0;
    sum.innerHTML = (n.sec ? `SECURITY <b class="rd">${n.sec}</b> \u00b7 ` : "") + `ON TIME <b>${n.on}</b> \u00b7 BOARDING <b class="am">${n.board}</b> \u00b7 DELAYED <b class="am">${n.delay}</b> \u00b7 CANCELLED <b class="rd">${n.cancel}</b>`;
  };
  // Chosen from the menu, the table isn't redrawn, so put the board up now.
  if (hosts.length) festiveHooks.rendered();

  // ---- The airfield behind the page ----
  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const S = Math.max(.72, Math.min(1.15, W / 1400));
  const HZ = H * .44, RY = HZ + 18 * S;
  const RX0 = W * .1, RX1 = W * .86;
  const TWR = W * .06;
  const land = document.createElement("canvas");
  land.width = W * dpr; land.height = H * dpr;
  const lc = land.getContext("2d");
  lc.scale(dpr, dpr);
  {
    const sky = lc.createLinearGradient(0, 0, 0, HZ);
    sky.addColorStop(0, "#0a0f22"); sky.addColorStop(.55, "#2a2552"); sky.addColorStop(.86, "#8a4a5a"); sky.addColorStop(1, "#e08a4e");
    lc.fillStyle = sky; lc.fillRect(0, 0, W, HZ);
    const ground = lc.createLinearGradient(0, HZ, 0, H);
    ground.addColorStop(0, "#15151a"); ground.addColorStop(1, "#07080a");
    lc.fillStyle = ground; lc.fillRect(0, HZ, W, H - HZ);
    // A city along the horizon, far off, with its lights on.
    for (let x = W * .5; x < W; x += rnd(10, 26)) {
      const bw = rnd(8, 22), bh = rnd(8, 34) * S;
      lc.fillStyle = "#12131b"; lc.fillRect(x, HZ - bh, bw, bh);
      lc.fillStyle = "rgba(255, 210, 140, .55)";
      for (let wy = HZ - bh + 3; wy < HZ - 2; wy += 5) for (let wx = x + 2; wx < x + bw - 2; wx += 4) if (Math.random() < .35) lc.fillRect(wx, wy, 1.5, 1.5);
    }
    // The terminal, glass lit warm, with its jet bridges.
    const tx = W * .66, tw = W * .3, th = 28 * S;
    lc.fillStyle = "#101116"; lc.fillRect(tx, HZ - th, tw, th + 6);
    lc.fillStyle = "rgba(255, 196, 120, .42)"; lc.fillRect(tx + 6, HZ - th + 6, tw - 12, th * .45);
    lc.strokeStyle = "rgba(0, 0, 0, .55)"; lc.lineWidth = 1;
    for (let x = tx + 6; x < tx + tw - 6; x += 11) { lc.beginPath(); lc.moveTo(x, HZ - th + 6); lc.lineTo(x, HZ - th + 6 + th * .45); lc.stroke(); }
    lc.fillStyle = "#0d0e12";
    for (let k = 0; k < 4; k++) lc.fillRect(tx + tw * (.12 + k * .22), HZ - 6 * S, 26 * S, 5 * S);
    // The tower, its cab lit.
    lc.fillStyle = "#101116";
    lc.fillRect(TWR - 5 * S, HZ - 64 * S, 10 * S, 64 * S);
    lc.beginPath(); lc.moveTo(TWR - 13 * S, HZ - 64 * S); lc.lineTo(TWR + 13 * S, HZ - 64 * S); lc.lineTo(TWR + 10 * S, HZ - 78 * S); lc.lineTo(TWR - 10 * S, HZ - 78 * S); lc.closePath(); lc.fill();
    lc.fillStyle = "rgba(140, 220, 200, .5)"; lc.fillRect(TWR - 9 * S, HZ - 76 * S, 18 * S, 7 * S);
    lc.fillStyle = "#101116"; lc.fillRect(TWR - 12 * S, HZ - 82 * S, 24 * S, 4 * S);
    // The runway, with its centre line and edge lights.
    lc.fillStyle = "#1c1d22"; lc.fillRect(RX0, RY - 3, RX1 - RX0, 6);
    lc.fillStyle = "rgba(255, 255, 255, .35)";
    for (let x = RX0 + 8; x < RX1 - 8; x += 22) lc.fillRect(x, RY - .5, 10, 1);
    for (let x = RX0; x <= RX1; x += 18) {
      lc.fillStyle = x < RX0 + 1 ? "#58e07a" : x > RX1 - 18 ? "#ff4b3e" : "rgba(255, 244, 214, .85)";
      lc.fillRect(x, RY - 5, 1.8, 1.8); lc.fillRect(x, RY + 3.5, 1.8, 1.8);
    }
  }
  const APPROACH = Array.from({ length: 7 }, (_, i) => RX0 - 12 - i * 14 * S);

  function jet(x, y, sc, ang, t) {
    ctx.save(); ctx.translate(x, y); ctx.rotate(ang); ctx.scale(sc, sc);
    ctx.fillStyle = "#16181e";
    ctx.beginPath(); ctx.moveTo(-30, -3); ctx.lineTo(24, -3); ctx.quadraticCurveTo(32, -2, 33, 0); ctx.quadraticCurveTo(32, 3, 24, 3.5); ctx.lineTo(-28, 3.5); ctx.lineTo(-33, 0); ctx.closePath(); ctx.fill();
    ctx.beginPath(); ctx.moveTo(-23, -3); ctx.lineTo(-31, -15); ctx.lineTo(-26, -15); ctx.lineTo(-15, -3); ctx.closePath(); ctx.fill();
    ctx.beginPath(); ctx.moveTo(-2, 1); ctx.lineTo(-13, 10); ctx.lineTo(-7, 10); ctx.lineTo(8, 1.5); ctx.closePath(); ctx.fill();
    ctx.fillRect(-6, 3.5, 8, 2.6);
    ctx.fillStyle = "rgba(255, 214, 140, .85)";
    for (let wx = -20; wx < 20; wx += 3.2) ctx.fillRect(wx, -1.4, 1.4, 1.2);
    ctx.fillStyle = "#ff3b30"; ctx.beginPath(); ctx.arc(-9, 10, 1.4, 0, TAU); ctx.fill();
    if (Math.sin(t * 8) > .8) { ctx.fillStyle = "#fff"; ctx.beginPath(); ctx.arc(-31, -15, 1.8, 0, TAU); ctx.fill(); }
    ctx.restore();
  }

  let takeoff = null, takeoffAt = 0, cruise = null, cruiseAt = 0, alarmUntil = 0;
  depart = () => { if (!takeoff) takeoff = { x: RX0 + 20, y: RY - 5 * S, v: 0, ang: 0 }; };

  function step(dt, now) {
    if (!takeoff && now > takeoffAt && now > alarmUntil) { depart(); takeoffAt = now + rnd(25e3, 45e3); }
    if (takeoff) {
      const p = takeoff;
      // An intruder on the field: the take-off is abandoned, brakes on.
      if (now < alarmUntil && p.ang === 0) { p.v = Math.max(0, p.v - 150 * S * dt); p.x += p.v * dt; if (p.v <= 0 && now > alarmUntil - 200) takeoff = null; return; }
      p.v += 62 * S * dt;
      p.x += Math.cos(p.ang) * p.v * dt;
      p.y += Math.sin(p.ang) * p.v * dt;
      if (p.x > RX0 + (RX1 - RX0) * .55) p.ang = Math.max(-.2, p.ang - dt * .25);
      if (p.x > W + 90 || p.y < -60) takeoff = null;
    }
    if (!cruise && now > cruiseAt) {
      const dir = Math.random() < .5 ? 1 : -1;
      cruise = { x: dir > 0 ? -60 : W + 60, y: rnd(H * .06, H * .18), dir };
      cruiseAt = now + rnd(40e3, 75e3);
    }
    if (cruise) { cruise.x += cruise.dir * 40 * S * dt; if (cruise.x < -80 || cruise.x > W + 80) cruise = null; }
  }

  function draw(now) {
    const t = (now || 0) / 1000;
    ctx.drawImage(land, 0, 0, W, H);
    // The approach lights run towards the runway, one after another.
    const lead = calm ? -1 : Math.floor(t * 7) % 10;
    APPROACH.forEach((x, i) => {
      const on = (APPROACH.length - 1 - i) === lead;
      ctx.fillStyle = on ? "rgba(255, 255, 255, .95)" : "rgba(255, 236, 190, .35)";
      ctx.beginPath(); ctx.arc(x, RY, on ? 2.4 : 1.3, 0, TAU); ctx.fill();
    });
    // The tower's beacon: green and white by turns.
    const beacon = calm ? 0 : Math.floor(t * 1.2) % 2;
    ctx.fillStyle = beacon ? "rgba(255, 255, 255, .95)" : "rgba(80, 240, 130, .95)";
    ctx.beginPath(); ctx.arc(TWR, HZ - 86 * S, 2.2, 0, TAU); ctx.fill();
    if (now < alarmUntil && Math.sin(t * 9) > 0) {
      ctx.fillStyle = "#ff3b30";
      for (let x = RX0; x <= RX1; x += 18) { ctx.fillRect(x - .6, RY - 5.6, 3, 3); ctx.fillRect(x - .6, RY + 2.9, 3, 3); }
    }
    if (takeoff) jet(takeoff.x, takeoff.y, S * .9, takeoff.ang, t);
    if (cruise) {
      ctx.save(); ctx.translate(cruise.x, cruise.y); if (cruise.dir < 0) ctx.scale(-1, 1);
      jet(0, 0, S * .42, 0, t);
      ctx.restore();
    }
  }

  if (calm) {
    festiveHooks.intruder = () => festiveHooks.rendered();
    // Reduced motion: a plane lined up at the start of the runway, waiting.
    takeoff = { x: RX0 + 30, y: RY - 5 * S, v: 0, ang: 0 };
    draw(0);
    return;
  }
  takeoffAt = performance.now() + rnd(6000, 14000);
  cruiseAt = performance.now() + rnd(10e3, 25e3);
  c.start();
  festiveHooks.scanDone = () => { clackAll = true; if (!document.hidden) depart(); };
  // The alarm: every flight holds, the title flaps, the runway goes red.
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    if (document.hidden) { festiveHooks.rendered(); return; }
    holdAll = Date.now() + 8000; alarmUntil = performance.now() + 8000;
    clackAll = true;
    const title = head.querySelector(".df-title");
    head.classList.add("alert");
    title.textContent = "\u26a0 SECURITY ALERT";
    festiveHooks.rendered();
    festiveTimers.push(setTimeout(() => {
      head.classList.remove("alert");
      title.textContent = "\u2708 DEPARTURES";
      clackAll = true; festiveHooks.rendered();
    }, 8200));
  };
}

BAMF.registerTheme("departures", ctx => buildDepartures(ctx.root, ctx.switched));
})();
