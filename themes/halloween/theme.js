// Halloween: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
function cobweb(size) {
  let d = "";
  const spokes = [0, 15, 30, 45, 60, 75, 90].map(a => a * Math.PI / 180);
  for (const a of spokes) d += `M0 0 L${(Math.cos(a) * size).toFixed(1)} ${(Math.sin(a) * size).toFixed(1)} `;
  for (const r of [size * .22, size * .42, size * .64, size * .86]) {
    spokes.forEach((a, i) => {
      if (!i) { d += `M${(Math.cos(a) * r).toFixed(1)} ${(Math.sin(a) * r).toFixed(1)} `; return; }
      const b = spokes[i - 1], m = (a + b) / 2, rr = r * .86;
      d += `Q${(Math.cos(m) * rr).toFixed(1)} ${(Math.sin(m) * rr).toFixed(1)} ${(Math.cos(a) * r).toFixed(1)} ${(Math.sin(a) * r).toFixed(1)} `;
    });
  }
  return `<path d="${d}"/>`;
}

const PUMPKIN = `<svg class="pumpkin" viewBox="0 0 60 52" width="52"><path d="M30 12c-2-6 1-10 6-11" stroke="#4d7a2a" stroke-width="3" fill="none" stroke-linecap="round"/>
  <ellipse cx="18" cy="31" rx="15" ry="19" fill="#e0680f"/><ellipse cx="42" cy="31" rx="15" ry="19" fill="#e0680f"/><ellipse cx="30" cy="31" rx="14" ry="20" fill="#f07818"/>
  <path class="face" d="M17 25l6-6 5 6z M33 25l6-6 5 6z M16 34l5 4 4-3 5 4 5-4 4 3 5-4c-2 8-8 12-14 12s-12-4-14-12z"/></svg>`;

const BAT = `<svg viewBox="0 0 64 28"><path d="M32 10c2-3 2-6 1-8l-1 4-1-4c-1 2-1 5 1 8-6-6-14-9-22-6 4 2 6 5 6 9-4-2-9-2-13 1 5 1 8 4 9 8 3-3 9-4 12-2 2 1 4 4 8 6 4-2 6-5 8-6 3-2 9-1 12 2 1-4 4-7 9-8-4-3-9-3-13-1 0-4 2-7 6-9-8-3-16 0-22 6z"/></svg>`;

const GHOST = `<svg viewBox="0 0 54 64"><path d="M27 2C13 2 5 13 5 27v33l7-6 7 6 8-6 8 6 7-6 7 6V27C49 13 41 2 27 2z" fill="#f4eefa" opacity=".92"/>
  <ellipse cx="19" cy="26" rx="4" ry="6" fill="#1a1020"/><ellipse cx="35" cy="26" rx="4" ry="6" fill="#1a1020"/><ellipse cx="27" cy="40" rx="5" ry="3.5" fill="#1a1020"/></svg>`;

const SPIDER = `<svg viewBox="0 0 30 30" width="30" style="position:absolute;left:0;top:214px"><g stroke="#1a0d1f" stroke-width="1.6" fill="none" stroke-linecap="round">
  <path d="M15 14L4 6M15 16L2 15M15 18L4 25M15 20L7 29M15 14L26 6M15 16L28 15M15 18L26 25M15 20L23 29"/></g>
  <circle cx="15" cy="12" r="4" fill="#1a0d1f"/><ellipse cx="15" cy="19" rx="6" ry="7" fill="#1a0d1f"/><circle cx="13.5" cy="11" r="1" fill="#ff3b3b"/><circle cx="16.5" cy="11" r="1" fill="#ff3b3b"/></svg>`;

// Drawn facing left and mirrored, so she flies handle first with the bristles trailing.
const WITCH = `<svg viewBox="0 0 64 34"><g fill="#0a0508" stroke="#ff8c1a" stroke-width=".5" transform="translate(64 0) scale(-1 1)">
  <path d="M2 26L44 20l1 2L3 28z"/><path d="M44 19l16-6-4 8 6 1-17 4z"/>
  <path d="M18 22l6-10 5 1 2 8z"/><circle cx="25" cy="10" r="3.2"/><path d="M20 9l6-9 3 8 5 1-14 2z"/><path d="M24 21l-2 7h3l2-6z"/></g></svg>`;

