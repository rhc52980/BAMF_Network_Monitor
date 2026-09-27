// Waterworks: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// The pipework behind the page, laid out for the window: a riser up each
// side joined by a main along the floor, an upper main from riser to riser,
// a manifold on the right feeding two more mains, branches off the left
// riser to valve wheels, and open ends pouring into the reservoir. Valves of
// every kind sit along the runs (gate wheels, ball valves, a butterfly valve,
// a check valve, relief valves), gauges stand on stems, a pump runs on the
// floor main, and a few joints leak. Horizontal runs carry the class "h":
// high-pressure bursts come off those.
function wwPipework() {
  const W = innerWidth, H = innerHeight, L = 24, R = W - 24, F = H - 42, P = H - 24;
  const X = f => Math.round(W * f), Y = f => Math.round(H * f);
  const Ya = Y(.2), Yb = Y(.46), Yc = Y(.7), xm = X(.62), xn = X(.84);
  const hz = [], vt = [], ends = [], outs = [];
  vt.push(`M${L} 40V${F - 24}Q${L} ${F} ${L + 24} ${F}H${R - 24}Q${R} ${F} ${R} ${F - 24}V40`);
  hz.push(`M${L} ${Ya}H${R}`, `M${xm} ${Yb}H${R}`, `M${xm} ${Yc}H${R}`);
  vt.push(`M${xm} ${Ya}V${F}`, `M${xn} ${Ya}V${Yb}`);
  // Branches off the left riser, each turning down to a valve wheel.
  const branchYs = [Y(.33), Y(.56)];
  branchYs.forEach((y, i) => {
    const x1 = L + X(.05 + .03 * i);
    hz.push(`M${L} ${y}H${x1}`);
    vt.push(`M${x1} ${y}Q${x1 + 20} ${y} ${x1 + 20} ${y + 20}V${y + 70}`);
    ends.push([x1 + 20, y + 78]);
  });
  // Open ends pouring into the reservoir: one off the lower main, one off
  // each riser.
  const o1 = X(.76), o2y = Math.round(Yc + (F - Yc) * .4), o2 = R - 86, o3y = Y(.8), o3 = L + 64;
  vt.push(`M${o1} ${Yc}V${Yc + 34}`); outs.push([o1, Yc + 40]);
  hz.push(`M${R} ${o2y}H${o2 + 18}`); vt.push(`M${o2 + 18} ${o2y}Q${o2} ${o2y} ${o2} ${o2y + 18}V${o2y + 30}`); outs.push([o2, o2y + 36]);
  hz.push(`M${L} ${o3y}H${o3 - 18}`); vt.push(`M${o3 - 18} ${o3y}Q${o3} ${o3y} ${o3} ${o3y + 18}V${o3y + 30}`); outs.push([o3, o3y + 36]);

  const pipe = (d, h) => `<path class="rim${h ? " h" : ""}" d="${d}"/><path class="bore" d="${d}"/><path class="flow" d="${d}"/><path class="flow fast" d="${d}"/><path class="shine" d="${d}"/>`;
  const stream = ([x, y]) => `<path class="stream" d="M${x} ${y}V${P}"/><path class="stream2" d="M${x} ${y}V${P}"/>
    <g class="splash" transform="translate(${x} ${P})"><path d="M-6 0q-6 -10 -14 -8"/><path d="M6 0q6 -10 14 -8"/><path d="M-3 0q-2 -14 -6 -14"/><path d="M3 0q2 -14 6 -14"/></g>`;
  const tee = ([x, y]) => `<circle class="tee" cx="${x}" cy="${y}" r="12"/><circle class="tee-in" cx="${x}" cy="${y}" r="4.5"/>`;
  const at = (x, y, inner, cls = "") => `<g${cls ? ` class="${cls}"` : ""} transform="translate(${x} ${y})">${inner}</g>`;
  const WHEEL = `<g class="wheel"><circle r="14" fill="none" stroke="#e8483b" stroke-width="4"/>
    <path d="M0 -14V14M-14 0H14M-10 -10L10 10M-10 10L10 -10" stroke="#e8483b" stroke-width="2.2"/><circle r="4" fill="#b8352b"/></g>`;
  const gate = (x, y) => at(x, y, `<rect x="-11" y="-13" width="22" height="26" rx="3" fill="#5d6b79" stroke="#3d4854"/>
    <rect x="-2.5" y="-30" width="5" height="18" fill="#9aa8b6"/><g transform="translate(0 -36)">${WHEEL}</g>`);
  const ball = (x, y) => at(x, y, `<rect x="-13" y="-12" width="26" height="24" rx="6" fill="#b8894a" stroke="#6b4f22"/>
    <path d="M-13 -6h-4v12h4M13 -6h4v12h-4" fill="#9aa8b6"/><rect x="-3" y="-19" width="6" height="8" fill="#7d8b99"/>
    <g class="lever"><rect x="-4" y="-24" width="36" height="7" rx="3" fill="#e8483b"/></g>`, "ballv");
  const butterfly = (x, y) => at(x, y, `<rect x="-8" y="-17" width="16" height="34" rx="2" fill="#6f7d8b" stroke="#3d4854"/>
    <path d="M-12 -17v34M12 -17v34" stroke="#9aa8b6" stroke-width="3"/><rect x="-3" y="-30" width="6" height="14" fill="#7d8b99"/>
    <rect x="-16" y="-48" width="32" height="20" rx="3" fill="#f2c230" stroke="#8a6a10"/>
    <g transform="translate(0 -57)"><g class="wheel"><circle r="9" fill="none" stroke="#f2c230" stroke-width="3"/><path d="M0 -9V9M-9 0H9" stroke="#f2c230" stroke-width="2"/></g></g>`);
  const check = (x, y) => at(x, y, `<rect x="-17" y="-12" width="34" height="24" rx="8" fill="#b8894a" stroke="#6b4f22"/>
    <rect x="-6" y="-20" width="12" height="9" rx="2" fill="#9a7440"/>
    <path d="M-8 0H6M2 -5l6 5-6 5" fill="none" stroke="#3a2a14" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>`);
  const relief = (x, y) => at(x, y, `<rect x="-8" y="-14" width="16" height="8" fill="#7d8b99"/>
    <rect x="-6" y="-40" width="12" height="27" rx="2" fill="#5d6b79" stroke="#3d4854"/>
    <path d="M-5 -16l10-3-10-3 10-3-10-3 10-3-10-3" fill="none" stroke="#c9d3dd" stroke-width="1.4"/>
    <rect class="cap" x="-8" y="-48" width="16" height="9" rx="3" fill="#e8483b"/><path d="M6 -30h10v-6" fill="none" stroke="#9aa8b6" stroke-width="4"/>
    <g class="vent" transform="translate(16 -38)"><path d="M0 0q-4 -16 -14 -26"/><path d="M0 0q2 -18 -2 -32"/><path d="M0 0q8 -14 14 -24"/></g>`, "relief");
  const gauge = (x, y) => `<g class="gauge" transform="translate(${x} ${y})"><circle r="30" fill="#5d6b79"/><circle r="26" fill="#16202a" stroke="#2b343d" stroke-width="2"/>
    ${Array.from({ length: 9 }, (_, i) => { const a = (-120 + i * 30) * Math.PI / 180; return `<path d="M${(19 * Math.sin(a)).toFixed(1)} ${(-19 * Math.cos(a)).toFixed(1)}L${(23 * Math.sin(a)).toFixed(1)} ${(-23 * Math.cos(a)).toFixed(1)}" stroke="#9aa8b6" stroke-width="${i % 2 ? 1 : 2}"/>`; }).join("")}
    <path d="M${(22 * Math.sin(-2.1)).toFixed(1)} ${(-22 * Math.cos(-2.1)).toFixed(1)}A22 22 0 0 1 ${(22 * Math.sin(-1.2)).toFixed(1)} ${(-22 * Math.cos(-1.2)).toFixed(1)}" fill="none" stroke="#e8483b" stroke-width="3"/>
    <text y="13" text-anchor="middle" font-size="6" font-family="IBM Plex Mono, monospace" fill="#9aa8b6">PSI</text>
    <g class="needle"><g class="jitter"><path d="M-1.6 0L0 -22L1.6 0z" fill="#e8483b"/></g></g><circle r="3" fill="#9aa8b6"/></g>`;
  // A gauge on a stem standing up off a horizontal run.
  const gaugeOn = (x, y) => `<rect x="${x - 3}" y="${y - 34}" width="6" height="26" fill="#7d8b99"/>${gauge(x, y - 62)}`;
  const pump = (x, y) => at(x, y, `<rect x="-40" y="-8" width="118" height="10" rx="2" fill="#3d4854"/>
    <circle cx="0" cy="-28" r="22" fill="#2f6fa8" stroke="#1d4a73" stroke-width="3"/><circle cx="0" cy="-28" r="7" fill="#9aa8b6"/>
    <rect x="20" y="-46" width="42" height="34" rx="5" fill="#2a5d8e" stroke="#1d4a73" stroke-width="2"/>
    <path d="M27 -42v26M34 -42v26M41 -42v26M48 -42v26M55 -42v26" stroke="#1d4a73" stroke-width="2"/>
    <g transform="translate(68 -29)"><circle r="9" fill="#25303b" stroke="#9aa8b6" stroke-width="2"/><g class="fan"><path d="M0 -7V7M-7 0H7" stroke="#9aa8b6" stroke-width="2"/></g></g>
    <circle class="lamp" cx="41" cy="-52" r="3"/>`);
  const leak = (x, y, d) => at(x, y, `<circle class="drop" r="2.8" style="animation-delay:${d}s"/><ellipse class="splat" cy="64" rx="7" ry="2" style="animation-delay:${d}s"/>`);
  const mist = (x, y) => at(x, y, `<g class="mist"><path d="M0 0q-6 -12 -16 -16"/><path d="M0 0q0 -14 -2 -22"/><path d="M0 0q6 -12 14 -18"/></g>`);

  const tees = [[L, Ya], [R, Ya], [xm, Ya], [xm, Yb], [xm, Yc], [xm, F], [R, Yb], [R, Yc], [xn, Ya], [xn, Yb], [o1, Yc], [R, o2y], [L, o3y], ...branchYs.map(y => [L, y])];
  const narrow = W < 760;
  const valves = [ball(X(.28), Ya), gate(X(.52), Ya), relief(X(.57), Ya), butterfly(X(.76), Ya), relief(X(.92), Ya),
    check(X(.7), Yb), relief(X(.79), Yb), gate(X(.68), Yc), ball(X(.87), Yc)].join("");
  const gauges = gauge(L + 46, Y(.38)) + gauge(R - 46, Y(.58)) + gaugeOn(X(.44), Ya) + (narrow ? "" : gaugeOn(X(.94), Yb));
  const leaks = [leak(X(.36), Ya + 9, 0), leak(X(.74), Yb + 9, 1.3), leak(X(.95), Yc + 9, 2.2), leak(X(.2), Ya + 9, .7), leak(X(.66), F - 10, 1.8)].join("")
    + mist(X(.49), Ya - 9) + mist(X(.9), Yc - 9);
  return `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">${outs.map(stream).join("")}
    ${hz.map(d => pipe(d, true)).join("")}${vt.map(d => pipe(d, false)).join("")}
    ${outs.map(([x, y]) => `<rect class="lip" x="${x - 12}" y="${y - 9}" width="24" height="7" rx="2"/>`).join("")}
    <g class="flanges"></g>${tees.map(tee).join("")}${valves}
    ${ends.map(([x, y]) => `<g transform="translate(${x} ${y})"><circle r="9" fill="#5d6b79"/>${WHEEL}</g>`).join("")}
    ${gauges}${narrow ? "" : pump(X(.86), F)}${leaks}<g class="sprays"></g><g class="bursts"></g></svg><div class="ww-pool"></div>`;
}

