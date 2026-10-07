// ---- The screen saver ----
// After a while with no mouse or keys, the dashboard fades away and leaves the
// theme's scenery, which keeps answering the network, with a small card that
// drifts so nothing burns in. A theme with no scenery gets the devices as
// points of light. Any input brings the page back, and the click or key that
// does it goes no further. Kept per browser: a wall screen wants it, a desk
// doesn't.
let saverMins = 0, saverOn = false, saverSince = 0, lastInput = Date.now(), saverTimer = null, saverRaf = 0;
let saverStyle = "scene", watchtower = null, saverForceWatch = false, saverGen = 0;
try { saverMins = Number(localStorage.getItem("bamf-saver")) || 0; saverStyle = localStorage.getItem("bamf-saver-style") || "scene"; } catch {}
// New devices waiting for someone to deal with them on the watchtower, oldest
// first. Kept in this browser, so a reload of the wall screen doesn't lose them.
let watchAlerts = [];
try { watchAlerts = (JSON.parse(localStorage.getItem("bamf-watch-alerts") || "[]") || []).filter(a => a && a.id != null); } catch {}
// The new device whose alert is held, oldest first: ones marked known, ignored or gone no longer count.
function waitingAlert() {
  const before = watchAlerts.length;
  watchAlerts = watchAlerts.filter(a => { const h = hosts.find(x => x.id === a.id); return h && !h.ignored && !h.forgotten && (a.test || !h.known); });
  if (watchAlerts.length !== before) saveWatchAlerts();
  const a = watchAlerts[0];
  return a ? { a, h: hosts.find(x => x.id === a.id) } : null;
}
function saveWatchAlerts() { try { localStorage.setItem("bamf-watch-alerts", JSON.stringify(watchAlerts.filter(a => !a.test))); } catch {} }
function watchAlert(h, test) {
  if (!h) return;
  watchAlerts = watchAlerts.filter(a => a.id !== h.id);
  if (test) watchAlerts.unshift({ id: h.id, test: true }); else watchAlerts.push({ id: h.id });
  saveWatchAlerts();
}
// The buttons follow the callout the watchtower draws.
function placeSaverActions(box) {
  const el = $("saverActions");
  if (!box) { el.hidden = true; return; }
  el.hidden = false;
  el.style.width = box.w + "px";
  el.style.transform = `translate(${Math.round(box.x)}px, ${Math.round(box.y)}px)`;
  $("svKnown").hidden = box.test;
  $("svQueue").textContent = box.count > 1 ? `1 of ${box.count} waiting` : box.test ? "Test alert" : "";
}
async function resolveWatchAlert(markKnown) {
  const a = watchAlerts[0]; if (!a) return;
  const h = hosts.find(x => x.id === a.id);
  if (markKnown && h && !a.test) {
    try { await setKnown(h, true); await refresh(); toast(`${esc(nameOrIp(h))} is marked known`); }
    catch (e) { console.error(e); toast("Couldn't mark it known - see the server log"); return; }
  }
  watchAlerts = watchAlerts.filter(x => x.id !== a.id);
  saveWatchAlerts();
  watchtower?.redraw();
  if (!watchAlerts.length) $("saver").classList.remove("pointer");
  setTimeout(saverCard, 50);
}
$("svKnown").onclick = e => { e.stopPropagation(); resolveWatchAlert(true); };
$("svDismiss").onclick = e => { e.stopPropagation(); resolveWatchAlert(false); };
function saverStatus() {
  $("setSaver").value = String(saverMins);
  $("setSaverStyle").value = saverStyle;
  $("setSaverStatus").textContent = saverMins ? `On in this browser, after ${saverMins === 60 ? "an hour" : saverMins + " minutes"}.` : "";
}
function saverCard() {
  const shown = hosts.filter(h => !h.ignored && !h.forgotten && !h.remote);
  const on = shown.filter(h => h.online).length, unknown = shown.filter(h => h.online && !h.known).length;
  $("svOn").textContent = on; $("svAll").textContent = shown.length;
  $("svTime").textContent = new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  $("svSub").textContent = [unknown ? `${unknown} unknown` : "", lastScan ? `last scan ${fmtAgo(lastScan)}` : ""].filter(Boolean).join(" \u00b7 ");
  const down = shown.filter(h => h.watched && !h.online).map(h => nameOrIp(h));
  $("svAlert").textContent = down.length ? (down.length > 2 ? `${down.slice(0, 2).join(", ")} and ${down.length - 2} more watched are offline` : `${down.join(" and ")} ${down.length === 1 ? "is" : "are"} offline`) : "";
  const card = $("saverCard"), W = innerWidth - card.offsetWidth - 40, H = innerHeight - card.offsetHeight - 40;
  const t = calmMotion() ? 0 : Date.now();
  if (watchtower) {
    // The watchtower keeps it in the corner, drifting a little, in its own words.
    const alert = watchtower.busy();
    $("saver").classList.toggle("alert", alert);
    $("svBrand").textContent = alert ? "BAMF \u00b7 ALERT" : "BAMF \u00b7 " + (watchtower.label || "ON WATCH");
    card.style.transform = `translate(${Math.round(W - 10 + Math.sin(t / 53000) * 14)}px, ${Math.round(H - 10 + Math.sin(t / 71000) * 10)}px)`;
    return;
  }
  $("svBrand").textContent = "BAMF";
  // Round the screen, slowly, so the card never sits in one place for long.
  const x = 20 + Math.max(0, W) * (.5 + .46 * Math.sin(t / 47000)), y = 20 + Math.max(0, H) * (.5 + .44 * Math.sin(t / 61000 + 1.3));
  card.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`;
}
// Points of light for a theme with no scenery: a device each, coloured as in
// the table, drifting, with a faint line between those that pass close.
function saverDots() {
  const cv = $("saverDots"), dpr = Math.min(devicePixelRatio || 1, 2), W = innerWidth, H = innerHeight;
  cv.width = W * dpr; cv.height = H * dpr;
  const g = cv.getContext("2d"); g.scale(dpr, dpr);
  const css = getComputedStyle(document.documentElement);
  const col = { up: css.getPropertyValue("--led-on").trim() || "#3fdb7f", un: css.getPropertyValue("--led-warn").trim() || "#ffb454", off: css.getPropertyValue("--led-off").trim() || "#445" };
  const dots = sceneHosts().map(h => ({ h, x: Math.random() * W, y: Math.random() * H, vx: rnd(-9, 9), vy: rnd(-7, 7), r: rnd(2.5, 4.5), p: rnd(0, 6.3) }));
  const draw = (now, dt) => {
    g.clearRect(0, 0, W, H);
    for (const d of dots) {
      d.x = (d.x + d.vx * dt + W) % W; d.y = (d.y + d.vy * dt + H) % H;
    }
    g.lineWidth = 1;
    for (let i = 0; i < dots.length; i++) for (let j = i + 1; j < dots.length; j++) {
      const a = dots[i], b = dots[j], dd = Math.hypot(a.x - b.x, a.y - b.y);
      if (dd < 160) { g.strokeStyle = `rgba(160,180,210,${.16 * (1 - dd / 160)})`; g.beginPath(); g.moveTo(a.x, a.y); g.lineTo(b.x, b.y); g.stroke(); }
    }
    for (const d of dots) {
      const st = sceneState(d.h), c = col[st], pulse = st === "off" ? .4 : .75 + .25 * Math.sin(now / 900 + d.p);
      g.globalAlpha = .18 * pulse; g.fillStyle = c; g.beginPath(); g.arc(d.x, d.y, d.r * 4, 0, Math.PI * 2); g.fill();
      g.globalAlpha = pulse; g.beginPath(); g.arc(d.x, d.y, d.r, 0, Math.PI * 2); g.fill();
    }
    g.globalAlpha = 1;
  };
  if (calmMotion()) { draw(0, 0); return; }
  let last = performance.now();
  const frame = now => {
    saverRaf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000); last = now;
    if (!document.hidden) draw(now, dt);
  };
  saverRaf = requestAnimationFrame(frame);
}
function startSaver() {
  if (saverOn) return;
  saverOn = true; saverSince = Date.now();
  document.querySelectorAll(".row-menu.show, .tools-menu.show").forEach(m => m.classList.remove("show"));
  closeThemeMenu();
  const s = $("saver");
  s.classList.remove("waking");
  s.hidden = false;
  // The watchtower if that's the style; else scenery if the theme has any,
  // otherwise the points of light.
  // The grid is the 3D one: its own module, fetched now, which falls back to the scenery or the points of light where WebGL isn't there.
  const grid = saverStyle.startsWith("grid");
  const watch = !grid && (saverForceWatch || saverStyle.startsWith("watch"));
  s.classList.toggle("watch", watch || grid);
  s.classList.toggle("grid", grid);
  s.classList.toggle("terminal", saverStyle === "grid-terminal");
  document.body.classList.toggle("saver-watch", watch || grid);
  $("saverWatch").hidden = !watch;
  $("saverGrid").hidden = !grid;
  if (grid) {
    $("saverDots").hidden = true;
    startGridSaver();
  } else if (watch) {
    $("saverDots").hidden = true;
    watchtower = buildWatchtower($("saverWatch"), saverStyle === "watch-siren", placeSaverActions);
  } else {
    const scene = !!(document.getElementById("themeBg") || document.querySelector("#festive > *"));
    $("saverDots").hidden = scene;
    if (!scene) saverDots();
  }
  document.body.classList.add("saver-anim");
  requestAnimationFrame(() => document.body.classList.add("saving"));
  saverCard();
  saverTimer = setInterval(saverCard, 1200);
}
// The grid, in 3D: the library and models come the first time, and where the browser can't draw 3D the saver is the scenery or the points of light.
async function startGridSaver() {
  const gen = ++saverGen;
  try {
    const mod = await import("/saver3d/grid.mjs");
    if (gen !== saverGen || !saverOn) return;
    const g = await mod.createGridSaver($("saverGrid"), {
      skin: saverStyle === "grid-terminal" ? "terminal" : "neon", calm: calmMotion(), siren: saverStyle === "grid-siren" ? saverSiren : null,
      hosts: sceneHosts, kindOf: deviceKind, nameOf: dispName, gatewayIp: subnet => (networkPlaces[subnet] || {}).gateway || null,
      rateOf: h => { const top = (trafficCache && trafficCache.top) || [], i = top.findIndex(x => x.hostId === h.id); return i < 0 ? 2 : Math.max(3, 9 - i); },
      unusual: async () => { const r = await fetch("/api/unusual"); return r.ok ? (await r.json()).items || [] : []; },
      waiting: () => { const w = waitingAlert(); return w ? { h: w.h, test: !!w.a.test, count: watchAlerts.length } : null; },
      describe: gridDescribe, onBox: placeSaverActions,
    });
    if (gen !== saverGen || !saverOn) { g.stop(); return; }
    watchtower = g;
    saverCard();
  } catch (err) {
    console.warn("The grid saver couldn't start:", err);
    if (gen !== saverGen || !saverOn) return;
    // Nothing 3D: the theme's scenery, or the points of light.
    $("saverGrid").hidden = true; $("saver").classList.remove("grid", "watch"); document.body.classList.remove("saver-watch");
    const scene = !!(document.getElementById("themeBg") || document.querySelector("#festive > *"));
    $("saverDots").hidden = scene;
    if (!scene) saverDots();
  }
}
// What the grid's callout says about a device, the same as the watchtower's.
function gridDescribe(h) {
  const name = nameOrIp(h);
  const guess = h.typeName || guessFamily(h.osGuess), maker = (h.vendor || "").split(/[ ,.]/)[0].toLowerCase();
  const what = [h.vendor, guess && !(maker && guess.toLowerCase().includes(maker)) ? guess : null].filter(Boolean);
  const seen = h.firstSeen ? new Date(h.firstSeen).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "";
  return {
    name,
    line1: (name !== h.ip ? h.ip + "  \u00b7  " : "") + (what.length ? (what.length > 1 ? `${what[0]}, looks like ${what[1].toLowerCase()}` : what[0]) : "no vendor known"),
    line2: `${h.mac}${seen ? "  \u00b7  first seen " + seen : ""}`,
    line3: `${h.subnet}  \u00b7  ${h.known ? "known" : "not on the known list"}`,
  };
}
function stopSaver() {
  if (!saverOn) return;
  saverGen++;
  saverOn = false; lastInput = Date.now();
  clearInterval(saverTimer); cancelAnimationFrame(saverRaf); saverRaf = 0;
  watchtower?.stop(); watchtower = null; saverForceWatch = false;
  // A test alert ends with the saver; real ones wait for next time.
  watchAlerts = watchAlerts.filter(a => !a.test);
  placeSaverActions(null);
  document.body.classList.remove("saving", "saver-watch");
  $("saver").classList.remove("alert", "pointer");
  // The overlay stays a moment to catch the rest of the click that woke it.
  const s = $("saver"); s.classList.add("waking");
  setTimeout(() => { if (!saverOn) { s.hidden = true; s.classList.remove("watch", "grid", "terminal"); $("saverWatch").hidden = true; $("saverGrid").hidden = true; document.body.classList.remove("saver-anim"); } }, 900);
}
function noteInput(e) {
  lastInput = Date.now();
  if (!saverOn) return;
  // A settling mouse just after it starts isn't someone coming back.
  if (e.type === "pointermove" && Date.now() - saverSince < 1500) return;
  // An alert on the watchtower waits to be dealt with: the mouse brings up the
  // pointer for its buttons instead of waking the page, and the buttons work.
  if (watchtower && watchtower.holding()) {
    if (e.type === "pointermove" || e.type === "wheel") { $("saver").classList.add("pointer"); return; }
    if ((e.type === "pointerdown" || e.type === "touchstart") && e.target && e.target.closest && e.target.closest("#saverActions")) return;
  }
  if (e.type === "pointerdown" || e.type === "keydown" || e.type === "touchstart") { e.preventDefault(); e.stopPropagation(); }
  stopSaver();
}
for (const ev of ["pointermove", "pointerdown", "keydown", "wheel", "touchstart"])
  document.addEventListener(ev, noteInput, { capture: true, passive: false });
document.addEventListener("visibilitychange", () => { if (!document.hidden) lastInput = Date.now(); });
setInterval(() => {
  if (saverOn || !saverMins || document.hidden) return;
  if (Date.now() - lastInput < saverMins * 60e3) return;
  if (document.querySelector(".modal-backdrop:not([hidden])")) return;
  const a = document.activeElement;
  if (a && /^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName)) return;
  startSaver();
}, 5000);
$("setSaver").onchange = () => {
  saverMins = Number($("setSaver").value) || 0;
  try { localStorage.setItem("bamf-saver", String(saverMins)); } catch {}
  saverStatus();
  toast(saverMins ? `This screen's saver starts after ${saverMins === 60 ? "an hour" : saverMins + " minutes"} idle` : "No screen saver on this screen");
};
$("setSaverStyle").onchange = () => {
  saverStyle = $("setSaverStyle").value;
  try { localStorage.setItem("bamf-saver-style", saverStyle); } catch {}
  toast(saverStyle === "scene" ? "The screen saver shows the theme's scenery"
    : saverStyle.startsWith("grid") ? "The screen saver is the grid" + (saverStyle === "grid-terminal" ? ", in terminal green" : saverStyle === "grid-siren" ? ", with the siren" : "")
    : "The screen saver is the watchtower" + (saverStyle === "watch-siren" ? ", with the siren" : ""));
};
$("setSaverTry").onclick = () => { $("setSaverTry").blur(); setTimeout(startSaver, 250); };
// A look at what a new device does to the watchtower, on one of your own
// devices, marked TEST, with only Dismiss.
$("setSaverAlert").onclick = () => {
  $("setSaverAlert").blur();
  const list = sceneHosts();
  const h = list.find(x => x.online && !x.known) || list.find(x => x.online) || list[0];
  if (!h) { toast("There's no device to show it on yet"); return; }
  watchAlert(h, true);
  saverForceWatch = !saverStyle.startsWith("grid");
  setTimeout(startSaver, 250);
};
saverStatus();

