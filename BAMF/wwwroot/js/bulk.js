// Select mode for the device list: tick several devices and do one thing to all of them. Select, beside the filters,
// turns it on; a bar along the bottom then offers the same choices a device's own ⋯ menu has. What's selected is
// what the list is showing: change the filter or the network and the ticks that are no longer in view fall away.

let bulkEnabled = true;      // Select mode is offered (Settings can switch it off)
let selectMode = false;
const selected = new Set();  // device ids
let lastTicked = null;       // for shift-click ranges

// What "all" means: every device the list is showing that this server can change (a remote site's can't be).
function bulkRows() { return visible().filter(h => !h.remote); }

const BULK_VERBS = {
  known: "marked known", unknown: "marked unknown", watch: "now watched", unwatch: "no longer watched", ignore: "ignored", unignore: "no longer ignored",
  forget: "forgotten", restore: "restored", snooze: "snoozed", unsnooze: "back on alert", tag: "tagged", untag: "untagged",
};

function renderBulk() {
  renderBulkUndo();
  const off = !bulkEnabled || role === "viewer";
  const toggle = $("bulkEnabled");
  if (toggle) toggle.checked = bulkEnabled;
  const entry = $("bulkOpen");
  if (entry) { entry.hidden = off; entry.textContent = selectMode ? "Stop selecting" : "Select"; entry.setAttribute("aria-pressed", selectMode ? "true" : "false"); entry.classList.toggle("on", selectMode); }
  if (off && selectMode) { selectMode = false; selected.clear(); }
  document.body.classList.toggle("selecting", selectMode);
  const bar = $("bulkBar");
  if (!bar) return;
  bar.hidden = !selectMode || (view !== "devices" && view !== "forgotten");
  if (bar.hidden) return;
  const shown = new Set(bulkRows().map(h => h.id));
  for (const id of [...selected]) if (!shown.has(id)) selected.delete(id);
  renderBulkBar(shown.size);
}

function renderBulkBar(total) {
  if (total === undefined) total = bulkRows().length;
  const n = selected.size;
  $("bulkCount").textContent = n ? `${n} selected` : total ? `Select all ${total} shown` : "Nothing to select";
  const all = $("bulkAll");
  all.checked = total > 0 && n === total;
  all.indeterminate = n > 0 && n < total;
  const inForgotten = view === "forgotten";
  document.querySelectorAll("#bulkActs [data-bulk]").forEach(b => {
    // The Forgotten tab restores; everywhere else, forgets.
    const hide = inForgotten ? b.dataset.bulk !== "restore" : b.dataset.bulk === "restore";
    b.hidden = hide;
    b.disabled = n === 0;
  });
}

// A row's tick box, as it's built.
function bulkWireRow(tr, h) {
  const box = tr.querySelector(".sel-box");
  if (!box) return;
  box.checked = selected.has(h.id);
  tr.classList.toggle("is-selected", box.checked);
  box.onclick = e => {
    e.stopPropagation();
    if (e.shiftKey && lastTicked !== null && lastTicked !== h.id) {
      const order = [...$("tbody").querySelectorAll("tr[data-id]")].map(r => Number(r.dataset.id));
      const a = order.indexOf(lastTicked), b = order.indexOf(h.id);
      if (a >= 0 && b >= 0) for (const id of order.slice(Math.min(a, b), Math.max(a, b) + 1)) setTicked(id, box.checked);
    } else setTicked(h.id, box.checked);
    lastTicked = h.id;
    syncTicks();
  };
}
function setTicked(id, on) { if (on) selected.add(id); else selected.delete(id); }
function syncTicks() {
  document.querySelectorAll("#tbody tr[data-id]").forEach(tr => {
    const on = selected.has(Number(tr.dataset.id));
    tr.classList.toggle("is-selected", on);
    const box = tr.querySelector(".sel-box");
    if (box) box.checked = on;
  });
  renderBulkBar();
}

function endSelect() { selectMode = false; selected.clear(); lastTicked = null; renderBulk(); }
$("bulkOpen").onclick = () => {
  if (view !== "devices" && view !== "forgotten") showView("devices");
  selectMode = !selectMode;
  if (!selectMode) { selected.clear(); lastTicked = null; }
  renderBulk();
};
$("bulkDone").onclick = endSelect;
$("bulkAll").onchange = () => {
  const on = $("bulkAll").checked;
  selected.clear();
  if (on) for (const h of bulkRows()) selected.add(h.id);
  syncTicks();
};

// ---- doing it ----
// How each device stood before a change, so Undo can put it back exactly. Kept until the next change, Undo, or ten minutes.
let lastUndo = null;
const UNDO_MS = 10 * 60e3;
function hostState(h) {
  return { id: h.id, known: !!h.known, watched: !!h.watched, ignored: !!h.ignored, forgotten: !!h.forgotten, tags: [...(h.tags || [])], snoozedUntil: h.snoozedUntil || null };
}
function renderBulkUndo() {
  const b = $("bulkUndo");
  if (!b) return;
  if (lastUndo && Date.now() - lastUndo.at > UNDO_MS) lastUndo = null;
  b.hidden = !lastUndo || !selectMode;
  if (lastUndo) b.textContent = `Undo: ${lastUndo.what}`;
}
async function bulkUndo() {
  const u = lastUndo;
  if (!u) return;
  lastUndo = null;
  renderBulkUndo();
  try {
    const r = await fetch("/api/hosts/bulk/restore", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ states: u.states }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || `Couldn't undo that (HTTP ${r.status})`)); return; }
    const bad = (d.failed || []).length + (d.missing || 0);
    toast(`Put ${d.done} device${d.done === 1 ? "" : "s"} back` + (bad ? `. ${bad} couldn't be changed.` : "."));
    await refresh();
  } catch { toast("Couldn't reach BAMF"); }
}
$("bulkUndo").onclick = bulkUndo;