// A burst's water: jets fanning out of the split (up when dir is -1, down
// when 1), beads flying and falling, and a cloud of mist.
function wwJet(dir, big) {
  let s = `<ellipse class="ww-bmist" rx="${big ? 34 : 24}" ry="${big ? 16 : 11}" cy="${dir * 20}"/>`;
  for (let i = 0; i < (big ? 9 : 6); i++) {
    const a = rnd(-.95, .95), l = rnd(50, 110) * (big ? 1.5 : 1), ex = Math.sin(a) * l, ey = dir * Math.cos(a) * l;
    s += `<path class="ww-jet" d="M0 0Q${(ex * .45).toFixed(1)} ${(ey * .8).toFixed(1)} ${ex.toFixed(1)} ${(ey + l * .45).toFixed(1)}" style="animation-delay:0s,${(i * .03).toFixed(2)}s"/>`;
  }
  for (let i = 0; i < (big ? 22 : 14); i++)
    s += `<circle class="ww-bead" r="${rnd(1.2, 2.8).toFixed(1)}" style="--dx:${rnd(-110, 110).toFixed(0)}px;--dy:${(dir * rnd(40, 140)).toFixed(0)}px;animation-delay:${rnd(0, .8).toFixed(2)}s"/>`;
  return s;
}

// The valves that sit on the main under the header: a wheel, and a relief
// valve that lets off with the surge.
const WW_HWHEEL = `<svg width="28" height="30" viewBox="0 0 28 30"><rect x="8" y="17" width="12" height="12" rx="2" fill="#5d6b79" stroke="#3d4854"/>
  <rect x="12.5" y="9" width="3" height="9" fill="#9aa8b6"/><g class="hw"><circle cx="14" cy="8" r="7" fill="none" stroke="#e8483b" stroke-width="2.6"/>
  <path d="M14 1v14M7 8h14" stroke="#e8483b" stroke-width="1.8"/><circle cx="14" cy="8" r="2" fill="#b8352b"/></g></svg>`;

