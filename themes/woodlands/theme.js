// Woodlands: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Woodlands: a forest late in the afternoon. Ridges of pine go back into the
// haze towards a low sun, light comes down through the trees in shafts, mist
// lies in the valley, leaves come down, and flocks cross high up with now and
// then a hawk circling. In front of the page a vine runs along the top with
// songbirds perched on it, a trunk stands in each margin - an owl in a hollow
// and a woodpecker on one, a squirrel running up and down the other - and
// along the bottom is a strip of forest floor where a deer, a fox or a rabbit
// comes through, and a butterfly now and then.
//
// The birds on the vine are the network. As many of their perches are taken
// as the share of devices that are up, so a device dropping off sends a bird
// away and one coming back brings one in. A finished scan startles a flock out
// of the trees and wakes the owl, and a new device is someone new wandering
// into the clearing.
function buildWoodlands(root) {
  const calm = calmMotion();
  const c = seasonCanvas(draw, step);
  const { ctx, W, H } = c;
  const fr = frontCanvas(root);
  fr.globalAlpha = 1;
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const S = Math.max(.72, Math.min(1.15, W / 1400));
  const TAU = Math.PI * 2;
  const HY = H * .58;
  const TW = W < 640 ? 9 : 16;
  const BAND = Math.round(58 * S);
  const FY = H - BAND, GY = H - 8 * S;
  const OWL_Y = Math.round(H * .3), WP_Y = Math.round(H * .6);
  const LX = 3 + TW / 2, RX = W - 3 - TW / 2;

  // Room at the bottom for the forest floor, as with the Laser Show's, so it
  // never hides the last rows of the page.
  const room = document.createElement("div");
  room.style.height = `${BAND}px`;
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());

  const layer = () => {
    const cv = document.createElement("canvas");
    cv.width = W * dpr; cv.height = H * dpr;
    const g = cv.getContext("2d");
    g.scale(dpr, dpr);
    return [cv, g];
  };
  const bez = (a, b, cp, u) => (1 - u) * (1 - u) * a + 2 * (1 - u) * u * cp + u * u * b;

  // ---- The shapes ----
  function pine(g, x, y, h) {
    const w = h * .4;
    g.fillRect(x - h * .03, y - h * .16, h * .06, h * .16);
    for (let k = 0; k < 3; k++) {
      const top = y - h + k * h * .22, bot = y - h * .15 - (2 - k) * h * .12, bw = w * (.55 + k * .25);
      g.beginPath(); g.moveTo(x, top); g.lineTo(x + bw, bot); g.lineTo(x - bw, bot); g.closePath(); g.fill();
    }
  }
  function crown(g, x, y, r, col) {
    g.fillStyle = "#1c271d";
    g.fillRect(x - r * .08, y, r * .16, r * 1.5);
    g.fillStyle = col;
    for (const [dx, dy, k] of [[0, 0, 1], [-.6, .25, .75], [.6, .2, .8], [-.25, -.45, .7], [.3, -.4, .72]]) {
      g.beginPath(); g.arc(x + dx * r, y + dy * r, r * k, 0, TAU); g.fill();
    }
  }
  function leaf(g, x, y, sz, rot, col) {
    g.save(); g.translate(x, y); g.rotate(rot);
    g.fillStyle = col;
    g.beginPath(); g.ellipse(0, 0, sz, sz * .45, 0, 0, TAU); g.fill();
    g.restore();
  }
  function fern(g, x, y, h) {
    g.strokeStyle = "#4f7d3a"; g.lineWidth = 1.3;
    for (const a of [-1.15, -.6, -.1, .45, 1.05]) {
      const ex = x + Math.sin(a) * h, ey = y - Math.cos(a) * h * .85;
      const cx = x + Math.sin(a) * h * .35, cy = y - h * .95;
      g.beginPath(); g.moveTo(x, y); g.quadraticCurveTo(cx, cy, ex, ey); g.stroke();
      for (let k = 2; k < 9; k++) {
        const u = k / 9, px = bez(x, ex, cx, u), py = bez(y, ey, cy, u), sz = (1 - u) * h * .16 + 1.2;
        leaf(g, px, py, sz, a + 1.2, "#57893f");
        leaf(g, px, py, sz, a - 1.2 + Math.PI, "#4d7c38");
      }
    }
  }
  function mushroom(g, x, y, sc, red) {
    g.fillStyle = "#e6dcc6";
    g.fillRect(x - 1.6 * sc, y - 7 * sc, 3.2 * sc, 7 * sc);
    g.fillStyle = red ? "#c23b2c" : "#8a5a36";
    g.beginPath(); g.ellipse(x, y - 7 * sc, 6 * sc, 4 * sc, 0, Math.PI, TAU); g.fill();
    if (red) {
      g.fillStyle = "#f3eee2";
      for (const [dx, dy] of [[-2.6, -8.6], [1.4, -9.8], [3.4, -8]]) { g.beginPath(); g.arc(x + dx * sc, y + dy * sc, .9 * sc, 0, TAU); g.fill(); }
    }
  }
  // The big trees framing each side of the view, their crowns reaching in
  // along the top where they show behind the header.
  function bigTree(g, side) {
    const dir = side ? -1 : 1, x = side ? W * .955 : W * .045, tw = 42 * S;
    const bark = g.createLinearGradient(x - tw / 2, 0, x + tw / 2, 0);
    bark.addColorStop(0, "#150f0a"); bark.addColorStop(.4, "#35261a"); bark.addColorStop(1, "#110c08");
    g.fillStyle = bark;
    g.beginPath(); g.moveTo(x - tw * .5, H); g.lineTo(x - tw * .38, 0); g.lineTo(x + tw * .38, 0); g.lineTo(x + tw * .5, H); g.closePath(); g.fill();
    g.strokeStyle = "rgba(0, 0, 0, .35)"; g.lineWidth = 1.4;
    for (let y = 30; y < H; y += rnd(26, 60)) {
      g.beginPath(); g.moveTo(x - tw * .3, y); g.quadraticCurveTo(x - tw * .05, y + 10, x + tw * .2, y + rnd(18, 34)); g.stroke();
    }
    g.strokeStyle = "#2a1e14"; g.lineCap = "round";
    g.lineWidth = 10 * S;
    g.beginPath(); g.moveTo(x, H * .17); g.quadraticCurveTo(x + dir * W * .07, H * .1, x + dir * W * .16, H * .12); g.stroke();
    g.lineWidth = 5 * S;
    g.beginPath(); g.moveTo(x + dir * W * .09, H * .115); g.quadraticCurveTo(x + dir * W * .12, H * .05, x + dir * W * .2, H * .06); g.stroke();
    for (let k = 0; k < 18; k++) {
      g.fillStyle = pick(["#1a3822", "#20452a", "#17331f", "#29512f", "#23492a"]);
      g.beginPath(); g.arc(x + dir * rnd(-W * .03, W * .22), rnd(-30, H * .15), rnd(24, 58) * S, 0, TAU); g.fill();
    }
  }
  // A big tree out in the middle, like the ones at the edges but with its
  // crown spreading both ways, and kept up behind the header so the band just
  // below it keeps a little sky.
  function midTree(g, x, tw, crownLow) {
    const bark = g.createLinearGradient(x - tw / 2, 0, x + tw / 2, 0);
    bark.addColorStop(0, "#150f0a"); bark.addColorStop(.4, "#33241a"); bark.addColorStop(1, "#110c08");
    g.fillStyle = bark;
    g.beginPath(); g.moveTo(x - tw * .5, H); g.lineTo(x - tw * .36, 0); g.lineTo(x + tw * .36, 0); g.lineTo(x + tw * .5, H); g.closePath(); g.fill();
    g.strokeStyle = "rgba(0, 0, 0, .35)"; g.lineWidth = 1.4;
    for (let y = 30; y < H; y += rnd(26, 60)) {
      g.beginPath(); g.moveTo(x - tw * .3, y); g.quadraticCurveTo(x - tw * .05, y + 10, x + tw * .2, y + rnd(18, 34)); g.stroke();
    }
    g.strokeStyle = "#2a1e14"; g.lineCap = "round"; g.lineWidth = 7 * S;
    for (const d of [-1, 1]) { g.beginPath(); g.moveTo(x, crownLow + 8 * S); g.quadraticCurveTo(x + d * W * .04, crownLow - 14 * S, x + d * W * .08, crownLow - 22 * S); g.stroke(); }
    for (let k = 0; k < 16; k++) {
      g.fillStyle = pick(["#1a3822", "#20452a", "#17331f", "#29512f", "#23492a"]);
      g.beginPath(); g.arc(x + rnd(-W * .1, W * .1), rnd(-40, crownLow - 10 * S), rnd(22, 50) * S, 0, TAU); g.fill();
    }
  }
  // A branch, from a trunk out to its tip, tapering.
  function limb(g, x0, y0, x1, y1, w0) {
    g.strokeStyle = "#2c2016"; g.lineCap = "round";
    const n = 6;
    for (let i = 0; i < n; i++) {
      const u0 = i / n, u1 = (i + 1) / n;
      g.lineWidth = w0 * (1 - u0 * .65);
      g.beginPath(); g.moveTo(x0 + (x1 - x0) * u0, y0 + (y1 - y0) * u0 + Math.sin(u0 * Math.PI) * 4 * S);
      g.lineTo(x0 + (x1 - x0) * u1, y0 + (y1 - y0) * u1 + Math.sin(u1 * Math.PI) * 4 * S); g.stroke();
    }
    for (let k = 0; k < 5; k++) leaf(g, x0 + (x1 - x0) * rnd(.35, 1), y0 + (y1 - y0) * rnd(.35, 1) + rnd(-6, 6) * S, rnd(5, 8) * S, rnd(-1, 1), pick(["#2f5a2a", "#3d6e32", "#356231"]));
  }
  // A songbird facing right, feet at the origin.
  function songbird(g, x, y, dir, sc, flap, k, perched, flick) {
    g.save(); g.translate(x, y); g.scale(dir * sc, sc);
    g.fillStyle = k.back;
    g.save(); g.translate(-4.5, -3.2); g.rotate(flick > 0 ? -.5 : 0);
    g.beginPath(); g.moveTo(0, 0); g.lineTo(-6.5, 1.5); g.lineTo(-6, 3.4); g.lineTo(0, 1.6); g.closePath(); g.fill();
    g.restore();
    g.beginPath(); g.ellipse(0, -4, 6, 4.2, -.12, 0, TAU); g.fill();
    g.fillStyle = k.breast; g.beginPath(); g.ellipse(2.2, -2.8, 3.5, 3, 0, 0, TAU); g.fill();
    g.fillStyle = k.head; g.beginPath(); g.arc(5, -7.6, 3.1, 0, TAU); g.fill();
    g.fillStyle = "#e3a232"; g.beginPath(); g.moveTo(7.8, -8.2); g.lineTo(10.8, -7.5); g.lineTo(7.8, -6.8); g.closePath(); g.fill();
    g.fillStyle = "#0b0b0b"; g.beginPath(); g.arc(6, -8.3, .8, 0, TAU); g.fill();
    g.fillStyle = k.wing;
    g.beginPath(); g.ellipse(-1, -5 - flap * 3.5, 4.4, 2.1 + Math.abs(flap) * 1.6, -.3 - flap * .7, 0, TAU); g.fill();
    if (perched) {
      g.strokeStyle = "#3a2a1c"; g.lineWidth = .9;
      g.beginPath(); g.moveTo(-.5, -.3); g.lineTo(-.5, 1.2); g.moveTo(1.8, -.4); g.lineTo(1.8, 1.2); g.stroke();
    }
    g.restore();
  }
  // A deer facing right, feet at the origin. head: 0 up, 1 down grazing.
  function deer(g, x, y, dir, sc, ph, head, antlers) {
    g.save(); g.translate(x, y); g.scale(dir * sc, sc);
    const leg = (lx, a, col) => { g.save(); g.translate(lx, -26); g.rotate(a); g.fillStyle = col; g.fillRect(-1.3, 0, 2.6, 26); g.restore(); };
    const s1 = Math.sin(ph) * .35, s2 = Math.sin(ph + Math.PI) * .35;
    leg(-14, s1, "#4e321c"); leg(12, s2, "#4e321c");
    g.fillStyle = "#8b5a33"; g.beginPath(); g.ellipse(0, -33, 20, 9.5, 0, 0, TAU); g.fill();
    g.fillStyle = "#c69a6a"; g.beginPath(); g.ellipse(0, -28.5, 15, 4, 0, 0, TAU); g.fill();
    g.fillStyle = "#f2ead8"; g.beginPath(); g.ellipse(-20, -37, 3, 4.5, -.5, 0, TAU); g.fill();
    leg(-10, s2, "#6b4426"); leg(15, s1, "#6b4426");
    g.save(); g.translate(14, -38); g.rotate(head * 1.15);
    g.fillStyle = "#8b5a33";
    g.beginPath(); g.moveTo(-4, 4); g.lineTo(3, -16); g.lineTo(9, -14); g.lineTo(5, 5); g.closePath(); g.fill();
    g.beginPath(); g.ellipse(9, -17, 7, 4.2, .35, 0, TAU); g.fill();
    g.fillStyle = "#3a2616"; g.beginPath(); g.arc(15, -15, 1.6, 0, TAU); g.fill();
    g.fillStyle = "#1a120b"; g.beginPath(); g.arc(9, -18.5, 1, 0, TAU); g.fill();
    g.fillStyle = "#7a4d2b"; g.beginPath(); g.ellipse(4, -22, 2, 4.5, -.6, 0, TAU); g.fill();
    if (antlers) {
      g.strokeStyle = "#d9c7a0"; g.lineWidth = 1.4; g.lineCap = "round";
      g.beginPath(); g.moveTo(6, -21); g.lineTo(3, -31); g.moveTo(4.4, -26); g.lineTo(0, -29); g.moveTo(3, -31); g.lineTo(6.5, -35); g.moveTo(3, -31); g.lineTo(-.5, -34); g.stroke();
    }
    g.restore();
    g.restore();
  }
  // A fox facing right, feet at the origin. nose: 0 level, 1 down sniffing.
  // A black bear, big and dark, a rim of evening light along its back so it
  // reads against the floor, and a glint in its eye.
  function bear(g, x, y, dir, sc, ph, sniff) {
    g.save(); g.translate(x, y); g.scale(dir * sc, sc);
    const leg = (lx, a) => { g.save(); g.translate(lx, -14); g.rotate(a); g.fillStyle = "#1c130d"; g.fillRect(-3, 0, 6.5, 14); g.restore(); };
    const s1 = Math.sin(ph) * .35, s2 = Math.sin(ph + Math.PI) * .35;
    leg(-14, s1); leg(10, s2);
    g.fillStyle = "#2b1e15";
    g.beginPath(); g.ellipse(-2, -22, 22, 12, 0, 0, TAU); g.fill();
    g.beginPath(); g.ellipse(5, -29, 10, 7.5, 0, 0, TAU); g.fill();
    g.strokeStyle = "rgba(255, 214, 160, .45)"; g.lineWidth = 1.2;
    g.beginPath(); g.ellipse(-2, -22, 22, 12, 0, Math.PI * 1.08, Math.PI * 1.92); g.stroke();
    leg(-9, s2); leg(14, s1);
    g.save(); g.translate(18, -25); g.rotate(sniff * .3);
    g.fillStyle = "#2b1e15";
    g.beginPath(); g.ellipse(4, -2, 8.5, 7, 0, 0, TAU); g.fill();
    g.beginPath(); g.arc(-1, -8.5, 3, 0, TAU); g.arc(5.5, -9.5, 3, 0, TAU); g.fill();
    g.fillStyle = "#4a3526"; g.beginPath(); g.ellipse(11.5, 1, 5, 3.4, 0, 0, TAU); g.fill();
    g.fillStyle = "#0d0907"; g.beginPath(); g.arc(15.8, .4, 1.4, 0, TAU); g.fill();
    g.fillStyle = "#ffcf5a"; g.beginPath(); g.arc(7.5, -4, 1.1, 0, TAU); g.fill();
    g.restore();
    g.restore();
  }
  function fox(g, x, y, dir, sc, ph, nose) {
    g.save(); g.translate(x, y); g.scale(dir * sc, sc);
    const leg = (lx, a) => { g.save(); g.translate(lx, -12); g.rotate(a); g.fillStyle = "#2a1a12"; g.fillRect(-1.1, 0, 2.2, 12); g.restore(); };
    const s1 = Math.sin(ph) * .45, s2 = Math.sin(ph + Math.PI) * .45;
    leg(-9, s1); leg(8, s2);
    g.fillStyle = "#d9772b"; g.beginPath(); g.ellipse(-20, -16, 11, 4.5, -.25 + Math.sin(ph * .5) * .1, 0, TAU); g.fill();
    g.fillStyle = "#f4efe6"; g.beginPath(); g.ellipse(-29, -18.5, 3.4, 2.8, -.25, 0, TAU); g.fill();
    g.fillStyle = "#d9772b"; g.beginPath(); g.ellipse(0, -17, 13, 6, 0, 0, TAU); g.fill();
    leg(-6, s2); leg(10, s1);
    g.save(); g.translate(11, -20); g.rotate(nose * .55);
    g.fillStyle = "#d9772b";
    g.beginPath(); g.ellipse(4, -2, 6, 4.4, 0, 0, TAU); g.fill();
    g.beginPath(); g.moveTo(7, -1); g.lineTo(14, 1); g.lineTo(7, 3); g.closePath(); g.fill();
    g.fillStyle = "#f4efe6"; g.beginPath(); g.ellipse(5, 1.5, 3.5, 1.8, 0, 0, TAU); g.fill();
    g.fillStyle = "#1b120c"; g.beginPath(); g.arc(14, 1, 1.1, 0, TAU); g.fill();
    g.beginPath(); g.arc(6, -3, .9, 0, TAU); g.fill();
    g.fillStyle = "#b85e1f";
    g.beginPath(); g.moveTo(1, -5); g.lineTo(2.5, -11); g.lineTo(5, -5.5); g.closePath(); g.fill();
    g.beginPath(); g.moveTo(4, -5.5); g.lineTo(6.5, -11); g.lineTo(8, -5); g.closePath(); g.fill();
    g.restore();
    g.restore();
  }
  // A rabbit facing right, feet at the origin. up: 0 crouched, 1 sitting up.
  function rabbit(g, x, y, dir, sc, up) {
    g.save(); g.translate(x, y); g.scale(dir * sc, sc);
    g.fillStyle = "#9b8a76";
    g.beginPath(); g.ellipse(0, -6 - up * 2, 8, 6 + up * 1.5, -.2 - up * .5, 0, TAU); g.fill();
    g.beginPath(); g.arc(7, -12 - up * 5, 4, 0, TAU); g.fill();
    g.beginPath(); g.ellipse(5, -19 - up * 6, 1.6, 5.5, -.2, 0, TAU); g.fill();
    g.beginPath(); g.ellipse(7.5, -19.5 - up * 6, 1.6, 5.5, .15, 0, TAU); g.fill();
    g.fillStyle = "#f5f0e6"; g.beginPath(); g.arc(-8, -6, 2.6, 0, TAU); g.fill();
    g.fillStyle = "#1b1410"; g.beginPath(); g.arc(9, -13 - up * 5, .9, 0, TAU); g.fill();
    g.restore();
  }
  // A squirrel facing right, centred on its body.
  function squirrel(g, ph) {
    g.fillStyle = "#8a6446";
    g.beginPath(); g.moveTo(-6, 0); g.bezierCurveTo(-16, 2, -18, -12, -10, -14 + Math.sin(ph) * 1.5); g.bezierCurveTo(-6, -15, -4, -9, -7, -7); g.closePath(); g.fill();
    g.beginPath(); g.ellipse(0, 0, 7, 4, 0, 0, TAU); g.fill();
    g.beginPath(); g.arc(7, -1, 3.2, 0, TAU); g.fill();
    g.beginPath(); g.moveTo(6, -3.5); g.lineTo(6.5, -6.5); g.lineTo(8, -4); g.closePath(); g.fill();
    g.fillStyle = "#e8dcc4"; g.beginPath(); g.ellipse(1, 2, 4.5, 1.6, 0, 0, TAU); g.fill();
    g.fillStyle = "#000"; g.beginPath(); g.arc(8.4, -1.6, .7, 0, TAU); g.fill();
  }
  // A woodpecker clinging to the trunk on its left, facing it.
  function woodpecker(g, x, y, sc, peck) {
    g.save(); g.translate(x, y); g.scale(sc, sc);
    g.fillStyle = "#1d1d1d"; g.beginPath(); g.ellipse(2, 2, 3.4, 8, .15, 0, TAU); g.fill();
    g.fillStyle = "#f2f2f2"; g.fillRect(2.5, -2, 1.4, 8); g.fillRect(.5, 1, 1.2, 5);
    g.fillStyle = "#1d1d1d"; g.beginPath(); g.moveTo(0, 8); g.lineTo(-1, 14); g.lineTo(2, 9); g.closePath(); g.fill();
    g.save(); g.translate(-peck * 2.5, 0);
    g.fillStyle = "#f2f2f2"; g.beginPath(); g.arc(0, -7, 3, 0, TAU); g.fill();
    g.fillStyle = "#d8322a"; g.beginPath(); g.arc(1.2, -9.2, 1.9, 0, TAU); g.fill();
    g.fillStyle = "#3a3a3a"; g.beginPath(); g.moveTo(-2.5, -7.5); g.lineTo(-7, -6.8); g.lineTo(-2.5, -6); g.closePath(); g.fill();
    g.fillStyle = "#000"; g.beginPath(); g.arc(-.5, -7.6, .7, 0, TAU); g.fill();
    g.restore();
    g.restore();
  }
  // A bald eagle in the air, seen from below: broad dark wings with the
  // fingered tips, and the white head and tail. Head towards -y.
  function eagleFlying(x, y, sc, ang, flap) {
    ctx.save(); ctx.translate(x, y); ctx.rotate(ang + Math.PI / 2); ctx.scale(sc, sc);
    ctx.fillStyle = "#2c1d11";
    for (const d of [-1, 1]) {
      ctx.beginPath();
      ctx.moveTo(0, -2);
      ctx.quadraticCurveTo(d * 14, -7 - flap * 6, d * 34, -3 - flap * 9);
      ctx.lineTo(d * 37, -flap * 8); ctx.lineTo(d * 33, 1 - flap * 7); ctx.lineTo(d * 35, 3 - flap * 6); ctx.lineTo(d * 30, 3 - flap * 5);
      ctx.quadraticCurveTo(d * 14, 5 - flap * 2, 0, 4);
      ctx.closePath(); ctx.fill();
    }
    ctx.beginPath(); ctx.ellipse(0, 1, 4, 9, 0, 0, TAU); ctx.fill();
    ctx.fillStyle = "#f4f1e8"; ctx.beginPath(); ctx.arc(0, -8, 3.6, 0, TAU); ctx.fill();
    ctx.fillStyle = "#f2b632"; ctx.beginPath(); ctx.moveTo(-1.2, -11); ctx.lineTo(1.2, -11); ctx.lineTo(0, -13.5); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#f1eee4"; ctx.beginPath(); ctx.moveTo(-3, 9); ctx.lineTo(3, 9); ctx.lineTo(5.5, 16); ctx.lineTo(-5.5, 16); ctx.closePath(); ctx.fill();
    ctx.restore();
  }
  // A bald eagle sitting in the nest, facing right: the white head and the
  // hooked yellow beak over the rim.
  function eagleSitting(x, y, dir, sc) {
    ctx.save(); ctx.translate(x, y); ctx.scale(dir * sc, sc);
    ctx.fillStyle = "#3a2616"; ctx.beginPath(); ctx.ellipse(0, -8, 12, 11, 0, 0, TAU); ctx.fill();
    ctx.fillStyle = "#f4f1e8"; ctx.beginPath(); ctx.ellipse(5, -22, 8, 7, -.2, 0, TAU); ctx.fill();
    ctx.fillStyle = "#f2b632";
    ctx.beginPath(); ctx.moveTo(11, -24.5); ctx.quadraticCurveTo(19.5, -23.5, 18, -18); ctx.lineTo(15.5, -19.8); ctx.lineTo(11, -20.5); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#1a1208"; ctx.beginPath(); ctx.arc(8.5, -24, 1.1, 0, TAU); ctx.fill();
    ctx.strokeStyle = "rgba(0, 0, 0, .35)"; ctx.lineWidth = .8; ctx.beginPath(); ctx.moveTo(6.5, -25.8); ctx.lineTo(10.5, -25.2); ctx.stroke();
    ctx.restore();
  }
  function skyBird(x, y, sc, flap) {
    ctx.strokeStyle = "rgba(22, 30, 28, .7)";
    ctx.lineWidth = 1.8 * sc;
    ctx.beginPath();
    ctx.moveTo(x - 8 * sc, y - flap * 4 * sc); ctx.quadraticCurveTo(x - 3 * sc, y - 4 * sc, x, y);
    ctx.quadraticCurveTo(x + 3 * sc, y - 4 * sc, x + 8 * sc, y - flap * 4 * sc); ctx.stroke();
  }

  // The band just under the header is open page, where the back layer shows
  // through: the nest, the owl and the snake live there.
  const HB = Math.round(document.querySelector("header")?.getBoundingClientRect().bottom || 60);
  const NX = W * .70, NY = HB + 34 * S;          // the eagles' nest
  const OX = W * .885, OY = HB + 58 * S;         // the owl on its branch
  const BX = W * .80, BY = HB + 76 * S;          // the owls' nest: a plastic laundry hamper
  const SX = W * .56 + 70 * S, SY = HB + 22 * S; // where the snake hangs over

  // ---- Behind the page: the landscape, drawn once ----
  const [land, lc] = layer();
  {
    const sky = lc.createLinearGradient(0, 0, 0, HY + H * .12);
    sky.addColorStop(0, "#0c1e29"); sky.addColorStop(.36, "#1c3d49"); sky.addColorStop(.62, "#4a6858");
    sky.addColorStop(.82, "#ad915c"); sky.addColorStop(1, "#5f6440");
    lc.fillStyle = sky; lc.fillRect(0, 0, W, H);
    const sx = W * .72, sy = HY - H * .05;
    const sun = lc.createRadialGradient(sx, sy, 0, sx, sy, H * .45);
    sun.addColorStop(0, "rgba(255, 224, 150, .8)"); sun.addColorStop(.22, "rgba(255, 206, 130, .3)"); sun.addColorStop(1, "rgba(255, 200, 120, 0)");
    lc.fillStyle = sun; lc.fillRect(0, 0, W, H);
    const ridge = (base, amp, col, treeCol, tMin, tMax, gap, seed) => {
      const ys = x => base - amp * (.5 + .3 * Math.sin(x / 190 + seed) + .2 * Math.sin(x / 67 + seed * 2.3));
      lc.fillStyle = col;
      lc.beginPath(); lc.moveTo(0, H);
      for (let x = 0; x <= W + 8; x += 8) lc.lineTo(x, ys(x));
      lc.lineTo(W, H); lc.closePath(); lc.fill();
      lc.fillStyle = treeCol;
      for (let x = rnd(0, gap); x < W; x += rnd(gap * .5, gap * 1.3)) pine(lc, x, ys(x) + 3, rnd(tMin, tMax));
    };
    ridge(HY - H * .02, H * .06, "#3a5954", "#314e49", 10 * S, 22 * S, 9 * S, 1.3);
    ridge(HY + H * .035, H * .05, "#29443f", "#223b36", 18 * S, 38 * S, 14 * S, 4.1);
    ridge(HY + H * .1, H * .045, "#1b3028", "#162921", 34 * S, 72 * S, 24 * S, 2.2);
    for (let x = rnd(0, 120); x < W; x += rnd(90, 220)) crown(lc, x, HY + H * .1 - rnd(24, 54) * S, rnd(20, 38) * S, pick(["#1a3326", "#1e3a2a", "#173022"]));
    lc.fillStyle = "#101c16"; lc.fillRect(0, HY + H * .15, W, H);
    // Three more big trees out across the middle: one holds the nest, one
    // the snake's branch.
    midTree(lc, W * .34, 36 * S, HB + 8 * S);
    midTree(lc, W * .56, 38 * S, HB + 4 * S);
    midTree(lc, NX, 40 * S, HB + 18 * S);
    bigTree(lc, 0); bigTree(lc, 1);
    // The snake's branch, reaching out from the middle tree; the owl's, in
    // from the right-hand edge tree; and the fork the nest sits in.
    limb(lc, W * .56, SY + 4 * S, SX + 60 * S, SY - 2 * S, 9 * S);
    limb(lc, W * .955, OY + 10 * S, OX - 40 * S, OY + 2 * S, 10 * S);
    // The fork the laundry basket is wedged in.
    limb(lc, W * .955, BY + 16 * S, BX - 30 * S, BY + 3 * S, 9 * S);
    lc.strokeStyle = "#2c2016"; lc.lineCap = "round"; lc.lineWidth = 5 * S;
    lc.beginPath(); lc.moveTo(BX + 10 * S, BY + 8 * S); lc.lineTo(BX + 26 * S, BY - 6 * S); lc.stroke();
    lc.strokeStyle = "#2c2016"; lc.lineCap = "round"; lc.lineWidth = 6 * S;
    for (const d of [-1, 1]) { lc.beginPath(); lc.moveTo(NX, NY + 20 * S); lc.lineTo(NX + d * 30 * S, NY - 2 * S); lc.stroke(); }
  }
  // The nest's sticks, laid once: some along the back rim, behind the eagle,
  // and the rest across the front of the bowl.
  const sticks = back => Array.from({ length: back ? 16 : 30 }, () => {
    const x = rnd(-34, 34), y = back ? rnd(-12, -6) : rnd(-7, 4), a = rnd(-.5, .5), l = rnd(10, 22);
    return [x - Math.cos(a) * l / 2, y - Math.sin(a) * l / 2, x + Math.cos(a) * l / 2, y + Math.sin(a) * l / 2, pick(["#6b4a2a", "#4a331d", "#7d5a36", "#5a3e22"]), rnd(1.4, 2.6)];
  });
  const nestBack = sticks(true), nestFront = sticks(false);

  // ---- In front of the page: the vine, the margin trunks and the floor, drawn once ----
  const ANCH = Math.max(3, Math.round(W / 300));
  const vineY = x => {
    const seg = W / ANCH, i = Math.max(0, Math.min(ANCH - 1, Math.floor(x / seg))), u = (x - i * seg) / seg;
    return 3 + 13 * S * 4 * u * (1 - u);
  };
  const GREENS = ["#2f5a2a", "#3d6e32", "#4b8038", "#356231"];
  const [still, st] = layer();
  {
    for (const cx of [LX, RX]) {
      const x0 = cx - TW / 2;
      const g = st.createLinearGradient(x0, 0, x0 + TW, 0);
      g.addColorStop(0, "#24180f"); g.addColorStop(.45, "#4a3322"); g.addColorStop(1, "#1d140d");
      st.fillStyle = g; st.fillRect(x0, 0, TW, FY + 4);
      st.strokeStyle = "rgba(0, 0, 0, .4)"; st.lineWidth = 1;
      for (let y = 20; y < FY; y += rnd(14, 34)) { st.beginPath(); st.moveTo(x0 + TW * .2, y); st.lineTo(x0 + TW * .6, y + rnd(6, 12)); st.stroke(); }
    }
    st.fillStyle = "#0a0604";
    st.beginPath(); st.ellipse(LX, OWL_Y, TW * .38, TW * .6, 0, 0, TAU); st.fill();
    st.strokeStyle = "#5a3f2a"; st.lineWidth = 1.5; st.stroke();
    st.strokeStyle = "#35522a"; st.lineWidth = 3 * S;
    st.beginPath(); for (let x = 0; x <= W; x += 4) st.lineTo(x, vineY(x)); st.stroke();
    for (let x = 4; x < W; x += rnd(9, 17)) leaf(st, x, vineY(x) + pick([-3, 4]) * S, rnd(4, 7) * S, rnd(-.8, .8), pick(GREENS));
    const fg = st.createLinearGradient(0, FY, 0, H);
    fg.addColorStop(0, "#1e301d"); fg.addColorStop(.35, "#172618"); fg.addColorStop(1, "#0c140d");
    st.fillStyle = fg; st.fillRect(0, FY, W, BAND);
    st.fillStyle = "#26401f";
    for (let x = 0; x < W; x += rnd(10, 22)) { st.beginPath(); st.ellipse(x, FY + 2, rnd(8, 16), rnd(3, 6), 0, 0, TAU); st.fill(); }
    st.strokeStyle = "#436e33"; st.lineWidth = 1.2;
    for (let x = 0; x < W; x += rnd(3, 7)) {
      const h = rnd(5, 13) * S, lean = rnd(-3, 3);
      st.beginPath(); st.moveTo(x, FY + 5); st.quadraticCurveTo(x + lean * .5, FY + 5 - h * .6, x + lean, FY + 5 - h); st.stroke();
    }
    for (let x = rnd(40, 140); x < W - 30; x += rnd(160, 340)) fern(st, x, FY + 10 * S, rnd(20, 30) * S);
    for (let x = rnd(80, 220); x < W - 40; x += rnd(170, 400)) mushroom(st, x, rnd(FY + 16 * S, H - 8), rnd(.8, 1.25) * S, Math.random() < .55);
    st.fillStyle = "#3a4038";
    for (let x = rnd(30, 200); x < W; x += rnd(120, 300)) { st.beginPath(); st.ellipse(x, rnd(FY + 20 * S, H - 6), rnd(4, 9) * S, rnd(3, 5) * S, 0, 0, TAU); st.fill(); }
    // A fallen log, over towards the right.
    const lx = W * .8, lw = Math.min(170, W * .13), ly = H - 14 * S, lh = 11 * S;
    st.fillStyle = "#4a3321"; st.fillRect(lx, ly - lh, lw, lh * 2);
    st.fillStyle = "#6b4c33"; st.beginPath(); st.ellipse(lx + lw, ly, lh * .55, lh, 0, 0, TAU); st.fill();
    st.strokeStyle = "#4a3321"; st.lineWidth = 1; st.beginPath(); st.ellipse(lx + lw, ly, lh * .3, lh * .55, 0, 0, TAU); st.stroke();
    st.fillStyle = "#35592b";
    for (let k = 0; k < 5; k++) { st.beginPath(); st.ellipse(lx + rnd(0, lw), ly - lh, rnd(5, 10), 2.5, 0, 0, TAU); st.fill(); }
  }

  // ---- What moves ----
  const shafts = Array.from({ length: 5 }, (_, i) => ({ x: W * (.42 + i * .13) + rnd(-30, 30), w: rnd(30, 80) * S, ph: rnd(0, TAU) }));
  const mist = [{ x: rnd(0, W), y: HY + H * .06, rx: W * .5, ry: H * .035, v: 6 }, { x: rnd(0, W), y: HY + H * .11, rx: W * .6, ry: H * .04, v: -4 }];
  const LEAF = ["#c9a13a", "#d98b35", "#a8562c", "#7fae4a", "#5f8c3a", "#e0b54a"];
  const mkLeaf = top => ({ x: rnd(0, W), y: top ? rnd(-H * .3, -8) : rnd(0, H), sz: rnd(3.5, 6.5) * S, r: rnd(0, TAU),
    spin: rnd(-1.5, 1.5), vy: rnd(16, 38) * S, sway: rnd(12, 34), ph: rnd(0, TAU), col: pick(LEAF) });
  const leaves = Array.from({ length: Math.min(36, Math.round(W / 40)) }, () => mkLeaf(false));
  const flocks = [], startled = [], flyers = [], notes = [], chips = [];
  let flockAt = 0;
  // The eagle: sitting in the nest, out soaring, or on its way home.
  const smooth = u => u <= 0 ? 0 : u >= 1 ? 1 : u * u * (3 - 2 * u);
  const eagle = { mode: "nest", at: 0, x: NX, y: NY - 18 * S, px: NX, py: NY, ang: 0, cx: 0, cy: 0, R: 0, a: 0, t: 0, dur: 0,
    from: null, dir: -1, turnAt: 0, flap: 0 };
  // The owl watching over: its head follows the pointer, or looks about.
  const bigOwl = { yaw: 0, want: 0, blink: 0, blinkAt: 0, wide: 0, lookAt: 0, px: null, pAt: 0,
    // Where it is: on its branch watching, flying to the basket, feeding the
    // owlets on its rim, or flying back.
    mode: "perch", at: 0, t: 0, x: OX, y: OY, from: null, to: null };
  const RIM = [BX + 27 * S, BY - 24 * S];          // where the owl stands on the basket
  const owlets = [0, 1, 2].map(i => ({ ph: rnd(0, 6.3), blink: 0, blinkAt: 0, dx: (i - 1) * 11 - 3 }));
  const snake = { flick: 0, flickAt: 0 };
  let walker = null, walkerAt = 0, bunny = null, bunnyAt = 0, bfly = null, bflyAt = 0;
  const owl = { blink: 0, blinkAt: 0, wide: 0 };
  // A new device nobody has marked known is an intruder: a bear, lumbering
  // into the clearing. Alarm calls go up, every bird on the vine bursts off,
  // the flocks scatter, and the great owl's eyes go wide as it turns to fix on
  // it. It stays in the clearing, tagged, the owl watching, until the device
  // is marked known; then it lumbers off, and someone new wanders in.
  const watchIn = intruderWatch();
  const bears = new Map();              // id -> { h, x, dir, ph, sniff, state, alarm, clearAt }
  let redFrom = 0;
  const bearX = k => W * .74 - k * 170 * S;
  const inClearing = () => [...bears.values()].filter(b => b.state !== "out").sort((a, b) => (b.alarm || 0) - (a.alarm || 0));
  const wp = { burst: 0, at: 0 };
  const sq = { y: H * .45, target: H * .45, pause: 2, ph: 0, dir: -1 };

  // The songbirds on the vine, and what their numbers mean.
  const KINDS = [
    { back: "#6b4f36", breast: "#e0703a", head: "#57402c", wing: "#5b432f" },   // robin
    { back: "#3c6fb0", breast: "#d98a4a", head: "#3c6fb0", wing: "#2f5d99" },   // bluebird
    { back: "#e3c03a", breast: "#f1d64c", head: "#222222", wing: "#2b2b2b" },   // goldfinch
    { back: "#8a8f86", breast: "#ece6d6", head: "#1f1f1f", wing: "#6b716a" },   // chickadee
    { back: "#c3322b", breast: "#d5392f", head: "#c3322b", wing: "#a4261f" },   // cardinal
  ];
  const nPerch = Math.max(4, Math.min(12, Math.floor(W / 125)));
  const perches = [];
  {
    const perSeg = Math.ceil(nPerch / ANCH), seg = W / ANCH;
    for (let i = 0; i < ANCH; i++)
      for (let j = 0; j < perSeg && perches.length < nPerch; j++) {
        const x = (i + .3 + .4 * (j + .5) / perSeg) * seg;
        if (x > TW + 14 && x < W - TW - 14) perches.push({ x, y: vineY(x), bird: null });
      }
  }
  const newBird = (now, kind) => ({ kind: kind || pick(KINDS), dir: Math.random() < .5 ? 1 : -1, hop: 0, flick: 0,
    songAt: now + rnd(3e3, 16e3), turnAt: now + rnd(4e3, 12e3) });
  let wantPerched = 0, trafficAt = 0, settled = false;
  // Put the right number on the vine at once, with nothing flying.
  const settle = now => {
    let sitting = perches.filter(p => p.bird);
    while (sitting.length > wantPerched) { pick(sitting).bird = null; sitting = perches.filter(p => p.bird); }
    const empty = perches.filter(p => !p.bird).sort(() => Math.random() - .5);
    for (const p of empty.slice(0, Math.max(0, wantPerched - sitting.length))) p.bird = newBird(now);
  };
  // Until the first list of devices arrives there's nothing to count, so the
  // vine waits for it rather than filling up and then emptying.
  const retarget = () => {
    if (!hosts.length) return;
    const share = hosts.filter(h => h.online).length / hosts.length;
    wantPerched = Math.round(perches.length * share);
    if (share > 0 && !wantPerched) wantPerched = 1;
    if (!settled) { settle(performance.now()); settled = true; }
  };
  const startle = () => {
    const side = Math.random() < .5 ? 0 : 1, n = Math.floor(rnd(14, 22));
    for (let i = 0; i < n; i++) {
      const x = side ? W - rnd(W * .01, W * .2) : rnd(W * .01, W * .2);
      startled.push({ x, y: rnd(H * .02, H * .13), vx: (side ? -1 : 1) * rnd(60, 200) * S, vy: rnd(-150, -60) * S, ph: rnd(0, TAU) });
    }
    owl.wide = 2.6;
  };
  const spawnWalker = kind => {
    const dir = Math.random() < .5 ? 1 : -1;
    kind = kind || (Math.random() < .62 ? "deer" : "fox");
    walker = { kind, dir, x: dir > 0 ? -80 * S : W + 80 * S, stopX: rnd(W * .28, W * .7), state: "walk", until: 0, ph: 0, head: 0,
      antlers: Math.random() < .4, sc: S * (kind === "fox" ? 1 : .95) };
  };
  const spawnBunny = () => {
    const dir = Math.random() < .5 ? 1 : -1;
    bunny = { dir, x: dir > 0 ? -24 : W + 24, hopT: 0, hops: 3, pause: 0, up: 0, sitting: false };
  };

  function step(dt, now) {
    for (const l of leaves) { l.y += l.vy * dt; l.r += l.spin * dt; if (l.y > H + 10) Object.assign(l, mkLeaf(true)); }
    for (const m of mist) { m.x += m.v * dt; if (m.x > W + m.rx) m.x = -m.rx; if (m.x < -m.rx) m.x = W + m.rx; }
    // Flocks crossing, and now and then a hawk.
    if (now > flockAt && flocks.length < 2) {
      const dir = Math.random() < .5 ? 1 : -1, n = Math.floor(rnd(4, 10));
      const birds = Array.from({ length: n }, (_, i) => {
        const rank = Math.ceil(i / 2), side = i % 2 ? 1 : -1;
        return { dx: -dir * rank * 13 * S + rnd(-3, 3), dy: side * rank * 6 * S + rnd(-2, 2), ph: rnd(0, TAU) };
      });
      flocks.push({ x: dir > 0 ? -60 : W + 60, y: rnd(H * .05, H * .26), v: rnd(38, 64) * S * dir, birds });
      flockAt = now + rnd(9e3, 22e3);
    }
    for (let i = flocks.length - 1; i >= 0; i--) {
      const f = flocks[i];
      f.x += f.v * dt; f.y += Math.sin(now / 1500 + i) * 3 * dt;
      if (f.x < -320 || f.x > W + 320) flocks.splice(i, 1);
    }
    // The eagle: sits, turns its head now and then, and every minute or so
    // goes out to soar in wide circles before gliding home.
    const e = eagle;
    e.px = e.x; e.py = e.y;
    e.flap = Math.max(0, e.flap - dt);
    if (e.mode === "nest") {
      e.x = NX; e.y = NY - 18 * S;
      if (now > e.turnAt) { e.dir *= -1; e.turnAt = now + rnd(3e3, 8e3); }
      if (now > e.at) {
        e.mode = "out"; e.t = 0; e.dur = rnd(18, 28); e.from = [e.x, e.y]; e.flap = 2;
        // It circles in the open band under the header, the part of the sky
        // the page doesn't cover, rather than disappearing behind the cards.
        e.cx = rnd(W * .55, W * .8); e.cy = HB + 50 * S;
        e.R = rnd(110, 170) * S; e.a = Math.atan2(NY - e.cy, NX - e.cx);
      }
    } else if (e.mode === "out") {
      e.t += dt; e.a += dt * .5;
      const cx = e.cx + Math.cos(e.a) * e.R, cy = e.cy + Math.sin(e.a) * e.R * .26;
      const u = smooth(Math.min(1, e.t / 2.5));
      e.x = e.from[0] + (cx - e.from[0]) * u; e.y = e.from[1] + (cy - e.from[1]) * u;
      if (Math.random() < dt * .1) e.flap = .8;
      if (e.t > e.dur) { e.mode = "back"; e.t = 0; e.from = [e.x, e.y]; }
    } else {
      e.t += dt;
      const u = smooth(Math.min(1, e.t / 4.5));
      const mx = (e.from[0] + NX) / 2, my = Math.min(e.from[1], NY) - 60 * S;
      e.x = (1 - u) * (1 - u) * e.from[0] + 2 * (1 - u) * u * mx + u * u * NX;
      e.y = (1 - u) * (1 - u) * e.from[1] + 2 * (1 - u) * u * my + u * u * (NY - 18 * S);
      if (u > .8) e.flap = .6;
      if (u >= 1) { e.mode = "nest"; e.at = now + rnd(45e3, 90e3); }
    }
    if (e.mode !== "nest") e.ang = Math.atan2(e.y - e.py, e.x - e.px);
    // The owl: on its branch it follows the pointer while it moves, and
    // otherwise looks about. Every minute or so it flies to the basket, stays
    // a while on the rim with the owlets, and flies back.
    const o = bigOwl;
    const prowler = inClearing()[0];
    if (o.mode === "perch") {
      o.x = OX; o.y = OY;
      if (prowler) { o.want = Math.max(-1, Math.min(1, (prowler.x - OX) / (W * .35))); o.wide = Math.max(o.wide, .4); }
      else if (o.px !== null && now - o.pAt < 4000) o.want = Math.max(-1, Math.min(1, (o.px - OX) / (W * .35)));
      else if (now > o.lookAt) { o.want = rnd(-1, 1); o.lookAt = now + rnd(3e3, 7e3); }
      if (!prowler && now > o.at) { o.mode = "toNest"; o.t = 0; o.from = [OX, OY]; o.to = RIM; }
    } else if (o.mode === "nest") {
      o.x = RIM[0]; o.y = RIM[1];
      o.want = -.6;                         // looking down and in, at the owlets
      o.t += dt;
      if (o.t > o.stay) { o.mode = "toPerch"; o.t = 0; o.from = RIM; o.to = [OX, OY]; }
    } else {
      // A short flight either way: up over an arc and down onto the spot.
      o.t += dt;
      const u = Math.min(1, o.t / 2.2), e = u * u * (3 - 2 * u);
      o.x = o.from[0] + (o.to[0] - o.from[0]) * e;
      o.y = o.from[1] + (o.to[1] - o.from[1]) * e - Math.sin(Math.PI * u) * 34 * S;
      o.want = o.to[0] < o.from[0] ? -1 : 1;
      if (u >= 1) {
        if (o.mode === "toNest") { o.mode = "nest"; o.t = 0; o.stay = rnd(7, 11); }
        else { o.mode = "perch"; o.at = now + rnd(35e3, 70e3); }
      }
    }
    o.yaw += (o.want - o.yaw) * Math.min(1, dt * 3);
    for (const b of owlets) {
      if (now > b.blinkAt) { b.blink = .14; b.blinkAt = now + rnd(2e3, 6e3); }
      b.blink = Math.max(0, b.blink - dt);
    }
    if (now > o.blinkAt) { o.blink = .16; o.blinkAt = now + rnd(2500, 6500); }
    o.blink = Math.max(0, o.blink - dt);
    o.wide = Math.max(0, o.wide - dt);
    // The snake: a flick of the tongue now and then.
    if (now > snake.flickAt) { snake.flick = .35; snake.flickAt = now + rnd(2500, 6000); }
    snake.flick = Math.max(0, snake.flick - dt);
    for (let i = startled.length - 1; i >= 0; i--) {
      const b = startled[i];
      b.x += b.vx * dt; b.y += b.vy * dt; b.vy += 45 * S * dt;
      if (b.y < -40 || b.x < -40 || b.x > W + 40) startled.splice(i, 1);
    }
    // Birds coming and going from the vine, one at a time, until the number
    // sitting matches the share of the network that's up.
    if (settled && now > trafficAt) {
      const sitting = perches.filter(p => p.bird).length, coming = flyers.filter(f => f.to).length;
      if (sitting + coming < wantPerched) {
        const empty = perches.filter(p => !p.bird && !flyers.some(f => f.to === p));
        if (empty.length) {
          const p = pick(empty), fromLeft = Math.random() < .5;
          flyers.push({ kind: pick(KINDS), x0: fromLeft ? -20 : W + 20, y0: rnd(H * .08, H * .25), to: p, t: 0, dur: rnd(1.4, 2.2) });
        }
        trafficAt = now + rnd(450, 900);
      } else if (sitting > wantPerched) {
        const p = pick(perches.filter(q => q.bird)), away = Math.random() < .5 ? -1 : 1;
        flyers.push({ kind: p.bird.kind, x0: p.x, y0: p.y, x1: p.x + away * rnd(220, 420), y1: -40, to: null, t: 0, dur: rnd(1.1, 1.7) });
        p.bird = null;
        trafficAt = now + rnd(450, 900);
      }
    }
    for (let i = flyers.length - 1; i >= 0; i--) {
      const f = flyers[i];
      f.t += dt;
      if (f.t >= f.dur) { if (f.to) f.to.bird = newBird(now, f.kind); flyers.splice(i, 1); }
    }
    for (const p of perches) {
      const b = p.bird;
      if (!b) continue;
      b.hop = Math.max(0, b.hop - dt);
      b.flick = Math.max(0, b.flick - dt);
      if (now > b.turnAt) { if (Math.random() < .5) b.dir *= -1; else b.hop = .3; b.turnAt = now + rnd(3e3, 11e3); }
      if (Math.random() < dt * .25) b.flick = .25;
      if (now > b.songAt) { notes.push({ x: p.x + b.dir * 8 * S, y: p.y + 6 * S, dir: b.dir, t: 0, g: pick(["\u266a", "\u266b"]) }); b.songAt = now + rnd(7e3, 22e3); }
    }
    for (let i = notes.length - 1; i >= 0; i--) {
      const n = notes[i];
      n.t += dt; n.x += n.dir * 18 * dt; n.y += 6 * dt + Math.sin(n.t * 5) * 8 * dt;
      if (n.t > 1.8) notes.splice(i, 1);
    }
    // The owl blinks; the woodpecker drums in bursts; the squirrel runs.
    if (now > owl.blinkAt) { owl.blink = .18; owl.blinkAt = now + rnd(2500, 7000); }
    owl.blink = Math.max(0, owl.blink - dt);
    owl.wide = Math.max(0, owl.wide - dt);
    if (now > wp.at && wp.burst <= 0) { wp.burst = rnd(.7, 1.3); wp.at = now + rnd(4e3, 11e3); }
    if (wp.burst > 0) {
      wp.burst -= dt;
      if (Math.random() < dt * 14) chips.push({ x: LX + TW / 2, y: WP_Y - 7 * S, vx: rnd(20, 70), vy: rnd(-60, -10), t: 0 });
    }
    for (let i = chips.length - 1; i >= 0; i--) {
      const ch = chips[i];
      ch.t += dt; ch.vy += 300 * dt; ch.x += ch.vx * dt; ch.y += ch.vy * dt;
      if (ch.t > .8) chips.splice(i, 1);
    }
    if (sq.pause > 0) { sq.pause -= dt; if (sq.pause <= 0) sq.target = rnd(H * .2, FY - 30 * S); }
    else {
      const d = sq.target - sq.y, v = 120 * S * dt;
      sq.dir = d < 0 ? -1 : 1;
      if (Math.abs(d) <= v) { sq.y = sq.target; sq.pause = rnd(1.5, 5); } else sq.y += Math.sign(d) * v;
      sq.ph += dt * 18;
    }
    // The bears: in to their place, a sniff now and then, and off when cleared.
    inClearing().forEach((b, k) => {
      const tx = bearX(Math.min(k, 1));
      if (b.x == null) b.x = tx;
      const d = tx - b.x;
      if (Math.abs(d) > 2) { b.dir = d > 0 ? 1 : -1; b.x += Math.sign(d) * Math.min(Math.abs(d), 60 * S * dt); b.ph += dt * 5; }
      else { b.state = "held"; b.dir = -1; b.sniff = Math.max(0, Math.sin(now / 1400 + b.x)) ; }
    });
    for (const [id, b] of bears) if (b.state === "out" && now - b.clearAt > 2200) {
      b.dir = 1; b.x += 70 * S * dt; b.ph += dt * 6;
      if (b.x > W + 140 * S) { bears.delete(id); if (!b.h.test && !walker) spawnWalker(); }
    }
    // Along the floor: a deer or a fox, and a rabbit.
    if (!walker && now > walkerAt) spawnWalker();
    if (walker) {
      const w = walker, isDeer = w.kind === "deer", v = (isDeer ? 52 : 92) * S;
      if (w.state === "walk" || w.state === "leave") {
        w.x += w.dir * v * dt; w.ph += dt * (isDeer ? 6 : 11);
        w.head = Math.max(0, w.head - dt * 2);
        if (w.state === "walk" && (w.dir > 0 ? w.x >= w.stopX : w.x <= w.stopX)) {
          w.state = "graze"; w.until = now + (isDeer ? rnd(3500, 7000) : rnd(1200, 2400));
        }
        if (w.state === "leave" && (w.x < -130 * S || w.x > W + 130 * S)) { walker = null; walkerAt = now + rnd(35e3, 80e3); }
      } else if (w.state === "graze") {
        w.head = Math.min(1, w.head + dt * 2.2);
        if (now > w.until) { w.state = isDeer ? "look" : "leave"; w.until = now + rnd(1200, 2200); }
      } else if (w.state === "look") {
        w.head = Math.max(0, w.head - dt * 2.5);
        if (now > w.until) w.state = "leave";
      }
    }
    if (!bunny && now > bunnyAt) spawnBunny();
    if (bunny) {
      const b = bunny;
      if (b.pause > 0) {
        b.pause -= dt;
        b.up = b.sitting ? Math.min(1, b.up + dt * 4) : 0;
        if (b.pause <= 0) { b.hops = Math.floor(rnd(2, 6)); b.sitting = false; }
      } else {
        b.up = Math.max(0, b.up - dt * 6);
        b.hopT += dt;
        b.x += b.dir * 62 * S * dt;
        if (b.hopT >= .42) { b.hopT = 0; b.hops--; if (b.hops <= 0) { b.pause = rnd(.8, 2.6); b.sitting = Math.random() < .45; } }
      }
      if (b.x < -40 || b.x > W + 40) { bunny = null; bunnyAt = now + rnd(20e3, 45e3); }
    }
    if (!bfly && now > bflyAt) bfly = { x: rnd(W * .1, W * .9), y: FY + rnd(14, BAND - 16), t: 0, dir: Math.random() < .5 ? 1 : -1, col: pick(["#ff9f1c", "#f4d35e", "#9ad1ff", "#f7a8c4"]) };
    if (bfly) {
      bfly.t += dt; bfly.x += bfly.dir * 40 * dt;
      bfly.y = Math.max(FY + 10 * S, Math.min(H - 10 * S, bfly.y + Math.sin(bfly.t * 3) * 30 * dt));
      if (bfly.x < -40 || bfly.x > W + 40) { bfly = null; bflyAt = now + rnd(12e3, 30e3); }
    }
  }

  function drawNest(front) {
    ctx.save(); ctx.translate(NX, NY); ctx.scale(S, S); ctx.lineCap = "round";
    if (front) { ctx.fillStyle = "#3f2b18"; ctx.beginPath(); ctx.ellipse(0, -1, 34, 9, 0, 0, Math.PI); ctx.fill(); }
    for (const [x0, y0, x1, y1, col, w] of front ? nestFront : nestBack) {
      ctx.strokeStyle = col; ctx.lineWidth = w; ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(x1, y1); ctx.stroke();
    }
    ctx.restore();
  }
  // The eaglet: a grey fuzzy head that pops up while its parent is away.
  function drawEaglet(t) {
    const bob = Math.max(0, Math.sin(t * 1.3)) * 4 * S;
    ctx.fillStyle = "#b9b4aa"; ctx.beginPath(); ctx.arc(NX - 6 * S, NY - 10 * S - bob, 6 * S, 0, TAU); ctx.fill();
    ctx.fillStyle = "#3a3026"; ctx.beginPath(); ctx.moveTo(NX - 1 * S, NY - 11 * S - bob); ctx.lineTo(NX + 3.5 * S, NY - 10 * S - bob); ctx.lineTo(NX - 1 * S, NY - 8.5 * S - bob); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#111"; ctx.beginPath(); ctx.arc(NX - 4 * S, NY - 12 * S - bob, .9 * S, 0, TAU); ctx.fill();
  }
  // The great horned owl on its branch, watching.
  // The owl in flight, from the side: broad rounded wings, the tufted head.
  function drawOwlFlying(x, y, dir, flap) {
    ctx.save(); ctx.translate(x, y); ctx.scale(dir * S, S);
    ctx.fillStyle = "#6a4a2c";
    ctx.beginPath(); ctx.moveTo(-4, -14); ctx.quadraticCurveTo(-10, -34 - flap * 10, -26, -30 - flap * 14); ctx.quadraticCurveTo(-14, -20, -2, -12); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#7a5634"; ctx.beginPath(); ctx.ellipse(0, -14, 13, 8, -.15, 0, TAU); ctx.fill();
    ctx.fillStyle = "#c7a57a"; ctx.beginPath(); ctx.ellipse(2, -12, 8, 5, -.15, 0, TAU); ctx.fill();
    ctx.fillStyle = "#6a4a2c";
    ctx.beginPath(); ctx.moveTo(2, -14); ctx.quadraticCurveTo(8, -32 - flap * 12, 26, -30 - flap * 14); ctx.quadraticCurveTo(16, -18, 4, -10); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#7a5634"; ctx.beginPath(); ctx.ellipse(13, -19, 7.5, 7, 0, 0, TAU); ctx.fill();
    ctx.beginPath(); ctx.moveTo(10, -24); ctx.lineTo(9, -31); ctx.lineTo(14, -25); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#d8b98a"; ctx.beginPath(); ctx.ellipse(16, -19, 4.5, 5, 0, 0, TAU); ctx.fill();
    ctx.fillStyle = "#f7b733"; ctx.beginPath(); ctx.arc(17.5, -20, 2, 0, TAU); ctx.fill();
    ctx.fillStyle = "#111"; ctx.beginPath(); ctx.arc(18.2, -20, 1, 0, TAU); ctx.fill();
    ctx.fillStyle = "#c9a13a"; ctx.fillRect(-2, -8, 2.5, 4); ctx.fillRect(2, -8, 2.5, 4);
    ctx.restore();
  }
  // The laundry hamper: moulded light-blue plastic with a sheen across it,
  // rows of vent slots, a thick rolled lip and a handle slot at each end, and
  // a sock left hanging over the edge. Back and front are drawn apart so the
  // owlets sit inside it.
  function drawBasket(front) {
    ctx.save(); ctx.translate(BX, BY); ctx.rotate(-.06); ctx.scale(S, S);
    if (!front) {
      ctx.fillStyle = "#23566f"; ctx.beginPath(); ctx.ellipse(0, -23, 23, 4, 0, 0, TAU); ctx.fill();
      ctx.restore();
      return;
    }
    // The tub, a little wider at the top, its bottom corners rounded.
    const g = ctx.createLinearGradient(-23, 0, 23, 0);
    g.addColorStop(0, "#4f9cc3"); g.addColorStop(.3, "#93d3ef"); g.addColorStop(.55, "#72bde2"); g.addColorStop(1, "#3f89ae");
    ctx.fillStyle = g;
    ctx.beginPath(); ctx.moveTo(-23, -23); ctx.lineTo(23, -23); ctx.lineTo(19.6, -3);
    ctx.quadraticCurveTo(19.2, 0, 16.5, 0); ctx.lineTo(-16.5, 0); ctx.quadraticCurveTo(-19.2, 0, -19.6, -3); ctx.closePath(); ctx.fill();
    // Vent slots in rows, following the taper, dark where the inside shows.
    ctx.fillStyle = "#1d4a60";
    for (const y of [-15, -9.5, -4]) {
      const half = 23 - (y + 23) * (3.4 / 23) - 3.5;
      for (let x = -half; x <= half - 2.8; x += 5.4) ctx.fillRect(x, y - 1.7, 2.8, 3.4);
    }
    // The rolled lip, lighter, and the two handle slots just under it.
    ctx.fillStyle = "#b3e3f7"; ctx.fillRect(-24.5, -26.5, 49, 4.8);
    ctx.fillStyle = "rgba(255, 255, 255, .45)"; ctx.fillRect(-24.5, -26.5, 49, 1.2);
    ctx.fillStyle = "#163b4d";
    for (const hx of [-17, 11]) { ctx.beginPath(); ctx.ellipse(hx + 3, -19.4, 3.6, 1.5, 0, 0, TAU); ctx.fill(); }
    // The sock, red with a white band, flopped over the rim.
    ctx.fillStyle = "#c8403a";
    ctx.beginPath(); ctx.moveTo(-22, -26); ctx.lineTo(-17, -26); ctx.lineTo(-17.5, -14); ctx.quadraticCurveTo(-18, -10, -22.5, -10.5); ctx.lineTo(-24, -12); ctx.quadraticCurveTo(-22, -13, -22, -16); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#f2ece0"; ctx.fillRect(-22, -22, 5, 2);
    ctx.restore();
  }
  // Three owlets, white fluff and big dark eyes, peeking over the rim. They
  // bob, and when the parent's there they bob faster with their beaks open.
  function drawOwlets(t, fed) {
    ctx.save(); ctx.translate(BX, BY); ctx.rotate(-.06); ctx.scale(S, S);
    for (const b of owlets) {
      const bob = calm ? 0 : Math.sin(t * (fed ? 7 : 1.6) + b.ph) * (fed ? 2.6 : 1.4);
      const x = b.dx, y = -28 + bob;
      ctx.fillStyle = "#ece6da"; ctx.beginPath(); ctx.ellipse(x, y, 6.2, 6.8, 0, 0, TAU); ctx.fill();
      ctx.fillStyle = "rgba(255, 255, 255, .8)";
      for (const [fx, fy] of [[-3, -4], [2, -5], [4, -1], [-4, 1]]) { ctx.beginPath(); ctx.arc(x + fx, y + fy, 1.1, 0, TAU); ctx.fill(); }
      for (const ex of [-2.4, 2.4]) {
        ctx.fillStyle = "#1a140e"; ctx.beginPath(); ctx.arc(x + ex, y - .6, 1.9, 0, TAU); ctx.fill();
        ctx.fillStyle = "#fff"; ctx.beginPath(); ctx.arc(x + ex + .5, y - 1.2, .5, 0, TAU); ctx.fill();
        if (b.blink > 0) { ctx.fillStyle = "#ece6da"; ctx.beginPath(); ctx.arc(x + ex, y - .6, 2.2, 0, TAU); ctx.fill(); }
      }
      ctx.fillStyle = fed ? "#e86a5a" : "#6b5a44";
      ctx.beginPath(); ctx.moveTo(x - 1.2, y + 1.6); ctx.lineTo(x + 1.2, y + 1.6); ctx.lineTo(x, y + (fed ? 4.4 : 3.4)); ctx.closePath(); ctx.fill();
    }
    ctx.restore();
  }
  function drawBigOwl(atX, atY) {
    const o = bigOwl, hx = o.yaw * 3;
    ctx.save(); ctx.translate(atX, atY); ctx.scale(S, S);
    ctx.fillStyle = "#c9a13a"; ctx.fillRect(-5, -2, 3, 3); ctx.fillRect(2, -2, 3, 3);
    ctx.fillStyle = "#7a5634"; ctx.beginPath(); ctx.ellipse(0, -15, 11, 15, 0, 0, TAU); ctx.fill();
    ctx.fillStyle = "#c7a57a"; ctx.beginPath(); ctx.ellipse(0, -12, 7, 10, 0, 0, TAU); ctx.fill();
    ctx.strokeStyle = "rgba(90, 60, 30, .55)"; ctx.lineWidth = .8;
    for (let k = 0; k < 5; k++) { ctx.beginPath(); ctx.moveTo(-5, -18 + k * 3); ctx.lineTo(5, -18 + k * 3); ctx.stroke(); }
    ctx.fillStyle = "#6a4a2c";
    ctx.beginPath(); ctx.ellipse(-9, -14, 4, 11, .2, 0, TAU); ctx.fill();
    ctx.beginPath(); ctx.ellipse(9, -14, 4, 11, -.2, 0, TAU); ctx.fill();
    // Head, turned by the yaw: the face slides the way it's looking.
    ctx.fillStyle = "#7a5634"; ctx.beginPath(); ctx.ellipse(hx * .4, -33, 10.5, 9, 0, 0, TAU); ctx.fill();
    ctx.beginPath(); ctx.moveTo(hx * .4 - 9, -38); ctx.lineTo(hx * .4 - 10, -47); ctx.lineTo(hx * .4 - 4, -40); ctx.closePath(); ctx.fill();
    ctx.beginPath(); ctx.moveTo(hx * .4 + 9, -38); ctx.lineTo(hx * .4 + 10, -47); ctx.lineTo(hx * .4 + 4, -40); ctx.closePath(); ctx.fill();
    ctx.fillStyle = "#d8b98a"; ctx.beginPath(); ctx.ellipse(hx, -33, 8, 6.5, 0, 0, TAU); ctx.fill();
    const er = o.wide > 0 ? 3.2 : 2.7;
    for (const ex of [-3.7, 3.7]) {
      ctx.fillStyle = "#f7b733"; ctx.beginPath(); ctx.arc(hx + ex, -34, er, 0, TAU); ctx.fill();
      ctx.fillStyle = "#111"; ctx.beginPath(); ctx.arc(hx + ex + o.yaw * .9, -34, er * (o.wide > 0 ? .62 : .48), 0, TAU); ctx.fill();
      if (o.blink > 0) { ctx.fillStyle = "#7a5634"; ctx.beginPath(); ctx.arc(hx + ex, -34, er + .4, 0, TAU); ctx.fill(); }
    }
    ctx.fillStyle = "#3a2a1a"; ctx.beginPath(); ctx.moveTo(hx - 1.3, -32); ctx.lineTo(hx + 1.3, -32); ctx.lineTo(hx, -28.5); ctx.closePath(); ctx.fill();
    ctx.restore();
  }
  // The snake: wound twice round its branch, the rest of it hanging and
  // swaying, head at the bottom and tongue flicking.
  function drawSnake(t) {
    const seg = 3.2 * S, n = 15, pts = [];
    for (let i = 0; i <= n; i++) {
      const u = i / n, sway = calm ? 0 : Math.sin(t * .9 + i * .28) * u * 10 * S;
      pts.push([SX + sway, SY + i * seg]);
    }
    ctx.lineCap = "round"; ctx.lineJoin = "round";
    // The coils round the branch.
    for (const dx of [-26, -13]) {
      ctx.strokeStyle = "#5f9a2e"; ctx.lineWidth = 5 * S;
      ctx.beginPath(); ctx.ellipse(SX + dx * S, SY - 1 * S, 6 * S, 5 * S, 0, 0, TAU); ctx.stroke();
    }
    ctx.strokeStyle = "#5f9a2e"; ctx.lineWidth = 5 * S;
    ctx.beginPath(); ctx.moveTo(SX - 34 * S, SY - 3 * S); ctx.lineTo(SX - 42 * S, SY - 2 * S); ctx.stroke();
    // The hanging part, tapering, with a paler belly line and dark bands.
    for (let i = 0; i < n; i++) {
      ctx.strokeStyle = "#6aa832"; ctx.lineWidth = (5 - i * .09) * S;
      ctx.beginPath(); ctx.moveTo(pts[i][0], pts[i][1]); ctx.lineTo(pts[i + 1][0], pts[i + 1][1]); ctx.stroke();
      if (i % 3 === 1) { ctx.strokeStyle = "#3f6e20"; ctx.lineWidth = (4.4 - i * .08) * S; ctx.beginPath(); ctx.moveTo(pts[i][0], pts[i][1]); ctx.lineTo((pts[i][0] + pts[i + 1][0]) / 2, (pts[i][1] + pts[i + 1][1]) / 2); ctx.stroke(); }
    }
    ctx.strokeStyle = "rgba(220, 214, 122, .7)"; ctx.lineWidth = 1.2 * S;
    ctx.beginPath(); pts.forEach(([x, y], i) => i ? ctx.lineTo(x + 1.2 * S, y) : ctx.moveTo(x + 1.2 * S, y)); ctx.stroke();
    const [hx, hy] = pts[n];
    ctx.fillStyle = "#5f9a2e"; ctx.beginPath(); ctx.ellipse(hx, hy + 4 * S, 3.6 * S, 5 * S, 0, 0, TAU); ctx.fill();
    ctx.fillStyle = "#f2d64b"; ctx.beginPath(); ctx.arc(hx - 1.6 * S, hy + 3 * S, 1 * S, 0, TAU); ctx.arc(hx + 1.6 * S, hy + 3 * S, 1 * S, 0, TAU); ctx.fill();
    if (snake.flick > 0) {
      ctx.strokeStyle = "#d8322a"; ctx.lineWidth = .9 * S;
      ctx.beginPath(); ctx.moveTo(hx, hy + 9 * S); ctx.lineTo(hx, hy + 13 * S); ctx.lineTo(hx - 1.6 * S, hy + 15 * S);
      ctx.moveTo(hx, hy + 13 * S); ctx.lineTo(hx + 1.6 * S, hy + 15 * S); ctx.stroke();
    }
  }

  function drawOwl() {
    const s = TW / 16, r = (2.3 + (owl.wide > 0 ? .9 : 0)) * s;
    fr.fillStyle = "#6b4d33";
    fr.beginPath(); fr.ellipse(LX, OWL_Y + s, TW * .32, TW * .45, 0, 0, TAU); fr.fill();
    for (const dx of [-2.9, 2.9]) {
      const ex = LX + dx * s, ey = OWL_Y - 1.5 * s;
      fr.fillStyle = "#f2c443"; fr.beginPath(); fr.arc(ex, ey, r, 0, TAU); fr.fill();
      fr.fillStyle = "#111"; fr.beginPath(); fr.arc(ex, ey, r * (owl.wide > 0 ? .62 : .5), 0, TAU); fr.fill();
      if (owl.blink > 0) { fr.fillStyle = "#6b4d33"; fr.fillRect(ex - r - .5, ey - r - .5, r * 2 + 1, r * 2 + 1); }
    }
    fr.fillStyle = "#d8a23a";
    fr.beginPath(); fr.moveTo(LX - 1.2 * s, OWL_Y + 1.5 * s); fr.lineTo(LX + 1.2 * s, OWL_Y + 1.5 * s); fr.lineTo(LX, OWL_Y + 4.2 * s); fr.closePath(); fr.fill();
    fr.fillStyle = "#5a3f29";
    fr.beginPath(); fr.moveTo(LX - 4.5 * s, OWL_Y - 5 * s); fr.lineTo(LX - 5.5 * s, OWL_Y - 9 * s); fr.lineTo(LX - 2 * s, OWL_Y - 5.5 * s); fr.closePath(); fr.fill();
    fr.beginPath(); fr.moveTo(LX + 4.5 * s, OWL_Y - 5 * s); fr.lineTo(LX + 5.5 * s, OWL_Y - 9 * s); fr.lineTo(LX + 2 * s, OWL_Y - 5.5 * s); fr.closePath(); fr.fill();
  }

  function draw(now) {
    const t = (now || 0) / 1000;
    // Behind the page.
    ctx.drawImage(land, 0, 0, W, H);
    ctx.save();
    ctx.globalCompositeOperation = "lighter";
    for (const sh of shafts) {
      const a = calm ? .045 : .03 + .03 * (.5 + .5 * Math.sin(t / 5 + sh.ph));
      ctx.fillStyle = `rgba(255, 226, 160, ${a.toFixed(3)})`;
      ctx.beginPath(); ctx.moveTo(sh.x, -10); ctx.lineTo(sh.x + sh.w, -10); ctx.lineTo(sh.x + sh.w - H * .6, H); ctx.lineTo(sh.x - H * .6, H); ctx.closePath(); ctx.fill();
    }
    ctx.restore();
    for (const m of mist) {
      ctx.save(); ctx.translate(m.x, m.y); ctx.scale(1, m.ry / m.rx);
      const g = ctx.createRadialGradient(0, 0, 0, 0, 0, m.rx);
      g.addColorStop(0, "rgba(220, 232, 222, .16)"); g.addColorStop(1, "rgba(220, 232, 222, 0)");
      ctx.fillStyle = g; ctx.beginPath(); ctx.arc(0, 0, m.rx, 0, TAU); ctx.fill();
      ctx.restore();
    }
    for (const f of flocks) for (const b of f.birds) skyBird(f.x + b.dx, f.y + b.dy, S * .9, Math.sin(t * 9 + b.ph));
    for (const b of startled) skyBird(b.x, b.y, S, Math.sin(t * 16 + b.ph));
    // The nest and who's in it; the eagle aloft if it's out.
    drawNest(false);
    if (eagle.mode === "nest") eagleSitting(NX + 4 * S, NY, eagle.dir, S); else drawEaglet(t);
    drawNest(true);
    if (eagle.mode !== "nest") eagleFlying(eagle.x, eagle.y, S * 1.05, eagle.ang, eagle.flap > 0 ? Math.sin(t * 10) : 0);
    drawSnake(t);
    // The laundry basket with its owlets, and the owl wherever it is.
    drawBasket(false);
    drawOwlets(t, bigOwl.mode === "nest");
    drawBasket(true);
    if (bigOwl.mode === "perch" || bigOwl.mode === "nest") drawBigOwl(bigOwl.x, bigOwl.y);
    else drawOwlFlying(bigOwl.x, bigOwl.y, bigOwl.to[0] < bigOwl.from[0] ? -1 : 1, calm ? 0 : Math.sin(t * 13));
    for (const l of leaves) {
      ctx.save(); ctx.translate(l.x + Math.sin(t + l.ph) * l.sway, l.y); ctx.rotate(l.r);
      ctx.fillStyle = l.col; ctx.beginPath(); ctx.ellipse(0, 0, l.sz, l.sz * .5, 0, 0, TAU); ctx.fill();
      ctx.restore();
    }
    // In front of the page.
    fr.clearRect(0, 0, W, H);
    fr.drawImage(still, 0, 0, W, H);
    drawOwl();
    woodpecker(fr, LX + TW / 2 + 3.5 * S, WP_Y, S, wp.burst > 0 ? Math.abs(Math.sin(t * 38)) : 0);
    for (const ch of chips) { fr.fillStyle = `rgba(150, 110, 70, ${Math.max(0, 1 - ch.t / .8).toFixed(2)})`; fr.fillRect(ch.x, ch.y, 1.8, 1.8); }
    fr.save();
    fr.translate(RX - TW / 2 - 4 * S, sq.y);
    fr.rotate(sq.dir < 0 ? -Math.PI / 2 : Math.PI / 2);
    if (sq.dir > 0) fr.scale(1, -1);
    fr.scale(S, S);
    squirrel(fr, sq.pause > 0 ? t * 3 : sq.ph);
    fr.restore();
    for (const p of perches) {
      const b = p.bird;
      if (!b) continue;
      const hopY = b.hop > 0 ? Math.sin(Math.PI * b.hop / .3) * 4 * S : 0;
      songbird(fr, p.x, p.y - 1.4 * S - hopY, b.dir, S, 0, b.kind, true, b.flick);
    }
    for (const f of flyers) {
      const u = Math.min(1, f.t / f.dur);
      let x, y, dir;
      if (f.to) { x = f.x0 + (f.to.x - f.x0) * u; y = f.y0 + (f.to.y - 1.4 * S - f.y0) * u - Math.sin(Math.PI * u) * 30 * S; dir = f.to.x >= f.x0 ? 1 : -1; }
      else { const e = u * u; x = f.x0 + (f.x1 - f.x0) * e; y = f.y0 + (f.y1 - f.y0) * u; dir = f.x1 >= f.x0 ? 1 : -1; }
      songbird(fr, x, y, dir, S, Math.sin(f.t * 28), f.kind, false, 0);
    }
    fr.font = `${Math.round(12 * S)}px serif`;
    fr.textAlign = "center";
    for (const n of notes) { fr.fillStyle = `rgba(250, 238, 200, ${Math.max(0, 1 - n.t / 1.8).toFixed(2)})`; fr.fillText(n.g, n.x, n.y); }
    if (bunny) {
      const jump = bunny.pause > 0 ? 0 : Math.sin(Math.PI * bunny.hopT / .42) * 9 * S;
      rabbit(fr, bunny.x, GY - jump, bunny.dir, S, bunny.up);
    }
    if (walker) {
      if (walker.kind === "deer") deer(fr, walker.x, GY, walker.dir, walker.sc, walker.ph, walker.head, walker.antlers);
      else fox(fr, walker.x, GY, walker.dir, walker.sc, walker.ph, walker.head);
    }
    drawBears(now || 0);
    if (bfly) {
      const w = Math.abs(Math.sin(bfly.t * 9)) * 6 + 2.5;
      fr.save(); fr.translate(bfly.x, bfly.y); fr.scale(S, S);
      fr.fillStyle = bfly.col;
      fr.beginPath(); fr.ellipse(-w * .6, -2, w, 5, -.4, 0, TAU); fr.fill();
      fr.beginPath(); fr.ellipse(w * .6, -2, w, 5, .4, 0, TAU); fr.fill();
      fr.fillStyle = "#3b2a1e"; fr.beginPath(); fr.ellipse(0, 0, 1.6, 5.5, 0, 0, TAU); fr.fill();
      fr.restore();
    }
  }

  // The alarm's red along the floor; the bears, and their tags over them.
  function drawBears(now) {
    const red = redFrom && now - redFrom < 6500 ? 1 - (now - redFrom) / 6500 : 0;
    if (red > 0) { fr.fillStyle = `rgba(255, 50, 35, ${((.12 + .12 * Math.sin((now - redFrom) / 240)) * red).toFixed(3)})`; fr.fillRect(0, FY - 30 * S, W, H - FY + 30 * S); }
    const list = inClearing();
    list.forEach((b, i) => { if (b.x == null) b.x = bearX(Math.min(i, 1)); });
    let n = 0;
    for (const b of bears.values()) {
      if (b.x == null || (b.state !== "out" && list.indexOf(b) > 1)) continue;
      bear(fr, b.x, GY, b.dir, S * 1.1, b.ph, b.sniff || 0);
      const state = b.clearAt ? "cleared" : b.alarm && now - b.alarm < 8000 ? "alarm" : "held";
      const k = b.clearAt ? Math.max(0, 1 - Math.max(0, now - b.clearAt - 1800) / 1200) : 1;
      if (n < 2) intruderTag(fr, b.x, GY - 50 * S - n * 46 * S, b.h, { s: .85 * S, state, k });
      n++;
    }
  }
  function syncIntruders() {
    const { held, added, cleared } = watchIn();
    const now = performance.now();
    for (const h of added) if (!bears.has(h.id)) bears.set(h.id, { h, x: null, dir: -1, ph: 0, sniff: 0, state: "held", alarm: 0, clearAt: 0 });
    for (const h of held) { const b = bears.get(h.id); if (b) b.h = h; }
    for (const h of cleared) {
      const b = bears.get(h.id); if (!b || b.state === "out") continue;
      if (calm || document.hidden) { bears.delete(h.id); continue; }
      Object.assign(b, { state: "out", clearAt: now });
    }
    room.style.height = `${BAND + (inClearing().length ? 110 : 0)}px`;
  }
  // The alarm: every bird up, calls going, the owl's eyes wide.
  festiveHooks.intruder = h => {
    if (!h) return;
    syncIntruders();
    let b = bears.get(h.id);
    if (!b) { b = { h, x: null, dir: -1, ph: 0, sniff: 0, state: "held", alarm: 0, clearAt: 0 }; bears.set(h.id, b); }
    room.style.height = `${BAND + 110}px`;
    if (calm) { draw(0); return; }
    if (document.hidden) return;
    const now = performance.now();
    Object.assign(b, { x: W + 90 * S, state: "in", alarm: now, clearAt: 0 });
    redFrom = now;
    startle();
    for (const p of perches) {
      if (!p.bird) continue;
      startled.push({ x: p.x, y: p.y, vx: rnd(-180, 180) * S, vy: rnd(-200, -90) * S, ph: rnd(0, TAU) });
      notes.push({ x: p.x, y: p.y - 6 * S, dir: 1, t: 0, g: "!" });
      p.bird = null;
    }
    bigOwl.wide = 3; owl.wide = 3; bigOwl.pAt = 0;
  };
  retarget();
  syncIntruders();
  if (calm) {
    // Reduced motion: the wood holding still. A deer grazing, a rabbit sat up,
    // a flock caught mid-sky, and the vine with its birds - which still shows
    // how much of the network is up, redrawn as that changes but never moving.
    walker = { kind: "deer", dir: -1, x: W * .62, state: "graze", ph: 0, head: 1, antlers: true, sc: S * .95 };
    bunny = { dir: 1, x: W * .33, hopT: 0, hops: 0, pause: 1, up: 1, sitting: true };
    sq.pause = 1;
    flocks.push({ x: W * .42, y: H * .12, v: 0, birds: [0, 1, 2, 3, 4].map(i => ({ dx: -Math.ceil(i / 2) * 13 * S, dy: (i % 2 ? 1 : -1) * Math.ceil(i / 2) * 6 * S, ph: i })) });
    draw(0);
    festiveHooks.rendered = () => { retarget(); settle(performance.now()); syncIntruders(); draw(0); };
    return;
  }
  const t0 = performance.now();
  flockAt = t0 + rnd(1500, 5000); walkerAt = t0 + rnd(8e3, 20e3);
  eagle.at = t0 + rnd(15e3, 35e3); eagle.turnAt = t0 + rnd(2e3, 5e3);
  bigOwl.blinkAt = t0 + rnd(1000, 3000); snake.flickAt = t0 + rnd(1500, 4000);
  bigOwl.at = t0 + rnd(12e3, 25e3);
  // The owl watches the pointer. Only a note of where it is: the drawing is
  // done in the frame loop, which stops with the tab.
  const watch = ev => { bigOwl.px = ev.clientX; bigOwl.pAt = performance.now(); };
  addEventListener("pointermove", watch, { passive: true });
  festiveStops.push(() => removeEventListener("pointermove", watch));
  bunnyAt = t0 + rnd(4e3, 12e3); bflyAt = t0 + rnd(8e3, 18e3); wp.at = t0 + rnd(1500, 4000); owl.blinkAt = t0 + rnd(1000, 4000);
  c.start();
  festiveHooks.rendered = () => { retarget(); syncIntruders(); };
  festiveHooks.scanDone = () => {
    if (document.hidden) return;
    startle();
    // The owl's eyes go wide and it turns to see what's taken off.
    bigOwl.wide = 2.6; bigOwl.want = startled.length && startled[0].vx < 0 ? 1 : -1; bigOwl.lookAt = performance.now() + 3000; bigOwl.pAt = 0;
  };

}

BAMF.registerTheme("woodlands", ctx => buildWoodlands(ctx.root, ctx.switched));
})();
