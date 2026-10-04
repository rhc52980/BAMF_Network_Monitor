// ---- theme + comic splat ----
const SPLAT_WORDS = ["BAMF!"];

function currentTheme() {
  return document.documentElement.getAttribute("data-theme") || "dark";
}

function showSplat() {
  const word = SPLAT_WORDS[Math.floor(Math.random() * SPLAT_WORDS.length)];
  const hue = [ ["#ffb454","#0c3a22"], ["#3fdb7f","#0c3a22"], ["#6db7d8","#10141a"], ["#f2716f","#2a0f0f"] ][Math.floor(Math.random()*4)];
  const rot = (Math.random() * 16 - 8).toFixed(1);
  // 12-point starburst
  let pts = "";
  const cx = 250, cy = 200;
  for (let i = 0; i < 24; i++) {
    const r = i % 2 === 0 ? 200 : 120;
    const a = (Math.PI * 2 * i) / 24 - Math.PI / 2;
    pts += `${(cx + r * Math.cos(a)).toFixed(0)},${(cy + r * Math.sin(a)).toFixed(0)} `;
  }
  const el = $("splat");
  el.innerHTML = `
    <svg viewBox="0 0 500 400" style="transform:rotate(${rot}deg)">
      <g class="burst-group">
        <polygon points="${pts}" fill="${hue[0]}" stroke="${hue[1]}" stroke-width="8" stroke-linejoin="round"/>
        <text x="${cx}" y="${cy}" text-anchor="middle" dominant-baseline="central"
              font-family="Arial Black, Arial, sans-serif" font-weight="900"
              font-size="${word.length > 5 ? 66 : 92}" fill="${hue[1]}"
              stroke="#fff" stroke-width="1.5" paint-order="stroke">${word}</text>
      </g>
    </svg>`;
  el.classList.remove("fade");
  el.classList.add("show");
  clearTimeout(el._t1); clearTimeout(el._t2);
  el._t1 = setTimeout(() => el.classList.add("fade"), 650);
  el._t2 = setTimeout(() => { el.classList.remove("show", "fade"); }, 1050);
}

// Dark, Light and High Contrast are built in and can't be removed. Every other
// theme is a folder in the server's themes folder, listed from /api/themes and
// added here as the list arrives; see "themes from folders" below.
const THEMES = [
  { id: "dark",        name: "Dark",         sw: ["#10141a", "#3fdb7f", "#ffb454"] },
  { id: "light",       name: "Light",        sw: ["#eef1f5", "#1ba85a", "#d67d00"] },
  { id: "contrast",    name: "High Contrast",sw: ["#000000", "#00ff00", "#ffff00"] },
];
// The menu groups themes so a list this long can be read at a glance. A theme
// with something moving behind the dashboard is "Animated", which is also the
// honest warning for anyone who'd rather things held still. A theme says which
// it is in its theme.json; one that doesn't goes under More.
const THEME_CATS = [
  { key: "plain",     name: "Colours",   title: "Colour schemes: nothing moves." },
  { key: "animated",  name: "Animated",  title: "Something moves behind the dashboard. Reduced motion settles them all." },
  { key: "holiday",   name: "Holidays",  title: "Seasonal. Holiday Spirit puts these on by date." },
  { key: "installed", name: "More",      title: "Themes added to the themes folder that don't say what kind they are." },
];
const themeCat = t => t.cat || (t.dropIn ? "installed" : "plain");
function secretUnlocked() { try { return localStorage.getItem("bamf-secret") === "1"; } catch { return false; } }

let themeMenuOpen = false;

function buildThemeMenu() {
  let menu = $("themeMenu");
  if (!menu) {
    menu = document.createElement("div");
    menu.id = "themeMenu";
    menu.className = "theme-menu";
    document.body.appendChild(menu);
  }
  menu.innerHTML = "";
  const cur = currentTheme();
  const unlocked = secretUnlocked();
  for (const cat of THEME_CATS) {
    const list = THEMES.filter(t => (!t.secret || unlocked) && themeCat(t) === cat.key);
    if (!list.length) continue;
    const h = document.createElement("div");
    h.className = "theme-head";
    h.textContent = cat.name;
    if (cat.title) h.title = cat.title;
    menu.appendChild(h);
    for (const t of list) {
      const item = document.createElement("button");
      item.className = "theme-item" + (t.id === cur ? " active" : "");
      item.innerHTML = `
        <span class="sw">${t.sw.map(c => `<i style="background:${esc(c)}"></i>`).join("")}</span>
        <span class="tn">${esc(t.name)}</span>`;
      item.onclick = () => {
        pickThemeFromMenu(t.id);
        showSplat();
        closeThemeMenu();
      };
      menu.appendChild(item);
    }
  }
  // The way to choose which themes the menu offers.
  const manage = document.createElement("button");
  manage.className = "theme-item theme-manage";
  manage.textContent = "Add or remove themes…";
  manage.onclick = () => {
    closeThemeMenu();
    showView("settings/appearance");
    setTimeout(() => $("themesCard")?.scrollIntoView({ block: "start" }), 50);
  };
  menu.appendChild(manage);
  // Under the button that opened it, with its right edge on the button's, and
  // kept inside the screen on a narrow one. On a phone the header wraps and the
  // button is at the left, so lining the right edges up would push the menu off
  // the left side: its right offset is capped so its left edge stays 8px in.
  const r = (themeAnchor || $("themeToggle")).getBoundingClientRect();
  const vw = document.documentElement.clientWidth;
  const w = Math.min(300, vw - 16);                // the menu's width, as the stylesheet sets it
  menu.style.top = (r.bottom + 8) + "px";
  menu.style.right = Math.max(8, Math.min(vw - r.right, vw - w - 8)) + "px";
}

