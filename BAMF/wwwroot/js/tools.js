// The Tools tab: ping that runs live, trace route and path ping as hop tables, a DNS lookup that asks three resolvers, an HTTP check, a port
// check, and a "Why is it slow?" button that checks router, DNS, internet and a web name in turn. They run on the machine BAMF is on, so "can
// BAMF reach it, and by what path?" has an answer without a terminal. A device BAMF knows can be picked whenever the tools are on; an address
// or name that was typed needs "any address" switched on in Settings.

const TL_NAMES = { ping: "Ping", trace: "Trace route", path: "Path ping", dns: "DNS lookup", http: "HTTP check", port: "Port check", why: "Why is it slow?" };
const TL_RECENT_KEY = "bamfToolsRecent";
let tlCaps = null;           // what /api/tools said: switches, this machine's router and DNS server, the internet watch's address
let tlKind = "ping";
let tlRunning = false;       // a one-shot tool is running (live ping runs on its own)
let tlPing = null;           // the live ping in progress
let tlEntered = false;       // the tab is open: its capabilities are read again each time it is opened, since Settings may have changed
let tlToken = 0;             // bumped whenever the result area is taken over, so a late answer doesn't draw over a newer one

const tlRecent = () => { try { return JSON.parse(localStorage.getItem(TL_RECENT_KEY) || "[]"); } catch { return []; } };
const tlRemember = list => { try { localStorage.setItem(TL_RECENT_KEY, JSON.stringify(list.slice(0, 8))); } catch { /* private window: the list just isn't kept */ } };

// ---- the tab ----

// Hidden for the view-only password (it can't run anything) and when the tools are switched off.
function renderToolsTab() {
  const tab = $("toolsTab");
  if (tab) tab.hidden = !networkToolsEnabled || role === "viewer";
}

function tlDevices() { return hosts.filter(h => !h.remote && !h.forgotten && h.ip); }
function tlMatch(text) {
  const t = text.trim().toLowerCase();
  if (!t) return null;
  return tlDevices().find(h => h.ip === t || dispName(h).toLowerCase() === t || (h.customName || "").toLowerCase() === t || (h.hostname || "").toLowerCase() === t) || null;
}

async function tlLoadCaps() {
  try { const r = await fetch("/api/tools"); if (r.ok) tlCaps = await r.json(); } catch { /* the next visit tries again */ }
  return tlCaps;
}

// Called with every render while the tab is open: cheap, and never touches what's in the result area.
function renderToolsView() {
  const dl = $("tlDevices");
  const names = tlDevices().sort((a, b) => dispName(a).localeCompare(dispName(b)));
  const sig = names.map(h => h.id + dispName(h) + h.ip).join("|");
  if (dl.dataset.sig !== sig) {
    dl.dataset.sig = sig;
    dl.innerHTML = names.map(h => `<option value="${esc(dispName(h) === "—" ? h.ip : dispName(h))}">${esc(h.ip)}</option>`).join("");
  }
  if (!tlEntered || !tlCaps) { tlEntered = true; tlLoadCaps().then(() => { renderToolsQuick(); renderToolsNote(); }); }
  renderToolsQuick();
  renderToolsNote();
  renderToolsRecent();
  $("tlPorts").hidden = tlKind !== "port";
  for (const b of $("tlButtons").querySelectorAll("button")) b.classList.toggle("on", b.dataset.tool === tlKind);
  if (!$("tlResult").innerHTML) $("tlResult").innerHTML = `<div class="tl-empty">Pick a tool, or press <b>Why is it slow?</b>. Pick a device from the box, or type an address.</div>`;
}

function renderToolsNote() {
  const note = $("tlNote");
  if (!tlCaps) { note.hidden = true; return; }
  if (!tlCaps.enabled) { note.hidden = false; note.innerHTML = `Network tools are switched off. <a href="#settings/system">Settings → System</a> switches them on.`; return; }
  note.hidden = tlCaps.anywhere;
  if (!tlCaps.anywhere) note.innerHTML = `You can pick any of your devices. To use an address or name that isn't one, such as one on the internet, switch on <a href="#settings/system">Ping and trace route to any address</a>.`;
}

