// Power Plant: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- The plant's noises ----
// The turbine hall: a deep hum with the whine of the set over it.
function turbineHum() {
  const ctx = waterCtx(); if (!ctx) return null;
  const src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, 4, true); src.loop = true;
  f.type = "lowpass"; f.frequency.value = 200;
  g.gain.setValueAtTime(0, ctx.currentTime); g.gain.linearRampToValueAtTime(.05, ctx.currentTime + 2);
  src.connect(f).connect(g).connect(ctx.destination); src.start();
  const o = ctx.createOscillator(), og = ctx.createGain(), o2 = ctx.createOscillator(), og2 = ctx.createGain();
  o.type = "sine"; o.frequency.value = 60; og.gain.value = .018;
  o2.type = "triangle"; o2.frequency.value = 420; og2.gain.value = .006;
  o.connect(og).connect(ctx.destination); o2.connect(og2).connect(ctx.destination);
  o.start(); o2.start();
  return {
    surge() { try { og2.gain.setTargetAtTime(.016, ctx.currentTime, .3); og2.gain.setTargetAtTime(.006, ctx.currentTime + 2.5, .8); } catch { } },
    stop() { try { for (const x of [g, og, og2]) x.gain.setTargetAtTime(0, ctx.currentTime, .3); src.stop(ctx.currentTime + 1.4); o.stop(ctx.currentTime + 1.4); o2.stop(ctx.currentTime + 1.4); } catch { } },
  };
}

// A breaker closing: a heavy clunk with a spring behind it.
function breakerClunk(open) {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01;
  const o = ctx.createOscillator(), g = ctx.createGain();
  o.type = "square"; o.frequency.setValueAtTime(open ? 160 : 120, t); o.frequency.exponentialRampToValueAtTime(open ? 60 : 45, t + .1);
  g.gain.setValueAtTime(.16, t); g.gain.exponentialRampToValueAtTime(.001, t + .18);
  o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .2);
  const n = ctx.createBufferSource(), nf = ctx.createBiquadFilter(), ng = ctx.createGain();
  n.buffer = noiseBuffer(ctx, .2, false);
  nf.type = "bandpass"; nf.frequency.value = 1800; nf.Q.value = 1.2;
  ng.gain.setValueAtTime(.07, t); ng.gain.exponentialRampToValueAtTime(.001, t + .14);
  n.connect(nf).connect(ng).connect(ctx.destination); n.start(t); n.stop(t + .2);
}

// The trip alarm: the two-tone klaxon on the annunciator panel.
function tripKlaxon() {
  const ctx = waterCtx(); if (!ctx) return;
  let t = ctx.currentTime + .02;
  for (let i = 0; i < 2; i++) {
    for (const f0 of [520, 390]) {
      const o = ctx.createOscillator(), g = ctx.createGain();
      o.type = "sawtooth"; o.frequency.value = f0;
      g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.055, t + .03); g.gain.setValueAtTime(.055, t + .22); g.gain.linearRampToValueAtTime(0, t + .27);
      o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .3);
      t += .28;
    }
  }
}