// ---- Settings → Appearance → Themes ----
// Every theme BAMF comes with and every one in the themes folder, with its
// picture: Add puts it in the folder, Remove takes it out. The server does the
// copying, so every dashboard gets the same list.
let themeLibBusy = false;
async function renderThemeLib() {
  const box = $("themeLib");
  if (!box) return;
  let data;
  try {
    const r = await fetch("/api/themes/library", { cache: "no-store" });
    if (!r.ok) throw new Error("HTTP " + r.status);
    data = await r.json();
  } catch (e) { box.innerHTML = `<p class="modal-help">Couldn't read the themes: ${esc(e.message)}</p>`; return; }
  $("themesDir").textContent = data.folder || "the themes folder";
  const unlocked = secretUnlocked();
  const list = data.themes.filter(t => !t.secret || unlocked || t.installed && !t.library);
  const cats = [["colours", "Colours"], ["animated", "Animated"], ["holiday", "Holidays"], ["", "More"]];
  const inCat = (t, c) => c ? t.category === c : !["colours", "animated", "holiday"].includes(t.category);
  const cur = currentTheme();
  const tile = t => {
    const state = !t.installed ? "Not installed" : !t.library ? "Added by hand" : t.stock ? "" : "Edited here: updates leave it alone";
    const also = t.variants.length ? ` <span class="tl-also">with ${t.variants.map(v => esc(v.name)).join(", ")}</span>` : "";
    const using = [t.id, ...t.variants.map(v => v.id)].includes(cur);
    return `<div class="tl-tile${t.installed ? "" : " off"}" data-id="${esc(t.id)}">
      <div class="tl-pic">${t.preview ? `<img loading="lazy" alt="" src="${esc(t.preview)}">` : `<span class="tl-sw">${t.swatch.map(c => `<i style="background:${esc(c)}"></i>`).join("")}</span>`}</div>
      <div class="tl-body">
        <div class="tl-name">${esc(t.name)}${also}</div>
        ${t.description ? `<div class="tl-desc">${esc(t.description)}</div>` : ""}
        ${state || using ? `<div class="tl-state">${using ? "In use in this browser" + (state ? " · " : "") : ""}${esc(state)}</div>` : ""}
      </div>
      <button type="button" class="export-btn tl-btn" data-act="${t.installed ? "remove" : "install"}" data-id="${esc(t.id)}">${t.installed ? "Remove" : "Add"}</button>
    </div>`;
  };
  box.innerHTML = cats.map(([c, name]) => {
    const ts = list.filter(t => inCat(t, c)).sort((a, b) => a.name.localeCompare(b.name));
    return ts.length ? `<div class="tl-head">${name} <span class="set-hint">${ts.filter(t => t.installed).length} of ${ts.length} installed</span></div><div class="tl-grid">${ts.map(tile).join("")}</div>` : "";
  }).join("");
  const missing = list.filter(t => !t.installed && t.library);
  $("themeAddAll").hidden = !missing.length;
  $("themeAddAll").textContent = `Add all ${missing.length}`;
  $("themeAddAll").onclick = () => themeLibAct(missing.map(t => ["install", t.id]));
  const n = list.filter(t => t.installed).length;
  $("themeLibStatus").textContent = `${n} installed, plus the three built in.` +
    (data.problems.length ? " " + data.problems.map(p => `${p.file} wasn't installed: ${p.problem}`).join(" ") : "");
  box.querySelectorAll(".tl-btn").forEach(b => b.onclick = () => themeLibAct([[b.dataset.act, b.dataset.id]]));
}
async function themeLibAct(steps) {
  if (themeLibBusy) return;
  themeLibBusy = true;
  try {
    for (const [act, id] of steps) {
      const r = await fetch(`/api/themes/${encodeURIComponent(id)}/${act}`, { method: "POST" });
      if (!r.ok) throw new Error((await r.json().catch(() => null))?.error || ("HTTP " + r.status));
    }
    await loadDropInList(true);
    // A theme this browser was wearing that's gone: back to Dark.
    if (!builtInTheme(currentTheme()) && !THEMES.some(t => t.id === currentTheme())) applyTheme("dark");
    renderNightRow();
    await renderThemeLib();
  } catch (e) { toast("Couldn't change the themes: " + e.message); }
  finally { themeLibBusy = false; }
}
$("themeUploadBtn").onclick = () => $("themeUpload").click();
$("themeUpload").onchange = async e => {
  const f = e.target.files[0];
  e.target.value = "";
  if (!f) return;
  try {
    const r = await fetch("/api/themes/upload?name=" + encodeURIComponent(f.name), { method: "POST", headers: { "Content-Type": "application/zip" }, body: f });
    const j = await r.json().catch(() => null);
    if (!r.ok) throw new Error(j?.error || ("HTTP " + r.status));
    await loadDropInList(true);
    await renderThemeLib();
    toast(`Added ${j.ids.length === 1 ? "a theme" : j.ids.length + " themes"}: pick it from the theme menu.`);
  } catch (err) { toast(err.message); }
};

let themeAnchor = null;
function openThemeMenu(anchor) { themeAnchor = anchor || null; loadDropInList(); buildThemeMenu(); $("themeMenu").classList.add("show"); themeMenuOpen = true; }
function closeThemeMenu() { const m = $("themeMenu"); if (m) m.classList.remove("show"); themeMenuOpen = false; }

$("themeToggle").onclick = (e) => {
  e.stopPropagation();
  themeMenuOpen ? closeThemeMenu() : openThemeMenu();
};
// Settings → Appearance → Theme: the same list, under its own button.
$("setThemeOpen").onclick = (e) => {
  e.stopPropagation();
  themeMenuOpen ? closeThemeMenu() : openThemeMenu($("setThemeOpen"));
};
// Over to the devices, where the scene can be seen, and an intruder for it.
$("setIntruderTry").onclick = () => {
  if (!festiveHooks.intruder) { toast("This theme has no intruder scene. The animated and holiday ones do: try Harbour, Goat, City Lights or Halloween, then try again."); return; }
  showView("devices");
  setTimeout(() => { if (!showIntruder()) toast("There's no device to borrow for the test yet."); }, 500);
};
document.addEventListener("click", (e) => {
  if (themeMenuOpen && !e.target.closest("#themeMenu") && e.target.id !== "themeToggle")
    closeThemeMenu();
});