// Starting points: this machine's own router and DNS server, the internet watch's address, and the targets used last.
function renderToolsQuick() {
  const q = [];
  const add = (label, text) => { if (text && !q.some(x => x.text === text)) q.push({ label, text }); };
  if (tlCaps) {
    (tlCaps.gateways || []).slice(0, 1).forEach(g => add("Router", g));
    (tlCaps.dnsServers || []).slice(0, 1).forEach(d => add("DNS server", d));
    if (tlCaps.anywhere) { add("Internet", tlCaps.wanTarget); add("example.com", "example.com"); }
  }
  for (const r of tlRecent()) add(r.text, r.text);
  $("tlQuick").innerHTML = q.slice(0, 9).map(x => `<button type="button" class="tl-chip" data-t="${esc(x.text)}" title="${esc(x.text)}">${esc(x.label)}</button>`).join("");
}

function renderToolsRecent() {
  const list = tlRecent();
  $("tlRecentWrap").hidden = !list.length;
  $("tlRecent").innerHTML = list.map((r, i) =>
    `<button type="button" class="tl-run" data-i="${i}"><span class="mono">${esc(TL_NAMES[r.tool] || r.tool)}</span><span class="tl-run-t">${esc(r.text)}</span><span class="tl-run-s">${esc(r.summary || "")}</span><span class="tl-run-w">${esc(fmtAgo(r.at))}</span></button>`).join("");
}

// ---- running ----

function tlTarget() {
  const text = $("tlTarget").value.trim();
  return { text, device: tlMatch(text) };
}

function tlBody(kind, t) {
  const body = { tool: kind };
  if (t.device) {
    if (kind === "http") body.target = ((t.device.openPorts || []).includes(443) ? "https://" : "http://") + t.device.ip + "/";
    else body.hostId = t.device.id;
  } else body.target = t.text;
  if (kind === "port") body.ports = $("tlPorts").value.trim();
  return body;
}

function tlSay(html) { tlToken++; $("tlResult").innerHTML = html; }

function tlStopPing() {
  if (tlPing) { tlPing.stopped = true; clearTimeout(tlPing.timer); }
}

// Away from the tab: the live ping stops, and the next visit reads the switches again.
function tlLeft() { tlStopPing(); tlEntered = false; }

function tlPick(kind) {
  tlKind = kind;
  $("tlPorts").hidden = kind !== "port";
  for (const b of $("tlButtons").querySelectorAll("button")) b.classList.toggle("on", b.dataset.tool === kind);
}

function tlRun(kind) {
  if (kind) tlPick(kind);
  tlStopPing();
  const t = tlTarget();
  if (!t.text) { tlSay(`<div class="tl-empty">Pick a device or type an address first.</div>`); $("tlTarget").focus(); return; }
  if (!t.device && tlCaps && !tlCaps.anywhere) {
    tlSay(`<div class="tl-bad">That isn't one of your devices. To use an address or name that isn't, switch on <a href="#settings/system">Ping and trace route to any address</a> in Settings.</div>`);
    return;
  }
  if (tlKind === "ping") tlStartPing(t);
  else tlOneShot(tlKind, t);
}

async function tlOneShot(kind, t) {
  if (tlRunning) { toast("Another tool is still running"); return; }
  tlRunning = true;
  const token = ++tlToken;
  const slow = kind === "path" ? " This can take a minute or two." : kind === "trace" ? " This can take half a minute." : "";
  $("tlResult").innerHTML = `<div class="tl-wait">Running ${esc(TL_NAMES[kind].toLowerCase())} on ${esc(t.device ? dispName(t.device) : t.text)}…${slow}</div>`;
  try {
    const r = await fetch("/api/tools/run", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(tlBody(kind, t)) });
    const d = await r.json().catch(() => ({}));
    if (token !== tlToken) return;
    if (!r.ok) { $("tlResult").innerHTML = `<div class="tl-bad">${esc(d.error || `It didn't run (HTTP ${r.status}).`)}</div>`; return; }
    $("tlResult").innerHTML = TL_DRAW[kind](d) + tlActions();
    tlWireActions(() => tlRun(kind));
    const mem = tlRecent().filter(x => !(x.tool === kind && x.text === t.text));
    mem.unshift({ tool: kind, text: t.text, summary: tlSummary(kind, d), at: new Date().toISOString() });
    tlRemember(mem); renderToolsRecent(); renderToolsQuick();
  } catch { if (token === tlToken) $("tlResult").innerHTML = `<div class="tl-bad">Couldn't reach BAMF.</div>`; }
  finally { tlRunning = false; }
}

