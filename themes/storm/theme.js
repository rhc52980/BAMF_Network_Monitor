// Thunderstorm: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// The storm's sound, made in the browser like the goats' bleats: rain as
// filtered noise, and thunder as a crack and a long low rumble, softer and
// later the further away the strike.
let stormAudio = null;

function stormSound() {
  try { stormAudio = stormAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return null; }
  const ctx = stormAudio;
  const noise = (secs, brown) => {
    const buf = ctx.createBuffer(1, Math.floor(ctx.sampleRate * secs), ctx.sampleRate), d = buf.getChannelData(0);
    let last = 0;
    for (let i = 0; i < d.length; i++) {
      const w = Math.random() * 2 - 1;
      if (brown) { last = (last + .02 * w) / 1.02; d[i] = last * 3.5; } else d[i] = w;
    }
    return buf;
  };
  const white = noise(2, false), brown = noise(6, true);
  const master = ctx.createGain();
  master.gain.value = 0;
  master.connect(ctx.destination);
  // Rain: looping noise, band-limited to a hiss, loudness set by the storm.
  const rain = ctx.createBufferSource();
  rain.buffer = white; rain.loop = true;
  const hp = ctx.createBiquadFilter(), lp = ctx.createBiquadFilter(), rainGain = ctx.createGain();
  hp.type = "highpass"; hp.frequency.value = 400;
  lp.type = "lowpass"; lp.frequency.value = 5200;
  rainGain.gain.value = .05;
  rain.connect(hp).connect(lp).connect(rainGain).connect(master);
  rain.start();
  return {
    ctx,
    on(yes) {
      if (yes && ctx.state === "suspended") ctx.resume?.().catch(() => {});
      master.gain.setTargetAtTime(yes ? 1 : 0, ctx.currentTime, .4);
    },
    rain(level) { rainGain.gain.setTargetAtTime(.03 + .09 * level, ctx.currentTime, 1.5); },
    thunder(near) {
      // near: 1 overhead, 0 far off.
      const t = ctx.currentTime + .02;
      if (near > .55) {
        const crack = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
        crack.buffer = white;
        f.type = "highpass"; f.frequency.value = 900;
        g.gain.setValueAtTime(0, t);
        g.gain.linearRampToValueAtTime(.55 * near, t + .01);
        g.gain.exponentialRampToValueAtTime(.001, t + .45);
        crack.connect(f).connect(g).connect(master);
        crack.start(t, Math.random()); crack.stop(t + .5);
      }
      const len = 3.5 + 3 * Math.random();
      const rum = ctx.createBufferSource(), f = ctx.createBiquadFilter(), g = ctx.createGain();
      rum.buffer = brown;
      f.type = "lowpass"; f.frequency.value = 90 + 260 * near;
      const peak = .35 + .6 * near, at = t + .08 + (1 - near) * .5;
      g.gain.setValueAtTime(0, t);
      g.gain.linearRampToValueAtTime(peak, at);
      // It rolls: a few swells as it dies away.
      for (let k = 1; k <= 4; k++) g.gain.linearRampToValueAtTime(peak * (k % 2 ? .45 : .8) / k, at + len * k / 5);
      g.gain.linearRampToValueAtTime(0, t + len);
      rum.connect(f).connect(g).connect(master);
      rum.start(t, Math.random() * 2); rum.stop(t + len + .1);
    },
    stop() { try { rain.stop(); } catch {} master.disconnect(); },
  };
}

// The Map for the Thunderstorm: an offline device drips.
function stormMapDeco(g, n) {
  if (n.type === "dev") g.insertBefore(svgEl("path", { class: "t-drip", d: "M0 17c-2 3-3 4.5-3 6a3 3 0 0 0 6 0c0-1.5-1-3-3-6z" }), g.querySelector(".t-watch"));
}