// The hall: two cooling towers steaming outside the window, the turbine set
// turning in the middle, the boiler glowing at the back, and the line leaving
// through the pylons with current crawling along it.
// The station itself, drawn rather than loaded. What says "power station" is
// the shape of the plant, so the pieces are the recognisable ones: hyperbolic
// cooling towers steaming, the stack with its beacon, lattice pylons carrying
// the line away, and the turbine hall - gantry crane overhead, the turbine set
// and its generator on the floor, the step-up transformer at the end of it.
//
// It's laid out in bands: the window and the yard beyond it along the top, the
// hall's air left dark in the middle where the page is read, and the machine
// floor along the bottom.
const PP_TOWER = (x, base, h, w) => {
  // A cooling tower's waist is what makes it read as one: widest at the feet,
  // pinched about three quarters up, flaring a little to the rim. Sampled off a
  // hyperbola rather than drawn by eye.
  const rw = w * .55, yw = .72, c = .52;
  const r = t => rw * Math.sqrt(1 + ((t - yw) / c) ** 2);
  const y = t => base - h * t;
  // Up the left side and back down the right, so the outline closes cleanly.
  const side = (sign, down) => Array.from({ length: 13 }, (_, k) => {
    const t = down ? 1 - k / 12 : k / 12;
    return `${(x + sign * r(t)).toFixed(1)} ${y(t).toFixed(1)}`;
  }).join("L");
  return `<g class="pp-tower">
    ${Array.from({ length: 3 }, (_, k) => `<ellipse class="pp-steam" cx="${(x + (k - 1) * 18).toFixed(0)}" cy="${(y(1) - 4).toFixed(0)}" rx="${15 + k * 5}" ry="${9 + k * 3}" fill="#9fb4c2" opacity=".13" style="animation-delay:-${(k * 2.7 + x % 5).toFixed(1)}s"/>`).join("")}
    <path d="M${side(-1, false)}L${side(1, true)}z" fill="#1c242a" stroke="#333e46" stroke-width="3"/>
    ${[.2, .42, .64, .86].map(t => `<path d="M${(x - r(t)).toFixed(1)} ${y(t).toFixed(1)}h${(r(t) * 2).toFixed(1)}" stroke="#2a343b" stroke-width="2" opacity=".65"/>`).join("")}
    <ellipse cx="${x}" cy="${y(1).toFixed(1)}" rx="${r(1).toFixed(1)}" ry="${(r(1) * .17).toFixed(1)}" fill="#141a1f" stroke="#333e46" stroke-width="3"/>
    <ellipse cx="${x}" cy="${y(0).toFixed(1)}" rx="${r(0).toFixed(1)}" ry="${(r(0) * .1).toFixed(1)}" fill="#161d22" opacity=".9"/>
  </g>`;
};

// A lattice pylon: two legs, cross-bracing, and the arms the line hangs from.
const PP_PYLON = (x, base, h) => {
  const legs = [], braces = [];
  const wAt = t => 34 - 22 * t;                     // wide at the feet, narrow at the top
  for (let k = 0; k <= 6; k++) {
    const t = k / 6, y = base - h * t, w = wAt(t);
    legs.push(`${k ? "L" : "M"}${(x - w).toFixed(1)} ${y.toFixed(1)}`);
    if (k < 6) {
      const t2 = (k + 1) / 6, y2 = base - h * t2, w2 = wAt(t2);
      braces.push(`M${(x - w).toFixed(1)} ${y.toFixed(1)}L${(x + w2).toFixed(1)} ${y2.toFixed(1)}M${(x + w).toFixed(1)} ${y.toFixed(1)}L${(x - w2).toFixed(1)} ${y2.toFixed(1)}`);
    }
  }
  const mirror = [];
  for (let k = 0; k <= 6; k++) { const t = k / 6; mirror.push(`${k ? "L" : "M"}${(x + wAt(t)).toFixed(1)} ${(base - h * t).toFixed(1)}`); }
  return `<g fill="none" stroke="#39434b" stroke-width="3">
    <path d="${legs.join("")}"/><path d="${mirror.join("")}"/>
    <path d="${braces.join("")}" stroke-width="1.6" opacity=".8"/>
    <path d="M${x - 40} ${base - h * .74}h80M${x - 30} ${base - h * .92}h60" stroke-width="4"/>
    ${[-40, -14, 14, 40].map(dx => `<path d="M${x + dx} ${base - h * .74}v9" stroke="#4a545b" stroke-width="3"/>`).join("")}
  </g>`;
};