function tlSummary(kind, d) {
  const x = d.data || {};
  if (kind === "dns") return (x.compare && x.compare.summary) || (x.reverse && x.reverse.summary) || "";
  return x.summary || "";
}

function tlActions() { return `<div class="tl-acts"><button type="button" class="export-btn" id="tlCopy">Copy</button><button type="button" class="export-btn" id="tlAgain">Run again</button></div>`; }
function tlWireActions(again) {
  const c = $("tlCopy"), a = $("tlAgain");
  if (c) c.onclick = async () => {
    const text = [...$("tlResult").querySelectorAll(".tl-copy")].map(e => e.innerText.replace(/\n+/g, "\n").trim()).join("\n");
    try { await navigator.clipboard.writeText(text); toast("Copied"); } catch { toast("Couldn't copy"); }
  };
  if (a) a.onclick = again;
}

// ---- live ping ----

function tlStartPing(t) {
  const p = tlPing = { body: tlBody("ping", t), label: t.device ? dispName(t.device) : t.text, address: "", pts: [], sent: 0, lost: 0, sum: 0, got: 0, min: null, max: null, jitSum: 0, last: null, stopped: false, timer: null, startedAt: Date.now(), error: "" };
  const token = ++tlToken;
  $("tlResult").innerHTML = `<div class="tl-pinghead"><span class="tl-live" id="tlLive"></span><span class="tl-pause"><button type="button" class="export-btn" id="tlPause">Pause</button></span></div>
    <div class="tl-stats" id="tlStats"></div><div id="tlSpark"></div><div class="tl-err" id="tlPingErr"></div>`;
  $("tlPause").onclick = () => {
    if (p.stopped) { p.stopped = false; p.startedAt = Date.now(); tlTick(p, token); $("tlPause").textContent = "Pause"; }
    else { tlStopPing(); $("tlPause").textContent = "Resume"; paintPing(p); }
  };
  paintPing(p);
  tlTick(p, token);
  const mem = tlRecent().filter(x => !(x.tool === "ping" && x.text === t.text));
  mem.unshift({ tool: "ping", text: t.text, summary: "", at: new Date().toISOString() });
  tlRemember(mem); renderToolsRecent(); renderToolsQuick();
}

async function tlTick(p, token) {
  if (p.stopped || token !== tlToken) return;
  // Ten minutes is plenty; a forgotten tab shouldn't ping all night.
  if (Date.now() - p.startedAt > 10 * 60e3) { p.stopped = true; if ($("tlPause")) $("tlPause").textContent = "Resume"; paintPing(p); return; }
  try {
    const r = await fetch("/api/tools/ping", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(p.body) });
    const d = await r.json().catch(() => ({}));
    if (token !== tlToken || p.stopped) return;
    if (r.status === 429) { p.error = ""; }
    else if (!r.ok) { p.stopped = true; p.error = d.error || `It didn't run (HTTP ${r.status}).`; if ($("tlPause")) $("tlPause").textContent = "Resume"; paintPing(p); return; }
    else {
      p.error = ""; p.address = d.address || p.address; p.sent++;
      const ms = d.ok ? d.ms : null;
      if (ms === null) p.lost++;
      else { p.got++; p.sum += ms; p.min = p.min === null ? ms : Math.min(p.min, ms); p.max = p.max === null ? ms : Math.max(p.max, ms); if (p.last !== null) p.jitSum += Math.abs(ms - p.last); p.last = ms; }
      p.pts.push(ms); if (p.pts.length > 60) p.pts.shift();
    }
  } catch { if (token === tlToken && !p.stopped) { p.error = "Couldn't reach BAMF."; } }
  if (token !== tlToken || p.stopped) return;
  paintPing(p);
  p.timer = setTimeout(() => tlTick(p, token), 1000);
}

