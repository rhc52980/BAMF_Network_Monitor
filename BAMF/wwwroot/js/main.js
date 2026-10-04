// Switching between views, and starting the page: loaded last, so everything
// it starts is there.


// Views are addressable: /#settings opens the Settings tab, /#activity the
// feed, and so on, so a tab can be bookmarked or pinned and Back/Forward
// walk between them. The Devices view is the bare URL, no hash.
const VIEWS = ["devices", "map", "floor", "home", "activity", "forgotten", "settings"];
function showView(name, { fromHash = false } = {}) {
  // Settings takes a section after a slash: settings/internet.
  let sub = null;
  if (name && name.includes("/")) [name, sub] = name.split("/", 2);
  if (!VIEWS.includes(name)) name = "devices";
  if (name === "settings") showSetSec(sub || setSec);
  document.querySelectorAll("[data-view]").forEach(x => x.classList.toggle("active", x.dataset.view === name));
  view = name;
  if (!fromHash) {
    const want = name === "devices" ? "" : "#" + name + (name === "settings" ? "/" + setSec : "");
    if ((location.hash || "") !== want)
      history.pushState(null, "", want || location.pathname + location.search);
  }
  if (view === "settings") loadSettings();
  render();
}
document.querySelectorAll("[data-view]").forEach(b => b.onclick = () => showView(b.dataset.view));
window.addEventListener("hashchange", () => showView(location.hash.slice(1) || "devices", { fromHash: true }));
document.querySelectorAll(".tab").forEach(t => t.onclick = () => {
  document.querySelectorAll(".tab").forEach(x => x.classList.remove("active"));
  t.classList.add("active");
  filter = t.dataset.filter;
  render();
});
$("search").oninput = e => { query = e.target.value; render(); };

// Honour a view in the URL before the first paint. showView calls render(),
// which is harmless with no data yet; refresh() re-renders when data lands.
if (location.hash.length > 1) showView(location.hash.slice(1), { fromHash: true });
refresh();
checkSetup();
// Not while the page is hidden (a phone with the app in the background, a tab nobody is
// looking at): no requests and no redrawing. Coming back refreshes at once.
setInterval(() => { if (!document.hidden) refresh(); }, POLL_MS);
document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