function applyTheme(theme, { holiday = false } = {}) {
  document.documentElement.setAttribute("data-theme", theme);
  const t = THEMES.find(x => x.id === theme);
  $("themeIcon").textContent = "\u25d1"; // half-filled circle = "theme"
  $("themeToggle").title = "Theme: " + (t ? t.name : theme) + " — click to change";
  $("setThemeStatus").textContent = "Now: " + (t ? t.name : theme) + (holiday ? ", for the holiday" : "");
  // localStorage is what actually persists: per browser, across tabs and
  // restarts. window.name only ever survived a reload of the same tab, which
  // is why the theme kept snapping back to dark. Keep writing it as a fallback
  // for a browser that blocks storage (private mode, strict site settings).
  // A theme put on by Holiday Spirit isn't this browser's own choice, so it
  // isn't saved: the saved one comes back when the season ends.
  if (!holiday) {
    try { localStorage.setItem("bamf-theme", theme); } catch {}
    try { window.name = "bamf-theme:" + theme; } catch {}
  }
  const prev = festivePrev;
  themeSheets(theme);
  festive(theme);
  redrawMapForTheme();
  // A theme from the themes folder loads its files the first time it's
  // picked; its effects start once they have. If its folder has gone, fall
  // back to this browser's own theme, or to Dark.
  if (!builtInTheme(theme) && !THEME_FX[theme]) {
    ensureDropIn(theme).then(ok => {
      if (currentTheme() !== theme) return;
      if (!ok) {
        let own = null;
        try { own = localStorage.getItem("bamf-theme"); } catch {}
        applyTheme(holiday && own && own !== theme ? own : "dark", { holiday: true });
        return;
      }
      themeSheets(theme);
      festivePrev = prev;
      festive(theme);
      redrawMapForTheme();
    });
  }
}
// ---- Holiday Spirit ----
// With it on, every dashboard wears Halloween through October, Thanksgiving
// for the week of the holiday, Christmas from December 1st to 25th and New
// Year from Boxing Day to January 2nd, by this browser's date, then goes back
// to its own theme. Checked on every poll, so an open dashboard changes over
// at midnight. Picking another theme from the menu during a season keeps that
// pick in this browser until the next season.
// Thanksgiving is the fourth Thursday of November, so it moves every year.
// The theme runs from the Monday of that week to the Sunday after it.
function thanksgivingDay(year) {
  const first = new Date(year, 10, 1);
  return 1 + ((11 - first.getDay()) % 7) + 21; // first Thursday, then three weeks
}
function holidaySeason(d = new Date()) {
  const m = d.getMonth(), day = d.getDate();
  if (m === 9) return "halloween";
  if (m === 10) {
    const t = thanksgivingDay(d.getFullYear());
    if (day >= t - 3 && day <= t + 3) return "thanksgiving";
    return null;
  }
  if (m === 11) return day <= 25 ? "christmas" : "newyear";
  if (m === 0 && day <= 2) return "newyear";
  return null;
}
const seasonKey = season => `${season}-${new Date().getFullYear()}`;
let holidayShowing = null;
// A theme that isn't installed can't be put on. Until the list has arrived,
// assume it is: applyTheme falls back if it turns out not to be.
const themeThere = id => !dropInReady || THEMES.some(t => t.id === id);
function applyHolidaySpirit() {
  const season = holidaySpirit && themeThere(holidaySeason()) ? holidaySeason() : null;
  let kept = null;
  try { kept = localStorage.getItem("bamf-holiday-kept"); } catch {}
  if (season && kept !== seasonKey(season)) {
    holidayShowing = season;
    if (currentTheme() !== season) applyTheme(season, { holiday: true });
  } else if (holidayShowing) {
    // The season is over, or Holiday Spirit was switched off: back to this browser's own theme.
    holidayShowing = null;
    let own = null;
    try { own = localStorage.getItem("bamf-theme"); } catch {}
    applyTheme(own || "dark", { holiday: true });
  }
  renderHolidayToggle();
}
function pickThemeFromMenu(id) {
  const season = holidaySpirit ? holidaySeason() : null;
  if (season && id === season) {
    // Back to the season's theme: Holiday Spirit takes over again.
    try { localStorage.removeItem("bamf-holiday-kept"); } catch {}
    holidayShowing = season;
    applyTheme(id, { holiday: true });
    return;
  }
  if (season) {
    try { localStorage.setItem("bamf-holiday-kept", seasonKey(season)); } catch {}
    holidayShowing = null;
  }
  if (isNight()) {
    if (id === nightMode.theme) {
      // Back to the night theme: Night mode takes over again.
      try { localStorage.removeItem("bamf-night-kept"); } catch {}
      nightShowing = true;
      applyTheme(id, { holiday: true });
      renderNightRow();
      return;
    }
    // Another theme tonight: this browser keeps it until the next night.
    try { localStorage.setItem("bamf-night-kept", nightKey()); } catch {}
    nightShowing = false;
  }
  applyTheme(id);
  renderNightRow();
}
function renderHolidayToggle() {
  const t = $("setHoliday");
  if (!t) return;
  setToggleState(t, holidaySpirit);
  const season = holidaySeason();
  const name = { halloween: "Halloween", thanksgiving: "Thanksgiving", christmas: "Christmas", newyear: "New Year" }[season];
  const until = { halloween: "November 1st", thanksgiving: "the Monday after", christmas: "December 26th", newyear: "January 3rd" }[season];
  $("setHolidayStatus").textContent = !holidaySpirit ? ""
    : season && !themeThere(season) ? `It's ${name}, but the ${name} theme isn't installed, so every screen keeps its own. Add it back under Themes below.`
    : season ? (holidayShowing ? `It's ${name}: the ${name} theme is on until ${until}.`
      : `It's ${name}, but this browser picked its own theme for the season.`)
    : "Next up: " + nextHoliday();
  t.title = holidaySpirit ? "Holiday Spirit is on - click to turn off" : "Click to turn on Holiday Spirit";
}
// Whichever season comes round next, by this browser's date.
function nextHoliday(d = new Date()) {
  const y = d.getFullYear(), t = thanksgivingDay(y);
  const when = [
    ["Halloween", new Date(y, 9, 1), "October 1st"],
    ["Thanksgiving", new Date(y, 10, t - 3), `November ${t - 3}`],
    ["Christmas", new Date(y, 11, 1), "December 1st"],
    ["New Year", new Date(y, 11, 26), "December 26th"],
  ].find(([, start]) => start > d);
  return when ? `${when[0]} on ${when[2]}.` : `Halloween on October 1st, ${y + 1}.`;
}

