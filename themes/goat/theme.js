// Goat: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- Goat ----
// An alpine pasture behind the page and a herd along the bottom. Each goat
// has a mind of its own: it wanders, grazes, pronks and bleats. Now and then
// two square up and butt heads, and one leaps across the tops of the stats
// cards, stopping to take a bite out of one. A finished scan gets a bleat, an
// offline device's row is headbutted, watched devices wear a bell, the scan
// bar is grass being eaten, and on the Map a goat stands on the router. When
// everything is online, the herd celebrates.
// A new device nobody has marked known is an intruder: a wolf. It slinks in at
// the edge of the pasture; the herd bunches together, bells clanging, bleating
// the alarm, and the ram squares up and charges it back. Then it lurks at the
// edge, eyes glinting, tagged, until the device is marked known: then it slinks
// off, and the device arrives as a kid goat instead.
// One goat, facing right, in a 64 x 56 box. Its parts are classed so CSS
// can walk its legs, lower its head to graze, flick its ear and wag its tail.
function goatInner({ coat = "#f4f0e6", shade = "#d8cfbd", patch = null, horns = true } = {}) {
  const s = "#5b4a36";
  return `<g class="g-back-legs"><path class="leg l1" d="M19 34v16"/><path class="leg l2" d="M24 35v15"/></g>
    <g class="g-front-legs"><path class="leg l3" d="M38 34v16"/><path class="leg l4" d="M43 33v17"/></g>
    <path class="g-tail" d="M12.5 24l-5-7 7 4z" fill="${coat}" stroke="${s}" stroke-width="1"/>
    <ellipse cx="28" cy="28" rx="17.5" ry="10.5" fill="${coat}" stroke="${s}" stroke-width="1.3"/>
    ${patch ? `<path d="M18 21q7-5 13 1q-1 9-9 9q-6-2-4-10z" fill="${patch}" opacity=".9"/>` : ""}
    <path d="M17 34q11 5 22 0" fill="none" stroke="${shade}" stroke-width="2"/>
    <g class="g-head">
      <path d="M39 26q3-9 7-15l7 3q-3 8-6 13z" fill="${coat}" stroke="${s}" stroke-width="1.2"/>
      <path d="M45 9q6-4 11 1l5 6q1 3-2 4.5l-8 1q-5-2-6.5-7.5z" fill="${coat}" stroke="${s}" stroke-width="1.2"/>
      ${horns ? `<path d="M48 8.5q-2-7-10-7.5q5 2 7 8.5" fill="#b39a74" stroke="${s}" stroke-width=".9"/>` : ""}
      <path class="g-ear" d="M46.5 11.5q-6 .5-9 4q6 .5 9-2z" fill="${shade}" stroke="${s}" stroke-width=".8"/>
      <path d="M55 21l1.5 6.5-3.5-6z" fill="${shade}" stroke="${s}" stroke-width=".6"/>
      <circle cx="52.5" cy="11.5" r="1.8" fill="#fff" stroke="${s}" stroke-width=".5"/><rect x="51.3" y="11.1" width="2.4" height=".9" rx=".3" fill="#222"/>
      <circle cx="59.5" cy="17" r=".7" fill="${s}"/>
    </g>`;
}

// A wolf, facing right in a 70 x 48 box, its legs classed like a goat's so it
// walks the same way; lean, grey, ears up, tail low.
function wolfInner(night) {
  const fur = night ? "#2f343c" : "#6b7078", shade = night ? "#1f232a" : "#4b5058", eye = night ? "#ffd24a" : "#f2c14a";
  return `<g class="g-back-legs"><path class="leg l1" d="M16 30v15"/><path class="leg l2" d="M21 31v14"/></g>
    <g class="g-front-legs"><path class="leg l3" d="M41 30v15"/><path class="leg l4" d="M46 29v16"/></g>
    <path class="w-tail" d="M10 21Q-1 25 2 39Q6 31 12 28z" fill="${fur}" stroke="${shade}" stroke-width="1"/>
    <path d="M9 22Q11 14 26 15L45 16Q53 17 52 27Q50 33 41 33L15 33Q8 31 9 22z" fill="${fur}" stroke="${shade}" stroke-width="1.2"/>
    <path d="M24 15.5l2-4 2 4 2-4 2 4 2-3.5 2 3.5" fill="none" stroke="${fur}" stroke-width="1.6" stroke-linejoin="round"/>
    <path d="M15 31q13 4 26 0" fill="none" stroke="${shade}" stroke-width="2"/>
    <g class="w-head">
      <path d="M45 19Q51 11 57 12.5L67 17.5Q69.5 20 65.5 21.5L57 23.5Q50 25.5 46.5 23.5z" fill="${fur}" stroke="${shade}" stroke-width="1.1"/>
      <path d="M49.5 13L51.5 4.5L55 12z M54 12.5L57.5 5.5L58.5 13z" fill="${fur}" stroke="${shade}" stroke-width=".8"/>
      <path d="M58 20.5L66 19.5" stroke="${shade}" stroke-width=".9"/>
      <circle class="w-eye" cx="56.5" cy="15.6" r="1.4" fill="${eye}"/>
      <circle cx="67" cy="18.4" r="1.2" fill="#111"/>
    </g>`;
}

