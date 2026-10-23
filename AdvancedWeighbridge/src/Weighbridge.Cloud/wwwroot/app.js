// AWMS Cloud dashboard – a small single-page app without external dependencies.
// Security notes: all server data is inserted with textContent (never as HTML); the access token is kept in memory and
// the refresh token in sessionStorage (cleared when the tab closes); the server sends a strict Content-Security-Policy.
"use strict";

const API = "/api/v1";
const state = { access: null, user: null, timer: null, unread: 0 };

// ------------------------------------------------------------------ helpers

function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (v === true) el.setAttribute(k, "");
    else el.setAttribute(k, v);
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

const $view = () => document.getElementById("view");
const can = (p) => !!state.user && state.user.permissions.includes(p);
const kg = (g) => (g === null || g === undefined) ? "—" : (g / 1000).toLocaleString("en-US", { maximumFractionDigits: 0 }) + " kg";
const fmt = (iso) => iso ? new Date(iso).toLocaleString(undefined, { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" }) : "—";
const ago = (seconds) => seconds === null || seconds === undefined ? "never"
  : seconds < 90 ? `${Math.round(seconds)} s ago` : seconds < 5400 ? `${Math.round(seconds / 60)} min ago` : `${Math.round(seconds / 3600)} h ago`;
const pill = (cls, text) => h("span", { class: `pill ${cls}` }, text);
const statusPill = (s) => pill(s === "Completed" ? "ok" : s === "Cancelled" ? "bad" : "warn", s);
const isoInput = (d) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);

function banner(text, kind = "warn", ms = 6000) {
  const b = document.getElementById("banner");
  if (!text) { b.hidden = true; return; }
  b.textContent = text;
  b.className = `banner ${kind}`;
  b.hidden = false;
  clearTimeout(banner.t);
  if (ms) banner.t = setTimeout(() => (b.hidden = true), ms);
}

class ApiError extends Error {
  constructor(status, message, mfaRequired) { super(message); this.status = status; this.mfaRequired = !!mfaRequired; }
}

async function api(path, { method = "GET", body, raw = false, auth = true, retry = true } = {}) {
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (auth && state.access) headers.Authorization = `Bearer ${state.access}`;
  const res = await fetch(API + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: "same-origin" });
  if (res.status === 401 && auth && retry && await refresh()) return api(path, { method, body, raw, auth, retry: false });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`, mfa = false;
    try { const j = await res.json(); msg = j.error || msg; mfa = j.mfaRequired; } catch { /* not JSON */ }
    if (res.status === 401 && auth) { signOutLocal(); }
    throw new ApiError(res.status, msg, mfa);
  }
  if (raw) return res;
  return res.status === 204 ? null : res.json();
}

async function refresh() {
  const token = sessionStorage.getItem("awms.refresh");
  if (!token) return false;
  if (!refresh.pending) {
    refresh.pending = (async () => {
      try {
        const r = await api("/auth/refresh", { method: "POST", body: { refreshToken: token }, auth: false, retry: false });
        accept(r);
        return true;
      } catch { sessionStorage.removeItem("awms.refresh"); return false; }
      finally { setTimeout(() => (refresh.pending = null), 0); }
    })();
  }
  return refresh.pending;
}

function accept(login) {
  state.access = login.accessToken;
  state.user = login.user;
  sessionStorage.setItem("awms.refresh", login.refreshToken);
}

function signOutLocal() {
  state.access = null;
  state.user = null;
  sessionStorage.removeItem("awms.refresh");
  location.hash = "#/login";
}

async function download(path, fallbackName) {
  try {
    const res = await api(path, { raw: true });
    const blob = await res.blob();
    const cd = res.headers.get("Content-Disposition") || "";
    const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
    const a = h("a", { href: URL.createObjectURL(blob), download: m ? decodeURIComponent(m[1]) : fallbackName });
    document.body.append(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  } catch (e) { banner(e.message, "error"); }
}

function dialog(title, content, actions) {
  const opener = document.activeElement;
  const onKey = (e) => { if (e.key === "Escape") close(); };
  const close = () => {
    document.removeEventListener("keydown", onKey);
    back.remove();
    if (opener && opener.isConnected) opener.focus();
  };
  const closeButton = h("button", { class: "secondary", onclick: () => close() }, "Close");
  const back = h("div", { class: "dialog-backdrop", onclick: (e) => { if (e.target === back) close(); } },
    h("div", { class: "dialog", role: "dialog", "aria-modal": "true", "aria-label": title },
      h("h1", {}, title), content,
      h("div", { class: "dialog-actions" }, ...(actions ? actions(close) : []), closeButton)));
  back.closeDialog = close;
  document.body.append(back);
  document.addEventListener("keydown", onKey);
  (back.querySelector("input, select, textarea") || closeButton).focus();
  return close;
}

const closeDialogs = () => document.querySelectorAll(".dialog-backdrop").forEach((d) => d.closeDialog ? d.closeDialog() : d.remove());

function table(columns, rows, onClick) {
  return h("div", { class: "table-wrap" }, h("table", {},
    h("thead", {}, h("tr", {}, columns.map((c) => h("th", { class: c.right ? "right" : null }, c.title)))),
    h("tbody", {}, rows.length === 0
      ? h("tr", {}, h("td", { colspan: columns.length, class: "muted" }, "Nothing to show."))
      : rows.map((r) => h("tr", { class: onClick ? "clickable" : null, onclick: onClick ? () => onClick(r) : null, tabindex: onClick ? "0" : null,
          onkeydown: onClick ? (e) => { if (e.key === "Enter") onClick(r); } : null },
        columns.map((c) => h("td", { class: [c.right ? "right" : "", c.cls || ""].join(" ").trim() || null }, c.value(r))))))));
}

// ------------------------------------------------------------------ chrome

const routes = [
  { path: "dashboard", title: "Dashboard", perm: "dashboard.view", render: renderDashboard },
  { path: "transactions", title: "Transactions", perm: "history.view", render: renderTransactions },
  { path: "reports", title: "Reports", perm: "reports.view", render: renderReports },
  { path: "notifications", title: "Notifications", perm: "dashboard.view", render: renderNotifications },
  { path: "audit", title: "Audit", perm: "audit.view", render: renderAudit },
  { path: "admin", title: "Administration", perm: ["cloud.manage", "users.manage"], render: renderAdmin },
  { path: "account", title: "Account", perm: null, render: renderAccount },
];

function renderChrome(active) {
  const nav = document.getElementById("nav");
  const who = document.getElementById("who");
  nav.replaceChildren();
  who.replaceChildren();
  const restricted = state.user && (state.user.mustChangePassword || state.user.mfaSetupRequired);
  nav.hidden = who.hidden = !state.user;
  if (!state.user) return;
  for (const r of routes) {
    const perms = r.perm === null ? [] : [].concat(r.perm);
    if (perms.length && !perms.some(can)) continue;
    if (restricted && r.path !== "account") continue;
    nav.append(h("a", { href: `#/${r.path}`, class: r.path === active ? "active" : null, "aria-current": r.path === active ? "page" : null },
      r.title, r.path === "notifications" && state.unread > 0 ? h("span", { class: "count", "aria-label": `${state.unread} unread` }, state.unread > 99 ? "99+" : state.unread) : null));
  }
  who.append(h("span", { class: "name" }, `${state.user.displayName} (${state.user.role})`),
    h("button", { class: "secondary", onclick: signOut }, "Sign out"));
}

