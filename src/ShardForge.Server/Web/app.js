// Shared helpers for ShardForge Server pages.
"use strict";
const SF = {
  async api(method, path, body) {
    const opt = { method, headers: { "X-ShardForge": "1" }, credentials: "same-origin" };
    if (body !== undefined) { opt.body = typeof body === "string" ? body : JSON.stringify(body); opt.headers["Content-Type"] = "application/json"; }
    const r = await fetch(path, opt);
    const ct = r.headers.get("content-type") || "";
    const data = ct.includes("json") ? await r.json() : null;
    if (!r.ok) { const e = new Error(data?.error || `${r.status} ${r.statusText}`); e.status = r.status; e.data = data; throw e; }
    return data;
  },
  async me() {
    try { return await SF.api("GET", "/api/me"); } catch (e) { if (e.status === 401) return null; throw e; }
  },
  /** Sends people to setup or sign-in when needed; returns the signed-in user. */
  async requireLogin() {
    const st = await SF.api("GET", "/api/status");
    if (st.needsSetup) { location.href = "/setup.html"; return null; }
    const me = await SF.me();
    if (!me) { location.href = "/login.html?next=" + encodeURIComponent(location.pathname + location.search); return null; }
    return me;
  },
  header(me, current) {
    const nav = [["/", "Dashboard"], ["/editor/", "Editor"], ["/walk/", "Walk"], ...(me.level >= 3 ? [["/admin.html", "Users"]] : [])];
    document.querySelector("header.top").innerHTML = `
      <a class="brand" href="/">ShardForge</a>
      <nav aria-label="Main">${nav.map(([h, t]) => `<a href="${h}"${h === current ? ' aria-current="page"' : ""}>${t}</a>`).join("")}</nav>
      <div class="spacer"></div>
      <div class="userbox"><span>${SF.esc(me.username)}</span><span class="pill ${me.role}">${me.role}</span><button id="btnLogout">Sign out</button></div>`;
    document.getElementById("btnLogout").onclick = async () => { await SF.api("POST", "/api/auth/logout"); location.href = "/login.html"; };
  },
  esc: s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])),
  ago(iso) {
    if (!iso) return "never";
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (s < 60) return "just now";
    if (s < 3600) return `${Math.floor(s / 60)} min ago`;
    if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
    return new Date(iso).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
  },
  size(n) { return n > 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(n / 1024)) + " KB"; },
  msg(el, text, kind = "error") { el.textContent = text; el.className = "msg " + (text ? kind : ""); },
  safeNext(n) { return n && n.startsWith("/") && !n.startsWith("//") ? n : "/"; },
};
