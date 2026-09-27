// Matrix: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Half-width katakana, drawn mirrored, with digits and a few symbols.
const RAIN_GLYPHS = "ｦｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789Z:.=*+-<>¦|";

const SCRAMBLE_GLYPHS = "0123456789ABCDEF#$%&*<>=";

// A new device nobody has marked known is an intruder. The rain turns red,
// INTRUSION DETECTED types itself across the screen, and an Agent walks in
// along the bottom. He stays there, tagged, the device's name falling in red
// now and then, until it's marked known; then he's gone, and its name falls
// in white.
const MX_AGENT = `<svg viewBox="0 0 40 84" width="40" height="84">
  <circle cx="20" cy="10" r="7.5" fill="#1a1f1a"/>
  <path d="M13 9h14v3.2H13z" fill="#0a0d0a"/><path class="glint" d="M14.5 9.6h4v1.6h-4zM21.5 9.6h4v1.6h-4z" fill="#7dff9c"/>
  <path d="M8 22q12-6 24 0l3 30H5z" fill="#0c0f0c"/>
  <path d="M17 20l3 10 3-10z" fill="#e8f5e8"/><path d="M19.2 22h1.6l.8 9-1.6 2-1.6-2z" fill="#0a0d0a"/>
  <path d="M5 50l3 30h7l2-24 2 0 3 24h7l3-30z" fill="#0c0f0c"/>
  <path d="M4 24q-3 12 0 26M36 24q3 12 0 26" stroke="#0c0f0c" stroke-width="5" stroke-linecap="round" fill="none"/>
  <path d="M14 20q6 3 12 0" stroke="#223022" stroke-width="1.4" fill="none"/></svg>`;

