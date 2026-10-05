// BAMF's finder: what the app shows first. It reopens the server you used last,
// or looks for BAMF on the phone's network, or takes an address you type. Once
// it has one it hands the whole window to that server's dashboard.
//
// Coming back here (the Back button, or a swipe from the edge) never connects
// by itself again, so there is always a way to pick a different server.
(function () {
  "use strict";
  const $ = id => document.getElementById(id);
  // The plugin is written natively (Java and Swift) and the page asks Capacitor for it by name.
  const Native = (function () {
    const cap = window.Capacitor;
    if (!cap) return null;
    try { if (typeof cap.registerPlugin === "function") return cap.registerPlugin("BamfDiscovery"); } catch (e) { /* fall through */ }
    return (cap.Plugins && cap.Plugins.BamfDiscovery) || null;
  })();
  const KEY = "bamf.servers";   // [{url, last}], newest first
  const MAX_SAVED = 8;

  // ---- what's saved ----
  function load() { try { const v = JSON.parse(localStorage.getItem(KEY) || "[]"); return Array.isArray(v) ? v : []; } catch (e) { return []; } }
  function store(list) { try { localStorage.setItem(KEY, JSON.stringify(list.slice(0, MAX_SAVED))); } catch (e) { /* storage blocked: it just isn't remembered */ } }
  function remember(url) { store([{ url: url, last: Date.now() }].concat(load().filter(s => s.url !== url))); }
  function forget(url) { store(load().filter(s => s.url !== url)); renderSaved(); }

  const label = url => { try { const u = new URL(url); return u.host; } catch (e) { return url; } };
  const status = (text, bad) => { $("status").textContent = text; $("status").style.color = bad ? "var(--danger)" : ""; };

  // ---- opening a server ----
  async function open(url) {
    status("Opening " + label(url) + "…");
    remember(url);
    try { await Native.openServer({ url: url }); }
    catch (e) { status("Couldn't open " + label(url) + ".", true); }
  }

  async function reachable(url) {
    try { return (await Native.check({ url: url })).ok === true; } catch (e) { return false; }
  }

  // ---- lists ----
  function row(url, onGo, onRemove) {
    const d = document.createElement("div");
    d.className = "row";
    const go = document.createElement("button");
    go.className = "go"; go.type = "button";
    go.textContent = label(url);
    const small = document.createElement("small");
    small.textContent = url.startsWith("https") ? "HTTPS" : "HTTP";
    go.appendChild(small);
    go.onclick = onGo;
    d.appendChild(go);
    if (onRemove) {
      const rm = document.createElement("button");
      rm.className = "rm"; rm.type = "button"; rm.textContent = "✕";
      rm.setAttribute("aria-label", "Forget " + label(url));
      rm.onclick = onRemove;
      d.appendChild(rm);
    }
    return d;
  }

  function renderSaved() {
    const list = load();
    $("saved").hidden = list.length === 0;
    $("savedList").replaceChildren(...list.map(s => row(s.url, () => open(s.url), () => forget(s.url))));
  }

  const foundUrls = new Set();
  function showFound(url) {
    if (foundUrls.has(url)) return;
    foundUrls.add(url);
    $("found").hidden = false;
    $("foundList").appendChild(row(url, () => open(url)));
  }

  // ---- discovery ----
  let scanning = false;
  async function scan(auto) {
    if (scanning) return;
    scanning = true;
    foundUrls.clear();
    $("foundList").replaceChildren();
    $("found").hidden = true;
    $("scanBtn").disabled = true;
    $("scanHint").textContent = "";
    let net;
    try { net = await Native.getNetworkInfo(); }
    catch (e) {
      status("Not on Wi-Fi.", true);
      $("scanHint").textContent = "Connect this phone to the Wi-Fi your BAMF machine is on, or enter an address below.";
      $("scanBtn").disabled = false; scanning = false;
      return;
    }
    status("Looking on " + net.scanned + "…");
    $("bar").hidden = false; $("barFill").style.width = "0%";
    const handles = [
      await Native.addListener("serverFound", s => showFound(s.url)),
      await Native.addListener("progress", p => { $("barFill").style.width = Math.round(100 * p.done / p.total) + "%"; }),
    ];
    let res = null;
    try { res = await Native.discover({ port: 8840 }); } catch (e) { /* reported below */ }
    handles.forEach(h => h.remove());
    $("bar").hidden = true;
    $("scanBtn").disabled = false;
    scanning = false;
    const n = foundUrls.size;
    if (n === 1 && auto) { open([...foundUrls][0]); return; }
    if (n === 0) {
      status("No BAMF found on " + net.scanned + ".", true);
      $("scanHint").textContent = "Is BAMF running, and is it on this Wi-Fi? Its address can also be entered below.";
    } else {
      status(n === 1 ? "Found 1 BAMF server." : "Found " + n + " BAMF servers.");
    }
  }

  // ---- typing an address ----
  // "192.168.1.20" -> http on BAMF's port; "host:9000" -> http there; a full URL as written.
  function candidates(text) {
    const t = text.trim().replace(/\/+$/, "");
    if (!t) return [];
    if (/^https?:\/\//i.test(t)) return [t];
    return /:\d+$/.test(t) ? ["http://" + t, "https://" + t] : ["http://" + t + ":8840", "https://" + t];
  }

  $("manual").addEventListener("submit", async ev => {
    ev.preventDefault();
    const msg = $("manualMsg"); msg.hidden = true; msg.className = "msg";
    const list = candidates($("addr").value);
    if (!list.length) return;
    status("Checking " + $("addr").value.trim() + "…");
    for (const url of list) {
      if (await reachable(url)) { open(url); return; }
    }
    status("");
    msg.hidden = false; msg.classList.add("bad");
    msg.textContent = "No BAMF server answered at that address.";
  });

  $("scanBtn").addEventListener("click", () => scan(false));

  // ---- start ----
  async function start() {
    renderSaved();
    if (!Native) {
      status("This page runs inside the BAMF app.", true);
      const c = window.Capacitor;
      $("scanHint").textContent = c
        ? "Capacitor " + (c.getPlatform ? c.getPlatform() : "?") + "; plugins: " + (c.PluginHeaders || []).map(h => h.name).join(", ")
        : "Capacitor isn't loaded.";
      return;
    }
    // Arrived by going back from a server: offer the choices, don't reconnect.
    const nav = performance.getEntriesByType && performance.getEntriesByType("navigation")[0];
    if (nav && nav.type === "back_forward") { status("Choose a server."); return; }
    const last = load()[0];
    if (last) {
      status("Connecting to " + label(last.url) + "…");
      if (await reachable(last.url)) { open(last.url); return; }
      status(label(last.url) + " isn't answering.", true);
    }
    scan(true);
  }

  // A page restored from the back/forward cache doesn't re-run start(); show the choices.
  window.addEventListener("pageshow", ev => { if (ev.persisted) { renderSaved(); status("Choose a server."); } });
  start();
})();
