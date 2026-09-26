// Matrix: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Half-width katakana, drawn mirrored, with digits and a few symbols.
const RAIN_GLYPHS = "ｦｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ0123456789Z:.=*+-<>¦|";

const SCRAMBLE_GLYPHS = "0123456789ABCDEF#$%&*<>=";

function buildMatrix(root, switched) {
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
          if (d.prev && d.row >= 0) glyph(d.prev.ch, c, d.row, d.prev.msg ? (d.prev.white ? "#ffffff" : "#8dffa8") : "#00ff41");
          let ch, msg = false;
          if (d.msg && d.mi < d.msg.length) { ch = d.msg[d.mi++]; msg = true; }
          else { d.msg = null; ch = pick(RAIN_GLYPHS); }
          if (r >= 0 && ch !== " ") glyph(ch, c, r, msg ? "#ffffff" : "#d8ffe0");
          d.prev = { ch, msg, white: d.white };
          d.row = r;
        }
        d.y += d.speed;
        if (r > rows && Math.random() > .97) {
          Object.assign(d, { y: -rnd(0, 12), speed: rnd(.3, .9), row: null, prev: null, msg: null });
          const w = Math.random() < .05 ? words() : [];
          if (w.length) spell(d, pick(w), false);
        }
      });
    };
    raf = requestAnimationFrame(frame);
    festiveStops.push(() => cancelAnimationFrame(raf));
    // A new device drops its name down a column, in white.
    festiveHooks.newDevice = h => {
      const d = drops[Math.floor(rnd(cols * .15, cols * .85))];
      Object.assign(d, { y: 0, speed: .8, row: null, prev: null });
      spell(d, h ? nameOrIp(h) : "NEW DEVICE", true);
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

  // Numbers decode when they change; the name and the device names do once.
  const stats = ["statTotal", "statOnline", "statUnknown"];
  const seen = {};
  let first = true;
  festiveHooks.rendered = () => {
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
