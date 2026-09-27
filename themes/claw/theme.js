// Claw Machine: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// ---- Claw Machine ----
// A prize cabinet. Behind the glass: a heap of plush prizes, a prize chute,
// neon tubes up the sides, and a claw on a rail that plays by itself now and
// then. It usually drops what it grabs, and sometimes wins. Marquee bulbs
// chase under the header and race when a scan finishes. The 🕹 button sends
// a claw down onto one of your devices' rows: win and it carries the device
// off, lose and it slips. A new device is always a win. A device going
// offline gets a TRY AGAIN stamp and one coming back a BONUS. Watched devices
// carry a little bear, and unknown ones are mystery capsules.
const CLAW_COLS = ["#ff7ab6", "#7ae0ff", "#ffd23f", "#b18cff", "#7cf0b0", "#ff9f43", "#f7efff", "#c8a27a", "#ff5d73"];

// Dusk's prizes: the same plush in faded, after-hours colours.
const CLAW_DUSK_COLS = ["#c9a0ae", "#9fbfc4", "#d8c38f", "#a99cc4", "#9fc7ae", "#d3a987", "#e6e1d8", "#b59a80", "#c98a8f"];

const CLAW_TYPES = ["bear", "bunny", "duck", "cat", "frog", "star", "capsule", "ball", "bear", "bunny"];

// One plush, drawn in a 60 by 60 box.
function plushShape(type, c) {
  const eyes = `<circle cx="23" cy="30" r="2.6" fill="#1a0930"/><circle cx="37" cy="30" r="2.6" fill="#1a0930"/>`;
  const cheeks = `<circle cx="18" cy="37" r="3" fill="rgba(255,90,140,.45)"/><circle cx="42" cy="37" r="3" fill="rgba(255,90,140,.45)"/>`;
  switch (type) {
    case "bear": return `<circle cx="14" cy="14" r="9" fill="${c}"/><circle cx="46" cy="14" r="9" fill="${c}"/>
      <circle cx="14" cy="14" r="4.5" fill="rgba(255,255,255,.35)"/><circle cx="46" cy="14" r="4.5" fill="rgba(255,255,255,.35)"/>
      <circle cx="30" cy="32" r="22" fill="${c}"/><ellipse cx="30" cy="39" rx="9" ry="7" fill="rgba(255,255,255,.45)"/><ellipse cx="30" cy="36" rx="3.2" ry="2.4" fill="#1a0930"/>${eyes}`;
    case "bunny": return `<ellipse cx="21" cy="11" rx="6" ry="15" fill="${c}"/><ellipse cx="39" cy="11" rx="6" ry="15" fill="${c}"/>
      <ellipse cx="21" cy="12" rx="2.6" ry="10" fill="#ffc6e0"/><ellipse cx="39" cy="12" rx="2.6" ry="10" fill="#ffc6e0"/>
      <circle cx="30" cy="35" r="20" fill="${c}"/>${eyes}${cheeks}<path d="M28 37h4l-2 2z" fill="#ff5d8f"/>`;
    case "duck": return `<circle cx="30" cy="32" r="22" fill="#ffd23f"/><path d="M26 11q4 -8 8 0" fill="#ffd23f"/>
      <ellipse cx="30" cy="40" rx="10" ry="5" fill="#ff9f43"/><path d="M20 40h20" stroke="#d9771f" stroke-width="1.4"/>${eyes}`;
    case "cat": return `<path d="M10 22L14 4l14 12zM50 22L46 4 32 16z" fill="${c}"/><circle cx="30" cy="33" r="21" fill="${c}"/>${eyes}${cheeks}
      <path d="M27 36l3 2 3-2M30 38v2" fill="none" stroke="#1a0930" stroke-width="1.5" stroke-linecap="round"/>
      <path d="M8 34h10M8 38h10M42 34h10M42 38h10" stroke="rgba(26,9,48,.5)" stroke-width="1"/>`;
    case "frog": return `<circle cx="18" cy="16" r="9" fill="#7cf0b0"/><circle cx="42" cy="16" r="9" fill="#7cf0b0"/>
      <circle cx="18" cy="16" r="4" fill="#fff"/><circle cx="42" cy="16" r="4" fill="#fff"/><circle cx="18" cy="17" r="2" fill="#1a0930"/><circle cx="42" cy="17" r="2" fill="#1a0930"/>
      <ellipse cx="30" cy="36" rx="25" ry="18" fill="#7cf0b0"/><path d="M18 40q12 8 24 0" fill="none" stroke="#1a0930" stroke-width="2" stroke-linecap="round"/>${cheeks}`;
    case "star": return `<path d="M30 4l7.6 15.4 17 2.5-12.3 12 2.9 16.9L30 43l-15.2 8 2.9-16.9L5.4 22l17-2.5z" fill="${c}" stroke="rgba(0,0,0,.15)"/>
      <circle cx="25" cy="27" r="2.2" fill="#1a0930"/><circle cx="35" cy="27" r="2.2" fill="#1a0930"/>
      <path d="M26 33q4 3 8 0" fill="none" stroke="#1a0930" stroke-width="1.6" stroke-linecap="round"/>`;
    case "capsule": return `<circle cx="30" cy="30" r="22" fill="#f7efff"/><path d="M8 30a22 22 0 0 1 44 0z" fill="${c}"/>
      <path d="M8 30h44" stroke="rgba(26,9,48,.45)" stroke-width="2"/><ellipse cx="22" cy="18" rx="6" ry="3.5" fill="rgba(255,255,255,.55)" transform="rotate(-30 22 18)"/>`;
    default: return `<circle cx="30" cy="30" r="21" fill="${c}"/><path d="M9 30q21 -14 42 0M11 38q19 -12 38 0" fill="none" stroke="rgba(255,255,255,.7)" stroke-width="3"/>
      <ellipse cx="22" cy="20" rx="5" ry="3" fill="rgba(255,255,255,.5)"/>`;
  }
}

