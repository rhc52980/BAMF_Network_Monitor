// Aquarium: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
function bubbleSound(n = 4, vol = .16) {
  const ctx = waterCtx(); if (!ctx) return;
  let t = ctx.currentTime + .02;
  for (let i = 0; i < n; i++) {
    const o = ctx.createOscillator(), g = ctx.createGain(), f0 = rnd(260, 480);
    o.type = "sine"; o.frequency.setValueAtTime(f0, t); o.frequency.exponentialRampToValueAtTime(f0 * 1.9, t + .07);
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(vol, t + .01); g.gain.exponentialRampToValueAtTime(.001, t + .09);
    o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .1);
    t += rnd(.06, .16);
  }
}

// The aquarium's pump: a low hum and a soft rush of water that runs while sound is on.
function pumpHum() {
  const ctx = waterCtx(); if (!ctx) return null;
  const src = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain(), o = ctx.createOscillator(), og = ctx.createGain();
  src.buffer = noiseBuffer(ctx, 4, true); src.loop = true;
  f.type = "lowpass"; f.frequency.value = 240;
  g.gain.setValueAtTime(0, ctx.currentTime); g.gain.linearRampToValueAtTime(.05, ctx.currentTime + 1.2);
  src.connect(f).connect(g).connect(ctx.destination); src.start();
  o.type = "sine"; o.frequency.value = 56; og.gain.value = .012; o.connect(og).connect(ctx.destination); o.start();
  return { stop() { try { g.gain.setTargetAtTime(0, ctx.currentTime, .3); og.gain.setTargetAtTime(0, ctx.currentTime, .3); src.stop(ctx.currentTime + 1.2); o.stop(ctx.currentTime + 1.2); } catch { } } };
}

// Drawn head-left, then mirrored so every fish faces right: the way it swims
// by default. One swimming right to left is flipped by its .rtl class.
const AQ_FISH = (body, fin) => `<svg width="46" height="24" viewBox="0 0 46 24"><g transform="translate(46 0) scale(-1 1)"><path class="tail" d="M34 12l11-9v18z" fill="${fin}"/>
  <ellipse cx="20" cy="12" rx="16" ry="9" fill="${body}"/><path d="M14 5q6-6 12 0" fill="${fin}"/><path d="M22 12q3 3 0 6" fill="none" stroke="${fin}" stroke-width="1.5"/>
  <circle cx="10" cy="10" r="2.2" fill="#fff"/><circle cx="9.5" cy="10" r="1.1" fill="#032a3a"/></g></svg>`;

// A shark, facing right, that cruises past now and then, well behind the glass.
const AQ_SHARK = `<svg width="170" height="70" viewBox="0 0 170 70">
  <path class="tail" d="M30 34L5 9l8 25-8 25z" fill="#5d7584"/>
  <path d="M24 34C50 14 110 12 150 28c10 4 16 8 14 10-14 8-54 14-94 10C50 46 34 40 24 34z" fill="#6f8796"/>
  <path d="M40 40c40 10 90 8 120-2-20 12-70 18-110 10z" fill="#e3eaee"/>
  <path d="M80 19L95 0l9 21z" fill="#5d7584"/><path d="M100 44L87 63l25-17z" fill="#5d7584"/><path d="M52 44l-4 9 12-7z" fill="#5d7584"/>
  <path d="M125 31v8M130 30v9M135 30v8" stroke="#4b6170" stroke-width="1.4" stroke-linecap="round"/>
  <circle cx="146" cy="31" r="2.3" fill="#0b1a22"/><path d="M149 40q7 2 13-1" fill="none" stroke="#3b4d59" stroke-width="1.2"/></svg>`;

// An octopus, facing right: mantle, two eyes and eight arms that curl.
const AQ_OCTOPUS = `<svg width="140" height="110" viewBox="0 0 140 110">
  <g fill="#a85fd0">
    <path class="arm" d="M44 56c-6 14-16 20-28 22 10 2 22-2 30-12z"/>
    <path class="arm" d="M52 60c-4 16-10 26-20 32 12 0 22-10 27-23z"/>
    <path class="arm" d="M62 63c-1 17-4 28-10 36 10-4 17-16 18-30z"/>
    <path class="arm" d="M72 63c3 17 3 28 0 37 8-6 12-19 10-33z"/>
    <path class="arm" d="M82 61c7 15 9 26 8 35 7-7 8-21 3-33z"/>
    <path class="arm" d="M91 57c11 12 16 21 18 30 4-9 1-23-9-33z"/>
    <path class="arm" d="M97 51c13 7 21 14 26 22 1-10-7-22-19-27z"/>
    <path class="arm" d="M38 49c-13 6-21 12-27 20 0-10 9-20 22-24z"/>
  </g>
  <ellipse cx="68" cy="38" rx="36" ry="30" fill="#b46ad8"/>
  <ellipse cx="68" cy="46" rx="28" ry="20" fill="#c98ae4"/>
  <circle cx="84" cy="30" r="9" fill="#fff"/><circle cx="87" cy="30" r="4.5" fill="#1b0c26"/>
  <circle cx="58" cy="28" r="8" fill="#fff"/><circle cx="61" cy="28" r="4" fill="#1b0c26"/>
  <g fill="#8e4cb4" opacity=".55">
    <circle cx="52" cy="58" r="2"/><circle cx="64" cy="62" r="2"/><circle cx="76" cy="62" r="2"/><circle cx="88" cy="58" r="2"/>
  </g></svg>`;

