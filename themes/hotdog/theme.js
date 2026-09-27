// Hotdog Stand: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Hotdog Stand: a squeeze of ketchup and mustard across the page now and
// then, a cart rolling by, "Order up!" for a new device, a "Ding!" when a
// scan finishes, and rows stamped 86'd when their device goes offline.
const HD_CART = `<svg viewBox="0 0 150 96" width="150" height="96" aria-hidden="true">
  <path d="M26 30 Q75 4 124 30 Z" fill="#ff0000" stroke="#000" stroke-width="2"/>
  <path d="M26 30 Q50 22 58 30 M58 30 Q75 18 92 30 M92 30 Q100 22 124 30" fill="#ffff00" stroke="#000" stroke-width="1.5"/>
  <rect x="73" y="29" width="4" height="26" fill="#000"/>
  <rect x="18" y="52" width="112" height="28" rx="3" fill="#ff0000" stroke="#000" stroke-width="2"/>
  <rect x="18" y="52" width="112" height="7" fill="#ffff00" stroke="#000" stroke-width="2"/>
  <g transform="translate(46 64)"><rect x="-14" y="-5" width="28" height="11" rx="5.5" fill="#e9b96e" stroke="#000" stroke-width="1.2"/>
    <rect x="-16" y="-3" width="32" height="6" rx="3" fill="#b3452b" stroke="#000" stroke-width="1"/>
    <path d="M-11 0q2.5-3 5 0t5 0t5 0t5 0" fill="none" stroke="#ffd200" stroke-width="1.6"/></g>
  <text x="96" y="72" font-family="Arial, sans-serif" font-weight="700" font-size="11" fill="#fff" text-anchor="middle">HOT DOGS</text>
  <rect x="8" y="56" width="12" height="3" fill="#000"/>
  <g class="wheel"><circle cx="40" cy="84" r="9" fill="#222" stroke="#000"/><path d="M40 76v16M32 84h16" stroke="#ffff00" stroke-width="2"/></g>
  <g class="wheel"><circle cx="108" cy="84" r="9" fill="#222" stroke="#000"/><path d="M108 76v16M100 84h16" stroke="#ffff00" stroke-width="2"/></g>
</svg>`;

// A new device nobody has marked known is an intruder: a seagull. It swoops
// on the cart and makes off with a hot dog; the bells ring, and a Windows 3.1
// box pops up to say so. It perches on the cart's umbrella along the bottom,
// the hot dog in its beak, tagged, until the device is marked known. Then it
// flies off, the cart rolls away, and it's Order up! for the device.
const HD_GULL = `<svg viewBox="0 0 64 46" width="64" height="46" aria-hidden="true">
  <path d="M13 26L3 21l2 10z" fill="#9aa3ad" stroke="#000" stroke-width="1.2" stroke-linejoin="round"/>
  <ellipse cx="27" cy="27" rx="16" ry="10" fill="#fff" stroke="#000" stroke-width="1.4"/>
  <g class="hd-wing"><path d="M15 23Q27 13 39 22Q30 31 15 28z" fill="#9aa3ad" stroke="#000" stroke-width="1.2"/></g>
  <circle cx="41" cy="16" r="7.5" fill="#fff" stroke="#000" stroke-width="1.4"/><circle cx="43" cy="14" r="1.4" fill="#000"/>
  <path d="M47 15l9 2.5-9 2.5z" fill="#f2b300" stroke="#000" stroke-width="1"/>
  <g transform="translate(55 19) rotate(18)"><rect x="-4" y="-3.5" width="16" height="7" rx="3.5" fill="#e9b96e" stroke="#000" stroke-width="1"/>
    <rect x="-5" y="-2" width="18" height="4" rx="2" fill="#b3452b" stroke="#000" stroke-width=".8"/></g>
  <path d="M24 36v8M31 36v8M21 44h6M28 44h6" stroke="#e07000" stroke-width="2" stroke-linecap="round"/></svg>`;