const goatSvg = (w, look) => `<svg class="goat-svg" width="${w}" height="${Math.round(w * 56 / 64)}" viewBox="0 0 64 56">${goatInner(look)}</svg>`;

const GOAT_COATS = [
  { coat: "#f4f0e6", shade: "#d8cfbd" },
  { coat: "#b98252", shade: "#8e5f38" },
  { coat: "#f4f0e6", shade: "#d8cfbd", patch: "#4a3a2c" },
  { coat: "#3f362d", shade: "#2a231d", patch: "#e9e2d2" },
  { coat: "#d9c2a0", shade: "#b59c78" },
];

// Just the head, for the scan bar, the search box, the peek and the headbutt.
const GOAT_HEAD = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="36 0 28 30">${goatInner(GOAT_COATS[0]).replace(/[\s\S]*<g class="g-head">/, "<g>")}</svg>`;

// The goats' voice: a bleat made in the browser, so there's no audio file.
// A sawtooth through "aah" formants, starting low for the b and falling off
// at the end, with the fast wobble a goat's voice has. Off until the speaker
// button turns it on; browsers only allow sound after a click anyway.
let goatAudio = null;

function goatBleatSound({ pitch = 1, length = .8, volume = .5 } = {}) {
  try { goatAudio = goatAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return; }
  const ctx = goatAudio;
  if (ctx.state === "suspended") ctx.resume?.().catch(() => {});
  const t = ctx.currentTime + .02, f0 = 320 * pitch * (.92 + Math.random() * .16), wob = 7 + Math.random() * 3;
  const osc = ctx.createOscillator();
  osc.type = "sawtooth";
  osc.frequency.setValueAtTime(f0 * .78, t);
  osc.frequency.exponentialRampToValueAtTime(f0, t + .08);
  osc.frequency.setValueAtTime(f0, t + length * .65);
  osc.frequency.exponentialRampToValueAtTime(f0 * .86, t + length);
  const vib = ctx.createOscillator(), vibDepth = ctx.createGain();
  vib.frequency.value = wob; vibDepth.gain.value = f0 * .07;
  vib.connect(vibDepth).connect(osc.frequency);
  const trem = ctx.createOscillator(), tremDepth = ctx.createGain(), tremGain = ctx.createGain();
  trem.frequency.value = wob; tremDepth.gain.value = .3; tremGain.gain.value = .7;
  trem.connect(tremDepth).connect(tremGain.gain);
  const env = ctx.createGain();
  env.gain.setValueAtTime(0, t);
  env.gain.linearRampToValueAtTime(volume, t + .05);
  env.gain.setValueAtTime(volume, t + length * .7);
  env.gain.linearRampToValueAtTime(0, t + length);
  const mix = ctx.createGain();
  for (const [f, q, g] of [[760, 5, 1], [1250, 6, .7], [2650, 8, .3]]) {
    const bp = ctx.createBiquadFilter(), gg = ctx.createGain();
    bp.type = "bandpass"; bp.frequency.value = f * (pitch > 1.3 ? 1.18 : 1); bp.Q.value = q; gg.gain.value = g;
    osc.connect(bp).connect(gg).connect(mix);
  }
  mix.connect(tremGain).connect(env).connect(ctx.destination);
  for (const o of [osc, vib, trem]) { o.start(t); o.stop(t + length + .05); }
}

// A cricket, for Goat Night: three quick high chirps.
function cricketSound() {
  try { goatAudio = goatAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return; }
  const ctx = goatAudio, f = 4200 + Math.random() * 600;
  for (let i = 0; i < 3; i++) {
    const t = ctx.currentTime + .03 + i * .11, o = ctx.createOscillator(), g = ctx.createGain();
    o.type = "sine"; o.frequency.value = f;
    g.gain.setValueAtTime(0, t);
    g.gain.linearRampToValueAtTime(.05, t + .012);
    g.gain.linearRampToValueAtTime(0, t + .06);
    o.connect(g).connect(ctx.destination);
    o.start(t); o.stop(t + .07);
  }
}

function buildGoat(root, switched, night = false) {
  const calm = calmMotion();
  const html = document.documentElement;
  html.style.setProperty("--goat-head", `url("data:image/svg+xml,${encodeURIComponent(GOAT_HEAD)}")`);
  festiveStops.push(() => html.style.removeProperty("--goat-head"));
  const sleep = ms => new Promise(res => festiveTimers.push(setTimeout(res, ms)));

  // The pasture: clouds, mountains with snow, pines, a crag with a goat on it.
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg goat-bg" + (night ? " night" : "");
  bg.setAttribute("aria-hidden", "true");
  // Day, or the same hills by moonlight.
  const K = night
    ? { sun: "#f4f1dc", far: "#1e2a3c", snow: "#aab6c8", near: "#18241f", grass: "#1f3318", crag: "#3a3a40", cragLine: "#2a2a30", pine: "#12201a", trunk: "#2a2118", front: "#1a2c14" }
    : { sun: "#fff4c2", far: "#9fb8c9", snow: "#fff", near: "#7d9a86", grass: "#8fbf5a", crag: "#8c8579", cragLine: "#6e685e", pine: "#3f6b3a", trunk: "#5b4a36", front: "#79a94a" };
  const pines = [[80, 610], [140, 640], [1330, 600], [1400, 630], [1460, 610], [620, 650], [690, 665]]
    .map(([x, y]) => `<g transform="translate(${x} ${y})"><path d="M0 0l-26 60h52z M0 -30l-20 48h40z M0 -56l-14 38h28z" fill="${K.pine}"/><rect x="-4" y="60" width="8" height="14" fill="${K.trunk}"/></g>`).join("");
  // Stars only come out at night.
  const stars = night ? Array.from({ length: 70 }, (_, i) =>
    `<i class="goat-star" style="left:${(i * 37.3) % 100}vw;top:${(i * 53.7) % 46}vh;animation-duration:${(1.4 + (i % 5) * .6).toFixed(1)}s;animation-delay:-${i % 7}s;${i % 9 ? "" : "width:3px;height:3px;"}"></i>`).join("") : "";
  bg.innerHTML = stars + [["6vh", 120, -30], ["16vh", 170, -100], ["10vh", 140, -60]]
    .map(([top, s, d]) => `<div class="goat-cloud" style="top:${top};animation-duration:${s}s;animation-delay:${d}s"></div>`).join("") +
    `<svg class="scene goat-svg" viewBox="0 0 1600 900" preserveAspectRatio="xMidYMax slice">
      ${night ? `<circle cx="1320" cy="150" r="80" fill="#f4f1dc" opacity=".12"/><circle cx="1320" cy="150" r="52" fill="#f4f1dc"/>
        <circle cx="1302" cy="138" r="9" fill="#dcd8c0"/><circle cx="1336" cy="168" r="6" fill="#dcd8c0"/><circle cx="1330" cy="128" r="4" fill="#dcd8c0"/>`
        : `<circle cx="1320" cy="150" r="60" fill="${K.sun}" opacity=".9"/>`}
      <path d="M0 520L180 300L300 420L470 210L640 430L800 260L980 470L1150 230L1330 440L1480 300L1600 400V900H0z" fill="${K.far}"/>
      <path d="M470 210L420 272L452 262L476 286L500 258L530 268z M1150 230L1100 290L1130 282L1152 304L1178 276L1210 286z M800 260L760 312L790 304L806 322L826 300L850 308z" fill="${K.snow}"/>
      <path d="M0 640L220 470L400 580L600 450L820 600L1040 470L1240 590L1450 480L1600 560V900H0z" fill="${K.near}"/>
      <path d="M0 720Q400 620 800 700T1600 680V900H0z" fill="${K.grass}"/>
      <path d="M1180 700l40-120 70-40 60 30 30 90 20 40z" fill="${K.crag}" stroke="${K.cragLine}" stroke-width="3"/>
      <g class="crag-goat" transform="translate(1262 492) scale(1.3)">${goatInner(GOAT_COATS[0])}</g>
      ${night ? `<g opacity=".9"><rect x="560" y="690" width="70" height="46" fill="#3a1512"/><path d="M552 692L595 660L638 692z" fill="#2a0e0c"/>
        <rect x="586" y="704" width="18" height="16" fill="#f5d77a" opacity=".85"/><rect x="586" y="704" width="18" height="16" fill="none" stroke="#2a0e0c" stroke-width="2"/><path d="M595 704v16M586 712h18" stroke="#2a0e0c" stroke-width="1.5"/></g>` : ""}
      ${pines}
      <path d="M0 800Q400 740 800 790T1600 770V900H0z" fill="${K.front}"/>
    </svg>`;
  // Fireflies over the meadow at night.
  if (night) bg.insertAdjacentHTML("beforeend", Array.from({ length: 16 }, (_, i) =>
    `<i class="goat-fly" style="left:${(i * 61.7) % 96 + 2}vw;bottom:${6 + (i * 13) % 26}vh;animation-duration:${(5 + (i % 4) * 1.5).toFixed(1)}s, ${(1.2 + (i % 3) * .5).toFixed(1)}s;animation-delay:-${i % 6}s, -${i % 4}s"></i>`).join(""));
  document.body.prepend(bg);
  root.insertAdjacentHTML("beforeend", `<div class="goat-meadow${night ? " night" : ""}"></div>`);

  // A speech bubble over something.
  const say = (x, y, text) => {
    const b = document.createElement("div");
    b.className = "goat-bubble";
    b.textContent = text;
    b.style.cssText = `left:${Math.round(x)}px;top:${Math.round(y)}px`;
    root.appendChild(b);
    festiveTimers.push(setTimeout(() => b.remove(), 2000));
  };
  const boom = (x, y) => {
    const b = document.createElement("div");
    b.className = "goat-boom";
    b.style.cssText = `left:${Math.round(x)}px;top:${Math.round(y)}px`;
    b.innerHTML = `<svg width="70" height="50" viewBox="0 0 70 50"><path d="M35 2l6 12 13-6-4 13 15 3-13 8 8 11-14-2-3 13-8-11-9 10-1-14-14 3 9-11L5 24l14-4-3-13 13 7z" fill="#ffd84d" stroke="#9e2b25" stroke-width="2"/>
      <text x="35" y="31" text-anchor="middle" font-family="Space Grotesk, sans-serif" font-weight="800" font-size="12" fill="#9e2b25">BONK!</text></svg>`;
    root.appendChild(b);
    festiveTimers.push(setTimeout(() => b.remove(), 950));
  };

  // Sound: the speaker button beside the theme button. Off until it's
  // clicked, remembered in this browser. The herd's idle bleats are rationed
  // to one every eight seconds or so; the ones that mark an event always sound.
  let soundOn = false;
  try { soundOn = localStorage.getItem("bamf-goat-sound") === "1"; } catch {}
  let lastVoice = 0;
  const voice = (pitch, always) => {
    if (!soundOn || document.hidden) return;
    const now = Date.now();
    if (!always && now - lastVoice < 8000) return;
    lastVoice = now;
    goatBleatSound({ pitch, length: pitch > 1.3 ? .55 : rnd(.7, .95) });
  };
  const speaker = document.createElement("button");
  speaker.type = "button";
  speaker.className = "arp-toggle goat-sound";
  const showSpeaker = () => {
    speaker.textContent = soundOn ? "🔊" : "🔇";
    speaker.title = soundOn ? "Goat sounds on: click to mute" : "Goat sounds off: click to let the goats baaah";
    speaker.setAttribute("aria-pressed", String(soundOn));
  };
  showSpeaker();
  speaker.onclick = () => {
    soundOn = !soundOn;
    try { localStorage.setItem("bamf-goat-sound", soundOn ? "1" : "0"); } catch {}
    showSpeaker();
    if (soundOn) { lastVoice = 0; voice(1, true); }
  };
  $("themeToggle")?.before(speaker);
  festiveStops.push(() => speaker.remove());
  if (night && !calm) festiveTimers.push(setInterval(() => {
    if (soundOn && !document.hidden && Math.random() < .6) cricketSound();
  }, 7000));

  // The herd. Each goat walks with a CSS transition on left; its actions are
  // promises, so a goat's life reads top to bottom.
  const W = () => innerWidth;
  const herd = [];
  let alive = true;
  festiveStops.push(() => { alive = false; });
  const makeGoat = (look, w, x) => {
    const el = document.createElement("div");
    el.className = "goat-agent";
    el.innerHTML = goatSvg(w, look);
    el.style.left = x + "px";
    root.appendChild(el);
    const g = { el, x, w, h: Math.round(w * 56 / 64), busy: false };
    g.face = dir => el.classList.toggle("left", dir < 0);
    g.walkTo = (to, speed = 40) => {
      to = Math.max(-w - 20, Math.min(W() + 20, to));
      const d = Math.abs(to - g.x);
      if (d < 2) return Promise.resolve();
      g.face(to > g.x ? 1 : -1);
      el.classList.add(speed > 90 ? "running" : "walking");
      el.style.transition = `left ${(d / speed).toFixed(2)}s linear`;
      el.style.left = to + "px";
      g.x = to;
      return sleep(d / speed * 1000).then(() => el.classList.remove("walking", "running"));
    };
    g.pose = (cls, ms) => { el.classList.add(cls); return sleep(ms).then(() => el.classList.remove(cls)); };
    g.bleat = (text, always) => {
      say(g.x + w * .55, innerHeight - 12 - g.h - 34, text || pick(["Baaah!", "Baa-aa-aah!", "BAAAH!", "Baaaah.", "Meh-eh-eh!", "Maa?"]));
      voice(w < 50 ? 1.7 : rnd(.9, 1.15), always);
    };
    return g;
  };
  const life = async g => {
    while (alive && document.body.contains(g.el)) {
      if (g.busy || document.hidden) { await sleep(800); continue; }
      const r = Math.random();
      if (r < .42) await g.walkTo(rnd(10, W() - g.w - 10), rnd(24, 42));
      else if (r < .78) await g.pose("grazing", rnd(2500, 6500));
      else if (r < .88) await g.pose("pronk", 700);
      else { g.bleat(); await sleep(1200); }
      await sleep(rnd(300, 1400));
    }
  };
  const count = Math.max(3, Math.min(5, Math.round(W() / 330)));
  for (let i = 0; i < count; i++) {
    const g = makeGoat(GOAT_COATS[i % GOAT_COATS.length], i % 3 === 2 ? 56 : 64, rnd(20, W() - 90));
    if (Math.random() < .5) g.face(-1);
    herd.push(g);
    if (calm) g.el.classList.add("grazing"); else life(g);
  }

  // Two goats square up and butt heads.
  const headbutt = async () => {
    const free = herd.filter(g => !g.busy);
    if (document.hidden || free.length < 2) return;
    const [a, b] = free.sort(() => Math.random() - .5);
    a.busy = b.busy = true;
    const mid = rnd(W() * .25, W() * .7);
    await Promise.all([a.walkTo(mid - a.w - 26, 70), b.walkTo(mid + 26, 70)]);
    a.face(1); b.face(-1);
    await sleep(500);
    await Promise.all([a.pose("rear", 520), b.pose("rear", 520)]);
    await Promise.all([a.walkTo(mid - a.w + 6, 260), b.walkTo(mid - 6, 260)]);
    boom(mid, innerHeight - 12 - a.h * .7);
    await Promise.all([a.walkTo(mid - a.w - 40, 120), b.walkTo(mid + 40, 120)]);
    await sleep(700);
    if (Math.random() < .5) a.bleat("Hmph.", true); else b.bleat("BAAAH!", true);
    await sleep(900);
    a.busy = b.busy = false;
  };

  // One goat leaps across the tops of the stats cards, takes a bite out of
  // one, and jumps down the far side.
  const parkour = () => {
    const cards = [...document.querySelectorAll(".stats .stat")].map(c => ({ el: c, r: c.getBoundingClientRect() }));
    if (document.hidden || cards.length < 2 || cards[0].r.top < 40 || cards[0].r.bottom > innerHeight) return;
    const w = 58, h = Math.round(w * 56 / 64), ground = innerHeight - 12 - h;
    const el = document.createElement("div");
    el.className = "goat-leaper running";
    el.innerHTML = goatSvg(w, pick(GOAT_COATS));
    root.appendChild(el);
    const pts = [[-80, ground], [cards[0].r.left - 90, ground]];
    const biteAt = Math.min(2, cards.length - 1);
    cards.forEach((c, i) => {
      const y = c.r.top - h + 3;
      const land = i === biteAt ? c.r.right - w + 4 : c.r.left + c.r.width * .35;
      pts.push({ arc: true, x: land, y });
      if (i === biteAt) pts.push({ hold: 1600, x: land, y });
      else pts.push([Math.min(c.r.right - w, land + 60), y]);
    });
    const last = cards[cards.length - 1].r;
    pts.push({ arc: true, x: last.right + 50, y: ground }, [W() + 90, ground]);
    // Keyframes with a peak between each jump; timing from the distance.
    const frames = [], times = [];
    let t = 0, prev = [pts[0][0], pts[0][1]];
    const push = (x, y, dt) => { t += dt; frames.push({ transform: `translate(${x.toFixed(0)}px, ${y.toFixed(0)}px)` }); times.push(t); };
    push(prev[0], prev[1], 0);
    let holdStart = 0;
    for (const p of pts.slice(1)) {
      const x = p.x ?? p[0], y = p.y ?? p[1];
      if (p.hold) { holdStart = t; push(x, y, p.hold); prev = [x, y]; continue; }
      const d = Math.hypot(x - prev[0], y - prev[1]);
      if (p.arc) {
        const peak = Math.min(prev[1], y) - 60;
        push((prev[0] + x) / 2, peak, d / 0.9 / 2 + 150);
        push(x, y, d / 0.9 / 2 + 150);
      } else push(x, y, d / 0.28);
      prev = [x, y];
    }
    const total = t;
    frames.forEach((f, i) => { f.offset = times[i] / total; });
    const anim = el.animate(frames, { duration: total, easing: "linear", fill: "forwards" });
    // At the bite: head down, and a bite out of the card that grows back.
    festiveTimers.push(setTimeout(() => {
      el.classList.remove("running"); el.classList.add("grazing");
      const card = cards[biteAt].el;
      festiveTimers.push(setTimeout(() => card.classList.add("goat-bitten"), 700));
      festiveTimers.push(setTimeout(() => card.classList.remove("goat-bitten"), 25000));
      festiveTimers.push(setTimeout(() => { el.classList.remove("grazing"); el.classList.add("running"); }, 1500));
    }, holdStart));
    festiveStops.push(() => document.querySelectorAll(".stat.goat-bitten").forEach(c => c.classList.remove("goat-bitten")));
    anim.onfinish = () => el.remove();
  };

  if (!calm) {
    const every = (fn, first, a, b) => { const next = f => festiveTimers.push(setTimeout(() => { fn(); next(false); }, f ? first : rnd(a, b))); next(true); };
    every(headbutt, rnd(25e3, 45e3), 70e3, 140e3);
    every(parkour, rnd(12e3, 25e3), 80e3, 160e3);
  }

  // A scan finishes: somebody bleats.
  festiveHooks.scanDone = () => { if (herd.length) pick(herd).bleat(null, true); };

  // ---- intruders: wolves at the edge of the pasture ----
  const watchIn = intruderWatch();
  const wolves = new Map();             // id -> { h, g, tag, alarm, gone }
  const SHOW = 2;
  const wolfX = k => W() - 100 - k * 74;
  const makeWolf = (h, x) => {
    const g = makeGoat(GOAT_COATS[0], 70, x);
    g.el.classList.add("goat-wolf");
    g.el.innerHTML = `<svg class="goat-svg wolf${night ? " night" : ""}" width="70" height="48" viewBox="0 0 70 48">${wolfInner(night)}</svg>`;
    g.h = 48; g.busy = true;
    const tag = intruderTagEl(h, "held");
    g.el.appendChild(tag);
    return { h, g, tag, alarm: 0, gone: false };
  };
  // Where each wolf lurks: the newest nearest the edge.
  // One stood down keeps its place until it slinks off, so none lands on it.
  const lurking = () => [...wolves.values()].filter(w => !w.leaving).sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  // While a wolf is about, the page gets room at the bottom to scroll clear of
  // it and its tag, the way a theme's floor does.
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const settle = () => lurking().forEach((w, k) => {
    room.style.height = wolves.size ? "124px" : "0";
    w.g.el.hidden = k >= SHOW;
    if (w.gone) return;
    // Tags stack, the nearer wolf's lower, so two never overlap.
    w.tag.style.bottom = `calc(100% + ${8 + k * 52}px)`;
    if (k >= SHOW || w.moving) return;
    if (calm) { w.g.el.style.left = wolfX(k) + "px"; w.g.x = wolfX(k); w.g.face(-1); w.g.el.classList.add("lurk"); return; }
    w.g.walkTo(wolfX(k), 60).then(() => { w.g.face(-1); w.g.el.classList.add("lurk"); });
  });
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!wolves.has(h.id)) {
      const w = makeWolf(h, wolfX(Math.min(wolves.size, SHOW - 1)));
      w.g.face(-1); w.g.el.classList.add("lurk");
      wolves.set(h.id, w);
    }
    for (const h of held) { const w = wolves.get(h.id); if (w && !w.gone) { w.h = h; if (!w.alarm || Date.now() - w.alarm > 9000) intruderTagEl(h, "held", w.tag); } }
    for (const h of cleared) {
      const w = wolves.get(h.id); if (!w || w.gone) continue;
      w.gone = true;
      if (calm || document.hidden) { w.g.el.remove(); wolves.delete(h.id); if (!wolves.size) room.style.height = "0"; continue; }
      // It slinks off; the device comes in as a kid goat instead.
      intruderTagEl(w.h, "cleared", w.tag);
      (async () => {
        await sleep(2200);
        w.tag.remove(); w.g.el.classList.remove("lurk");
        w.leaving = true; settle();
        await w.g.walkTo(W() + 90, 90);
        w.g.el.remove(); wolves.delete(h.id);
        if (!h.test) kidArrives(h);
        settle();
        if (!wolves.size) room.style.height = "0";
      })();
    }
  }
  const redFlush = () => {
    const r = document.createElement("div");
    r.className = "goat-red";
    root.appendChild(r);
    festiveTimers.push(setTimeout(() => r.remove(), 6500));
  };
  // The alarm: the wolf slinks in, the herd bunches and bleats, bells going,
  // and the ram charges it back to the edge.
  festiveHooks.intruder = async h => {
    if (!h) return;
    syncIntruders();
    let w = wolves.get(h.id);
    if (!w) { w = makeWolf(h, wolfX(0)); wolves.set(h.id, w); }
    if (calm || document.hidden) { settle(); return; }
    w.alarm = Date.now(); w.moving = true;
    intruderTagEl(h, "alarm", w.tag);
    w.g.el.classList.remove("lurk");
    w.g.el.style.transition = "none"; w.g.el.style.left = (W() + 20) + "px"; w.g.x = W() + 20; void w.g.el.offsetWidth;
    settle();
    const stop = W() - 250;
    await w.g.walkTo(stop, 70);
    w.g.face(-1);
    redFlush();
    // The herd sees it: they bunch together, facing it, and sound the alarm.
    const flock = herd.filter(g => document.body.contains(g.el));
    flock.forEach(g => { g.busy = true; });
    const mid = W() * .3;
    await Promise.all(flock.map((g, i) => g.walkTo(mid + (i - flock.length / 2) * 42, 170).then(() => g.face(1))));
    bells(); flock.slice(0, 3).forEach((g, i) => festiveTimers.push(setTimeout(() => g.bleat(i ? "Baa!!" : "WOLF!", true), i * 300)));
    await sleep(900);
    // The ram: the biggest goat, horns and all.
    const ram = flock.find(g => g.w >= 64) || flock[0];
    if (ram) {
      await ram.walkTo(stop - ram.w - 60, 190);
      ram.face(1);
      await ram.pose("rear", 520);
      await ram.walkTo(stop - ram.w + 8, 320);
      boom(stop, innerHeight - 12 - 30);
      voice(.9, true);
      w.moving = false;
      settle();
      await ram.walkTo(stop - ram.w - 90, 100);
      await sleep(900);
      ram.bleat("Hmph.", true);
    } else { w.moving = false; settle(); }
    await sleep(1200);
    flock.forEach(g => { g.busy = false; });
    festiveTimers.push(setTimeout(() => { if (!w.gone) intruderTagEl(w.h, "held", w.tag); }, 3000));
  };
  // Bells on the herd, clanging.
  const bells = () => {
    if (!soundOn || document.hidden) return;
    try { goatAudio = goatAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return; }
    const ctx = goatAudio;
    for (let k = 0; k < 7; k++) {
      const t = ctx.currentTime + .02 + k * .16 + Math.random() * .05;
      for (const [m, a] of [[1, .05], [2.4, .02]]) {
        const o = ctx.createOscillator(), g = ctx.createGain();
        o.type = "triangle"; o.frequency.value = (k % 2 ? 1150 : 980) * m;
        g.gain.setValueAtTime(a, t); g.gain.exponentialRampToValueAtTime(.0005, t + .5);
        o.connect(g).connect(ctx.destination); o.start(t); o.stop(t + .55);
      }
    }
  };
  syncIntruders();
  settle();

  // A new device arrives as a kid goat, pronking, with its name: once it's
  // been marked known, since until then it's the wolf.
  const kidArrives = async h => {
    if (document.hidden) return;
    const kid = makeGoat({ coat: "#fbf8f1", shade: "#e2d9c6", horns: false }, 38, -50);
    kid.busy = true;
    const tag = document.createElement("div");
    tag.className = "goat-tag";
    tag.textContent = `Baa! ${h ? nameOrIp(h) : "New device"}`;
    kid.el.appendChild(tag);
    await kid.walkTo(W() * .3, 150);
    voice(1.8, true);
    for (let i = 0; i < 3; i++) await kid.pose("pronk", 700);
    await sleep(3000);
    tag.remove();
    await kid.walkTo(W() + 60, 120);
    kid.el.remove();
  };
  // The search box: a goat munching beside it while you type.
  const search = $("search");
  const munch = document.createElement("span");
  munch.className = "goat-munch";
  munch.setAttribute("aria-hidden", "true");
  search?.before(munch);
  const onInput = () => { munch.classList.remove("chewing"); void munch.offsetWidth; munch.classList.add("chewing"); };
  search?.addEventListener("input", onInput);
  festiveStops.push(() => { munch.remove(); search?.removeEventListener("input", onInput); });

  let allUp = null;
  festiveHooks.rendered = () => {
    syncIntruders();
    // Offline devices: their row gets headbutted.
    for (const id of wentOffIds) {
      const tr = document.querySelector(`tr[data-id="${id}"]`);
      const cell = tr?.querySelector("td.hostname");
      if (!cell) continue;
      const b = document.createElement("span");
      b.className = "goat-bonk";
      cell.appendChild(b);
      festiveTimers.push(setTimeout(() => { tr.classList.add("goat-shake"); }, 500));
      festiveTimers.push(setTimeout(() => { tr.classList.remove("goat-shake"); b.remove(); }, 1400));
    }
    wentOffIds = [];
    // Watched devices wear a bell that rings while they're up.
    for (const h of hosts) {
      if (!h.watched) continue;
      const cell = document.querySelector(`tr[data-id="${h.id}"] td.hostname`);
      if (!cell || cell.querySelector(".goat-bell")) continue;
      const bell = document.createElement("span");
      bell.className = "goat-bell" + (h.online ? "" : " still");
      bell.title = h.online ? "Bell ringing: online" : "Bell silent: offline";
      bell.innerHTML = `<svg width="14" height="16" viewBox="0 0 14 16"><path d="M7 0v2" stroke="#6b4a1a" stroke-width="1.5"/><path d="M2 12q0-9 5-10q5 1 5 10z" fill="#d9a441" stroke="#8a6a33"/>
        <rect x="1" y="11.5" width="12" height="2" rx="1" fill="#b98a2e"/><circle cx="7" cy="14.5" r="1.4" fill="#6b4a1a"/></svg>`;
      cell.querySelector(".name-text")?.after(bell);
    }
    // Everything online: the herd celebrates.
    const live = hosts.filter(h => !h.ignored && !h.forgotten);
    const up = live.length > 0 && live.every(h => h.online);
    if (up && allUp === false) herd.filter(g => !g.busy).forEach((g, i) => festiveTimers.push(setTimeout(() => {
      if (!calm) g.pose("pronk", 700);
      if (i < 3) g.bleat(i ? "Baa!" : "BAAAAH!", true);
    }, i * 350)));
    allUp = up;
  };
  // On the Map, a goat stands on the router: king of the hill.
  festiveHooks.decorateNode = (g, n) => {
    if (!(n.type === "root" || n.isRoot)) return;
    const k = svgEl("g", { class: "goat-king goat-svg", transform: "translate(-17,-44) scale(.53)" });
    k.innerHTML = goatInner(GOAT_COATS[0]);
    g.appendChild(k);
  };
  festiveHooks.rendered();
}

BAMF.registerTheme("goat", ctx => buildGoat(ctx.root, ctx.switched));
BAMF.registerTheme("goatnight", ctx => buildGoat(ctx.root, ctx.switched, true));
})();
