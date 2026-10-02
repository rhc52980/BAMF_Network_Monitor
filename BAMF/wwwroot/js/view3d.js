// The 3D tab. The 3D library and models are only fetched the first time it's opened; after that this keeps
// it fed from the dashboard's own data: the same devices, kinds, names, gateways and unusual findings the
// other tabs use. The drawing is in view3d/scene.mjs, and what goes where in view3d/data.mjs.
let view3d = null, view3dStarting = null, view3dFailed = null, view3dData = null;

const VIEW3D_CREDITS = `<h3>Model credits</h3>
  <p>Two of the device models are adapted from Poly Pizza models under the Creative Commons Attribution 3.0 licence, simplified and stripped of their colours:</p>
  <ul>
    <li><b>Security Camera</b> by J-Toastie</li>
    <li><b>Laptop</b> by Poly by Google</li>
  </ul>
  <p>The other forty-four were made for BAMF. The 3D drawing is <a href="https://threejs.org" target="_blank" rel="noopener noreferrer">three.js</a> (MIT licence).</p>`;

async function start3d() {
  const wrap = $("view3dWrap");
  try {
    const [scene, data] = await Promise.all([import("/view3d/scene.mjs"), import("/view3d/data.mjs")]);
    view3dData = data;
    view3d = await scene.createView(wrap, {
      credits: VIEW3D_CREDITS,
      // History lives in the device's row on the Devices tab, so go there with it open.
      onOpenDevice: async d => { const h = hosts.find(x => x.id === d.id); if (!h) return; await openHistory(h); jumpToHost(h.id); },
    });
  } catch (err) {
    view3dFailed = err;
    wrap.innerHTML = `<div class="v3d-fail"><b>The 3D view couldn't start.</b><br>${esc(err.message || String(err))}<br>
      The <a href="#map">Map</a> and the <a href="#devices">Devices</a> tab show the same network without it.</div>`;
  }
}

// What's been noticed as unusual, for the beacons.
async function unusual3d() {
  try {
    const r = await fetch("/api/unusual");
    return r.ok ? (await r.json()).items || [] : [];
  } catch { return []; }
}

// The tab fills what's left of the window below the tab bar (on a phone the stylesheet sets its height).
function size3d() {
  const wrap = $("view3dWrap");
  if (wrap.hidden) return;
  if (wrap.classList.contains("v3d-full") || document.fullscreenElement === wrap) return;   // full screen sizes itself
  wrap.style.height = innerWidth <= 700 ? "" : Math.max(480, innerHeight - wrap.getBoundingClientRect().top - 16) + "px";
}
addEventListener("resize", size3d);

// Called with each refresh while the 3D tab is open, and when it's left.
async function refresh3d() {
  if (view !== "3d") { if (view3d) view3d.setActive(false); return; }
  size3d();
  if (view3dFailed) return;
  if (!view3d) {
    if (!view3dStarting) view3dStarting = start3d();
    await view3dStarting;
    if (!view3d) return;
  }
  const top = (trafficCache && trafficCache.top) || [];
  const rateOf = h => { const i = top.findIndex(x => x.hostId === h.id); return i < 0 ? 2 : Math.max(3, 9 - i); };
  const items = await unusual3d();
  if (view !== "3d") return;
  const scene = view3dData.buildScene({
    hosts: hosts.filter(h => inNetwork(h)), kindOf: deviceKind, nameOf: dispName, unusual: items,
    gatewayIp: subnet => (networkPlaces[subnet] || {}).gateway || null, rateOf,
  });
  view3d.update(scene);
  view3d.setActive(true);
}