// A crab, facing right: shell, two claws, six legs and eyes on stalks.
const AQ_CRAB = `<svg width="96" height="60" viewBox="0 0 96 60">
  <g stroke="#b53a22" stroke-width="3.4" stroke-linecap="round" fill="none">
    <path d="M30 40l-9 12M40 42l-5 13M50 42l3 13M60 40l8 12"/>
  </g>
  <path class="claw" d="M20 30c-8-2-14 2-15 8 4 3 9 3 12 0l1 5c4-3 5-9 2-13z" fill="#e0563b"/>
  <path class="claw b" d="M74 28c8-3 15 0 16 6-3 4-8 5-12 2l-1 5c-4-2-6-9-3-13z" fill="#e0563b"/>
  <ellipse cx="47" cy="33" rx="25" ry="16" fill="#e0563b"/>
  <path d="M26 30q21-10 42 0" fill="none" stroke="#f08a6f" stroke-width="3" stroke-linecap="round"/>
  <g stroke="#c0442a" stroke-width="3" stroke-linecap="round"><path d="M40 19v-8M55 19v-8"/></g>
  <circle cx="40" cy="9" r="4.5" fill="#fff"/><circle cx="41" cy="9" r="2.2" fill="#2a0d06"/>
  <circle cx="55" cy="9" r="4.5" fill="#fff"/><circle cx="56" cy="9" r="2.2" fill="#2a0d06"/>
  <path d="M36 38q11 6 22 0" fill="none" stroke="#8f2d19" stroke-width="2" stroke-linecap="round"/></svg>`;

// A diver, facing right: tank, hose, mask, and fins that kick.
const AQ_DIVER = `<svg width="170" height="86" viewBox="0 0 170 86">
  <path class="fin" d="M18 36l-16-9 2 12-2 12 16-7z" fill="#2f9fbf"/>
  <path class="fin b" d="M20 48l-14 4 4 8 12-4z" fill="#2aa0a8"/>
  <path d="M26 32c18-7 44-9 62-5l4 14c-18 6-46 6-66 0z" fill="#22364a"/>
  <rect x="44" y="18" width="26" height="20" rx="8" fill="#8fa6b3"/>
  <rect x="48" y="16" width="6" height="6" rx="2" fill="#5d7584"/>
  <path d="M70 26c14-2 22 2 26 8" fill="none" stroke="#1b2a3a" stroke-width="4"/>
  <path d="M96 30q8-8 16-4" fill="none" stroke="#3d5063" stroke-width="3.4" stroke-linecap="round"/>
  <circle cx="116" cy="30" r="15" fill="#22364a"/>
  <path d="M118 22a12 12 0 0 1 12 10 12 12 0 0 1-12 8 9 9 0 0 1-4-9z" fill="#bfe9ff" opacity=".9"/>
  <path d="M104 33q6 8 14 6" fill="none" stroke="#12202e" stroke-width="3" stroke-linecap="round"/>
  <path d="M88 40q16 8 30 2" fill="none" stroke="#2c4358" stroke-width="7" stroke-linecap="round"/>
  <path d="M116 44q10 6 18 0" fill="none" stroke="#e0b48a" stroke-width="6" stroke-linecap="round"/></svg>`;

// A sea turtle, facing right: shell, head, and flippers that row.
const AQ_TURTLE = `<svg width="170" height="96" viewBox="0 0 170 96">
  <path class="flip b" d="M62 60c-14 10-30 20-44 22 12-12 22-22 34-28z" fill="#4f8a5c"/>
  <path class="flip" d="M104 56c14 14 22 28 24 38-12-8-24-22-30-32z" fill="#4f8a5c"/>
  <path d="M40 50c6-4 10-4 14 0-4 6-10 8-16 6z" fill="#5d9a68"/>
  <ellipse cx="86" cy="46" rx="46" ry="26" fill="#6b5a2e"/>
  <path d="M46 46c10-22 70-26 82 0" fill="#7f6a36"/>
  <g fill="none" stroke="#4d3f1d" stroke-width="2.2"><path d="M70 30l-8 16 10 14M102 30l8 16-10 14M72 30h28M62 46h48M72 60h28"/></g>
  <path d="M130 42c10-8 22-8 28 0 2 6-4 12-12 12-8 0-14-4-16-12z" fill="#5d9a68"/>
  <circle cx="148" cy="42" r="2.4" fill="#0e1a12"/><path d="M152 49q4 0 6-2" fill="none" stroke="#2f5a38" stroke-width="1.4" stroke-linecap="round"/>
  <path class="flip" d="M112 40c10-12 24-20 36-22-8 10-20 20-30 26z" fill="#5d9a68"/>
  <path class="flip b" d="M60 38c-10-10-22-14-32-14 8 8 18 14 28 18z" fill="#5d9a68"/></svg>`;