// ---- The watchtower: a screen saver with someone keeping watch ----
// Night, a full moon, and a watchtower with BAMF on the hut. On the deck, in
// a fedora and a trench coat, binoculars up and the moon behind him, stands
// the watchman. Below him is the yard, your network: every device a marker on
// the ground, green, amber or dim as in the table. His searchlight sweeps the
// yard, naming what it passes, and his binoculars follow it; now and then he
// lowers them and looks about.
//
// A new device is a jailbreak: the beacons on the hut spin up, the night goes
// red, and the light swings onto it and locks on, a reticle closing in and its
// details beside it, for fifteen seconds. A watched device dropping is LOST
// CONTACT, and coming back REACQUIRED. A scan is a fast sweep of the yard.
// The whole scene drifts a few pixels over the minutes and the moon moves, so
// nothing sits still long enough to burn in.
function buildWatchtower(cv, siren, onBox = () => {}) {
  const calm = calmMotion();
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  const g = cv.getContext("2d"); g.scale(dpr, dpr);
  const sc = Math.max(.55, Math.min(1.6, Math.min(H / 900, W / 1100)));
  const MONO = '"IBM Plex Mono", monospace', SANS = '"Space Grotesk", sans-serif';
  const horizon = H * .622, M = 40 * sc;        // M: room for the scene to drift in
  const tx = Math.max(200 * sc, W * .236), deck = H * .4, base = H + 10;
  const moon0 = { x: tx + 138 * sc, y: H * .196, r: 104 * sc };
  const lamp = { x: tx + 132 * sc, y: deck - 66 * sc };
  const man = { x: tx + 92 * sc, y: deck, s: 1.5 * sc };
  const vp = { x: W * .686, y: horizon - 6 * sc };
  const place = (d, a) => {
    const y = horizon + Math.pow(d, 1.6) * (H - horizon - 40 * sc) + 8 * sc, spread = (y - vp.y) * 2.1;
    return { x: vp.x + a * spread * .55, y, s: (.35 + d * 1.1) * sc };
  };
  let seed = 11;
  const rand = () => (seed = (seed * 16807) % 2147483647) / 2147483647, R = (a, b) => a + rand() * (b - a);

  // ---- what doesn't move, drawn once, a margin bigger all round for the drift ----
  const layer = () => { const c = document.createElement("canvas"); c.width = (W + 2 * M) * dpr; c.height = (H + 2 * M) * dpr; const x = c.getContext("2d"); x.scale(dpr, dpr); x.translate(M, M); return { c, x }; };
  const back = layer(), towerL = layer(), rail = layer();
  const lights = [];
  {
    const b = back.x;
    const sky = b.createLinearGradient(0, -M, 0, horizon);
    sky.addColorStop(0, "#03060c"); sky.addColorStop(.55, "#0a1424"); sky.addColorStop(1, "#18283f");
    b.fillStyle = sky; b.fillRect(-M, -M, W + 2 * M, horizon + M);
    for (let i = 0; i < 180 * (W / 1400); i++) { b.fillStyle = `rgba(210,225,255,${R(.1, .5)})`; const s = R(.6, 1.5); b.fillRect(R(-M, W + M), R(-M, horizon * .75), s, s); }
    for (let x = Math.max(tx + 250 * sc, W * .44); x < W + M; x += R(10, 26) * sc) {
      const bw = R(10, 30) * sc, bh = R(8, 50) * sc;
      b.fillStyle = "#0a111d"; b.fillRect(x, horizon - bh, bw, bh + 2);
      if (rand() < .65) for (let k = 0; k < 3; k++) lights.push({ x: x + R(2, bw - 3), y: horizon - R(3, bh - 2), p: R(0, 6.3), a: R(.3, .8) });
    }
    const ground = b.createLinearGradient(0, horizon, 0, H + M);
    ground.addColorStop(0, "#0e1827"); ground.addColorStop(1, "#060a10");
    b.fillStyle = ground; b.fillRect(-M, horizon, W + 2 * M, H - horizon + M);
    b.strokeStyle = "rgba(80,160,190,.09)"; b.lineWidth = 1;
    for (let k = -18; k <= 18; k++) { b.beginPath(); b.moveTo(vp.x, vp.y); b.lineTo(vp.x + k * 170 * sc, H + M); b.stroke(); }
    for (let k = 1; k < 11; k++) { const y = horizon + Math.pow(k / 10, 2.1) * (H - horizon); b.beginPath(); b.moveTo(-M, y); b.lineTo(W + M, y); b.stroke(); }
  }
  {
    const t = towerL.x, ink = "#06080c";
    const L = y => tx - 70 * sc - (y - deck) / (base - deck) * 60 * sc, Rr = y => tx + 150 * sc + (y - deck) / (base - deck) * 60 * sc;
    t.strokeStyle = ink; t.lineWidth = 16 * sc;
    t.beginPath(); t.moveTo(L(base + M), base + M); t.lineTo(L(deck + 14 * sc), deck + 14 * sc); t.moveTo(Rr(base + M), base + M); t.lineTo(Rr(deck + 14 * sc), deck + 14 * sc); t.stroke();
    t.lineWidth = 6 * sc;
    const step = (base - deck) / 4;
    for (let k = 0; k < 5; k++) {
      const y0 = deck + 26 * sc + k * step, y1 = y0 + step;
      t.beginPath(); t.moveTo(L(y0), y0); t.lineTo(Rr(y1), y1); t.moveTo(Rr(y0), y0); t.lineTo(L(y1), y1); t.moveTo(L(y1), y1); t.lineTo(Rr(y1), y1); t.stroke();
    }
    t.lineWidth = 3 * sc; t.beginPath(); t.moveTo(tx + 30 * sc, base + M); t.lineTo(tx + 40 * sc, deck + 14 * sc); t.moveTo(tx + 52 * sc, base + M); t.lineTo(tx + 58 * sc, deck + 14 * sc); t.stroke();
    for (let y = deck + 30 * sc; y < base + M; y += 22 * sc) { const k = (base - y) / (base - deck); t.beginPath(); t.moveTo(tx + 30 * sc + k * 10 * sc, y); t.lineTo(tx + 52 * sc + k * 6 * sc, y); t.stroke(); }
    t.strokeStyle = "rgba(150,175,225,.22)"; t.lineWidth = 2 * sc;
    t.beginPath(); t.moveTo(Rr(base + M) + 7 * sc, base + M); t.lineTo(Rr(deck + 14 * sc) + 7 * sc, deck + 14 * sc); t.stroke();
    t.fillStyle = ink; t.fillRect(tx - 96 * sc, deck, 266 * sc, 18 * sc);
    t.fillStyle = "rgba(160,185,230,.32)"; t.fillRect(tx - 96 * sc, deck, 266 * sc, 2 * sc);
    const hL = tx - 88 * sc, hR = tx + 26 * sc, hT = deck - 118 * sc;
    t.fillStyle = "#0a0e14"; t.fillRect(hL, hT, hR - hL, deck - hT);
    t.fillStyle = ink; t.beginPath(); t.moveTo(hL - 18 * sc, hT + 4 * sc); t.lineTo(hL + 20 * sc, hT - 30 * sc); t.lineTo(hR + 12 * sc, hT - 30 * sc); t.lineTo(hR + 26 * sc, hT + 4 * sc); t.closePath(); t.fill();
    t.strokeStyle = "rgba(160,185,230,.35)"; t.lineWidth = 1.5 * sc; t.beginPath(); t.moveTo(hL + 20 * sc, hT - 30 * sc); t.lineTo(hR + 12 * sc, hT - 30 * sc); t.lineTo(hR + 26 * sc, hT + 4 * sc); t.stroke();
    t.fillStyle = "#11161e"; t.fillRect(hL + 26 * sc, hT + 60 * sc, 80 * sc, 24 * sc);
    t.strokeStyle = "rgba(160,185,230,.3)"; t.lineWidth = 1; t.strokeRect(hL + 26 * sc, hT + 60 * sc, 80 * sc, 24 * sc);
    t.font = `700 ${20 * sc}px ${SANS}`; t.fillStyle = "rgba(225,232,245,.8)"; t.fillText("BAMF", hL + 37 * sc, hT + 79 * sc);
  }
  const hut = { l: tx - 88 * sc, r: tx + 26 * sc, t: deck - 118 * sc };
  {
    const r = rail.x, ink = "#06080c";
    r.strokeStyle = ink; r.lineWidth = 6 * sc;
    r.beginPath(); r.moveTo(tx - 96 * sc, deck - 62 * sc); r.lineTo(tx + 170 * sc, deck - 62 * sc); r.stroke();
    r.lineWidth = 3.5 * sc; r.beginPath(); r.moveTo(tx - 96 * sc, deck - 32 * sc); r.lineTo(tx + 170 * sc, deck - 32 * sc); r.stroke();
    // Posts clear of the sign on the hut.
    r.lineWidth = 5 * sc; for (const x of [-94, -70, 26, 92, 170]) { r.beginPath(); r.moveTo(tx + x * sc, deck); r.lineTo(tx + x * sc, deck - 64 * sc); r.stroke(); }
    r.strokeStyle = "rgba(170,195,240,.4)"; r.lineWidth = 1.3 * sc; r.beginPath(); r.moveTo(tx - 96 * sc, deck - 65 * sc); r.lineTo(tx + 170 * sc, deck - 65 * sc); r.stroke();
    r.fillStyle = ink; r.fillRect(lamp.x - 5 * sc, lamp.y + 12 * sc, 8 * sc, deck - lamp.y - 12 * sc);
  }
  // The moon, with its glow, once.
  const moonImg = (() => {
    const s = moon0.r * 9, c = document.createElement("canvas"); c.width = c.height = Math.round(s * dpr);
    const x = c.getContext("2d"); x.scale(dpr, dpr); const cx = s / 2, r = moon0.r;
    const glow = x.createRadialGradient(cx, cx, r * .6, cx, cx, r * 4.2);
    glow.addColorStop(0, "rgba(200,218,255,.45)"); glow.addColorStop(.35, "rgba(150,175,225,.14)"); glow.addColorStop(1, "rgba(150,175,225,0)");
    x.fillStyle = glow; x.fillRect(0, 0, s, s);
    const disc = x.createRadialGradient(cx - r * .2, cx - r * .23, r * .1, cx, cx, r);
    disc.addColorStop(0, "#f7f9ff"); disc.addColorStop(1, "#d6deef");
    x.fillStyle = disc; x.beginPath(); x.arc(cx, cx, r, 0, Math.PI * 2); x.fill();
    x.fillStyle = "rgba(150,165,195,.28)";
    for (const [dx, dy, rr] of [[-.29, -.17, .16], [.25, .21, .12], [-.06, .37, .09], [.33, -.29, .08], [-.42, .25, .07], [.08, -.04, .05]]) { x.beginPath(); x.arc(cx + dx * r, cx + dy * r, rr * r, 0, Math.PI * 2); x.fill(); }
    return { c, s };
  })();
  const clouds = [[.31, .12, .34, .55], [.17, .64, .3, .35], [.12, .74, .19, .25], [.37, .5, .4, .2], [.26, .82, .21, .3]].map(([y, x, w, a]) => ({ y: H * y, x: W * x, w: W * w, a, v: R(3, 7) * sc }));

  // ---- the yard: a place for every device, kept as long as it's there ----
  const spots = new Map();
  function spotFor(h) {
    if (spots.has(h.id)) return spots.get(h.id);
    const hs = sceneHash(h.mac || h.id);
    let best = null, bestD = -1;
    for (let k = 0; k < 10; k++) {
      const u = sceneHash(hs + ":" + k);
      const d = .12 + (u % 1000) / 1000 * .8, a = (((u >> 10) % 2000) / 1000 - 1) * .92;
      const p = place(d, a);
      const hidden = p.x < tx + 190 * sc || p.x > W - 20 || (p.x > W - 360 && p.y > H - 200);
      let near = hidden ? -1 : 1e9;
      if (!hidden) for (const q of spots.values()) near = Math.min(near, Math.hypot(p.x - q.x, (p.y - q.y) * 2.2));
      if (near > bestD || !best) { bestD = near; best = { ...p, d, a }; }
    }
    spots.set(h.id, best);
    return best;
  }
  const devices = () => sceneHosts();

  // ---- what's going on ----
  let aim = place(.6, .4), sweep = 0, events = [], cur = null;
  let raise = 1, lowerAt = performance.now() + R(20e3, 40e3), lowered = 0, lookIdle = 0;
  function event(kind, data) {
    if (kind === "scan") { sweep = 1; return; }
    if (kind === "change" && data) {
      for (const id of data.off || []) { const h = hosts.find(x => x.id === id); if (h && h.watched) events.push({ kind: "lost", h, secs: 5 }); }
      for (const id of data.back || []) { const h = hosts.find(x => x.id === id); if (h && h.watched) events.push({ kind: "back", h, secs: 4 }); }
    }
    if (calm) { if (!cur && events.length) begin(events.shift(), performance.now()); render(performance.now()); }
  }
  function begin(e, now) {
    cur = { ...e, t0: now, spot: spotFor(e.h) };
    if (e.kind === "new" && siren) saverSiren();
    if (calm && e.kind !== "new") setTimeout(() => { cur = null; if (events.length) begin(events.shift(), performance.now()); render(performance.now()); }, e.secs * 1000);
  }
  // A new device's alert holds until it's dealt with: marked known, dismissed,
  // or the device ignored or gone. The oldest waiting is the one shown.
  function waiting() {
    const before = watchAlerts.length;
    watchAlerts = watchAlerts.filter(a => { const h = hosts.find(x => x.id === a.id); return h && !h.ignored && !h.forgotten && (a.test || !h.known); });
    if (watchAlerts.length !== before) saveWatchAlerts();
    const a = watchAlerts[0];
    return a ? { a, h: hosts.find(x => x.id === a.id) } : null;
  }
  function checkAlerts(now) {
    const w = waiting();
    if (w && !(cur && cur.kind === "new" && cur.h.id === w.h.id)) begin({ kind: "new", h: w.h, test: w.a.test, secs: Infinity }, now);
    else if (!w && cur && cur.kind === "new") cur = null;
  }

  function update(dt, now) {
    for (const c of clouds) { c.x += c.v * dt; if (c.x > W + c.w) c.x = -c.w; }
    checkAlerts(now);
    if (cur && now - cur.t0 > cur.secs * 1000) cur = null;
    if (!cur && events.length) begin(events.shift(), now);
    let want;
    if (cur) want = cur.spot;
    else if (sweep > 0) { sweep = Math.max(0, sweep - dt / 2.4); want = place(.5, -.9 + 1.8 * (1 - sweep)); }
    else { const u = now / 1000; want = place(.5 + .3 * Math.sin(u * .11), .78 * Math.sin(u * .17 + 1)); }
    const k = cur ? Math.min(1, dt * 5) : sweep > 0 ? Math.min(1, dt * 8) : Math.min(1, dt * 1.5);
    aim = { x: aim.x + (want.x - aim.x) * k, y: aim.y + (want.y - aim.y) * k };
    // Now and then he lowers the glasses and has a look round; not while something's on.
    if (cur) { raise = Math.min(1, raise + dt * 4); lowerAt = now + R(25e3, 45e3); }
    else if (lowered > 0) { lowered -= dt; raise = Math.max(0, raise - dt * 1.6); lookIdle = Math.sin(now / 900) * .12; if (lowered <= 0) lowerAt = now + R(25e3, 45e3); }
    else { raise = Math.min(1, raise + dt * 1.4); lookIdle *= .9; if (now > lowerAt) lowered = R(3, 5); }
  }

  function render(now) {
    const t = now || 0;
    const ox = calm ? 0 : Math.sin(t / 170000) * 18 * sc, oy = calm ? 0 : Math.sin(t / 230000 + 1) * 10 * sc;
    g.save(); g.translate(ox, oy);
    g.drawImage(back.c, -M, -M, W + 2 * M, H + 2 * M);
    for (const l of lights) { g.fillStyle = `rgba(255,200,120,${l.a * (.6 + .4 * Math.sin(t / 1300 + l.p))})`; g.fillRect(l.x, l.y, 1.6 * sc, 1.6 * sc); }
    // The moon, slowly on its way across.
    const mx = moon0.x + (calm ? 0 : Math.sin(t / 600000) * 34 * sc), my = moon0.y + (calm ? 0 : Math.cos(t / 600000) * 12 * sc);
    g.drawImage(moonImg.c, mx - moonImg.s / 2, my - moonImg.s / 2, moonImg.s, moonImg.s);
    for (const c of clouds) {
      const cg = g.createLinearGradient(0, c.y - 14 * sc, 0, c.y + 14 * sc);
      cg.addColorStop(0, "rgba(16,24,40,0)"); cg.addColorStop(.5, `rgba(20,30,50,${c.a + .3})`); cg.addColorStop(1, "rgba(16,24,40,0)");
      g.fillStyle = cg; g.beginPath(); g.ellipse(c.x, c.y, c.w / 2, 15 * sc, 0, 0, Math.PI * 2); g.fill();
      g.strokeStyle = `rgba(185,205,245,${c.a * .45})`; g.lineWidth = 1; g.beginPath(); g.ellipse(c.x, c.y - 7 * sc, c.w / 2.4, 6 * sc, 0, Math.PI * 1.08, Math.PI * 1.92); g.stroke();
    }
    const alarm = cur && cur.kind === "new";
    const warn = cur && cur.kind === "lost", good = cur && cur.kind === "back";
    // The searchlight across the yard, and its pool.
    const rgb = alarm ? "255,236,210" : "215,232,255", a0 = alarm ? .34 : .2, spread = (alarm ? 66 : 96) * sc;
    {
      const dx = aim.x - lamp.x, dy = aim.y - lamp.y, len = Math.hypot(dx, dy) || 1, nx = -dy / len, ny = dx / len;
      const bg = g.createLinearGradient(lamp.x, lamp.y, aim.x, aim.y);
      bg.addColorStop(0, `rgba(${rgb},${a0})`); bg.addColorStop(1, `rgba(${rgb},${a0 * .35})`);
      g.fillStyle = bg; g.beginPath();
      g.moveTo(lamp.x + nx * 5 * sc, lamp.y + ny * 5 * sc); g.lineTo(aim.x + nx * spread, aim.y + ny * spread * .5); g.lineTo(aim.x - nx * spread, aim.y - ny * spread * .5); g.lineTo(lamp.x - nx * 5 * sc, lamp.y - ny * 5 * sc); g.closePath(); g.fill();
      const pool = g.createRadialGradient(aim.x, aim.y, 4, aim.x, aim.y, spread * 1.3);
      pool.addColorStop(0, `rgba(${rgb},${a0 * 1.25})`); pool.addColorStop(1, `rgba(${rgb},0)`);
      g.fillStyle = pool; g.beginPath(); g.ellipse(aim.x, aim.y, spread * 1.3, spread * .5, 0, 0, Math.PI * 2); g.fill();
    }
    // The devices, each where it lives in the yard; named as the light passes.
    const list = devices();
    for (const h of list) {
      const p = spotFor(h), st = sceneState(h), col = st === "up" ? "63,219,127" : st === "un" ? "255,180,84" : "90,100,115";
      g.fillStyle = `rgba(${col},.18)`; g.beginPath(); g.ellipse(p.x, p.y, 16 * p.s, 5 * p.s, 0, 0, Math.PI * 2); g.fill();
      g.fillStyle = `rgba(${col},${st === "off" ? .5 : .95})`; g.beginPath(); g.arc(p.x, p.y - 3 * p.s, 3.2 * p.s, 0, Math.PI * 2); g.fill();
      const dist = Math.hypot(p.x - aim.x, (p.y - aim.y) * 2);
      const tag = watchAlerts.some(x => x.id === h.id && !x.test);
      if (tag && !(cur && cur.h.id === h.id)) {
        g.strokeStyle = `rgba(255,77,61,${.5 + .3 * Math.sin(t / 300)})`; g.lineWidth = 1.4; g.beginPath(); g.ellipse(p.x, p.y, 22 * p.s, 7 * p.s, 0, 0, Math.PI * 2); g.stroke();
        g.font = `600 ${Math.round(9 + 3 * p.s / sc)}px ${MONO}`; g.fillStyle = "rgba(255,120,105,.9)"; g.fillText("NEW", p.x + 10 * p.s, p.y + 12 * p.s);
      }
      if (!cur && dist < 160 * sc) {
        g.font = `500 ${Math.round((10 + 5 * p.s / sc) * Math.min(1.2, sc))}px ${MONO}`;
        g.fillStyle = `rgba(230,240,255,${.9 * Math.min(1, (160 * sc - dist) / (60 * sc))})`;
        g.fillText(nameOrIp(h), p.x + 8 * p.s, p.y - 6 * p.s);
      }
    }
    for (const [fy, a] of [[horizon + 10 * sc, .5], [horizon + 60 * sc, .28], [horizon + 150 * sc, .16], [horizon + 280 * sc, .1]]) {
      const drift = calm ? 0 : Math.sin(t / 9000 + fy) * 30 * sc;
      const fg = g.createLinearGradient(0, fy - 30 * sc, 0, fy + 30 * sc);
      fg.addColorStop(0, "rgba(40,55,80,0)"); fg.addColorStop(.5, `rgba(40,55,80,${a})`); fg.addColorStop(1, "rgba(40,55,80,0)");
      g.fillStyle = fg; g.fillRect(-M + drift, fy - 30 * sc, W + 2 * M, 60 * sc);
    }
    // The tower, the lit window, the beacons.
    g.drawImage(towerL.c, -M, -M, W + 2 * M, H + 2 * M);
    const wx = hut.l + 30 * sc, wy = hut.t + 22 * sc, flick = calm ? 1 : .9 + .1 * Math.sin(t / 170) * Math.sin(t / 1300);
    const wg = g.createRadialGradient(wx + 22 * sc, wy + 18 * sc, 4, wx + 22 * sc, wy + 18 * sc, 100 * sc);
    wg.addColorStop(0, `rgba(255,190,110,${.28 * flick})`); wg.addColorStop(1, "rgba(255,190,110,0)");
    g.fillStyle = wg; g.fillRect(wx - 80 * sc, wy - 80 * sc, 204 * sc, 200 * sc);
    g.fillStyle = `rgb(255,${Math.round(196 * flick)},${Math.round(122 * flick)})`; g.fillRect(wx, wy, 46 * sc, 34 * sc);
    g.fillStyle = "#0a0e14"; g.fillRect(wx + 21.5 * sc, wy, 3 * sc, 34 * sc); g.fillRect(wx, wy + 15.5 * sc, 46 * sc, 3 * sc);
    for (const bx of [hut.l + 2 * sc, hut.r + 8 * sc]) {
      const by = hut.t - 30 * sc;
      if (alarm) {
        const spin = t / 140 + (bx > tx ? Math.PI : 0), face = Math.max(0, Math.cos(spin));
        const rg = g.createRadialGradient(bx, by, 2, bx, by, 110 * sc); rg.addColorStop(0, `rgba(255,60,48,${.35 + .45 * face})`); rg.addColorStop(1, "rgba(255,60,48,0)");
        g.fillStyle = rg; g.fillRect(bx - 110 * sc, by - 110 * sc, 220 * sc, 220 * sc);
        const dir = Math.sin(spin) > 0 ? 1 : -1, len = 520 * sc * Math.abs(Math.sin(spin));
        g.fillStyle = `rgba(255,70,55,${.12 + .1 * face})`; g.beginPath(); g.moveTo(bx, by - 2 * sc); g.lineTo(bx + dir * len, by - 140 * sc * Math.abs(Math.sin(spin))); g.lineTo(bx + dir * len, by + 40 * sc); g.closePath(); g.fill();
      }
      g.fillStyle = alarm ? "#ff3b30" : "#2a1110"; g.beginPath(); g.arc(bx, by, 7 * sc, Math.PI, 0); g.fill(); g.fillRect(bx - 8 * sc, by, 16 * sc, 4 * sc);
    }
    // The watchman.
    watchman(t, alarm);
    // The searchlight, turned to where it's pointing.
    g.save(); g.translate(lamp.x, lamp.y); g.rotate(Math.atan2(aim.y - lamp.y, aim.x - lamp.x));
    g.fillStyle = "#0e131b"; g.fillRect(-26 * sc, -13 * sc, 30 * sc, 26 * sc);
    g.fillStyle = "#151c26"; g.fillRect(-30 * sc, -9 * sc, 6 * sc, 18 * sc);
    g.fillStyle = alarm ? "#fff3dc" : "#e8f0ff"; g.fillRect(4 * sc, -12 * sc, 4 * sc, 24 * sc);
    const lg = g.createRadialGradient(8 * sc, 0, 1, 8 * sc, 0, 30 * sc); lg.addColorStop(0, "rgba(255,250,235,.9)"); lg.addColorStop(1, "rgba(255,250,235,0)");
    g.fillStyle = lg; g.fillRect(-22 * sc, -30 * sc, 60 * sc, 60 * sc);
    g.restore();
    g.drawImage(rail.c, -M, -M, W + 2 * M, H + 2 * M);
    g.restore();

    // The alarm over everything: the red, the reticle, the details.
    if (cur) {
      const age = (t - cur.t0) / 1000, left = cur.secs - age;
      const fade = calm ? 1 : Math.min(1, age * 3, Math.max(0, left) * 1.5);
      const settle = calm || age > 15 ? .6 : 1;
      if (alarm) {
        g.fillStyle = `rgba(255,30,20,${(.07 + (calm || age > 15 ? 0 : .04 * Math.sin(t / 220))) * fade * settle})`; g.fillRect(0, 0, W, H);
        const vg = g.createRadialGradient(W / 2, H / 2, Math.min(W, H) * .35, W / 2, H / 2, Math.max(W, H) * .7);
        vg.addColorStop(0, "rgba(130,0,0,0)"); vg.addColorStop(1, `rgba(130,0,0,${.38 * fade * settle})`);
        g.fillStyle = vg; g.fillRect(0, 0, W, H);
      }
      const col = alarm ? "255,77,61" : warn ? "255,122,70" : "63,219,127";
      const p = { x: cur.spot.x + ox, y: cur.spot.y + oy };
      const lock = calm ? 1 : easeInOut(Math.min(1, Math.max(0, (age - .8) / .7)));
      if (lock > 0) {
        g.globalAlpha = fade;
        const r = 36 * sc * (1 + 1.6 * (1 - lock));
        g.strokeStyle = `rgb(${col})`; g.lineWidth = 2;
        for (const [sx, sy] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) { g.beginPath(); g.moveTo(p.x + sx * r, p.y + sy * r * .7 - sy * 12 * sc); g.lineTo(p.x + sx * r, p.y + sy * r * .7); g.lineTo(p.x + sx * r - sx * 12 * sc, p.y + sy * r * .7); g.stroke(); }
        g.beginPath(); g.arc(p.x, p.y - 4 * sc, 16 * sc, 0, Math.PI * 2); g.stroke();
        g.beginPath(); g.moveTo(p.x - 28 * sc, p.y - 4 * sc); g.lineTo(p.x - 11 * sc, p.y - 4 * sc); g.moveTo(p.x + 11 * sc, p.y - 4 * sc); g.lineTo(p.x + 28 * sc, p.y - 4 * sc); g.stroke();
        g.fillStyle = `rgb(${col})`; g.beginPath(); g.arc(p.x, p.y - 4 * sc, 4.5 * sc, 0, Math.PI * 2); g.fill();
        // The details, beside it and kept on the screen.
        const h = cur.h, cw = 340 * sc, ch = (alarm ? 150 : 104) * sc;
        let box = null;
        const toLeft = p.x > W * .62;
        const cx = Math.min(W - cw - 20, Math.max(20, toLeft ? p.x - 80 * sc - cw : p.x + 80 * sc));
        let cy = Math.max(20, p.y - 210 * sc);
        if (cx + cw > W - 330 && cy + ch > H - 200) cy = Math.max(20, H - 210 - ch);
        const slide = calm ? 1 : easeInOut(Math.min(1, Math.max(0, (age - 1.2) / .5)));
        if (slide > 0) {
          g.globalAlpha = fade * slide;
          g.strokeStyle = `rgba(${col},.8)`; g.lineWidth = 1.5; g.beginPath(); g.moveTo(p.x + (toLeft ? -28 : 28) * sc, p.y - 28 * sc); g.lineTo(toLeft ? cx + cw : cx, cy + ch); g.stroke();
          g.fillStyle = "rgba(12,7,9,.9)"; g.fillRect(cx, cy, cw, ch);
          g.strokeStyle = `rgb(${col})`; g.lineWidth = 2; g.strokeRect(cx, cy, cw, ch);
          g.fillStyle = `rgb(${col})`; g.fillRect(cx, cy, cw, 28 * sc);
          g.font = `600 ${14 * sc}px ${MONO}`; g.fillStyle = "#140504";
          g.fillText(alarm ? (cur.test ? "TEST  ·  NEW DEVICE" : "NEW DEVICE  ·  LOCKED ON") : warn ? "LOST CONTACT" : "REACQUIRED", cx + 12 * sc, cy + 19 * sc);
          if (alarm) box = { x: cx, y: cy + ch + 10 * sc, w: cw, id: h.id, test: !!cur.test, count: watchAlerts.length };
          const name = nameOrIp(h);
          g.font = `600 ${26 * sc}px ${MONO}`; g.fillStyle = "#fff1ee"; g.fillText(name.length > 18 ? name.slice(0, 17) + "…" : name, cx + 14 * sc, cy + 60 * sc);
          const guess = h.typeName || guessFamily(h.osGuess), maker = (h.vendor || "").split(/[ ,.]/)[0].toLowerCase();
          const what = [h.vendor, guess && !(maker && guess.toLowerCase().includes(maker)) ? guess : null].filter(Boolean);
          g.font = `500 ${15 * sc}px ${SANS}`; g.fillStyle = "#f3c9c3";
          g.fillText((name !== h.ip ? h.ip + "  ·  " : "") + (what.length ? (what.length > 1 ? `${what[0]}, looks like ${what[1].toLowerCase()}` : what[0]) : "no vendor known"), cx + 14 * sc, cy + 85 * sc, cw - 24 * sc);
          if (alarm) {
            const seen = h.firstSeen ? new Date(h.firstSeen).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "";
            g.font = `500 ${12.5 * sc}px ${MONO}`; g.fillStyle = "#d69b92";
            g.fillText(`${h.mac}${seen ? "  ·  first seen " + seen : ""}`, cx + 14 * sc, cy + 110 * sc, cw - 24 * sc);
            g.fillText(`${h.subnet}  ·  ${h.known ? "known" : "not on the known list"}`, cx + 14 * sc, cy + 130 * sc, cw - 24 * sc);
          }
        }
        g.globalAlpha = 1;
        onBox(box);
      } else onBox(null);
    } else onBox(null);
  }

  // The watchman, in profile at the rail, backlit by the moon.
  const ink = "#040609";
  const HEAD = new Path2D("M -10 -150 C -12 -160 -8 -168 -1 -170 C 6 -170 10 -166 10 -160 L 13 -151 L 10 -149 L 11 -145 C 9 -142 5 -140 0 -140 C -4 -140 -8 -143 -10 -150 Z");
  // Worn down on his head, the brim at his brow and the crown over the top of it.
  const HAT = new Path2D("M -27 -159 Q -24 -165 -12 -165 L 14 -166 Q 26 -165 31 -158 L 29 -156 Q 22 -161 12 -162 L -12 -161 Q -22 -161 -27 -157 Z M -14 -164 C -15 -174 -11 -180 -3 -180 L 3 -179 C 11 -180 15 -175 14 -164 Z");
  const lerp = (a, b, k) => a + (b - a) * k;
  function watchman(t, alarm) {
    const tail = calm ? 0 : Math.sin(t / 380) * .7 + Math.sin(t / 1170) * .5;
    const bob = calm ? 0 : Math.sin(t / 1600) * .5;
    const coat = new Path2D(
      `M -7 -154 C -11 -154 -13 -150 -13 -145 C -17 -143 -21 -140 -22 -134 C -22 -118 -21 -102 -20 -90 L -19 -87 ` +
      `C -22 -66 ${-26 - tail} -48 ${-33 - tail * 2} -34 L ${-42 - tail * 4} ${-25 + tail * 1.5} L -28 -24 L -10 -22 L -9 0 L -1 0 L 0 -21 ` +
      `L 5 -21 L 6 -3 L 17 -2 L 18 1 L 3 1 L 3 0 L 12 -22 L 21 -23 C 19 -44 16 -64 13 -85 L 14 -89 C 14 -104 13 -122 10 -134 ` +
      `C 8 -140 4 -144 0 -146 L -2 -152 Z`);
    // Where he's looking: at the light while the glasses are up, about him while they're down.
    const toAim = Math.max(.02, Math.min(.34, Math.atan2(aim.y - (man.y - 150 * man.s), aim.x - man.x) * .42));
    const look = lerp(lookIdle, toAim, raise);
    const shoulder = [2, -134], elbow = [lerp(12, 25, raise), lerp(-104, -112, raise)], hand = [lerp(20, 18, raise), lerp(-92, -156, raise)];
    // The glasses: at his eyes when up, in his hand when down.
    const up = (() => { const c = Math.cos(look), s = Math.sin(look), x = 20, y = -160 + 140; return [x * c - y * s, x * s + y * c - 140]; })();
    const bc = [lerp(hand[0] + 4, up[0], raise), lerp(hand[1] - 2, up[1], raise)], ba = lerp(.05, look, raise);
    const parts = glow => {
      g.fillStyle = ink; g.strokeStyle = ink;
      g.fill(coat);
      g.save(); g.translate(0, -140); g.rotate(look); g.translate(0, 140); g.fill(HEAD); g.fill(HAT); g.restore();
      g.lineCap = "round"; g.lineJoin = "round"; g.lineWidth = 9;
      g.beginPath(); g.moveTo(...shoulder); g.lineTo(...elbow); g.lineTo(...hand); g.stroke();
      g.save(); g.translate(bc[0], bc[1]); g.rotate(ba);
      g.fillRect(-12, -5.5, 19, 11); g.fillRect(5, -7.5, 7, 15);
      g.restore();
    };
    g.save(); g.translate(man.x, man.y + bob); g.scale(man.s, man.s);
    g.save(); g.shadowColor = alarm ? "rgba(255,150,140,.95)" : "rgba(205,222,255,.95)"; g.shadowBlur = 7 * man.s; parts(); g.restore();
    parts();
    // The hat band and the BAMF star on it.
    g.save(); g.translate(0, -140); g.rotate(look); g.translate(0, 140);
    g.fillStyle = "#132016"; g.beginPath(); g.moveTo(-14, -167); g.lineTo(14, -168); g.lineTo(14, -165); g.lineTo(-14, -164); g.closePath(); g.fill();
    g.translate(-5, -166);
    g.fillStyle = "rgba(63,219,127,.35)"; g.beginPath(); g.arc(0, 0, 4.5, 0, Math.PI * 2); g.fill();
    g.fillStyle = "#3fdb7f"; for (let k = 0; k < 4; k++) { g.rotate(Math.PI / 4); g.fillRect(-2.8, -.6, 5.6, 1.2); }
    g.restore();
    // The lens catching the light while the glasses are up.
    if (raise > .6) { g.save(); g.translate(bc[0], bc[1]); g.rotate(ba); g.fillStyle = alarm ? "rgba(255,120,105,.95)" : "rgba(215,232,255,.95)"; g.fillRect(11.4, -6.5, 1.3, 13); g.restore(); }
    // The window's warmth on his back.
    g.save(); g.clip(coat);
    const warm = g.createLinearGradient(-30, 0, 2, 0); warm.addColorStop(0, "rgba(255,170,90,.16)"); warm.addColorStop(1, "rgba(255,170,90,0)");
    g.fillStyle = warm; g.fillRect(-50, -160, 60, 170); g.restore();
    // His breath in the cold, now and then.
    if (!calm) {
      const b = (t / 4200) % 1;
      if (b < .5) { const k = b / .5; g.fillStyle = `rgba(210,222,240,${.16 * (1 - k)})`; g.beginPath(); g.ellipse(16 + k * 14, -146 - k * 6, 3 + k * 7, 2 + k * 3, -.3, 0, Math.PI * 2); g.fill(); }
    }
    g.restore();
  }

  // ---- run it ----
  let raf = 0, last = performance.now();
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (document.hidden || now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000); last = now;
    update(dt, now); render(now);
  };
  if (calm) { checkAlerts(performance.now()); render(performance.now()); }
  else raf = requestAnimationFrame(frame);
  return { event, stop() { cancelAnimationFrame(raf); raf = 0; onBox(null); }, busy: () => !!cur,
    holding: () => !!(cur && cur.kind === "new"), redraw() { if (calm) { checkAlerts(performance.now()); render(performance.now()); } } };
}

// The siren, if it's been asked for: a two-tone wail, three times, low.
function saverSiren() {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .02, o = ctx.createOscillator(), lp = ctx.createBiquadFilter(), g = ctx.createGain();
  o.type = "sawtooth"; lp.type = "lowpass"; lp.frequency.value = 1400;
  for (let k = 0; k < 3; k++) { o.frequency.setValueAtTime(560, t + k * 1.1); o.frequency.linearRampToValueAtTime(1080, t + k * 1.1 + .55); o.frequency.linearRampToValueAtTime(560, t + k * 1.1 + 1.1); }
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.05, t + .2); g.gain.setValueAtTime(.05, t + 3.1); g.gain.linearRampToValueAtTime(0, t + 3.4);
  o.connect(lp).connect(g).connect(ctx.destination); o.start(t); o.stop(t + 3.5);
}
