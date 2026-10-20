// Thanksgiving: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
//
// Behind the page: a harvest dusk. A big orange moon comes up behind a farm, with its barn, silo and a house whose windows
// flicker and whose chimney smokes; a scarecrow in the corn, shocks and hay bales, and maples in red and gold that shed
// leaves into a wind that comes and goes. Geese go over in a V. In front: a garland of leaves and gourds along the top, a
// cornucopia and a stack of pumpkins in the corners, and a turkey that struts through now and then.
//
// A new device nobody has marked known is an intruder: a raccoon at the pie. The edges of the page glow red, the leaves
// whip up and it's GOBBLE GOBBLE GOBBLE; it scurries in and sits by the pie along the bottom, a slice in its paws, tagged,
// until the device is marked known. Then it scurries off, the leaves blow through, and a turkey comes by to see who's new.
(() => {
const TG_RACCOON = `<svg viewBox="0 0 76 74" width="76" height="74" aria-hidden="true">
  <path d="M44 62c14 4 28-2 30-16 1-8-4-12-8-10 3 4 2 10-4 14s-12 4-18 3z" fill="#7a7570"/>
  <path d="M61 57l4-9M67 52l3-10M55 60l2-7" stroke="#2a2624" stroke-width="4" stroke-linecap="round"/>
  <ellipse cx="32" cy="50" rx="17" ry="19" fill="#8a8580"/><ellipse cx="32" cy="54" rx="10" ry="13" fill="#b5b0aa"/>
  <ellipse cx="22" cy="70" rx="7" ry="3.5" fill="#5a5652"/><ellipse cx="42" cy="70" rx="7" ry="3.5" fill="#5a5652"/>
  <path d="M19 14l-2-11 10 6zM45 14l2-11-10 6z" fill="#6e6a66"/>
  <ellipse cx="32" cy="22" rx="15" ry="13" fill="#9a958f"/>
  <path d="M18 23q5-8 14-4 9-4 14 4-5 6-14 3-9 3-14-3z" fill="#1d1a19"/>
  <circle cx="25" cy="22" r="2.2" fill="#fff"/><circle cx="39" cy="22" r="2.2" fill="#fff"/><circle cx="25.5" cy="22" r="1" fill="#000"/><circle cx="38.5" cy="22" r="1" fill="#000"/>
  <path d="M27 29q5 4 10 0" fill="#e9e5df"/><circle cx="32" cy="29" r="2" fill="#1d1a19"/>
  <path d="M20 42l18-6-4 12z" fill="#e8a33d" stroke="#b2641f" stroke-width="1.2" stroke-linejoin="round"/><path d="M20 42l18-6" stroke="#c77a2a" stroke-width="3" stroke-linecap="round"/>
  <ellipse cx="21" cy="44" rx="4" ry="3" fill="#6e6a66"/><ellipse cx="36" cy="41" rx="4" ry="3" fill="#6e6a66"/></svg>`;

const TG_PIE = `<svg viewBox="0 0 90 34" width="90" height="34" aria-hidden="true">
  <path d="M4 14h82l-6 18H10z" fill="#a9b0b8" stroke="#6b7079" stroke-width="1.2"/>
  <path d="M6 14q39-12 78 0z" fill="#d99a4a"/><path d="M6 14h78" stroke="#b2641f" stroke-width="4" stroke-linecap="round"/>
  <path d="M20 9l8 5M34 6l10 8M50 6l10 8M64 8l8 6M28 7l-6 7M46 5l-8 9M62 6l-8 8M74 9l-6 5" stroke="#b2641f" stroke-width="2" stroke-linecap="round"/>
  <path d="M60 14l10-8 14 8z" fill="#3a2410"/><path d="M60 14l10-8" stroke="#e8a33d" stroke-width="2"/></svg>`;

// A turkey, facing right: tail fanned behind him, wattle, and a head that
// dips to peck as he walks.
const TG_TURKEY = `<svg width="150" height="120" viewBox="0 0 150 120">
  <g class="fan">
    <path d="M18 78C2 60 6 26 30 12c-8 18-6 40 6 54z" fill="#8c4a21"/>
    <path d="M30 80C16 56 26 22 52 12c-12 18-14 44-6 62z" fill="#b25c26"/>
    <path d="M44 82C36 54 50 22 76 16c-16 18-22 44-16 64z" fill="#d1652f"/>
    <path d="M60 84C58 56 74 26 98 24c-18 16-26 40-24 58z" fill="#e8a33d"/>
    <path d="M76 86C80 60 96 36 118 36c-18 14-28 32-28 50z" fill="#c7a23f"/>
  </g>
  <ellipse cx="86" cy="86" rx="32" ry="24" fill="#5a3218"/>
  <ellipse cx="94" cy="92" rx="22" ry="15" fill="#6e401f"/>
  <g class="head">
    <path d="M106 70c8-10 22-12 28-4" fill="none" stroke="#5a3218" stroke-width="11" stroke-linecap="round"/>
    <circle cx="130" cy="62" r="11" fill="#7d4a24"/>
    <path d="M139 60l10 4-10 5z" fill="#e8a33d"/>
    <path d="M128 72q4 10-2 14" fill="none" stroke="#d1452f" stroke-width="5" stroke-linecap="round"/>
    <circle cx="133" cy="58" r="2.4" fill="#1d0e04"/>
  </g>
  <g stroke="#e8a33d" stroke-width="5" stroke-linecap="round"><path d="M80 108v8M96 108v8"/></g>
  <g stroke="#e8a33d" stroke-width="4" stroke-linecap="round"><path d="M74 116h12M90 116h12"/></g>
</svg>`;

const TG_LEAF = ["#d1452f", "#e8873d", "#e8a33d", "#a7642f", "#8c6b2f", "#a7c04f"];
// Autumn's colours as they go: the trees, and the garland.
const TG_CANOPY = [["#b5301f", "#d1452f", "#e8873d"], ["#c9701f", "#e8a33d", "#f0c15a"], ["#8f2a1a", "#b5301f", "#d1652f"]];

// A maple leaf, 24 wide, stem at the bottom.
const MAPLE = "M12 0.8L14.3 6 18 3.6 17 9.2 22.6 7.6 19.4 12.2 23.4 14.8 17.6 15.6 18.4 19.6 13.2 17.8 13.2 23.2 10.8 23.2 10.8 17.8 5.6 19.6 6.4 15.6 0.6 14.8 4.6 12.2 1.4 7.6 7 9.2 6 3.6 9.7 6Z";
const mapleSvg = (col, w = 24, vein = "rgba(60,25,8,.45)") => `<svg viewBox="0 0 24 24" width="${w}" height="${w}" aria-hidden="true"><path d="${MAPLE}" fill="${col}"/><path d="M12 6v17M12 12l-5-3M12 12l5-3M12 16l-4-1.5M12 16l4-1.5" stroke="${vein}" stroke-width=".9" fill="none" stroke-linecap="round"/></svg>`;

function tgSeeded(seed) { let s = seed >>> 0; return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; }; }

// ---- the dusk behind the page ----

// A tree in its colours: a trunk, two boughs and a crown of overlapping circles.
function tgTree(x, y, h, r, pal, dim = 1) {
  const w = h * .56, cy = -h * .7;
  let crown = "";
  for (let i = 0; i < 22; i++) {
    const a = r() * 6.283, d = Math.sqrt(r()) * w * .5;
    const rad = w * (.16 + r() * .14);
    crown += `<circle cx="${(Math.cos(a) * d).toFixed(1)}" cy="${(cy + Math.sin(a) * d * .78).toFixed(1)}" r="${rad.toFixed(1)}" fill="${pal[Math.floor(r() * 2.4)]}"/>`;
  }
  for (let i = 0; i < 7; i++) {
    const a = 3.4 + r() * 1.8, d = w * (.2 + r() * .22);
    crown += `<circle cx="${(Math.cos(a) * d).toFixed(1)}" cy="${(cy + Math.sin(a) * d * .78).toFixed(1)}" r="${(w * (.07 + r() * .06)).toFixed(1)}" fill="${pal[2]}" opacity=".75"/>`;
  }
  return `<g transform="translate(${x} ${y})" opacity="${dim}"><g class="tg-tree" style="--d:${(6 + r() * 4).toFixed(1)}s;--o:-${(r() * 5).toFixed(1)}s">
    <path d="M${-h * .04} 0L${-h * .022} ${-h * .5}L${h * .022} ${-h * .5}L${h * .04} 0Z" fill="#2a1710"/>
    <path d="M0 ${-h * .3}Q${-w * .28} ${-h * .44} ${-w * .42} ${-h * .6}M0 ${-h * .34}Q${w * .26} ${-h * .46} ${w * .4} ${-h * .62}" stroke="#2a1710" stroke-width="${(h * .03).toFixed(1)}" fill="none" stroke-linecap="round"/>
    ${crown}</g></g>`;
}

// The whole landscape as one picture, 1600 by 700, standing on the bottom edge of the window.
function tgLand() {
  const r = seeded(7), VW = 1600, VH = 700;
  const far = x => 405 + 38 * Math.sin(x / 310) + 14 * Math.sin(x / 105 + 2);
  const mid = x => 492 + 20 * Math.sin(x / 235 + 1) + 9 * Math.sin(x / 88);
  const near = x => 612 + 16 * Math.sin(x / 270 + .6) + 6 * Math.sin(x / 70);
  const ridge = (f, step = 16) => { let d = `M0 ${f(0).toFixed(1)}`; for (let x = step; x <= VW + step; x += step) d += `L${x} ${f(x).toFixed(1)}`; return d + `V${VH}H0Z`; };

  // The far hills, with a line of trees along them.
  let s = `<path d="${ridge(far)}" fill="#4a2230"/>`;
  for (let x = 20; x < VW; x += 12 + r() * 22) {
    const h = 14 + r() * 22;
    s += `<path d="M${x} ${far(x) + 3}l${-h * .3} 0 ${h * .3} ${-h}z" fill="#3b1b28" opacity=".9"/><circle cx="${x}" cy="${(far(x) - h * .55).toFixed(1)}" r="${(h * .32).toFixed(1)}" fill="${r() < .5 ? "#6a2b26" : "#5a2a2a"}" opacity=".85"/>`;
  }
  s += `<path d="${ridge(x => mid(x) - 14 + 28 * Math.sin(x / 400))}" fill="#3d1d28" opacity=".7"/>`;

  // The middle hill and the farm on it: a barn and silo, and the house.
  s += `<path d="${ridge(mid)}" fill="#351a22"/>`;
  const gy = mid(1000) + 5;
  s += `<g transform="translate(0 ${(gy - 480).toFixed(1)})">
    <rect x="936" y="402" width="26" height="80" fill="#4f2a26"/><path d="M934 404a15 15 0 0 1 30 0z" fill="#33191c"/><rect x="934" y="430" width="30" height="3" fill="#2a1316"/><rect x="934" y="452" width="30" height="3" fill="#2a1316"/>
    <path d="M806 478V438L830 418 884 418 908 438V478Z" fill="#7a2c22"/><path d="M798 440L830 410 884 410 916 440 908 442 884 420 830 420 806 442Z" fill="#2a1316"/>
    <path d="M832 478V452h50v26z" fill="#5d211b"/><path d="M832 452l50 26M882 452l-50 26" stroke="#d8c3a2" stroke-width="2.4" opacity=".75"/><rect x="843" y="430" width="28" height="12" fill="#ffcf6b" class="tg-win"/>
    <rect x="1008" y="428" width="132" height="54" fill="#5a3326"/><path d="M996 432L1074 376 1152 432Z" fill="#2b1514"/><path d="M996 432L1074 376" stroke="#4a2420" stroke-width="3" fill="none"/>
    <rect x="1112" y="378" width="15" height="40" fill="#35201c"/>
    <rect x="1018" y="441" width="22" height="25" fill="#ffcf6b" class="tg-win"/><rect x="1049" y="441" width="22" height="25" fill="#ffcf6b" class="tg-win b"/><rect x="1103" y="441" width="22" height="25" fill="#ffcf6b" class="tg-win c"/>
    <path d="M1018 453h22M1029 441v25M1049 453h22M1060 441v25M1103 453h22M1114 441v25" stroke="#3a1f18" stroke-width="1.6"/>
    <rect x="1080" y="452" width="16" height="30" fill="#3b2015"/><circle cx="1092" cy="468" r="1.2" fill="#e8a33d"/><rect x="1066" y="398" width="16" height="16" fill="#ffcf6b" class="tg-win b"/><path d="M1066 406h16M1074 398v16" stroke="#3a1f18" stroke-width="1.4"/>
    <circle cx="1100" cy="446" r="22" fill="#ffcf6b" opacity=".07"/>
    <g class="tg-smoke">${[0, 1, 2, 3].map(k => `<circle cx="1119" cy="374" r="6" fill="#d9c9bd" style="--d:${(7 + k * .6).toFixed(1)}s;--o:-${(k * 1.8).toFixed(1)}s"/>`).join("")}</g>
  </g>`;

  // Fence posts and rails along the middle hill, on its left.
  let rail = "";
  for (let x = 40; x < 740; x += 52) {
    const y = mid(x) + 14;
    rail += `<path d="M${x} ${y}v-26" stroke="#241015" stroke-width="5" stroke-linecap="round"/>`;
    if (x + 52 < 740) rail += `<path d="M${x} ${y - 20}L${x + 52} ${(mid(x + 52) + 14 - 20).toFixed(1)}M${x} ${y - 9}L${x + 52} ${(mid(x + 52) + 14 - 9).toFixed(1)}" stroke="#241015" stroke-width="3" stroke-linecap="round"/>`;
  }
  s += rail;
  // Trees on the middle hill, dim with distance.
  for (const [x, h, k] of [[250, 120, 0], [600, 140, 1], [730, 100, 2], [1230, 132, 0], [1330, 110, 1]]) s += tgTree(x, mid(x) + 16, h, r, TG_CANOPY[k], .78);

  // The field: rows of corn, then shocks, bales and a scarecrow.
  s += `<path d="${ridge(near)}" fill="#2a130f"/>`;
  let rows = "";
  for (let i = -12; i < 26; i++) { const x0 = i * 74; rows += `M${x0 + 60} 700L${x0 + 200 + i * 8} 600 `; }
  s += `<path d="${rows}" stroke="#c98a3a" stroke-width="2" opacity=".14" fill="none"/>`;
  const shock = (x, h) => {
    const y = near(x) + 22 + r() * 18;
    let g = `<g transform="translate(${x} ${y.toFixed(1)})"><ellipse cx="0" cy="2" rx="${h * .34}" ry="${h * .06}" fill="#000" opacity=".3"/>`;
    for (let k = -4; k <= 4; k++) g += `<path d="M${(k * h * .08).toFixed(1)} 0L${(k * h * .012).toFixed(1)} ${-h}" stroke="${k % 2 ? "#b98a3c" : "#9c7230"}" stroke-width="${(h * .07).toFixed(1)}" stroke-linecap="round"/>`;
    return g + `<path d="M${-h * .14} ${-h * .52}L${h * .14} ${-h * .5}" stroke="#5a3d1b" stroke-width="4" stroke-linecap="round"/><path d="M${-h * .06} ${-h * .98}L${h * .06} ${-h * .98}" stroke="#7e5a24" stroke-width="3" stroke-linecap="round"/></g>`;
  };
  const bale = (x, big) => {
    const y = near(x) + 30 + r() * 14, w = big ? 64 : 48, h = w * .62;
    return `<g transform="translate(${x} ${y.toFixed(1)})"><ellipse cx="0" cy="2" rx="${w * .6}" ry="5" fill="#000" opacity=".3"/><rect x="${-w / 2}" y="${-h}" width="${w}" height="${h}" rx="6" fill="#c7a23f"/><path d="M${-w / 2 + 4} ${-h * .66}H${w / 2 - 4}M${-w / 2 + 4} ${-h * .33}H${w / 2 - 4}" stroke="#8a6a1f" stroke-width="2"/><path d="M${-w * .16} ${-h}V0M${w * .16} ${-h}V0" stroke="#7a5a1a" stroke-width="2.4"/></g>`;
  };
  for (const x of [330, 690, 1190, 1560]) s += shock(x, 78 + r() * 18);
  for (const [x, big] of [[470, 1], [1090, 0], [1380, 1]]) s += bale(x, big);
  // The scarecrow, in plaid, with two crows on him.
  const sx = 840, sy = near(sx) + 44;
  s += `<g transform="translate(${sx} ${sy.toFixed(1)})" class="tg-scare">
    <path d="M0 0V-118M-44 -92H44" stroke="#4a2c14" stroke-width="5" stroke-linecap="round"/>
    <path d="M-20 -104L20 -104 16 -60 -16 -60Z" fill="#8a2c22"/><path d="M-20 -92H20M-20 -78H20M-8 -104V-60M8 -104V-60" stroke="#2a1310" stroke-width="1.6" opacity=".6"/>
    <path d="M-44 -92l-10 14M-40 -92l-8 18M44 -92l10 14M40 -92l8 18M-12 -60l-3 12M0 -60v14M12 -60l3 12" stroke="#d9b95a" stroke-width="2.4" stroke-linecap="round"/>
    <circle cx="0" cy="-122" r="14" fill="#d8b98a"/><path d="M-7 -124l4 0M3 -124l4 0M-5 -115q5 4 10 0" stroke="#3b2015" stroke-width="1.6" fill="none" stroke-linecap="round"/>
    <path d="M-24 -130H24L14 -156 -14 -156Z" fill="#35201c"/><rect x="-26" y="-134" width="52" height="6" rx="2" fill="#241015"/><rect x="-14" y="-140" width="28" height="5" fill="#c9702f"/>
    <g class="tg-crow" transform="translate(-40 -100)"><ellipse cx="0" cy="0" rx="9" ry="5.5" fill="#150b10"/><circle cx="-8" cy="-4" r="4" fill="#150b10"/><path d="M-12 -4l-5 2 5 1z" fill="#8a6a2a"/><path d="M8 0l8 4-9-1z" fill="#150b10"/></g>
    <g class="tg-crow b" transform="translate(40 -96) scale(-1 1)"><ellipse cx="0" cy="0" rx="9" ry="5.5" fill="#150b10"/><circle cx="-8" cy="-4" r="4" fill="#150b10"/><path d="M-12 -4l-5 2 5 1z" fill="#8a6a2a"/><path d="M8 0l8 4-9-1z" fill="#150b10"/></g></g>`;

  // The two big maples in front, either side, and their fallen leaves.
  s += tgTree(120, near(120) + 70, 400, r, TG_CANOPY[0]);
  s += tgTree(VW - 110, near(VW - 110) + 66, 360, r, TG_CANOPY[1]);
  for (const x of [120, VW - 110]) for (let i = 0; i < 26; i++) {
    s += `<g transform="translate(${(x + (r() - .5) * 200).toFixed(1)} ${(near(x) + 64 + r() * 30).toFixed(1)}) rotate(${(r() * 360).toFixed(0)}) scale(${(.5 + r() * .5).toFixed(2)})"><path d="${MAPLE}" fill="${TG_LEAF[Math.floor(r() * 4)]}" transform="translate(-12 -12)" opacity=".92"/></g>`;
  }
  return `<svg viewBox="0 0 ${VW} ${VH}" preserveAspectRatio="xMidYMax slice" aria-hidden="true">${s}</svg>`;
}
function seeded(seed) { return tgSeeded(seed); }

// The sky: a gradient down to the glow at the horizon, stars, the harvest moon and its clouds.
function tgSky() {
  const r = tgSeeded(5);
  let stars = "";
  for (let i = 0; i < 70; i++) {
    const sz = (r() < .15 ? 2.6 : 1.5 + r()).toFixed(1);
    stars += `<i class="tg-star${r() < .35 ? " tw" : ""}" style="left:${(r() * 100).toFixed(1)}%;top:${(r() * 46).toFixed(1)}%;width:${sz}px;height:${sz}px;--d:${(1.8 + r() * 3).toFixed(1)}s;--o:-${(r() * 4).toFixed(1)}s"></i>`;
  }
  const clouds = [0, 1, 2].map(k => `<div class="tg-cloud" style="--w:${360 + k * 140}px;top:${14 + k * 11}vh;--d:${150 + k * 55}s;--o:-${k * 70}s"></div>`).join("");
  const moon = `<div class="tg-moon"><svg viewBox="0 0 120 120" aria-hidden="true">
    <circle cx="60" cy="60" r="56" fill="url(#tgMoon)"/>
    <circle cx="40" cy="44" r="9" fill="#e07d22" opacity=".35"/><circle cx="76" cy="70" r="12" fill="#e07d22" opacity=".3"/><circle cx="62" cy="30" r="6" fill="#e07d22" opacity=".3"/><circle cx="48" cy="82" r="7" fill="#e07d22" opacity=".28"/>
    <defs><radialGradient id="tgMoon" cx=".42" cy=".38" r=".75"><stop offset="0" stop-color="#ffd48a"/><stop offset=".7" stop-color="#f7a444"/><stop offset="1" stop-color="#e5782a"/></radialGradient></defs></svg></div>`;
  return `<div class="tg-sky"></div><div class="tg-stars">${stars}</div>${moon}${clouds}`;
}

// A V of geese, flapping, as they cross the sky.
function tgGeese(n) {
  const goose = (x, y, k) => `<g transform="translate(${x} ${y})"><g class="tg-flap" style="--o:-${(k * .17).toFixed(2)}s"><path d="M-17 2Q-9-9 0 0Q9-9 17 2Q9-2 0 5Q-9-2-17 2Z" fill="#150b10"/></g></g>`;
  let b = "";
  for (let i = 0; i < n; i++) {
    const side = i % 2 ? 1 : -1, rank = Math.ceil(i / 2);
    b += goose(rank * 26 * side * .9, rank * 14, i);
  }
  return `<svg viewBox="-110 -14 220 ${14 * n / 2 + 40}" width="${Math.round(60 + n * 8)}" aria-hidden="true">${b}</svg>`;
}

// ---- the garland along the top, and the corners ----

function tgGarland(W) {
  const span = W < 700 ? 130 : 190, sag = 9;
  let wire = "", hang = "";
  const cols = ["#d1452f", "#e8873d", "#e8a33d", "#a7642f", "#b5301f", "#c7a23f"];
  for (let x0 = 0, i = 0; x0 < W; x0 += span, i++) {
    const x1 = x0 + span, cx = (x0 + x1) / 2;
    wire += `M${x0} 3Q${cx} ${2 * sag} ${x1} 3`;
    const n = Math.max(3, Math.round(span / 40));
    for (let k = 0; k < n; k++) {
      const t = (k + .5) / n, u = 1 - t;
      const bx = u * u * x0 + 2 * u * t * cx + t * t * x1, by = u * u * 3 + 2 * u * t * 2 * sag + t * t * 3;
      const ang = Math.round((t - .5) * -50 + (k % 2 ? 14 : -14)), col = cols[(i * 3 + k) % cols.length];
      hang += `<g transform="translate(${bx.toFixed(1)} ${by.toFixed(1)}) rotate(${ang})"><g class="tg-sway" style="--d:${(3 + (k % 3) * .6).toFixed(1)}s;--o:-${(k * .5).toFixed(1)}s"><g transform="translate(-7.5 -3) scale(.62)"><path d="${MAPLE}" fill="${col}"/><path d="M12 6v17M12 12l-5-3M12 12l5-3" stroke="rgba(60,25,8,.4)" stroke-width=".9" fill="none"/></g></g></g>`;
    }
    if (i % 2 === 1 && x1 > W * .36 && x1 < W * .62) {
      const len = 14 + (i * 7) % 16;
      hang += `<g class="orn" style="transform-origin:${x1}px 3px;--d:${(2.6 + (i % 3) * .5).toFixed(1)}s"><line x1="${x1}" y1="3" x2="${x1}" y2="${3 + len}" stroke="#7a5a2a" stroke-width="1.4"/><ellipse cx="${x1}" cy="${len + 12}" rx="8" ry="9" fill="${i % 4 === 1 ? "#e8873d" : "#c9702f"}"/><path d="M${x1 - 4} ${len + 8}q4-4 8 0" stroke="#ffd98a" stroke-width="1.4" fill="none" opacity=".6"/><path d="M${x1} ${len + 2}v-3" stroke="#4a6a24" stroke-width="2.4" stroke-linecap="round"/></g>`;
    }
  }
  return `<path d="${wire}" fill="none" stroke="#6b4a24" stroke-width="1.8"/>${hang}`;
}

const TG_CORNUCOPIA = `<svg viewBox="0 0 130 84" width="124" aria-hidden="true">
  <path d="M6 70C8 40 34 20 72 20c18 0 32 8 36 22 3 12-8 22-22 22-14 0-26 6-36 12-16 10-34 6-44-6z" fill="#b9843a" stroke="#6b4420" stroke-width="1.6" stroke-linejoin="round"/>
  <path d="M16 62c6-16 24-28 50-30M22 70c10-14 28-24 54-26M34 74c8-8 24-16 44-18" stroke="#8a5e24" stroke-width="2" fill="none" stroke-linecap="round"/>
  <path d="M96 20c14 2 26 12 24 28-1 14-14 20-26 18 10-4 14-14 12-24-1-9-4-16-10-22z" fill="#a2702f" stroke="#6b4420" stroke-width="1.4"/>
  <ellipse cx="108" cy="44" rx="13" ry="22" fill="#3a2110" transform="rotate(-8 108 44)"/>
  <circle cx="104" cy="40" r="15" fill="#e8873d"/><path d="M104 25q-4 12 0 30M96 29q-5 11-1 24M112 29q4 11 1 24" stroke="#c2591a" stroke-width="1.6" fill="none"/><path d="M104 25l1-6" stroke="#4a6a24" stroke-width="3" stroke-linecap="round"/>
  <circle cx="116" cy="58" r="10" fill="#c23b2b"/><path d="M116 48q3-3 5-1" stroke="#4a6a24" stroke-width="2" fill="none"/><circle cx="113" cy="55" r="2.4" fill="#fff" opacity=".4"/>
  <g fill="#7a3a8a"><circle cx="92" cy="66" r="5"/><circle cx="101" cy="68" r="5"/><circle cx="96" cy="74" r="5"/><circle cx="105" cy="76" r="5"/><circle cx="99" cy="81" r="5"/></g>
  <path d="M84 52c6-8 18-14 30-12-6 8-18 14-30 12z" fill="#e8b73d" stroke="#a87a1f" stroke-width="1"/><path d="M88 50l22-8" stroke="#a87a1f" stroke-width="1"/>
  <g transform="translate(70 6) rotate(-18) scale(.9)"><path d="${MAPLE}" fill="#d1452f"/></g></svg>`;

const TG_PUMPKINS = `<svg viewBox="0 0 96 78" width="92" aria-hidden="true">
  <g><ellipse cx="26" cy="58" rx="26" ry="19" fill="#d1652f"/><ellipse cx="14" cy="58" rx="14" ry="18" fill="#e8873d"/><ellipse cx="38" cy="58" rx="14" ry="18" fill="#e8873d"/><ellipse cx="26" cy="58" rx="9" ry="19" fill="#f09a4a"/><path d="M26 40q-1-6 3-9" stroke="#4a6a24" stroke-width="4" stroke-linecap="round" fill="none"/></g>
  <g><ellipse cx="68" cy="60" rx="24" ry="17" fill="#c9701f"/><ellipse cx="57" cy="60" rx="12" ry="16" fill="#dd8a2f"/><ellipse cx="79" cy="60" rx="12" ry="16" fill="#dd8a2f"/><ellipse cx="68" cy="60" rx="8" ry="17" fill="#eba04a"/><path d="M68 44q2-6-2-9" stroke="#4a6a24" stroke-width="4" stroke-linecap="round" fill="none"/></g>
  <g><ellipse cx="46" cy="32" rx="17" ry="13" fill="#e8873d"/><ellipse cx="38" cy="32" rx="9" ry="12" fill="#f09a4a"/><ellipse cx="54" cy="32" rx="9" ry="12" fill="#f09a4a"/><path d="M46 19q1-7 5-8" stroke="#4a6a24" stroke-width="3.6" stroke-linecap="round" fill="none"/><path d="M52 12q8-6 14 0" stroke="#4a6a24" stroke-width="1.6" fill="none" stroke-linecap="round"/></g></svg>`;

// On the Map, a pilgrim's hat on each device.
function tgMapDeco(g, n) {
  if (n.type !== "dev") return;
  const hat = svgEl("g", { class: "t-hat", transform: "translate(-1,-18) rotate(-12)" });
  hat.appendChild(svgEl("path", { d: "M-6 3L-4.5 -10 4.5 -10 6 3Z", fill: "#1d1410" }));
  hat.appendChild(svgEl("rect", { x: -11, y: 2, width: 22, height: 3.4, rx: 1.6, fill: "#1d1410" }));
  hat.appendChild(svgEl("rect", { x: -5.6, y: -2.4, width: 11.2, height: 3.6, fill: "#c9a23f" }));
  hat.appendChild(svgEl("rect", { x: -1.8, y: -2.8, width: 3.6, height: 4.4, fill: "none", stroke: "#1d1410", "stroke-width": 1 }));
  g.insertBefore(hat, g.querySelector(".t-name"));
}

function buildThanksgiving(root) {
  festiveHooks.decorateNode = tgMapDeco;
  const calm = calmMotion();
  const W = innerWidth, H = innerHeight, NS = "http://www.w3.org/2000/svg";
  const dpr = Math.min(devicePixelRatio || 1, 2);

  // ---- behind the page ----
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg tg-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.insertAdjacentHTML("beforeend", tgSky());
  const land = document.createElement("div");
  land.className = "tg-land";
  land.innerHTML = tgLand();
  bg.appendChild(land);
  const flock = document.createElement("div");
  flock.className = "tg-flockbox";
  bg.appendChild(flock);
  const cv = document.createElement("canvas");
  cv.className = "tg-leaves";
  bg.appendChild(cv);
  document.body.prepend(bg);
  const ctx = cv.getContext("2d");
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);

  // One leaf: a rounded blade with a stem, drawn at the origin and rotated.
  const leaf = (x, y, r, sz, col, flat) => {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(r);
    ctx.scale(1, flat);
    ctx.fillStyle = col;
    ctx.beginPath();
    ctx.moveTo(0, -sz);
    ctx.bezierCurveTo(sz * .9, -sz * .5, sz * .75, sz * .6, 0, sz);
    ctx.bezierCurveTo(-sz * .75, sz * .6, -sz * .9, -sz * .5, 0, -sz);
    ctx.fill();
    ctx.strokeStyle = "rgba(60, 30, 10, .45)";
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, -sz); ctx.lineTo(0, sz * 1.15); ctx.stroke();
    ctx.restore();
  };
  // Where the leaves come from: the crowns of the two big maples at the sides, and the wind carries them across.
  const spots = [[W * .075, H * .56], [W * .93, H * .6]];
  const make = (top) => {
    const sp = spots[Math.random() < .55 ? 0 : 1];
    const fromTree = top && Math.random() < .55;
    return {
      x: fromTree ? sp[0] + rnd(-W * .07, W * .07) : rnd(0, W), y: fromTree ? sp[1] + rnd(-H * .12, H * .04) : top ? rnd(-H * .3, -10) : rnd(0, H),
      sz: rnd(6, 13), r: rnd(0, 6.3), spin: rnd(-1.6, 1.6),
      vy: rnd(24, 62), sway: rnd(10, 34), phase: rnd(0, 6.3),
      col: TG_LEAF[Math.floor(Math.random() * TG_LEAF.length)], gx: 0,
    };
  };
  const leaves = Array.from({ length: Math.min(80, Math.round(W / 20)) }, () => make(false));

  // The harvest along the bottom: pumpkins and gourds, drawn once and kept, so they sit there like they were put out on the step.
  const patch = [];
  for (let x = rnd(30, 90); x < W - 40; x += rnd(150, 320)) {
    const r = rnd(16, 30);
    patch.push({ x, r, kind: Math.random() < .68 ? "pumpkin" : "gourd", col: Math.random() < .5 ? "#e8873d" : "#d1652f", tilt: rnd(-.2, .2) });
  }
  const gourd = (g) => {
    ctx.save();
    ctx.translate(g.x, H - 6 - g.r * .7);
    ctx.rotate(g.tilt);
    if (g.kind === "pumpkin") {
      ctx.fillStyle = g.col;
      for (const [dx, rx] of [[-g.r * .5, g.r * .6], [g.r * .5, g.r * .6], [0, g.r]]) {
        ctx.beginPath(); ctx.ellipse(dx, 0, rx, g.r * .82, 0, 0, Math.PI * 2); ctx.fill();
      }
      ctx.strokeStyle = "rgba(120, 55, 15, .35)";
      ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.ellipse(0, 0, g.r * .45, g.r * .8, 0, 0, Math.PI * 2); ctx.stroke();
    } else {
      ctx.fillStyle = "#c7a23f";
      ctx.beginPath(); ctx.ellipse(0, g.r * .2, g.r * .55, g.r * .62, 0, 0, Math.PI * 2); ctx.fill();
      ctx.beginPath(); ctx.ellipse(0, -g.r * .5, g.r * .3, g.r * .4, 0, 0, Math.PI * 2); ctx.fill();
    }
    ctx.strokeStyle = "#5a3d1b";
    ctx.lineWidth = 4;
    ctx.lineCap = "round";
    ctx.beginPath(); ctx.moveTo(0, -g.r * .78); ctx.lineTo(g.r * .15, -g.r * 1.1); ctx.stroke();
    ctx.restore();
  };

  // The wind: a slow push one way and the other, and gusts on top that die away.
  let gustV = 0;
  const windAt = t => 22 * Math.sin(t / 3800) + 14 * Math.sin(t / 1350 + 1) + gustV;
  const draw = (t) => {
    ctx.clearRect(0, 0, W, H);
    for (const g of patch) gourd(g);
    for (const l of leaves) leaf(l.x + Math.sin(t / 1000 + l.phase) * l.sway, l.y, l.r, l.sz, l.col, Math.cos(l.r * .7) * .6 + .4);
  };

  // ---- in front: the garland along the top, and the corners ----
  const top = document.createElementNS(NS, "svg");
  top.setAttribute("width", W); top.setAttribute("height", 96);
  top.style.left = "0"; top.style.top = "0";
  top.innerHTML = tgGarland(W);
  root.appendChild(top);
  const corner = (html, side, w) => {
    const c = document.createElement("div");
    c.className = "tg-corner";
    c.style[side] = "10px";
    c.innerHTML = html;
    root.appendChild(c);
  };
  corner(TG_CORNUCOPIA, "left");
  corner(TG_PUMPKINS, "right");

  // ---- geese: a V across the sky now and then ----
  const geese = () => {
    if (document.hidden || flock.childElementCount > 1) return;
    const el = document.createElement("div");
    el.className = "tg-geese" + (Math.random() < .5 ? " rtl" : "");
    el.style.top = rnd(8, 30).toFixed(0) + "vh";
    el.style.animationDuration = rnd(46, 74).toFixed(0) + "s";
    el.innerHTML = tgGeese(5 + Math.floor(Math.random() * 4) * 2);
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    flock.appendChild(el);
  };

  // ---- intruders: raccoons at the pie ----
  let turkey = null;                    // the stroll, once it's ready
  const watchIn = intruderWatch();
  const coons = new Map();              // id -> { h, el, tag, alarm, gone }
  const spacer = document.createElement("div");
  spacer.setAttribute("aria-hidden", "true");
  document.body.appendChild(spacer);
  festiveStops.push(() => spacer.remove());
  const slotX = k => Math.round(W < 700 ? W * .58 : W * .66 - k * 260);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...coons.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeCoon = h => {
    const el = document.createElement("div");
    el.className = "tg-thief";
    el.innerHTML = `${TG_PIE}<div class="tg-coon">${TG_RACCOON}</div>`;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const c = { h, el, tag, alarm: 0, gone: false };
    coons.set(h.id, c);
    return c;
  };
  const settle = () => {
    const list = order();
    spacer.style.height = list.some(c => !c.gone) ? "92px" : "0";
    list.forEach((c, k) => {
      c.el.hidden = k > (W < 700 ? 0 : 1);
      c.el.style.left = slotX(k) + "px";
    });
  };
  // Leaves whipped up across the screen, and the wind with them.
  const gust = () => {
    gustV = 120;
    for (const l of leaves.slice(0, 22)) { l.x = rnd(-40, W * .2); l.y = rnd(0, H * .7); l.spin = rnd(2, 4.5); l.vy = rnd(50, 90); }
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!coons.has(h.id)) makeCoon(h);
    for (const h of held) { const c = coons.get(h.id); if (c && !c.gone) { c.h = h; if (!c.alarm || Date.now() - c.alarm > 9000) intruderTagEl(h, "held", c.tag); } }
    for (const h of cleared) {
      const c = coons.get(h.id); if (!c || c.gone) continue;
      c.gone = true;
      const done = () => { c.el.remove(); coons.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: it scurries off, the leaves blow through, and a turkey
      // comes by to see who's new.
      intruderTagEl(c.h, "cleared", c.tag);
      festiveTimers.push(setTimeout(() => c.el.classList.add("off"), 1800));
      festiveTimers.push(setTimeout(() => c.el.classList.add("gone"), 2800));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) { gust(); turkey?.(); } }, 4000));
    }
    settle();
  }
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const c = coons.get(h.id) || makeCoon(h);
    if (calm || document.hidden) { settle(); return; }
    c.alarm = Date.now();
    intruderTagEl(h, "alarm", c.tag);
    settle();
    // It scurries in; the edges glow red, the leaves whip up, and the gobbling starts.
    c.el.classList.remove("in"); void c.el.offsetWidth; c.el.classList.add("in");
    gust();
    const glow = document.createElement("div");
    glow.className = "tg-alarm";
    root.appendChild(glow);
    festiveTimers.push(setTimeout(() => glow.remove(), 6000));
    const g = document.createElement("div");
    g.className = "tg-gobble";
    g.style.top = ((document.querySelector("header")?.getBoundingClientRect().bottom || 60) + 16) + "px";
    g.innerHTML = `GOBBLE GOBBLE GOBBLE!<small>${esc(nameOrIp(h))} is at the pie</small>`;
    root.appendChild(g);
    festiveTimers.push(setTimeout(() => g.remove(), 4600));
    const bird = document.querySelector(".tg-bird");
    if (bird) { bird.classList.add("proud"); festiveTimers.push(setTimeout(() => bird.classList.remove("proud"), 3000)); }
    bg.classList.add("startled");
    festiveTimers.push(setTimeout(() => bg.classList.remove("startled"), 3200));
    festiveTimers.push(setTimeout(() => { if (!c.gone) intruderTagEl(c.h, "held", c.tag); }, 9000));
  };
  festiveHooks.rendered = syncIntruders;
  syncIntruders();

  if (calm) {
    // Reduced motion: fallen leaves, scattered along the bottom.
    for (const l of leaves) { l.y = H - rnd(4, 46); l.sway = 0; }
    draw(0);
    return;
  }

  let raf = 0, last = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    const dt = Math.min(.05, (now - last) / 1000);
    last = now;
    gustV *= Math.pow(.35, dt);
    const wind = windAt(now);
    for (const l of leaves) {
      l.y += l.vy * dt;
      l.x += wind * dt * (.6 + l.sz / 20);
      l.r += (l.spin + wind * .01) * dt;
      if (l.y > H + 20 || l.x > W + 60 || l.x < -80) Object.assign(l, make(true));
    }
    draw(now);
  };
  const run = () => { if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); } };
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };
  const vis = () => document.hidden ? stop() : run();
  document.addEventListener("visibilitychange", vis);
  run();
  festiveStops.push(() => { stop(); document.removeEventListener("visibilitychange", vis); });
  // A turkey comes through every so often, tail up, pecking as he goes.
  turkey = () => {
    if (document.hidden || document.querySelector(".tg-bird")) return;
    const el = document.createElement("div");
    el.className = "tg-bird" + (Math.random() < .5 ? " rtl" : "");
    el.style.animationDuration = rnd(34, 58).toFixed(0) + "s";
    el.innerHTML = TG_TURKEY;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    root.appendChild(el);
  };
  const again = () => festiveTimers.push(setTimeout(() => { turkey(); again(); }, rnd(45e3, 95e3)));
  festiveTimers.push(setTimeout(() => { turkey(); again(); }, rnd(8e3, 20e3)));
  const flocks = () => festiveTimers.push(setTimeout(() => { geese(); flocks(); }, rnd(55e3, 110e3)));
  festiveTimers.push(setTimeout(() => { geese(); flocks(); }, 4e3));

  // A finished scan blows a handful across the screen, startles the crows, and puffs him up.
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    gust();
    bg.classList.add("startled");
    festiveTimers.push(setTimeout(() => bg.classList.remove("startled"), 2600));
    const t = document.querySelector(".tg-bird");
    if (t) { t.classList.add("proud"); festiveTimers.push(setTimeout(() => t.classList.remove("proud"), 1600)); }
  };
}

BAMF.registerTheme("thanksgiving", ctx => buildThanksgiving(ctx.root, ctx.switched));
})();