const PP_SCENE = () => `<svg viewBox="0 0 1400 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
  <!-- Out of the control room window: the yard at dusk. -->
  <rect x="0" y="0" width="1400" height="212" fill="#0c1115"/>
  <rect x="0" y="150" width="1400" height="62" fill="#111820"/>
  <!-- the boiler house and its stack, with the beacon on top -->
  <rect x="520" y="120" width="160" height="92" fill="#1a2126" stroke="#2c353c" stroke-width="3"/>
  <path d="M700 212V40h28v172z" fill="#1c242a" stroke="#333e46" stroke-width="3"/>
  <path d="M700 72h28M700 104h28M700 136h28" stroke="#2a343b" stroke-width="2"/>
  <circle class="pp-glow" cx="714" cy="36" r="6" fill="#ff4b3e"/>
  ${Array.from({ length: 3 }, (_, k) => `<ellipse class="pp-steam" cx="${714 + (k - 1) * 14}" cy="30" rx="${16 + k * 6}" ry="${9 + k * 3}" fill="#8fa3b0" opacity=".16" style="animation-delay:-${(k * 3.1).toFixed(1)}s"/>`).join("")}
  ${PP_TOWER(852, 212, 146, 80)}
  ${PP_TOWER(1024, 212, 120, 64)}
  <!-- the switchyard, and the line leaving the site -->
  ${PP_PYLON(1190, 212, 172)}
  ${PP_PYLON(1372, 212, 172)}
  <path d="M700 96C820 120 1060 128 1190 96C1290 72 1300 72 1372 92" fill="none" stroke="#242b30" stroke-width="4"/>
  <path class="pp-line" d="M700 96C820 120 1060 128 1190 96C1290 72 1300 72 1372 92"/>
  <path d="M700 132C820 156 1060 164 1190 132C1290 108 1300 108 1372 128" fill="none" stroke="#242b30" stroke-width="4"/>
  <path class="pp-line" style="animation-delay:-2s" d="M700 132C820 156 1060 164 1190 132C1290 108 1300 108 1372 128"/>
  <!-- the window frame, and the hall inside it -->
  <g stroke="#2c353c" stroke-width="6" fill="none"><path d="M0 212h1400M220 0v212M520 0v212M760 0v212M1060 0v212"/></g>
  <rect x="0" y="212" width="1400" height="688" fill="#12171b"/>
  <!-- the hall's gantry crane, up in the roof where there's open air -->
  <path d="M0 262h1400M0 300h1400" stroke="#1f272d" stroke-width="7"/>
  <g class="pp-crane">
    <rect x="470" y="252" width="300" height="20" rx="4" fill="#232c33" stroke="#39434b" stroke-width="3"/>
    <rect x="596" y="272" width="52" height="30" rx="3" fill="#2b353d" stroke="#39434b" stroke-width="3"/>
    <path d="M622 302v46" stroke="#4a545b" stroke-width="3"/>
    <path d="M610 348h24v14h-24z" fill="#3b464f" stroke="#4a545b" stroke-width="2"/>
  </g>
  <!-- the machine floor, low enough to stay out of the table's way -->
  <g class="pp-hall">
    <path d="M0 612h1400" stroke="#232b31" stroke-width="3"/>
    <g stroke="#1b2228" stroke-width="2">${Array.from({ length: 7 }, (_, i) => `<path d="M${-120 + i * 250} 900L${420 + i * 100} 616"/>`).join("")}</g>
    <!-- the boiler front, glowing through its inspection door -->
    <rect x="44" y="600" width="188" height="300" rx="6" fill="#1a2126" stroke="#2c353c" stroke-width="4"/>
    <path d="M44 660h188M44 720h188" stroke="#232b31" stroke-width="3"/>
    <rect x="76" y="742" width="124" height="86" rx="4" fill="#0e1215"/>
    <ellipse class="pp-glow" cx="138" cy="785" rx="50" ry="30" fill="#ff8a3d"/>
    <ellipse class="pp-glow" cx="138" cy="785" rx="26" ry="15" fill="#ffd36b" style="animation-delay:-2s"/>
    <!-- feedwater pumps in front of it -->
    ${[268, 330].map((x, i) => `<g><rect x="${x - 26}" y="812" width="52" height="46" rx="4" fill="#1a2126" stroke="#2c353c" stroke-width="3"/>
      <circle class="pp-spin slow" cx="${x}" cy="835" r="14" fill="none" stroke="#4a545b" stroke-width="5" stroke-dasharray="8 7" style="animation-delay:-${i}s"/></g>`).join("")}
    <!-- the turbine set on its plinth: three casings getting bigger down the shaft -->
    <rect x="360" y="856" width="640" height="24" fill="#1a2126" stroke="#2c353c" stroke-width="3"/>
    <path d="M660 700v156M860 700v156" stroke="#232b31" stroke-width="2"/>
    <rect x="372" y="752" width="128" height="104" rx="40" fill="#212930" stroke="#333e46" stroke-width="4"/>
    <rect x="506" y="724" width="152" height="132" rx="52" fill="#232c34" stroke="#333e46" stroke-width="4"/>
    <rect x="664" y="700" width="186" height="156" rx="62" fill="#252e37" stroke="#333e46" stroke-width="4"/>
    ${[402, 440, 470, 540, 584, 628, 700, 752, 804].map(x => `<path d="M${x} ${x < 500 ? 754 : x < 660 ? 726 : 702}v${x < 500 ? 100 : x < 660 ? 128 : 152}" stroke="#2c353c" stroke-width="3"/>`).join("")}
    <!-- the coupling, turning, and the generator with its exciter on the end -->
    <circle class="pp-spin" cx="876" cy="778" r="30" fill="#1a2126" stroke="#39434b" stroke-width="4"/>
    <g class="pp-spin">${Array.from({ length: 6 }, (_, i) => `<path d="M876 778L${(876 + Math.cos(i / 6 * 6.283) * 26).toFixed(1)} ${(778 + Math.sin(i / 6 * 6.283) * 26).toFixed(1)}" stroke="#4a545b" stroke-width="6" stroke-linecap="round"/>`).join("")}</g>
    <rect x="906" y="726" width="150" height="104" rx="26" fill="#232c34" stroke="#333e46" stroke-width="4"/>
    <rect x="1060" y="756" width="46" height="44" rx="12" fill="#1f272e" stroke="#333e46" stroke-width="3"/>
    <rect x="930" y="748" width="102" height="18" rx="3" fill="#48c5ff" opacity=".18"/>
    <!-- the step-up transformer at the end of it, bushings and radiators -->
    <rect x="1168" y="736" width="150" height="124" rx="5" fill="#1a2126" stroke="#2c353c" stroke-width="4"/>
    ${[1180, 1300].map(x => `<g stroke="#2c353c" stroke-width="3">${Array.from({ length: 5 }, (_, k) => `<path d="M${x - 10} ${750 + k * 24}h20"/>`).join("")}</g>`).join("")}
    ${[1200, 1243, 1286].map(x => `<g><path d="M${x} 736v-44" stroke="#39434b" stroke-width="5"/>
      ${[0, 1, 2].map(k => `<ellipse cx="${x}" cy="${702 + k * 11}" rx="11" ry="4" fill="#2c353c"/>`).join("")}</g>`).join("")}
    <path d="M1200 692q22-40 86-40h44" fill="none" stroke="#242b30" stroke-width="4"/>
  </g>
</svg>`;

