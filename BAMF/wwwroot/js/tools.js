// Ping, trace route and DNS lookup for one device, from its ⋯ menu, shown in a small window. They run on the machine BAMF
// is on, so "can BAMF reach it?" has an answer without a terminal.

const TOOL_NAMES = { ping: "Ping", trace: "Trace route", dns: "DNS lookup" };
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
  $("toolTitle").textContent = `${TOOL_NAMES[kind]} — ${dispName(h)}`;
  $("toolSub").textContent = `${h.ip}, from the machine BAMF runs on`;
  $("toolOut").textContent = kind === "trace" ? "Tracing the route… this can take half a minute." : "Running…";
  $("toolSummary").textContent = "";
  $("toolAgain").disabled = true;
  try {
    const r = await fetch(`/api/hosts/${h.id}/tool`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tool: kind }) });
    const d = await r.json().catch(() => ({}));
    if (toolHost !== h) return;     // closed while it ran
    if (!r.ok) { $("toolOut").textContent = d.error || `It didn't run (HTTP ${r.status}).`; return; }
    $("toolOut").textContent = (d.lines || []).join("\n");
    $("toolSummary").textContent = d.summary || "";
  } catch { if (toolHost === h) $("toolOut").textContent = "Couldn't reach BAMF."; }
  finally { toolRunning = false; $("toolAgain").disabled = false; }
}

$("toolAgain").onclick = runTool;
$("toolClose").onclick = closeToolDialog;
$("toolModal").onclick = e => { if (e.target.id === "toolModal") closeToolDialog(); };

// Settings: whether the tools are offered at all.
$("networkToolsEnabled").onchange = async () => {
  const enabled = $("networkToolsEnabled").checked;
  try {
    const r = await fetch("/api/settings/network-tools", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    networkToolsEnabled = enabled;
    toast(enabled ? "Ping, trace route and DNS lookup are in each device's menu" : "Ping, trace route and DNS lookup are off");
  } catch { $("networkToolsEnabled").checked = !enabled; toast("Couldn't save that"); }
};
