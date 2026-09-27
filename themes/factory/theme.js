// Factory: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- The factory's noises, made in the browser like the rest. Off unless
// you press the speaker beside the theme button. ----
// The line running: a low motor hum with rollers ticking over it.
function beltHum(night) {
  const ctx = waterCtx(); if (!ctx) return null;
  const src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, 4, true); src.loop = true;
  f.type = "lowpass"; f.frequency.value = night ? 180 : 300;
  g.gain.setValueAtTime(0, ctx.currentTime); g.gain.linearRampToValueAtTime(night ? .035 : .055, ctx.currentTime + 1.5);
  src.connect(f).connect(g).connect(ctx.destination); src.start();
  const o = ctx.createOscillator(), og = ctx.createGain();
  o.type = "sawtooth"; o.frequency.value = night ? 44 : 52; og.gain.value = .014;
  o.connect(og).connect(ctx.destination); o.start();
  return { stop() { try { g.gain.setTargetAtTime(0, ctx.currentTime, .3); og.gain.setTargetAtTime(0, ctx.currentTime, .3); src.stop(ctx.currentTime + 1.2); o.stop(ctx.currentTime + 1.2); } catch { } } };
}

// The press: a hiss of air, then the thump of the ram.
function pressThump(vol = 1) {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01;
  const air = ctx.createBufferSource(), af = ctx.createBiquadFilter(), ag = ctx.createGain();
  air.buffer = noiseBuffer(ctx, .3, false);
  af.type = "highpass"; af.frequency.value = 2400;
  ag.gain.setValueAtTime(.08 * vol, t); ag.gain.exponentialRampToValueAtTime(.001, t + .22);
  air.connect(af).connect(ag).connect(ctx.destination); air.start(t); air.stop(t + .3);
  const o = ctx.createOscillator(), g = ctx.createGain();
  o.type = "sine"; o.frequency.setValueAtTime(140, t + .16); o.frequency.exponentialRampToValueAtTime(44, t + .34);
  g.gain.setValueAtTime(.001, t + .15); g.gain.linearRampToValueAtTime(.3 * vol, t + .18); g.gain.exponentialRampToValueAtTime(.001, t + .5);
  o.connect(g).connect(ctx.destination); o.start(t + .15); o.stop(t + .55);
}

// A crate landing on the belt: a wooden knock with a metallic edge.
function crateClank(vol = 1) {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01;
  for (const [f0, f1, d, v] of [[420, 180, .12, .1], [1600, 900, .07, .05]]) {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "triangle"; o.frequency.setValueAtTime(f0, t); o.frequency.exponentialRampToValueAtTime(f1, t + d);
    g.gain.setValueAtTime(v * vol, t); g.gain.exponentialRampToValueAtTime(.001, t + d + .04);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + d + .06);
  }
}

// The beacon when a machine stops: two short buzzes, not a siren.
function andonBuzz() {
  const ctx = waterCtx(); if (!ctx) return;
  let t = ctx.currentTime + .02;
  for (let i = 0; i < 2; i++) {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "square"; o.frequency.value = 330;
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.07, t + .02); g.gain.setValueAtTime(.07, t + .16); g.gain.linearRampToValueAtTime(0, t + .2);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .22);
    t += .3;
  }
}

// A crate, a forklift and an arm, drawn small: they're only ever a few dozen
// pixels tall at the bottom of the screen.
const FAC_CRATE = `<svg viewBox="0 0 54 40"><rect x="1" y="6" width="52" height="33" rx="2" fill="#b07f3f" stroke="#6d4d24" stroke-width="2"/>
  <path d="M3 8l48 29M51 8L3 37" stroke="#8a6330" stroke-width="2"/>
  <rect x="1" y="6" width="52" height="7" fill="#c08f4b" stroke="#6d4d24" stroke-width="2"/></svg>`;