// A jellyfish: a bell that pulses and strands that trail.
const AQ_JELLY = (c1, c2) => `<svg width="60" height="96" viewBox="0 0 60 96">
  <g class="strand">${[14, 22, 30, 38, 46].map((x, i) => `<path d="M${x} 30q${i % 2 ? 6 : -6} 16 0 32t${i % 2 ? -4 : 4} 28" fill="none" stroke="${c2}" stroke-width="1.6" opacity=".75"/>`).join("")}</g>
  <path d="M24 30q-2 12 4 20M36 30q2 12-4 20" fill="none" stroke="${c1}" stroke-width="3" opacity=".7"/>
  <g class="bell"><path d="M4 32C4 12 16 2 30 2s26 10 26 30c-6-4-10 2-14-1-4 3-8-3-12 0-4-3-8 3-12 0-4 3-8-3-14 1z" fill="${c1}"/>
    <path d="M12 16q18-12 36 0" fill="none" stroke="#fff" stroke-width="2" opacity=".35" stroke-linecap="round"/></g></svg>`;

// The seabed along the bottom of the tank: sand, and on it an anchor on its
// chain, a treasure chest, coral and anemones, a sea star, a clam, and the
// wreck of a ship with its colours still flying and an eel in a porthole.
// It's one strip drawn to the window's width, in its own units (vw of them
// across, 104 up), scaled so the band is about 100px tall on a wide screen.
function aqSeabed(vw, seed) {
  let r = seed;
  const rand = () => { r = (r * 16807) % 2147483647; return (r - 1) / 2147483646; };
  const sandY = x => 64 + Math.sin(x / 90) * 3 + Math.sin(x / 37 + 1) * 1.5;
  const at = (x, inner, cls = "") => `<g${cls ? ` class="${cls}"` : ""} transform="translate(${x.toFixed(1)} 0)">${inner}</g>`;
  const wide = vw >= 1100;
  const anchorX = vw * .06, chestX = vw * .27, coralX = vw * .45, starX = vw * .57, wreckX = vw * .66, clamX = vw * .91;

  // Water down to the sand, so the band is solid and nothing behind it shows through.
  let sandTop = `M0 ${sandY(0).toFixed(1)}`;
  for (let x = 20; x <= vw + 20; x += 20) sandTop += `L${x} ${sandY(x).toFixed(1)}`;
  const pebbles = Array.from({ length: Math.round(vw / 9) }, () => {
    const x = rand() * vw, y = sandY(x) + 6 + rand() * 32;
    const c = ["#9c845a", "#cdb88a", "#7d6a45", "#b59a6a"][Math.floor(rand() * 4)];
    return `<ellipse cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" rx="${(1.4 + rand() * 2.6).toFixed(1)}" ry="${(1 + rand() * 1.6).toFixed(1)}" fill="${c}"/>`;
  }).join("");

  const anchor = at(anchorX, `<g transform="translate(0 ${(sandY(anchorX) - 2).toFixed(1)}) rotate(-24)">
      <path d="M0 0V-46" stroke="#5b6b75" stroke-width="6" stroke-linecap="round"/>
      <circle cx="0" cy="-52" r="6" fill="none" stroke="#5b6b75" stroke-width="4"/>
      <path d="M-14 -38h28" stroke="#5b6b75" stroke-width="5" stroke-linecap="round"/>
      <path d="M-22 -12Q-20 4 0 4T22 -12" fill="none" stroke="#5b6b75" stroke-width="6" stroke-linecap="round"/>
      <path d="M-26 -16l8 2-2 8zM26 -16l-8 2 2 8z" fill="#5b6b75"/>
      <circle cx="-4" cy="-30" r="2" fill="#6f8a5c" opacity=".8"/><circle cx="3" cy="-20" r="1.6" fill="#6f8a5c" opacity=".8"/></g>
    ${Array.from({ length: 9 }, (_, i) => `<ellipse cx="${(18 + i * 9).toFixed(1)}" cy="${(sandY(anchorX + 18 + i * 9) + 1 - Math.sin(i / 2) * 2).toFixed(1)}" rx="5" ry="3" fill="none" stroke="#66767f" stroke-width="2"/>`).join("")}`);

  const chest = at(chestX, `<g class="aq-chest" transform="translate(0 ${(sandY(chestX) - 22).toFixed(1)})">
      <ellipse class="aq-glint" cx="24" cy="2" rx="30" ry="14" fill="#ffd23f"/>
      <g fill="#ffd23f" stroke="#b8860b" stroke-width=".8">
        <circle cx="12" cy="4" r="4"/><circle cx="20" cy="2" r="4"/><circle cx="29" cy="3" r="4"/><circle cx="37" cy="5" r="4"/><circle cx="24" cy="-2" r="4"/></g>
      <rect x="0" y="4" width="48" height="26" rx="2" fill="#7a4a22" stroke="#3b230e" stroke-width="2"/>
      <path d="M0 14h48" stroke="#3b230e" stroke-width="1.5"/>
      <path d="M8 4v26M40 4v26" stroke="#4a4a4a" stroke-width="4"/>
      <rect x="20" y="10" width="8" height="10" rx="2" fill="#c9a227" stroke="#6b5310" stroke-width="1"/>
      <g class="aq-lid"><path d="M0 4Q0 -10 24 -10T48 4z" fill="#8a5528" stroke="#3b230e" stroke-width="2"/>
        <path d="M8 -6v10M40 -6v10" stroke="#4a4a4a" stroke-width="4"/></g></g>
    <circle cx="58" cy="${(sandY(chestX + 58) + 3).toFixed(1)}" r="3" fill="#ffd23f" stroke="#b8860b" stroke-width=".7"/>
    <circle cx="66" cy="${(sandY(chestX + 66) + 6).toFixed(1)}" r="2.6" fill="#ffd23f" stroke="#b8860b" stroke-width=".7"/>`);

  const coral = wide ? at(coralX, `<g transform="translate(0 ${sandY(coralX).toFixed(1)})">
      <g class="sway" fill="none" stroke-linecap="round">
        <path d="M0 0V-30M0 -18l-10-12M0 -24l9-14M-10 -30l-4-8M9 -38l5-6" stroke="#ff6b5a" stroke-width="5"/></g>
      <path d="M18 2c0-14 10-20 20-20s20 6 20 20z" fill="#d98b5f"/>
      <path d="M24 -2q4-6 8 0t8 0 8 0M26 -8q4-5 8 0t8 0" fill="none" stroke="#b56c43" stroke-width="1.6"/>
      <g class="aq-anem" transform="translate(70 0)">${[-10, -5, 0, 5, 10].map(dx => `<path d="M0 0q${dx} -12 ${dx * 1.6} -22" fill="none" stroke="#ff8ad6" stroke-width="3.2" stroke-linecap="round"/>`).join("")}
        <ellipse cx="0" cy="0" rx="8" ry="4" fill="#c2468f"/></g>
      <g class="aq-anem" transform="translate(-26 2)">${[-8, -3, 2, 7].map(dx => `<path d="M0 0q${dx} -9 ${dx * 1.5} -17" fill="none" stroke="#b18cff" stroke-width="2.8" stroke-linecap="round"/>`).join("")}
        <ellipse cx="0" cy="0" rx="6" ry="3" fill="#7a55d6"/></g></g>`) : "";

  const star = wide ? at(starX, `<path transform="translate(0 ${(sandY(starX) + 2).toFixed(1)}) rotate(14)" d="M0 -12L3.5 -4 12 -3.5 5.5 2 7.5 10 0 5.5-7.5 10-5.5 2-12-3.5-3.5-4z"
      fill="#ff8a3d" stroke="#c45a1c" stroke-width="1"/>`) : "";

  // The wreck: a hull settled on its side in the sand, a broken mast with
  // the flag still on it, portholes, and an eel at home in one of them.
  const wy = sandY(wreckX + 110);
  const wreck = at(wreckX, `<g transform="translate(0 ${(wy - 59).toFixed(1)}) rotate(-5 110 60)">
      <defs><clipPath id="aqPort"><circle cx="112" cy="50" r="8"/></clipPath></defs>
      <path d="M118 36L146 8" stroke="#3a2618" stroke-width="7" stroke-linecap="round"/>
      <path d="M138 18l-16 -4" stroke="#3a2618" stroke-width="4" stroke-linecap="round"/>
      <path class="aq-flag" d="M144 8l20 -3-4 7 5 6-21 2z" fill="#161616"/>
      <g class="aq-flag"><circle cx="153" cy="11" r="3" fill="#e8e2d0"/><path d="M148 17l10-4M148 13l10 4" stroke="#e8e2d0" stroke-width="1.2"/></g>
      <path d="M126 34l24 -20 6 26z" fill="#cdbf9f" opacity=".55"/>
      <path d="M138 26l4 -3 2 6zM148 32l5 -2 1 5z" fill="#062f40"/>
      <path d="M0 64L10 38Q22 28 64 28L196 34Q214 36 222 48L214 70z" fill="#4a3322" stroke="#2b1c11" stroke-width="2.5"/>
      <path d="M8 46H216M4 56H218" stroke="#3a2618" stroke-width="2"/>
      <path d="M150 34l8 12-6 8 10 12-14 4z" fill="#062f40" stroke="#2b1c11" stroke-width="1.5"/>
      <path d="M154 40l4 20M160 44l2 18" stroke="#3a2618" stroke-width="2"/>
      <path d="M12 38l196 -2" stroke="#6b4a2c" stroke-width="3"/>
      ${[70, 112, 186].map(x => `<circle cx="${x}" cy="50" r="8.5" fill="#0c1a1f" stroke="#b58a3a" stroke-width="2.5"/>`).join("")}
      <g clip-path="url(#aqPort)"><g class="aq-eel">
        <path d="M86 52q14 -8 30 -2" fill="none" stroke="#5f8a2e" stroke-width="9" stroke-linecap="round"/>
        <circle cx="114" cy="48" r="1.7" fill="#fff"/><circle cx="114.4" cy="48" r=".9" fill="#111"/>
        <path d="M117 53l3 -1" stroke="#2f4a14" stroke-width="1.2"/></g></g>
      <g fill="#a9b7a0" opacity=".75"><circle cx="30" cy="60" r="2"/><circle cx="36" cy="63" r="1.5"/><circle cx="98" cy="64" r="2"/><circle cx="202" cy="60" r="2"/></g>
      <path class="sway" d="M40 36q-6 -12 2 -22t0 -18" fill="none" stroke="#3fb070" stroke-width="3" stroke-linecap="round"/>
      <path class="sway" d="M206 44q6 -10 0 -20" fill="none" stroke="#2f8f5a" stroke-width="3" stroke-linecap="round"/></g>
    <path d="M-10 ${(wy + 2).toFixed(1)}q30 -16 70 -6q30 8 40 0" fill="#a88f60"/>`);

  const clam = wide ? at(clamX, `<g class="aq-clam" transform="translate(0 ${(sandY(clamX) - 6).toFixed(1)})">
      <path d="M0 6Q16 16 32 6z" fill="#c8b6d8" stroke="#8a74a0" stroke-width="1.2"/>
      <circle cx="16" cy="6" r="4.5" fill="#fbf7ff"/><circle cx="14.6" cy="4.6" r="1.4" fill="#fff"/>
      <g class="aq-clam-top"><path d="M0 6Q16 -10 32 6z" fill="#d9c8e8" stroke="#8a74a0" stroke-width="1.2"/>
        <path d="M8 3l3 -6M16 1V-6M24 3l-3 -6" stroke="#b09cc6" stroke-width="1"/></g></g>`) : "";

  return `<defs><linearGradient id="aqSand" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#bfa674"/><stop offset=".5" stop-color="#9c845a"/><stop offset="1" stop-color="#6e5a3a"/></linearGradient>
      <linearGradient id="aqDeep" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#032a3a"/><stop offset="1" stop-color="#04374a"/></linearGradient></defs>
    <rect x="0" y="0" width="${vw}" height="104" fill="url(#aqDeep)"/>
    ${anchor}${coral}${wreck}
    <path d="${sandTop}L${vw + 20} 104H0z" fill="url(#aqSand)"/>
    ${pebbles}${chest}${star}${clam}`;
}

