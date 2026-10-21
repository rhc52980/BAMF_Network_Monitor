// The exposure x-ray: with it on, a ring on the platform round every device with something to flag (red where it matters, amber where it
// is coming), small badges under it saying what, and every device with nothing to flag dimmed back, so what is exposed stands out.
// What counts is worked out in data.mjs (exposureOf) and arrives on each device as `exposure`; the gateway and the DHCP and DNS servers
// get a badge for their job whether or not anything is wrong.
import * as THREE from "three";
import { CSS2DObject } from "three/addons/renderers/CSS2DRenderer.js";

const RED = 0xff4b5c, AMBER = 0xffb454, ROLE = 0x26d9ff;

export function createXray({ ents, reduced = false }) {
  const marks = new Map();        // ent key -> { ring, el, label, sig }
  let on = false;

  const badgesOf = e => {
    const x = e.d.exposure, out = [];
    if (e.d.gw) out.push({ t: "Gateway", c: "role" });
    for (const r of (x && x.roles) || []) out.push({ t: r, c: "role" });
    for (const r of (x && x.risks) || []) out.push({ t: r.kind === "port" ? r.label.replace(/ is open.*$/, "") + " open" : r.kind === "cert" ? (/expired/.test(r.label) ? "Certificate expired" : "Certificate soon") : "UPnP", c: r.sev });
    return out;
  };

  function clear(key) {
    const m = marks.get(key); if (!m) return;
    m.ring.parent && m.ring.parent.remove(m.ring); m.ring.geometry.dispose(); m.ring.material.dispose();
    m.label.parent && m.label.parent.remove(m.label); m.el.remove();
    marks.delete(key);
  }
  function setOn(v) { on = !!v; if (!on) { for (const k of [...marks.keys()]) clear(k); for (const e of ents.values()) e.xdim = false; } }

  /** Called when the devices change: puts the rings and badges on what has them, and takes them off what no longer does. */
  function refresh() {
    if (!on) return;
    for (const e of ents.values()) {
      if (e.leaving) { clear(e.key); continue; }
      const badges = badgesOf(e), sev = (e.d.exposure && e.d.exposure.sev) || "";
      e.xdim = !badges.length;
      const sig = badges.map(b => b.t + b.c).join("|");
      let m = marks.get(e.key);
      if (!badges.length) { clear(e.key); continue; }
      if (m && m.sig === sig) continue;
      if (!m) {
        const ring = new THREE.Mesh(new THREE.TorusGeometry(1, 0.028, 6, 64), new THREE.MeshBasicMaterial({ color: ROLE, transparent: true, opacity: 0.9, blending: THREE.AdditiveBlending, depthWrite: false }));
        ring.rotation.x = Math.PI / 2;
        const el = document.createElement("div");
        const label = new CSS2DObject(el);
        e.group.add(ring, label);
        m = { ring, el, label, sig: "" };
        marks.set(e.key, m);
      }
      m.sig = sig;
      m.sev = sev;
      m.el.className = "v3d-xr";
      m.el.innerHTML = badges.map(b => `<span class="v3d-b v3d-b-${b.c}">${b.t.replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]))}</span>`).join("");
      m.ring.material.color.setHex(sev === "high" ? RED : sev === "med" ? AMBER : ROLE).multiplyScalar(1.6);
    }
    for (const k of [...marks.keys()]) if (!ents.has(k)) clear(k);
  }

  function tick(time) {
    if (!on) return;
    for (const [key, m] of marks) {
      const e = ents.get(key); if (!e) continue;
      const size = 0.9 + (m.sev === "high" ? 0.18 * Math.sin(time * 4) : 0);
      m.ring.position.y = -0.55;
      m.ring.scale.setScalar(reduced ? 0.95 : size);
      m.ring.material.opacity = (m.sev ? 0.9 : 0.5) * (e.dim ? 0.2 : 1);
      m.ring.visible = e.group.visible;
      m.label.position.set(0, -0.85, 0);
      m.label.visible = e.group.visible && !e.dim;
    }
  }

  function dispose() { for (const k of [...marks.keys()]) clear(k); }
  return { setOn, refresh, tick, dispose, get count() { return marks.size; }, get on() { return on; }, badgesOf };
}