const LEAF_COLORS = ["#e0680f", "#c2410c", "#b45309", "#a16207", "#9a3412"];

function flareLanterns() {
  document.querySelectorAll("#festive .pumpkin").forEach(p => {
    p.classList.add("flare");
    setTimeout(() => p.classList.remove("flare"), 2200);
  });
}

// The Map for Halloween: offline devices turn into ghosts, unknown ones get a pumpkin.
function halloweenMapDeco(g, n) {
  if (n.type !== "dev") return;
  const over = el => g.insertBefore(el, g.querySelector(".t-name"));
  over(svgEl("path", { class: "t-ghost", "fill-rule": "evenodd", transform: "translate(-12,-12)",
    d: "M12 2C7.6 2 5 5.4 5 9.5V21l2.3-1.8L9.6 21l2.4-1.8 2.4 1.8 2.3-1.8L19 21V9.5C19 5.4 16.4 2 12 2z M9.2 8.3a1.2 1.7 0 1 0 .01 0z M14.8 8.3a1.2 1.7 0 1 0 .01 0z" }));
  const pk = svgEl("g", { class: "t-pk", transform: "translate(14,-14)" });
  pk.appendChild(svgEl("circle", { r: 6, fill: "#f07818" }));
  pk.appendChild(svgEl("rect", { x: -1, y: -8.5, width: 2, height: 3, fill: "#4d7a2a" }));
  pk.appendChild(svgEl("path", { d: "M-3.4 -1.2l1.4-1.6 1.4 1.6z M.6 -1.2l1.4-1.6 1.4 1.6z M-3 1.6q3 2.6 6 0", fill: "#ffd23f", stroke: "#ffd23f", "stroke-width": ".6" }));
  over(pk);
}