function paintPing(p) {
  if (!$("tlStats")) return;
  const avg = p.got ? Math.round(p.sum / p.got) : null;
  const tiles = [
    ["Last", p.pts.length ? (p.pts[p.pts.length - 1] === null ? "lost" : p.pts[p.pts.length - 1] + " ms") : "—"],
    ["Average", avg === null ? "—" : avg + " ms"],
    ["Fastest / slowest", p.got ? `${p.min} / ${p.max}` : "—"],
    ["Lost", p.sent ? `${Math.round(100 * p.lost / p.sent)}%` : "—"],
    ["Jitter", p.got > 1 ? Math.round(p.jitSum / (p.got - 1)) + " ms" : "—"],
  ];
  $("tlLive").innerHTML = `<span class="tl-dot${p.stopped ? " off" : ""}"></span>${p.stopped ? "Paused" : "Pinging"} <b>${esc(p.label)}</b>${p.address && p.address !== p.label ? ` <span class="mono">${esc(p.address)}</span>` : ""}${p.stopped ? "" : " every second"}`;
  $("tlStats").innerHTML = tiles.map(([k, v]) => `<div class="tl-stat"><b>${esc(v)}</b><span>${esc(k)}</span></div>`).join("");
  $("tlSpark").innerHTML = sparkSvg(p.pts);
  $("tlPingErr").textContent = p.error;
}

// The last 60 answers as a line, with a red tick where one was lost.
function sparkSvg(pts) {
  const W = 620, H = 70, n = 60;
  const ok = pts.filter(v => v !== null);
  const top = Math.max(30, ok.length ? Math.max(...ok) * 1.2 : 30);
  let d = "", lost = "";
  pts.forEach((v, i) => {
    const x = Math.round(i * W / (n - 1));
    if (v === null) lost += `<line x1="${x}" x2="${x}" y1="4" y2="${H}" class="tl-lostline"/>`;
    else d += (d ? " L" : "M") + x + " " + Math.round(H - 4 - (v / top) * (H - 10));
  });
  return `<svg viewBox="0 0 ${W} ${H}" width="100%" height="${H}" role="img" aria-label="The last ${pts.length} ping answers"><line x1="0" x2="${W}" y1="${H - 1}" y2="${H - 1}" class="tl-base"/><path d="${d}" class="tl-line" fill="none"/>${lost}</svg>`;
}

// ---- drawing what came back ----

const tlMs = v => v == null ? "—" : v + " ms";
const tlFinding = text => text ? `<div class="tl-find tl-copy">${esc(text)}</div>` : "";

function tlHead(d, extra) {
  return `<div class="tl-rhead tl-copy"><b>${esc(d.target)}</b>${d.address && d.address !== d.target ? ` <span class="mono">${esc(d.address)}</span>` : ""}${extra ? ` <span class="tl-sum">${esc(extra)}</span>` : ""}${d.note ? `<div class="tl-note2">${esc(d.note)}</div>` : ""}</div>`;
}

function tlHopTable(d, path) {
  const hops = d.data.hops || [];
  const top = Math.max(40, ...hops.map(h => (path ? h.avg : h.ms) || 0));
  let prev = null;
  const rows = hops.map(h => {
    const ms = path ? h.avg : h.ms;
    const jump = prev !== null && ms != null && ms - prev >= 25;
    if (ms != null) prev = ms;
    const name = h.address ? (h.name ? `${esc(h.name)}` : `<span class="tl-dim">${esc(h.address)}</span>`) : `<span class="tl-dim">no answer</span>`;
    const bar = ms == null ? (path && h.silent && h.address ? `<span class="tl-dim">answers a trace, not a ping</span>` : "")
      : `<span class="tl-bar${jump ? " jump" : ""}" style="width:${Math.max(2, Math.round(100 * ms / top))}%"></span><span class="tl-ms">${path ? `${h.min} / ${h.avg} / ${h.max} ms` : ms + " ms"}</span>`;
    return `<tr><td>${h.n}</td><td>${name}</td><td class="mono">${h.name && h.address ? esc(h.address) : ""}</td><td class="tl-barcell">${bar}</td>${path ? `<td class="${h.lossPercent > 0 ? "tl-lossy" : "tl-dim"}">${h.silent ? "" : h.lossPercent + "%"}</td>` : ""}</tr>`;
  }).join("");
  return `<div class="tl-scroll"><table class="tl-table tl-copy"><thead><tr><th>Hop</th><th>Name</th><th>Address</th><th>${path ? "Fastest / average / slowest" : "Time"}</th>${path ? "<th>Lost</th>" : ""}</tr></thead><tbody>${rows}</tbody></table></div>`;
}