const WW_HRELIEF = `<svg width="28" height="30" viewBox="0 0 28 30"><rect x="9" y="22" width="10" height="7" fill="#7d8b99"/>
  <rect x="10" y="6" width="8" height="17" rx="1.5" fill="#5d6b79" stroke="#3d4854"/><path d="M11 20l6-2-6-2 6-2-6-2 6-2" fill="none" stroke="#c9d3dd" stroke-width="1"/>
  <rect class="cap" x="9" y="1" width="10" height="6" rx="2" fill="#e8483b"/>
  <g class="vent" transform="translate(14 1)"><path d="M0 0q-6 -10 -14 -14"/><path d="M0 0q0 -12 -2 -20"/><path d="M0 0q6 -10 14 -14"/></g></svg>`;

function dripSound(vol = .22) {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, o = ctx.createOscillator(), g = ctx.createGain();
  o.type = "sine";
  o.frequency.setValueAtTime(rnd(1500, 2100), t);
  o.frequency.exponentialRampToValueAtTime(rnd(520, 720), t + .09);
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(vol, t + .008); g.gain.exponentialRampToValueAtTime(.001, t + .16);
  o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .18);
}

function squeakSound() {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, o = ctx.createOscillator(), v = ctx.createOscillator(), vd = ctx.createGain(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  o.type = "sawtooth"; o.frequency.setValueAtTime(700, t); o.frequency.linearRampToValueAtTime(1350, t + .35);
  v.frequency.value = 11; vd.gain.value = 60; v.connect(vd).connect(o.frequency);
  f.type = "bandpass"; f.frequency.value = 1100; f.Q.value = 4;
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.11, t + .03); g.gain.setValueAtTime(.11, t + .3); g.gain.linearRampToValueAtTime(0, t + .42);
  o.connect(f).connect(g).connect(ctx.destination); o.start(t); v.start(t); o.stop(t + .45); v.stop(t + .45);
}