// ---- Night mode ----
// Between two clock times, by this browser's clock, the dashboard wears the
// night theme, then goes back to its own theme in the morning. Checked on
// every poll and once a minute. Picking another theme from the menu during
// the night keeps that pick in this browser until the next night. Holiday
// Spirit's season wins over it.
const clockMins = t => { const [h, m] = String(t || "").split(":").map(Number); return ((h || 0) * 60 + (m || 0)) % 1440; };
function isNight(d = new Date()) {
  if (!nightMode.enabled) return false;
  const now = d.getHours() * 60 + d.getMinutes(), from = clockMins(nightMode.from), to = clockMins(nightMode.to);
  return from > to ? (now >= from || now < to) : (now >= from && now < to);
}
// The night's name: the date it began on, so a theme kept for tonight ends at
// the next dusk rather than at midnight.
function nightKey(d = new Date()) {
  const now = d.getHours() * 60 + d.getMinutes(), from = clockMins(nightMode.from), to = clockMins(nightMode.to);
  const start = new Date(d);
  if (from > to && now < to) start.setDate(start.getDate() - 1);
  return `${start.getFullYear()}-${start.getMonth() + 1}-${start.getDate()}`;
}
const fmtClock = t => { const d = new Date(); d.setHours(...String(t).split(":").map(Number), 0, 0); return d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }); };
let nightShowing = false;
function applyNightMode() {
  if (holidayShowing) { renderNightRow(); return; }
  const night = isNight() && themeThere(nightMode.theme);
  let kept = null;
  try { kept = localStorage.getItem("bamf-night-kept"); } catch {}
  if (night && kept !== nightKey()) {
    nightShowing = true;
    if (currentTheme() !== nightMode.theme) applyTheme(nightMode.theme, { holiday: true });
  } else if (nightShowing && (!night || kept === nightKey())) {
    // Morning, or Night mode was switched off: back to this browser's own theme.
    nightShowing = false;
    let own = null;
    try { own = localStorage.getItem("bamf-theme"); } catch {}
    if (currentTheme() !== (own || "dark")) applyTheme(own || "dark", { holiday: true });
  }
  renderNightRow();
}
function renderNightRow() {
  const t = $("setNight");
  if (!t) return;
  setToggleState(t, nightMode.enabled);
  t.title = nightMode.enabled ? "Night mode is on - click to turn off" : "Click to turn on Night mode";
  const sel = $("nightTheme");
  if (document.activeElement !== sel) {
    const opts = THEMES.filter(x => !x.secret || secretUnlocked() || x.id === nightMode.theme)
      .map(x => `<option value="${esc(x.id)}"${x.id === nightMode.theme ? " selected" : ""}>${esc(x.name)}</option>`);
    if (!THEMES.some(x => x.id === nightMode.theme)) opts.push(`<option value="${esc(nightMode.theme)}" selected>${esc(nightMode.theme)}</option>`);
    sel.innerHTML = opts.join("");
  }
  if (document.activeElement !== $("nightFrom")) $("nightFrom").value = nightMode.from;
  if (document.activeElement !== $("nightTo")) $("nightTo").value = nightMode.to;
  $("nightHours").style.display = nightMode.enabled ? "" : "none";
  const name = THEMES.find(x => x.id === nightMode.theme)?.name || nightMode.theme;
  $("setNightStatus").textContent = !nightMode.enabled ? ""
    : !themeThere(nightMode.theme) ? `The night theme (${nightMode.theme}) isn't installed, so the night keeps each screen's own theme. Pick another, or add it back under Themes below.`
    : holidayShowing ? "Holiday Spirit has the theme until its season ends."
    : isNight() ? (nightShowing ? `It's night: ${name} is on until ${fmtClock(nightMode.to)}.` : "It's night, but this browser picked its own theme tonight.")
    : `${name} comes on at ${fmtClock(nightMode.from)}, by each screen's own clock.`;
}
async function saveNight(patch) {
  const next = { ...nightMode, ...patch };
  try {
    const r = await fetch("/api/settings/night", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(next),
    });
    if (!r.ok) throw new Error((await r.json().catch(() => null))?.error || ("HTTP " + r.status));
    nightMode = next;
    try { localStorage.setItem("bamf-night", JSON.stringify(nightMode)); localStorage.removeItem("bamf-night-kept"); } catch {}
    applyNightMode();
    return true;
  } catch (e) { console.error(e); toast("Couldn't save Night mode: " + e.message); renderNightRow(); return false; }
}

// Map decorations are drawn with the map, so a theme change redraws it.
function redrawMapForTheme() {
  mapSig = null;
  if (loadedOnce && view === "map") renderMap();
}

// ---- holiday decorations ----
// Drawn in a fixed layer over the page that never takes a click. Christmas:
// multicoloured bulbs along the top and down both sides, twinkling at random
// with the odd bright sparkle, ornaments swinging from the top string, and a
// little snow. Halloween: cobwebs, a spider on its thread, flickering
// jack-o'-lanterns, and now and then a bat or a ghost. With reduced motion
// asked for, it all holds still and nothing new flies in.
let festiveTimers = [];
let festiveTheme = null;
// What the current theme does when something happens on the network: a new
// device appears (newDevice) or a scan finishes (scanDone).
let festiveHooks = {};
// Anything else to stop when the theme changes (an animation frame loop).
let festiveStops = [];
let festivePrev = null;
// What each theme does, by id. Every theme with effects is in a folder, and its
// theme.js adds itself here with BAMF.registerTheme when it loads.
const THEME_FX = {};
const calmMotion = () => { try { return matchMedia("(prefers-reduced-motion: reduce)").matches; } catch { return false; } };
const rnd = (a, b) => a + Math.random() * (b - a);

function festive(theme) {
  festiveTimers.forEach(t => { clearInterval(t); clearTimeout(t); });
  festiveTimers = [];
  festiveStops.forEach(f => f());
  festiveStops = [];
  $("festive")?.remove();
  $("themeBg")?.remove();
  festiveHooks = {};
  const switched = festivePrev !== null && festivePrev !== theme;
  festivePrev = theme;
  festiveTheme = THEME_FX[theme] ? theme : null;
  if (!festiveTheme) return;
  const root = document.createElement("div");
  root.id = "festive";
  root.setAttribute("aria-hidden", "true");
  document.body.appendChild(root);
  THEME_FX[festiveTheme](root, switched);
}
// The lights are laid out for the window's width and height; redo them when it changes.
let festiveResize = null;
window.addEventListener("resize", () => {
  if (!festiveTheme) return;
  clearTimeout(festiveResize);
  festiveResize = setTimeout(() => festive(festiveTheme), 250);
});