async function bulkDo(action, extra = {}) {
  const ids = [...selected];
  if (!ids.length) return;
  const before = ids.map(id => hosts.find(h => h.id === id)).filter(Boolean).map(hostState);
  try {
    const r = await fetch("/api/hosts/bulk", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ids, action, ...extra }) });
    const d = await r.json().catch(() => ({}));
    if (!r.ok) { toast(esc(d.error || `Couldn't do that (HTTP ${r.status})`)); return; }
    const bad = (d.failed || []).length + (d.missing || 0);
    if (!d.done) toast(`Nothing changed` + (bad ? `. ${bad} couldn't be changed${d.failed && d.failed[0] ? ": " + esc(d.failed[0].error) : ""}.` : "."));
    selected.clear(); lastTicked = null;
    if (d.done) {
      lastUndo = { states: before, what: `${d.done} device${d.done === 1 ? "" : "s"} ${BULK_VERBS[action]}`, at: Date.now() };
      // The toast carries the same Undo, for the moment right after.
      toast(`${d.done} device${d.done === 1 ? "" : "s"} ${BULK_VERBS[action]}` + (bad ? `. ${bad} couldn't be changed.` : "") + ` <button type="button" class="toast-undo" id="toastUndo">Undo</button>`);
      $("toastUndo").onclick = () => { $("toast").classList.remove("show"); bulkUndo(); };
    }
    await refresh();
  } catch (e) { console.error(e); toast("Couldn't reach BAMF"); }
}

function closeBulkModal() { $("bulkModal").hidden = true; }
function bulkModal(title, sub, fill) {
  $("bulkModalTitle").textContent = title;
  $("bulkModalSub").textContent = sub;
  const body = $("bulkModalBody");
  body.innerHTML = "";
  fill(body);
  $("bulkModal").hidden = false;
  (body.querySelector("input, button") || $("bulkModalCancel")).focus();
}
$("bulkModalCancel").onclick = closeBulkModal;
$("bulkModal").onclick = e => { if (e.target.id === "bulkModal") closeBulkModal(); };

function bulkTagDialog(action) {
  const n = selected.size, adding = action === "tag";
  bulkModal(adding ? `Add a tag to ${n} device${n === 1 ? "" : "s"}` : `Remove a tag from ${n} device${n === 1 ? "" : "s"}`,
    adding ? "Type a tag, or click one already in use." : "Type the tag to remove, or click one already in use.", body => {
      const input = document.createElement("input");
      input.className = "modal-input"; input.maxLength = 24; input.placeholder = "kids, IoT, guest…"; input.autocomplete = "off";
      const go = tag => { tag = tag.trim(); if (!tag) return; closeBulkModal(); bulkDo(action, { tag }); };
      input.onkeydown = e => { if (e.key === "Enter") go(input.value); };
      const row = document.createElement("div"); row.className = "snooze-picks"; row.style.marginTop = "10px";
      for (const t of allTags()) {
        const b = document.createElement("button");
        b.type = "button"; b.className = "toggle"; b.textContent = t; b.onclick = () => go(t);
        row.appendChild(b);
      }
      const ok = document.createElement("button");
      ok.type = "button"; ok.className = "export-btn"; ok.style.marginTop = "10px"; ok.textContent = adding ? "Add" : "Remove"; ok.onclick = () => go(input.value);
      body.append(input, row, ok);
    });
}

function bulkSnoozeDialog() {
  const n = selected.size;
  const morning = new Date(); if (morning.getHours() >= 8) morning.setDate(morning.getDate() + 1);
  morning.setHours(8, 0, 0, 0);
  const picks = [["30 minutes", 30], ["1 hour", 60], ["2 hours", 120], ["8 hours", 480], ["1 day", 1440],
    ["Until " + snoozeClock(morning.toISOString()), Math.ceil((morning - Date.now()) / 60e3)]];
  bulkModal(`Snooze ${n} device${n === 1 ? "" : "s"}`, "Their alerts are held back for as long as you pick.", body => {
    const box = document.createElement("div"); box.className = "snooze-picks";
    for (const [label, minutes] of picks) {
      const b = document.createElement("button");
      b.type = "button"; b.className = "toggle"; b.textContent = label;
      b.onclick = () => { closeBulkModal(); bulkDo("snooze", { minutes }); };
      box.appendChild(b);
    }
    const end = document.createElement("button");
    end.type = "button"; end.className = "toggle"; end.style.marginTop = "10px"; end.textContent = "End their snoozes";
    end.onclick = () => { closeBulkModal(); bulkDo("unsnooze"); };
    body.append(box, end);
  });
}

document.querySelectorAll("#bulkActs [data-bulk]").forEach(b => b.onclick = () => {
  const action = b.dataset.bulk, n = selected.size;
  if (!n) return;
  if (action === "tag" || action === "untag") return bulkTagDialog(action);
  if (action === "snooze") return bulkSnoozeDialog();
  if (action === "forget" && !confirm(`Send ${n} device${n === 1 ? "" : "s"} to the Forgotten tab? They can be restored from there.`)) return;
  bulkDo(action);
});

// Settings: whether Select mode is offered at all.
$("bulkEnabled").onchange = async () => {
  const enabled = $("bulkEnabled").checked;
  try {
    const r = await fetch("/api/settings/bulk", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
    if (!r.ok) throw new Error();
    bulkEnabled = enabled;
    renderBulk();
    toast(enabled ? "Select is beside the filters on the Devices tab" : "Select devices is off");
  } catch { $("bulkEnabled").checked = !enabled; toast("Couldn't save that"); }
};
