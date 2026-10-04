// Christmas: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
//
// Behind the page: a winter night with stars, a moon, northern lights, mountains and pines, snow falling in
// three depths, and a house strung with lights that has smoke from its chimney and now and then Santa in it.
// In front: strings of bulbs, a snowman, presents, Santa's sleigh, a gingerbread man and a shooting star. A new
// device nobody has marked known is a burglar on the naughty list.
(() => {
// Classic multicolour strings repeat their colours in a fixed order.
const BULB_COLORS = ["#ff2e2e", "#ffae1a", "#2fd65a", "#2f8bff", "#e04cff"];

let bulbIndex = 0;

function bulbSvg(x, y, angle) {
  const c = BULB_COLORS[bulbIndex++ % BULB_COLORS.length];
  return `<g class="xl" style="--c:${c}" transform="translate(${x.toFixed(1)},${y.toFixed(1)}) rotate(${angle})">
    <rect class="xl-cap" x="-2.6" y="-1" width="5.2" height="4" rx="1"/>
    <ellipse class="glass" cx="0" cy="8.5" rx="3.7" ry="5.8"/></g>`;
}

// ---- the night behind the house: sky, stars, moon, aurora, mountains, pines and falling snow ----

// The same night every time: the scene is drawn from a fixed seed, so it doesn't reshuffle on a reload.
function seeded(seed) {
  let a = seed >>> 0;
  return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}

// A fir: three tiers, each a triangle with a cap of snow on its top third.
function xsPine(x, y, h, col, snow) {
  const w = h * .46;
  const tier = (ax, ay, hw, by) => {
    const f = .42, sx = hw * f, sy = ay + (by - ay) * f;
    return `<path d="M${ax} ${ay}L${ax + hw} ${by}H${ax - hw}Z" fill="${col}"/>` +
      `<path d="M${ax} ${ay}L${(ax + sx).toFixed(1)} ${sy.toFixed(1)}L${(ax + sx * .35).toFixed(1)} ${(sy - 2).toFixed(1)}L${ax} ${(sy + 1).toFixed(1)}L${(ax - sx * .4).toFixed(1)} ${(sy - 3).toFixed(1)}L${(ax - sx).toFixed(1)} ${sy.toFixed(1)}Z" fill="${snow}"/>`;
  };
  return `<rect x="${x - 2}" y="${y - 6}" width="4" height="10" fill="#1b130c"/>` +
    tier(x, y - h * .52, w * .5, y - 4) + tier(x, y - h * .8, w * .42, y - h * .3) + tier(x, y - h, w * .3, y - h * .6);
}

// Mountains with snow on their peaks, and two rows of pines in front, on a snowy floor. Wider than any
// screen needs; it keeps its shape and sits on the bottom edge.
function xsLand() {
  const r = seeded(11), VW = 1600, VH = 700, GROUND = 640;
  const peaks = [];
  for (let x = -60; x < VW + 200; x += 150 + r() * 130) peaks.push([x, 150 + r() * 150]);
  const valley = (a, b) => [(a[0] + b[0]) / 2 + (r() - .5) * 40, GROUND - 210 - r() * 70];
  let ridge = `M-80 ${GROUND}`, caps = "";
  peaks.forEach((p, i) => {
    const left = i ? valley(peaks[i - 1], p) : [p[0] - 150, GROUND - 200], right = i < peaks.length - 1 ? valley(p, peaks[i + 1]) : [p[0] + 150, GROUND - 200];
    ridge += `L${left[0].toFixed(0)} ${left[1].toFixed(0)}L${p[0].toFixed(0)} ${p[1].toFixed(0)}`;
    const at = (v, t) => [p[0] + (v[0] - p[0]) * t, p[1] + (v[1] - p[1]) * t];
    const a = at(left, .3), b = at(right, .3), m = (p[0] + (r() - .5) * 10).toFixed(1);
    caps += `<path d="M${p[0].toFixed(0)} ${p[1].toFixed(0)}L${a[0].toFixed(0)} ${a[1].toFixed(0)}l${(7 + r() * 5).toFixed(0)} -5 9 12 8 -10 ${(8 + r() * 4).toFixed(0)} 7 ${(b[0] - a[0] - 44).toFixed(0)} ${(b[1] - a[1] + 8).toFixed(0)}L${b[0].toFixed(0)} ${b[1].toFixed(0)}Z" fill="#dbe8f5" opacity=".88"/>`;
  });
  ridge += `L${VW + 200} ${GROUND}Z`;
  let far = "", near = "";
  for (let x = -20; x < VW + 40; x += 30 + r() * 26) far += xsPine(x, GROUND - 4 - r() * 10, 62 + r() * 50, "#0a2417", "#9db7cc");
  for (let x = -40; x < VW + 40; x += 46 + r() * 42) near += xsPine(x, GROUND + 14 + r() * 14, 110 + r() * 80, "#071d12", "#d3e3f2");
  return `<svg viewBox="0 0 ${VW} ${VH}" preserveAspectRatio="xMidYMax slice" aria-hidden="true">
    <defs>
      <linearGradient id="xsMtn" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2a4a6e"/><stop offset="1" stop-color="#112a43"/></linearGradient>
      <linearGradient id="xsFloor" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#cfe0ef"/><stop offset="1" stop-color="#eef5fb"/></linearGradient>
    </defs>
    <path d="${ridge}" fill="url(#xsMtn)"/>${caps}
    <rect x="-60" y="${GROUND - 70}" width="${VW + 120}" height="70" fill="#0c2a44" opacity=".55"/>
    ${far}
    <rect x="-60" y="${GROUND}" width="${VW + 120}" height="${VH - GROUND}" fill="url(#xsFloor)"/>
    ${near}
  </svg>`;
}

// Stars, a moon and its halo, the northern lights and a couple of clouds.
function xsSky(W, H) {
  const r = seeded(5);
  let stars = "";
  for (let i = 0; i < Math.round(Math.min(150, W / 9)); i++) {
    const s = r() < .12 ? 2.6 : r() < .5 ? 1.7 : 1.1, tw = r() < .35;
    stars += `<i class="xs-star${tw ? " tw" : ""}" style="left:${(r() * 100).toFixed(1)}%;top:${(r() * 62).toFixed(1)}%;width:${s}px;height:${s}px;--d:${(1.6 + r() * 3).toFixed(1)}s;--o:-${(r() * 5).toFixed(1)}s"></i>`;
  }
  const ribbon = (y, amp, c1, c2, o, delay, speed) => `<path class="xs-rib" d="M-100 ${y}${Array.from({ length: 9 }, (_, k) => ` Q${(k * 240 + 120).toFixed(0)} ${(y + (k % 2 ? -amp : amp)).toFixed(0)} ${((k + 1) * 240 - 100).toFixed(0)} ${y}`).join("")}"
    stroke="url(#${c1})" stroke-width="${(o * 110).toFixed(0)}" fill="none" opacity="${o}" style="animation-duration:${speed}s;animation-delay:-${delay}s"/>`;
  const aurora = `<svg class="xs-aurora" viewBox="0 0 1920 420" preserveAspectRatio="none" aria-hidden="true">
    <defs>
      <linearGradient id="xsA1" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#3dffa6" stop-opacity="0"/><stop offset=".3" stop-color="#3dffa6"/><stop offset=".62" stop-color="#34d6ff"/><stop offset="1" stop-color="#9a6bff" stop-opacity="0"/></linearGradient>
      <linearGradient id="xsA2" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#9a6bff" stop-opacity="0"/><stop offset=".4" stop-color="#c06bff"/><stop offset=".75" stop-color="#3dffa6"/><stop offset="1" stop-color="#3dffa6" stop-opacity="0"/></linearGradient>
    </defs>
    ${ribbon(150, 55, "xsA1", "", .55, 3, 26)}${ribbon(210, 40, "xsA2", "", .4, 11, 34)}${ribbon(110, 35, "xsA1", "", .32, 19, 41)}
  </svg>`;
  const moon = `<div class="xs-moon"><svg viewBox="0 0 120 120" aria-hidden="true">
    <defs><radialGradient id="xsMoon" cx="38%" cy="36%" r="70%"><stop offset="0" stop-color="#fffdf0"/><stop offset="1" stop-color="#e3dcc0"/></radialGradient></defs>
    <circle cx="60" cy="60" r="44" fill="url(#xsMoon)"/>
    <circle cx="44" cy="48" r="9" fill="#cfc7a6" opacity=".35"/><circle cx="72" cy="40" r="6" fill="#cfc7a6" opacity=".3"/><circle cx="68" cy="72" r="11" fill="#cfc7a6" opacity=".3"/>
    <circle cx="46" cy="76" r="5" fill="#cfc7a6" opacity=".3"/><circle cx="80" cy="60" r="3.5" fill="#cfc7a6" opacity=".3"/></svg></div>`;
  const clouds = [0, 1].map(k => `<div class="xs-cloud" style="--w:${(380 + k * 160)}px;top:${(9 + k * 13)}vh;--d:${(160 + k * 70)}s;--o:-${(k * 90 + 30)}s"></div>`).join("");
  return `<div class="xs-sky"></div><div class="xs-stars">${stars}</div>${aurora}${moon}${clouds}`;
}

// Snow falling behind the page in three depths: small and slow far away, big and quick up close, all
// leaning together on a wind that comes and goes.
function xsSnowfall(canvas, calm) {
  const ctx2 = canvas.getContext("2d");
  let W = 0, H = 0, flakes = [], raf = 0, t0 = performance.now();
  const size = () => {
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    W = canvas.clientWidth; H = canvas.clientHeight;
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    ctx2.setTransform(dpr, 0, 0, dpr, 0, 0);
    const n = Math.round(Math.min(170, Math.max(50, W * H / 9000)));
    flakes = Array.from({ length: n }, () => { const z = Math.random(); return { x: Math.random() * W, y: Math.random() * H, z, r: .8 + z * 2.6, v: 14 + z * 46, ph: Math.random() * 6.28 }; });
  };
  const draw = (dt, now) => {
    const wind = Math.sin(now / 9000) * 18 + Math.sin(now / 3100) * 8;
    ctx2.clearRect(0, 0, W, H);
    for (const f of flakes) {
      f.y += f.v * dt; f.x += (wind * (.4 + f.z) + Math.sin(now / 1300 + f.ph) * 9) * dt;
      if (f.y > H + 6) { f.y = -6; f.x = Math.random() * W; }
      if (f.x > W + 8) f.x = -8; else if (f.x < -8) f.x = W + 8;
      ctx2.globalAlpha = .35 + f.z * .5;
      ctx2.fillStyle = "#fff";
      ctx2.beginPath(); ctx2.arc(f.x, f.y, f.r, 0, 6.2832); ctx2.fill();
    }
  };
  size();
  window.addEventListener("resize", size);
  if (calm) { draw(0, 0); }
  else {
    const frame = now => { draw(Math.min(.1, (now - t0) / 1000), now); t0 = now; raf = requestAnimationFrame(frame); };
    raf = requestAnimationFrame(frame);
  }
  return () => { cancelAnimationFrame(raf); window.removeEventListener("resize", size); };
}

const SLEIGH = `<svg viewBox="0 0 190 40" width="190">
  <path d="M60 22H128" stroke="#e8c15a" stroke-width=".8"/>
  ${[0, 1, 2].map(i => { const x = 70 + i * 38; return `<g transform="translate(${x},0)">
    <path d="M6 12l-3-7M6 12l2-8M16 10l-2-7M16 10l3-6" stroke="#d9b48f" stroke-width="1.2" stroke-linecap="round"/>
    <ellipse cx="10" cy="21" rx="10" ry="5" fill="#8b5a2b"/><path d="M17 19l4-6" stroke="#8b5a2b" stroke-width="3" stroke-linecap="round"/>
    <circle cx="22" cy="12" r="3.6" fill="#8b5a2b"/>
    <path d="M3 24l-5 6M7 25l-2 7M14 25l3 6M18 23l6 5" stroke="#6b4420" stroke-width="1.6" stroke-linecap="round"/>
    ${i === 2 ? `<circle class="rudolph" cx="25.6" cy="12.5" r="1.9"/>` : `<circle cx="25.4" cy="12.5" r="1.2" fill="#3b2410"/>`}</g>`; }).join("")}
  <circle cx="30" cy="18" r="7" fill="#7a5230"/>
  <path d="M8 22h46c4 0 6 3 5 7l-2 4H18C11 33 7 28 8 22z" fill="#c8102e"/>
  <path d="M6 35c10 3 40 3 52 -1" stroke="#e8c15a" stroke-width="2" fill="none" stroke-linecap="round"/>
  <circle cx="44" cy="17" r="5.5" fill="#d42426"/><circle cx="44" cy="10" r="3.2" fill="#f5d0b0"/>
  <ellipse cx="44" cy="13" rx="3" ry="2.2" fill="#fff"/><path d="M40.5 8.5L47 8.5L45 3Z" fill="#d42426"/><circle cx="45" cy="3" r="1.3" fill="#fff"/></svg>`;

const GIFT_COLORS = ["#d42426", "#2fa84f", "#2f6fd4", "#c9a227"];

// A new device nobody has marked known is an intruder: a burglar with a sack.
// Every bulb on the house turns red and flashes, and the naughty list unrolls
// with its name on it; it tiptoes in and stands in the snow along the bottom,
// swag over its shoulder, tagged, until the device is marked known. Then it's
// crossed off, it drops the sack and tiptoes away, and Santa comes over with
// a present for the device.
const BURGLAR = `<svg viewBox="0 0 70 92" width="70" height="92" aria-hidden="true">
  <g class="xb-sack"><path d="M40 24c10-7 26 1 26 19 0 14-8 22-18 22s-16-8-14-20c1-9 2-15 6-21z" fill="#a8743f" stroke="#5a3a1a" stroke-width="1.3"/>
    <path d="M38 26q4 4 10 1" fill="none" stroke="#5a3a1a" stroke-width="1.5"/>
    <text x="51" y="50" text-anchor="middle" font-family="Space Grotesk, sans-serif" font-weight="800" font-size="8" fill="#5a3a1a">SWAG</text></g>
  <path d="M18 60l-4 28h7l4-19 4 19h7l-4-28z" fill="#1a1a22"/>
  <ellipse cx="16" cy="89" rx="5.5" ry="2.5" fill="#1a1a22"/><ellipse cx="34" cy="89" rx="5.5" ry="2.5" fill="#1a1a22"/>
  <rect x="12" y="32" width="26" height="30" rx="5" fill="#f4f4f4" stroke="#1a1a22" stroke-width="1.3"/>
  <path d="M12 39h26M12 46h26M12 53h26" stroke="#1a1a22" stroke-width="3.5"/>
  <path d="M35 37l7-9" stroke="#1a1a22" stroke-width="5" stroke-linecap="round"/><path d="M15 37l-5 12" stroke="#1a1a22" stroke-width="5" stroke-linecap="round"/>
  <circle cx="25" cy="21" r="10" fill="#f0c9a0" stroke="#1a1a22" stroke-width="1.2"/>
  <rect x="14" y="17" width="22" height="6" rx="3" fill="#1a1a22"/>
  <circle cx="21" cy="20" r="1.6" fill="#fff"/><circle cx="29" cy="20" r="1.6" fill="#fff"/>
  <path d="M14.6 15q10.4-13 20.8 0z" fill="#1a1a22"/><circle cx="25" cy="4" r="2.6" fill="#1a1a22"/>
  <path d="M22 27q3 1.5 6 0" fill="none" stroke="#7a4a2a" stroke-width="1.2" stroke-linecap="round"/></svg>`;

const giftSvg = c => `<svg viewBox="0 0 18 18" width="18"><rect x="2" y="7" width="14" height="10" rx="1" fill="${c}"/>
  <rect x="1" y="5" width="16" height="3.5" rx="1" fill="${c}"/><rect x="8" y="5" width="2" height="12" fill="#fff6d0"/>
  <path d="M9 5c-2-3-5-3-4-1 1 1.5 4 1 4 1z M9 5c2-3 5-3 4-1-1 1.5-4 1-4 1z" fill="#fff6d0"/></svg>`;

const GINGER = `<svg viewBox="0 0 26 32" width="26"><g fill="#b5703a"><circle cx="13" cy="7" r="6"/>
  <path d="M8 13h10l7 5-2 3-5-3v5l4 8-4 1-4-7-4 7-4-1 4-8v-5l-5 3-2-3z"/></g>
  <circle cx="11" cy="6" r=".9" fill="#fff"/><circle cx="15" cy="6" r=".9" fill="#fff"/><path d="M10.5 9q2.5 2 5 0" stroke="#fff" stroke-width=".8" fill="none"/>
  <circle cx="13" cy="16" r=".9" fill="#fff"/><circle cx="13" cy="19.5" r=".9" fill="#fff"/></svg>`;

const SNOWMAN = `<svg viewBox="0 0 48 60" width="46"><circle cx="24" cy="45" r="13" fill="#f4f8f6"/><circle cx="24" cy="27" r="9.5" fill="#f4f8f6"/>
  <circle cx="24" cy="13" r="7" fill="#f4f8f6"/><rect x="17" y="3" width="14" height="3" fill="#1d1d1d"/><rect x="19" y="-5" width="10" height="9" fill="#1d1d1d"/>
  <path d="M24 13l7 2-7 1z" fill="#ff8c1a"/><circle cx="21.5" cy="11" r="1" fill="#1d1d1d"/><circle cx="26.5" cy="11" r="1" fill="#1d1d1d"/>
  <path d="M16 21q8 5 16 0" stroke="#d42426" stroke-width="3" fill="none"/>
  <circle cx="24" cy="26" r="1" fill="#1d1d1d"/><circle cx="24" cy="30" r="1" fill="#1d1d1d"/>
  <path d="M14 29L5 22" stroke="#6b4420" stroke-width="1.6" stroke-linecap="round"/>
  <g class="arm"><path d="M34 30L44 20M41 23l3-1" stroke="#6b4420" stroke-width="1.6" stroke-linecap="round"/></g></svg>`;

const PRESENT_STACK = `<svg viewBox="0 0 60 44" width="58">
  <rect x="2" y="22" width="26" height="20" rx="1" fill="#2fa84f"/><rect x="13" y="22" width="4" height="20" fill="#ffd23f"/>
  <rect x="30" y="18" width="26" height="24" rx="1" fill="#d42426"/><rect x="41" y="18" width="4" height="24" fill="#fff6d0"/>
  <rect x="12" y="6" width="22" height="16" rx="1" fill="#2f6fd4"/><rect x="21" y="6" width="4" height="16" fill="#fff6d0"/>
  <path d="M23 6c-3-5-8-5-7-1 1 2 7 1 7 1z M23 6c3-5 8-5 7-1-1 2-7 1-7 1z" fill="#fff6d0"/></svg>`;

// A suburban house done up for Christmas: every eave, gable, window and tree
// strung with bulbs, the way the neighbour who takes it too far does it. Drawn
// here rather than loaded, so it costs nothing to ship and scales to any
// screen. Lights are laid along a line of points, a bulb every so often.
const XH_COLS = ["#ff4d4d", "#ffd23f", "#3ddc6f", "#4fa3ff", "#ff8ad6", "#fff1c9"];

let xhBulb = 0;

function xhLights(pts, gap = 26, r = 4.4) {
  let out = "";
  for (let i = 0; i < pts.length - 1; i++) {
    const [x1, y1] = pts[i], [x2, y2] = pts[i + 1];
    const d = Math.hypot(x2 - x1, y2 - y1), n = Math.max(1, Math.round(d / gap));
    for (let k = 0; k <= n; k++) {
      if (i > 0 && k === 0) continue; // no doubling up on corners
      const t = k / n, x = x1 + (x2 - x1) * t, y = y1 + (y2 - y1) * t;
      const c = XH_COLS[xhBulb % XH_COLS.length];
      out += `<circle class="xh-bulb" cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${r}" fill="${c}" color="${c}" style="animation-delay:-${(xhBulb * .17).toFixed(2)}s;animation-duration:${(2 + (xhBulb % 5) * .35).toFixed(2)}s"/>`;
      xhBulb++;
    }
  }
  return out;
}

// Icicle strands off an eave: the short hanging kind, uneven lengths.
function xhIcicles(x1, x2, y, gap = 44) {
  let out = "";
  for (let x = x1, i = 0; x <= x2; x += gap, i++) {
    const len = 14 + (i % 4) * 11;
    out += `<line x1="${x}" y1="${y}" x2="${x}" y2="${y + len}" stroke="#bfe3ff" stroke-width="1.4" opacity=".35"/>`;
    for (let k = 1; k <= Math.max(2, Math.round(len / 13)); k++) {
      const c = k % 2 ? "#fff1c9" : "#bfe3ff";
      out += `<circle class="xh-bulb" cx="${x}" cy="${(y + len * k / Math.max(2, Math.round(len / 13))).toFixed(1)}" r="2.6" fill="${c}" color="${c}" style="animation-delay:-${(i * .31 + k * .12).toFixed(2)}s;animation-duration:${(2.4 + (i % 3) * .4).toFixed(2)}s"/>`;
    }
  }
  return out;
}

function xhTree(x, y, h, i) {
  const w = h * .62;
  let t = `<path d="M${x} ${y - h}L${x + w / 2} ${y}H${x - w / 2}Z" fill="#12351f"/><rect x="${x - 5}" y="${y}" width="10" height="16" fill="#3b2a1b"/>`;
  // Lights wound round it, a few turns from top to bottom.
  const pts = [];
  for (let k = 0; k <= 26; k++) {
    const t2 = k / 26, yy = y - h + h * t2, rad = (w / 2) * t2;
    pts.push([x + Math.sin(t2 * 11 + i) * rad, yy]);
  }
  return t + xhLights(pts, 20, 3.6);
}

function xmasHouse() {
  xhBulb = 0;
  const win = (x, y, w, h) => `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="3" fill="#ffd98a" class="xh-win"/>` +
    `<path d="M${x + w / 2} ${y}V${y + h}M${x} ${y + h / 2}H${x + w}" stroke="#2a2118" stroke-width="3"/>` +
    xhLights([[x - 4, y - 4], [x + w + 4, y - 4], [x + w + 4, y + h + 4], [x - 4, y + h + 4], [x - 4, y - 4]], 22, 3.4);
  return `<svg viewBox="0 0 1500 760" preserveAspectRatio="xMidYMax meet" aria-hidden="true">
    <defs>
      <radialGradient id="xhHalo" cx="50%" cy="46%" r="52%">
        <stop offset="0" stop-color="#ffe6a8" stop-opacity=".85"/><stop offset="1" stop-color="#ffe6a8" stop-opacity="0"/>
      </radialGradient>
    </defs>
    <ellipse class="xh-glow" cx="750" cy="430" rx="640" ry="330" fill="url(#xhHalo)"/>
    <!-- snow on the ground, and the drive up to the garage -->
    <path d="M0 700q210-34 420-22t300 6 360-16 420 22v70H0z" fill="#e9f2fb" opacity=".92"/>
    <path d="M980 760l70-118h190l40 118z" fill="#d8e4f0" opacity=".85"/>
    ${xhTree(120, 700, 250, 1)}
    ${xhTree(1380, 706, 210, 3)}
    <!-- garage wing -->
    <path d="M900 420l190-92 190 92z" fill="#4a2f22"/>
    <rect x="920" y="420" width="340" height="230" fill="#6b4632"/>
    <rect x="960" y="470" width="260" height="180" rx="4" fill="#8a6a52"/>
    <path d="M960 520h260M960 570h260M960 620h260" stroke="#6f5340" stroke-width="4"/>
    <!-- the house itself -->
    <path d="M170 400L560 150l390 250z" fill="#4a2f22"/>
    <rect x="210" y="400" width="700" height="250" fill="#6b4632"/>
    <g class="xh-santa" aria-hidden="true">
      <g class="xh-wave"><path d="M640 176l17-14" stroke="#c9232b" stroke-width="7" stroke-linecap="round"/><circle cx="658" cy="161" r="4.6" fill="#fff"/></g>
      <rect x="610" y="182" width="26" height="34" rx="5" fill="#d42426"/>
      <circle cx="623" cy="176" r="12" fill="#f5cfae"/>
      <path d="M611 178c1 12 6 18 12 18s11-6 12-18c-5 3-19 3-24 0z" fill="#fff"/>
      <circle cx="619" cy="174" r="1.5" fill="#2a1a10"/><circle cx="628" cy="174" r="1.5" fill="#2a1a10"/><circle cx="623.5" cy="179" r="2" fill="#e49a8a"/>
      <path d="M610.5 170c0-12 7-24 20-24-3 6-3 12 0 18z" fill="#d42426"/>
      <rect x="609" y="167" width="26" height="6.5" rx="3.2" fill="#fff"/><circle cx="630" cy="144" r="4.4" fill="#fff"/>
    </g>
    <rect x="596" y="196" width="54" height="120" fill="#5b3b2a"/>
    <rect x="590" y="186" width="66" height="18" fill="#6e4a35"/>
    ${[0, 1, 2, 3, 4].map(i => `<circle class="xh-smoke" cx="623" cy="180" r="9" style="animation-delay:-${(i * 1.5).toFixed(1)}s"/>`).join("")}
    <!-- porch -->
    <path d="M470 520h190l-16-40H486z" fill="#4a2f22"/>
    <rect x="516" y="520" width="90" height="130" rx="3" fill="#7d4a2e"/>
    <circle cx="561" cy="556" r="20" fill="none" stroke="#2f7a44" stroke-width="9"/>
    <circle cx="561" cy="556" r="20" fill="none" stroke="#c62f2f" stroke-width="3" stroke-dasharray="4 9"/>
    <!-- attic windows, inside the gable and clear of the chimney -->
    ${win(420, 286, 84, 64)}
    ${win(660, 286, 84, 64)}
    <!-- and the ground floor, on the wall either side of the porch -->
    ${win(270, 470, 96, 92)}
    ${win(690, 470, 96, 92)}
    ${win(390, 486, 70, 76)}
    ${win(806, 486, 70, 76)}
    <!-- and now the lights: roof, eaves, corners, chimney, garage, porch -->
    ${xhLights([[170, 400], [560, 150], [950, 400]], 27, 5)}
    ${xhLights([[210, 404], [910, 404]], 27, 4.6)}
    ${xhLights([[214, 404], [214, 650]], 28, 4.2)}
    ${xhLights([[906, 404], [906, 650]], 28, 4.2)}
    ${xhLights([[210, 646], [910, 646]], 30, 4.2)}
    ${xhLights([[596, 316], [596, 196], [650, 196], [650, 316]], 24, 4)}
    ${xhLights([[900, 420], [1090, 328], [1280, 420]], 26, 4.6)}
    ${xhLights([[924, 424], [1256, 424]], 26, 4.2)}
    ${xhLights([[956, 466], [1224, 466], [1224, 650], [956, 650], [956, 466]], 28, 3.8)}
    ${xhLights([[470, 520], [486, 480], [644, 480], [660, 520]], 22, 4)}
    ${xhLights([[512, 520], [512, 652], [610, 652], [610, 520]], 24, 3.8)}
    ${xhIcicles(232, 892, 410)}
    ${xhIcicles(940, 1244, 430)}
    ${xhIcicles(486, 648, 484, 38)}
  </svg>`;
}

// The Map for Christmas: a Santa hat on everything that's up, a present on the unknown.
function christmasMapDeco(g, n) {
  if (n.type !== "dev") return;
  const over = el => g.insertBefore(el, g.querySelector(".t-name"));
  const hat = svgEl("g", { class: "t-hat", transform: "translate(-8,-17) rotate(-18)" });
  hat.appendChild(svgEl("path", { d: "M-8 3L8 3L2 -12Z", fill: "#d42426" }));
  hat.appendChild(svgEl("rect", { x: -9, y: 1, width: 18, height: 4, rx: 2, fill: "#fff" }));
  hat.appendChild(svgEl("circle", { cx: 2, cy: -12, r: 2.3, fill: "#fff" }));
  over(hat);
  const gift = svgEl("g", { class: "t-gift", transform: "translate(-12,-12)" });
  gift.appendChild(svgEl("rect", { x: 4, y: 10, width: 16, height: 10, rx: 1, fill: "#d42426" }));
  gift.appendChild(svgEl("rect", { x: 3, y: 7, width: 18, height: 4, rx: 1, fill: "#b81d24" }));
  gift.appendChild(svgEl("rect", { x: 11, y: 7, width: 2, height: 13, fill: "#ffd23f" }));
  gift.appendChild(svgEl("path", { d: "M12 7c-2-4-6-4-5-1 1 2 5 1 5 1z M12 7c2-4 6-4 5-1-1 2-5 1-5 1z", fill: "#ffd23f" }));
  over(gift);
}

function buildChristmas(root) {
  festiveHooks.decorateNode = christmasMapDeco;
  const W = window.innerWidth, H = window.innerHeight, NS = "http://www.w3.org/2000/svg";
  bulbIndex = 0;
  // Northern lights, very faint, behind the top string.
  // The house, behind the page; the lights and snow above it are unchanged.
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg xh-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.insertAdjacentHTML("beforeend", xsSky(W, H));
  const land = document.createElement("div");
  land.className = "xs-land";
  land.innerHTML = xsLand();
  bg.appendChild(land);
  const house = document.createElement("div");
  house.className = "xh-house";
  house.innerHTML = xmasHouse();
  bg.appendChild(house);
  const snowCanvas = document.createElement("canvas");
  snowCanvas.className = "xs-snow";
  bg.appendChild(snowCanvas);
  const siren = document.createElement("div");
  siren.className = "xs-alarm";
  bg.appendChild(siren);
  document.body.prepend(bg);
  festiveStops.push(xsSnowfall(snowCanvas, calmMotion()));
  // Snow that settles along the bottom, a snowman in one corner and presents in the other.
  const drift = document.createElement("div");
  drift.className = "drift";
  let bumps = "M0 16V9";
  for (let x = 0; x < W; x += 60) bumps += ` Q${x + 30} ${(rnd(0, 5)).toFixed(1)} ${x + 60} ${rnd(7, 10).toFixed(1)}`;
  drift.innerHTML = `<svg viewBox="0 0 ${W} 16" preserveAspectRatio="none"><path d="${bumps} V16Z" fill="#eef6f0" opacity=".85"/></svg>`;
  drift.style.transform = calmMotion() ? "scaleY(1)" : "scaleY(.2)";
  root.appendChild(drift);
  const snowman = document.createElement("div");
  snowman.className = "xcorner snowman";
  snowman.style.left = "12px";
  snowman.innerHTML = SNOWMAN;
  root.appendChild(snowman);
  const stack = document.createElement("div");
  stack.className = "xcorner";
  stack.style.right = "14px";
  stack.innerHTML = PRESENT_STACK;
  root.appendChild(stack);
  // The top string: scallops between hooks, bulbs along each scallop.
  const span = W < 700 ? 120 : 170, sag = 16;
  let wire = "", bulbs = "", orns = "";
  for (let x0 = 0, i = 0; x0 < W; x0 += span, i++) {
    const x1 = x0 + span, cx = (x0 + x1) / 2;
    wire += `M${x0} 2 Q${cx} ${2 * sag} ${x1} 2 `;
    const n = Math.max(3, Math.round(span / 34));
    for (let k = 0; k < n; k++) {
      const t = (k + .5) / n, u = 1 - t;
      const bx = u * u * x0 + 2 * u * t * cx + t * t * x1, by = u * u * 2 + 2 * u * t * 2 * sag + t * t * 2;
      bulbs += bulbSvg(bx, by, Math.round((t - .5) * -40));
    }
    // Ornaments hang only over the header's empty middle stretch, clear of the
    // version chips on the left and the scan status on the right.
    if (i % 2 === 1 && x1 > W * 0.36 && x1 < W * 0.62) {
      const len = rnd(16, 30), c = BULB_COLORS[(i * 7) % BULB_COLORS.length];
      orns += `<g class="orn" style="transform-origin:${x1}px 2px;--d:${rnd(2.2, 3.6).toFixed(2)}s;--o:-${rnd(0, 3).toFixed(2)}s">
        <line x1="${x1}" y1="2" x2="${x1}" y2="${(2 + len).toFixed(1)}"/>
        <rect x="${x1 - 2.5}" y="${(len).toFixed(1)}" width="5" height="4" rx="1" fill="#c8b98a"/>
        <circle cx="${x1}" cy="${(len + 12).toFixed(1)}" r="8.5" fill="${c}"/>
        <circle cx="${(x1 - 3).toFixed(1)}" cy="${(len + 9).toFixed(1)}" r="2.4" fill="#fff" opacity=".55"/></g>`;
    }
  }
  const top = document.createElementNS(NS, "svg");
  top.setAttribute("width", W); top.setAttribute("height", 90);
  top.style.left = "0"; top.style.top = "0";
  top.innerHTML = `<path class="xl-wire" d="${wire}"/>${orns}${bulbs}`;
  root.appendChild(top);
  // Down both sides, a gently wavy string with the bulbs pointing inward.
  for (const side of ["left", "right"]) {
    let d = "", b = "";
    const x = side === "left" ? 7 : 19;
    for (let y = 30, k = 0; y < H; y += 38, k++) {
      const wx = x + Math.sin(k) * 3;
      d += (k ? "L" : "M") + wx.toFixed(1) + " " + y + " ";
      b += bulbSvg(wx, y, side === "left" ? -90 : 90);
    }
    const svg = document.createElementNS(NS, "svg");
    svg.setAttribute("width", 26); svg.setAttribute("height", H);
    svg.style.top = "0"; svg.style[side] = "0";
    svg.innerHTML = `<path class="xl-wire" d="${d}"/>${b}`;
    root.appendChild(svg);
  }
  // ---- intruders: burglars in the snow ----
  const calm = calmMotion();
  const watchIn = intruderWatch();
  const burglars = new Map();           // id -> { h, el, tag, alarm, gone }
  const spacer = document.createElement("div");
  spacer.setAttribute("aria-hidden", "true");
  document.body.appendChild(spacer);
  festiveStops.push(() => { spacer.remove(); root.classList.remove("xm-red"); });
  const slotX = k => Math.round(W < 700 ? W * .58 : W * .68 - k * 250);
  // One stood down keeps its place until it has gone, so none lands on it.
  const order = () => [...burglars.values()].sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const makeBurglar = h => {
    const el = document.createElement("div");
    el.className = "xb";
    el.innerHTML = BURGLAR;
    const tag = intruderTagEl(h, "held");
    el.appendChild(tag);
    root.appendChild(el);
    const b = { h, el, tag, alarm: 0, gone: false };
    burglars.set(h.id, b);
    return b;
  };
  const settle = () => {
    const list = order();
    spacer.style.height = list.some(b => !b.gone) ? "112px" : "0";
    list.forEach((b, k) => {
      b.el.hidden = k > (W < 700 ? 0 : 1);
      if (!b.walking) b.el.style.left = slotX(k) + "px";
    });
  };
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    for (const h of added) if (!burglars.has(h.id)) makeBurglar(h);
    for (const h of held) { const b = burglars.get(h.id); if (b && !b.gone) { b.h = h; if (!b.alarm || Date.now() - b.alarm > 9000) intruderTagEl(h, "held", b.tag); } }
    for (const h of cleared) {
      const b = burglars.get(h.id); if (!b || b.gone) continue;
      b.gone = true;
      const done = () => { b.el.remove(); burglars.delete(h.id); settle(); };
      if (calm || document.hidden) { done(); continue; }
      // Stood down: off the list; it drops the sack and tiptoes away, and
      // Santa comes over with a present.
      intruderTagEl(b.h, "cleared", b.tag);
      festiveTimers.push(setTimeout(() => b.el.classList.add("drop"), 1400));
      festiveTimers.push(setTimeout(() => b.el.classList.add("away"), 2400));
      festiveTimers.push(setTimeout(() => { done(); if (!h.test) sleigh(true); }, 4200));
    }
    settle();
  }
  let redUntil = 0;
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    const b = burglars.get(h.id) || makeBurglar(h);
    if (calm || document.hidden) { settle(); return; }
    b.alarm = Date.now();
    intruderTagEl(h, "alarm", b.tag);
    settle();
    // It tiptoes in from the right.
    b.walking = true;
    b.el.style.transition = "none"; b.el.style.left = (W + 50) + "px"; b.el.classList.add("walk");
    void b.el.offsetWidth;
    b.el.style.transition = "left 3s linear";
    b.el.style.left = slotX(0) + "px";
    festiveTimers.push(setTimeout(() => { b.walking = false; b.el.style.transition = ""; b.el.classList.remove("walk"); settle(); }, 3050));
    // Every bulb red and flashing, and the naughty list unrolls.
    redUntil = Date.now() + 8000;
    root.classList.add("xm-red"); bg.classList.add("alarm");
    festiveTimers.push(setTimeout(() => { if (Date.now() >= redUntil - 50) { root.classList.remove("xm-red"); bg.classList.remove("alarm"); } }, 8000));
    const list = document.createElement("div");
    list.className = "xm-list";
    list.style.top = ((document.querySelector("header")?.getBoundingClientRect().bottom || 60) + 16) + "px";
    list.innerHTML = `<b>NAUGHTY LIST</b><s>the Grumbletons</s><s>next door's cat</s><span>✗ ${esc(nameOrIp(h))}</span>`;
    root.appendChild(list);
    festiveTimers.push(setTimeout(() => list.remove(), 5600));
    festiveTimers.push(setTimeout(() => { if (!b.gone) intruderTagEl(b.h, "held", b.tag); }, 9000));
  };
  festiveHooks.rendered = syncIntruders;
  syncIntruders();

  if (calm) return;
  // Every bulb twinkles on its own clock, like the old twinkle bulbs: lit most
  // of the time, dark for a moment at uneven intervals, now and then a quick
  // double blink or a longer rest. One ticker drives them all.
  let rippling = false;
  const lamps = [...root.querySelectorAll(".xl")].map(el => {
    const off = Math.random() < 0.2;
    if (off) el.classList.add("off");
    return { el, off, next: performance.now() + rnd(0, 3000) };
  });
  festiveTimers.push(setInterval(() => {
    if (rippling) return;
    const now = performance.now();
    for (const b of lamps) {
      if (now < b.next) continue;
      b.off = !b.off;
      b.el.classList.toggle("off", b.off);
      if (b.off) {
        b.next = now + (Math.random() < 0.15 ? rnd(900, 2200) : rnd(120, 650));   // mostly a blink, sometimes a rest
      } else {
        b.next = now + (Math.random() < 0.2 ? rnd(90, 260) : rnd(700, 4500));    // sometimes straight back off: a double blink
      }
    }
  }, 50));
  // A little snow.
  for (let i = 0; i < 26; i++) {
    const f = document.createElement("div");
    const size = rnd(2, 5);
    f.className = "snow";
    f.style.cssText = `left:${rnd(0, 100).toFixed(1)}vw;width:${size}px;height:${size}px;--d:${rnd(9, 20).toFixed(1)}s;--o:-${rnd(0, 20).toFixed(1)}s;--x:${rnd(-40, 40).toFixed(0)}px`;
    root.appendChild(f);
  }

  const later = (fn, ms) => festiveTimers.push(setTimeout(fn, ms));
  const shown = () => !document.hidden;   // a hidden tab runs no animations; don't spawn into it
  // The drift builds up over a few minutes, then stays.
  let depth = .2;
  festiveTimers.push(setInterval(() => { if (depth < 1) { depth = Math.min(1, depth + .08); drift.style.transform = `scaleY(${depth})`; } }, 18000));

  // Santa's sleigh across the top; about half the time he drops a present,
  // which tumbles down and lands in the snow (only the last three stay).
  const landed = [];
  const dropGift = x => {
    const g = document.createElement("div");
    g.className = "gift";
    const top = 40;
    g.style.cssText = `left:${Math.round(x)}px;top:${top}px;--fall:${Math.round(H - top - 30)}px;--spin:${rnd(-160, 160).toFixed(0)}deg`;
    g.innerHTML = giftSvg(GIFT_COLORS[Math.floor(Math.random() * GIFT_COLORS.length)]);
    root.appendChild(g);
    landed.push(g);
    if (landed.length > 3) landed.shift().remove();
  };
  const sleigh = (withGift) => {
    if (!shown()) return;
    const el = document.createElement("div");
    el.className = "sleigh";
    el.innerHTML = SLEIGH;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    root.appendChild(el);
    if (withGift) { const at = rnd(.3, .7); later(() => dropGift(W * at + 30), 11000 * at); }
  };
  const sleighRound = () => { sleigh(Math.random() < .5); later(sleighRound, rnd(100000, 160000)); };
  later(sleighRound, rnd(15000, 25000));

  // Santa looks out of the chimney, waves, and goes back down.
  const santa = house.querySelector(".xh-santa");
  const peek = () => {
    if (shown() && santa) {
      santa.classList.add("up");
      later(() => santa.classList.remove("up"), 6500);
    }
    later(peek, rnd(70000, 130000));
  };
  later(peek, rnd(12000, 20000));

  // A shooting star now and then.
  const star = () => {
    if (shown()) {
      const el = document.createElement("div");
      el.className = "shoot";
      el.style.left = rnd(8, 55).toFixed(0) + "vw";
      el.style.top = rnd(6, 30).toFixed(0) + "vh";
      el.addEventListener("animationend", () => el.remove());
      root.appendChild(el);
    }
    later(star, rnd(25000, 50000));
  };
  later(star, rnd(8000, 15000));

  // A gingerbread man strolls along the bottom now and then.
  const ginger = () => {
    if (shown()) {
      const el = document.createElement("div");
      el.className = "ginger" + (Math.random() < .5 ? " rtl" : "");
      el.innerHTML = GINGER;
      el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
      root.appendChild(el);
    }
    later(ginger, rnd(60000, 110000));
  };
  later(ginger, rnd(30000, 45000));

  // The snowman waves now and then.
  const wave = () => {
    snowman.classList.add("wave");
    later(() => snowman.classList.remove("wave"), 2400);
    later(wave, rnd(20000, 40000));
  };
  later(wave, rnd(5000, 10000));

  // The network: a new device sends Santa over straight away with a present,
  // and a finished scan runs a quick chase along the lights.
  festiveHooks.scanDone = () => {
    // The house blazes for a second, the way it does when the switch finally works.
    if (!document.hidden && !calmMotion()) {
      if (Math.random() < .25 && santa) { santa.classList.add("up"); later(() => santa.classList.remove("up"), 5000); }
      house.classList.add("blaze");
      festiveTimers.push(setTimeout(() => house.classList.remove("blaze"), 1200));
    }
    if (!shown() || rippling) return;
    rippling = true;
    lamps.forEach(b => b.el.classList.add("off"));
    lamps.forEach((b, i) => later(() => b.el.classList.remove("off"), 200 + i * 16));
    later(() => {
      lamps.forEach(b => { b.off = false; b.el.classList.remove("off"); b.next = performance.now() + rnd(500, 3000); });
      rippling = false;
    }, 400 + lamps.length * 16);
  };
}

BAMF.registerTheme("christmas", ctx => buildChristmas(ctx.root, ctx.switched));
})();