function buildMatrix(root, switched) {
  let redUntil = 0;
  const cv = document.createElement("canvas");
  cv.id = "themeBg";
  cv.className = "theme-bg";
  cv.setAttribute("aria-hidden", "true");
  document.body.prepend(cv);
  const ctx = cv.getContext("2d");
  const FS = 16, W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);
  ctx.fillStyle = "#000700";
  ctx.fillRect(0, 0, W, H);
  ctx.font = `${FS}px "IBM Plex Mono", monospace`;
  ctx.textBaseline = "top";
  const glyph = (ch, col, row, color) => {
    const x = col * FS, y = row * FS;
    ctx.fillStyle = "#000700";
    ctx.fillRect(x, y, FS, FS);
    ctx.fillStyle = color;
    ctx.save(); ctx.translate(x + FS, y); ctx.scale(-1, 1); ctx.fillText(ch, 1, 0); ctx.restore();
  };
  const cols = Math.ceil(W / FS), rows = Math.ceil(H / FS);
  let dropName = () => {}, alarmRain = () => {};

  if (calmMotion()) {
    // Reduced motion: a still wall of glyphs, fading down the screen.
    for (let c = 0; c < cols; c++) for (let r = 0; r < rows; r++)
      if (Math.random() < .35) glyph(pick(RAIN_GLYPHS), c, r, `rgba(0,255,65,${(.08 + Math.random() * .3).toFixed(2)})`);
  } else {
    // What the rain can spell: your devices' names and addresses.
    const words = () => hosts.filter(h => !h.forgotten && !h.ignored)
      .map(h => Math.random() < .5 ? h.ip : nameOrIp(h)).filter(Boolean);
    const drops = Array.from({ length: cols }, () => ({ y: -rnd(0, rows), speed: rnd(.3, .9), row: null, prev: null, msg: null, mi: 0, white: false }));
    const spell = (d, text, white) => { d.msg = text + "  "; d.mi = 0; d.white = white; };
    let raf = 0, last = 0;
    const frame = now => {
      raf = requestAnimationFrame(frame);
      if (now - last < 45) return;   // about 22 frames a second is plenty
      last = now;
      ctx.fillStyle = "rgba(0,7,0,.07)";
      ctx.fillRect(0, 0, W, H);
      drops.forEach((d, c) => {
        const r = Math.floor(d.y);
        if (r !== d.row) {
          // The old head becomes trail, then a new bright head below it.
          const red = performance.now() < redUntil || d.red;
          if (d.prev && d.row >= 0) glyph(d.prev.ch, c, d.row, d.prev.msg ? (d.prev.white ? "#ffffff" : red ? "#ff8a8a" : "#8dffa8") : red ? "#ff2b2b" : "#00ff41");
          let ch, msg = false;
          if (d.msg && d.mi < d.msg.length) { ch = d.msg[d.mi++]; msg = true; }
          else { d.msg = null; ch = pick(RAIN_GLYPHS); }
          if (r >= 0 && ch !== " ") glyph(ch, c, r, msg ? (red ? "#ffd0d0" : "#ffffff") : red ? "#ffd6d6" : "#d8ffe0");
          d.prev = { ch, msg, white: d.white };
          d.row = r;
        }
        d.y += d.speed;
        if (r > rows && Math.random() > .97) {
          Object.assign(d, { y: -rnd(0, 12), speed: rnd(.3, .9), row: null, prev: null, msg: null, red: false });
          const w = Math.random() < .05 ? words() : [];
          if (w.length) spell(d, pick(w), false);
        }
      });
    };
    raf = requestAnimationFrame(frame);
    festiveStops.push(() => cancelAnimationFrame(raf));
    // A device's name drops down a column: white once it's known, red while
    // it's an intruder.
    dropName = (text, red) => {
      const d = drops[Math.floor(rnd(cols * .15, cols * .85))];
      Object.assign(d, { y: 0, speed: .8, row: null, prev: null, red: !!red });
      spell(d, text, !red);
    };
    // The alarm: the rain runs red for a while, and a line is typed out.
    alarmRain = () => {
      redUntil = performance.now() + 8000;
      const box = document.createElement("div");
      box.className = "m-intro m-alert";
      root.appendChild(box);
      const text = "INTRUSION DETECTED";
      let i = 0;
      const type = () => {
        box.textContent = text.slice(0, ++i);
        if (i < text.length) festiveTimers.push(setTimeout(type, rnd(40, 90)));
        else { festiveTimers.push(setTimeout(() => box.classList.add("fade"), 2600)); festiveTimers.push(setTimeout(() => box.remove(), 3800)); }
      };
      festiveTimers.push(setTimeout(type, 200));
    };

    // Switching to Matrix, and only then: a line typed across the screen.
    if (switched) {
      const box = document.createElement("div");
      box.className = "m-intro";
      root.appendChild(box);
      const lines = ["Wake up…", "The network has you."];
      let li = 0, ci = 0;
      const type = () => {
        if (li >= lines.length) {
          festiveTimers.push(setTimeout(() => box.classList.add("fade"), 1800));
          festiveTimers.push(setTimeout(() => box.remove(), 3000));
          return;
        }
        if (ci === 0) box.appendChild(document.createElement("div"));
        box.lastChild.textContent = lines[li].slice(0, ++ci);
        if (ci >= lines[li].length) { li++; ci = 0; festiveTimers.push(setTimeout(type, 900)); }
        else festiveTimers.push(setTimeout(type, rnd(55, 120)));
      };
      festiveTimers.push(setTimeout(type, 400));
    }

    // Now and then a white rabbit hops along the bottom. Follow it.
    const rabbit = () => {
      if (document.hidden) return;
      const r = document.createElement("div");
      r.className = "m-rabbit";
      r.title = "Follow the white rabbit";
      r.innerHTML = `<svg viewBox="0 0 40 30" width="34" height="26"><g fill="#f2fff2">
        <ellipse cx="18" cy="21" rx="11" ry="7"/><circle cx="29" cy="14" r="5.5"/>
        <ellipse cx="28" cy="5" rx="1.8" ry="6.5" transform="rotate(-14 28 10)"/><ellipse cx="32" cy="5.5" rx="1.8" ry="6.5" transform="rotate(12 32 11)"/>
        <circle cx="7" cy="19" r="3.2"/><rect x="23" y="24" width="8" height="4" rx="2"/></g>
        <circle cx="31" cy="13" r="1" fill="#001200"/></svg>`;
      r.onclick = () => {
        r.remove();
        document.querySelector('.stat.clickable[data-jump="unknown"]')?.click();
        toast("You followed the white rabbit: here are your <strong>unknown</strong> devices.");
      };
      r.addEventListener("animationend", () => r.remove());
      root.appendChild(r);
    };
    const nextRabbit = first => festiveTimers.push(setTimeout(() => { rabbit(); nextRabbit(false); }, first ? rnd(25e3, 50e3) : rnd(90e3, 180e3)));
    nextRabbit(true);
  }

  // ---- intruders: Agents along the bottom ----
  const watchIn = intruderWatch();
  const agents = new Map();             // id -> { h, el, tag, alarm, gone }
  const calm = calmMotion();
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const standX = k => innerWidth - 90 - k * 170;
  const standing = () => [...agents.values()].filter(a => !a.gone).sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeAgent = h => {
    const el = document.createElement("div");
    el.className = "m-agent";
    el.innerHTML = MX_AGENT;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    return { h, el, tag, alarm: 0, gone: false };
  };
  const settle = () => {
    const list = standing();
    room.style.height = list.length ? "150px" : "0";
    list.forEach((a, k) => {
      a.el.hidden = k > 1;
      a.tag.style.bottom = `calc(100% + ${6 + k * 0}px)`;
      if (a.moving) return;
      if (calm) { a.el.style.transition = "none"; }
      a.el.style.left = standX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!agents.has(h.id)) { const a = makeAgent(h); a.el.style.transition = "none"; agents.set(h.id, a); }
    for (const h of held) { const a = agents.get(h.id); if (a && !a.gone) { a.h = h; if (!a.alarm || Date.now() - a.alarm > 9000) intruderTagEl(h, "held", a.tag); } }
    for (const h of cleared) {
      const a = agents.get(h.id); if (!a || a.gone) continue;
      a.gone = true;
      const done = () => { a.el.remove(); agents.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // He's gone, in a flicker; the name falls in white.
      intruderTagEl(a.h, "cleared", a.tag);
      festiveTimers.push(setTimeout(() => { a.el.classList.add("gone"); }, 2200));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) dropName(nameOrIp(h), false); }, 3400));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let a = agents.get(h.id);
    if (!a) { a = makeAgent(h); agents.set(h.id, a); }
    if (calm || document.hidden) { settle(); return; }
    a.alarm = Date.now(); a.moving = true;
    intruderTagEl(h, "alarm", a.tag);
    a.el.style.transition = "none"; a.el.style.left = (innerWidth + 40) + "px";
    void a.el.offsetWidth;
    settle();
    a.el.classList.add("walking");
    a.el.style.transition = "left 4.5s linear";
    a.el.style.left = standX(0) + "px";
    festiveTimers.push(setTimeout(() => { a.el.classList.remove("walking"); a.moving = false; settle(); }, 4600));
    festiveTimers.push(setTimeout(() => { if (!a.gone) intruderTagEl(a.h, "held", a.tag); }, 9000));
    alarmRain(); dropName(nameOrIp(h), true);
  };
  // While one's held, its name comes down the rain in red now and then.
  if (!calm) festiveTimers.push(setInterval(() => {
    if (document.hidden) return;
    const list = standing();
    if (list.length && Math.random() < .5) dropName(nameOrIp(pick(list).h), true);
  }, 20000));
  syncIntruders();

  // Numbers decode when they change; the name and the device names do once.
  const stats = ["statTotal", "statOnline", "statUnknown"];
  const seen = {};
  let first = true;
  festiveHooks.rendered = () => {
    syncIntruders();
    for (const id of stats) {
      const el = $(id);
      if (!el) continue;
      const v = el.textContent;
      if (first || (seen[id] !== undefined && seen[id] !== v)) scramble(el, v);
      seen[id] = v;
    }
    if (first) {
      first = false;
      const h1 = document.querySelector(".brand h1");
      if (h1) scramble(h1, "BAMF", 900);
      [...document.querySelectorAll("#hostTable .name-text, tbody .name-text")].slice(0, 60).forEach(el => scramble(el, el.textContent, rnd(500, 1100)));
    }
    // Rows whose device just went offline or moved glitch for a moment.
    for (const id of glitchIds) {
      const tr = document.querySelector(`tr[data-id="${id}"]`);
      if (!tr) continue;
      tr.classList.add("m-glitch");
      festiveTimers.push(setTimeout(() => tr.classList.remove("m-glitch"), 700));
    }
    glitchIds = [];
  };
  festiveHooks.rendered();
}

// Scrambles an element's text through random characters, settling left to
// right on the real text. Skipped under reduced motion.
function scramble(el, text, ms = 650) {
  if (calmMotion() || !text) return;
  cancelAnimationFrame(el._scr || 0);
  const start = performance.now();
  const tick = now => {
    const p = Math.min(1, (now - start) / ms);
    const keep = Math.floor(text.length * p);
    let s = text.slice(0, keep);
    for (let i = keep; i < text.length; i++) s += text[i] === " " ? " " : pick(SCRAMBLE_GLYPHS);
    el.textContent = p < 1 && festiveTheme === "matrix" ? s : text;
    if (p < 1 && festiveTheme === "matrix") el._scr = requestAnimationFrame(tick);
  };
  el._scr = requestAnimationFrame(tick);
}

BAMF.registerTheme("matrix", ctx => buildMatrix(ctx.root, ctx.switched));
})();
