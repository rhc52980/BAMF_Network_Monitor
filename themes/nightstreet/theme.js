// City Lights: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Night Street: a city block after dark. A skyline whose lit windows are how
// much of the network is up, street lamps pooling light on the pavement (one
// always buzzes, and moths circle a couple), and traffic along the road at
// the bottom. A new device arrives by taxi with its name on the roof sign; a
// finished scan brightens every lamp; a device going offline puts one out
// for a moment.
// An airliner at height: a silhouette with its wingtip lights and the strobe
// on the belly. Small, because it's a long way up.
const NS_PLANE = `<svg width="86" height="26" viewBox="0 0 86 26">
  <path d="M6 14q14-5 30-6l16-6 6 1-6 11 18-1 7-4h4l-3 6 3 6h-4l-7-4-18-1 6 11-6 1-16-6q-16-1-30-6z" fill="#28324a" stroke="#3b4764" stroke-width="1"/>
  <path d="M30 9l10-6 5 1-6 9z" fill="#36425e"/>
  <circle class="ns-nav red" cx="8" cy="13" r="3" fill="#ff4a4a"/>
  <circle class="ns-nav green" cx="80" cy="13" r="3" fill="#4aff88"/>
  <circle class="ns-strobe" cx="44" cy="19" r="3" fill="#ffffff"/>
</svg>`;

// The helicopter: rotor turning, nav lights, and the searchlight under the
// nose sweeping the street as it goes.
const NS_HELI = `<svg width="150" height="150" viewBox="0 0 150 150">
  <defs><linearGradient id="nsBeamCone" x1="0" x2="0" y1="0" y2="1">
    <stop offset="0" stop-color="#fff2c4" stop-opacity=".38"/><stop offset="1" stop-color="#fff2c4" stop-opacity="0"/>
  </linearGradient></defs>
  <g transform="translate(52 44)"><g class="ns-beam">
    <path d="M-7 0L-54 104H54L7 0z" fill="url(#nsBeamCone)"/>
    <ellipse cx="0" cy="104" rx="52" ry="9" fill="#fff2c4" opacity=".16"/>
  </g></g>
  <ellipse cx="52" cy="30" rx="30" ry="13" fill="#28324a" stroke="#3b4764" stroke-width="1"/>
  <path d="M74 26q22 2 44 6l2 5q-24 2-46 1z" fill="#28324a"/>
  <path d="M112 20h5l3 16h-5z" fill="#1b2130"/>
  <path d="M30 26q10-9 24-9t20 9z" fill="#6f86b8" opacity=".55"/>
  <path d="M36 42h34l-2 8H38z" fill="#1b2130"/>
  <path d="M28 52h52M34 52v6M74 52v6" stroke="#1b2130" stroke-width="4" fill="none"/>
  <rect x="50" y="8" width="4" height="10" rx="1.5" fill="#1b2130"/>
  <g class="ns-rotor"><ellipse cx="52" cy="8" rx="62" ry="2.6" fill="#8a93a6" opacity=".55"/>
    <ellipse cx="52" cy="8" rx="30" ry="1.6" fill="#b9c2d4" opacity=".4" transform="rotate(60 52 8)"/></g>
  <circle class="ns-nav red" cx="22" cy="30" r="3.2" fill="#ff4a4a"/>
  <circle class="ns-nav green" cx="120" cy="30" r="3" fill="#4aff88"/>
  <circle class="ns-strobe" cx="52" cy="46" r="3.4" fill="#fff2c4"/>
</svg>`;

const NS_CAR = (body, roof) => `<svg width="70" height="24" viewBox="0 0 70 24">
  <path d="M68 14L150 2V26z" fill="url(#nsBeam)" opacity=".8"/>
  <path d="M4 16v-5q0-3 3-3h10l7-6h22l8 6h9q4 0 4 4v4z" fill="${body}"/>
  <path d="M26 3h9v6h-15z M38 3h7l7 6h-14z" fill="${roof}"/>
  <circle cx="17" cy="17" r="4.5" fill="#0c0d10"/><circle cx="54" cy="17" r="4.5" fill="#0c0d10"/>
  <circle cx="17" cy="17" r="1.8" fill="#6b6f78"/><circle cx="54" cy="17" r="1.8" fill="#6b6f78"/>
  <rect x="64" y="10" width="4" height="3" rx="1" fill="#fff6d0"/><rect x="3" y="10" width="3" height="3" rx="1" fill="#ff3b3b"/>
  <defs><linearGradient id="nsBeam" x1="0" x2="1"><stop offset="0" stop-color="#fff2c0" stop-opacity=".55"/><stop offset="1" stop-color="#fff2c0" stop-opacity="0"/></linearGradient></defs></svg>`;

