// The service watch: the things on your devices, checked every minute. The card on Activity lists what is watched with its last 24 hours,
// and adds more (a suggestion from the open ports BAMF found, or a device, a check and a port by hand); Settings → Alerts has the switch.
let svwCache = null;
let svwOpen = false;       // the add panel; open of its own accord when nothing is watched yet

function svwTime(iso) { return new Date(iso).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }); }
function svwMs(ms) { return ms >= 1000 ? (Math.round(ms / 100) / 10) + " s" : ms + " ms"; }
function svwWhat(r) { return r.kind === "web" ? `${r.https ? "https" : "http"} :${r.port}${r.path === "/" ? "" : r.path}` : `port ${r.port}`; }

async function loadServiceWatch() {
  try {
    const r = await fetch("/api/service-watch");
    if (!r.ok) return;
    svwCache = await r.json();
    renderServiceWatch();
    renderServiceWatchSetting();
  } catch { /* keep the last */ }
}

function renderServiceWatchSetting() {
  const d = svwCache, t = $("setServiceWatch");
  if (!d || !t) return;
  setToggleState(t, d.enabled);
  $("serviceWatchStatus").textContent = !d.enabled ? "Off."
    : d.services.length ? `On, watching ${d.services.length} service${d.services.length === 1 ? "" : "s"}.` : "On. Nothing is watched yet: add one on the Activity tab.";
}

function renderServiceWatch() {
  const d = svwCache;
  if (!d || !$("svwBody")) return;
  const rows = d.services || [];
  const down = rows.filter(r => r.state === "down").length, slow = rows.filter(r => r.state === "slow").length;
  $("svwSub").textContent = !d.enabled ? "Off. Settings → Alerts switches it on."
    : !rows.length ? "Checks the things on your devices, not just the devices: a web page or a port, every minute. Nothing is watched yet."
    : down ? `${rows.length - down} of ${rows.length} up. Checked every minute, the last 24 hours below.`
    : `All ${rows.length} up${slow ? `, ${slow} slow` : ""}. Checked every minute, the last 24 hours below.`;
  $("svwNote").textContent = !d.enabled ? "" : `Alert after ${d.failsToAlert} failed checks in a row, and again when it's back. Quiet hours apply.`;
  $("svwAddBtn").style.display = rows.length >= d.max ? "none" : "";
  const body = $("svwBody");
  body.innerHTML = "";
  for (const r of rows) {
    const cls = r.state === "up" ? "up" : r.state === "down" ? "down" : r.state === "slow" ? "slow" : "";
    let line = "", lineCls = "", stat = "", statCls = "";
    if (r.state === "down") {
      const mins = Math.max(1, Math.round((Date.now() - new Date(r.downSince)) / 60000));
      line = `Down ${mins} min, since ${svwTime(r.downSince)}` + (r.error ? ` · ${r.error}` : "");
      lineCls = "bad"; stat = "no answer"; statCls = "bad";
    } else if (r.state === "off") { line = "Its device is off, so it isn't asked."; stat = "device off"; }
    else if (r.state === "waiting") { line = "First check within a minute."; stat = "waiting"; }
    else {
      if (r.state === "slow") { line = `Slow now: over ${svwMs(r.slowMs)}`; lineCls = "warn"; }
      else if (r.lastDown) line = `Down ${r.lastDown.minutes} min at ${svwTime(r.lastDown.at)}`;
      stat = r.ms != null ? svwMs(r.ms) : "—"; statCls = r.state === "slow" ? "warn" : "";
    }
    const el = document.createElement("div");
    el.className = "svw-row";
    el.innerHTML = `<div class="svw-head"><span class="wan-dot ${cls}"></span><span class="svw-name">${esc(r.name)}</span>` +
      `<span class="svw-stat" title="Share of checks that passed in the last 24 hours">${r.uptime == null ? "" : r.uptime + "%"}</span>` +
      `<span class="svw-stat ${statCls}">${esc(stat)}</span><button type="button" class="svw-del" aria-label="Stop watching ${esc(r.name)}" title="Stop watching">×</button></div>` +
      `<div class="svw-line">${esc(r.device)} · ${esc(svwWhat(r))}</div>` +
      (line ? `<div class="svw-line ${lineCls}">${esc(line)}</div>` : "") +
      `<div class="svw-strip" role="img" aria-label="Last 24 hours, half an hour a bar">${[...r.strip].map(c => `<i class="${c === "u" ? "" : c}"></i>`).join("")}</div>` +
      `<div class="svw-ends"><span>24 h ago</span><span>now</span></div>`;
    el.querySelector(".svw-del").onclick = async () => {
      if (!confirm(`Stop watching ${r.name} on ${r.device}? Its history goes too.`)) return;
      try {
        const res = await fetch(`/api/service-watch/${r.id}`, { method: "DELETE" });
        if (res.ok) { toast("Stopped watching " + esc(r.name)); loadServiceWatch(); } else toast("Couldn't do that");
      } catch { toast("Couldn't reach BAMF"); }
    };
    body.appendChild(el);
  }
  renderServiceWatchAdd();
}