function buildHalloween(root) {
  festiveHooks.decorateNode = halloweenMapDeco;
  const NS = "http://www.w3.org/2000/svg";
  const W = window.innerWidth, H = window.innerHeight, calm = calmMotion();
  const later = (fn, ms) => festiveTimers.push(setTimeout(fn, ms));
  // A hidden tab doesn't run animations, so nothing that flies in would ever
  // finish and leave; skip the spawn and just keep the schedule ticking.
  const shown = () => !document.hidden;
  // A full moon in the header's empty middle, low fog along the bottom.
  const moon = document.createElement("div");
  moon.className = "moon";
  moon.style.left = Math.round(W * 0.56) + "px";
  moon.innerHTML = MOON;
  root.appendChild(moon);
  for (const k of [0, 1]) {
    const f = document.createElement("div");
    f.className = "fog fog" + k;
    root.appendChild(f);
  }
  // Cobwebs in the top corners.
  for (const corner of ["left", "right"]) {
    const w = document.createElementNS(NS, "svg");
    w.setAttribute("width", 170); w.setAttribute("height", 170);
    w.setAttribute("class", "web");
    w.style.top = "0"; w.style[corner] = "0";
    if (corner === "right") w.style.transform = "scaleX(-1)";
    w.innerHTML = cobweb(165);
    root.appendChild(w);
  }
  // Jack-o'-lanterns in the bottom corners.
  for (const corner of ["left", "right"]) {
    const p = document.createElement("div");
    p.style.cssText = `position:absolute;bottom:6px;${corner}:10px`;
    p.innerHTML = PUMPKIN;
    root.appendChild(p);
  }
  // The spider: drops down, hangs a moment, climbs back up, and crawls along
  // the top to somewhere else before dropping again.
  const sp = document.createElement("div");
  sp.className = "spider-wrap";
  sp.innerHTML = `<div class="spider-thread"></div>${SPIDER}`;
  root.appendChild(sp);
  let spx = rnd(0.3, 0.7) * W;
  const move = (x, y, secs) => {
    sp.style.transition = secs ? `transform ${secs}s ease-in-out` : "none";
    sp.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`;
  };
  move(spx, calm ? 110 : 12, 0);
  if (calm) return;
  const spider = () => {
    move(spx, rnd(90, 190), 2.6);
    later(() => {
      move(spx, 12, 2.2);
      later(() => {
        const to = Math.min(W * 0.85, Math.max(W * 0.15, spx + (Math.random() < .5 ? -1 : 1) * rnd(0.2, 0.4) * W));
        const secs = Math.abs(to - spx) / 120;
        spx = to;
        move(spx, 12, secs);
        later(spider, secs * 1000 + rnd(800, 2500));
      }, 2400);
    }, 2600 + rnd(1500, 3500));
  };
  later(spider, 1200);

  // Glowing eyes that open in a quiet spot, blink, and close again.
  const eyes = () => {
    const zones = [
      () => [rnd(3, 10), rnd(0.25, 0.85) * H],                  // the left edge
      () => [W - rnd(24, 32), rnd(0.25, 0.85) * H],             // the right edge
      () => [rnd(0.36, 0.52) * W, rnd(12, 32)],                 // the header's empty middle
      () => [rnd(0.15, 0.85) * W, H - rnd(28, 60)],             // down in the fog
    ];
    if (!shown()) return later(eyes, rnd(4000, 9000));
    const [x, y] = zones[Math.floor(Math.random() * zones.length)]();
    const e = document.createElement("div");
    e.className = "eyes";
    e.style.left = Math.round(x) + "px";
    e.style.top = Math.round(y) + "px";
    e.innerHTML = "<i></i><i></i>";
    e.addEventListener("animationend", ev => { if (ev.target === e) e.remove(); });
    root.appendChild(e);
    later(eyes, rnd(4000, 9000));
  };
  later(eyes, 2000);

  // A few autumn leaves drifting down.
  for (let i = 0; i < 12; i++) {
    const l = document.createElement("div");
    l.className = "leaf";
    l.style.cssText = `left:${rnd(0, 100).toFixed(1)}vw;--d:${rnd(12, 22).toFixed(1)}s;--o:-${rnd(0, 22).toFixed(1)}s;--x:${rnd(-80, 80).toFixed(0)}px;--s:${rnd(1.6, 3.2).toFixed(1)}s`;
    l.innerHTML = `<svg viewBox="0 0 20 20"><path d="M10 1C5 5 3 10 5 15l5 4 5-4c2-5 0-10-5-14z M10 3v16" fill="${LEAF_COLORS[i % LEAF_COLORS.length]}" stroke="#5a2a0a" stroke-width=".6"/></svg>`;
    root.appendChild(l);
  }

  // Bats: one every so often, and now and then a whole swarm.
  const oneBat = (topVh, rtl, secs) => {
    const b = document.createElement("div");
    b.className = "bat" + (rtl ? " rtl" : "");
    b.style.cssText = `top:${topVh.toFixed(0)}vh;--d:${secs.toFixed(1)}s`;
    b.innerHTML = BAT;
    b.addEventListener("animationend", e => { if (e.target === b) b.remove(); });
    root.appendChild(b);
  };
  const bat = () => {
    if (shown()) oneBat(rnd(12, 60), Math.random() < .5, rnd(7, 12));
    later(bat, rnd(6000, 15000));
  };
  const swarm = () => {
    if (!shown()) return later(swarm, rnd(60000, 100000));
    const rtl = Math.random() < .5, base = rnd(18, 50), n = 5 + Math.floor(Math.random() * 4);
    for (let i = 0; i < n; i++) later(() => oneBat(base + rnd(-8, 8), rtl, rnd(6, 8)), i * rnd(120, 320));
    later(swarm, rnd(60000, 100000));
  };
  // Now and then a witch crosses the moon; less often, a ghost drifts up.
  const witch = () => {
    if (!shown()) return later(witch, rnd(45000, 80000));
    const w = document.createElement("div");
    w.className = "witch";
    w.innerHTML = WITCH;
    w.addEventListener("animationend", e => { if (e.target === w) w.remove(); });
    root.appendChild(w);
    later(witch, rnd(45000, 80000));
  };
  const ghost = () => {
    if (!shown()) return later(ghost, rnd(30000, 55000));
    const g = document.createElement("div");
    g.className = "ghost";
    g.style.left = rnd(10, 85).toFixed(0) + "vw";
    g.innerHTML = GHOST;
    g.addEventListener("animationend", e => { if (e.target === g) g.remove(); });
    root.appendChild(g);
    later(ghost, rnd(30000, 55000));
  };
  later(bat, 2500);
  later(swarm, rnd(20000, 35000));
  later(witch, rnd(10000, 18000));
  later(ghost, rnd(14000, 24000));
  festiveHooks.newDevice = flareLanterns;
}

BAMF.registerTheme("halloween", ctx => buildHalloween(ctx.root, ctx.switched));
})();