const HD_STOP = `<svg viewBox="0 0 32 32" width="32" height="32" aria-hidden="true"><path d="M10 1h12l9 9v12l-9 9H10l-9-9V10z" fill="#ff0000" stroke="#000" stroke-width="1.5"/>
  <path d="M10 10l12 12M22 10L10 22" stroke="#fff" stroke-width="4" stroke-linecap="round"/></svg>`;

// Hotdog Stand's sounds, made in the browser: a desk bell for a finished
// scan, a bicycle bell for the cart, a ketchup squirt, a jingle for a new
// device, and a chord, the old Windows kind, for a device that has gone.
let hdAudio = null;

function hotdogSound() {
  const ctx = () => {
    try { hdAudio = hdAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return null; }
    if (hdAudio.state === "suspended") hdAudio.resume?.().catch(() => {});
    return hdAudio;
  };
  const tone = (c, freq, type, at, len, gain) => {
    const o = c.createOscillator(), g = c.createGain();
    o.type = type; o.frequency.setValueAtTime(freq, at);
    g.gain.setValueAtTime(0, at); g.gain.linearRampToValueAtTime(gain, at + .01); g.gain.exponentialRampToValueAtTime(.0001, at + len);
    o.connect(g).connect(c.destination); o.start(at); o.stop(at + len + .05);
  };
  const bell = (c, at, f, gain) => { tone(c, f, "sine", at, .9, gain); tone(c, f * 2.76, "sine", at, .35, gain * .35); tone(c, f * 5.4, "sine", at, .15, gain * .15); };
  return {
    ding() { const c = ctx(); if (!c) return; bell(c, c.currentTime, 2093, .25); },
    bike() { const c = ctx(); if (!c) return; bell(c, c.currentTime, 2637, .16); bell(c, c.currentTime + .14, 2637, .16); },
    tada() { const c = ctx(); if (!c) return; [523, 659, 784, 1047].forEach((f, i) => tone(c, f, "square", c.currentTime + i * .09, i === 3 ? .5 : .12, .05)); },
    chord() { const c = ctx(); if (!c) return; [523, 659, 784].forEach(f => tone(c, f, "square", c.currentTime, .45, .04)); },
    squirt(second) {
      const c = ctx(); if (!c) return;
      const len = .28, buf = c.createBuffer(1, Math.floor(c.sampleRate * len), c.sampleRate), d = buf.getChannelData(0);
      for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
      const src = c.createBufferSource(); src.buffer = buf;
      const f = c.createBiquadFilter(); f.type = "lowpass"; f.Q.value = 6;
      const at = c.currentTime;
      f.frequency.setValueAtTime(second ? 700 : 1200, at); f.frequency.exponentialRampToValueAtTime(second ? 200 : 300, at + len);
      const g = c.createGain(); g.gain.setValueAtTime(.3, at); g.gain.exponentialRampToValueAtTime(.001, at + len);
      src.connect(f).connect(g).connect(c.destination); src.start(at); src.stop(at + len);
    },
  };
}

// The Map for Hotdog Stand: mustard across an online device, a hot dog on an
// unknown one, and a paper hat on the router. Under the tile, so the CSS can
// show each where it belongs.
function hotdogMapDeco(g, n) {
  const under = el => g.insertBefore(el, g.querySelector(".t-watch"));
  if (n.type === "dev") {
    under(svgEl("path", { class: "t-mustard", d: "M-13 13q3.25-4 6.5 0t6.5 0t6.5 0t6.5 0" }));
    const hd = svgEl("text", { class: "t-hd", x: 9, y: -9, "font-size": 15 });
    hd.textContent = "🌭";
    under(hd);
  }
  if (n.type === "root" || n.isRoot) {
    const hat = svgEl("g", { class: "t-paperhat", transform: "translate(0,-17)" });
    hat.appendChild(svgEl("path", { d: "M-15 0Q-12-13 0-14Q12-13 15 0Z", fill: "#fff", stroke: "#000", "stroke-width": 1.2 }));
    hat.appendChild(svgEl("rect", { x: -15, y: -4, width: 30, height: 4, fill: "#ff0000", stroke: "#000", "stroke-width": 1 }));
    under(hat);
  }
}

