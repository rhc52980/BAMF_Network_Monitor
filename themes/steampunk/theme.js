// Steampunk: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Steampunk: brass gears turning behind the page (they lurch forward when a
// scan finishes), an airship drifting past now and then, steam from the pipe
// under the header, a telegraph ticker for a new device, and a hiss of steam
// on the row of a device that goes offline.
function gearPath(teeth, r, depth) {
  // Trapezoid teeth round a circle of radius r, depth deep, centred on 0,0.
  const pts = [], step = Math.PI * 2 / teeth;
  for (let i = 0; i < teeth; i++) {
    const a = i * step;
    for (const [da, rr] of [[-.30, r - depth], [-.18, r], [.18, r], [.30, r - depth]])
      pts.push([Math.cos(a + da * step * 1.6) * rr, Math.sin(a + da * step * 1.6) * rr]);
  }
  return "M" + pts.map(p => p.map(v => v.toFixed(1)).join(" ")).join("L") + "Z";
}

function gearSvg(teeth, r, rev, secs) {
  const hub = r * .28, rim = r - r * .2;
  const spokes = Array.from({ length: 5 }, (_, i) => {
    const a = i * Math.PI * 2 / 5;
    return `M${(Math.cos(a) * hub).toFixed(1)} ${(Math.sin(a) * hub).toFixed(1)}L${(Math.cos(a) * rim * .82).toFixed(1)} ${(Math.sin(a) * rim * .82).toFixed(1)}`;
  }).join("");
  return `<svg viewBox="${-r} ${-r} ${2 * r} ${2 * r}"><g class="spin${rev ? " rev" : ""}" style="--t:${secs}s"><g class="kick">
    <path d="${gearPath(teeth, r, r * .12)}" fill="#b8894a"/>
    <circle r="${rim * .86}" fill="#5a3d1c"/><path d="${spokes}" stroke="#b8894a" stroke-width="${(r * .09).toFixed(1)}" stroke-linecap="round"/>
    <circle r="${hub}" fill="#b8894a"/><circle r="${hub * .45}" fill="#2a1d12"/></g></g></svg>`;
}

const SP_AIRSHIP = `<svg viewBox="0 0 220 100" aria-hidden="true"><g fill="#c9a36a" stroke="#3a2716" stroke-width="2">
  <ellipse cx="110" cy="38" rx="92" ry="30"/><path d="M26 38h168M40 20q70-10 140 0M40 56q70 10 140 0" fill="none"/>
  <path d="M8 38l-6-18 26 8M8 38l-6 18 26-8"/>
  <path d="M84 66l6 14h40l6-14" fill="#8a6a33"/><rect x="88" y="80" width="44" height="12" rx="3"/>
  <circle cx="98" cy="86" r="2.5" fill="#ffcc66"/><circle cx="110" cy="86" r="2.5" fill="#ffcc66"/><circle cx="122" cy="86" r="2.5" fill="#ffcc66"/>
  <rect class="prop" x="202" y="26" width="5" height="24" rx="2"/></g></svg>`;

// Steampunk's sounds: a hiss of steam from the valve, a clank of the gears
// when a scan finishes, a dull clunk for a device that has gone, a steam
// whistle for a new one, and a small bell when one comes back.
let spAudio = null;