// ---- Harbour and Ant Farm share these ----
// The devices a scene is built from: the ones the table shows, oldest first,
// so a device keeps its place and a new one is added at the end.
function sceneHosts() {
  return hosts.filter(h => !h.ignored && !h.forgotten && !h.remote)
    .sort((a, b) => String(a.firstSeen).localeCompare(String(b.firstSeen)) || a.id - b.id);
}
// Online and known, online and unknown, offline: the same three as the table.
const sceneState = h => !h.online ? "off" : h.known ? "up" : "un";
function sceneHash(s) {
  let h = 2166136261;
  for (const ch of String(s)) { h ^= ch.charCodeAt(0); h = Math.imul(h, 16777619); }
  return h >>> 0;
}
// A canvas the size of a layer, for drawing the parts that don't move once.
function sceneLayer(w, h) {
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const cv = document.createElement("canvas");
  cv.width = Math.max(1, Math.round(w * dpr)); cv.height = Math.max(1, Math.round(h * dpr));
  const g = cv.getContext("2d");
  g.scale(dpr, dpr);
  return { cv, g };
}
const easeInOut = t => t < .5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;

// ---- intruders ----
// To a theme with a scene for it, a new device that hasn't been marked known is
// an intruder: the scene raises the alarm when one turns up, then holds it, in
// view, until it's marked known, ignored or forgotten, or its days on the New
// tab run out. So what a scene holds is the New tab: every dashboard holds the
// same ones, a reload doesn't lose them, and marking a device known anywhere
// stands the scene down. Show me an intruder, in Settings, borrows a real
// device for a test that stands down on its own.
let intruderTest = null;           // { host, until }
const INTRUDER_TEST_MS = 25e3;
function intruders() {
  const list = hosts.filter(h => isNew(h) && !h.ignored && !h.forgotten && !h.remote)
    .sort((a, b) => String(a.firstSeen).localeCompare(String(b.firstSeen)) || a.id - b.id);
  if (intruderTest && Date.now() >= intruderTest.until) intruderTest = null;
  if (intruderTest && !list.some(h => h.id === intruderTest.host.id)) list.push({ ...intruderTest.host, test: true });
  return list;
}
// For a scene: call it to see what it's holding now, what's been added since
// it last looked, and what's been cleared.
function intruderWatch() {
  let held = new Map();
  return () => {
    const now = intruders();
    const ids = new Set(now.map(h => h.id));
    const added = now.filter(h => !held.has(h.id));
    const cleared = [...held.values()].filter(h => !ids.has(h.id));
    held = new Map(now.map(h => [h.id, h]));
    return { held: now, added, cleared };
  };
}
// The tag a scene hangs on an intruder, the same in every theme: what it is, in
// the watchtower's red while it's held and green once it's cleared. x is the
// tag's centre (or its left or right edge, by align), y its bottom. k fades it.
function intruderText(h, state) {
  const title = state === "cleared" ? "CLEARED · STANDING DOWN" : (h.test ? "TEST · " : "") + (state === "alarm" ? "INTRUDER ALERT" : "INTRUDER · HELD");
  const name = nameOrIp(h);
  let sub = [name !== h.ip ? h.ip : null, h.vendor].filter(Boolean).join("  ·  ");
  if (sub.length > 44) sub = sub.slice(0, 43) + "…";
  return { title, name, sub };
}
function intruderTag(g, x, y, h, { s = 1, align = "center", state = "held", k = 1 } = {}) {
  if (k <= 0) return;
  const clear = state === "cleared";
  const { title, name, sub } = intruderText(h, state);
  g.save();
  g.globalAlpha *= Math.min(1, k);
  const f1 = `700 ${9 * s}px "IBM Plex Mono", monospace`, f2 = `600 ${12 * s}px "Space Grotesk", sans-serif`, f3 = `500 ${9.5 * s}px "IBM Plex Mono", monospace`;
  g.font = f1; const w1 = g.measureText(title).width;
  g.font = f2; const w2 = g.measureText(name).width;
  g.font = f3; const w3 = sub ? g.measureText(sub).width : 0;
  const w = Math.max(w1, w2, w3) + 16 * s, hh = (sub ? 47 : 34) * s;
  const x0 = align === "left" ? x : align === "right" ? x - w : x - w / 2, y0 = y - hh;
  const edge = clear ? "79,224,160" : "255,77,61";
  g.fillStyle = "rgba(10,7,9,.88)"; g.fillRect(x0, y0, w, hh);
  g.fillStyle = `rgb(${edge})`; g.fillRect(x0, y0, 3 * s, hh);
  g.strokeStyle = `rgba(${edge},.85)`; g.lineWidth = Math.max(1, s); g.strokeRect(x0 + .5, y0 + .5, w - 1, hh - 1);
  g.textBaseline = "alphabetic"; g.textAlign = "left";
  g.font = f1; g.fillStyle = clear ? "#7df0bf" : "#ff8a7e"; g.fillText(title, x0 + 9 * s, y0 + 13 * s);
  g.font = f2; g.fillStyle = "#ffffff"; g.fillText(name, x0 + 9 * s, y0 + 28 * s);
  if (sub) { g.font = f3; g.fillStyle = "#c9d3dd"; g.fillText(sub, x0 + 9 * s, y0 + 41 * s); }
  g.restore();
}
// The same tag as an element, for a scene made of elements. Call it again with
// the element to change its state.
function intruderTagEl(h, state = "held", el = null) {
  el = el || document.createElement("div");
  const { title, name, sub } = intruderText(h, state);
  // Keeps any class the theme gave it, for where it sits.
  el.classList.add("intruder-tag");
  el.classList.toggle("cleared", state === "cleared");
  el.innerHTML = `<b>${esc(title)}</b><span>${esc(name)}</span>${sub ? `<small>${esc(sub)}</small>` : ""}`;
  return el;
}
// Settings → Appearance: a test intruder, on a device that isn't one already.
function showIntruder() {
  if (!festiveHooks.intruder) return false;
  const pool = hosts.filter(h => !h.ignored && !h.forgotten && !h.remote && !isNew(h));
  const h = pool.find(x => x.online && !x.known) || pool.find(x => x.online) || pool[0];
  if (!h) return false;
  intruderTest = { host: h, until: Date.now() + INTRUDER_TEST_MS };
  festiveHooks.intruder({ ...h, test: true });
  festiveTimers.push(setTimeout(() => festiveHooks.rendered?.(), INTRUDER_TEST_MS + 100));
  return true;
}