function surgeSound() {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, 1.6, true);
  f.type = "lowpass"; f.frequency.setValueAtTime(220, t); f.frequency.exponentialRampToValueAtTime(1900, t + .5); f.frequency.exponentialRampToValueAtTime(260, t + 1.5);
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.28, t + .35); g.gain.linearRampToValueAtTime(0, t + 1.5);
  src.connect(f).connect(g).connect(ctx.destination); src.start(t); src.stop(t + 1.6);
}

// A burst: the crack of a joint letting go, then the hiss of water under pressure.
function burstSound(big) {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, o = ctx.createOscillator(), og = ctx.createGain();
  o.type = "square"; o.frequency.setValueAtTime(210, t); o.frequency.exponentialRampToValueAtTime(70, t + .12);
  og.gain.setValueAtTime(.12, t); og.gain.exponentialRampToValueAtTime(.001, t + .16);
  o.connect(og).connect(ctx.destination); o.start(t); o.stop(t + .2);
  const len = big ? 2 : 1.4, src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, len, false);
  f.type = "bandpass"; f.Q.value = .8; f.frequency.setValueAtTime(3200, t); f.frequency.exponentialRampToValueAtTime(1400, t + len);
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(big ? .22 : .15, t + .04); g.gain.setValueAtTime(big ? .2 : .13, t + len * .55); g.gain.linearRampToValueAtTime(0, t + len);
  src.connect(f).connect(g).connect(ctx.destination); src.start(t); src.stop(t + len);
}

// A relief valve letting off: a short, sharp pssht.
function reliefSound() {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  src.buffer = noiseBuffer(ctx, .7, false);
  f.type = "highpass"; f.frequency.value = 2500;
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.1, t + .03); g.gain.exponentialRampToValueAtTime(.001, t + .65);
  src.connect(f).connect(g).connect(ctx.destination); src.start(t); src.stop(t + .7);
}

// The pressure alarm: a two-tone klaxon.
function alarmSound() {
  const ctx = waterCtx(); if (!ctx) return;
  const t = ctx.currentTime + .01, o = ctx.createOscillator(), f = ctx.createBiquadFilter(), g = ctx.createGain();
  o.type = "square";
  for (let i = 0; i < 6; i++) o.frequency.setValueAtTime(i % 2 ? 620 : 830, t + i * .22);
  f.type = "lowpass"; f.frequency.value = 2400;
  g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.07, t + .02); g.gain.setValueAtTime(.07, t + 1.28); g.gain.linearRampToValueAtTime(0, t + 1.34);
  o.connect(f).connect(g).connect(ctx.destination); o.start(t); o.stop(t + 1.36);
}

// A new device nobody has marked known is an intruder: a leak. The main under
// the header bursts, the pressure alarm sounds, every gauge slams into the red
// and the pipes shudder; then a section of pipe along the bottom is isolated,
// its valve shut, chained and padlocked with a red DO NOT OPEN tag, still
// dripping, and tagged, until the device is marked known. Then the lock comes
// off, the wheel spins open, and the valve on the main opens for the device.
const WW_LOCKOUT = `<svg viewBox="0 0 200 104" width="200" height="104">
  <rect x="0" y="62" width="200" height="22" fill="#5d6b79" stroke="#3d4854" stroke-width="1.5"/>
  <rect x="0" y="65" width="200" height="4" fill="#8795a3" opacity=".6"/>
  <rect x="62" y="56" width="8" height="34" rx="1" fill="#7d8b99" stroke="#3d4854"/><rect x="130" y="56" width="8" height="34" rx="1" fill="#7d8b99" stroke="#3d4854"/>
  <rect x="84" y="50" width="32" height="44" rx="4" fill="#5d6b79" stroke="#3d4854" stroke-width="1.5"/>
  <rect x="97" y="26" width="6" height="26" fill="#9aa8b6"/>
  <g class="ww-lwheel" transform="translate(100 20)"><g><circle r="15" fill="none" stroke="#e8483b" stroke-width="4"/>
    <path d="M0 -15V15M-15 0H15M-10.5 -10.5L10.5 10.5M-10.5 10.5L10.5 -10.5" stroke="#e8483b" stroke-width="2.2"/><circle r="4" fill="#b8352b"/></g></g>
  <g class="ww-lock">
    <path d="M88 10q12 10 24 0" fill="none" stroke="#c9d3dd" stroke-width="2" stroke-dasharray="3 2"/>
    <path d="M113 24v-5a5 5 0 0 1 10 0v5" fill="none" stroke="#c9d3dd" stroke-width="2.4"/>
    <rect x="110" y="23" width="16" height="13" rx="2" fill="#f2c230" stroke="#8a6a10"/><circle cx="118" cy="29" r="1.8" fill="#5a4308"/>
    <g class="ww-dtag"><path d="M118 36v6" stroke="#c9d3dd" stroke-width="1.4"/>
      <rect x="106" y="42" width="26" height="36" rx="2" fill="#e8483b" stroke="#8a1f16"/><circle cx="119" cy="46" r="1.8" fill="#8a1f16"/>
      <rect x="106" y="50" width="26" height="9" fill="#fff"/>
      <text x="119" y="57" text-anchor="middle" font-family="IBM Plex Mono, monospace" font-weight="700" font-size="6" fill="#8a1f16">DANGER</text>
      <text x="119" y="67" text-anchor="middle" font-family="IBM Plex Mono, monospace" font-weight="700" font-size="4.6" fill="#fff">DO NOT</text>
      <text x="119" y="73" text-anchor="middle" font-family="IBM Plex Mono, monospace" font-weight="700" font-size="4.6" fill="#fff">OPEN</text></g></g>
  <circle class="ww-ldrop" cx="40" cy="86" r="2.6"/><circle class="ww-ldrop" cx="160" cy="86" r="2.6" style="animation-delay:1.1s"/>
  <ellipse cx="100" cy="100" rx="90" ry="4" fill="#1f6fae" opacity=".55"/></svg>`;