const FAC_PART = `<svg viewBox="0 0 54 40"><rect x="1" y="6" width="52" height="33" rx="2" fill="#1c1f24" stroke="#ffb300" stroke-width="2"/>
  <path d="M2 34l6-6h6l-6 6zM14 34l6-6h6l-6 6zM26 34l6-6h6l-6 6zM38 34l6-6h6l-6 6z" fill="#ffb300"/>
  <text x="27" y="26" text-anchor="middle" font-family="IBM Plex Mono, monospace" font-weight="700" font-size="16" fill="#ff4d3d">?</text></svg>`;

const FAC_ARM = `<svg viewBox="0 0 70 72" width="70" height="72">
  <rect x="24" y="60" width="26" height="10" rx="2" fill="#3d4650" stroke="#232a32" stroke-width="2"/>
  <g class="upper"><rect x="31" y="26" width="12" height="36" rx="3" fill="#59636f" stroke="#232a32" stroke-width="2"/>
    <g class="fore"><rect x="34" y="16" width="30" height="9" rx="3" fill="#6d7884" stroke="#232a32" stroke-width="2"/>
      <path d="M62 14v13M66 16v9" stroke="#ffb300" stroke-width="3" stroke-linecap="round"/></g>
    <circle cx="37" cy="26" r="5" fill="#ffb300" stroke="#232a32" stroke-width="2"/></g></svg>`;

// It crosses left to right, so the mast and forks are on the right: a forklift
// drives forks-first when it's running empty.
const FAC_FORK = `<svg viewBox="0 0 118 64">
  <rect x="54" y="12" width="6" height="40" fill="#4a535e"/>
  <path d="M60 46h26v5H60z" fill="#c9a227"/><path d="M60 30h20v5H60z" fill="#c9a227"/>
  <rect x="16" y="18" width="40" height="30" rx="4" fill="#e0a500" stroke="#7a5a05" stroke-width="2"/>
  <rect x="22" y="4" width="28" height="16" rx="3" fill="#2f3944" stroke="#1b2129" stroke-width="2"/>
  <circle class="wheel" cx="44" cy="52" r="9" fill="#2b333d" stroke="#171c23" stroke-width="3"/>
  <circle class="wheel" cx="20" cy="52" r="9" fill="#2b333d" stroke="#171c23" stroke-width="3"/></svg>`;