function buildStorm(root) {
  festiveHooks.decorateNode = stormMapDeco;
  const calm = calmMotion();
  // Sound, if it's switched on: the speaker button beside the theme button.
  let snd = null;
  const soundOn = themeSoundButton("bamf-storm-sound", "Storm sounds on: click to mute",
    "Storm sounds off: click for rain and thunder", on => {
      if (on && !snd) snd = stormSound();
      snd?.on(on);
      if (on) snd?.rain(intensity);
    });
  if (soundOn()) {
    snd = stormSound();
    // Browsers only allow sound after a click on the page.
    const wake = () => snd?.on(soundOn());
    document.addEventListener("pointerdown", wake, { once: true });
    festiveStops.push(() => document.removeEventListener("pointerdown", wake));
    snd?.on(true);
  }
  // Nothing plays in a background tab.
  const vis = () => { if (!snd) return; if (document.hidden) snd.ctx.suspend?.().catch(() => {}); else if (soundOn()) snd.ctx.resume?.().catch(() => {}); };
  document.addEventListener("visibilitychange", vis);
  festiveStops.push(() => { document.removeEventListener("visibilitychange", vis); snd?.stop(); snd = null; });
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg st-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = `<div class="st-flash"></div><canvas></canvas>
    <div class="st-cloud" style="top:-40px;left:-10vw;width:48vw;animation-duration:140s"></div>
    <div class="st-cloud" style="top:-70px;left:40vw;width:60vw;animation-duration:190s;animation-delay:-60s"></div>
    <div class="st-cloud" style="top:-30px;left:75vw;width:40vw;animation-duration:160s;animation-delay:-110s"></div>`;
  document.body.prepend(bg);
  const cv = bg.querySelector("canvas"), flash = bg.querySelector(".st-flash");
  const ctx = cv.getContext("2d");
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);

  // How hard it's raining, 0.35 to 1: devices that went offline this session
  // and haven't come back push it up.
  const down = new Set();
  var intensity = .4;
  snd?.rain(intensity);
  const MAX = Math.round(Math.min(520, W * H / 2600));
  const drop = () => ({ x: rnd(-W * .2, W * 1.1), y: rnd(-H, 0), len: rnd(10, 22), v: rnd(11, 17) });
  const drops = Array.from({ length: MAX }, drop);
  let wind = -1.5, windTo = -1.5;
  const splashes = [];
  let bolt = null;

  const drawRain = n => {
    ctx.strokeStyle = "rgba(175, 205, 235, .38)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
      const d = drops[i];
      ctx.moveTo(d.x, d.y);
      ctx.lineTo(d.x + wind * d.len / d.v * 1.6, d.y + d.len);
    }
    ctx.stroke();
  };

  if (calm) {
    // Reduced motion: still streaks, no lightning.
    drops.forEach(d => { d.y = rnd(0, H); });
    drawRain(Math.round(MAX * .5));
    return;
  }

  const jag = (x, y, x2, y2, rough) => {
    const pts = [[x, y]];
    const steps = 10 + Math.floor(Math.random() * 6);
    for (let i = 1; i < steps; i++) {
      const t = i / steps;
      pts.push([x + (x2 - x) * t + rnd(-rough, rough), y + (y2 - y) * t + rnd(-rough * .3, rough * .3)]);
    }
    pts.push([x2, y2]);
    return pts;
  };
  const strike = (atX) => {
    if (document.hidden) return;
    const x = atX ?? rnd(W * .08, W * .92), end = rnd(H * .35, H * .7);
    const main = jag(x, -10, x + rnd(-120, 120), end, 34);
    const forks = [];
    for (let f = 0; f < 1 + Math.floor(Math.random() * 2); f++) {
      const from = main[3 + Math.floor(Math.random() * (main.length - 6))];
      forks.push(jag(from[0], from[1], from[0] + rnd(-160, 160), from[1] + rnd(60, 180), 22));
    }
    bolt = { paths: [main, ...forks], born: performance.now() };
    // One soft brightening of the sky, never a white screen.
    flash.classList.remove("on"); void flash.offsetWidth; flash.classList.add("on");
    // Thunder follows, later the further away it struck.
    const delay = rnd(700, 2600);
    festiveTimers.push(setTimeout(() => {
      if (soundOn() && !document.hidden) snd?.thunder(1 - (delay - 700) / 1900);
      const hd = document.querySelector("header");
      if (!hd) return;
      hd.classList.remove("st-rumble"); void hd.offsetWidth; hd.classList.add("st-rumble");
    }, delay));
  };
  // Lightning every 20-60 s in a light storm, more often in a heavy one; never under 20 s.
  const nextStrike = () => festiveTimers.push(setTimeout(() => { strike(); nextStrike(); },
    Math.max(20e3, rnd(20e3, 60e3) * (1.45 - intensity))));
  nextStrike();
  // Gusts swing the rain's angle.
  festiveTimers.push(setInterval(() => { windTo = rnd(-4.5, 1.5); }, 6500));

  let raf = 0, last = 0;
  const frame = now => {
    raf = requestAnimationFrame(frame);
    if (now - last < 33) return;
    last = now;
    ctx.clearRect(0, 0, W, H);
    wind += (windTo - wind) * .02;
    const n = Math.round(MAX * intensity);
    for (let i = 0; i < n; i++) {
      const d = drops[i];
      d.y += d.v; d.x += wind * 1.6;
      if (d.y > H) {
        if (Math.random() < .25) splashes.push({ x: d.x, y: H - rnd(2, 8), t: 0 });
        Object.assign(d, drop(), { y: rnd(-60, 0) });
      }
    }
    drawRain(n);
    // Splashes: two tiny arcs that grow and fade.
    ctx.strokeStyle = "rgba(175, 205, 235, .45)";
    for (let i = splashes.length - 1; i >= 0; i--) {
      const s = splashes[i];
      s.t += 1;
      if (s.t > 7) { splashes.splice(i, 1); continue; }
      ctx.globalAlpha = 1 - s.t / 7;
      ctx.beginPath();
      ctx.arc(s.x - 3, s.y, 1 + s.t * .6, Math.PI * 1.1, Math.PI * 1.6);
      ctx.moveTo(s.x + 3 + 1 + s.t * .6, s.y);
      ctx.arc(s.x + 3, s.y, 1 + s.t * .6, Math.PI * 1.9, Math.PI * 1.4, true);
      ctx.stroke();
      ctx.globalAlpha = 1;
    }
    if (bolt) {
      const age = now - bolt.born;
      if (age > 450) bolt = null;
      else {
        ctx.save();
        ctx.globalAlpha = age < 90 ? 1 : 1 - (age - 90) / 360;
        ctx.shadowColor = "#9fd8ff"; ctx.shadowBlur = 18;
        bolt.paths.forEach((pts, i) => {
          ctx.strokeStyle = i ? "rgba(220, 240, 255, .7)" : "#f2faff";
          ctx.lineWidth = i ? 1.2 : 2.4;
          ctx.beginPath();
          pts.forEach(([x, y], j) => j ? ctx.lineTo(x, y) : ctx.moveTo(x, y));
          ctx.stroke();
        });
        ctx.restore();
      }
    }
  };
  raf = requestAnimationFrame(frame);
  festiveStops.push(() => cancelAnimationFrame(raf));

  // A spark now and then along one of the cables on the Map.
  festiveTimers.push(setInterval(() => {
    if (document.hidden) return;
    const cables = [...document.querySelectorAll(".map-svg .pkt")];
    if (!cables.length) return;
    const c = pick(cables);
    c.classList.remove("st-spark"); void c.getBoundingClientRect(); c.classList.add("st-spark");
    festiveTimers.push(setTimeout(() => c.classList.remove("st-spark"), 1300));
  }, 9000));

  festiveHooks.newDevice = () => strike(rnd(W * .55, W * .9));
  // As devices come back, the storm eases again.
  festiveHooks.netChange = (off, back) => {
    off.forEach(id => down.add(id));
    back.forEach(id => down.delete(id));
    intensity = Math.min(1, .4 + .15 * down.size);
    snd?.rain(intensity);
  };
  // Rows whose device just went offline flicker like a power cut.
  festiveHooks.rendered = () => {
    for (const id of wentOffIds) {
      const tr = document.querySelector(`tr[data-id="${id}"]`);
      if (!tr) continue;
      tr.classList.add("st-flicker");
      festiveTimers.push(setTimeout(() => tr.classList.remove("st-flicker"), 1000));
    }
    wentOffIds = [];
  };
}

BAMF.registerTheme("storm", ctx => buildStorm(ctx.root, ctx.switched));
})();