const TL_DRAW = {
  trace: d => tlHead(d, d.data.summary) + tlHopTable(d, false) + tlFinding(d.data.finding),
  path: d => tlHead(d, d.data.summary) + tlHopTable(d, true) + tlFinding(d.data.finding),

  dns(d) {
    const x = d.data, out = [`<div class="tl-rhead tl-copy"><b>${esc(d.target)}</b></div>`];
    if (x.reverse) out.push(`<div class="tl-line1 tl-copy">${esc(x.reverse.summary)}</div>`);
    if (x.compare) {
      const rows = x.compare.answers.map(a => {
        const answer = a.status === "ok" ? a.addresses.map(esc).join(", ") : a.status === "nxdomain" ? "no such name" : a.status === "empty" ? "no address" : esc(a.error || "no answer");
        return `<tr><td>${esc(a.label)}</td><td class="mono">${esc(a.server)}</td><td>${tlMs(a.ms)}</td><td class="${a.status === "ok" ? "" : "tl-lossy"} mono">${answer}</td></tr>`;
      }).join("");
      out.push(`<div class="tl-scroll"><table class="tl-table tl-copy"><thead><tr><th>Asked</th><th>Server</th><th>Time</th><th>Answer</th></tr></thead><tbody>${rows}</tbody></table></div>`);
      out.push(`<div class="${x.compare.agree ? "tl-ok" : "tl-find"} tl-copy">${esc(x.compare.summary)}</div>`);
    }
    return out.join("");
  },

  http(d) {
    const x = d.data;
    const steps = x.steps.map(s => {
      const code = s.status == null ? "—" : s.status;
      const cls = s.error ? "tl-lossy" : s.status >= 400 ? "tl-lossy" : s.status >= 300 ? "tl-warn" : "tl-good";
      return `<tr><td class="mono tl-url">${esc(s.url)}</td><td class="${cls}">${esc(code + (s.reason ? " " + s.reason : ""))}</td><td>${tlMs(s.dnsMs)}</td><td>${tlMs(s.connectMs)}</td><td>${tlMs(s.tlsMs)}</td><td>${tlMs(s.firstByteMs)}</td><td>${s.totalMs} ms</td></tr>${s.error ? `<tr><td colspan="7" class="tl-lossy">${esc(s.error)}</td></tr>` : ""}`;
    }).join("");
    const c = x.cert;
    const cert = c ? `<div class="${c.trusted ? "tl-ok" : "tl-find"} tl-copy">Certificate for ${esc(c.subject || "?")}, issued by ${esc(c.issuer || "?")}: ${c.trusted ? "valid" : "not trusted"}${c.problem ? ` (${esc(c.problem)})` : ""}, ${c.daysLeft >= 0 ? `${c.daysLeft} days left` : `expired ${-c.daysLeft} days ago`} (${esc(new Date(c.notAfter).toLocaleDateString([], { year: "numeric", month: "short", day: "numeric" }))}).</div>` : "";
    return `<div class="tl-rhead tl-copy"><b>${esc(x.url)}</b> <span class="${x.ok ? "tl-good" : "tl-lossy"}">${esc(x.summary)}</span></div>` +
      `<div class="tl-scroll"><table class="tl-table tl-copy"><thead><tr><th>Address</th><th>Answer</th><th>DNS</th><th>Connect</th><th>TLS</th><th>First byte</th><th>Total</th></tr></thead><tbody>${steps}</tbody></table></div>` + cert;
  },

  port(d) {
    const x = d.data, open = new Map(x.open.map(p => [p.port, p.service]));
    const cells = x.tested.map(p => open.has(p) ? `<span class="tl-pc open" title="open">${p}${open.get(p) ? " " + esc(open.get(p)) : ""}</span>` : `<span class="tl-pc">${p}</span>`).join("");
    return tlHead(d, x.summary) + `<div class="tl-ports tl-copy">${cells}</div><div class="tl-dimline">Checked in ${x.ms} ms. A port that doesn't answer is closed or blocked by a firewall; BAMF can't tell which.</div>`;
  },
};

// ---- why is it slow? ----

