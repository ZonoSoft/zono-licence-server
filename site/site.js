// ZonoSoft website: menu on phones, the price calculator, and the download list.
(() => {
  const $ = (s, el = document) => el.querySelector(s);

  // Menu
  const toggle = $(".menu-toggle"), nav = $("#nav");
  if (toggle && nav) {
    toggle.addEventListener("click", () => {
      const open = nav.classList.toggle("open");
      toggle.setAttribute("aria-expanded", String(open));
      toggle.textContent = open ? "Close" : "Menu";
    });
    nav.addEventListener("click", e => { if (e.target.closest("a")) { nav.classList.remove("open"); toggle.setAttribute("aria-expanded", "false"); toggle.textContent = "Menu"; } });
  }

  // Price calculator: £12/month or £120/year, 5 team members included, +£2/month or +£20/year each
  const calc = $("#calc");
  if (calc) {
    let seats = 5;
    const render = () => {
      const yearly = calc.querySelector("input[name=plan]:checked").value === "yearly";
      const extra = Math.max(0, seats - 5);
      const total = yearly ? 120 + 20 * extra : 12 + 2 * extra;
      $("#seats").textContent = seats;
      $("#total").innerHTML = `£${total} <small>a ${yearly ? "year" : "month"}</small>`;
      $("#totalNote").textContent = extra === 0
        ? `5 team members included. Each extra person is £${yearly ? "20 a year" : "2 a month"}.`
        : `5 included + ${extra} extra at £${yearly ? "20 a year" : "2 a month"} each.`;
      calc.querySelector("[data-step='-1']").disabled = seats <= 5;
    };
    calc.addEventListener("click", e => {
      const b = e.target.closest("[data-step]");
      if (!b) return;
      seats = Math.min(500, Math.max(5, seats + Number(b.dataset.step)));
      render();
    });
    calc.addEventListener("change", render);
    render();
  }

  // Downloads: the newest of each app and platform on the server
  const list = $("#downloads");
  if (list) {
    const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const mb = b => (b / 1048576).toFixed(0) + " MB";
    const day = s => new Date(s).toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric" });
    const app = p => p === "team" ? "Zono Team" : "Zono Workforce";
    const who = p => p === "team" ? "For team members: clock in, shifts and holiday" : "For managers: rota, payroll, staff and settings";
    fetch("/api/v1/downloads").then(r => r.ok ? r.json() : Promise.reject())
      .then(files => {
        if (!files.length) throw new Error("none");
        files.sort((a, b) => (a.product === b.product ? 0 : a.product === "workforce" ? -1 : 1) || b.platform.localeCompare(a.platform));
        list.innerHTML = `<table class="dl-table"><thead><tr><th>App</th><th>For</th><th>Version</th><th></th></tr></thead><tbody>${files.map(f => `
          <tr>
            <td><strong>${app(f.product)}</strong><br>${esc(f.platform)}</td>
            <td>${who(f.product)}<div class="hash">SHA-256: ${esc(f.sha256)}</div></td>
            <td>${esc(f.version)}<div class="small">${day(f.uploadedUtc)}</div></td>
            <td><a class="btn small-btn" href="${esc(f.url)}" download>Download ${f.platform === "Android" ? ".apk" : ".zip"} (${mb(f.bytes)})</a></td>
          </tr>`).join("")}</tbody></table>`;
      })
      .catch(() => {
        list.innerHTML = `<p class="notice">The downloads are being updated. Please try again shortly, or email <a href="mailto:ZonoSoft@outlook.com">ZonoSoft@outlook.com</a> and we'll send you a link.</p>`;
      });
  }
})();