function buildHotdog(root) {
  festiveHooks.decorateNode = hotdogMapDeco;
  const calm = calmMotion();
  // Sound: off until the speaker button beside the theme button is clicked.
  const hd = hotdogSound();
  const soundOn = themeSoundButton("bamf-hotdog-sound", "Hotdog Stand sounds on: click to mute",
    "Hotdog Stand sounds off: click for the bell and the squirt", on => { if (on) hd.ding(); });
  const play = (fn, ...a) => { if (soundOn() && !document.hidden) fn(...a); };
  const below = () => (document.querySelector("header")?.getBoundingClientRect().bottom || 60) + 20;

  // A squeeze of ketchup, then mustard, across the page.
  const squiggle = () => {
    if (document.hidden) return;
    play(hd.squirt);
    festiveTimers.push(setTimeout(() => play(hd.squirt, true), 900));
    const W = Math.round(innerWidth * .64);
    const wave = (off, amp) => {
      let d = `M0 ${20 + off}`;
      for (let x = 0; x < W; x += 36) d += ` q9 ${-amp} 18 0 t18 0`;
      return d;
    };
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "hd-squig");
    svg.setAttribute("viewBox", `0 0 ${W} 40`);
    svg.style.top = below() + "px";
    svg.innerHTML = `<path d="${wave(-3, 14)}" stroke="#d8140a" stroke-width="7"/><path d="${wave(4, 12)}" stroke="#f2b300" stroke-width="5"/>`;
    root.appendChild(svg);
    svg.querySelectorAll("path").forEach(p => p.style.setProperty("--len", Math.ceil(p.getTotalLength())));
    festiveTimers.push(setTimeout(() => svg.classList.add("fade"), 4500));
    festiveTimers.push(setTimeout(() => svg.remove(), 5600));
  };
  // The cart rolls along the bottom, steam off the grill.
  const cart = () => {
    if (document.hidden) return;
    play(hd.bike);
    const c = document.createElement("div");
    c.className = "hd-cart";
    c.innerHTML = HD_CART + `<i class="hd-puff"></i><i class="hd-puff"></i><i class="hd-puff"></i>`;
    c.addEventListener("animationend", e => { if (e.target === c) c.remove(); });
    root.appendChild(c);
  };
  if (!calm) {
    const nextSquiggle = first => festiveTimers.push(setTimeout(() => { squiggle(); nextSquiggle(false); }, first ? rnd(6e3, 14e3) : rnd(40e3, 80e3)));
    const nextCart = first => festiveTimers.push(setTimeout(() => { cart(); nextCart(false); }, first ? rnd(20e3, 45e3) : rnd(90e3, 180e3)));
    nextSquiggle(true);
    nextCart(true);
  }

  // A new device, once it's been marked known: order up.
  const orderUp = h => {
    if (document.hidden) return;
    play(hd.tada);
    const o = document.createElement("div");
    o.className = "hd-order";
    o.style.top = below() + "px";
    o.innerHTML = `🛎️ Order up! 🌭<small>${esc(h ? nameOrIp(h) : "a new device")} just arrived</small>`;
    root.appendChild(o);
    festiveTimers.push(setTimeout(() => o.remove(), 3000));
  };
  // ---- intruders: seagulls on the cart ----
  const W = innerWidth;
  const watchIn = intruderWatch();
  const gulls = new Map();              // id -> { h, el, tag, alarm, gone }
  const spacer = document.createElement("div");
  spacer.setAttribute("aria-hidden", "true");
  document.body.appendChild(spacer);
  festiveStops.push(() => spacer.remove());
  const slotX = k => Math.round(W < 700 ? W * .6 : W * .72 - k * 280);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...gulls.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeGull = h => {
    const el = document.createElement("div");
    el.className = "hd-thief";
    el.innerHTML = HD_CART + `<div class="hd-gull">${HD_GULL}</div>`;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const g = { h, el, tag, alarm: 0, gone: false };
    gulls.set(h.id, g);
    return g;
  };
  const settle = () => {
    const list = order();
    spacer.style.height = list.some(g => !g.gone) ? "136px" : "0";
    list.forEach((g, k) => {
      g.el.hidden = k > (W < 700 ? 0 : 1);
      g.el.style.left = slotX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!gulls.has(h.id)) makeGull(h);
    for (const h of held) { const g = gulls.get(h.id); if (g && !g.gone) { g.h = h; if (!g.alarm || Date.now() - g.alarm > 9000) intruderTagEl(h, "held", g.tag); } }
    for (const h of cleared) {
      const g = gulls.get(h.id); if (!g || g.gone) continue;
      g.gone = true;
      const done = () => { g.el.remove(); gulls.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: it flies off, the cart rolls away, and it's Order up!
      intruderTagEl(g.h, "cleared", g.tag);
      festiveTimers.push(setTimeout(() => { g.el.classList.add("flown"); play(hd.bike); }, 1800));
      festiveTimers.push(setTimeout(() => g.el.classList.add("gone"), 2600));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) orderUp(h); }, 4000));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const g = gulls.get(h.id) || makeGull(h);
    if (calm || document.hidden) { settle(); return; }
    g.alarm = Date.now();
    intruderTagEl(h, "alarm", g.tag);
    settle();
    // It swoops on the cart, the bells ring, and Windows has something to say.
    g.el.classList.remove("swoop"); void g.el.offsetWidth; g.el.classList.add("swoop");
    play(hd.chord);
    festiveTimers.push(setTimeout(() => play(hd.bike), 500));
    festiveTimers.push(setTimeout(() => play(hd.bike), 900));
    const box = document.createElement("div");
    box.className = "hd-win";
    box.style.top = below() + "px";
    box.innerHTML = `<div class="hd-win-t">Intruder Alert</div><div class="hd-win-b">${HD_STOP}<div><b>Unauthorized customer.</b>
      ${esc(nameOrIp(h))} has helped itself to a hot dog.</div></div><div class="hd-win-btn">OK</div>`;
    root.appendChild(box);
    festiveTimers.push(setTimeout(() => box.remove(), 5200));
    festiveTimers.push(setTimeout(() => { if (!g.gone) intruderTagEl(g.h, "held", g.tag); }, 9000));
  };

  // A scan finishes: ding.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    play(hd.ding);
    const d = document.createElement("div");
    d.className = "hd-ding";
    d.style.top = (below() - 12) + "px";
    d.textContent = "🛎️ Ding!";
    root.appendChild(d);
    festiveTimers.push(setTimeout(() => d.remove(), 1700));
  };
  // A device goes offline: its row is 86'd, diner for "we're out of it".
  festiveHooks.rendered = () => {
    syncIntruders();
    if (wentOffIds.length) play(hd.chord);
    for (const id of wentOffIds) {
      const cell = document.querySelector(`tr[data-id="${id}"] td.hostname`);
      if (!cell) continue;
      const s = document.createElement("span");
      s.className = "hd-86";
      s.textContent = "86'd";
      s.title = "Diner slang: gone, out of stock. This device just went offline.";
      cell.appendChild(s);
      festiveTimers.push(setTimeout(() => s.remove(), 2900));
    }
    wentOffIds = [];
  };
  syncIntruders();
}

BAMF.registerTheme("hotdog", ctx => buildHotdog(ctx.root, ctx.switched));
})();
