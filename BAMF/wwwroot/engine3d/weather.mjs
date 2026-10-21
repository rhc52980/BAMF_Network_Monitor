// The weather over the 3D scene, from the network's score: high cloud, rain, lightning and a colder light as the score falls, and a flash
// when an alert arrives. What the score means for the sky is in data.mjs (weatherOf); this draws it. It touches the shared sky's tint and
// the scene's fog, so it puts both back when it is switched off, and never runs in the replay.
import * as THREE from "three";
import { SKIN } from "./fx.mjs";

const RAIN_STREAKS = 520;
const BOX = 34;                 // the rain falls in a box this wide round the point being looked at
const TOP = 26;

export function createWeather({ scene, glow, controls, flashEl, reduced = false }) {
  const fogDefault = { density: scene.fog.density, color: scene.fog.color.clone() };
  const want = { tint: new THREE.Color(1, 1, 1), fog: fogDefault.density, cloud: 0, rain: 0, storm: 0 };
  let on = false, lite = false, nextBolt = 6, bolt = null, boltBorn = 0;
  const cur = { tint: new THREE.Color(1, 1, 1), fog: fogDefault.density, cloud: 0, rain: 0, storm: 0 };

  // High, dark cloud: a few big soft shapes drifting across, in front of the stars and behind everything else.
  const clouds = [];
  for (let i = 0; i < 9; i++) {
    const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: glow, color: new THREE.Color(0.09, 0.12, 0.2), transparent: true, opacity: 0, depthWrite: false, fog: false }));
    const w = 38 + (i % 3) * 16;
    s.scale.set(w, w * 0.34, 1);
    s.position.set((i - 4) * 17 + (i % 2) * 6, 20 + (i % 4) * 2.6, -26 - (i % 3) * 14);
    s.userData = { speed: 0.35 + (i % 4) * 0.15, base: s.position.x };
    scene.add(s); clouds.push(s);
  }

  // Rain: streaks that fall slanting in the wind, in a box that follows where the camera looks.
  const rainPos = new Float32Array(RAIN_STREAKS * 6);
  const drops = Array.from({ length: RAIN_STREAKS }, () => ({ x: (Math.random() - 0.5) * BOX, y: Math.random() * TOP, z: (Math.random() - 0.5) * BOX, v: 16 + Math.random() * 12 }));
  const rainGeo = new THREE.BufferGeometry();
  rainGeo.setAttribute("position", new THREE.BufferAttribute(rainPos, 3));
  const rain = new THREE.LineSegments(rainGeo, new THREE.LineBasicMaterial({ color: new THREE.Color(0.45, 0.72, 1.0), transparent: true, opacity: 0, blending: THREE.AdditiveBlending, depthWrite: false, fog: false }));
  rain.frustumCulled = false; rain.visible = false; scene.add(rain);

  function makeBolt() {
    const pts = []; let x = 0, z = 0;
    for (let y = 44; y > 0; y -= 4 + Math.random() * 4) { pts.push(new THREE.Vector3(x, y, z)); x += (Math.random() - 0.5) * 6; z += (Math.random() - 0.5) * 3; }
    const geo = new THREE.BufferGeometry().setFromPoints(pts.flatMap((p, i) => (i ? [pts[i - 1], p] : [])));
    const l = new THREE.LineSegments(geo, new THREE.LineBasicMaterial({ color: new THREE.Color(0.8, 0.85, 1).multiplyScalar(2.4), transparent: true, opacity: 1, blending: THREE.AdditiveBlending, depthWrite: false, fog: false }));
    l.frustumCulled = false; return l;
  }
  function strike(big) {
    if (bolt) { scene.remove(bolt); bolt.geometry.dispose(); bolt.material.dispose(); }
    bolt = makeBolt();
    const t = controls.target, a = Math.random() * 6.283, r = 22 + Math.random() * 20;
    bolt.position.set(t.x + Math.cos(a) * r, -1, t.z + Math.sin(a) * r - 10);
    scene.add(bolt); boltBorn = performance.now();
    if (flashEl) { flashEl.style.transition = "none"; flashEl.style.opacity = big ? "0.7" : "0.42"; void flashEl.offsetWidth; flashEl.style.transition = "opacity 650ms ease-out"; flashEl.style.opacity = "0"; }
  }

  function set(w, enabled, isLite = false) {
    on = !!enabled && !!w; lite = isLite;
    if (!on) { want.tint.setRGB(1, 1, 1); want.fog = fogDefault.density; want.cloud = 0; want.rain = 0; want.storm = 0; return; }
    want.tint.setRGB(w.tint[0], w.tint[1], w.tint[2]); want.fog = w.fog; want.cloud = w.cloud; want.rain = w.rain; want.storm = w.storm;
  }

  /** A flash and a bolt, for an alert arriving. Quiet when the browser is asked for less motion. */
  function flash() { if (on && !reduced) strike(true); }

  function tick(time, dt) {
    const k = 1 - Math.exp(-1.6 * dt);
    cur.tint.lerp(want.tint, k); cur.fog += (want.fog - cur.fog) * k;
    for (const key of ["cloud", "rain", "storm"]) cur[key] += (want[key] - cur[key]) * k;
    // The shared sky is tinted for this scene; the saver resets it when it is on, and it is set again here when this scene is.
    SKIN.tint.value.copy(cur.tint);
    scene.fog.density = cur.fog;
    scene.fog.color.copy(fogDefault.color).multiply(cur.tint);
    const t = controls.target;
    for (const c of clouds) {
      c.material.opacity = Math.min(0.62, cur.cloud * 0.62);
      c.visible = c.material.opacity > 0.01;
      if (!reduced) { c.position.x += c.userData.speed * dt; if (c.position.x > 90) c.position.x = -90; }
    }
    const showRain = cur.rain > 0.02 && !reduced;
    rain.visible = showRain;
    if (showRain) {
      const n = Math.round(RAIN_STREAKS * Math.min(1, cur.rain) * (lite ? 0.5 : 1));
      rain.material.opacity = 0.1 + cur.rain * 0.3;
      const wind = 3 + Math.sin(time * 0.3) * 1.5 + cur.storm * 5;
      for (let i = 0; i < n; i++) {
        const d = drops[i];
        d.y -= d.v * dt; d.x += wind * dt;
        if (d.y < -1.5) { d.y = TOP; d.x = (Math.random() - 0.5) * BOX; d.z = (Math.random() - 0.5) * BOX; }
        if (d.x > BOX / 2) d.x -= BOX;
        const x = t.x + d.x, z = t.z + d.z, o = i * 6;
        rainPos[o] = x; rainPos[o + 1] = d.y; rainPos[o + 2] = z;
        rainPos[o + 3] = x + wind * 0.035; rainPos[o + 4] = d.y + 0.7 + cur.rain * 0.4; rainPos[o + 5] = z;
      }
      rainGeo.setDrawRange(0, n * 2);
      rainGeo.attributes.position.needsUpdate = true;
    }
    // lightning, in a storm: now and then, more often the worse it is; the bolt is gone in a moment
    if (cur.storm > 0.05 && !reduced) {
      nextBolt -= dt;
      if (nextBolt <= 0) { strike(false); nextBolt = (10 - cur.storm * 7) * (0.5 + Math.random()); }
    }
    if (bolt) {
      const age = (performance.now() - boltBorn) / 1000;
      bolt.material.opacity = age < 0.08 ? 1 : age < 0.14 ? 0.15 : age < 0.2 ? 0.9 : 0;
      if (age > 0.25) { scene.remove(bolt); bolt.geometry.dispose(); bolt.material.dispose(); bolt = null; }
    }
  }

  function dispose() {
    SKIN.tint.value.setRGB(1, 1, 1);
    scene.fog.density = fogDefault.density; scene.fog.color.copy(fogDefault.color);
    for (const c of clouds) { scene.remove(c); c.material.dispose(); }
    scene.remove(rain); rainGeo.dispose(); rain.material.dispose();
    if (bolt) { scene.remove(bolt); bolt.geometry.dispose(); bolt.material.dispose(); }
  }

  return { set, tick, flash, dispose, get on() { return on; }, get state() { return { ...cur, tint: cur.tint.toArray() }; } };
}
