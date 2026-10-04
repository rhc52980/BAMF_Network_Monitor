// The Sites card (Activity): this site and every other BAMF server being watched, side by side, so one look says
// where something is wrong. Only shown once another BAMF server has been added.

// One row per site: its devices counted, and how fresh what we know about it is. A remote's devices come from its
// last answer, so a site that has stopped answering is shown as it was, marked stale.
function sitesSummary(allHosts, remotes, localScan) {
  const count = list => {
    const live = list.filter(h => !h.ignored && !h.forgotten);
    return {
      total: live.length,
      online: live.filter(h => h.online).length,
      unknown: live.filter(h => h.online && !h.known).length,
      watchedOff: live.filter(h => h.watched && !h.online).length,
    };
  };
  const rows = [Object.assign({ name: "This site", local: true, ok: true, lastScan: localScan || null }, count(allHosts.filter(h => !h.remote)))];
  for (const r of remotes || []) {
    rows.push(Object.assign({
      name: r.name, local: false, ok: !!r.ok, error: r.error || null, version: r.version || null,
      lastScan: r.lastScan || null, fetched: r.fetchedUtc || null, net: (r.subnets && r.subnets[0]) || null,
    }, count(allHosts.filter(h => h.remote === r.name))));
  }
  return rows;
}

// What a row says is wrong with its site, if anything, in the order that matters.
function siteNote(s) {
  if (!s.ok) return s.fetched ? `Not answering. Showing what it said ${fmtAgo(s.fetched)}.` : `Not answering${s.error ? ": " + s.error : ""}.`;
  if (s.unknown) return `${s.unknown} unknown device${s.unknown === 1 ? " is" : "s are"} on it`;
  if (s.watchedOff) return `${s.watchedOff} watched device${s.watchedOff === 1 ? " is" : "s are"} offline`;
  return "";
}

function renderSites() {
  const card = $("sitesCard");
  if (!card) return;
  const rows = sitesSummary(hosts, remoteStatus, lastScan);
  card.hidden = rows.length < 2;
  if (card.hidden) return;
  const html = rows.map((s, i) => {
    const note = siteNote(s);
    const dot = !s.ok ? "down" : s.unknown || s.watchedOff ? "slow" : "up";
    const scan = s.lastScan ? `last scan ${fmtAgo(s.lastScan)}` : "no scan yet";
    return `<button type="button" class="site-row" data-i="${i}"><span class="wan-dot ${dot}"></span>` +
      `<span class="site-main"><b>${esc(s.name)}</b><span class="site-sub">${s.online} of ${s.total} online · ${esc(scan)}${s.version ? " · BAMF " + esc(s.version) : ""}</span>` +
      (note ? `<span class="site-note${s.ok ? "" : " bad"}">${esc(note)}</span>` : "") + `</span></button>`;
  }).join("");
  $("sitesBody").innerHTML = html;
  $("sitesBody").querySelectorAll(".site-row").forEach(b => b.onclick = () => {
    const s = rows[Number(b.dataset.i)];
    network = s.local ? "all" : s.net || "all";
    showView("devices");
  });
}