// ---- Waterworks and Aquarium ----
// Waterworks: water shimmering through a steel main under the header and the
// Map's pipes (still where a device is offline), side pipes, the odd drip, a
// tank in the header filling with how much of the network is up, water-meter
// counts that roll, and a surge when a scan finishes. A new device opens a
// valve and hangs a tag, an offline device's row springs a leak, and watched
// devices carry a red shut-off valve that closes when they drop.
// Aquarium: fish, bubbles, weed and gravel behind the glass. A new device
// swims in as a fish, a scan sends up bubbles, offline devices sink, and
// watched devices have a pet fish that swims beside their name.
function waterCommon(root) {
  // Rows of offline devices, and a count change on the stats, for either theme.
  const stats = ["statTotal", "statOnline", "statUnknown"];
  const seen = {};
  return {
    changedStats() {
      const out = [];
      for (const id of stats) {
        const el = $(id); if (!el) continue;
        if (seen[id] !== undefined && seen[id] !== el.textContent) out.push(el);
        seen[id] = el.textContent;
      }
      return out;
    },
    below: () => (document.querySelector("header")?.getBoundingClientRect().bottom || 60),
  };
}

// Sounds for the water themes, made in the browser like the goats' and the
// storm's: a drip, a valve's squeak, the surge of a main, bubbles, and the
// pump's hum. Off until the speaker button is clicked.
let waterAudio = null;
function waterCtx() {
  try { waterAudio = waterAudio || new (window.AudioContext || window.webkitAudioContext)(); } catch { return null; }
  if (waterAudio.state === "suspended") waterAudio.resume?.().catch(() => {});
  return waterAudio;
}
function noiseBuffer(ctx, secs, brown) {
  const buf = ctx.createBuffer(1, Math.floor(ctx.sampleRate * secs), ctx.sampleRate), d = buf.getChannelData(0);
  let last = 0;
  for (let i = 0; i < d.length; i++) { const w = Math.random() * 2 - 1; if (brown) { last = (last + .02 * w) / 1.02; d[i] = last * 3.5; } else d[i] = w; }
  return buf;
}

// ---- themes from folders ----
// Every theme but Dark, Light and High Contrast lives in its own folder on the
// server, <install>/themes/<id>, with a theme.json (name, swatch, category,
// description), a theme.css, a theme.js and a preview picture; a folder can
// hold a second theme that shares its files, like Goat Night beside Goat. The
// menu lists them from /api/themes; picking one loads its files once. Its
// script registers itself with BAMF.registerTheme(id, build), and build gets a
// small context: the effects layer, a way to put something behind the page,
// timers that stop on a theme change, and the events the themes use.
const DROPIN_ID = /^[a-z0-9][a-z0-9-]{0,39}$/;
const builtInTheme = id => THEMES.some(t => t.id === id && !t.dropIn);
const THEME_CAT_OF = { colours: "plain", animated: "animated", holiday: "holiday" };
let dropInList = null, dropInReady = false;
// fresh: after a theme is added or removed, read the list again.
function loadDropInList(fresh = false) {
  if (fresh) dropInList = null;
  if (!dropInList) dropInList = fetch("/api/themes", { cache: "no-store" })
    .then(r => r.ok ? r.json() : [])
    .then(list => {
      const add = list.filter(t => DROPIN_ID.test(t.id) && !builtInTheme(t.id)).map(t => ({
        id: t.id, folder: DROPIN_ID.test(t.folder || "") ? t.folder : t.id, name: t.name,
        sw: (t.swatch && t.swatch.length ? t.swatch : ["#888", "#aaa", "#ccc"]), dropIn: true, css: t.css, js: t.js,
        cat: THEME_CAT_OF[t.category] || "installed", secret: !!t.secret, description: t.description || "",
      })).sort((a, b) => a.name.localeCompare(b.name));
      // After the built-in three, in name order; the menu groups them.
      THEMES.splice(0, THEMES.length, ...THEMES.filter(t => !t.dropIn), ...add);
      dropInReady = true;
      if (themeMenuOpen) buildThemeMenu();
      return add;
    })
    .catch(() => []);
  return dropInList;
}
// Each folder's files load once, whichever of its themes asks first.
const dropInLoaded = {};
function ensureDropIn(id) {
  return loadDropInList().then(() => {
    const t = THEMES.find(x => x.id === id && x.dropIn);
    if (!t) return false;
    const f = t.folder;
    if (!dropInLoaded[f]) dropInLoaded[f] = (async () => {
      const load = (tag, attrs, key) => new Promise(res => {
        const el = document.createElement(tag);
        Object.assign(el, attrs);
        if (key) el.dataset.themeCss = key;
        el.onload = () => res(true);
        el.onerror = () => { el.remove(); res(false); };
        document.head.appendChild(el);
      });
      // The head may have loaded the stylesheet already, before the first paint.
      const early = [...document.querySelectorAll("link[data-theme-css]")].find(l => (l.dataset.themeCss === f || l.dataset.themeCss === id) && l.sheet);
      if (t.css && !early && !await load("link", { rel: "stylesheet", href: `/themes/${f}/theme.css` }, f)) return false;
      if (t.js && !await load("script", { src: `/themes/${f}/theme.js` })) return false;
      return true;
    })();
    return dropInLoaded[f];
  });
}
// A theme's stylesheet stays in the page once it has loaded, but only the
// current theme's is switched on, so two themes' rules never meet.
function themeSheets(theme) {
  const t = THEMES.find(x => x.id === theme);
  const mine = new Set([theme, t?.folder].filter(Boolean));
  document.querySelectorAll("link[data-theme-css]").forEach(l => { l.disabled = !mine.has(l.dataset.themeCss); });
  // The phone's status bar, or an installed app's title bar, in the theme's colour.
  const bg = getComputedStyle(document.documentElement).getPropertyValue("--bg").trim();
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta && bg) meta.content = bg;
}
// What a theme's build(ctx) gets to work with.
function themeContext(root, switched) {
  return {
    root, switched, calm: calmMotion(),
    // Put an element behind the whole page (only one per theme).
    background(el) { el.id = "themeBg"; el.classList.add("theme-bg"); el.setAttribute("aria-hidden", "true"); document.body.prepend(el); return el; },
    later(fn, ms) { const t = setTimeout(fn, ms); festiveTimers.push(t); return t; },
    every(fn, ms) { const t = setInterval(fn, ms); festiveTimers.push(t); return t; },
    onStop(fn) { festiveStops.push(fn); },
    // "newDevice"(host), "scanDone"(), "rendered"(), "netChange"(offIds, backIds),
    // "decorateNode"(g, node): add SVG to a Map node as it's drawn,
    // "intruder"(host): a new device that isn't known yet; raise the alarm.
    on(event, fn) { festiveHooks[event] = fn; },
    // The intruders to hold (the New tab's devices), a watcher that says what
    // changed since it last looked, and the tag to hang on one.
    intruders, intruderWatch, intruderTag, intruderTagEl,
    hosts: () => hosts, view: () => view,
    headerBottom: () => document.querySelector("header")?.getBoundingClientRect().bottom || 60,
    // Devices that went offline since the last time this was asked.
    wentOffline() { const ids = wentOffIds; wentOffIds = []; return ids; },
    svg: svgEl, rnd, pick, esc, nameOrIp,
  };
}
window.BAMF = Object.freeze({
  registerTheme(id, build) {
    if (!DROPIN_ID.test(id) || builtInTheme(id) || typeof build !== "function") return false;
    THEME_FX[id] = (root, switched) => build(themeContext(root, switched));
    return true;
  },
});