// The shop floor itself: a gantry of lamps up in the roof, machines standing
// on the floor at the bottom, and a welder throwing sparks in the corner. The
// machines sit low so they read as a room the dashboard is standing in, rather
// than clutter behind the middle of the page.
const FAC_SCENE = (night) => `<svg viewBox="0 0 1200 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
  <rect x="0" y="96" width="1200" height="12" fill="#3a434e"/>
  <path d="M0 96h1200M0 108h1200" stroke="#2a323b" stroke-width="2"/>
  ${[150, 450, 750, 1050].map(x => `<g class="fac-lamp"><path d="M${x} 108v22" stroke="#4a545f" stroke-width="3"/>
    <path d="M${x - 24} 154a24 24 0 0 1 48 0z" fill="#59636f" stroke="#2a323b" stroke-width="2"/>
    <ellipse cx="${x}" cy="156" rx="16" ry="5" fill="${night ? "#ffd98a" : "#fff3cf"}"/>
    <path d="M${x - 52} 156L${x - 96} 470L${x + 96} 470L${x + 52} 156z" fill="${night ? "#ffd98a" : "#fff3cf"}" opacity="${night ? ".035" : ".05"}"/></g>`).join("")}
  <g opacity="${night ? ".5" : ".75"}">
    <!-- the floor, and the machines standing on it -->
    <rect x="0" y="700" width="1200" height="200" fill="#20262d"/>
    <path d="M0 700h1200" stroke="#39424d" stroke-width="4"/>
    <rect x="40" y="520" width="240" height="180" rx="6" fill="#39424d" stroke="#232a32" stroke-width="3"/>
    <circle class="fac-wheelbig" cx="100" cy="580" r="30" fill="none" stroke="#6d7884" stroke-width="7" stroke-dasharray="11 10"/>
    <circle class="fac-wheelbig slow" cx="205" cy="630" r="38" fill="none" stroke="#6d7884" stroke-width="7" stroke-dasharray="13 12"/>
    <rect x="50" y="688" width="220" height="12" fill="#2b333d"/>
    <rect x="880" y="540" width="270" height="160" rx="6" fill="#39424d" stroke="#232a32" stroke-width="3"/>
    <circle class="fac-wheelbig" cx="940" cy="596" r="24" fill="none" stroke="#6d7884" stroke-width="6" stroke-dasharray="9 8"/>
    <rect x="990" y="566" width="130" height="22" rx="3" fill="#ffb300" opacity=".45"/>
    <rect x="990" y="602" width="130" height="22" rx="3" fill="#2b333d"/>
    <rect x="990" y="638" width="130" height="22" rx="3" fill="#2b333d"/>
    <!-- pipes down the back wall -->
    <path d="M330 110v560M362 110v560" stroke="#46505b" stroke-width="11"/>
    <path d="M330 300h300v16H330z" fill="#46505b"/>
    <!-- a welder at work in the corner -->
    <rect x="600" y="640" width="150" height="60" rx="4" fill="#39424d" stroke="#232a32" stroke-width="3"/>
    <g class="fac-spark"><path d="M676 620l9 16-18 3z" fill="#ffd24a"/><circle cx="683" cy="630" r="5" fill="#fff0b3"/></g>
    <!-- pallets stacked by the wall -->
    <rect x="770" y="656" width="70" height="44" fill="#6d4d24" opacity=".8"/>
    <rect x="770" y="612" width="70" height="40" fill="#7d5a2c" opacity=".8"/>
  </g></svg>`;

// Factory: the dashboard as a shop floor. A belt runs along the bottom with a
// crate for each device that turns up, a press stamps them when a scan lands,
// and the andon board in the header says whether the line is running. The
// night shift is the same floor with the lights down.
// A new device nobody has marked known is an intruder: a part that isn't on
// the manifest. It comes along the belt and the line emergency-stops - belt,
// crates and arm frozen, the andon red, a beacon turning - and it's taken off
// into the reject bay at the end of the line, tagged, until the device is
// marked known. Then it passes inspection and rides the belt with its name.
function buildFactory(root, switched, night) {
  const calm = calmMotion();
  let hum = null;
  const humOn = on => { if (on && !hum && !document.hidden) hum = beltHum(night); if (!on && hum) { hum.stop(); hum = null; } };
  const soundOn = themeSoundButton("bamf-factory-sound", "Factory sounds on: click to mute",
    "Factory sounds off: click for the belt, the press and the alarm", on => { humOn(on); if (on) crateClank(.6); });
  if (soundOn()) {
    const wake = () => humOn(soundOn());
    document.addEventListener("pointerdown", wake, { once: true });
    festiveStops.push(() => document.removeEventListener("pointerdown", wake));
  }
  const vis = () => humOn(soundOn() && !document.hidden);
  document.addEventListener("visibilitychange", vis);
  festiveStops.push(() => { document.removeEventListener("visibilitychange", vis); humOn(false); });

  // The floor, behind the page.
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg fac-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = FAC_SCENE(night);
  document.body.prepend(bg);

  // The andon board, beside the theme button: green running, amber unknown,
  // red when something watched is down.
  const andon = document.createElement("div");
  andon.className = "fac-andon";
  andon.innerHTML = `<i class="good"></i><i class="warn"></i><i class="bad"></i><span>LINE</span>`;
  $("themeToggle")?.before(andon);
  festiveStops.push(() => andon.remove());
  const setAndon = () => {
    const live = hosts.filter(h => !h.ignored && !h.forgotten && !h.remote);
    const down = live.filter(h => h.watched && !h.online).length;
    const unknown = live.filter(h => h.online && !h.known).length;
    andon.querySelector(".good").classList.toggle("on", !down);
    andon.querySelector(".warn").classList.toggle("on", unknown > 0 && !down);
    andon.querySelector(".bad").classList.toggle("on", down > 0);
    andon.title = down ? `${down} watched device${down === 1 ? "" : "s"} down: the line has stopped`
      : unknown ? `Running, with ${unknown} unknown device${unknown === 1 ? "" : "s"} on the floor` : "Everything running";
    bg.classList.toggle("fac-stopped", down > 0);
  };

  // The line, in front of the page so it's never behind the table.
  const line = document.createElement("div");
  line.className = "fac-line";
  line.innerHTML = `<div class="fac-belt"></div><div class="fac-legs"></div>` +
    `<div class="fac-arm">${FAC_ARM}</div>` +
    `<div class="fac-press"><span class="frame"></span><div class="ram"></div></div>`;
  root.appendChild(line);

  const stencil = name => {
    const t = (name || "").trim();
    if (!t) return "";
    // An address is stencilled as its last part; a name is cut to what fits.
    const m = /^\d{1,3}\.\d{1,3}\.\d{1,3}\.(\d{1,3})$/.exec(t);
    return m ? "." + m[1] : t.length > 8 ? t.slice(0, 8) : t;
  };
  const crate = (label, reject) => {
    if (document.hidden) return;
    const el = document.createElement("div");
    el.className = "fac-crate" + (reject ? " reject" : "");
    el.style.animationDuration = rnd(13, 19).toFixed(1) + "s";
    el.title = (label || "") + (reject ? " - off the air" : "");
    el.innerHTML = FAC_CRATE + `<span class="lbl">${esc(stencil(label))}</span>`;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    line.appendChild(el);
    if (soundOn()) crateClank(reject ? .8 : .5);
  };
  const press = () => {
    const el = line.querySelector(".fac-press");
    el.classList.add("hit");
    festiveTimers.push(setTimeout(() => el.classList.remove("hit"), 260));
    if (soundOn()) pressThump(night ? .7 : 1);
  };

  // ---- intruders: parts in the reject bay ----
  const watchIn = intruderWatch();
  const parts = new Map();              // id -> { h, el, tag, alarm, gone }
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const bay = document.createElement("div");
  bay.className = "fac-bay";
  bay.innerHTML = `<span>REJECT BAY</span>`;
  bay.hidden = true;
  line.appendChild(bay);
  const beacon = document.createElement("div");
  beacon.className = "fac-beacon";
  line.appendChild(beacon);
  // One stood down keeps its place until it has gone, so none lands on it.
  const inBay = () => [...parts.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makePart = h => {
    const el = document.createElement("div");
    el.className = "fac-part";
    el.innerHTML = FAC_PART;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    return { h, el, tag, alarm: 0, gone: false };
  };
  // The bay holds them side by side, the newest nearest the line; the tags
  // stand over them, stacked, and the page gets room to scroll clear.
  const settle = () => {
    const list = inBay();
    bay.hidden = !list.length;
    room.style.height = list.some(q => !q.gone) ? "150px" : "0";
    list.forEach((p, k) => {
      if (p.moving) return;
      if (p.el.parentNode !== bay) { p.el.style.cssText = ""; bay.appendChild(p.el); }
      p.el.hidden = k > 1;
      p.el.style.left = (8 + k * 60) + "px";
      p.tag.style.bottom = `calc(100% + ${34 + k * 52}px)`;
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!parts.has(h.id)) parts.set(h.id, makePart(h));
    for (const h of held) { const p = parts.get(h.id); if (p && !p.gone) { p.h = h; if (!p.alarm || Date.now() - p.alarm > 9000) intruderTagEl(h, "held", p.tag); } }
    for (const h of cleared) {
      const p = parts.get(h.id); if (!p || p.gone) continue;
      p.gone = true;
      const done = () => { p.el.remove(); parts.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Passed: out of the bay and onto the belt, with its name on it.
      intruderTagEl(p.h, "cleared", p.tag);
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) crate(nameOrIp(h), false); }, 2600));
    }
    settle();
  }
  // The alarm: along the belt it comes, and everything stops.
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let p = parts.get(h.id);
    if (!p) { p = makePart(h); parts.set(h.id, p); }
    if (calm || document.hidden) { settle(); return; }
    p.alarm = Date.now(); p.moving = true;
    intruderTagEl(h, "alarm", p.tag);
    // Onto the belt from the left, riding to the middle of the line.
    p.el.style.cssText = "position:absolute;bottom:34px;left:0;transform:translateX(-70px);transition:none";
    line.appendChild(p.el);
    void p.el.offsetWidth;
    p.el.style.transition = "transform 3s linear";
    p.el.style.transform = `translateX(${Math.round(innerWidth * .42)}px)`;
    settle();
    festiveTimers.push(setTimeout(() => {
      // Emergency stop: the line freezes, the andon goes red, the beacon turns.
      line.classList.add("fac-estop"); andon.classList.add("estop"); bg.classList.add("fac-alarm");
      if (soundOn()) { andonBuzz(); festiveTimers.push(setTimeout(andonBuzz, 800)); festiveTimers.push(setTimeout(andonBuzz, 1600)); }
    }, 3000));
    // Taken off the line into the reject bay.
    festiveTimers.push(setTimeout(() => {
      const x = innerWidth - 24 - 150;
      p.el.style.transition = "transform 2.4s ease-in-out";
      p.el.style.transform = `translateX(${x}px)`;
    }, 6500));
    festiveTimers.push(setTimeout(() => { p.moving = false; settle(); }, 9000));
    festiveTimers.push(setTimeout(() => {
      line.classList.remove("fac-estop"); andon.classList.remove("estop"); bg.classList.remove("fac-alarm");
      if (!p.gone) intruderTagEl(p.h, "held", p.tag);
    }, 9500));
  };
  festiveStops.push(() => andon.classList.remove("estop"));
  syncIntruders();

  setAndon();
  if (calm) { festiveHooks.rendered = () => { setAndon(); syncIntruders(); }; return; } // Reduced motion: the floor is there, nothing runs.

  // A crate every so often, named after something that's online.
  const named = () => {
    const live = hosts.filter(h => h.online && !h.ignored && !h.forgotten && !h.remote);
    return live.length ? nameOrIp(live[Math.floor(Math.random() * live.length)]) : "";
  };
  festiveTimers.push(setInterval(() => { if (!document.hidden && line.querySelectorAll(".fac-crate").length < 7) crate(named(), false); }, 5200));
  crate(named(), false);

  // A forklift crosses now and then.
  const fork = () => {
    if (document.hidden || line.querySelector(".fac-fork")) return;
    const el = document.createElement("div");
    el.className = "fac-fork";
    el.style.animationDuration = rnd(16, 26).toFixed(0) + "s";
    el.innerHTML = FAC_FORK;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    line.appendChild(el);
  };
  const forkAgain = () => festiveTimers.push(setTimeout(() => { fork(); forkAgain(); }, rnd(40e3, 90e3)));
  festiveTimers.push(setTimeout(() => { fork(); forkAgain(); }, rnd(9e3, 18e3)));

  // A finished scan is a batch coming off the line: the belt speeds up, the
  // press comes down, and a few crates go out.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    line.classList.add("fac-fast");
    festiveTimers.push(setTimeout(() => line.classList.remove("fac-fast"), 4000));
    press();
    for (let i = 0; i < 3; i++) festiveTimers.push(setTimeout(() => crate(named(), false), 300 + i * 700));
  };
  // Something goes off the air: the beacon, and a rejected crate with its name.
  festiveHooks.netChange = (off, back) => {
    setAndon();
    if (document.hidden) return;
    for (const id of off.slice(0, 3)) {
      const h = hosts.find(x => x.id === id);
      crate(h ? nameOrIp(h) : "", true);
    }
    if (off.length && soundOn()) andonBuzz();
    if (back.length) press();
  };
  festiveHooks.rendered = () => { setAndon(); syncIntruders(); };
}

BAMF.registerTheme("factory", ctx => buildFactory(ctx.root, ctx.switched, false));
BAMF.registerTheme("factorynight", ctx => buildFactory(ctx.root, ctx.switched, true));
})();
