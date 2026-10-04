// Saved views of the device list: a name for a combination of tab, network, status, device type, tag and search.
// Views ▾ lists them; picking one sets the list to that combination, and Save this view… keeps the current one.
// They're kept on the server, so every dashboard has the same ones.

let savedViews = [];
let viewsOpen = false;
const VIEW_NO_GUESS = "__none__";   // a view's way of saying "devices BAMF has no guess for"

// What the list is showing right now, as a view without a name.
function currentViewState() {
  return {
    tab: view === "forgotten" ? "forgotten" : "devices",
    network,
    status: filter,
    guess: guessFilter === null ? "" : guessFilter === NO_GUESS ? VIEW_NO_GUESS : guessFilter,
    tag: tagFilter || "",
    query,
  };
}

// A short, readable line for what a view shows: "Offline · 192.168.30.0/24 · tag kids".
function viewSummary(v) {
  const bits = [];
  if (v.tab === "forgotten") bits.push("Forgotten");
  if (v.status && v.status !== "all") bits.push(v.status[0].toUpperCase() + v.status.slice(1));
  if (v.network && v.network !== "all") bits.push(v.network);
  if (v.guess) bits.push(v.guess === VIEW_NO_GUESS ? "no guess" : v.guess);
  if (v.tag) bits.push("tag " + v.tag);
  if (v.query) bits.push(`“${v.query}”`);
  return bits.join(" · ") || "Everything";
}

function applyView(v) {
  network = v.network || "all";
  filter = v.status || "all";
  guessFilter = !v.guess ? null : v.guess === VIEW_NO_GUESS ? NO_GUESS : v.guess;
  tagFilter = v.tag || null;
  query = v.query || "";
  $("search").value = query;
  document.querySelectorAll(".tab").forEach(x => x.classList.toggle("active", x.dataset.filter === filter));
  showView(v.tab === "forgotten" ? "forgotten" : "devices");   // renders
}

async function loadViews() {
  try { const r = await fetch("/api/views"); if (r.ok) { savedViews = (await r.json()).views || []; renderViewsMenu(); } } catch { /* keep what we have */ }
}

function renderViewsMenu() {
  const menu = $("viewsMenu");
  if (!menu) return;
  menu.innerHTML = "";
  for (const v of savedViews) {
    const row = document.createElement("div");
    row.className = "view-item";
    const go = document.createElement("button");
    go.type = "button"; go.className = "view-go";
    go.innerHTML = `<b></b><span></span>`;
    go.children[0].textContent = v.name;
    go.children[1].textContent = viewSummary(v);
    go.onclick = () => { closeViewsMenu(); applyView(v); };
    row.appendChild(go);
    if (role !== "viewer") {
      const del = document.createElement("button");
      del.type = "button"; del.className = "view-del"; del.textContent = "×"; del.title = `Delete the view "${v.name}"`;
      del.onclick = e => { e.stopPropagation(); deleteView(v); };
      row.appendChild(del);
    }
    menu.appendChild(row);
  }
  if (!savedViews.length) {
    const none = document.createElement("div");
    none.className = "view-none";
    none.textContent = "No saved views yet. Set the list up the way you want, then save it.";
    menu.appendChild(none);
  }
  if (role !== "viewer") {
    const save = document.createElement("button");
    save.type = "button"; save.className = "view-save"; save.textContent = "Save this view…";
    save.onclick = () => { closeViewsMenu(); saveCurrentView(); };
    menu.appendChild(save);
  }
}

function closeViewsMenu() { viewsOpen = false; $("viewsMenu").classList.remove("show"); }
$("viewsBtn").onclick = e => {
  e.stopPropagation();
  viewsOpen = !viewsOpen;
  if (viewsOpen) { toolsOpen = false; $("toolsMenu").classList.remove("show"); loadViews(); }
  $("viewsMenu").classList.toggle("show", viewsOpen);
};
document.addEventListener("click", e => { if (viewsOpen && !e.target.closest("#viewsWrap")) closeViewsMenu(); });

async function saveCurrentView() {
  const state = currentViewState();
  const name = (prompt(`Name this view (${viewSummary(state)}):`, "") || "").trim();
  if (!name) return;
  if (savedViews.some(v => v.name.toLowerCase() === name.toLowerCase()) && !confirm(`Replace the view "${name}"?`)) return;
  try {
    const r = await fetch("/api/views", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name, ...state }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || `Couldn't save it (HTTP ${r.status})`)); return; }
    savedViews = d.views || [];
    renderViewsMenu();
    toast(`Saved the view “${esc(name)}”`);
  } catch { toast("Couldn't reach BAMF"); }
}

async function deleteView(v) {
  if (!confirm(`Delete the view "${v.name}"?`)) return;
  try {
    const r = await fetch("/api/views/delete", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: v.name }) });
    if (!r.ok) { toast("Couldn't delete it"); return; }
    savedViews = (await r.json()).views || [];
    renderViewsMenu();
  } catch { toast("Couldn't reach BAMF"); }
}

loadViews();
