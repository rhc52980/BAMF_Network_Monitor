// The 3D view's look: the shaders and little makers that give it its neon-hologram feel. Nothing in here
// knows about devices or networks; scene.mjs puts them to use. All of it is drawn with plain materials, so
// it works anywhere three.js does.
import * as THREE from "three";
import { ShaderPass } from "three/addons/postprocessing/ShaderPass.js";

/** One shared clock; every material below reads it, so only this needs updating each frame. */
export const T = { value: 0 };

// A round soft dot, for particles. Drawn once on a canvas.
export function glowTexture() {
  const c = document.createElement("canvas"); c.width = c.height = 64;
  const g = c.getContext("2d"), grad = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  grad.addColorStop(0, "rgba(255,255,255,1)"); grad.addColorStop(0.25, "rgba(255,255,255,.55)"); grad.addColorStop(1, "rgba(255,255,255,0)");
  g.fillStyle = grad; g.fillRect(0, 0, 64, 64);
  const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; return t;
}

// ---------- the sky ----------
export function makeSky() {
  return new THREE.Mesh(new THREE.SphereGeometry(300, 32, 16), new THREE.ShaderMaterial({
    side: THREE.BackSide, depthWrite: false, fog: false,
    uniforms: { time: T },
    vertexShader: `varying vec3 vd; void main(){ vd = normalize(position); gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `uniform float time; varying vec3 vd;
      float hash(vec3 p){ return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }
      void main(){
        float h = vd.y;
        vec3 top = vec3(0.004, 0.008, 0.024), mid = vec3(0.02, 0.03, 0.09), low = vec3(0.12, 0.03, 0.22);
        vec3 c = mix(low, mid, smoothstep(-0.05, 0.22, h)); c = mix(c, top, smoothstep(0.18, 0.75, h));
        c += vec3(0.0, 0.32, 0.5) * exp(-pow((h - 0.015) * 14.0, 2.0)) * 0.55;
        c = mix(vec3(0.006, 0.01, 0.03), c, smoothstep(-0.1, 0.0, h));          // below the horizon is dark; the floor sits there          // a thin cyan horizon
        vec3 g = floor(vd * 220.0); float s = step(0.9975, hash(g)) * smoothstep(0.05, 0.4, h);   // a few stars
        c += s * (0.5 + 0.5 * sin(time * 2.0 + hash(g) * 40.0));
        gl_FragColor = vec4(c, 1.0);
      }`,
  }));
}

// ---------- the floor: a neon grid that fades into the dark, with pulses running outward ----------
export function makeFloor(y) {
  const m = new THREE.Mesh(new THREE.PlaneGeometry(500, 500), new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false, uniforms: { time: T },
    vertexShader: `varying vec3 vw; void main(){ vec4 w = modelMatrix * vec4(position, 1.0); vw = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`,
    fragmentShader: `uniform float time; varying vec3 vw;
      float grid(vec2 q, float s, float w){ vec2 u = q / s; vec2 fw = fwidth(u); vec2 g = abs(fract(u - 0.5) - 0.5) / fw; float dense = 1.0 - smoothstep(0.12, 0.45, max(fw.x, fw.y)); return (1.0 - smoothstep(0.0, w, min(g.x, g.y))) * dense; }
      void main(){
        vec2 p = vw.xz; float r = length(p);
        float fade = 1.0 - smoothstep(18.0, 120.0, r);
        float pulse = exp(-pow((r - mod(time * 16.0, 160.0)) * 0.11, 2.0));
        float pulse2 = exp(-pow((r - mod(time * 16.0 + 80.0, 160.0)) * 0.11, 2.0));
        float inten = (grid(p, 2.0, 1.0) * 0.07 + grid(p, 10.0, 1.2) * 0.22) * fade * (1.0 + (pulse + pulse2) * 2.4);
        vec3 c = mix(vec3(0.05, 0.45, 0.95), vec3(0.9, 0.2, 0.9), smoothstep(40.0, 110.0, r));
        gl_FragColor = vec4(c * inten, 1.0);
      }`,
  }));
  m.rotation.x = -Math.PI / 2; m.position.y = y; m.renderOrder = -2; return m;
}

// ---------- a network's platform: a radar disc ----------
// Drawn on a circle of radius 1.15 that's scaled to the platform's radius, so r = 1 is its rim.
export function makePlatformFx(color) {
  const m = new THREE.Mesh(new THREE.CircleGeometry(1.15, 96), new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false,
    uniforms: { time: T, col: { value: new THREE.Color(color) }, seed: { value: Math.random() * 6 } },
    vertexShader: `varying vec2 vp; void main(){ vp = position.xy; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `uniform float time, seed; uniform vec3 col; varying vec2 vp;
      float band(float x, float c, float w){ return smoothstep(w, 0.0, abs(x - c)); }
      void main(){
        float r = length(vp), a = atan(vp.y, vp.x), tau = 6.2831853;
        float rim = band(r, 1.0, 0.012) * 0.9;
        float rings = (band(r, 0.34, 0.006) + band(r, 0.67, 0.006)) * 0.38 + band(r, 0.5, 0.004) * 0.18;
        float ticks = step(0.5, fract(a / tau * 96.0)) * band(r, 0.955, 0.028) * 0.7;
        float spokes = step(0.985, fract(a / tau * 12.0 + 0.004)) * step(r, 0.98) * step(0.1, r) * 0.18;
        float dash = step(0.55, fract((a / tau + time * 0.02) * 12.0)) * band(r, 1.085, 0.008) * 0.9;
        float d = mod(time * 0.7 + seed - (a + 3.14159), tau);
        float sweep = exp(-d * 2.4) * step(r, 1.0) * smoothstep(0.0, 0.12, r) * 0.4;
        float fill = (1.0 - r) * 0.06 * step(r, 1.0);
        gl_FragColor = vec4(col * (rim + rings + ticks + spokes + dash + sweep + fill), 1.0);
      }`,
  }));
  m.rotation.x = -Math.PI / 2; m.position.y = 0.012; return m;
}

// ---------- a device ----------
/** The glowing outline of a model: bright at its edges, with a band of light climbing it now and then. */
export function edgeMaterial(color) {
  return new THREE.ShaderMaterial({
    fog: false,
    uniforms: { col: { value: color }, time: T, phase: { value: Math.random() * 20 }, gain: { value: 1 } },
    vertexShader: `varying float vy; void main(){ vy = position.y; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `uniform vec3 col; uniform float time, phase, gain; varying float vy;
      void main(){
        float s = fract(time * 0.26 + phase) * 3.6 - 1.6;
        float band = exp(-pow((vy - s) * 2.6, 2.0));
        float drop = step(0.975, fract(sin(floor(time * 9.0) * 12.9898 + phase) * 43758.5453));
        gl_FragColor = vec4(col * (0.95 + 1.25 * band) * gain * (1.0 - 0.5 * drop), 1.0);
      }`,
  });
}

/** The inside of a model: dark, so the lines behind it dim, with a rim of light and faint scan lines. */
export function bodyMaterial(color) {
  return new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, fog: false,
    blending: THREE.CustomBlending, blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    uniforms: { col: { value: color }, time: T, op: { value: 0.4 }, gainRim: { value: 1 } },
    vertexShader: `varying vec3 vw; void main(){ vec4 w = modelMatrix * vec4(position, 1.0); vw = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }`,
    fragmentShader: `uniform vec3 col; uniform float time, op, gainRim; varying vec3 vw;
      void main(){
        vec3 n = normalize(cross(dFdx(vw), dFdy(vw))), v = normalize(cameraPosition - vw);
        float f = pow(1.0 - abs(dot(n, v)), 2.0);
        float sc = 0.5 + 0.5 * sin(vw.y * 52.0 - time * 2.6);
        gl_FragColor = vec4(col * (0.04 + f * 0.42 + sc * 0.04) * gainRim, op);
      }`,
  });
}

/** The pad a device stands on: a dashed ring that turns, a quiet inner ring, a soft glow. `hot` brightens it. */
export function padMaterial(color) {
  return new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false,
    uniforms: { col: { value: color }, time: T, hot: { value: 0 }, off: { value: 0 } },
    vertexShader: `varying vec2 vp; void main(){ vp = position.xy; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `uniform vec3 col; uniform float time, hot, off; varying vec2 vp;
      float band(float x, float c, float w){ return smoothstep(w, 0.0, abs(x - c)); }
      void main(){
        float r = length(vp), a = atan(vp.y, vp.x), tau = 6.2831853;
        float spin = time * (0.05 + hot * 0.25);
        float ring = band(r, 0.9, 0.05) * mix(1.0, step(0.45, fract((a / tau + spin) * 14.0)), 0.9);
        float inner = band(r, 0.6, 0.02) * 0.5;
        float fill = pow(max(0.0, 1.0 - r), 2.0) * 0.22;
        float k = (ring + inner + fill) * (0.8 + hot * 0.55) * (1.0 - off * 0.7) * step(r, 1.0);
        gl_FragColor = vec4(col * k, 1.0);
      }`,
  });
}

// ---------- the room ----------
/** Slow drifting motes in the air. */
export function makeDust(count, tex) {
  const pos = new Float32Array(count * 3), seed = new Float32Array(count);
  for (let i = 0; i < count; i++) {
    pos[i * 3] = (Math.random() - 0.5) * 120; pos[i * 3 + 1] = Math.random() * 40; pos[i * 3 + 2] = (Math.random() - 0.5) * 120; seed[i] = Math.random();
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute("position", new THREE.BufferAttribute(pos, 3)); g.setAttribute("seed", new THREE.BufferAttribute(seed, 1));
  const p = new THREE.Points(g, new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false,
    uniforms: { time: T, map: { value: tex }, speed: { value: 1 } },
    vertexShader: `attribute float seed; uniform float time, speed; varying float vs;
      void main(){ vs = seed; vec3 p = position; p.y = mod(p.y + time * speed * (0.25 + seed * 0.5), 40.0) - 3.0; p.x += sin(time * 0.2 + seed * 30.0) * 0.8;
        vec4 mv = modelViewMatrix * vec4(p, 1.0); gl_PointSize = (0.9 + seed * 1.6) * (60.0 / -mv.z); gl_Position = projectionMatrix * mv; }`,
    fragmentShader: `uniform sampler2D map; varying float vs; void main(){ vec4 t = texture2D(map, gl_PointCoord);
        vec3 c = mix(vec3(0.2, 0.8, 1.0), vec3(1.0, 0.3, 0.9), step(0.8, vs)); gl_FragColor = vec4(c * t.a * (0.25 + vs * 0.35), 1.0); }`,
  }));
  p.frustumCulled = false; return p;
}

// ---------- the picture as a whole ----------
/** Chromatic fringing toward the edges, a vignette and very faint scan lines. */
export function makeScreenPass() {
  return new ShaderPass({
    uniforms: { tDiffuse: { value: null }, time: T, height: { value: 600 } },
    vertexShader: `varying vec2 vUv; void main(){ vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `uniform sampler2D tDiffuse; uniform float time, height; varying vec2 vUv;
      void main(){
        vec2 d = vUv - 0.5; float r2 = dot(d, d);
        vec2 off = d * r2 * 0.016;
        vec3 c = vec3(texture2D(tDiffuse, vUv + off).r, texture2D(tDiffuse, vUv).g, texture2D(tDiffuse, vUv - off).b);
        c *= mix(0.5, 1.0, smoothstep(0.95, 0.2, length(d) * 1.2));
        c *= 0.975 + 0.025 * sin(vUv.y * height * 1.5 + time * 1.5);
        gl_FragColor = vec4(c, 1.0);
      }`,
  });
}