function buildWaterworks(root) {
  const calm = calmMotion(), c = waterCommon(root);
  // Sound, behind the speaker button beside the theme button.
  const soundOn = themeSoundButton("bamf-waterworks-sound", "Waterworks sounds on: click to mute",
    "Waterworks sounds off: click for drips, valves and the surge", on => { if (on) dripSound(); });
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg ww-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = wwPipework();
  document.body.prepend(bg);
  // A flange every so often along each run, square to the pipe.
  const flanges = bg.querySelector(".flanges");
  for (const path of bg.querySelectorAll("path.rim")) {
    const len = path.getTotalLength();
    for (let d = 70; d < len - 40; d += 190) {
      const a = path.getPointAtLength(d), b = path.getPointAtLength(d + 1);
      const deg = Math.atan2(b.y - a.y, b.x - a.x) * 180 / Math.PI;
      flanges.insertAdjacentHTML("beforeend", `<rect class="flange" x="-3" y="-13" width="6" height="26" rx="1" transform="translate(${a.x.toFixed(1)} ${a.y.toFixed(1)}) rotate(${deg.toFixed(1)})"/>`);
    }
  }
  // The gauges read the share of devices online; a scan spikes them.
  const needles = [...bg.querySelectorAll(".needle")];
  const readGauges = spike => {
    const live = hosts.filter(h => !h.ignored && !h.forgotten), on = live.filter(h => h.online).length;
    const share = live.length ? on / live.length : 1;
    needles.forEach((n, i) => n.setAttribute("transform", `rotate(${(-120 + 240 * Math.min(1, share + (spike ? .25 : 0)) - i * 6).toFixed(1)})`));
  };
  readGauges(false);
  // A joint that sprays when a device drops.
  const sprays = bg.querySelector(".sprays");
  const spray = () => {
    if (calm || document.hidden) return;
    const fl = [...flanges.children];
    if (!fl.length) return;
    const f = pick(fl);
    const m = /translate\(([-\d.]+) ([-\d.]+)\)/.exec(f.getAttribute("transform"));
    if (!m) return;
    const g = svgEl("g", { transform: `translate(${m[1]} ${m[2] - 6})` });
    g.innerHTML = `<g class="spray"><path d="M0 0q30 -14 60 6M0 0q34 -4 56 16M0 0q24 -22 50 -8" fill="none" stroke="#6cc8ff" stroke-width="2" stroke-dasharray="3 4" stroke-linecap="round"/></g>`;
    sprays.appendChild(g);
    festiveTimers.push(setTimeout(() => g.remove(), 2400));
  };

  // High-pressure bursts: now and then a run splits and sprays, the pipes
  // shudder, the nearest gauge slams into the red and the nearest wheel
  // spins shut on it.
  const bursts = bg.querySelector(".bursts");
  const gaugeEls = [...bg.querySelectorAll(".gauge")], wheels = [...bg.querySelectorAll(".wheel")];
  const balls = [...bg.querySelectorAll(".ballv")], reliefs = [...bg.querySelectorAll(".relief")];
  const nearest = (els, x, y) => {
    let best = null, bd = Infinity;
    for (const el of els) {
      const r = el.getBoundingClientRect(), d = (r.x + r.width / 2 - x) ** 2 + (r.y + r.height / 2 - y) ** 2;
      if (d < bd) { bd = d; best = el; }
    }
    return best;
  };
  const restart = (el, cls) => { el.classList.remove(cls); void el.getBoundingClientRect(); el.classList.add(cls); };
  const burst = big => {
    if (calm || document.hidden) return;
    const runs = [...bg.querySelectorAll("path.rim.h")].filter(p => p.getTotalLength() > 80);
    if (!runs.length) return;
    const path = pick(runs), len = path.getTotalLength();
    const pt = path.getPointAtLength(rnd(.15, .85) * len), dir = Math.random() < .65 ? -1 : 1;
    const g = svgEl("g", { transform: `translate(${pt.x.toFixed(1)} ${(pt.y + dir * 7).toFixed(1)})` });
    g.innerHTML = wwJet(dir, big);
    bursts.appendChild(g);
    restart(bg, "quake");
    const gauge = nearest(gaugeEls, pt.x, pt.y);
    if (gauge) { gauge.classList.add("red"); gauge.querySelector(".needle")?.setAttribute("transform", "rotate(118)"); }
    const wheel = nearest(wheels, pt.x, pt.y);
    festiveTimers.push(setTimeout(() => wheel && restart(wheel, "shutting"), 900));
    if (soundOn()) burstSound(big);
    festiveTimers.push(setTimeout(() => {
      g.remove(); bg.classList.remove("quake"); gauge?.classList.remove("red"); wheel?.classList.remove("shutting"); readGauges(false);
    }, 2600));
  };
  // A device dropping shuts a ball valve for a few seconds.
  const shutBall = () => {
    if (!balls.length) return;
    const b = pick(balls);
    b.classList.add("shut");
    festiveTimers.push(setTimeout(() => b.classList.remove("shut"), 3200));
  };

  // Valves on the main under the header, wherever the header is clear above
  // it: two wheels and a relief valve. The header's chips and labels fill in
  // after the first poll and change width as the scan text changes, so the
  // spots are chosen again whenever the header's content changes.
  const head = document.querySelector("header");
  const headValves = [];
  let headSig = "";
  const placeHeadValves = () => {
    if (!head) return;
    const hb = head.getBoundingClientRect(), sig = `${innerWidth}|${Math.round(hb.bottom)}|${head.innerText.length}`;
    if (sig === headSig || hb.bottom < 30) return;
    headSig = sig;
    headValves.splice(0).forEach(d => d.remove());
    const clear = x => [-36, -24, -12, 0, 12, 24, 36].every(dx => [4, 14, 26, 36].every(dy => document.elementFromPoint(x + dx, hb.bottom - dy) === head));
    const xs = [];
    for (let x = 120; x < innerWidth - 30; x += 240) if (clear(x)) xs.push(x);
    const chosen = xs.length > 3 ? [xs[1], xs[Math.floor(xs.length / 2)], xs[xs.length - 2]] : xs.slice(0, 3);
    [...new Set(chosen)].forEach((x, i) => {
      const d = document.createElement("div");
      d.className = "ww-hvalve" + (i === 1 ? " relief" : "");
      d.style.left = (x - 14) + "px";
      d.innerHTML = i === 1 ? WW_HRELIEF : WW_HWHEEL;
      head.appendChild(d);
      headValves.push(d);
    });
  };
  festiveStops.push(() => headValves.forEach(d => d.remove()));
  // The surge lifts every relief valve in turn.
  const vent = () => {
    if (calm || document.hidden) return;
    [...reliefs, ...headValves.filter(d => d.classList.contains("relief"))].forEach((r, i) => festiveTimers.push(setTimeout(() => {
      restart(r, "venting");
      festiveTimers.push(setTimeout(() => r.classList.remove("venting"), 1700));
    }, i * 220)));
    if (soundOn()) reliefSound();
  };
  // The main under the header bursts over the page, and its nearest wheel shuts it off.
  const headBurst = atX => {
    if (calm || document.hidden || !head) return;
    const hb = head.getBoundingClientRect();
    if (hb.bottom < 0) return;
    const x = atX ?? Math.round(rnd(.12, .88) * innerWidth);
    const d = document.createElement("div");
    d.className = "ww-hburst";
    d.style.cssText = `left:${x}px;top:${hb.bottom + 12}px`;
    d.innerHTML = `<svg width="1" height="1">${wwJet(1, true)}</svg><i class="crack"></i>`;
    root.appendChild(d);
    document.documentElement.classList.add("ww-shake");
    const wheel = headValves.filter(v => !v.classList.contains("relief"))
      .sort((a, b) => Math.abs(a.offsetLeft - x) - Math.abs(b.offsetLeft - x))[0];
    festiveTimers.push(setTimeout(() => wheel && restart(wheel, "shutting"), 800));
    if (soundOn()) burstSound(true);
    festiveTimers.push(setTimeout(() => document.documentElement.classList.remove("ww-shake"), 800));
    festiveTimers.push(setTimeout(() => { d.remove(); wheel?.classList.remove("shutting"); }, 2600));
  };
  festiveStops.push(() => document.documentElement.classList.remove("ww-shake"));

  // The tank: devices online now against the most seen online this session.
  const tank = document.createElement("div");
  tank.className = "ww-tank";
  tank.innerHTML = `<svg width="26" height="30" viewBox="0 0 26 30" aria-hidden="true">
    <rect x="3" y="3" width="20" height="25" rx="3" fill="#0d1318" stroke="#9aa8b6" stroke-width="2"/>
    <g class="water"><rect x="4.5" y="4.5" width="17" height="22" rx="1.5" fill="#1f6fae"/>
      <path class="wave" d="M4.5 6q2.2-2 4.3 0t4.3 0 4.3 0 4.3 0v2h-17z" fill="#6cc8ff"/></g>
    <path d="M8 1h10v3H8z" fill="#5d6b79"/></svg>`;
  const track = document.querySelector(".scanline .scan-track");
  track?.parentElement.insertBefore(tank, track);
  festiveStops.push(() => tank.remove());
  let peak = 0;
  const fillTank = () => {
    const live = hosts.filter(h => !h.ignored && !h.forgotten), on = live.filter(h => h.online).length;
    peak = Math.max(peak, on);
    const share = peak ? on / peak : 1;
    tank.querySelector(".water").style.transform = `scaleY(${Math.max(.08, share).toFixed(2)})`;
    tank.classList.toggle("full", live.length > 0 && live.every(h => h.online));
    tank.title = `Tank: ${on} device${on === 1 ? "" : "s"} online, of ${peak} at most this session`;
  };

  // Drips off a flange on the main.
  if (!calm) festiveTimers.push(setInterval(() => {
    if (document.hidden) return;
    const d = document.createElement("i");
    d.className = "ww-drip";
    d.style.cssText = `left:${(Math.floor(rnd(1, innerWidth / 240)) * 240 - 14).toFixed(0)}px;top:${c.below() + 10}px`;
    d.addEventListener("animationend", () => d.remove());
    root.appendChild(d);
    // The plop, as the drop lands.
    if (soundOn()) festiveTimers.push(setTimeout(() => dripSound(), 1500));
  }, 9000));

  // Search: a wash over the rows as you type.
  const search = $("search");
  const wash = () => { if (calm) return; const tb = document.querySelector("table tbody"); if (!tb) return; tb.classList.remove("ww-wash"); void tb.offsetWidth; tb.classList.add("ww-wash"); };
  const onInput = () => festiveTimers.push(setTimeout(wash, 30));
  search?.addEventListener("input", onInput);
  festiveStops.push(() => search?.removeEventListener("input", onInput));

  const surge = () => {
    const html = document.documentElement;
    html.classList.add("ww-surge");
    readGauges(true);
    if (soundOn() && !document.hidden) surgeSound();
    vent();
    festiveTimers.push(setTimeout(() => { html.classList.remove("ww-surge"); readGauges(false); }, 1300));
  };
  festiveStops.push(() => document.documentElement.classList.remove("ww-surge"));
  festiveHooks.scanDone = surge;
  // A burst somewhere in the pipework every half a minute or so, and the
  // main under the header lets go now and then.
  if (!calm) {
    const nextBurst = first => festiveTimers.push(setTimeout(() => { burst(Math.random() < .3); nextBurst(false); }, first ? rnd(6e3, 14e3) : rnd(20e3, 40e3)));
    nextBurst(true);
    const nextHead = first => festiveTimers.push(setTimeout(() => { headBurst(); nextHead(false); }, first ? rnd(25e3, 45e3) : rnd(60e3, 110e3)));
    nextHead(true);
  }

  // A new device, once it's been marked known: a valve spins open on the main
  // and a tag drops with its name.
  const welcome = h => {
    if (document.hidden) return;
    if (soundOn()) squeakSound();
    const x = Math.round(innerWidth * .6);
    const v = document.createElement("div");
    v.className = "ww-newvalve";
    v.style.cssText = `left:${x - 15}px;top:${c.below() - 21}px`;
    v.innerHTML = `<svg width="30" height="30" viewBox="0 0 30 30"><circle cx="15" cy="15" r="11" fill="none" stroke="#e8483b" stroke-width="3.5"/>
      <path d="M15 4v22M4 15h22" stroke="#e8483b" stroke-width="2.5"/><circle cx="15" cy="15" r="3" fill="#b8352b"/></svg>`;
    root.appendChild(v);
    const t = document.createElement("div");
    t.className = "ww-tagdrop";
    t.style.cssText = `left:${x - 6}px;top:${c.below() + 16}px`;
    t.textContent = `New: ${h ? nameOrIp(h) : "a device"}`;
    root.appendChild(t);
    surge();
    festiveTimers.push(setTimeout(() => { v.remove(); t.remove(); }, 3500));
  };

  // ---- intruders: leaks, isolated and locked out ----
  const W = innerWidth;
  const watchIn = intruderWatch();
  const locks = new Map();              // id -> { h, el, tag, alarm, gone }
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const slotX = k => Math.round(W < 700 ? W * .56 : W * .7 - k * 270);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...locks.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeLock = h => {
    const el = document.createElement("div");
    el.className = "ww-lockout";
    el.innerHTML = WW_LOCKOUT;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const l = { h, el, tag, alarm: 0, gone: false };
    locks.set(h.id, l);
    return l;
  };
  const settle = () => {
    const list = order();
    room.style.height = list.some(l => !l.gone) ? "116px" : "0";
    list.forEach((l, k) => {
      l.el.hidden = k > (W < 700 ? 0 : 1);
      l.el.style.left = slotX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!locks.has(h.id)) makeLock(h);
    for (const h of held) { const l = locks.get(h.id); if (l && !l.gone) { l.h = h; if (!l.alarm || Date.now() - l.alarm > 9000) intruderTagEl(h, "held", l.tag); } }
    for (const h of cleared) {
      const l = locks.get(h.id); if (!l || l.gone) continue;
      l.gone = true;
      const done = () => { l.el.remove(); locks.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: the lock comes off, the wheel spins open, and the main
      // opens for the device.
      intruderTagEl(l.h, "cleared", l.tag);
      festiveTimers.push(setTimeout(() => { l.el.classList.add("open"); if (soundOn()) squeakSound(); }, 1400));
      festiveTimers.push(setTimeout(() => l.el.classList.add("gone"), 3000));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) welcome(h); }, 4400));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const l = locks.get(h.id) || makeLock(h);
    if (calm || document.hidden) { settle(); return; }
    l.alarm = Date.now();
    intruderTagEl(h, "alarm", l.tag);
    settle();
    // The leak: the main bursts, the klaxon, every gauge into the red.
    headBurst(slotX(0));
    restart(bg, "quake");
    gaugeEls.forEach(g => { g.classList.add("red"); g.querySelector(".needle")?.setAttribute("transform", "rotate(118)"); });
    if (soundOn()) { alarmSound(); burstSound(true); }
    const a = document.createElement("div");
    a.className = "ww-alarm";
    a.textContent = "PRESSURE ALARM · LEAK · ISOLATING";
    a.style.cssText = `left:${slotX(0)}px;top:${c.below() + 22}px`;
    root.appendChild(a);
    // Then the section's isolated: the lockout drops in, its wheel spinning shut.
    l.el.classList.remove("shutting"); void l.el.offsetWidth; l.el.classList.add("shutting");
    festiveTimers.push(setTimeout(() => { a.remove(); bg.classList.remove("quake"); gaugeEls.forEach(g => g.classList.remove("red")); readGauges(false); }, 6000));
    festiveTimers.push(setTimeout(() => { if (!l.gone) intruderTagEl(l.h, "held", l.tag); }, 9000));
  };

  let lastView = view;
  festiveHooks.rendered = () => {
    syncIntruders();
    const down = new Set(wentOffIds);
    if (wentOffIds.length) { spray(); burst(true); shutBall(); }
    if (wentOffIds.length && soundOn() && !document.hidden) { dripSound(.3); festiveTimers.push(setTimeout(() => dripSound(.2), 220)); festiveTimers.push(setTimeout(() => dripSound(.12), 520)); }
    readGauges(false);
    for (const id of wentOffIds) {
      const cell = document.querySelector(`tr[data-id="${id}"] td.hostname`);
      if (!cell) continue;
      const l = document.createElement("span");
      l.className = "ww-leak";
      l.innerHTML = `<i></i><i></i><i></i><i></i><i></i><b></b><svg class="ww-rowjet" width="1" height="1"><path d="M0 14q10 -12 30 -10"/><path d="M0 14q14 -4 34 4"/><path d="M0 14q8 -16 20 -20"/></svg>`;
      cell.appendChild(l);
      festiveTimers.push(setTimeout(() => l.remove(), 2700));
    }
    wentOffIds = [];
    for (const el of c.changedStats()) { el.classList.remove("ww-roll"); void el.offsetWidth; el.classList.add("ww-roll"); }
    placeHeadValves();
    fillTank();
    // A red shut-off valve on watched devices: open while they run, closed when they drop.
    for (const h of hosts) {
      if (!h.watched) continue;
      const cell = document.querySelector(`tr[data-id="${h.id}"] td.hostname`);
      if (!cell || cell.querySelector(".ww-valve")) continue;
      const v = document.createElement("span");
      v.className = "ww-valve" + (down.has(h.id) ? " closing" : h.online ? "" : " shut");
      v.title = h.online ? "Valve open: running" : "Valve shut: offline";
      v.innerHTML = `<svg width="16" height="14" viewBox="0 0 16 14"><rect x="6.5" y="5" width="3" height="6" fill="#9aa8b6"/><rect x="2" y="10" width="12" height="3.5" rx="1" fill="#7d8b99"/>
        <g class="handle"><rect x="1" y="3.5" width="14" height="3" rx="1.5" fill="#e8483b"/><circle cx="8" cy="5" r="1.8" fill="#b8352b"/></g></svg>`;
      cell.querySelector(".name-text")?.after(v);
    }
    if (view === "map" && lastView !== "map" && !calm) {
      const mc = $("mapClusters");
      mc.classList.remove("ww-fill"); void mc.offsetWidth; mc.classList.add("ww-fill");
      festiveTimers.push(setTimeout(() => mc.classList.remove("ww-fill"), 1100));
    }
    lastView = view;
  };
  // An inspection tag on unknown devices' pipes.
  festiveHooks.decorateNode = (g, n) => {
    if (n.type !== "dev") return;
    const t = svgEl("g", { class: "ww-tag t-deco-icon", transform: "translate(-24,-24)" });
    t.appendChild(svgEl("path", { d: "M0 3l3-3h9v10H3L0 7z", fill: "#f2e6c9", stroke: "#25303b", "stroke-width": .8 }));
    const q = svgEl("text", { x: 7.5, y: 8, "text-anchor": "middle", "font-size": 8, "font-weight": 800, fill: "#25303b" });
    q.textContent = "?";
    t.appendChild(q);
    g.appendChild(t);
  };
  festiveHooks.rendered();
}

BAMF.registerTheme("waterworks", ctx => buildWaterworks(ctx.root, ctx.switched));
})();