function steampunkSound() {
  const ctx = () => {
    try { spAudio = spAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return null; }
    if (spAudio.state === "suspended") spAudio.resume?.().catch(() => {});
    return spAudio;
  };
  const noise = (c, len) => {
    const buf = c.createBuffer(1, Math.floor(c.sampleRate * len), c.sampleRate), d = buf.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    return buf;
  };
  const hiss = (c, at, len, gain) => {
    const src = c.createBufferSource(); src.buffer = noise(c, len);
    const bp = c.createBiquadFilter(); bp.type = "bandpass"; bp.frequency.value = 2600; bp.Q.value = .7;
    const g = c.createGain();
    g.gain.setValueAtTime(0, at); g.gain.linearRampToValueAtTime(gain, at + .06); g.gain.exponentialRampToValueAtTime(.001, at + len);
    src.connect(bp).connect(g).connect(c.destination); src.start(at); src.stop(at + len);
  };
  const metal = (c, at, freqs, len, gain) => freqs.forEach((f, i) => {
    const o = c.createOscillator(), g = c.createGain();
    o.type = i ? "triangle" : "square"; o.frequency.value = f;
    g.gain.setValueAtTime(gain / (i + 1), at); g.gain.exponentialRampToValueAtTime(.0001, at + len);
    o.connect(g).connect(c.destination); o.start(at); o.stop(at + len + .05);
  });
  return {
    hiss(len = .8) { const c = ctx(); if (!c) return; hiss(c, c.currentTime, len, .16); },
    clank() { const c = ctx(); if (!c) return; const at = c.currentTime; metal(c, at, [620, 1370, 2210], .3, .1); metal(c, at + .11, [540, 1180], .25, .07); },
    clunk() { const c = ctx(); if (!c) return; metal(c, c.currentTime, [180, 260, 410], .35, .12); },
    whistle() {
      const c = ctx(); if (!c) return;
      const at = c.currentTime, len = .9;
      const o = c.createOscillator(), g = c.createGain(), v = c.createOscillator(), vg = c.createGain();
      o.type = "sine";
      o.frequency.setValueAtTime(880, at); o.frequency.exponentialRampToValueAtTime(1320, at + .12);
      o.frequency.setValueAtTime(1320, at + len - .15); o.frequency.exponentialRampToValueAtTime(900, at + len);
      v.frequency.value = 6; vg.gain.value = 18; v.connect(vg).connect(o.frequency);
      g.gain.setValueAtTime(0, at); g.gain.linearRampToValueAtTime(.1, at + .08); g.gain.setValueAtTime(.1, at + len - .12); g.gain.linearRampToValueAtTime(0, at + len);
      o.connect(g).connect(c.destination); o.start(at); v.start(at); o.stop(at + len); v.stop(at + len);
      hiss(c, at, len + .2, .05);
    },
    bell() {
      const c = ctx(); if (!c) return;
      const at = c.currentTime;
      [1568, 1976].forEach((f, i) => {
        const o = c.createOscillator(), g = c.createGain();
        o.type = "sine"; o.frequency.value = f;
        g.gain.setValueAtTime(.1, at + i * .18); g.gain.exponentialRampToValueAtTime(.0001, at + i * .18 + .7);
        o.connect(g).connect(c.destination); o.start(at + i * .18); o.stop(at + i * .18 + .8);
      });
    },
  };
}

// The Map for Steampunk: a turning cog on anything running, a top hat on the router.
function steampunkMapDeco(g, n) {
  const under = el => g.insertBefore(el, g.querySelector(".t-watch"));
  if (n.type === "dev") {
    const cog = svgEl("g", { class: "t-cog", transform: "translate(14,-14)" });
    cog.appendChild(svgEl("path", { d: gearPath(8, 6, 1.8) }));
    cog.appendChild(svgEl("circle", { r: 1.6, fill: "#2a1d12" }));
    under(cog);
  }
  if (n.type === "root" || n.isRoot) {
    const top = svgEl("g", { class: "t-tophat", transform: "translate(0,-17)" });
    top.appendChild(svgEl("path", { d: "M-15 0h30v-2.5h-30z M-9 -2.5v-14h18v14z", fill: "#3b2a1a", stroke: "#e8c27a", "stroke-width": 1.2 }));
    top.appendChild(svgEl("rect", { x: -9, y: -7, width: 18, height: 4, fill: "#c2371f" }));
    under(top);
  }
}

