// Synthwave: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Synthwave: a sun going down behind a neon grid, all behind the page.
function buildSynthwave() {
  const bg = document.createElement("div");
  bg.id = "themeBg";
  bg.className = "theme-bg sw-bg";
  bg.setAttribute("aria-hidden", "true");
  bg.innerHTML = `<div class="sw-sun"></div><div class="sw-horizon"></div><div class="sw-grid"></div>`;
  document.body.prepend(bg);
}

BAMF.registerTheme("synthwave", ctx => buildSynthwave(ctx.root, ctx.switched));
})();
