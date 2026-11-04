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
    const here = encodeURIComponent(location.pathname);
    Promise.all([
      fetch("/api/v1/downloads").then(r => r.ok ? r.json() : Promise.reject()),
      fetch("/api/portal/session", { credentials: "same-origin" }).then(r => r.ok ? r.json() : { signedIn: false }).catch(() => ({ signedIn: false }))
    ])
      .then(([files, session]) => {
        if (!files.length) throw new Error("none");
        files.sort((a, b) => (a.product === b.product ? 0 : a.product === "workforce" ? -1 : 1) || b.platform.localeCompare(a.platform));
        const gate = session.signedIn ? "" : `
          <div class="notice dl-gate">
            <p><strong>Downloads are for ZonoSoft account holders.</strong> Creating an account is free and takes a minute. The app needs a licence key, or email ZonoSoft@outlook.com for a free demo key to try it first.</p>
            <p><a class="btn" href="/account/register?next=${here}">Create a free account</a> <a class="btn quiet" href="/portal?next=${here}">Sign in</a></p>
          </div>`;
        const button = f => session.signedIn
          ? `<a class="btn small-btn" href="${esc(f.url)}" download>${/\.exe$|\.msi$/i.test(f.name) ? "Download installer" : "Download ." + f.name.split(".").pop()} (${mb(f.bytes)})</a>`
          : `<span class="small">${mb(f.bytes)}<br>Sign in to download</span>`;
        list.innerHTML = gate + `<table class="dl-table"><thead><tr><th>App</th><th>For</th><th>Version</th><th></th></tr></thead><tbody>${files.map(f => `
          <tr>
            <td><strong>${app(f.product)}</strong><br>${esc(f.platform)}</td>
            <td>${who(f.product)}<div class="hash">SHA-256: ${esc(f.sha256)}</div></td>
            <td>${esc(f.version)}<div class="small">${day(f.uploadedUtc)}</div></td>
            <td>${button(f)}</td>
          </tr>`).join("")}</tbody></table>`;
      })
      .catch(() => {
        list.innerHTML = `<p class="notice">The downloads are being updated. Please try again shortly, or email <a href="mailto:ZonoSoft@outlook.com">ZonoSoft@outlook.com</a> and we'll send you a link.</p>`;
      });
  }
})();

// Create account (website): makes the ZonoSoft account and signs in to it
(() => {
  const form = document.getElementById("register");
  if (!form) return;
  const $ = id => document.getElementById(id);
  const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  form.addEventListener("input", e => e.target.removeAttribute("aria-invalid"));
  let userTouched = false;
  $("rUser").addEventListener("input", () => { userTouched = true; });
  $("rEmail").addEventListener("input", () => { if (!userTouched) $("rUser").value = $("rEmail").value.trim(); });

  const fail = (message, field) => {
    $("rError").textContent = message; $("rError").hidden = false;
    form.querySelectorAll("[aria-invalid]").forEach(el => el.removeAttribute("aria-invalid"));
    if (field) { field.setAttribute("aria-invalid", "true"); field.focus(); }
  };
  form.addEventListener("submit", async e => {
    e.preventDefault();
    $("rError").hidden = true;
    const required = [["rBusiness", "Enter the business name."], ["rEmail", "Enter your email address."], ["rUser", "Choose a username."], ["rPass", "Choose a password."]];
    for (const [id, msg] of required) if (!$(id).value.trim()) return fail(msg, $(id));
    if ($("rPass").value.length < 10) return fail("The password needs at least 10 characters.", $("rPass"));
    if ($("rPass").value !== $("rPass2").value) return fail("The two passwords don't match.", $("rPass2"));
    if (!$("rTerms").checked) return fail("Please agree to the terms and privacy notice.", $("rTerms"));
    $("rGo").disabled = true; $("rGo").textContent = "Creating your account…";
    try {
      const r = await fetch("/api/portal/register", {
        method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ businessName: $("rBusiness").value, contactName: $("rContact").value, email: $("rEmail").value, username: $("rUser").value, password: $("rPass").value, acceptTerms: true })
      });
      let j = {}; try { j = await r.json(); } catch {}
      if (r.ok && j.ok) {
        const n = new URLSearchParams(location.search).get("next") || "";
        location.href = /^\/(?![\/\\])[A-Za-z0-9/_\-.]*$/.test(n) ? n : "/portal";
        return;
      }
      fail(r.status === 429 ? "Too many tries. Wait a minute and try again." : (j.message || "The account couldn't be created. Try again."));
    } catch {
      fail("Couldn't reach ZonoSoft. Check your internet connection and try again.");
    } finally {
      $("rGo").disabled = false; $("rGo").textContent = "Create account";
    }
  });
})();
