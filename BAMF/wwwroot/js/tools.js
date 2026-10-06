// Ping, trace route and DNS lookup for one device, from its ⋯ menu, shown in a small window. They run on the machine BAMF
// is on, so "can BAMF reach it?" has an answer without a terminal.

const TOOL_NAMES = { ping: "Ping", trace: "Trace route", path: "Path ping", dns: "DNS lookup" };
let toolHost = null, toolKind = null, toolRunning = false;

function openToolDialog(h, kind) {
  toolHost = h; toolKind = kind;
  $("toolModal").hidden = false;
  runTool();
}
function closeToolDialog() { $("toolModal").hidden = true; toolHost = null; }

async function runTool() {
  const h = toolHost, kind = toolKind;
  if (!h || toolRunning) return;
  toolRunning = true;
  const typed = h.typedTarget;
  $("toolTitle").textContent = typed ? `${TOOL_NAMES[kind]} \u2014 ${typed}` : `${TOOL_NAMES[kind]} \u2014 ${dispName(h)}`;
  $("toolSub").textContent = typed ? "to an address you typed, from the machine BAMF runs on" : `${h.ip}, from the machine BAMF runs on`;
  $("toolOut").textContent = kind === "path" ? "Tracing the route, then pinging every router on it... this can take a minute or two." : kind === "trace" ? "Tracing the route... this can take half a minute." : "Running...";
  $("toolSummary").textContent = "";
  $("toolAgain").disabled = true;
  try {
    const r = typed
      ? await fetch("/api/tools/anywhere", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tool: kind, target: typed }) })
      : await fetch(`/api/hosts/${h.id}/tool`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tool: kind }) });
    const d = await r.json().catch(() => ({}));
    if (toolHost !== h) return;     // closed while it ran
    if (!r.ok) { $("toolOut").textContent = d.error || `It didn't run (HTTP ${r.status}).`; return; }
    $("toolOut").textContent = (d.lines || []).join("\n");
    $("toolSummary").textContent = d.summary || "";
  } catch { if (toolHost === h) $("toolOut").textContent = "Couldn't reach BAMF."; }
  finally { toolRunning = false; $("toolAgain").disabled = false; }
}

// Tools -> Ping / Trace route / Path ping to an address: ask where, then show the answer in the same window.
const ANY_ASK = { ping: "Ping which address or name?", trace: "Trace the route to which address or name?", path: "Ping every hop on the way to which address or name?" };
function askAnywhere(kind) {
  toolsOpen = false; $("toolsMenu").classList.remove("show");
  const target = (prompt(ANY_ASK[kind] + " For example 8.8.8.8 or example.com:", "") || "").trim();
  if (!target) return;
  toolHost = { id: 0, ip: target, typedTarget: target }; toolKind = kind;
  $("toolModal").hidden = false;
  runTool();
}
$("pingAnyOpen").onclick = () => askAnywhere("ping");
$("traceOpen").onclick = () => askAnywhere("trace");
$("pathAnyOpen").onclick = () => askAnywhere("path");
$("toolAgain").onclick = runTool;
$("toolClose").onclick = closeToolDialog;
$("toolModal").onclick = e => { if (e.target.id === "toolModal") closeToolDialog(); };

// Settings: whether a trace to any address is offered. Off by default.
$("traceAnywhereEnabled").onchange = async () => {
  const enabled = $("traceAnywhereEnabled").checked;
  try {
    const r = await fetch("/api/settings/trace-anywhere", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    traceAnywhereEnabled = enabled;
    for (const id of ["pingAnyOpen", "traceOpen", "pathAnyOpen"]) $(id).hidden = !enabled || role === "viewer" || !networkToolsEnabled;
    toast(enabled ? "Tools can ping and trace to any address" : "Ping and trace to any address are off");
  } catch { $("traceAnywhereEnabled").checked = !enabled; toast("Couldn't save that"); }
};

// Settings: whether the tools are offered at all.
$("networkToolsEnabled").onchange = async () => {
  const enabled = $("networkToolsEnabled").checked;
  try {
    const r = await fetch("/api/settings/network-tools", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    networkToolsEnabled = enabled;
    for (const id of ["pingAnyOpen", "traceOpen", "pathAnyOpen"]) $(id).hidden = !enabled || !traceAnywhereEnabled || role === "viewer";
    toast(enabled ? "Ping, trace route and DNS lookup are in each device's menu" : "Ping, trace route and DNS lookup are off");
  } catch { $("networkToolsEnabled").checked = !enabled; toast("Couldn't save that"); }
};
