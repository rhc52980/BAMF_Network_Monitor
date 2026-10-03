// Halloween: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
//
// Behind the page: a night sky with a big moon and clouds crossing it, a haunted house on a hill with
// flickering windows, a graveyard and dead trees. In front of it: cobwebs, a spider, lanterns with embers,
// bats, crows, a witch, ghosts, will-o'-the-wisps, lightning now and then and a skeleton hand. A new device
// nobody has marked known is a werewolf at the graveyard gate.
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
  <path d="M30 12v38M22 14c-6 8-7 24 0 34M38 14c6 8 7 24 0 34" stroke="#b84e08" stroke-width=".8" fill="none" opacity=".6"/>
  <path class="face" d="M17 25l6-6 5 6z M33 25l6-6 5 6z M16 34l5 4 4-3 5 4 5-4 4 3 5-4c-2 8-8 12-14 12s-12-4-14-12z"/></svg>`;

const BAT = `<svg viewBox="0 0 64 28"><path d="M32 10c2-3 2-6 1-8l-1 4-1-4c-1 2-1 5 1 8-6-6-14-9-22-6 4 2 6 5 6 9-4-2-9-2-13 1 5 1 8 4 9 8 3-3 9-4 12-2 2 1 4 4 8 6 4-2 6-5 8-6 3-2 9-1 12 2 1-4 4-7 9-8-4-3-9-3-13-1 0-4 2-7 6-9-8-3-16 0-22 6z"/></svg>`;

const GHOST = `<svg viewBox="0 0 54 64"><path d="M27 2C13 2 5 13 5 27v33l7-6 7 6 8-6 8 6 7-6 7 6V27C49 13 41 2 27 2z" fill="#f4eefa" opacity=".92"/>
  <ellipse cx="19" cy="26" rx="4" ry="6" fill="#1a1020"/><ellipse cx="35" cy="26" rx="4" ry="6" fill="#1a1020"/><ellipse cx="27" cy="40" rx="5" ry="3.5" fill="#1a1020"/>
  <path d="M5 38c-5 2-7 7-5 11M49 38c5 2 7 7 5 11" stroke="#f4eefa" stroke-width="4" fill="none" stroke-linecap="round" opacity=".85"/></svg>`;

const SPIDER = `<svg viewBox="0 0 30 30" width="30" style="position:absolute;left:0;top:214px"><g stroke="#1a0d1f" stroke-width="1.6" fill="none" stroke-linecap="round">
  <path d="M15 14L4 6M15 16L2 15M15 18L4 25M15 20L7 29M15 14L26 6M15 16L28 15M15 18L26 25M15 20L23 29"/></g>
  <circle cx="15" cy="12" r="4" fill="#1a0d1f"/><ellipse cx="15" cy="19" rx="6" ry="7" fill="#1a0d1f"/><circle cx="13.5" cy="11" r="1" fill="#ff3b3b"/><circle cx="16.5" cy="11" r="1" fill="#ff3b3b"/></svg>`;

// Drawn facing left and mirrored, so she flies handle first with the bristles trailing.
const WITCH = `<svg viewBox="0 0 64 34"><g fill="#0a0508" stroke="#ff8c1a" stroke-width=".5" transform="translate(64 0) scale(-1 1)">
  <path d="M2 26L44 20l1 2L3 28z"/><path d="M44 19l16-6-4 8 6 1-17 4z"/>
  <path d="M18 22l6-10 5 1 2 8z"/><circle cx="25" cy="10" r="3.2"/><path d="M20 9l6-9 3 8 5 1-14 2z"/><path d="M24 21l-2 7h3l2-6z"/></g></svg>`;

// A crow in flight.
const CROW = `<svg viewBox="0 0 40 24"><path d="M2 9c5 0 9 1 12 4 2-4 6-7 12-8-3 3-4 5-4 8 4-1 8-3 14-3-5 3-8 5-9 9-3-1-6-1-8 1-2-2-5-2-8-1 0-3-3-7-9-10z"/><circle cx="6" cy="10" r=".9" fill="#ff8c1a" stroke="none"/></svg>`;

// A skeleton hand, reaching up.
const HAND = `<svg viewBox="0 0 70 118"><g fill="#ebe3d2" stroke="#8d8270" stroke-width="1" stroke-linejoin="round">
  <rect x="27" y="70" width="14" height="48" rx="3"/><path d="M27 84h14M27 98h14M27 110h14" fill="none"/>
  <path d="M19 72c-3-12-2-24 3-32h28c5 8 6 20 3 32z"/>
  <rect x="22" y="14" width="7" height="20" rx="3.5"/><rect x="22" y="34" width="7" height="12" rx="3.5"/>
  <rect x="31" y="6" width="7" height="22" rx="3.5"/><rect x="31" y="28" width="7" height="14" rx="3.5"/>
  <rect x="40" y="10" width="7" height="21" rx="3.5"/><rect x="40" y="31" width="7" height="13" rx="3.5"/>
  <rect x="48" y="20" width="6" height="16" rx="3"/><rect x="48" y="36" width="6" height="10" rx="3"/>
  <path d="M20 62L6 48c-3-3 0-8 4-7l14 10z"/></g></svg>`;

// A new device nobody has marked known is an intruder: a werewolf. The moon
// turns blood red, it howls, bats burst across the sky and the lanterns flare
// red; it lopes in and crouches by the graveyard gate along the bottom, eyes
// burning, tagged, until the device is marked known. Then it slinks off into
// the fog, and the lanterns flare for the new arrival.
const WEREWOLF = `<svg viewBox="0 0 130 96" width="112"><g fill="#0a0508" stroke="#ff8c1a" stroke-width=".7" stroke-linejoin="round">
  <path d="M3 44L12 38 22 32 24 26 25 22 27 6 33 20 35 19 41 5 45 22 50 28 53 22 57 30 61 23 65 33 70 27 74 37 82 36 92 38 100 44 108 46 112 44 120 36 126 24 128 20 122 40 116 52 112 56 108 66 110 76 118 90 102 90 100 80 94 74 84 72 70 72 68 84 58 92 46 92 50 86 58 80 60 72 52 64 40 58 28 54 14 52 6 50 13 47 6 45z"/></g>
  <path d="M30 56L44 62M38 58L50 66M58 36L66 46M72 40L80 48" stroke="#ff8c1a" stroke-width=".6" opacity=".45" fill="none"/>
  <path d="M6 45l2 3 2-3z M11 46l2 3 2-3z" fill="#f5efe6"/><path d="M8 50l2-3 2 3z M13 51l2-3 2 3z" fill="#f5efe6"/>
  <path d="M118 90l3 4M112 90l2 5M52 92l-2 4M58 92l-1 4" stroke="#f5efe6" stroke-width="1.2" stroke-linecap="round" fill="none"/>
  <circle class="hw-eye" cx="23" cy="34" r="2.1" fill="#ff2a2a"/></svg>`;
const GATE = (() => {
  const bars = [60, 70, 80, 90, 100, 110, 120, 130, 140, 150];
  const top = x => Math.round(40 - 16 * Math.sin((x - 50) / 110 * Math.PI));
  return `<svg viewBox="0 0 170 104" width="190"><g fill="#120b16" stroke="#6b567a" stroke-width="1">
  <path d="M2 104V70a15 15 0 0 1 30 0v34z"/><rect x="42" y="36" width="12" height="68"/><rect x="156" y="36" width="12" height="68"/>
  <path d="M40 36h16l-3-6h-10z M154 36h16l-3-6h-10z"/></g>
  <g fill="#cfc6dc" stroke="#6b567a" stroke-width=".6"><circle cx="48" cy="24" r="6"/><circle cx="162" cy="24" r="6"/><rect x="45" y="28" width="6" height="3"/><rect x="159" y="28" width="6" height="3"/></g>
  <g fill="#120b16"><circle cx="46" cy="23" r="1.5"/><circle cx="50" cy="23" r="1.5"/><circle cx="160" cy="23" r="1.5"/><circle cx="164" cy="23" r="1.5"/></g>
  <text x="17" y="82" text-anchor="middle" font-family="Space Grotesk, sans-serif" font-weight="700" font-size="8" fill="#6b567a">RIP</text>
  <g stroke="#6b567a" stroke-width="2" fill="none"><path d="M54 56h102M54 96h102"/>
  <path d="${bars.map(x => `M${x} 102V${top(x)}`).join("")}"/></g>
  <path fill="#6b567a" d="${bars.map(x => `M${x} ${top(x) - 6}l-2.6 6h5.2z`).join("")}"/>
  <g class="hw-lamp"><path d="M105 12v10" stroke="#6b567a" stroke-width="1"/><rect x="100" y="22" width="10" height="13" rx="2" fill="#ffb347" stroke="#6b567a" stroke-width="1"/>
  <circle cx="105" cy="28" r="14" fill="#ff9a2a" opacity=".18"/></g></svg>`;
})();

// The haunted house on its hill, with a dead tree beside it. The lit windows flicker.
const HOUSE = (() => {
  const win = (shape, d, o, cls = "") => shape.replace("<", `<`).replace("/>", ` class="hw-win ${cls}" style="--d:${d}s;--o:-${o}s"/>`);
  return `<svg viewBox="0 0 620 440">
  <defs><radialGradient id="hwwin" cx=".5" cy=".4" r=".7"><stop offset="0" stop-color="#ffe9a0"/><stop offset="1" stop-color="#ff9a2a"/></radialGradient>
  <linearGradient id="hwhill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#160b22"/><stop offset="1" stop-color="#07030c"/></linearGradient></defs>
  <g fill="#0b0613" stroke="#2c1744" stroke-width="1" stroke-linejoin="round">
    <rect x="118" y="262" width="118" height="100"/><path d="M106 264L177 208L248 264z"/>
    <rect x="236" y="168" width="72" height="194"/><path d="M224 170L272 78L320 170z"/>
    <path d="M272 78V50M264 60H280" fill="none" stroke-width="2"/>
    <rect x="306" y="248" width="156" height="114"/><path d="M294 250L384 186L474 250z"/><rect x="424" y="172" width="20" height="50"/>
    <rect x="248" y="318" width="48" height="44"/><path d="M244 320L272 304L300 320z"/>
    <path d="M118 262h118M306 248h156" stroke="#3a2158" fill="none"/>
  </g>
  ${win('<path d="M258 238V208a14 14 0 0 1 28 0V238z"/>', 3.1, .3)}
  ${win('<rect x="266" y="118" width="12" height="22" rx="1"/>', 4.2, 1.1, "dim")}
  ${win('<rect x="134" y="282" width="20" height="32"/>', 2.7, .9)}
  ${win('<rect x="198" y="282" width="20" height="32"/>', 3.6, 2.1, "dim")}
  ${win('<rect x="322" y="268" width="24" height="38"/>', 3.4, .5)}
  ${win('<rect x="368" y="268" width="24" height="38"/>', 2.9, 1.7, "dim")}
  ${win('<rect x="414" y="268" width="24" height="38"/>', 4.0, .2)}
  ${win('<circle cx="384" cy="222" r="9"/>', 2.4, 1.4)}
  ${win('<path d="M262 362V338a10 10 0 0 1 20 0V362z"/>', 5.0, .6, "dim")}
  <path d="M0 440V352C70 334 150 322 240 334S420 364 620 322V440z" fill="url(#hwhill)"/>
  <g fill="none" stroke="#0b0613" stroke-linecap="round" stroke-linejoin="round">
    <path d="M58 368C62 322 56 292 46 258" stroke-width="10"/>
    <path d="M52 300C40 290 26 286 12 270" stroke-width="5"/><path d="M56 282C70 266 82 262 96 244" stroke-width="5"/>
    <path d="M48 262C38 246 36 230 28 208" stroke-width="4"/><path d="M70 270C84 262 98 262 112 250" stroke-width="3"/>
    <path d="M30 226C20 222 12 214 8 200M96 246C100 236 108 232 118 232M14 276C10 268 10 260 14 252" stroke-width="2.4"/>
  </g>
  <g stroke="#0b0613" stroke-width="3" fill="#0b0613"><path d="M488 372V342M498 374V338l3-8 3 8v36M510 372V344M522 374V340M534 372V346M546 374V342" fill="none"/></g>
  </svg>`;
})();

// A graveyard to the right, with a dead tree and two crows on it.
const YARD = (() => `<svg viewBox="0 0 520 280">
  <g fill="#0a0511" stroke="#2c1744" stroke-width="1" stroke-linejoin="round">
    <path d="M64 280V196a26 26 0 0 1 52 0V280z"/><path d="M210 280V170M186 196H234" stroke="#0a0511" stroke-width="12" fill="none"/>
    <path d="M286 280V150l14-34 14 34V280z"/><path d="M372 280V212a22 22 0 0 1 44 0V280z" transform="rotate(6 394 280)"/>
    <path d="M10 280V230a18 18 0 0 1 36 0V280z"/>
  </g>
  <g fill="#3a2a55" opacity=".7"><path d="M96 196a26 26 0 0 0-8-18v18z"/><path d="M296 150l4-34 4 34z"/></g>
  <text x="90" y="238" text-anchor="middle" font-family="Space Grotesk, sans-serif" font-weight="700" font-size="14" fill="#3a2a55">RIP</text>
  <g fill="none" stroke="#0a0511" stroke-linecap="round" stroke-linejoin="round">
    <path d="M470 282C468 220 476 170 490 110" stroke-width="12"/>
    <path d="M476 190C456 176 436 172 414 150" stroke-width="6"/><path d="M486 150C506 134 512 120 520 98" stroke-width="5"/>
    <path d="M490 112C478 94 480 74 470 54" stroke-width="4"/><path d="M414 150C404 144 398 134 396 120M436 172C426 176 418 184 412 196" stroke-width="3"/>
    <path d="M470 54C462 50 454 42 452 30M520 98C514 92 506 90 498 92" stroke-width="2.4"/>
  </g>
  <g fill="#0a0511"><path d="M418 150c4-8 10-10 16-8l8-4-4 8c3 4 2 9-2 12l-6-2-3 6-3-6c-3-1-5-3-6-6z"/><path d="M458 176c3-7 9-9 14-8l7-3-3 7c2 4 2 8-2 11l-5-2-3 5-3-5c-3 0-4-2-5-5z"/></g>
  <g stroke="#0a0511" stroke-width="3" fill="none"><path d="M0 282V250M12 282V246M24 282V250M36 282V246M48 282V250"/></g>
  </svg>`)();

const HILLS = `<svg viewBox="0 0 1000 150" preserveAspectRatio="none"><path d="M0 150V84C120 54 240 100 380 78S640 36 780 76 930 70 1000 56V150z" fill="#12091b" opacity=".85"/>
  <path d="M0 150V112C150 92 270 126 420 108S700 82 860 110 960 106 1000 100V150z" fill="#0a0511"/></svg>`;

const MOON_SVG = `<svg viewBox="0 0 200 200"><defs><radialGradient id="hwmoon" cx=".42" cy=".38" r=".7"><stop offset="0" stop-color="#fffbe8"/><stop offset=".6" stop-color="#f3e7b8"/><stop offset="1" stop-color="#d6c383"/></radialGradient></defs>
  <circle cx="100" cy="100" r="96" fill="url(#hwmoon)"/>
  <g fill="#c9b573" opacity=".5"><ellipse cx="68" cy="70" rx="16" ry="14"/><ellipse cx="124" cy="58" rx="9" ry="8"/><ellipse cx="132" cy="112" rx="20" ry="17"/><ellipse cx="78" cy="132" rx="11" ry="10"/><ellipse cx="104" cy="152" rx="7" ry="6"/><ellipse cx="46" cy="106" rx="6" ry="5"/></g>
  <circle class="hw-blood" cx="100" cy="100" r="96" fill="#d8261a" style="mix-blend-mode:multiply"/></svg>`;

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

// The sky, the moon, the clouds, the house and the graveyard: drawn once, behind the page.
function backdrop(W, H) {
  const bg = document.createElement("div");
  bg.className = "hw-bg";
  const size = Math.round(Math.min(250, Math.max(96, W * .15)));
  const mx = Math.round(W * (W < 760 ? .74 : .77)), my = Math.round(Math.max(24, H * .05));
  bg.style.setProperty("--mx", (mx / W * 100).toFixed(1) + "%");
  bg.style.setProperty("--my", ((my + size / 2) / H * 100).toFixed(1) + "%");
  let stars = "";
  for (let i = 0, n = Math.round(W * H / 8000); i < n; i++) {
    const s = rnd(.6, 1.8).toFixed(1), tw = i % 5 === 0;
    stars += `<i class="hw-star${tw ? " tw" : ""}" style="left:${rnd(0, 100).toFixed(1)}%;top:${rnd(0, 72).toFixed(1)}%;width:${s}px;height:${s}px;${tw ? `--d:${rnd(1.6, 4).toFixed(1)}s;--o:-${rnd(0, 4).toFixed(1)}s` : ""}"></i>`;
  }
  let clouds = "";
  [[.18, 1.5, 95, 0], [.5, 1.1, 130, 38], [.72, 1.8, 110, 71]].forEach(([y, wide, secs, off], i) => {
    clouds += `<div class="hw-cloud" style="top:${Math.round(my + size * y)}px;--w:${Math.round(260 * wide + W * .18)}px;--d:${secs}s;--o:-${off}s"></div>`;
  });
  bg.innerHTML = `<div class="hw-sky"></div>${stars}
    <div class="hw-moon" style="left:${mx}px;top:${my}px;--s:${size}px">${MOON_SVG}</div>${clouds}
    <div class="hw-scene"><div class="hw-hills">${HILLS}</div><div class="hw-house">${HOUSE}</div><div class="hw-yard">${YARD}</div></div>`;
  return bg;
}

function buildHalloween(ctx) {
  const root = ctx.root;
  festiveHooks.decorateNode = halloweenMapDeco;
  const NS = "http://www.w3.org/2000/svg";
  const W = window.innerWidth, H = window.innerHeight, calm = calmMotion();
  const later = (fn, ms) => festiveTimers.push(setTimeout(fn, ms));
  // A hidden tab doesn't run animations, so nothing that flies in would ever
  // finish and leave; skip the spawn and just keep the schedule ticking.
  const shown = () => !document.hidden;
  const bg = ctx.background(backdrop(W, H));
  const moon = bg.querySelector(".hw-moon");
  const moonX = parseFloat(moon.style.left), moonY = parseFloat(moon.style.top), moonR = parseFloat(moon.style.getPropertyValue("--s")) / 2;
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
  // A patch of jack-o'-lanterns in each bottom corner, lighting the page, with embers rising.
  for (const corner of ["left", "right"]) {
    const sizes = corner === "left" ? [40, 62, 46] : [46, 62, 40];
    const glow = document.createElement("div");
    glow.className = "hw-glow";
    glow.style[corner] = "-90px";
    root.appendChild(glow);
    const patch = document.createElement("div");
    patch.className = "hw-patch";
    patch.style[corner] = "8px";
    patch.innerHTML = sizes.map((s, i) => `<div style="--w:${s}px;margin-${corner === "left" ? "right" : "left"}:-4px"><div style="animation-delay:-${i * 0.9}s">${PUMPKIN}</div></div>`).join("");
    patch.querySelectorAll(".face").forEach((f, i) => { f.style.animationDelay = `-${(i * 0.8).toFixed(1)}s`; });
    root.appendChild(patch);
    if (!calm) for (let i = 0; i < 7; i++) {
      const e = document.createElement("i");
      e.className = "hw-ember";
      e.style.cssText = `${corner}:${Math.round(rnd(26, 120))}px;bottom:${Math.round(rnd(52, 84))}px;--d:${rnd(2.6, 5).toFixed(1)}s;--o:-${rnd(0, 5).toFixed(1)}s;--x:${Math.round(rnd(-24, 24))}px`;
      root.appendChild(e);
    }
  }
  // Will-o'-the-wisps, drifting about the edges of the page.
  if (!calm) for (let i = 0; i < 9; i++) {
    const wsp = document.createElement("i");
    wsp.className = "hw-wisp" + (i % 3 === 0 ? " o" : "");
    const edge = i % 2 ? rnd(2, 9) : rnd(90, 97);
    wsp.style.cssText = `left:${edge.toFixed(1)}vw;top:${rnd(22, 88).toFixed(1)}vh;--d:${rnd(9, 17).toFixed(1)}s;--o:-${rnd(0, 14).toFixed(1)}s;--x1:${Math.round(rnd(-50, 50))}px;--y1:${Math.round(rnd(-70, 40))}px;--x2:${Math.round(rnd(-80, 80))}px;--y2:${Math.round(rnd(-110, 30))}px`;
    root.appendChild(wsp);
  }

  // ---- intruders: werewolves at the graveyard gate ----
  const watchIn = intruderWatch();
  const wolves = new Map();             // id -> { h, el, tag, alarm, gone, running }
  const room = document.createElement("div");
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  const gateX = Math.min(W * .5, W - 290);
  const gate = document.createElement("div");
  gate.className = "hw-gate";
  gate.innerHTML = GATE;
  gate.hidden = true;
  gate.style.left = Math.round(gateX) + "px";
  root.appendChild(gate);
  // The first by the gate, a second by the gravestone (when there's room).
  const slotX = k => Math.round(k ? gateX - 125 : gateX + 115);
  // One stood down keeps its place until it has gone, so none lands on it.
  const atGate = () => [...wolves.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeWolf = h => {
    const el = document.createElement("div");
    el.className = "hw-wolf";
    el.innerHTML = WEREWOLF;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const w = { h, el, tag, alarm: 0, gone: false, running: false };
    wolves.set(h.id, w);
    return w;
  };
  const settle = () => {
    const list = atGate();
    room.style.height = list.some(w => !w.gone) ? "124px" : "0";
    gate.hidden = !list.length;
    list.forEach((w, k) => {
      w.el.hidden = k > (W < 700 ? 0 : 1);
      w.tag.style.bottom = `calc(100% + ${4 + k * 52}px)`;
      if (!w.running) w.el.style.left = slotX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!wolves.has(h.id)) makeWolf(h);
    for (const h of held) { const w = wolves.get(h.id); if (w && !w.gone) { w.h = h; if (!w.alarm || Date.now() - w.alarm > 9000) intruderTagEl(h, "held", w.tag); } }
    for (const h of cleared) {
      const w = wolves.get(h.id); if (!w || w.gone) continue;
      w.gone = true;
      const done = () => { w.el.remove(); wolves.delete(h.id); settle(); };
      if (calm || !shown()) { done(); continue; }
      // Stood down: it slinks off into the fog, and the lanterns welcome the device.
      intruderTagEl(w.h, "cleared", w.tag);
      later(() => w.el.classList.add("gone"), 2200);
      later(() => { done(); if (!h.test) flareLanterns(); }, 4000);
    }
    settle();
  }
  let bloodUntil = 0;
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const w = wolves.get(h.id) || makeWolf(h);
    if (calm || !shown()) { settle(); return; }
    w.alarm = Date.now(); w.running = true;
    intruderTagEl(h, "alarm", w.tag);
    settle();
    // In it lopes from the right, to the gate.
    w.el.style.transition = "none"; w.el.style.left = (W + 30) + "px"; w.el.classList.add("run");
    void w.el.offsetWidth;
    w.el.style.transition = "left 2.4s cubic-bezier(.3, .1, .4, 1)";
    w.el.style.left = slotX(0) + "px";
    later(() => { w.running = false; w.el.style.transition = ""; w.el.classList.remove("run"); settle(); }, 2450);
    later(() => { if (!w.gone) intruderTagEl(w.h, "held", w.tag); }, 9000);
    // A blood moon, a howl, the lanterns red and the bats out.
    bloodUntil = Date.now() + 9000;
    moon.classList.add("blood");
    later(() => { if (Date.now() >= bloodUntil - 50) moon.classList.remove("blood"); }, 9000);
    const howl = document.createElement("div");
    howl.className = "hw-howl";
    howl.textContent = "AWOOOOOOO!";
    howl.style.left = Math.round(moonX) + "px";
    howl.style.top = Math.round(moonY + moonR * 2 + 4) + "px";
    root.appendChild(howl);
    later(() => howl.remove(), 3600);
    root.querySelectorAll(".pumpkin").forEach(p => p.classList.add("blood"));
    later(() => root.querySelectorAll(".pumpkin").forEach(p => p.classList.remove("blood")), 7000);
    for (let i = 0; i < 8; i++) later(() => {
      const b = document.createElement("div");
      b.className = "bat" + (i % 2 ? " rtl" : "");
      b.style.cssText = `top:${rnd(6, 40).toFixed(0)}vh;--d:${rnd(4.5, 6.5).toFixed(1)}s`;
      b.innerHTML = BAT;
      b.addEventListener("animationend", e => { if (e.target === b) b.remove(); });
      root.appendChild(b);
    }, 300 + i * 140);
    lightning(true);
  };
  festiveHooks.rendered = syncIntruders;
  syncIntruders();

  // A bolt of lightning: the sky and the scene flash, a jagged line forks down, and the page lights up.
  function lightning(quick) {
    if (calm || !shown()) return;
    const x = rnd(.08, .92) * W, reach = rnd(.4, .72) * H;
    const pts = [[x, 0]];
    for (let y = 0, px = x; y < reach;) { y += rnd(26, 58); px += rnd(-34, 34); pts.push([px, Math.min(y, reach)]); }
    const branch = pts[Math.floor(pts.length / 2)], bp = [branch];
    for (let i = 0, y = branch[1], px = branch[0]; i < 4; i++) { y += rnd(18, 36); px += rnd(10, 36) * (Math.random() < .5 ? -1 : 1); bp.push([px, y]); }
    const line = p => "M" + p.map(q => q[0].toFixed(0) + " " + q[1].toFixed(0)).join("L");
    const s = document.createElementNS(NS, "svg");
    s.setAttribute("class", "hw-bolt"); s.setAttribute("width", W); s.setAttribute("height", H);
    s.innerHTML = `<path d="${line(pts)}" stroke-width="2.4"/><path d="${line(bp)}" stroke-width="1.4"/>`;
    const flash = document.createElement("div");
    flash.className = "hw-flash";
    root.appendChild(flash); root.appendChild(s);
    bg.classList.add("flash");
    later(() => bg.classList.remove("flash"), 130);
    later(() => bg.classList.add("flash"), 260);
    later(() => bg.classList.remove("flash"), 360);
    later(() => { flash.remove(); s.remove(); }, 900);
  }
  const storm = () => { lightning(); later(storm, rnd(40000, 90000)); };
  later(storm, rnd(16000, 26000));

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
  // A flock of crows crosses the sky now and then.
  const crows = () => {
    if (!shown()) return later(crows, rnd(50000, 90000));
    const c = document.createElement("div"), rtl = Math.random() < .5, n = 3 + Math.floor(Math.random() * 3);
    c.className = "hw-crows" + (rtl ? " rtl" : "");
    c.style.cssText = `top:${rnd(7, 30).toFixed(0)}vh;--d:${rnd(11, 15).toFixed(1)}s`;
    c.innerHTML = Array.from({ length: n }, (_, i) => `<span style="margin-top:${Math.round(rnd(-14, 14))}px;animation-delay:-${(i * .11).toFixed(2)}s">${CROW}</span>`).join("");
    c.addEventListener("animationend", e => { if (e.target === c) c.remove(); });
    root.appendChild(c);
    later(crows, rnd(50000, 90000));
  };
  // Across the moon goes a witch; a ghost drifts up; and out of the ground, now and then, comes a hand.
  const witch = () => {
    if (!shown()) return later(witch, rnd(45000, 80000));
    const w = document.createElement("div");
    w.className = "witch";
    w.style.top = Math.round(Math.max(0, moonY + moonR * .4)) + "px";
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
  const hand = () => {
    if (!shown()) return later(hand, rnd(90000, 150000));
    const hd = document.createElement("div");
    hd.className = "hw-hand";
    hd.style.left = Math.round(rnd(.12, .84) * W) + "px";
    hd.innerHTML = HAND;
    hd.addEventListener("animationend", e => { if (e.target === hd) hd.remove(); });
    root.appendChild(hd);
    later(hand, rnd(90000, 150000));
  };
  later(bat, 2500);
  later(swarm, rnd(20000, 35000));
  later(crows, rnd(8000, 14000));
  later(witch, rnd(10000, 18000));
  later(ghost, rnd(14000, 24000));
  later(hand, rnd(20000, 32000));
}

BAMF.registerTheme("halloween", ctx => buildHalloween(ctx));
})();