// Power Plant: the dashboard as a generating station. Every device is a feeder
// on the busbar with its own breaker, the frequency in the header sags as
// devices drop, and a trip lights the annunciator and stops the set.
function buildPowerPlant(root) {
  const calm = calmMotion();
  let hum = null;
  const humOn = on => { if (on && !hum && !document.hidden) hum = turbineHum(); if (!on && hum) { hum.stop(); hum = null; } };
  const soundOn = themeSoundButton("bamf-plant-sound", "Plant sounds on: click to mute",
    "Plant sounds off: click for the turbine, the breakers and the trip alarm", on => { humOn(on); if (on) breakerClunk(false); });
  if (soundOn()) {
    const wake = () => humOn(soundOn());
    document.addEventListener("pointerdown", wake, { once: true });
    festiveStops.push(() => document.removeEventListener("pointerdown", wake));
  }
  const vis = () => humOn(soundOn() && !document.hidden);
  document.addEventListener("visibilitychange", vis);
  festiveStops.push(() => { document.removeEventListener("visibilitychange", vis); humOn(false); });

  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg pp-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = PP_SCENE();
  document.body.prepend(bg);

  // The frequency meter in the header.
  const freq = document.createElement("div");
  freq.className = "pp-freq";
  freq.innerHTML = `<i></i><b>60.00</b> Hz`;
  $("themeToggle")?.before(freq);
  festiveStops.push(() => freq.remove());

  // The control desk: two gauges, the busbar with a breaker per device, and
  // the annunciator panel.
  const gauge = (id, cap) => `<div class="pp-gauge" id="${id}"><svg viewBox="0 0 78 78">
      <circle cx="39" cy="44" r="32" fill="#161a1d" stroke="#4a545b" stroke-width="3"/>
      ${Array.from({ length: 11 }, (_, i) => {
        const a = Math.PI * (1 + i / 10);
        return `<path d="M${39 + Math.cos(a) * 27} ${44 + Math.sin(a) * 27}L${39 + Math.cos(a) * 22} ${44 + Math.sin(a) * 22}" stroke="${i > 7 ? "#ff4b3e" : "#5c676f"}" stroke-width="2"/>`;
      }).join("")}
      <path class="needle" d="M37.6 44L39 16l1.4 28z" fill="#ffd36b"/>
      <circle cx="39" cy="44" r="4" fill="#4a545b"/>
      <text class="cap" x="39" y="68" text-anchor="middle">${cap}</text>
      <text class="val" x="39" y="38" text-anchor="middle"></text>
    </svg></div>`;
  const desk = document.createElement("div");
  desk.className = "pp-desk";
  desk.innerHTML = gauge("ppLoad", "LOAD %") +
    `<div class="pp-bus"><div class="pp-busbar"></div><div class="pp-breakers"></div><div class="pp-busbar"></div></div>` +
    gauge("ppOut", "OUTPUT") +
    `<div class="pp-ann">
      <span data-ann="feeder">FEEDER TRIP</span><span data-ann="unknown">UNKNOWN UNIT</span>
      <span data-ann="scan">POLL RUNNING</span><span data-ann="all">ALL FEEDERS UP</span>
    </div>`;
  root.appendChild(desk);
  const breakers = desk.querySelector(".pp-breakers");

  const live = () => hosts.filter(h => !h.ignored && !h.forgotten && !h.remote)
    .sort((a, b) => nameOrIp(a).localeCompare(nameOrIp(b)));
  const setNeedle = (id, frac, text) => {
    const g = desk.querySelector("#" + id);
    g.querySelector(".needle").style.transform = `rotate(${(-90 + Math.max(0, Math.min(1, frac)) * 180).toFixed(1)}deg)`;
    g.querySelector(".val").textContent = text;
  };
  const ann = (key, cls) => {
    const el = desk.querySelector(`[data-ann="${key}"]`);
    if (el) el.className = cls || "";
  };
  const paint = () => {
    const list = live();
    const up = list.filter(h => h.online).length;
    const unknown = list.filter(h => h.online && !h.known).length;
    const down = list.filter(h => !h.online && h.watched).length;
    const frac = list.length ? up / list.length : 1;
    // The frequency sags with load lost, the way a grid does.
    const hz = (60 - (1 - frac) * 1.4).toFixed(2);
    freq.querySelector("b").textContent = hz;
    freq.className = "pp-freq" + (down ? " trip" : frac < .9 ? " low" : "");
    freq.title = `${up} of ${list.length} feeders carrying${down ? `, ${down} tripped` : ""}`;
    setNeedle("ppLoad", frac, Math.round(frac * 100) + "%");
    setNeedle("ppOut", Math.min(1, up / Math.max(8, list.length)), up + "");
    bg.classList.toggle("pp-tripped", down > 0);
    desk.classList.toggle("pp-tripped", down > 0);
    // A breaker per device, in the order the table lists them.
    const want = list.slice(0, 40);
    if (breakers.children.length !== want.length)
      breakers.innerHTML = want.map(() => `<span class="pp-brk"><i></i></span>`).join("");
    want.forEach((h, i) => {
      const el = breakers.children[i];
      if (!el) return;
      el.className = "pp-brk" + (!h.online ? " open" : h.known ? "" : " un");
      el.title = `${nameOrIp(h)} · ${h.ip} · ${h.online ? "carrying" : "tripped"}`;
      el.dataset.id = h.id;
    });
    ann("feeder", down ? "bad" : "");
    ann("unknown", unknown ? "lit" : "");
    ann("all", down || unknown ? "" : "lit");
  };
  // Repainted whenever the table is, which is how the desk fills in after the
  // first scan. Set before reduced motion takes the early way out, or the
  // busbar would stay empty.
  festiveHooks.rendered = () => paint();
  paint();
  if (calm) return; // Reduced motion: the plant is lit, the set is still.

  // A scan is a load surge: the set runs up, the line runs fast, the
  // annunciator says the poll is on.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    ann("scan", "lit");
    for (const el of bg.querySelectorAll(".pp-spin:not(.slow)")) el.classList.add("fast");
    if (soundOn()) hum?.surge();
    festiveTimers.push(setTimeout(() => {
      ann("scan", "");
      for (const el of bg.querySelectorAll(".pp-spin")) el.classList.remove("fast");
    }, 3500));
  };
  // A new device is a feeder being closed onto the bus.
  festiveHooks.newDevice = () => {
    paint();
    if (!document.hidden && soundOn()) breakerClunk(false);
  };
  // Something drops: its breaker opens, the frequency sags, the klaxon sounds.
  festiveHooks.netChange = (off, back) => {
    paint();
    if (document.hidden) return;
    if (off.length && soundOn()) { breakerClunk(true); tripKlaxon(); }
    if (back.length && soundOn()) breakerClunk(false);
  };
}

BAMF.registerTheme("powerplant", ctx => buildPowerPlant(ctx.root, ctx.switched));
})();