// ---- the add panel ----
function svwForm() {
  const kind = $("svwKind").value;
  return { hostId: +$("svwDevice").value, kind: kind === "port" ? "port" : "web", https: kind === "webs", port: +$("svwPort").value,
    path: $("svwPath").value.trim() || "/", name: $("svwName").value.trim(), slowMs: $("svwSlow").value === "" ? 0 : +$("svwSlow").value };
}
function svwSyncKind() {
  const k = $("svwKind").value;
  $("svwPathWrap").style.display = k === "port" ? "none" : "";
}
function renderServiceWatchAdd() {
  const d = svwCache;
  if (!d) return;
  const open = svwOpen || (!d.services.length && d.enabled);
  $("svwAdd").hidden = !open;
  if (!open) return;
  const sel = $("svwDevice"), keep = sel.value;
  sel.innerHTML = d.devices.map(x => `<option value="${x.id}">${esc(x.name)} · ${esc(x.ip)}</option>`).join("");
  if (keep && [...sel.options].some(o => o.value === keep)) sel.value = keep;
  const sg = $("svwSugg");
  sg.innerHTML = "";
  $("svwSuggHelp").hidden = !d.suggestions.length;
  for (const s of d.suggestions) {
    const b = document.createElement("button");
    b.type = "button"; b.className = "tl-chip";
    b.textContent = `${s.name} on ${s.device} :${s.port}`;
    b.onclick = () => {
      sel.value = String(s.hostId);
      $("svwKind").value = s.kind === "port" ? "port" : s.https ? "webs" : "web";
      $("svwPort").value = s.port; $("svwPath").value = s.path; $("svwName").value = s.name;
      svwSyncKind(); $("svwResult").textContent = ""; $("svwResult").className = "svw-result";
    };
    sg.appendChild(b);
  }
}
function svwSay(text, cls) { const r = $("svwResult"); r.textContent = text; r.className = "svw-result" + (cls ? " " + cls : ""); }

$("svwAddBtn").onclick = () => { svwOpen = !svwOpen; renderServiceWatchAdd(); };
$("svwCancel").onclick = () => { svwOpen = false; svwSay(""); renderServiceWatchAdd(); };
$("svwKind").onchange = () => {
  svwSyncKind();
  const k = $("svwKind").value, p = $("svwPort");
  if (k === "webs" && (p.value === "80" || p.value === "8080")) p.value = "443";
  if (k === "web" && (p.value === "443" || p.value === "8443")) p.value = "80";
};
$("svwTry").onclick = async () => {
  const f = svwForm();
  if (!f.hostId) { svwSay("Pick a device first.", "bad"); return; }
  if (!(f.port >= 1 && f.port <= 65535)) { svwSay("Enter a port from 1 to 65535.", "bad"); return; }
  svwSay("Checking…");
  $("svwTry").disabled = true;
  try {
    const r = await fetch("/api/service-watch/try", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(f) });
    const d = await r.json();
    if (!r.ok) svwSay(d.error || "That didn't go.", "bad");
    else svwSay(d.summary, d.ok ? (d.slow ? "" : "ok") : "bad");
  } catch { svwSay("Couldn't reach BAMF.", "bad"); }
  $("svwTry").disabled = false;
};
$("svwSave").onclick = async () => {
  const f = svwForm();
  if (!f.hostId) { svwSay("Pick a device first.", "bad"); return; }
  if (!(f.port >= 1 && f.port <= 65535)) { svwSay("Enter a port from 1 to 65535.", "bad"); return; }
  try {
    const r = await fetch("/api/service-watch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(f) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { svwSay(d.error || "That didn't go.", "bad"); return; }
    toast("Watching it: the first check is within a minute");
    svwOpen = false; svwSay(""); $("svwName").value = "";
    loadServiceWatch();
  } catch { svwSay("Couldn't reach BAMF.", "bad"); }
};

// ---- Settings → Alerts ----
$("setServiceWatch").onclick = async () => {
  const on = !$("setServiceWatch").classList.contains("on");
  try {
    const r = await fetch("/api/settings/service-watch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: on }) });
    if (!r.ok) throw new Error("HTTP " + r.status);
    toast(on ? "Services are watched again" : "The service watch is off");
    loadServiceWatch();
  } catch { toast("Couldn't save that"); }
};