function buildSteampunk(root) {
  festiveHooks.decorateNode = steampunkMapDeco;
  const calm = calmMotion();
  // Sound: off until the speaker button beside the theme button is clicked.
  const sp = steampunkSound();
  const soundOn = themeSoundButton("bamf-steampunk-sound", "Steampunk sounds on: click to mute",
    "Steampunk sounds off: click to hear the works", on => { if (on) sp.hiss(.9); });
  const play = (fn, ...a) => { if (soundOn() && !document.hidden) fn(...a); };
  // A device comes back: a small bell.
  festiveHooks.netChange = (off, back) => { if (back.length) play(sp.bell); };
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg sp-bg";
  bg.setAttribute("aria-hidden", "true");
  // Two meshed pairs: bottom left and top right. A gear turns in time with
  // its tooth count, and each turns against the one it meshes with.
  const gears = [
    { t: 24, r: 150, x: -90, y: "calc(100vh - 210px)", rev: false },
    { t: 12, r: 78, x: 196, y: "calc(100vh - 156px)", rev: true },
    { t: 18, r: 110, x: "calc(100vw - 150px)", y: 90, rev: true },
    { t: 10, r: 62, x: "calc(100vw - 250px)", y: 70, rev: false },
  ];
  bg.innerHTML = gears.map(g => {
    const px = v => typeof v === "number" ? v + "px" : v;
    return `<div class="sp-gear" style="left:${px(g.x)};top:${px(g.y)};width:${2 * g.r}px;height:${2 * g.r}px">${gearSvg(g.t, g.r, g.rev, g.t * 2.5)}</div>`;
  }).join("");
  document.body.prepend(bg);

  const pipeY = () => (document.querySelector("header")?.getBoundingClientRect().bottom || 60);
  // The valve on the pipe, and the steam it lets off.
  const valveX = Math.round(innerWidth * .5);
  const valve = document.createElement("div");
  valve.className = "sp-valve";
  valve.style.cssText = `left:${valveX}px;top:${pipeY() - 6}px`;
  valve.innerHTML = `<svg viewBox="0 0 26 22" width="26" height="22"><rect x="10" y="0" width="6" height="10" fill="#8a6a33" stroke="#3a2716"/>
    <circle cx="13" cy="4" r="7" fill="none" stroke="#c99a3f" stroke-width="2.5"/><path d="M6 4h14M13 -3v14" stroke="#c99a3f" stroke-width="1.6"/></svg>`;
  root.appendChild(valve);
  const steam = n => {
    if (calm || document.hidden) return;
    play(sp.hiss, n >= 5 ? 1.4 : .8);
    for (let i = 0; i < n; i++) {
      const p = document.createElement("i");
      p.className = "sp-puff";
      p.style.cssText = `left:${valveX + 6}px;top:${pipeY() - 12}px;--dx:${rnd(-30, 30).toFixed(0)}px;animation-delay:${(i * .18).toFixed(2)}s`;
      p.addEventListener("animationend", () => p.remove());
      root.appendChild(p);
    }
  };
  if (!calm) {
    festiveTimers.push(setInterval(() => steam(3), 38e3));
    // An airship drifts past, behind everything.
    const ship = () => {
      if (document.hidden) return;
      const a = document.createElement("div");
      a.className = "sp-airship";
      a.innerHTML = SP_AIRSHIP;
      a.addEventListener("animationend", e => { if (e.target === a) a.remove(); });
      bg.appendChild(a);
    };
    const nextShip = first => festiveTimers.push(setTimeout(() => { ship(); nextShip(false); }, first ? rnd(15e3, 40e3) : rnd(120e3, 240e3)));
    nextShip(true);
  }

  // A scan finishes: the gears lurch forward and the valve lets off steam.
  festiveHooks.scanDone = () => {
    play(sp.clank);
    bg.classList.remove("burst"); void bg.offsetWidth; bg.classList.add("burst");
    festiveTimers.push(setTimeout(() => bg.classList.remove("burst"), 1900));
    steam(5);
  };
  // A new device: a telegraph ticker tape.
  festiveHooks.newDevice = h => {
    if (document.hidden) return;
    play(sp.whistle);
    const t = document.createElement("div");
    t.className = "sp-ticker";
    t.style.top = (pipeY() + 22) + "px";
    t.textContent = `⚙ Telegram — new arrival: ${h ? nameOrIp(h) : "unknown device"} — stop`;
    root.appendChild(t);
    festiveTimers.push(setTimeout(() => t.remove(), 5600));
  };
  // A device goes offline: a hiss of steam on its row.
  festiveHooks.rendered = () => {
    if (wentOffIds.length) play(sp.clunk);
    for (const id of wentOffIds) {
      const cell = document.querySelector(`tr[data-id="${id}"] td.hostname`);
      if (!cell) continue;
      const s = document.createElement("span");
      s.className = "sp-hiss";
      s.textContent = "hsss";
      cell.appendChild(s);
      festiveTimers.push(setTimeout(() => s.remove(), 1900));
    }
    wentOffIds = [];
  };
  steampunkExtras(root);
}

