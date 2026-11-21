// ZonoSoft software catalogue: tabs per operating system; choosing an app opens its page
// (product information, minimum and recommended specification, setup guide, download).
// Pages: /software#windows (a tab) and /software#windows/shifenza (an app). Downloads need a ZonoSoft account.
(() => {
  const root = document.getElementById("catalog");
  if (!root) return;
  const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const mb = b => Math.max(1, Math.round(b / 1048576)) + " MB";
  const day = s => new Date(s).toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric" });
  const contact = '<a href="/contact">contact us</a>';
  let data, files = [], session = { signedIn: false };

  const load = Promise.all([
    fetch("/catalog.json").then(r => r.json()),
    fetch("/api/v1/downloads").then(r => r.ok ? r.json() : []).catch(() => []),
    fetch("/api/portal/session", { credentials: "same-origin" }).then(r => r.ok ? r.json() : { signedIn: false }).catch(() => ({ signedIn: false }))
  ]).then(([d, f, s]) => { data = d; files = f; session = s; });

  const fileFor = (product, platform) =>
    files.find(f => f.product === product.download && f.platform.toLowerCase() === platform.download.toLowerCase());

  function tabs(current) {
    return `<div class="os-tabs" role="tablist" aria-label="Operating system">${data.platforms.map(p => `
      <a role="tab" href="#${p.id}" aria-selected="${p.id === current}">${esc(p.short || p.name)}</a>`).join("")}</div>`;
  }

  function catalogue(platformId) {
    const platform = data.platforms.find(p => p.id === platformId) || data.platforms[0];
    const products = data.products.filter(p => p.platforms[platform.id]);
    const cards = products.map(p => {
      const f = fileFor(p, platform);
      return `<a class="app-card" href="#${platform.id}/${p.id}">
        <img src="${esc(p.icon)}" alt="" width="64" height="64">
        <span><strong>${esc(p.name)}</strong><span>${esc(p.tagline)}</span>
        <span class="small">${f ? `Version ${esc(f.version)} · ${mb(f.bytes)}` : "Coming soon"}</span></span>
      </a>`;
    }).join("");
    const none = `<div class="notice"><p><strong>Coming soon for ${esc(platform.name)}.</strong> Our apps aren't available for ${esc(platform.name)} yet.
      To hear when they are, ${contact}.</p></div>`;
    root.innerHTML = tabs(platform.id) + (products.length ? `<div class="app-grid">${cards}</div>` : none);
    document.title = `${platform.name} software | ZonoSoft`;
  }

  function specTable(rows) {
    return `<table class="spec-table"><tbody>${rows.map(([k, v]) => `<tr><th scope="row">${esc(k)}</th><td>${esc(v)}</td></tr>`).join("")}</tbody></table>`;
  }

  function productPage(platformId, productId) {
    const platform = data.platforms.find(p => p.id === platformId);
    const product = data.products.find(p => p.id === productId);
    const spec = product && platform && product.platforms[platform.id];
    if (!spec) return catalogue(platformId);
    const f = fileFor(product, platform);
    const here = encodeURIComponent("/software#" + platform.id + "/" + product.id);
    const download = !f
      ? `<p class="notice">The ${esc(platform.name)} version is being updated. Please try again shortly, or ${contact}.</p>`
      : session.signedIn
        ? `<p><a class="btn" href="${esc(f.url)}" download>Download ${esc(product.name)} for ${esc(platform.short || platform.name)} (${mb(f.bytes)})</a></p>
           <p class="small">Version ${esc(f.version)}, ${day(f.uploadedUtc)}.</p><p class="hash">SHA-256: ${esc(f.sha256)}</p>`
        : `<div class="notice dl-gate">
             <p><strong>Downloads are for ZonoSoft account holders.</strong> Creating an account is free and takes a minute.</p>
             <p><a class="btn" href="/account/register?next=${here}">Create a free account</a> <a class="btn quiet" href="/portal?next=${here}">Sign in</a></p>
             <p class="small">Version ${esc(f.version)} · ${mb(f.bytes)}</p>
           </div>`;
    root.innerHTML = tabs(platform.id) + `
      <p class="small"><a href="#${platform.id}">← All ${esc(platform.name)} software</a></p>
      <div class="app-head">
        <img src="${esc(product.icon)}" alt="" width="88" height="88">
        <div><h2>${esc(product.name)} <span class="small">for ${esc(platform.name)}</span></h2><p class="lead">${esc(product.tagline)}</p></div>
      </div>
      <section class="app-part" aria-labelledby="info"><h3 id="info">Product information</h3>
        ${product.info.map(p => `<p>${esc(p)}</p>`).join("")}
        <p><a href="${esc(product.learnMore)}">More about ${esc(product.name)}</a></p></section>
      <section class="app-part" aria-labelledby="spec"><h3 id="spec">Specification</h3>
        <div class="spec-grid"><div><h4>Minimum</h4>${specTable(spec.minimum)}</div><div><h4>Recommended</h4>${specTable(spec.recommended)}</div></div></section>
      <section class="app-part" aria-labelledby="setup"><h3 id="setup">Setup guide</h3>
        <ol class="steps">${spec.setup.map(s => `<li>${s}</li>`).join("")}</ol>
        <p><a href="${esc(spec.guide)}">Full ${esc(product.name)} guide for ${esc(platform.short || platform.name)}</a></p></section>
      <section class="app-part" aria-labelledby="dl"><h3 id="dl">Download</h3>${download}
        <p class="small">Our apps aren't in the Microsoft Store or Google Play yet, so your device may warn you about an app from outside the store. Android apps are signed by ZonoSoft (certificate SHA-256 A7:96:D7:8B:…:62:A1:37).</p></section>`;
    document.title = `${product.name} for ${platform.name} | ZonoSoft`;
  }

  function route() {
    const [platformId, productId] = location.hash.replace(/^#/, "").split("/");
    const known = data.platforms.some(p => p.id === platformId);
    if (productId) { productPage(known ? platformId : "windows", productId); window.scrollTo(0, 0); }
    else catalogue(known ? platformId : (/Android/i.test(navigator.userAgent) ? "android" : "windows"));
  }

  load.then(() => { route(); addEventListener("hashchange", route); })
    .catch(() => { root.innerHTML = `<p class="notice">The software list couldn't load. Please try again shortly, or ${contact}.</p>`; });
})();
