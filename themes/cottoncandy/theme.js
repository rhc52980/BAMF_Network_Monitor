// Cotton Candy: a BAMF theme. It runs in the dashboard's page, and uses the
// dashboard's own helpers, so it's kept in step with the BAMF it ships with.
(() => {
// Cotton Candy: a few soft bubbles drifting up.
function buildCottonCandy(root) {
  if (calmMotion()) return;
  const bubble = () => {
    if (document.hidden || root.querySelectorAll(".cc-bubble").length >= 7) return;
    const b = document.createElement("div");
    b.className = "cc-bubble";
    const size = rnd(18, 56);
    b.style.cssText = `left:${rnd(2, 96).toFixed(1)}vw;width:${size.toFixed(0)}px;height:${size.toFixed(0)}px;` +
      `animation-duration:${rnd(14, 24).toFixed(1)}s;--sway:${rnd(-60, 60).toFixed(0)}px`;
    b.addEventListener("animationend", () => b.remove());
    root.appendChild(b);
  };
  bubble();
  festiveTimers.push(setInterval(bubble, 4200));
}

BAMF.registerTheme("cottoncandy", ctx => buildCottonCandy(ctx.root, ctx.switched));
})();