async function signOut() {
  const token = sessionStorage.getItem("awms.refresh");
  try { if (token) await api("/auth/logout", { method: "POST", body: { refreshToken: token } }); } catch { /* already signed out */ }
  signOutLocal();
}

async function route() {
  clearInterval(state.timer);
  closeDialogs();
  const path = (location.hash.replace(/^#\/?/, "") || "dashboard").split("?")[0];
  if (!state.user && !(await refresh())) {
    renderChrome(null);
    return renderLogin();
  }
  if (path === "login") { location.hash = "#/dashboard"; return; }
  if ((state.user.mustChangePassword || state.user.mfaSetupRequired) && path !== "account") { location.hash = "#/account"; return; }
  const r = routes.find((x) => x.path === path) || routes[0];
  renderChrome(r.path);
  $view().replaceChildren(h("p", { class: "muted" }, "Loading…"));
  try { await r.render(); }
  catch (e) { $view().replaceChildren(h("div", { class: "panel" }, h("h1", {}, r.title), h("p", { class: "pill bad" }, e.message))); }
  $view().focus({ preventScroll: true });
  pollUnread();
}

async function pollUnread() {
  if (!can("dashboard.view")) return;
  try {
    const list = await api("/notifications?unreadOnly=true&limit=100");
    if (list.length !== state.unread) { state.unread = list.length; renderChrome((location.hash.replace(/^#\/?/, "") || "dashboard").split("?")[0]); }
  } catch { /* ignore */ }
}

// ------------------------------------------------------------------ login

function renderLogin(message) {
  const user = h("input", { name: "username", autocomplete: "username", required: true, autofocus: true });
  const pass = h("input", { name: "password", type: "password", autocomplete: "current-password", required: true });
  const code = h("input", { name: "totp", inputmode: "numeric", autocomplete: "one-time-code", pattern: "[0-9]{6}", maxlength: "6" });
  const codeRow = h("label", { hidden: true }, "Authentication code", code);
  const error = h("p", { class: "pill bad", hidden: !message }, message || "");
  const submit = h("button", { type: "submit" }, "Sign in");
  const form = h("form", { class: "form", onsubmit: async (e) => {
    e.preventDefault();
    submit.disabled = true;
    error.hidden = true;
    try {
      accept(await api("/auth/login", { method: "POST", auth: false, body: { username: user.value, password: pass.value, totpCode: code.value || null } }));
      location.hash = "#/dashboard";
      route();
    } catch (err) {
      if (err.mfaRequired) { codeRow.hidden = false; code.focus(); }
      error.textContent = err.message;
      error.hidden = false;
    } finally { submit.disabled = false; }
  } }, h("label", {}, "User name", user), h("label", {}, "Password", pass), codeRow, error, submit);
  $view().replaceChildren(h("div", { class: "panel login" }, h("h1", {}, "Sign in"), form,
    h("p", { class: "muted" }, "Weighing continues at each site even when this server is unreachable; data appears here once synchronised.")));
}

// ------------------------------------------------------------------ dashboard

async function renderDashboard() {
  const container = h("div", { class: "grid" });
  const stamp = h("span", { class: "muted" });
  $view().replaceChildren(h("div", { class: "site-head" }, h("h1", {}, "Live status"), stamp), container);
  const load = async () => {
    const statuses = await api("/status");
    stamp.textContent = `Updated ${new Date().toLocaleTimeString()} · refreshes every 5 s`;
    container.replaceChildren(...(statuses.length ? statuses.map(siteCard) : [h("p", { class: "muted" }, "No sites are registered yet.")]));
  };
  await load();
  state.timer = setInterval(() => load().catch((e) => banner(e.message, "error")), 5000);
}

function siteCard(s) {
  const online = s.site.online;
  const card = h("section", { class: "panel" },
    h("div", { class: "site-head" }, h("h2", {}, `${s.site.name} (${s.site.code})`),
      online ? pill("ok", "ONLINE") : pill("bad", "OFFLINE")),
    h("p", { class: "muted" }, `Last contact ${ago(s.heartbeatAgeSeconds)}${s.site.appVersion ? ` · desktop ${s.site.appVersion}` : ""}`));
  for (const w of s.weighbridges) card.append(weighbridgeTile(w, s));
  if (s.health) {
    card.append(h("dl", { class: "facts" },
      h("dt", {}, "Disk free"), h("dd", {}, `${s.health.diskFreeGb} GB`),
      h("dt", {}, "Last backup"), h("dd", {}, fmt(s.health.lastBackupUtc)),
      h("dt", {}, "Waiting to sync"), h("dd", {}, `${s.health.syncPending}${s.health.syncFailed ? ` (${s.health.syncFailed} retrying)` : ""}`),
      ...(s.health.warnings || []).flatMap((w) => [h("dt", {}, "Warning"), h("dd", {}, pill("warn", w))])));
  }
  return card;
}

function weighbridgeTile(w, s) {
  // Never present old data as live: a stale heartbeat or an old reading is shown greyed and labelled.
  const notLive = s.liveDataStale || w.displayStatus === "NotLive";
  let cls = "idle", label = "NOT LIVE";
  if (!notLive) {
    if (w.connection !== "Connected") { cls = "bad"; label = w.connection.toUpperCase(); }
    else if (w.liveG === null || w.liveG === undefined) { cls = "idle"; label = "LIVE WEIGHT NOT SHARED"; }
    else if (w.stable) { cls = "ok"; label = "STABLE"; }
    else { cls = "warn"; label = "MOTION"; }
  }
  const age = s.heartbeatAgeSeconds;
  return h("div", { class: `wb ${cls}` },
    h("div", { class: "site-head" }, h("strong", {}, `${w.name} (${w.code})`), h("span", { class: "state" }, label)),
    h("div", { class: "weight", "aria-label": notLive ? `Last known weight ${kg(w.liveG)}, not live` : `Weight ${kg(w.liveG)}` }, kg(w.liveG)),
    notLive ? h("p", { class: "muted" }, `Last known value from ${ago(age)} — not a current reading.`) : null,
    h("dl", { class: "facts" },
      h("dt", {}, "Mode"), h("dd", {}, w.operatingMode + (w.automationState ? ` · ${w.automationState}` : "")),
      h("dt", {}, "Open tickets"), h("dd", {}, String(w.pendingTransactions)),
      w.lastError ? [h("dt", {}, "Last error"), h("dd", {}, w.lastError)] : null));
}

// ------------------------------------------------------------------ transactions

async function siteOptions(select, includeAll = true) {
  const sites = await api("/sites");
  select.replaceChildren(...(includeAll ? [h("option", { value: "" }, "All sites")] : []), ...sites.map((s) => h("option", { value: s.code }, `${s.code} – ${s.name}`)));
  return sites;
}

async function renderTransactions() {
  const site = h("select", {});
  const from = h("input", { type: "date", value: isoInput(new Date(Date.now() - 7 * 864e5)) });
  const to = h("input", { type: "date", value: isoInput(new Date()) });
  const q = h("input", { type: "search", placeholder: "Ticket, vehicle, customer, material" });
  const status = h("select", {}, h("option", { value: "" }, "Any status"), ["Pending", "Completed", "Cancelled"].map((s) => h("option", { value: s }, s)));
  const manual = h("input", { type: "checkbox" });
  const result = h("div", {});
  let page = 1;
  await siteOptions(site);
  const load = async () => {
    const p = new URLSearchParams({ page, pageSize: 50 });
    if (site.value) p.set("site", site.value);
    if (from.value) p.set("from", new Date(from.value + "T00:00:00").toISOString());
    if (to.value) p.set("to", new Date(new Date(to.value + "T00:00:00").getTime() + 864e5).toISOString());
    if (q.value.trim()) p.set("q", q.value.trim());
    if (status.value) p.set("status", status.value);
    if (manual.checked) p.set("manualOnly", "true");
    const data = await api(`/transactions?${p}`);
    const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
    result.replaceChildren(
      table([
        { title: "Ticket", value: (t) => t.ticketNo, cls: "mono nowrap" },
        { title: "Site", value: (t) => t.siteCode },
        { title: "Created", value: (t) => fmt(t.createdUtc), cls: "nowrap" },
        { title: "Vehicle", value: (t) => t.vehicleReg, cls: "nowrap" },
        { title: "Customer / supplier", value: (t) => t.customer || t.supplier || "—" },
        { title: "Material", value: (t) => t.material || "—" },
        { title: "Net", value: (t) => kg(t.netG), right: true, cls: "mono nowrap" },
        { title: "Status", value: (t) => [statusPill(t.status), t.hasManualWeight ? " " : null, t.hasManualWeight ? pill("warn", "MANUAL") : null] },
      ], data.items, (t) => showTransaction(t.uid, load)),
      h("div", { class: "pager" }, h("span", { class: "muted" }, `${data.total} transactions · page ${data.page} of ${pages}`),
        h("button", { class: "secondary", disabled: page <= 1, onclick: () => { page--; load(); } }, "Previous"),
        h("button", { class: "secondary", disabled: page >= pages, onclick: () => { page++; load(); } }, "Next")));
  };
  const search = () => { page = 1; load().catch((e) => banner(e.message, "error")); };
  $view().replaceChildren(h("h1", {}, "Transactions"),
    h("form", { class: "toolbar", onsubmit: (e) => { e.preventDefault(); search(); } },
      h("label", {}, "Site", site), h("label", {}, "From", from), h("label", {}, "To", to), h("label", {}, "Search", q),
      h("label", {}, "Status", status), h("label", { class: "check" }, manual, "Manual weights only"), h("button", { type: "submit" }, "Search")),
    h("div", { class: "panel" }, result));
  await load();
}

async function showTransaction(uid, reload) {
  const d = await api(`/transactions/${uid}`);
  const t = d.transaction;
  const facts = [
    ["Ticket", t.ticketNo], ["Status", t.status], ["Site / weighbridge", `${t.siteCode} / ${t.weighbridgeCode}`], ["Direction", t.direction],
    ["Mode", t.operatingMode], ["Vehicle", t.vehicleReg], ["Driver", t.driverName], ["Customer", t.customerName], ["Supplier", t.supplierName],
    ["Material", t.materialName], ["Order", t.orderNo], ["Delivery note", t.deliveryNoteNo], ["Reference", t.referenceNo], ["Remarks", t.remarks],
    ["Gross", kg(t.grossG)], ["Tare", kg(t.tareG) + (t.tareMethod ? ` (${t.tareMethod})` : "")], ["Net", kg(t.netG)],
    ["Created", `${fmt(t.createdUtc)} by ${t.createdBy}`], ["Completed", t.completedUtc ? `${fmt(t.completedUtc)} by ${t.completedBy}` : null],
    ["Cancelled", t.cancelledUtc ? `${fmt(t.cancelledUtc)} by ${t.cancelledBy}: ${t.cancelReason}` : null], ["Replaces ticket", t.replacesTicketNo],
    ["Version", String(t.version)],
  ].filter(([, v]) => v !== null && v !== undefined && v !== "");
  const content = h("div", {},
    t.hasManualWeight ? h("p", { class: "pill warn" }, "This transaction contains a manually entered weight (not an indicator measurement).") : null,
    t.hasOverride ? h("p", { class: "pill warn" }, "An unstable-weight override was used.") : null,
    h("dl", { class: "facts" }, facts.flatMap(([k, v]) => [h("dt", {}, k), h("dd", {}, v)])),
    h("h2", {}, "Measurements"),
    table([
      { title: "Stage", value: (m) => String(m.stage) },
      { title: "Source", value: (m) => m.source === "Manual" ? pill("warn", "MANUAL") : m.source },
      { title: "Weight", value: (m) => kg(m.weightG), right: true, cls: "mono nowrap" },
      { title: "Stable", value: (m) => m.isStable ? "Yes" : m.unstableOverride ? `Override: ${m.overrideReason || ""}` : "No" },
      { title: "Captured", value: (m) => fmt(m.capturedUtc), cls: "nowrap" },
      { title: "Operator", value: (m) => m.operatorName },
      { title: "Raw", value: (m) => m.rawText || (m.manualReason ? `Reason: ${m.manualReason}` : "—"), cls: "mono" },
    ], t.measurements),
    d.commands.length ? [h("h2", {}, "Remote requests"), table([
      { title: "Requested", value: (c) => `${fmt(c.requestedUtc)} by ${c.requestedBy}` },
      { title: "Type", value: (c) => c.type },
      { title: "Reason", value: (c) => c.reason || "" },
      { title: "Status", value: (c) => pill(c.status === "Completed" ? "ok" : c.status === "Failed" ? "bad" : "info", c.status) },
      { title: "Result", value: (c) => c.resultMessage || "" },
    ], d.commands)] : null);
  dialog(`Ticket ${t.ticketNo}`, content, (close) => [
    h("button", { class: "secondary", onclick: () => download(`/transactions/${uid}/ticket.pdf`, `${t.ticketNo}.pdf`) }, "Download PDF copy"),
    can("transaction.cancel") && t.status !== "Cancelled" ? h("button", { class: "danger", onclick: () => { close(); requestCancel(t, reload); } }, "Request cancellation") : null,
  ].filter(Boolean));
}

function requestCancel(t, reload) {
  const reason = h("textarea", { rows: "3", required: true, minlength: "5", maxlength: "500" });
  const err = h("p", { class: "pill bad", hidden: true });
  dialog(`Cancel ticket ${t.ticketNo}?`, h("div", { class: "form" },
    h("p", {}, "The request is sent to the site. The site applies it under its own rules and reports the result here. The original record is kept with the reason."),
    h("label", {}, "Reason (required)", reason), err), (close) => [
    h("button", { class: "danger", onclick: async () => {
      try {
        await api(`/transactions/${t.uid}/cancel`, { method: "POST", body: { reason: reason.value } });
        close();
        banner(`Cancellation of ${t.ticketNo} requested. It is applied when the site next connects.`, "ok");
        reload && reload();
      } catch (e) { err.textContent = e.message; err.hidden = false; }
    } }, "Send request")]);
}

// ------------------------------------------------------------------ reports

async function renderReports() {
  const site = h("select", {});
  const group = h("select", {}, [["day", "Day"], ["site", "Site"], ["customer", "Customer"], ["supplier", "Supplier"], ["material", "Material"], ["vehicle", "Vehicle"]]
    .map(([v, t]) => h("option", { value: v }, t)));
  const from = h("input", { type: "date", value: isoInput(new Date(Date.now() - 30 * 864e5)) });
  const to = h("input", { type: "date", value: isoInput(new Date()) });
  const out = h("div", { class: "panel" });
  await siteOptions(site);
  const params = () => {
    const p = new URLSearchParams({ from: new Date(from.value + "T00:00:00").toISOString(), to: new Date(new Date(to.value + "T00:00:00").getTime() + 864e5).toISOString() });
    if (site.value) p.set("site", site.value);
    return p;
  };
  const load = async () => {
    const p = params();
    p.set("groupBy", group.value);
    const r = await api(`/reports/summary?${p}`);
    out.replaceChildren(
      h("p", { class: "muted" }, `Completed weighments ${fmt(r.from)} – ${fmt(r.to)}`),
      table([
        { title: group.options[group.selectedIndex].text, value: (x) => x.key },
        { title: "Tickets", value: (x) => x.count.toLocaleString(), right: true },
        { title: "Net", value: (x) => kg(x.netG), right: true, cls: "mono nowrap" },
      ], r.rows),
      h("p", {}, h("strong", {}, `Total: ${r.totalCount.toLocaleString()} tickets, ${kg(r.totalNetG)}`)));
  };
  const exports = can("reports.export") ? ["csv", "xlsx", "pdf"].map((f) =>
    h("button", { type: "button", class: "secondary", onclick: () => { const p = params(); p.set("format", f); download(`/reports/export?${p}`, `weighments.${f}`); } }, `Export ${f.toUpperCase()}`)) : [];
  $view().replaceChildren(h("h1", {}, "Reports"),
    h("form", { class: "toolbar", onsubmit: (e) => { e.preventDefault(); load().catch((x) => banner(x.message, "error")); } },
      h("label", {}, "Site", site), h("label", {}, "Group by", group), h("label", {}, "From", from), h("label", {}, "To", to),
      h("button", { type: "submit" }, "Show"), ...exports), out);
  await load();
}

// ------------------------------------------------------------------ notifications

async function renderNotifications() {
  const list = await api("/notifications?limit=200");
  const sev = (s) => pill(s === "Critical" || s === "Error" ? "bad" : s === "Warning" ? "warn" : "info", s.toUpperCase());
  $view().replaceChildren(h("div", { class: "site-head" }, h("h1", {}, "Notifications"),
      h("button", { class: "secondary", onclick: async () => { await api("/notifications/read", { method: "POST", body: { ids: null } }); state.unread = 0; route(); } }, "Mark all as read")),
    h("div", { class: "panel" }, table([
      { title: "", value: (n) => n.isRead ? "" : pill("info", "NEW") },
      { title: "When", value: (n) => fmt(n.atUtc), cls: "nowrap" },
      { title: "Site", value: (n) => n.siteCode },
      { title: "Severity", value: (n) => sev(n.severity) },
      { title: "Title", value: (n) => n.title },
      { title: "Message", value: (n) => n.message },
    ], list, (n) => n.entityId && /^[0-9a-f-]{36}$/i.test(n.entityId) && can("history.view") ? showTransaction(n.entityId).catch((e) => banner(e.message, "error")) : null)));
}

// ------------------------------------------------------------------ audit

async function renderAudit() {
  const site = h("select", {});
  const q = h("input", { type: "search", placeholder: "Text, user, action" });
  const out = h("div", { class: "panel" });
  const verify = h("div", {});
  let page = 1;
  await siteOptions(site);
  const load = async () => {
    const p = new URLSearchParams({ page, pageSize: 100 });
    if (site.value) p.set("site", site.value);
    if (q.value.trim()) p.set("q", q.value.trim());
    const data = await api(`/audit?${p}`);
    const pages = Math.max(1, Math.ceil(data.total / data.pageSize));
    out.replaceChildren(table([
      { title: "When", value: (a) => fmt(a.atUtc), cls: "nowrap" },
      { title: "Site", value: (a) => a.siteCode },
      { title: "User", value: (a) => a.username },
      { title: "Category", value: (a) => a.category },
      { title: "Action", value: (a) => a.action },
      { title: "Summary", value: (a) => a.summary + (a.reason ? ` — ${a.reason}` : "") },
    ], data.items), h("div", { class: "pager" }, h("span", { class: "muted" }, `${data.total} events · page ${data.page} of ${pages}`),
      h("button", { class: "secondary", disabled: page <= 1, onclick: () => { page--; load(); } }, "Previous"),
      h("button", { class: "secondary", disabled: page >= pages, onclick: () => { page++; load(); } }, "Next")));
  };
  const check = async () => {
    if (!site.value) { verify.replaceChildren(pill("warn", "Choose a site to verify its audit chain.")); return; }
    const r = await api(`/audit/verify?site=${encodeURIComponent(site.value)}`);
    verify.replaceChildren(pill(r.valid ? "ok" : "bad", r.valid ? "CHAIN INTACT" : "CHAIN BROKEN"), " ", r.message);
  };
  $view().replaceChildren(h("h1", {}, "Audit trail"),
    h("form", { class: "toolbar", onsubmit: (e) => { e.preventDefault(); page = 1; load().catch((x) => banner(x.message, "error")); } },
      h("label", {}, "Site", site), h("label", {}, "Search", q), h("button", { type: "submit" }, "Search"),
      h("button", { type: "button", class: "secondary", onclick: () => check().catch((x) => banner(x.message, "error")) }, "Verify chain")),
    h("div", { class: "panel" }, verify, h("p", { class: "muted" }, "Each event carries the hash of the previous one. Removing, re-ordering or replacing events at the site breaks the chain.")),
    out);
  await load();
}

// ------------------------------------------------------------------ administration

async function renderAdmin() {
  const parts = [h("h1", {}, "Administration")];
  if (can("cloud.manage")) parts.push(await sitesPanel());
  if (can("users.manage")) parts.push(await usersPanel());
  if (can("cloud.manage")) parts.push(await conflictsPanel(), await serverLogPanel());
  $view().replaceChildren(...parts);
}

function showKey(title, r) {
  dialog(title, h("div", {},
    h("p", {}, `Site key for ${r.site.code}. Copy it now and enter it in the desktop application (Administration → Cloud). It is not shown again; only a fingerprint is stored.`),
    h("p", { class: "secret" }, r.siteKey)), () => [
    h("button", { onclick: async () => { try { await navigator.clipboard.writeText(r.siteKey); banner("Copied.", "ok", 2000); } catch { banner("Select the key and copy it manually.", "warn"); } } }, "Copy")]);
}

async function sitesPanel() {
  const sites = await api("/admin/sites");
  const code = h("input", { required: true, maxlength: "20", pattern: "[A-Za-z0-9][A-Za-z0-9_-]*", placeholder: "e.g. QRY" });
  const name = h("input", { required: true, maxlength: "200", placeholder: "Site name" });
  return h("section", { class: "panel" }, h("h2", {}, "Sites"),
    table([
      { title: "Code", value: (s) => s.code, cls: "mono" },
      { title: "Name", value: (s) => s.name },
      { title: "State", value: (s) => !s.isActive ? pill("idle", "DEACTIVATED") : s.online ? pill("ok", "ONLINE") : pill("bad", "OFFLINE") },
      { title: "Last contact", value: (s) => fmt(s.lastHeartbeatUtc) },
      { title: "Key", value: (s) => `${s.keyPrefix}…`, cls: "mono" },
      { title: "", value: (s) => [
        h("button", { class: "secondary", onclick: async () => {
          if (!confirm(`Issue a new key for ${s.code}? The current key stops working immediately.`)) return;
          try { showKey("New site key", await api(`/admin/sites/${encodeURIComponent(s.code)}/rotate-key`, { method: "POST" })); } catch (e) { banner(e.message, "error"); }
        } }, "New key"), " ",
        h("button", { class: "secondary", onclick: async () => {
          try { await api(`/admin/sites/${encodeURIComponent(s.code)}/${s.isActive ? "deactivate" : "activate"}`, { method: "POST" }); route(); } catch (e) { banner(e.message, "error"); }
        } }, s.isActive ? "Deactivate" : "Activate")] },
    ], sites),
    h("form", { class: "toolbar", onsubmit: async (e) => {
      e.preventDefault();
      try { const r = await api("/admin/sites", { method: "POST", body: { code: code.value.trim(), name: name.value.trim() } }); showKey("Site registered", r); route(); }
      catch (x) { banner(x.message, "error"); }
    } }, h("label", {}, "Site code (as in the desktop)", code), h("label", {}, "Name", name), h("button", { type: "submit" }, "Register site")));
}

const ROLES = ["Administrator", "Manager", "Operator", "Auditor", "Viewer"];

async function usersPanel() {
  const users = await api("/admin/users");
  const sites = await api("/admin/sites").catch(() => []);
  return h("section", { class: "panel" }, h("h2", {}, "Cloud users"),
    table([
      { title: "User", value: (u) => u.username },
      { title: "Name", value: (u) => u.displayName },
      { title: "Role", value: (u) => u.role },
      { title: "Sites", value: (u) => u.role === "Administrator" ? "All" : (u.sites.join(", ") || "—") },
      { title: "State", value: (u) => [u.isActive ? pill("ok", "ACTIVE") : pill("idle", "DISABLED"), u.locked ? [" ", pill("bad", "LOCKED")] : null, u.mfaEnabled ? [" ", pill("info", "MFA")] : null] },
      { title: "Last sign-in", value: (u) => fmt(u.lastLoginUtc) },
    ], users, (u) => editUser(u, sites)),
    h("button", { onclick: () => editUser(null, sites) }, "Add user"));
}

function editUser(u, sites) {
  const username = h("input", { value: u ? u.username : "", disabled: !!u, required: true, autocomplete: "off" });
  const display = h("input", { value: u ? u.displayName : "", required: true });
  const role = h("select", {}, ROLES.map((r) => h("option", { value: r, selected: u ? u.role === r : r === "Viewer" }, r)));
  const siteBoxes = sites.map((s) => h("input", { type: "checkbox", value: s.code, checked: !!u && u.sites.includes(s.code) }));
  const password = h("input", { type: "password", autocomplete: "new-password", required: !u });
  const active = h("input", { type: "checkbox", checked: u ? u.isActive : true });
  const err = h("p", { class: "pill bad", hidden: true });
  const fail = (e) => { err.textContent = e.message; err.hidden = false; };
  const chosenSites = () => siteBoxes.filter((b) => b.checked).map((b) => b.value);
  dialog(u ? `User ${u.username}` : "New cloud user", h("div", { class: "form" },
    h("label", {}, "User name", username), h("label", {}, "Display name", display), h("label", {}, "Role", role),
    h("fieldset", {}, h("legend", {}, "Sites (administrators see all)"), siteBoxes.map((b) => h("label", { class: "check" }, b, b.value))),
    h("label", {}, u ? "New temporary password (optional)" : "Temporary password (must be changed at first sign-in)", password),
    u ? h("label", { class: "check" }, active, "Active") : null, err), (close) => [
    u && u.locked ? h("button", { class: "secondary", onclick: async () => { try { await api(`/admin/users/${u.id}/unlock`, { method: "POST" }); close(); route(); } catch (e) { fail(e); } } }, "Unlock") : null,
    u && u.mfaEnabled ? h("button", { class: "secondary", onclick: async () => {
      if (!confirm(`Remove the authenticator app of ${u.username}?`)) return;
      try { await api(`/admin/users/${u.id}/reset-mfa`, { method: "POST" }); close(); route(); } catch (e) { fail(e); }
    } }, "Reset MFA") : null,
    h("button", { onclick: async () => {
      try {
        if (u) {
          await api(`/admin/users/${u.id}`, { method: "PUT", body: { displayName: display.value, role: role.value, sites: chosenSites(), isActive: active.checked } });
          if (password.value) await api(`/admin/users/${u.id}/reset-password`, { method: "POST", body: { newPassword: password.value } });
        } else {
          await api("/admin/users", { method: "POST", body: { username: username.value.trim(), displayName: display.value, password: password.value, role: role.value, sites: chosenSites() } });
        }
        close();
        route();
      } catch (e) { fail(e); }
    } }, "Save")].filter(Boolean));
}

async function conflictsPanel() {
  const list = await api("/admin/conflicts");
  return h("section", { class: "panel" }, h("h2", {}, "Synchronisation conflicts"),
    h("p", { class: "muted" }, "A site sent different content for a version the cloud already had. The cloud copy was kept; compare with the site record."),
    table([
      { title: "When", value: (c) => fmt(c.atUtc) },
      { title: "Site", value: (c) => c.siteCode },
      { title: "Type", value: (c) => c.entityType },
      { title: "Id", value: (c) => c.uid, cls: "mono" },
      { title: "Versions (site / cloud)", value: (c) => `${c.localVersion ?? "?"} / ${c.serverVersion ?? "?"}` },
    ], list));
}

async function serverLogPanel() {
  const list = await api("/admin/server-audit?limit=100");
  return h("section", { class: "panel" }, h("h2", {}, "Server security log"),
    table([
      { title: "When", value: (a) => fmt(a.atUtc), cls: "nowrap" },
      { title: "User", value: (a) => a.username },
      { title: "Action", value: (a) => a.action },
      { title: "Detail", value: (a) => a.detail },
      { title: "IP", value: (a) => a.ip || "", cls: "mono" },
    ], list));
}

// ------------------------------------------------------------------ account

async function renderAccount() {
  state.user = await api("/me");
  const u = state.user;
  const current = h("input", { type: "password", autocomplete: "current-password", required: true });
  const next = h("input", { type: "password", autocomplete: "new-password", required: true, minlength: "10" });
  const again = h("input", { type: "password", autocomplete: "new-password", required: true });
  const err = h("p", { class: "pill bad", hidden: true });
  const parts = [h("h1", {}, "Account")];
  if (u.mustChangePassword) parts.push(h("p", { class: "pill warn" }, "Choose a new password before continuing."));
  if (u.mfaSetupRequired) parts.push(h("p", { class: "pill warn" }, "This server requires administrators to use two-factor authentication. Set it up below."));
  parts.push(h("section", { class: "panel" }, h("h2", {}, "Password"),
    h("form", { class: "form", onsubmit: async (e) => {
      e.preventDefault();
      err.hidden = true;
      if (next.value !== again.value) { err.textContent = "The new passwords do not match."; err.hidden = false; return; }
      try {
        accept(await api("/me/password", { method: "POST", body: { currentPassword: current.value, newPassword: next.value } }));
        banner("Password changed. Other sessions were signed out.", "ok");
        location.hash = "#/dashboard";
        route();
      } catch (x) { err.textContent = x.message; err.hidden = false; }
    } }, h("label", {}, "Current password", current), h("label", {}, "New password (at least 10 characters with a digit and an upper-case letter or symbol)", next),
      h("label", {}, "Repeat new password", again), err, h("button", { type: "submit" }, "Change password"))));
  parts.push(await mfaPanel(u));
  $view().replaceChildren(...parts);
}

async function mfaPanel(u) {
  const box = h("section", { class: "panel" }, h("h2", {}, "Two-factor authentication"));
  if (u.mfaEnabled) {
    const pass = h("input", { type: "password", autocomplete: "current-password" });
    const code = h("input", { inputmode: "numeric", maxlength: "6", autocomplete: "one-time-code" });
    box.append(h("p", {}, pill("ok", "ENABLED"), " Sign-in requires a code from your authenticator app."),
      h("form", { class: "form", onsubmit: async (e) => {
        e.preventDefault();
        try { await api("/me/mfa/disable", { method: "POST", body: { password: pass.value, code: code.value } }); banner("Two-factor authentication removed.", "ok"); route(); }
        catch (x) { banner(x.message, "error"); }
      } }, h("label", {}, "Password", pass), h("label", {}, "Current code", code), h("button", { type: "submit", class: "danger" }, "Remove authenticator")));
    return box;
  }
  box.append(h("p", {}, "Protect the account with an authenticator app (TOTP, e.g. Google Authenticator, Microsoft Authenticator)."),
    h("button", { onclick: async () => {
      const s = await api("/me/mfa/setup", { method: "POST" });
      const code = h("input", { inputmode: "numeric", maxlength: "6", autocomplete: "one-time-code", required: true });
      box.replaceChildren(h("h2", {}, "Two-factor authentication"),
        h("p", {}, "Scan the code with the authenticator app, or type the secret, then enter the 6-digit code it shows."),
        h("img", { class: "qr", src: `data:image/png;base64,${s.qrPngBase64}`, alt: "QR code for the authenticator app" }),
        h("p", { class: "secret" }, s.secret),
        h("form", { class: "form", onsubmit: async (e) => {
          e.preventDefault();
          try {
            await api("/me/mfa/enable", { method: "POST", body: { secret: s.secret, code: code.value } });
            banner("Two-factor authentication is on.", "ok");
            state.user = await api("/me");
            route();
          } catch (x) { banner(x.message, "error"); }
        } }, h("label", {}, "Code from the app", code), h("button", { type: "submit" }, "Turn on")));
    } }, "Set up authenticator"));
  return box;
}

// ------------------------------------------------------------------ start

window.addEventListener("hashchange", route);
route();