function buildNightStreet(root) {
  const calm = calmMotion();
  const W = innerWidth, H = innerHeight, WALK = H - 30;   // the pavement's top edge, just above the road
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg ns-bg";
  bg.setAttribute("aria-hidden", "true");
  // The skyline: blocks of every height, each with a grid of windows.
  let x = -10, blocks = "", wins = [];
  const shades = ["#0f1320", "#121725", "#0d111b", "#141a29"];
  // One building stands over the rest, and runs its lights as a colour show -
  // the tower every city has now. It goes up somewhere right of centre.
  const towerX = W * rnd(.52, .76);
  let towerDone = false;
  const RGB = ["#ff4d6d", "#ff9a3d", "#ffe14a", "#4dff88", "#4ad4ff", "#b07cff"];
  while (x < W) {
    const tower = !towerDone && x >= towerX;
    if (tower) towerDone = true;
    const bw = tower ? 82 : Math.round(rnd(70, 150));
    const bh = tower ? Math.round(Math.min(H * .74, WALK - 70)) : Math.round(rnd(140, Math.min(440, H * .58)));
    const top = WALK - bh;
    if (tower) {
      // A setback near the top, a crown that washes through the colours, and
      // the aircraft beacon on the mast above it.
      const inset = 14, ct = top + 42;
      // A shade lighter than the other blocks, with an edge: up above the
      // rooftops it has only the sky behind it, and a black tower on a black
      // sky is just windows hanging in the air.
      blocks += `<rect x="${x}" y="${ct}" width="${bw}" height="${bh - 42}" fill="#161d2d" stroke="#222c44" stroke-width="1.5"/>` +
        `<rect x="${x + inset}" y="${top}" width="${bw - inset * 2}" height="52" fill="#1a2234" stroke="#222c44" stroke-width="1.5"/>` +
        `<rect class="ns-crown" x="${x + inset + 3}" y="${top + 4}" width="${bw - inset * 2 - 6}" height="5" fill="#4ad4ff"/>` +
        `<rect class="ns-crown" x="${x + 4}" y="${ct - 5}" width="${bw - 8}" height="4" fill="#b07cff" style="animation-delay:-3s"/>` +
        `<rect x="${x + bw / 2 - 1.5}" y="${top - 46}" width="3" height="48" fill="#1c2233"/>` +
        `<circle class="ns-beacon" cx="${x + bw / 2}" cy="${top - 48}" r="3" fill="#ff4a4a"/>`;
      // Its windows: a grid that waves through the colours, floor by floor.
      let row = 0;
      for (let wy = ct + 10; wy < WALK - 30; wy += 20, row++) {
        let col = 0;
        for (let wx = x + 9; wx < x + bw - 12; wx += 15, col++)
          wins.push(`<rect class="ns-tw" x="${wx}" y="${wy}" width="8" height="11" fill="${RGB[(row + col) % RGB.length]}" style="animation-delay:-${(((row * 2 + col) % 9) * 1.05).toFixed(2)}s"/>`);
      }
    } else {
      blocks += `<rect x="${x}" y="${top}" width="${bw}" height="${bh}" fill="${pick(shades)}"/>`;
      if (Math.random() < .3) blocks += `<rect x="${x + bw / 2 - 1}" y="${top - 26}" width="2" height="26" fill="#1c2233"/><circle cx="${x + bw / 2}" cy="${top - 27}" r="2" fill="#ff4a4a" class="ns-star"/>`;
      for (let wy = top + 14; wy < WALK - 34; wy += 22)
        for (let wx = x + 10; wx < x + bw - 16; wx += 18) wins.push(`<rect class="ns-win" x="${wx}" y="${wy}" width="8" height="11"/>`);
    }
    x += bw + Math.round(rnd(3, 18));
  }
  const stars = Array.from({ length: 40 }, (_, i) =>
    `<circle class="ns-star" cx="${((i * 97.3) % 100) * W / 100}" cy="${((i * 41.9) % 30) * H / 100}" r="${i % 7 ? .9 : 1.4}" fill="#fff" style="animation-duration:${(1.5 + (i % 4) * .7).toFixed(1)}s;animation-delay:-${i % 5}s"/>`).join("");
  // Lamps along the pavement, each with a cone of light and a pool under it.
  const lamps = [];
  for (let lx = 110; lx < W - 40; lx += 290) lamps.push(lx);
  const lampSvg = lamps.map((lx, i) => {
    const hy = WALK - 210, hx = lx + 34;
    const moths = !calm && i % 3 === 1 ? [0, 120, 240].map(a => `<g transform="rotate(${a})"><circle cx="11" r="1.3" fill="#d8d0b8">
        <animateTransform attributeName="transform" type="rotate" from="0" to="360" dur="${2.4 + a / 200}s" repeatCount="indefinite"/></circle></g>`).join("") : "";
    return `<g class="ns-lamp${i === 1 ? " buzz" : ""}">
      <g class="ns-glow"><path d="M${hx - 6} ${hy + 6}L${hx - 80} ${WALK}H${hx + 80}L${hx + 6} ${hy + 6}z" fill="url(#nsCone)"/>
        <ellipse cx="${hx}" cy="${WALK + 2}" rx="95" ry="12" fill="url(#nsPool)"/><circle cx="${hx}" cy="${hy + 5}" r="16" fill="url(#nsPool)"/></g>
      <path d="M${lx} ${WALK}V${hy - 8}q0-14 16-14h14q6 0 6 6" fill="none" stroke="#2b303c" stroke-width="5"/>
      <path d="M${hx - 12} ${hy}h24l-5 6h-14z" fill="#3a3f4c"/><rect class="ns-glow" x="${hx - 7}" y="${hy + 5}" width="14" height="3" rx="1.5" fill="#fff0c4"/>
      <g transform="translate(${hx} ${hy + 5})">${moths}</g></g>`;
  }).join("");
  bg.innerHTML = `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">
    <defs><linearGradient id="nsCone" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#ffcf7a" stop-opacity=".42"/><stop offset="1" stop-color="#ffcf7a" stop-opacity=".02"/></linearGradient>
      <radialGradient id="nsPool"><stop offset="0" stop-color="#ffcf7a" stop-opacity=".5"/><stop offset="1" stop-color="#ffcf7a" stop-opacity="0"/></radialGradient></defs>
    ${stars}
    <circle cx="${W * .82}" cy="${H * .13}" r="30" fill="#e9e4cf"/><circle cx="${W * .82 + 13}" cy="${H * .13 - 7}" r="27" fill="#070a14"/>
    ${blocks}${wins.join("")}
    <rect x="0" y="${WALK}" width="${W}" height="${H - WALK}" fill="#23262f"/><rect x="0" y="${WALK}" width="${W}" height="2" fill="#3a3d47"/>
    ${lampSvg}</svg>`;
  document.body.prepend(bg);
  root.insertAdjacentHTML("beforeend", `<div class="ns-road"></div>`);

  // Lit windows: a share of them, going up and down with the network.
  const winEls = [...bg.querySelectorAll(".ns-win")];
  const lightWindows = (all = false) => {
    const live = hosts.filter(h => !h.ignored && !h.forgotten), on = live.filter(h => h.online).length;
    const want = Math.round(winEls.length * (.18 + .5 * (live.length ? on / live.length : 1)));
    let lit = winEls.filter(w => w.classList.contains("on"));
    // A window or two at a time, as people get home or go to bed.
    let steps = calm || all ? winEls.length : 6;
    while (lit.length < want && steps-- > 0) { const w = pick(winEls.filter(x => !x.classList.contains("on"))); if (!w) break; w.classList.add("on"); if (Math.random() < .08) w.classList.add("tv"); lit.push(w); }
    while (lit.length > want && steps-- > 0) { const w = lit.splice(Math.floor(Math.random() * lit.length), 1)[0]; w.classList.remove("on", "tv"); }
  };
  lightWindows(true);
  // Windows going on and off all over the city, all night: a few flick every
  // couple of seconds, on top of the slow drift that follows the network.
  if (!calm) festiveTimers.push(setInterval(() => {
    if (document.hidden) return;
    for (let i = 0, n = 1 + Math.floor(Math.random() * 3); i < n; i++) {
      const w = pick(winEls);
      if (!w) break;
      if (w.classList.contains("on")) {
        // Someone turns in for the night - or just turns the television on.
        if (Math.random() < .25) w.classList.toggle("tv");
        else w.classList.remove("on", "tv");
      } else {
        w.classList.add("on");
        if (Math.random() < .1) w.classList.add("tv");
      }
    }
  }, 1900));
  // And the slow drift back to however much of the network is up.
  if (!calm) festiveTimers.push(setInterval(() => { if (!document.hidden) lightWindows(); }, 9000));

  // Traffic in the sky. The plane crosses high up; the helicopter comes over
  // lower, with its searchlight sweeping the street.
  const sky = (cls, art, top, secs) => {
    if (calm || document.hidden || document.querySelector("." + cls)) return null;
    const el = document.createElement("div");
    el.className = `ns-air ${cls}` + (Math.random() < .5 ? " rtl" : "");
    el.style.top = top + "px";
    el.style.animationDuration = secs.toFixed(0) + "s";
    el.innerHTML = art;
    el.addEventListener("animationend", e => { if (e.target === el) el.remove(); });
    root.appendChild(el);
    return el;
  };
  // Both fly in the band of sky above the cards, where they can be seen: the
  // plane high in it, the helicopter lower, with its beam reaching down.
  const plane = () => sky("ns-plane", NS_PLANE, rnd(H * .07, H * .12), rnd(38, 60));
  const heli = () => sky("ns-heli", NS_HELI, rnd(H * .08, H * .15), rnd(26, 40));
  const again = (fn, gap) => festiveTimers.push(setTimeout(() => { fn(); again(fn, gap); }, rnd(gap[0], gap[1])));
  if (!calm) {
    festiveTimers.push(setTimeout(() => { plane(); again(plane, [70e3, 150e3]); }, rnd(12e3, 30e3)));
    festiveTimers.push(setTimeout(() => { heli(); again(heli, [120e3, 260e3]); }, rnd(45e3, 90e3)));
  }

  // Traffic.
  const car = (opts = {}) => {
    if (calm || document.hidden) return;
    const rtl = opts.rtl ?? Math.random() < .5;
    const el = document.createElement("div");
    el.className = `ns-car ${rtl ? "rtl lane2" : "lane1"}`;
    const [body, roof] = opts.taxi ? ["#f2c230", "#a7c7e7"] : pick([["#3a5a8c", "#9fb8d8"], ["#8c2f2f", "#b8c8d8"], ["#d8d8d8", "#6b7a8c"], ["#2f6b4f", "#a8c8b8"], ["#4a3a6b", "#b8b0d8"]]);
    el.innerHTML = NS_CAR(body, roof) + (opts.taxi ? `<span class="ns-taxi-sign">${esc(opts.taxi)}</span>` : "");
    el.style.animationDuration = (opts.taxi ? 11 : rnd(6, 10)).toFixed(1) + "s";
    el.addEventListener("animationend", () => el.remove());
    root.appendChild(el);
  };
  const traffic = () => festiveTimers.push(setTimeout(() => { car(); traffic(); }, rnd(9000, 22000)));
  traffic();

  // A scan brightens every lamp, and the Map's wiring with them.
  festiveHooks.scanDone = () => {
    const html = document.documentElement;
    html.classList.add("ns-bright");
    festiveTimers.push(setTimeout(() => html.classList.remove("ns-bright"), 1200));
  };
  festiveStops.push(() => document.documentElement.classList.remove("ns-bright"));
  festiveHooks.newDevice = h => car({ taxi: "NEW: " + (h ? nameOrIp(h) : "a device"), rtl: false });
  const lampEls = [...bg.querySelectorAll(".ns-lamp")];
  festiveHooks.netChange = off => {
    if (!off.length || calm) return;
    const l = pick(lampEls.filter(x => !x.classList.contains("out")));
    if (l) {
      l.classList.add("out");
      festiveTimers.push(setTimeout(() => l.classList.remove("out"), 6000));
    }
    // Something's gone missing, so the helicopter comes over to look for it.
    festiveTimers.push(setTimeout(heli, rnd(1500, 4000)));
  };
  festiveHooks.rendered = () => lightWindows();
}

BAMF.registerTheme("nightstreet", ctx => buildNightStreet(ctx.root, ctx.switched));
})();