// ---- for the themes ----
// Hosts changed since the last poll, for the Matrix row glitch: gone offline
// or moved address. Set by refresh(), used once by the next render.
let glitchIds = [];
// Devices that just went offline, for the Thunderstorm's flickering rows.
let wentOffIds = [];

const pick = s => s[Math.floor(Math.random() * s.length)];

// Thunderstorm: rain behind the page, forked lightning with a soft flash of
// the sky and a rumble a moment later, drifting clouds, and splashes along
// the bottom. The storm follows the network: every device that goes offline
// makes it heavier and the lightning more frequent, and it eases as they
// come back. The lightning never flashes the screen:
// one soft brightening, at most every 20 seconds, and none at all under
// reduced motion.
// A speaker button beside the theme button, for a theme that makes sound.
// Off until clicked, remembered in this browser under its own key. Returns
// whether sound is on now.
function themeSoundButton(key, onTitle, offTitle, onChange) {
  let on = false;
  try { on = localStorage.getItem(key) === "1"; } catch {}
  const b = document.createElement("button");
  b.type = "button";
  b.className = "arp-toggle goat-sound";
  const show = () => {
    b.textContent = on ? "🔊" : "🔇";
    b.title = on ? onTitle : offTitle;
    b.setAttribute("aria-pressed", String(on));
  };
  show();
  b.onclick = () => {
    on = !on;
    try { localStorage.setItem(key, on ? "1" : "0"); } catch {}
    show();
    onChange(on);
  };
  $("themeToggle")?.before(b);
  festiveStops.push(() => b.remove());
  return () => on;
}

// The three seasons share their plumbing: a canvas behind the dashboard, a
// frame loop that stops with the tab, and a still picture under reduced
// motion. This hands back the canvas and a run/stop pair so each one only has
// to say what it draws.
// Anything along the bottom of the screen would be hidden behind the device
// table on the back canvas, so it gets a canvas of its own in front of the
// page, the way the Aquarium's crab and the Thanksgiving turkey do.
function frontCanvas(root) {
  const el = document.createElement("canvas");
  el.className = "theme-front";
  el.setAttribute("aria-hidden", "true");
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  el.width = W * dpr; el.height = H * dpr;
  el.style.cssText = "position:fixed;inset:0;width:100vw;height:100vh;pointer-events:none";
  const ctx = el.getContext("2d");
  ctx.scale(dpr, dpr);
  ctx.globalAlpha = .9;
  root.appendChild(el);
  return ctx;
}

function seasonCanvas(draw, step) {
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = "<canvas></canvas>";
  document.body.prepend(bg);
  const cv = bg.querySelector("canvas"), ctx = cv.getContext("2d");
  const W = innerWidth, H = innerHeight, dpr = Math.min(devicePixelRatio || 1, 2);
  cv.width = W * dpr; cv.height = H * dpr;
  ctx.scale(dpr, dpr);
  return { ctx, W, H, start() {
    let raf = 0, last = 0;
    const frame = now => {
      raf = requestAnimationFrame(frame);
      if (now - last < 33) return;
      const dt = Math.min(.05, (now - last) / 1000);
      last = now;
      step(dt, now);
      draw(now);
    };
    const run = () => { if (!raf) { last = performance.now(); raf = requestAnimationFrame(frame); } };
    const stop = () => { cancelAnimationFrame(raf); raf = 0; };
    const vis = () => document.hidden ? stop() : run();
    document.addEventListener("visibilitychange", vis);
    run();
    festiveStops.push(() => { stop(); document.removeEventListener("visibilitychange", vis); });
  } };
}

// A floor along the bottom of the window, in front of the page: a strip of
// ground the scene can put things on without them landing on a row of the
// table. It's opaque, and the page gets that much more room at the bottom (a
// spacer, not padding: the body is the height of the window and the page
// overflows it), so the last rows still scroll clear of it.
function floorBand(root, px) {
  const W = innerWidth, dpr = Math.min(devicePixelRatio || 1, 2);
  const el = document.createElement("canvas");
  el.className = "theme-floor";
  el.setAttribute("aria-hidden", "true");
  el.width = W * dpr; el.height = px * dpr;
  el.style.cssText = `position:fixed;left:0;bottom:0;width:100vw;height:${px}px;pointer-events:none`;
  const ctx = el.getContext("2d");
  ctx.scale(dpr, dpr);
  root.appendChild(el);
  const room = document.createElement("div");
  room.style.height = `${px}px`;
  room.setAttribute("aria-hidden", "true");
  document.body.appendChild(room);
  festiveStops.push(() => room.remove());
  return { ctx, W, H: px };
}