const plushSvg = (size, type = pick(CLAW_TYPES), c = pick(CLAW_COLS)) =>
  `<svg width="${size}" height="${size}" viewBox="0 0 60 60" aria-hidden="true">${plushShape(type, c)}</svg>`;

const CLAW_SVG = `<svg width="60" height="64" viewBox="0 0 60 64" aria-hidden="true">
  <path class="prong m" d="M30 14V46Q30 53 26 56" fill="none" stroke="#b8acd0" stroke-width="4" stroke-linecap="round"/>
  <path class="prong l" d="M28 14L14 34Q9 44 17 55" fill="none" stroke="#e0d8ee" stroke-width="4.5" stroke-linecap="round"/>
  <path class="prong r" d="M32 14L46 34Q51 44 43 55" fill="none" stroke="#e0d8ee" stroke-width="4.5" stroke-linecap="round"/>
  <rect x="17" y="0" width="26" height="17" rx="5" fill="#e8e0f5" stroke="#6b5a8a" stroke-width="1.5"/>
  <rect class="stripe" x="20" y="4" width="20" height="4" rx="2" fill="#ff4fa3"/><circle cx="30" cy="16" r="3.5" fill="#6b5a8a"/></svg>`;

const clawWait = ms => new Promise(r => setTimeout(r, ms));

// The cabinet's sounds, made in the browser: a coin going in, the gantry
// motor, the clunk of the claw closing, the win jingle, the sad trombone,
// and a bonus blip.
let clawAudio = null;