const TL_GLYPH = { ok: "✓", slow: "!", fail: "✕", skipped: "–" };
async function tlWhy() {
  tlStopPing();
  if (tlRunning) { toast("Another tool is still running"); return; }
  tlRunning = true;
  for (const b of $("tlButtons").querySelectorAll("button")) b.classList.remove("on");
  const token = ++tlToken;
  $("tlResult").innerHTML = `<div class="tl-wait">Checking your router, your DNS server, the internet and a web name…</div>`;
  try {
    const r = await fetch("/api/tools/diagnose", { method: "POST" });
    const d = await r.json().catch(() => ({}));
    if (token !== tlToken) return;
    if (!r.ok) { $("tlResult").innerHTML = `<div class="tl-bad">${esc(d.error || `It didn't run (HTTP ${r.status}).`)}</div>`; return; }
    const steps = d.steps.map(s => `<div class="tl-step tl-copy"><span class="tl-g ${s.status}">${TL_GLYPH[s.status] || ""}</span><span class="tl-step-l">${esc(s.label)}</span><span class="mono tl-step-t">${esc(s.target)}</span><span class="tl-step-d">${esc(s.detail)}</span></div>`).join("");
    $("tlResult").innerHTML = steps + `<div class="tl-verdict ${d.level} tl-copy">${esc(d.verdict)}</div>` + tlActions();
    tlWireActions(tlWhy);
    const mem = tlRecent().filter(x => x.tool !== "why");
    mem.unshift({ tool: "why", text: "Why is it slow?", summary: d.verdict.length > 70 ? d.verdict.slice(0, 67) + "…" : d.verdict, at: new Date().toISOString() });
    tlRemember(mem); renderToolsRecent();
  } catch { if (token === tlToken) $("tlResult").innerHTML = `<div class="tl-bad">Couldn't reach BAMF.</div>`; }
  finally { tlRunning = false; }
}

// ---- wiring ----

$("tlButtons").onclick = e => { const b = e.target.closest("button[data-tool]"); if (b) tlRun(b.dataset.tool); };
$("tlQuick").onclick = e => { const b = e.target.closest("button[data-t]"); if (b) { $("tlTarget").value = b.dataset.t; tlRun(); } };
$("tlTarget").addEventListener("keydown", e => { if (e.key === "Enter") tlRun(); });
$("tlPorts").addEventListener("keydown", e => { if (e.key === "Enter") tlRun(); });
$("tlWhy").onclick = tlWhy;
$("tlRecent").onclick = e => {
  const b = e.target.closest("button[data-i]");
  if (!b) return;
  const r = tlRecent()[Number(b.dataset.i)];
  if (!r) return;
  if (r.tool === "why") { tlWhy(); return; }
  $("tlTarget").value = r.text;
  tlRun(r.tool);
};
$("tlClear").onclick = () => { tlRemember([]); renderToolsRecent(); renderToolsQuick(); };

// From a device's ⋯ menu: open the tab on that device and run the tool.
function openTools(h, kind) {
  $("tlTarget").value = dispName(h) !== "—" ? dispName(h) : h.ip;
  if (!tlMatch($("tlTarget").value)) $("tlTarget").value = h.ip;
  tlPick(kind);
  showView("tools");
  tlRun(kind);
}

// ---- Settings: whether a typed address is allowed, and whether the tools are offered at all ----

$("traceAnywhereEnabled").onchange = async () => {
  const enabled = $("traceAnywhereEnabled").checked;
  try {
    const r = await fetch("/api/settings/trace-anywhere", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    traceAnywhereEnabled = enabled;
    tlCaps = null;
    toast(enabled ? "The Tools tab accepts any address" : "The Tools tab is back to your own devices");
  } catch { $("traceAnywhereEnabled").checked = !enabled; toast("Couldn't save that"); }
};

$("networkToolsEnabled").onchange = async () => {
  const enabled = $("networkToolsEnabled").checked;
  try {
    const r = await fetch("/api/settings/network-tools", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    networkToolsEnabled = enabled;
    tlCaps = null;
    renderToolsTab();
    toast(enabled ? "The Tools tab is on" : "The Tools tab is off");
  } catch { $("networkToolsEnabled").checked = !enabled; toast("Couldn't save that"); }
};