const AQ_COLOURS = [["#ff9f43", "#e8662a"], ["#ffd23f", "#e0a800"], ["#7ae0ff", "#2fa7d6"], ["#ff7ab6", "#d8438a"], ["#b18cff", "#7a55d6"]];

// A new device nobody has marked known is an intruder: a shark, in front of
// the glass. It charges in; the fish scatter at full speed, the crab digs in,
// the eel pulls back into the wreck, and the water flushes red. Then it
// prowls the bottom of the tank, tagged, until the device is marked known:
// then it swims off, and the device swims in as a fish with its name.
function buildAquarium(root) {
  const calm = calmMotion(), c = waterCommon(root);
  // Sound: bubbles when they rise, and the pump's hum while it's on.
  let hum = null;
  const humOn = on => { if (on && !hum && !document.hidden) hum = pumpHum(); if (!on && hum) { hum.stop(); hum = null; } };
  const soundOn = themeSoundButton("bamf-aquarium-sound", "Aquarium sounds on: click to mute",
    "Aquarium sounds off: click for bubbles and the pump", on => { humOn(on); if (on) bubbleSound(3); });
  if (soundOn()) {
    // Browsers only allow sound after a click on the page.
    const wake = () => humOn(soundOn());
    document.addEventListener("pointerdown", wake, { once: true });
    festiveStops.push(() => document.removeEventListener("pointerdown", wake));
  }
  const vis = () => humOn(soundOn() && !document.hidden);
  document.addEventListener("visibilitychange", vis);
  festiveStops.push(() => { document.removeEventListener("visibilitychange", vis); humOn(false); });
  let lastBubbleSound = 0;
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg aq-bg";
  bg.setAttribute("aria-hidden", "true");
  const weeds = [4, 13, 22, 58, 67, 83, 94].map((x, i) => {
    const h = 70 + (i * 37) % 90;
    return `<div class="aq-weed" style="left:${x}vw;animation-duration:${(3.5 + (i % 3)).toFixed(1)}s;animation-delay:-${i}s">
      <svg width="30" height="${h}" viewBox="0 0 30 ${h}"><path d="M15 ${h}C${5} ${h * .7} 25 ${h * .45} 13 ${h * .2}S18 4 16 0" fill="none" stroke="${i % 2 ? "#2f8f5a" : "#3fb070"}" stroke-width="5" stroke-linecap="round"/>
      <path d="M15 ${h}C24 ${h * .75} 6 ${h * .5} 19 ${h * .3}" fill="none" stroke="#27794b" stroke-width="3.5" stroke-linecap="round"/></svg></div>`;
  }).join("");
  bg.innerHTML = `<div class="aq-rays"></div>${weeds}<div class="aq-gravel"></div>`;
  document.body.prepend(bg);

  // The seabed, in front of the page along the bottom. It's solid, and the
  // page gets that much more room at the bottom (a spacer, not padding: the
  // body is the height of the window and the page overflows it), so the last
  // rows still scroll clear of the wreck.
  const S = Math.max(.7, Math.min(1.15, innerWidth / 1400));
  const bedH = Math.round(104 * S), bedW = innerWidth / S;
  const bed = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  bed.setAttribute("class", "aq-seabed");
  bed.setAttribute("width", String(innerWidth));
  bed.setAttribute("height", String(bedH));
  bed.setAttribute("viewBox", `0 0 ${bedW.toFixed(1)} 104`);
  bed.setAttribute("preserveAspectRatio", "none");
  bed.innerHTML = aqSeabed(bedW, 1234567);
  root.appendChild(bed);
  const room = document.createElement("div");
  room.style.height = `${bedH}px`;
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  // The weed grows up from the seabed rather than from behind it, where only
  // its tips would show above the sand.
  bg.querySelectorAll(".aq-weed").forEach(w => { w.style.bottom = `${bedH - Math.round(40 * S)}px`; });
  const chest = bed.querySelector(".aq-chest"), eel = bed.querySelector(".aq-eel"), clam = bed.querySelector(".aq-clam");
  // Open something for a while, then close it again.
  const briefly = (el, cls, ms) => {
    if (!el || calm || document.hidden) return;
    el.classList.add(cls);
    festiveTimers.push(setTimeout(() => el.classList.remove(cls), ms));
  };
  // Bubbles out of the chest, in front of the page, when it opens.
  const chestBubbles = n => {
    if (!chest || calm || document.hidden) return;
    const box = chest.getBoundingClientRect();
    for (let i = 0; i < n; i++) {
      const b = document.createElement("i");
      b.className = "aq-dbub";
      const sz = rnd(5, 11);
      b.style.cssText = `left:${(box.left + box.width * rnd(.25, .75)).toFixed(0)}px;top:${(box.top + 4).toFixed(0)}px;` +
        `width:${sz.toFixed(0)}px;height:${sz.toFixed(0)}px;animation-delay:${(i * .18).toFixed(2)}s`;
      b.addEventListener("animationend", () => b.remove());
      root.appendChild(b);
    }
  };

  const fish = () => {
    if (document.hidden) return;
    const [b, f] = pick(AQ_COLOURS);
    const el = document.createElement("div");
    el.className = "aq-fish" + (Math.random() < .5 ? " rtl" : "");
    el.style.cssText = `top:${rnd(18, 80).toFixed(0)}vh;animation-duration:${rnd(22, 40).toFixed(0)}s;opacity:${rnd(.45, .8).toFixed(2)}`;
    el.innerHTML = AQ_FISH(b, f);
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    bg.appendChild(el);
  };
  const bubbles = (n, x) => {
    if (calm || document.hidden) return;
    // A column of bubbles always sounds; the odd ones drifting up, now and then.
    if (soundOn() && (n >= 4 || Date.now() - lastBubbleSound > 12000)) { lastBubbleSound = Date.now(); bubbleSound(Math.min(n, 6), n >= 4 ? .16 : .09); }
    for (let i = 0; i < n; i++) {
      const b = document.createElement("i");
      b.className = "aq-bubble";
      const s = rnd(5, 14);
      b.style.cssText = `left:${(x ?? rnd(2, 97)).toFixed(1)}vw;width:${s.toFixed(0)}px;height:${s.toFixed(0)}px;--sway:${rnd(-24, 24).toFixed(0)}px;` +
        `animation-duration:${rnd(7, 13).toFixed(1)}s;animation-delay:${(i * .25).toFixed(2)}s`;
      b.addEventListener("animationend", () => b.remove());
      bg.appendChild(b);
    }
  };
  if (!calm) {
    for (let i = 0; i < 3; i++) festiveTimers.push(setTimeout(fish, i * 4000 + 500));
    festiveTimers.push(setInterval(() => { if (bg.querySelectorAll(".aq-fish").length < 5) fish(); }, 9000));
    festiveTimers.push(setInterval(() => bubbles(2), 2600));
    // Now and then the shark cruises past, slower and deeper than the fish.
    const shark = () => {
      if (document.hidden || bg.querySelector(".aq-shark")) return;
      const el = document.createElement("div");
      el.className = "aq-fish aq-shark" + (Math.random() < .5 ? " rtl" : "");
      el.style.cssText = `top:${rnd(30, 65).toFixed(0)}vh;animation-duration:${rnd(38, 52).toFixed(0)}s`;
      el.innerHTML = AQ_SHARK;
      el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
      bg.appendChild(el);
    };
    const nextShark = first => festiveTimers.push(setTimeout(() => { shark(); nextShark(false); }, first ? rnd(20e3, 40e3) : rnd(90e3, 180e3)));
    nextShark(true);

    // One of each at a time, each on its own unhurried schedule.
    // The octopus swims in the tank, behind the panels with the fish. The crab
    // and the diver would be lost back there - a crab is small and the gravel
    // is under the table - so they pass in front of the page instead, the way
    // the hotdog cart does, slow and see-through enough to read through.
    const swimmer = (cls, art, style, parent) => {
      if (document.hidden || document.querySelector("." + cls)) return null;
      const el = document.createElement("div");
      el.className = cls + (Math.random() < .5 ? " rtl" : "");
      el.style.cssText = style;
      el.innerHTML = art;
      el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
      (parent || bg).appendChild(el);
      return el;
    };
    const octopus = () => swimmer("aq-oct", AQ_OCTOPUS, `top:${rnd(24, 62).toFixed(0)}vh;animation-duration:${rnd(48, 70).toFixed(0)}s`);
    const crab = () => swimmer("aq-crab", AQ_CRAB, `animation-duration:${rnd(34, 55).toFixed(0)}s`, root);
    // The diver breathes out as he goes: a few bubbles from the regulator,
    // rising from wherever he's got to, until he's off the screen.
    const diver = () => {
      const el = swimmer("aq-diver", AQ_DIVER, `top:${rnd(26, 58).toFixed(0)}vh;animation-duration:${rnd(55, 80).toFixed(0)}s`, root);
      if (!el) return;
      const breathe = setInterval(() => {
        if (!el.isConnected) { clearInterval(breathe); return; }
        if (document.hidden) return;
        const r = el.getBoundingClientRect();
        if (r.width === 0) return;
        for (let i = 0; i < 3; i++) {
          const b = document.createElement("i");
          b.className = "aq-dbub";
          const sz = rnd(4, 9);
          // Out of the regulator: the front of him, whichever way he's facing.
          b.style.cssText = `left:${(el.classList.contains("rtl") ? r.left + r.width * .18 : r.left + r.width * .78).toFixed(0)}px;` +
            `top:${(r.top + r.height * .34).toFixed(0)}px;width:${sz.toFixed(0)}px;height:${sz.toFixed(0)}px;` +
            `animation-delay:${(i * .28).toFixed(2)}s`;
          b.addEventListener("animationend", () => b.remove());
          root.appendChild(b);
        }
      }, 2600);
      festiveTimers.push(breathe);
    };
    const every = (fn, first, gap) => {
      const again = () => festiveTimers.push(setTimeout(() => { fn(); again(); }, rnd(gap[0], gap[1])));
      festiveTimers.push(setTimeout(() => { fn(); again(); }, first));
    };
    every(crab, rnd(6e3, 14e3), [30e3, 70e3]);
    every(octopus, rnd(25e3, 45e3), [80e3, 150e3]);
    every(diver, rnd(45e3, 70e3), [110e3, 200e3]);
    // A turtle glides past, deeper and slower than anything else.
    const turtle = () => {
      if (document.hidden || bg.querySelector(".aq-turtle")) return;
      const el = document.createElement("div");
      el.className = "aq-fish aq-turtle" + (Math.random() < .5 ? " rtl" : "");
      el.style.cssText = `top:${rnd(34, 62).toFixed(0)}vh;animation-duration:${rnd(60, 85).toFixed(0)}s`;
      el.innerHTML = AQ_TURTLE;
      el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
      bg.appendChild(el);
    };
    every(turtle, rnd(15e3, 35e3), [90e3, 170e3]);
    // Jellyfish drift up from the bottom, a couple at most.
    const JELLY = [["#ff9fd6", "#ffc7e8"], ["#b9a2ff", "#dccfff"], ["#8fe3ff", "#c8f2ff"]];
    const jelly = () => {
      if (document.hidden || bg.querySelectorAll(".aq-jelly").length >= 2) return;
      const [c1, c2] = pick(JELLY);
      const el = document.createElement("div");
      el.className = "aq-jelly";
      el.style.cssText = `left:${rnd(6, 88).toFixed(0)}vw;animation-duration:${rnd(55, 80).toFixed(0)}s`;
      el.innerHTML = AQ_JELLY(c1, c2);
      el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
      bg.appendChild(el);
    };
    every(jelly, rnd(4e3, 10e3), [40e3, 80e3]);
    // The eel looks out now and then of its own accord, and the clam opens.
    every(() => briefly(eel, "peek", 4200), rnd(20e3, 40e3), [45e3, 90e3]);
    every(() => briefly(clam, "open", 3800), rnd(30e3, 55e3), [60e3, 120e3]);
  } else {
    // Reduced motion: a crab sits on the gravel instead of scuttling across it.
    const el = document.createElement("div");
    el.className = "aq-crab still";
    el.style.cssText = "animation:none;left:14vw";
    el.innerHTML = AQ_CRAB;
    root.appendChild(el);
  }

  // A scan: a column of bubbles from the gravel, and the treasure chest
  // opens with a stream of them of its own.
  festiveHooks.scanDone = () => {
    bubbles(10, rnd(10, 90));
    briefly(chest, "open", 3200);
    chestBubbles(8);
  };
  // Something drops off the network: the eel comes out to see what happened.
  // Something comes back: the clam opens to show its pearl.
  festiveHooks.netChange = (off, back) => {
    if (off.length) briefly(eel, "peek", 5200);
    if (back.length) briefly(clam, "open", 4200);
  };
  // ---- intruders: sharks prowling the bottom of the tank ----
  const watchIn = intruderWatch();
  const sharks = new Map();             // id -> { el, tag, h, alarm, gone, dir }
  const SHOW = 2, SW = 200;
  const prowlRoom = document.createElement("div");
  prowlRoom.setAttribute("aria-hidden", "true");
  document.body.appendChild(prowlRoom);
  festiveStops.push(() => prowlRoom.remove());
  const prowling = () => [...sharks.values()].filter(k => !k.gone).sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  // Each shark has a lane just above the seabed, the newest lowest; the page
  // gets room at the bottom to scroll clear of them.
  const lane = k => bedH + 6 + k * 78;
  const place = () => {
    const list = prowling();
    prowlRoom.style.height = list.length ? `${Math.min(list.length, SHOW) * 78 + 60}px` : "0";
    list.forEach((k, i) => { k.el.hidden = i >= SHOW; k.el.style.bottom = lane(i) + "px"; });
  };
  const makeShark = (h, x) => {
    const el = document.createElement("div");
    el.className = "aq-intruder";
    el.innerHTML = AQ_SHARK.replace('width="170" height="70"', `width="${SW}" height="${Math.round(SW * 70 / 170)}"`);
    el.style.left = x + "px";
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    return { el, tag, h, alarm: 0, gone: false, dir: -1, x };
  };
  const swimTo = (k, x, secs) => {
    k.dir = x < k.x ? -1 : 1;
    k.el.classList.toggle("rtl", k.dir < 0);
    k.el.style.transition = `left ${secs}s ${secs < 4 ? "ease-out" : "ease-in-out"}`;
    k.el.style.left = x + "px";
    k.x = x;
  };
  // Back and forth across the right of the tank, unhurried.
  const prowl = () => {
    if (calm || document.hidden) return;
    for (const k of prowling()) {
      if (k.alarm && Date.now() - k.alarm < 3500) continue;
      const a = innerWidth * .42, b = innerWidth - SW - 30;
      swimTo(k, k.dir < 0 ? b : a, rnd(11, 15));
    }
  };
  if (!calm) festiveTimers.push(setInterval(prowl, 14000));
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!sharks.has(h.id)) {
      const k = makeShark(h, innerWidth - SW - 40 - sharks.size * 260);
      k.el.classList.add("rtl");
      sharks.set(h.id, k);
    }
    for (const h of held) { const k = sharks.get(h.id); if (k && !k.gone) { k.h = h; if (!k.alarm || Date.now() - k.alarm > 9000) intruderTagEl(h, "held", k.tag); } }
    for (const h of cleared) {
      const k = sharks.get(h.id); if (!k || k.gone) continue;
      k.gone = true;
      if (calm || document.hidden) { k.el.remove(); sharks.delete(h.id); place(); continue; }
      // It swims off; the device comes in as a fish instead.
      intruderTagEl(k.h, "cleared", k.tag);
      festiveTimers.push(setTimeout(() => {
        k.tag.remove();
        swimTo(k, -SW - 60, 4);
        festiveTimers.push(setTimeout(() => { k.el.remove(); sharks.delete(h.id); place(); if (!h.test) fishArrives(h); }, 4200));
      }, 2200));
    }
    place();
  }
  // The alarm: in it charges, everything else scatters.
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let k = sharks.get(h.id);
    if (!k) { k = makeShark(h, innerWidth - SW - 40); sharks.set(h.id, k); place(); }
    if (calm || document.hidden) return;
    k.alarm = Date.now();
    intruderTagEl(h, "alarm", k.tag);
    k.el.style.transition = "none"; k.el.style.left = (innerWidth + 30) + "px"; k.x = innerWidth + 30; void k.el.offsetWidth;
    place();
    swimTo(k, innerWidth * .55, 2.6);
    festiveTimers.push(setTimeout(() => { if (!k.gone) intruderTagEl(k.h, "held", k.tag); }, 9000));
    // The water flushes red.
    const red = document.createElement("div");
    red.className = "aq-red";
    bg.appendChild(red);
    festiveTimers.push(setTimeout(() => red.remove(), 6500));
    // Every fish in the tank bolts, the turtle and the octopus too.
    for (const f of bg.querySelectorAll(".aq-fish:not(.aq-shark), .aq-oct")) for (const a of f.getAnimations()) a.playbackRate = 7;
    // The crab digs in, the eel's gone back into the wreck, the clam shuts.
    document.querySelectorAll(".aq-crab").forEach(cr => { cr.classList.add("dig"); festiveTimers.push(setTimeout(() => cr.classList.remove("dig"), 12000)); });
    eel?.classList.remove("peek"); clam?.classList.remove("open");
    bubbles(14);
    if (soundOn()) { bubbleSound(6, .2); festiveTimers.push(setTimeout(() => bubbleSound(6, .14), 500)); }
  };
  syncIntruders();

  // A new device swims in as a fish, with its name: once it's been marked
  // known, since until then it's the shark.
  const fishArrives = h => {
    if (document.hidden) return;
    if (soundOn()) bubbleSound(5, .18);
    const [b, f] = pick(AQ_COLOURS);
    const el = document.createElement("div");
    el.className = "aq-newfish";
    el.style.top = (c.below() + 30) + "px";
    el.innerHTML = AQ_FISH(b, f).replace('width="46" height="24"', 'width="70" height="36"') + `<span class="lbl">${esc(h ? nameOrIp(h) : "a new device")}</span>`;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    root.appendChild(el);
  };
  festiveHooks.rendered = () => {
    syncIntruders();
    const down = new Set(wentOffIds);
    for (const id of wentOffIds) {
      const tr = document.querySelector(`tr[data-id="${id}"]`);
      if (!tr) continue;
      tr.classList.add("aq-sink");
      festiveTimers.push(setTimeout(() => tr.classList.remove("aq-sink"), 2500));
    }
    wentOffIds = [];
    // A pet fish beside each watched device; it floats still when the device is down.
    for (const h of hosts) {
      if (!h.watched) continue;
      const cell = document.querySelector(`tr[data-id="${h.id}"] td.hostname`);
      if (!cell || cell.querySelector(".aq-pet")) continue;
      const p = document.createElement("span");
      p.className = "aq-pet" + (h.online && !down.has(h.id) ? "" : " still");
      p.title = h.online ? "Swimming: online" : "Floating: offline";
      p.innerHTML = `<svg width="18" height="10" viewBox="0 0 46 24">${AQ_FISH("#ff9f43", "#e8662a").replace(/^<svg[^>]*>|<\/svg>$/g, "")}</svg>`;
      cell.querySelector(".name-text")?.after(p);
    }
  };
  // A shell marks unknown devices on the reef.
  festiveHooks.decorateNode = (g, n) => {
    if (n.type !== "dev") return;
    const s = svgEl("g", { class: "aq-shell t-deco-icon", transform: "translate(11,-24)" });
    s.appendChild(svgEl("path", { d: "M1 11Q0 2 7 1Q14 2 13 11Z", fill: "#ffd9c7", stroke: "#b8735a", "stroke-width": .8 }));
    s.appendChild(svgEl("path", { d: "M7 11V2M4 11L5 3M10 11L9 3", stroke: "#b8735a", "stroke-width": .7 }));
    g.appendChild(s);
  };
  festiveHooks.rendered();
}

BAMF.registerTheme("aquarium", ctx => buildAquarium(ctx.root, ctx.switched));
})();