function clawSound() {
  const ctx = () => {
    try { clawAudio = clawAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return null; }
    if (clawAudio.state === "suspended") clawAudio.resume?.().catch(() => {});
    return clawAudio;
  };
  const tone = (c, f, type, at, len, gain) => {
    const o = c.createOscillator(), g = c.createGain();
    o.type = type; o.frequency.setValueAtTime(f, at);
    g.gain.setValueAtTime(0, at); g.gain.linearRampToValueAtTime(gain, at + .01); g.gain.exponentialRampToValueAtTime(.0001, at + len);
    o.connect(g).connect(c.destination); o.start(at); o.stop(at + len + .05);
  };
  return {
    coin() { const c = ctx(); if (!c) return; const t = c.currentTime; tone(c, 1568, "square", t, .07, .05); tone(c, 2349, "square", t + .08, .3, .05); },
    whir(sec) {
      const c = ctx(); if (!c) return;
      const t = c.currentTime, o = c.createOscillator(), f = c.createBiquadFilter(), g = c.createGain();
      o.type = "sawtooth"; o.frequency.setValueAtTime(68, t); o.frequency.linearRampToValueAtTime(92, t + sec);
      f.type = "lowpass"; f.frequency.value = 480;
      g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(.06, t + .06); g.gain.setValueAtTime(.06, t + Math.max(.07, sec - .08)); g.gain.linearRampToValueAtTime(0, t + sec);
      o.connect(f).connect(g).connect(c.destination); o.start(t); o.stop(t + sec + .05);
    },
    clunk() { const c = ctx(); if (!c) return; const t = c.currentTime; tone(c, 140, "triangle", t, .2, .2); tone(c, 620, "square", t, .05, .04); },
    win() { const c = ctx(); if (!c) return; const t = c.currentTime; [523, 659, 784, 1047, 784, 1047, 1319].forEach((f, i) => tone(c, f, "square", t + i * .1, i === 6 ? .5 : .12, .045)); },
    lose() {
      const c = ctx(); if (!c) return;
      const t = c.currentTime;
      [392, 370, 349, 294].forEach((f, i) => {
        const last = i === 3, at = t + i * .32, o = c.createOscillator(), g = c.createGain(), v = c.createOscillator(), vg = c.createGain(), lp = c.createBiquadFilter();
        o.type = "sawtooth"; o.frequency.setValueAtTime(f, at); if (last) o.frequency.linearRampToValueAtTime(f * .94, at + .7);
        v.frequency.value = 6; vg.gain.value = last ? 6 : 0; v.connect(vg).connect(o.frequency);
        lp.type = "lowpass"; lp.frequency.value = 1200;
        g.gain.setValueAtTime(0, at); g.gain.linearRampToValueAtTime(.05, at + .03); g.gain.setValueAtTime(.05, at + (last ? .6 : .24)); g.gain.linearRampToValueAtTime(0, at + (last ? .8 : .3));
        o.connect(lp).connect(g).connect(c.destination); o.start(at); v.start(at); o.stop(at + .85); v.stop(at + .85);
      });
    },
    bonus() { const c = ctx(); if (!c) return; const t = c.currentTime; [988, 1319, 1976].forEach((f, i) => tone(c, f, "square", t + i * .07, .1, .04)); },
  };
}

// A claw on a rail: it moves along the rail, lowers and raises on its cable,
// opens and closes, holds a prize and lets it go. parent is a fixed layer the
// size of the window, so window coordinates are its coordinates.
function makeClaw(parent, x, top) {
  const rig = document.createElement("div");
  rig.className = "claw-rig";
  rig.style.cssText = `left:${x}px;top:${top}px`;
  rig.innerHTML = `<div class="cable"></div><div class="car"></div><div class="claw"><div class="held"></div>${CLAW_SVG}</div>`;
  parent.appendChild(rig);
  const cable = rig.querySelector(".cable"), claw = rig.querySelector(".claw"), held = rig.querySelector(".held");
  let X = x, drop = 24;
  const done = a => a.finished.catch(() => {});
  return {
    el: rig,
    get x() { return X; },
    // The cable length that puts the tips of the prongs at y.
    reachTo: y => Math.max(24, y - top - 8 - 50),
    async move(to, speed = 320) {
      const a = rig.animate([{ left: X + "px" }, { left: to + "px" }], { duration: Math.max(250, Math.abs(to - X) / speed * 1000), easing: "ease-in-out" });
      rig.style.left = to + "px"; X = to;
      await done(a);
    },
    async lower(to, ms = 900) {
      const o = { duration: ms, easing: "ease-in-out" };
      const a = cable.animate([{ height: drop + "px" }, { height: to + "px" }], o);
      const b = claw.animate([{ top: (8 + drop) + "px" }, { top: (8 + to) + "px" }], o);
      cable.style.height = to + "px"; claw.style.top = (8 + to) + "px"; drop = to;
      await Promise.all([done(a), done(b)]);
    },
    open() { rig.classList.add("open"); return clawWait(380); },
    close() { rig.classList.remove("open"); return clawWait(380); },
    hold(html) { held.innerHTML = html; held.classList.add("on"); },
    // Let go of the prize: it falls to y and fades there, or falls away.
    drop(y, fade = true) {
      if (!held.classList.contains("on")) return Promise.resolve();
      const r = held.getBoundingClientRect(), f = document.createElement("div");
      f.className = "claw-falling";
      f.innerHTML = held.innerHTML;
      f.style.cssText = `left:${r.left}px;top:${r.top}px`;
      parent.appendChild(f);
      held.classList.remove("on"); held.innerHTML = "";
      const dist = Math.max(0, y - r.bottom);
      const a = f.animate([{ transform: "translate(0, 0) rotate(0)" }, { transform: `translate(0, ${dist}px) rotate(${rnd(-40, 40).toFixed(0)}deg)` }],
        { duration: 300 + Math.sqrt(dist) * 28, easing: "cubic-bezier(.45, 0, 1, .6)", fill: "forwards" });
      return done(a).then(() => fade ? done(f.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 500, fill: "forwards" })) : null).then(() => f.remove());
    },
    remove() { rig.getAnimations({ subtree: true }).forEach(a => a.cancel()); rig.remove(); },
  };
}