const MOON = `<svg viewBox="0 0 46 46"><circle cx="23" cy="23" r="21" fill="#f3ecc8"/>
  <circle cx="15" cy="17" r="4" fill="#e2d9ae"/><circle cx="29" cy="28" r="5.5" fill="#e2d9ae"/><circle cx="27" cy="13" r="2.5" fill="#e2d9ae"/></svg>`;

// ---- the secret ----
// Type B-A-M-F anywhere that isn't a text box, or tap the logo five times
// quickly (for phones), and the holiday themes unlock.
function revealSecret() {
  const first = !secretUnlocked();
  try { localStorage.setItem("bamf-secret", "1"); } catch {}
  $("secretEmoji").textContent = first ? "🎁" : ["🎄", "🎃", "🦇", "⭐", "👻"][Math.floor(Math.random() * 5)];
  $("secretTitle").textContent = first ? "You found the secret!" : "Still a secret 🤫";
  $("secretText").textContent = first
    ? "BAMF unlocked two holiday themes. They're in the theme menu from now on, under Holidays."
    : "The holiday themes are already yours. Pick one:";
  // Only the holiday themes that are installed.
  const picks = [...document.querySelectorAll("[data-secret]")];
  picks.forEach(b => { b.hidden = !themeThere(b.dataset.secret); });
  if (!picks.some(b => !b.hidden)) $("secretText").textContent += " They aren't installed here, though: add them under Settings → Appearance → Themes.";
  $("secretModal").hidden = false;
  if (first) showSplat();
}
function closeSecret() { $("secretModal").hidden = true; }
let secretTyped = "";
document.addEventListener("keydown", e => {
  if (e.ctrlKey || e.metaKey || e.altKey || !e.key || e.key.length !== 1) return;
  const t = e.target;
  if (t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.tagName === "SELECT" || t.isContentEditable)) return;
  secretTyped = (secretTyped + e.key.toLowerCase()).slice(-4);
  if (secretTyped === "bamf") { secretTyped = ""; revealSecret(); }
});
let logoTaps = [];
document.querySelector(".brand h1").addEventListener("click", () => {
  const now = Date.now();
  logoTaps = logoTaps.filter(t => now - t < 2500).concat(now);
  if (logoTaps.length >= 5) { logoTaps = []; revealSecret(); }
});
$("secretClose").onclick = closeSecret;
$("secretModal").onclick = e => { if (e.target.id === "secretModal") closeSecret(); };
document.querySelectorAll("[data-secret]").forEach(b => b.onclick = () => {
  applyTheme(b.dataset.secret);
  closeSecret();
  showSplat();
});

// Row density: a class on <html> so the head script can apply it before the
// first paint, the same way the theme is handled.
function renderDensityToggle() {
  const on = document.documentElement.classList.contains("compact");
  const t = $("setCompact");
  if (t) setToggleState(t, on);
}

$("setCompact").onclick = () => {
  const on = !document.documentElement.classList.contains("compact");
  document.documentElement.classList.toggle("compact", on);
  try { localStorage.setItem("bamf-density", on ? "compact" : "comfortable"); } catch {}
  renderDensityToggle();
};
renderDensityToggle();

// Restore the saved theme: localStorage first (survives restarts and new
// tabs), then window.name (same-tab reload only) for anyone who chose a theme
// before storage was used, then dark. Each read is guarded separately so a
// browser that throws on storage access still gets a theme.
(function initTheme() {
  // A name that isn't built in may be a theme from the themes folder;
  // applyTheme checks, and falls back to Dark if there's no such folder.
  const valid = t => THEMES.some(x => x.id === t && (!x.secret || secretUnlocked())) || (DROPIN_ID.test(t) && !builtInTheme(t));
  let saved = null;
  try {
    const t = localStorage.getItem("bamf-theme");
    if (t && valid(t)) saved = t;
  } catch {}
  if (!saved) {
    try {
      if (window.name && window.name.startsWith("bamf-theme:")) {
        const t = window.name.split(":")[1];
        if (valid(t)) saved = t;
      }
    } catch {}
  }
  applyTheme(saved || "dark");
  applyHolidaySpirit();
  applyNightMode();
  // The clock moves on its own, so look once a minute even between polls.
  setInterval(applyNightMode, 60e3);
})();

$("setHoliday").onclick = async () => {
  const next = !holidaySpirit;
  try {
    const r = await fetch("/api/settings/holiday-spirit", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: next }),
    });
    if (!r.ok) throw new Error("HTTP " + r.status);
    holidaySpirit = next;
    try { localStorage.setItem("bamf-holiday-spirit", next ? "1" : "0"); localStorage.removeItem("bamf-holiday-kept"); } catch {}
    applyHolidaySpirit();
    renderHolidayToggle();
    toast(next ? "Holiday Spirit is on: Halloween through October, Christmas from December 1st to 25th"
      : "Holiday Spirit is off: dashboards keep their own theme all year");
  } catch (e) { console.error(e); toast("Couldn't save Holiday Spirit - see the server log"); }
};

$("setNight").onclick = async () => {
  if (await saveNight({ enabled: !nightMode.enabled })) {
    const name = THEMES.find(x => x.id === nightMode.theme)?.name || nightMode.theme;
    toast(nightMode.enabled ? `Night mode is on: ${name} from ${fmtClock(nightMode.from)} to ${fmtClock(nightMode.to)}`
      : "Night mode is off: dashboards keep their own theme all night");
  }
};
$("nightFrom").onchange = e => { if (e.target.value) saveNight({ from: e.target.value }); };
$("nightTo").onchange = e => { if (e.target.value) saveNight({ to: e.target.value }); };
$("nightTheme").onchange = e => saveNight({ theme: e.target.value });
// Once the list of installed themes is in, the season and the night can be
// checked against it.
loadDropInList().then(() => { applyHolidaySpirit(); applyNightMode(); });