// More Steampunk: a pressure gauge in the header, a brass clock on the Last
// scan card, wind-up keys on watched devices, lamps in the corners, a lens
// over addresses, a telescope search, capsules along the Map's pipes, and a
// Map that unrolls. Identify's punch card and the port scan's orrery are CSS.
function steampunkExtras(root) {
  const calm = calmMotion();
  const added = [];   // elements put into the page outside the effects layer, removed on a theme change
  festiveStops.push(() => added.forEach(el => el.remove()));

  // Pressure: devices online now against the most seen online this session.
  // Well below that and the needle drops into the red and trembles.
  const gauge = document.createElement("div");
  gauge.className = "sp-gauge";
  gauge.innerHTML = `<svg width="34" height="34" viewBox="0 0 34 34" aria-hidden="true">
    <circle cx="17" cy="17" r="15.5" fill="#efe2c0" stroke="#c99a3f" stroke-width="2.5"/>
    <path d="M6.4 25.6A15 15 0 0 1 4 12" fill="none" stroke="#c2371f" stroke-width="3"/>
    ${Array.from({ length: 9 }, (_, i) => { const a = (-120 + i * 30) * Math.PI / 180;
      return `<path d="M${(17 + Math.sin(a) * 11).toFixed(1)} ${(17 - Math.cos(a) * 11).toFixed(1)}L${(17 + Math.sin(a) * 13.5).toFixed(1)} ${(17 - Math.cos(a) * 13.5).toFixed(1)}" stroke="#3a2716" stroke-width="1"/>`; }).join("")}
    <g class="needle-wrap"><g class="needle"><path d="M17 17L17 5" stroke="#1a120b" stroke-width="1.6" stroke-linecap="round"/></g></g>
    <circle cx="17" cy="17" r="2.4" fill="#c99a3f"/></svg>`;
  const track = document.querySelector(".scanline .scan-track");
  track?.parentElement.insertBefore(gauge, track);
  added.push(gauge);
  let peak = 0;
  const readGauge = () => {
    const live = hosts.filter(h => !h.ignored && !h.forgotten);
    const on = live.filter(h => h.online).length;
    peak = Math.max(peak, on);
    const share = peak ? on / peak : 1;
    gauge.querySelector(".needle").style.transform = `rotate(${(-120 + share * 240).toFixed(0)}deg)`;
    gauge.classList.toggle("red", share < .7);
    gauge.title = `Pressure: ${on} device${on === 1 ? "" : "s"} online, of ${peak} at most this session`;
  };

  // A brass clock on the Last scan card, keeping real time.
  const lastCard = $("statLast")?.closest(".stat");
  const clock = document.createElement("div");
  clock.className = "sp-clock";
  clock.setAttribute("aria-hidden", "true");
  clock.innerHTML = `<svg width="44" height="44" viewBox="0 0 44 44">
    <circle cx="22" cy="22" r="20" fill="#efe2c0" stroke="#c99a3f" stroke-width="3"/>
    ${Array.from({ length: 12 }, (_, i) => { const a = i * Math.PI / 6;
      return `<circle cx="${(22 + Math.sin(a) * 15.5).toFixed(1)}" cy="${(22 - Math.cos(a) * 15.5).toFixed(1)}" r="${i % 3 ? .9 : 1.5}" fill="#3a2716"/>`; }).join("")}
    <path class="hand hr" d="M22 22V12" stroke="#1a120b" stroke-width="2.4" stroke-linecap="round"/>
    <path class="hand mn" d="M22 22V7" stroke="#1a120b" stroke-width="1.6" stroke-linecap="round"/>
    <path class="sec" d="M22 25V6" stroke="#c2371f" stroke-width=".8"/>
    <circle cx="22" cy="22" r="1.8" fill="#c99a3f"/></svg>`;
  if (lastCard) { lastCard.appendChild(clock); added.push(clock); }
  const setClock = () => {
    const d = new Date(), m = d.getMinutes() + d.getSeconds() / 60, h = (d.getHours() % 12) + m / 60;
    clock.querySelector(".hr").style.transform = `rotate(${h * 30}deg)`;
    clock.querySelector(".mn").style.transform = `rotate(${m * 6}deg)`;
    clock.querySelector(".sec").style.animationDelay = `-${d.getSeconds()}s`;
  };
  setClock();
  festiveTimers.push(setInterval(setClock, 20e3));

  // Oil lamps in the corners.
  const lamps = document.createElement("div");
  lamps.className = "sp-lamps";
  root.appendChild(lamps);

  // The telescope search: rows glint as you type.
  const glint = () => {
    if (calm) return;
    const tb = document.querySelector("table tbody");
    if (!tb) return;
    tb.classList.remove("sp-glint"); void tb.offsetWidth; tb.classList.add("sp-glint");
  };
  const search = $("search");
  const onInput = () => festiveTimers.push(setTimeout(glint, 30));
  search?.addEventListener("input", onInput);
  festiveStops.push(() => search?.removeEventListener("input", onInput));

  // Now and then a brass capsule shoots along a pipe on the Map, with a
  // thunk of air where it lands.
  if (!calm) festiveTimers.push(setInterval(() => {
    if (document.hidden) return;
    const pipes = [...document.querySelectorAll(".map-svg .pkt")].filter(p => p.getAttribute("d"));
    if (!pipes.length) return;
    const p = pick(pipes), len = p.getTotalLength();
    p.style.setProperty("--len", Math.ceil(len));
    p.classList.remove("sp-tube"); void p.getBoundingClientRect(); p.classList.add("sp-tube");
    festiveTimers.push(setTimeout(() => {
      p.classList.remove("sp-tube");
      const end = p.getPointAtLength(len);
      const puff = document.createElementNS("http://www.w3.org/2000/svg", "circle");
      puff.setAttribute("class", "sp-thunk");
      puff.setAttribute("cx", end.x.toFixed(1)); puff.setAttribute("cy", end.y.toFixed(1)); puff.setAttribute("r", 3);
      p.parentElement.appendChild(puff);
      festiveTimers.push(setTimeout(() => puff.remove(), 850));
    }, 1300));
  }, 7000));

  // After each render: the gauge, keys on watched rows, and the Map unrolling
  // when you arrive on it.
  let lastView = view;
  const hiss = festiveHooks.rendered;
  festiveHooks.rendered = () => {
    const down = new Set(wentOffIds);
    hiss?.();
    readGauge();
    for (const h of hosts) {
      if (!h.watched) continue;
      const cell = document.querySelector(`tr[data-id="${h.id}"] td.hostname`);
      if (!cell || cell.querySelector(".sp-key")) continue;
      const k = document.createElement("span");
      k.className = "sp-key" + (down.has(h.id) ? " down" : h.online ? "" : " stopped");
      k.title = h.online ? "Wound and running" : "Wound down: offline";
      k.innerHTML = `<svg width="18" height="12" viewBox="0 0 18 12"><path class="bow" d="M5 1.5a4.5 4.5 0 1 0 .01 0z M5 4a2 2 0 1 1-.01 0z" fill="#c99a3f" fill-rule="evenodd"/>
        <path d="M9 5h8v2h-1.5v2.5h-1.5V7h-1v2h-1.5V7H9z" fill="#c99a3f"/></svg>`;
      cell.querySelector(".name-text")?.after(k);
    }
    if (view === "map" && lastView !== "map" && !calm) {
      const mc = $("mapClusters");
      mc.classList.remove("sp-unroll"); void mc.offsetWidth; mc.classList.add("sp-unroll");
      festiveTimers.push(setTimeout(() => mc.classList.remove("sp-unroll"), 1000));
    }
    lastView = view;
  };

  // A finished scan also rings the clock.
  const lurch = festiveHooks.scanDone;
  festiveHooks.scanDone = () => {
    lurch?.();
    if (!lastCard || calm) return;
    clock.classList.remove("chime"); void clock.offsetWidth; clock.classList.add("chime");
    const n = document.createElement("span");
    n.className = "sp-note";
    n.textContent = "♪";
    lastCard.appendChild(n);
    festiveTimers.push(setTimeout(() => n.remove(), 1500));
  };
  festiveHooks.rendered();
}

BAMF.registerTheme("steampunk", ctx => buildSteampunk(ctx.root, ctx.switched));
})();