// A new device nobody has marked known is an intruder: a mystery prize that
// was never put in. TILT: the cabinet shakes, the marquee bulbs go red, and a
// black box with a question mark drops into the prize chute and sits there,
// glowing red, tagged, until the device is marked known. Then it pops open,
// and the claw wins it as a new prize.
const CLAW_MYSTERY = `<svg viewBox="0 0 60 60" width="60" height="60">
  <rect x="6" y="18" width="48" height="38" rx="4" fill="#1a1024" stroke="#ff3b5c" stroke-width="2.5"/>
  <rect x="3" y="12" width="54" height="10" rx="3" fill="#24142f" stroke="#ff3b5c" stroke-width="2.5"/>
  <path d="M30 12v44" stroke="#ff3b5c" stroke-width="3" opacity=".7"/>
  <text x="30" y="47" text-anchor="middle" font-family="Space Grotesk, sans-serif" font-weight="800" font-size="24" fill="#ff5a78">?</text></svg>`;

function buildClaw(root, _switched, dusk = false) {
  const calm = calmMotion();
  // Claw Machine Dusk is the same cabinet in softer colours.
  const cols = dusk ? CLAW_DUSK_COLS : CLAW_COLS;
  const plush = (size, type = pick(CLAW_TYPES), c = pick(cols)) => plushSvg(size, type, c);
  let alive = true;
  festiveStops.push(() => { alive = false; });
  const snd = clawSound();
  const soundOn = themeSoundButton("bamf-claw-sound", "Claw machine sounds on: click to mute",
    "Claw machine sounds off: click for the coin, the motor and the jingle", on => { if (on) snd.coin(); });
  const play = (fn, ...a) => { if (soundOn() && !document.hidden) fn(...a); };
  const W = innerWidth, H = innerHeight;
  const head = document.querySelector("header");
  const headBottom = () => Math.max(0, head?.getBoundingClientRect().bottom || 0);
  const railY = Math.max(56, headBottom()) + 22;

  // Behind the glass: the heap of prizes, with the chute to its right.
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg claw-bg";
  bg.setAttribute("aria-hidden", "true");
  const pileH = Math.round(Math.min(170, Math.max(90, H * .17)));
  const chuteW = W < 700 ? 96 : 150, chuteX = W - 30 - chuteW;
  const heap = x => pileH * (.55 + .45 * Math.sin(Math.PI * Math.min(1, Math.max(0, x / chuteX))));
  const pileTop = x => H - heap(x);
  const items = [];
  for (let i = 0, n = Math.round(chuteX / 20); i < n; i++) {
    const size = rnd(40, 66), x = rnd(-20, chuteX - size + 6), hgt = heap(x + size / 2);
    items.push({ x, y: H - hgt + rnd(-size * .25, hgt - size * .45), size, type: pick(CLAW_TYPES), c: pick(cols), rot: rnd(-28, 28) });
  }
  items.sort((a, b) => a.y - b.y);
  const base = `M0 ${H}` + Array.from({ length: 41 }, (_, i) => { const x = chuteX * i / 40; return `L${x.toFixed(0)} ${(H - heap(x) * .7).toFixed(0)}`; }).join("") + `L${chuteX} ${H}Z`;
  bg.innerHTML = `<div class="rail" style="top:${railY - 10}px"></div>
    <svg class="pile" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}"><path d="${base}" fill="${dusk ? "#262a33" : "#2a1552"}"/>
      ${items.map(p => `<g transform="translate(${p.x.toFixed(0)} ${p.y.toFixed(0)}) rotate(${p.rot.toFixed(0)} ${(p.size / 2).toFixed(0)} ${(p.size / 2).toFixed(0)}) scale(${(p.size / 60).toFixed(3)})">${plushShape(p.type, p.c)}</g>`).join("")}</svg>
    <div class="chute" style="left:${chuteX}px;width:${chuteW}px;height:${Math.round(pileH * 1.1)}px"><b>PRIZE</b></div>
    <i class="tube l"></i><i class="tube r"></i><div class="glass"></div>`;
  document.body.prepend(bg);
  const chute = bg.querySelector(".chute");

  // The cabinet's own claw, playing by itself every so often.
  const bgClaw = makeClaw(bg, Math.round(W * .18), railY);
  bg.appendChild(bg.querySelector(".glass"));
  let bgBusy = false;
  const bgRound = async () => {
    if (!alive || calm || document.hidden || bgBusy) return;
    bgBusy = true;
    try {
      const tx = rnd(70, chuteX - 70);
      await bgClaw.move(tx); if (!alive) return;
      await bgClaw.open();
      await bgClaw.lower(bgClaw.reachTo(pileTop(tx) + 18)); if (!alive) return;
      await bgClaw.close(); if (!alive) return;
      const got = Math.random() < .75;
      if (got) bgClaw.hold(plush(44));
      await clawWait(250);
      await bgClaw.lower(24, 1100); if (!alive) return;
      if (got) {
        const cx = chuteX + chuteW / 2;
        if (Math.random() < .3) {
          await bgClaw.move(cx, 240); if (!alive) return;
          await bgClaw.open();
          chute.classList.remove("win"); void chute.offsetWidth; chute.classList.add("win");
          await bgClaw.drop(H + 60, false);
          await bgClaw.close();
        } else {
          // It slips somewhere on the way, as it usually does.
          const slip = bgClaw.x + (cx - bgClaw.x) * rnd(.15, .7);
          await bgClaw.move(slip, 240); if (!alive) return;
          await bgClaw.drop(pileTop(slip) + 12);
        }
      }
      if (!alive) return;
      await bgClaw.move(rnd(W * .08, chuteX * .5), 320);
    } catch { } finally { bgBusy = false; }
  };
  if (!calm) {
    const next = first => festiveTimers.push(setTimeout(() => { bgRound(); next(false); }, first ? rnd(2500, 6000) : rnd(13e3, 22e3)));
    next(true);
  }

  // A claw in front of the page: it comes in along the marquee, goes down
  // onto a device's row (or the middle of the screen), and comes back up
  // with the prize. Win and it carries it off; lose and it slips.
  let fgBusy = false;
  const banner = (big, small) => {
    const b = document.createElement("div");
    b.className = "claw-banner";
    b.innerHTML = `<b>${esc(big)}</b>${small ? `<small>${esc(small)}</small>` : ""}`;
    root.appendChild(b);
    festiveTimers.push(setTimeout(() => b.remove(), 2600));
  };
  const visibleRows = () => [...document.querySelectorAll("tbody tr[data-id]")].filter(tr => {
    const r = tr.getBoundingClientRect();
    return r.height > 0 && r.top > headBottom() + 70 && r.bottom < innerHeight - 20;
  });
  const fgPlay = async ({ tr, name, win, title }) => {
    if (fgBusy || !alive) return;
    if (calm) { play(win ? snd.win : snd.lose); banner(win ? (title || "WINNER!") : "SO CLOSE!", name); return; }
    fgBusy = true;
    let c = null;
    try {
      const top = headBottom();
      const target = tr?.querySelector(".name-text") || tr?.querySelector("td.hostname");
      let tx, ty;
      if (target) { const r = target.getBoundingClientRect(); tx = r.left + Math.min(r.width, 120) / 2; ty = r.top + r.height / 2 + 6; }
      else { tx = innerWidth * rnd(.3, .7); ty = innerHeight * .55; }
      tx = Math.max(40, Math.min(innerWidth - 40, tx));
      c = makeClaw(root, innerWidth + 40, top);
      play(snd.whir, .9);
      await c.move(tx, 900); if (!alive) return;
      await c.open();
      play(snd.whir, .7);
      await c.lower(c.reachTo(ty), 750); if (!alive) return;
      await c.close();
      play(snd.clunk);
      if (tr) { tr.classList.remove("claw-grab"); void tr.offsetWidth; tr.classList.add("claw-grab"); festiveTimers.push(setTimeout(() => tr.classList.remove("claw-grab"), 750)); }
      c.hold(`<div class="claw-tag">${plush(26)}<span>${esc(name)}</span></div>`);
      await clawWait(200);
      play(snd.whir, .8);
      await c.lower(24, 800); if (!alive) return;
      if (win) {
        play(snd.whir, .8);
        await c.move(innerWidth - 60, 700); if (!alive) return;
        await c.open();
        c.drop(innerHeight + 80, false);
        play(snd.win);
        banner(title || "WINNER!", name);
        await clawWait(600);
      } else {
        const slip = c.x + (innerWidth - 60 - c.x) * rnd(.15, .45);
        play(snd.whir, .5);
        await c.move(slip, 700); if (!alive) return;
        play(snd.lose);
        const t = document.createElement("div");
        t.className = "claw-close";
        t.textContent = "SO CLOSE!";
        t.style.cssText = `left:${slip}px;top:${ty - 30}px`;
        root.appendChild(t);
        festiveTimers.push(setTimeout(() => t.remove(), 1800));
        await c.drop(ty);
      }
      if (!alive) return;
      await c.move(innerWidth + 50, 900);
    } catch { } finally { c?.remove(); fgBusy = false; }
  };

  // The 🕹 button beside the speaker: one play, on one of the rows in view.
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "arp-toggle claw-play";
  btn.innerHTML = `<span aria-hidden="true">🕹</span>`;
  btn.title = "Play: the claw goes for one of your devices";
  btn.setAttribute("aria-label", "Play the claw machine");
  btn.onclick = () => {
    if (fgBusy) return;
    play(snd.coin);
    const rows = visibleRows(), tr = rows.length ? pick(rows) : null;
    const h = tr ? hosts.find(x => String(x.id) === tr.dataset.id) : null;
    fgPlay({ tr, name: h ? nameOrIp(h) : "a mystery prize", win: Math.random() < .35 });
  };
  $("themeToggle")?.before(btn);
  festiveStops.push(() => btn.remove());

  // A finished scan: a coin goes in, the marquee races and the cabinet plays.
  festiveHooks.scanDone = () => {
    const html = document.documentElement;
    html.classList.add("claw-fast");
    festiveTimers.push(setTimeout(() => html.classList.remove("claw-fast"), 1800));
    play(snd.coin);
    bgRound();
  };
  festiveStops.push(() => document.documentElement.classList.remove("claw-fast"));
  festiveStops.push(() => document.querySelectorAll(".claw-charm, .claw-stamp, .claw-drop, .claw-bonus").forEach(el => el.remove()));
  // A new device, once it's been marked known: always a win.
  const newPrize = h => {
    if (document.hidden) return;
    const tr = h ? document.querySelector(`tbody tr[data-id="${h.id}"]`) : null;
    fgPlay({ tr: tr && visibleRows().includes(tr) ? tr : null, name: h ? nameOrIp(h) : "a new device", win: true, title: "NEW PRIZE!" });
  };

  // ---- intruders: mystery prizes in the chute ----
  const watchIn = intruderWatch();
  const boxes = new Map();              // id -> { h, el, tag, alarm, gone }
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const slotX = k => chuteX + chuteW / 2 - 30 - k * 70;
  const inChute = () => [...boxes.values()].filter(b => !b.gone).sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeBox = h => {
    const el = document.createElement("div");
    el.className = "claw-mystery";
    el.innerHTML = CLAW_MYSTERY;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    return { h, el, tag, alarm: 0, gone: false };
  };
  const settle = () => {
    const list = inChute();
    room.style.height = list.length ? "150px" : "0";
    list.forEach((b, k) => {
      b.el.hidden = k > 1;
      b.tag.style.bottom = `calc(100% + ${6 + k * 52}px)`;
      if (b.moving) return;
      b.el.style.left = slotX(k) + "px";
      b.el.style.top = (innerHeight - 70) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!boxes.has(h.id)) { const b = makeBox(h); b.el.style.transition = "none"; boxes.set(h.id, b); }
    for (const h of held) { const b = boxes.get(h.id); if (b && !b.gone) { b.h = h; if (!b.alarm || Date.now() - b.alarm > 9000) intruderTagEl(h, "held", b.tag); } }
    for (const h of cleared) {
      const b = boxes.get(h.id); if (!b || b.gone) continue;
      b.gone = true;
      const done = () => { b.el.remove(); boxes.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Checked and passed: the box pops open, and it's a prize after all.
      intruderTagEl(b.h, "cleared", b.tag);
      festiveTimers.push(setTimeout(() => { b.el.classList.add("open"); }, 2000));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) newPrize(h); }, 2900));
    }
    settle();
  }
  // TILT: the cabinet shakes, the bulbs go red, and in drops the box.
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let b = boxes.get(h.id);
    if (!b) { b = makeBox(h); boxes.set(h.id, b); }
    if (calm || document.hidden) { settle(); return; }
    b.alarm = Date.now(); b.moving = true;
    intruderTagEl(h, "alarm", b.tag);
    const html = document.documentElement;
    html.classList.add("claw-tilt");
    bg.classList.remove("tilt"); void bg.offsetWidth; bg.classList.add("tilt");
    festiveTimers.push(setTimeout(() => { html.classList.remove("claw-tilt"); bg.classList.remove("tilt"); }, 8000));
    const t = document.createElement("div");
    t.className = "claw-banner claw-tiltbanner";
    t.innerHTML = `<b>TILT!</b><small>${esc("a mystery prize nobody put in: " + nameOrIp(h))}</small>`;
    root.appendChild(t);
    festiveTimers.push(setTimeout(() => t.remove(), 2600));
    b.el.style.transition = "none"; b.el.style.left = slotX(0) + "px"; b.el.style.top = (railY + 10) + "px";
    void b.el.offsetWidth;
    settle();
    b.el.style.transition = "top 1.1s cubic-bezier(.5, 0, .8, 1.3)";
    b.el.style.top = (innerHeight - 70) + "px";
    festiveTimers.push(setTimeout(() => { b.moving = false; settle(); play(snd.clunk); }, 1150));
    festiveTimers.push(setTimeout(() => { if (!b.gone) intruderTagEl(b.h, "held", b.tag); }, 9000));
    play(snd.lose);
  };
  festiveStops.push(() => document.documentElement.classList.remove("claw-tilt"));
  syncIntruders();
  let backIds = [];
  festiveHooks.netChange = (off, back) => { backIds = back.slice(); if (back.length) play(snd.bonus); };
  festiveHooks.rendered = () => {
    syncIntruders();
    for (const id of wentOffIds) {
      const cell = document.querySelector(`tr[data-id="${id}"] td.hostname`);
      if (!cell) continue;
      const s = document.createElement("span");
      s.className = "claw-stamp";
      s.textContent = "TRY AGAIN";
      const f = document.createElement("span");
      f.className = "claw-drop";
      f.innerHTML = plush(22);
      cell.append(s, f);
      festiveTimers.push(setTimeout(() => { s.remove(); f.remove(); }, 2600));
    }
    if (wentOffIds.length) play(snd.lose);
    wentOffIds = [];
    for (const id of backIds) {
      const cell = document.querySelector(`tr[data-id="${id}"] td.hostname`);
      if (!cell) continue;
      const s = document.createElement("span");
      s.className = "claw-bonus";
      s.textContent = "BONUS!";
      cell.appendChild(s);
      festiveTimers.push(setTimeout(() => s.remove(), 1800));
    }
    backIds = [];
    // Watched devices carry a little bear: bobbing while they run, grey when they're off.
    for (const h of hosts) {
      if (!h.watched) continue;
      const cell = document.querySelector(`tr[data-id="${h.id}"] td.hostname`);
      if (!cell || cell.querySelector(".claw-charm")) continue;
      const v = document.createElement("span");
      v.className = "claw-charm" + (h.online ? "" : " off");
      v.title = h.online ? "Watched: running" : "Watched: offline";
      v.innerHTML = plush(16, "bear", dusk ? "#b59a80" : "#c8a27a");
      cell.querySelector(".name-text")?.after(v);
    }
  };
  // Unknown devices on the Map are mystery capsules.
  festiveHooks.decorateNode = (g, n) => {
    if (n.type !== "dev") return;
    const t = svgEl("g", { class: "claw-cap t-deco-icon", transform: "translate(-27,-27) scale(.25)" });
    t.innerHTML = plushShape("capsule", dusk ? "#b8788a" : "#ff4fa3");
    g.appendChild(t);
  };
  festiveHooks.rendered();
}

BAMF.registerTheme("claw", ctx => buildClaw(ctx.root, ctx.switched));
BAMF.registerTheme("clawdusk", ctx => buildClaw(ctx.root, ctx.switched, true));
})();
